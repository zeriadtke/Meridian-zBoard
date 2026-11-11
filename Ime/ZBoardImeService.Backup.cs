using Android.Content;
using ZBoard.Backup;
using ZBoard.Clipboard;
using ZBoard.Settings;
using ZBoard.Suggestions;

namespace ZBoard.Ime;

/// <summary>Welche Teile eine Sicherung enthalten soll.</summary>
[Flags]
public enum BackupParts
{
    None = 0,
    Settings = 1,
    Learned = 2,
    Emails = 4,
    PinnedClips = 8,
    All = Settings | Learned | Emails | PinnedClips,
}

/// <summary>
/// Sichern und Wiederherstellen (aus den Einstellungen). Die Einstellungen laufen im selben Prozess wie die Tastatur:
/// Gesichert wird der aktuelle Stand im Speicher, beim Wiederherstellen übernimmt eine geladene Tastatur die Daten
/// sofort, damit sie beim nächsten Schließen nicht den alten Stand zurückschreibt.
/// </summary>
public sealed partial class ZBoardImeService
{
    /// <summary>Wird bei jedem Wiederherstellen (und Löschen der angehefteten Zwischenablage) erhöht; die Tastatur lädt dann beim nächsten Öffnen Angeheftetes neu.</summary>
    static int s_dataGeneration;

    int _seenDataGeneration;

    static readonly Lock BackupLock = new();

    static string LearningDir(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "learning");

    /// <summary>Stellt die gewählten Teile zusammen. Gelerntes kommt aus dem Speicher, falls die Sprache geladen ist.</summary>
    public static BackupData CreateBackup(Context context, BackupParts parts, string appVersion)
    {
        lock (BackupLock)
        {
            var settings = new ZBoardSettings(context);
            Dictionary<string, UserHistory>? learned = null;
            if (parts.HasFlag(BackupParts.Learned))
            {
                learned = new Dictionary<string, UserHistory>(StringComparer.Ordinal);
                var dir = LearningDir(context);
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*.tsv"))
                    {
                        var language = Path.GetFileNameWithoutExtension(file);
                        if (!BackupArchive.IsLanguageCode(language)) continue;
                        using var stream = File.OpenRead(file);
                        learned[language] = UserHistory.Load(stream);
                    }
                }

                foreach (var (language, task) in Engines)
                {
                    if (task.IsCompletedSuccessfully && BackupArchive.IsLanguageCode(language)) learned[language] = task.Result.History;
                }

                foreach (var language in learned.Where(kv => kv.Value.WordCount == 0).Select(kv => kv.Key).ToList())
                {
                    learned.Remove(language);
                }
            }

            return new BackupData
            {
                AppVersion = appVersion,
                Created = DateTimeOffset.Now,
                Settings = parts.HasFlag(BackupParts.Settings) ? settings.Export() : null,
                SettingsModified = settings.SettingsModifiedMs,
                PinnedClips = parts.HasFlag(BackupParts.PinnedClips) ? settings.PinnedClips : null,
                Learned = learned,
                Emails = parts.HasFlag(BackupParts.Emails) ? Emails(context) : null,
            };
        }
    }

    /// <summary>
    /// Übernimmt eine Sicherung. Jeder enthaltene Teil ersetzt den bisherigen vollständig, nicht enthaltene Teile
    /// bleiben unverändert.
    /// </summary>
    public static void Restore(Context context, BackupData data)
    {
        lock (BackupLock)
        {
            if (data.Learned != null) RestoreLearned(context, data.Learned);

            if (data.Emails != null)
            {
                lock (EmailLock)
                {
                    var path = EmailFile(context);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var stream = File.Create(path)) data.Emails.Save(stream);
                    s_emails = null; // beim nächsten Zugriff aus der Datei laden
                }
            }

            if (data.Settings != null || data.PinnedClips != null)
            {
                new ZBoardSettings(context).Import(data.Settings, data.PinnedClips);
            }

            Interlocked.Increment(ref s_dataGeneration);
        }
    }

    static void RestoreLearned(Context context, IReadOnlyDictionary<string, UserHistory> learned)
    {
        var dir = LearningDir(context);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(dir, "*.tsv"))
        {
            if (BackupArchive.IsLanguageCode(Path.GetFileNameWithoutExtension(file))) File.Delete(file);
        }

        foreach (var (language, history) in learned)
        {
            if (!BackupArchive.IsLanguageCode(language)) continue;
            using var stream = File.Create(LearningFile(context, language));
            history.SaveCopy(stream);
        }

        foreach (var (language, task) in Engines)
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result.History.ReplaceWith(learned.GetValueOrDefault(language) ?? new UserHistory());
            }
            else
            {
                // Lädt gerade (vielleicht noch die alte Datei): verwerfen, das nächste Öffnen lädt neu.
                Engines.TryRemove(language, out _);
            }
        }
    }

    // ---------- Synchronisation ----------

    /// <summary>Was in die Cloud geht: synchronisierte Einstellungen, Gelerntes, E-Mail-Adressen (keine Zwischenablage).</summary>
    public static BackupData CreateSyncData(Context context, string appVersion)
    {
        var all = CreateBackup(context, BackupParts.Settings | BackupParts.Learned | BackupParts.Emails, appVersion);
        return new BackupData
        {
            AppVersion = all.AppVersion,
            Created = all.Created,
            Settings = BackupMerge.SyncedSettings(all.Settings!),
            SettingsModified = new ZBoardSettings(context).SettingsModifiedMs,
            Learned = all.Learned,
            Emails = all.Emails,
        };
    }

    /// <summary>
    /// Führt den Stand aus der Cloud mit dem eigenen zusammen: Gelerntes und E-Mail-Adressen werden vereint (nichts
    /// Eigenes geht verloren, auch nicht, was während des Abgleichs getippt wurde), Einstellungen gewinnen, wenn sie
    /// neuer sind. Auf dem Hauptthread aufrufen.
    /// </summary>
    public static void ApplySync(Context context, BackupData remote, bool firstSync)
    {
        lock (BackupLock)
        {
            if (remote.Learned != null)
            {
                foreach (var (language, theirs) in remote.Learned)
                {
                    if (BackupArchive.IsLanguageCode(language)) MergeLearned(context, language, theirs);
                }
            }

            if (remote.Emails != null)
            {
                var emails = Emails(context);
                if (emails.MergeFrom(remote.Emails))
                {
                    lock (EmailLock)
                    {
                        var path = EmailFile(context);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using var stream = File.Create(path);
                        emails.Save(stream);
                    }
                }
            }

            var settings = new ZBoardSettings(context);
            if (BackupMerge.TakeRemoteSettings(settings.SettingsModifiedMs, firstSync, remote))
            {
                settings.Import(BackupMerge.CombineSettings(settings.Export(), remote), pinnedClips: null, markModifiedNow: false);
            }
        }
    }

    static void MergeLearned(Context context, string language, UserHistory theirs)
    {
        var path = LearningFile(context, language);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (Engines.TryGetValue(language, out var task))
        {
            if (task.IsCompletedSuccessfully)
            {
                var live = task.Result.History;
                if (!live.MergeFrom(theirs)) return;
                using var stream = File.Create(path);
                live.Save(stream);
                return;
            }

            // Lädt gerade (vielleicht noch den alten Stand): verwerfen, das nächste Öffnen lädt die zusammengeführte Datei.
            Engines.TryRemove(language, out _);
        }

        var history = new UserHistory();
        if (File.Exists(path))
        {
            using var input = File.OpenRead(path);
            history = UserHistory.Load(input);
        }

        if (!history.MergeFrom(theirs)) return;
        using var output = File.Create(path);
        history.Save(output);
    }

    /// <summary>Löscht die angeheftete Zwischenablage (aus den Einstellungen); gilt ab dem nächsten Öffnen der Tastatur.</summary>
    public static void ClearPinnedClips(Context context)
    {
        new ZBoardSettings(context).PinnedClips = string.Empty;
        Interlocked.Increment(ref s_dataGeneration);
    }

    /// <summary>Beim Öffnen der Tastatur: nach einem Wiederherstellen die angeheftete Zwischenablage neu laden.</summary>
    void ReloadRestoredData()
    {
        int generation = Volatile.Read(ref s_dataGeneration);
        if (generation == _seenDataGeneration) return;
        _seenDataGeneration = generation;
        _clipboard = ClipboardHistory.DeserializePinned(_settings.PinnedClips);
    }
}
