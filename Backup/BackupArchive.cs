using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZBoard.Suggestions;

namespace ZBoard.Backup;

/// <summary>Was eine Sicherung enthält. Ein Teil, der <c>null</c> ist, ist nicht in der Sicherung.</summary>
public sealed class BackupData
{
    /// <summary>Version von zBoard, mit der die Sicherung erstellt wurde (nur zur Anzeige).</summary>
    public string AppVersion { get; init; } = string.Empty;

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Einstellungen: Schlüssel → <see cref="bool"/>, <see cref="int"/>, <see cref="long"/>, <see cref="float"/> oder <see cref="string"/>.</summary>
    public IReadOnlyDictionary<string, object>? Settings { get; init; }

    /// <summary>Letzte Änderung der Einstellungen (Unix-ms, 0 = unbekannt); entscheidet bei der Synchronisation.</summary>
    public long SettingsModified { get; init; }

    /// <summary>Angeheftete Einträge der Zwischenablage (Format von <c>ClipboardHistory.SerializePinned</c>).</summary>
    public string? PinnedClips { get; init; }

    /// <summary>Gelernte Wörter und Wortfolgen pro Sprache ("de", "en" …).</summary>
    public IReadOnlyDictionary<string, UserHistory>? Learned { get; init; }

    /// <summary>Gemerkte E-Mail-Adressen.</summary>
    public EmailMemory? Emails { get; init; }

    public bool IsEmpty => Settings == null && PinnedClips == null && Learned == null && Emails == null;
}

public enum BackupError
{
    /// <summary>Keine zBoard-Sicherung (andere ZIP-Datei oder gar kein ZIP).</summary>
    NotABackup,

    /// <summary>Mit einer neueren zBoard-Version erstellt, deren Format diese Version nicht kennt.</summary>
    NewerVersion,

    /// <summary>Beschädigt oder unplausibel groß.</summary>
    Damaged,
}

public sealed class BackupException(BackupError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public BackupError Error { get; } = error;
}

/// <summary>
/// Sicherung als ZIP-Datei, damit man sie notfalls auch von Hand ansehen kann:
/// <code>
/// manifest.json          Format, Version, Inhalt
/// settings.json          Einstellungen nach Typ getrennt
/// clipboard/pinned.txt   angeheftete Zwischenablage
/// learning/&lt;sprache&gt;.tsv gelernte Wörter (Format von UserHistory)
/// learning/emails.tsv    E-Mail-Adressen (Format von EmailMemory)
/// </code>
/// Plattformunabhängig; das Lesen prüft alles, bevor etwas übernommen wird.
/// </summary>
public static partial class BackupArchive
{
    public const string Format = "zboard-backup";
    public const int FormatVersion = 1;
    public const string MimeType = "application/zip";

    /// <summary>Obergrenzen gegen beschädigte oder untergeschobene Dateien.</summary>
    const long MaxFileBytes = 64L * 1024 * 1024;
    const long MaxEntryBytes = 32L * 1024 * 1024;
    const int MaxEntries = 64;
    const int MaxSettings = 500;

    const string ManifestEntry = "manifest.json";
    const string SettingsEntry = "settings.json";
    const string PinnedEntry = "clipboard/pinned.txt";
    const string LearningPrefix = "learning/";
    const string EmailsEntry = "learning/emails.tsv";

    const string PartSettings = "settings";
    const string PartClipboard = "clipboard";
    const string PartLearned = "learned";
    const string PartEmails = "emails";

    [GeneratedRegex("^[a-z]{2,8}$")]
    private static partial Regex LanguageCode();

    /// <summary>Gültiger Sprachcode für eine Lerndatei (verhindert Pfade wie "../x").</summary>
    public static bool IsLanguageCode(string code) => code != "emails" && LanguageCode().IsMatch(code);

    /// <summary>Vorschlag für den Dateinamen, z. B. "zBoard-Sicherung-2026-11-11.zip".</summary>
    public static string SuggestedFileName(DateTime localNow, bool german) =>
        $"zBoard-{(german ? "Sicherung" : "backup")}-{localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.zip";

    // ---------- Schreiben ----------

    public static void Write(Stream output, BackupData data)
    {
        // Erst vollständig im Speicher bauen: Ein Fehler hinterlässt so keine halbe Datei beim Ziel.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var parts = new List<string>();
            if (data.Settings != null)
            {
                parts.Add(PartSettings);
                WriteEntry(zip, SettingsEntry, stream => WriteSettings(stream, data.Settings));
            }

            if (data.PinnedClips != null)
            {
                parts.Add(PartClipboard);
                WriteEntry(zip, PinnedEntry, stream =>
                {
                    var bytes = new UTF8Encoding(false).GetBytes(data.PinnedClips);
                    stream.Write(bytes);
                });
            }

            if (data.Learned != null)
            {
                parts.Add(PartLearned);
                foreach (var (language, history) in data.Learned.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!IsLanguageCode(language)) throw new ArgumentException($"Ungültiger Sprachcode: {language}", nameof(data));
                    WriteEntry(zip, $"{LearningPrefix}{language}.tsv", stream => history.SaveCopy(stream));
                }
            }

            if (data.Emails != null)
            {
                parts.Add(PartEmails);
                WriteEntry(zip, EmailsEntry, stream => data.Emails.SaveCopy(stream));
            }

            WriteEntry(zip, ManifestEntry, stream => WriteManifest(stream, data, parts));
        }

        buffer.Position = 0;
        buffer.CopyTo(output);
        output.Flush();
    }

    static void WriteEntry(ZipArchive zip, string name, Action<Stream> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        write(stream);
    }

    static void WriteManifest(Stream stream, BackupData data, List<string> parts)
    {
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("format", Format);
        json.WriteNumber("version", FormatVersion);
        json.WriteString("app", data.AppVersion);
        json.WriteString("created", data.Created.ToString("o", CultureInfo.InvariantCulture));
        if (data.SettingsModified > 0) json.WriteNumber("settingsModified", data.SettingsModified);
        json.WriteStartArray("contents");
        foreach (var part in parts) json.WriteStringValue(part);
        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>Nach Typ getrennt, damit beim Lesen wieder genau der richtige Typ entsteht (int bleibt int).</summary>
    static void WriteSettings(Stream stream, IReadOnlyDictionary<string, object> settings)
    {
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        WriteGroup<bool>("bool", (k, v) => json.WriteBoolean(k, v));
        WriteGroup<int>("int", (k, v) => json.WriteNumber(k, v));
        WriteGroup<long>("long", (k, v) => json.WriteNumber(k, v));
        WriteGroup<float>("float", (k, v) => json.WriteNumber(k, v));
        WriteGroup<string>("string", (k, v) => json.WriteString(k, v));
        json.WriteEndObject();

        void WriteGroup<T>(string type, Action<string, T> write)
        {
            var values = settings.Where(kv => kv.Value is T).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (values.Count == 0) return;
            json.WriteStartObject(type);
            foreach (var (key, value) in values) write(key, (T)value);
            json.WriteEndObject();
        }
    }

    // ---------- Lesen ----------

    public static BackupData Read(Stream input)
    {
        // Ins Speicher kopieren (Content-Provider-Streams sind oft nicht durchsuchbar), mit Obergrenze.
        using var buffer = new MemoryStream();
        CopyLimited(input, buffer, MaxFileBytes);
        buffer.Position = 0;

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException e)
        {
            throw new BackupException(BackupError.NotABackup, "Keine ZIP-Datei.", e);
        }

        using (zip)
        {
            try
            {
                return ReadArchive(zip);
            }
            catch (Exception e) when (e is InvalidDataException or JsonException or DecoderFallbackException or IOException)
            {
                throw new BackupException(BackupError.Damaged, "Die Sicherung ist beschädigt.", e);
            }
        }
    }

    static BackupData ReadArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries) throw new BackupException(BackupError.Damaged, "Zu viele Einträge.");

        var manifestEntry = zip.GetEntry(ManifestEntry)
            ?? throw new BackupException(BackupError.NotABackup, "manifest.json fehlt.");

        HashSet<string> parts;
        string appVersion;
        DateTimeOffset created;
        long settingsModified;
        using (var manifest = JsonDocument.Parse(ReadBytes(manifestEntry)))
        {
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
                || format.GetString() != Format)
            {
                throw new BackupException(BackupError.NotABackup, "Keine zBoard-Sicherung.");
            }

            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int v) || v < 1)
                throw new BackupException(BackupError.Damaged, "Version fehlt.");
            if (v > FormatVersion)
                throw new BackupException(BackupError.NewerVersion, $"Format {v} ist neuer als {FormatVersion}.");

            appVersion = root.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.String ? app.GetString() ?? string.Empty : string.Empty;
            created = root.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.String
                      && DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            settingsModified = root.TryGetProperty("settingsModified", out var sm) && sm.TryGetInt64(out long ms) && ms > 0 ? ms : 0;
            parts = root.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array
                ? contents.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
        }

        IReadOnlyDictionary<string, object>? settings = null;
        if (parts.Contains(PartSettings))
        {
            var entry = zip.GetEntry(SettingsEntry) ?? throw new BackupException(BackupError.Damaged, "settings.json fehlt.");
            settings = ReadSettings(ReadBytes(entry));
        }

        string? pinned = null;
        if (parts.Contains(PartClipboard))
        {
            var entry = zip.GetEntry(PinnedEntry) ?? throw new BackupException(BackupError.Damaged, "pinned.txt fehlt.");
            pinned = StrictUtf8.GetString(ReadBytes(entry));
        }

        Dictionary<string, UserHistory>? learned = null;
        if (parts.Contains(PartLearned))
        {
            learned = new Dictionary<string, UserHistory>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(LearningPrefix, StringComparison.Ordinal) || !entry.FullName.EndsWith(".tsv", StringComparison.Ordinal)) continue;
                var language = entry.FullName[LearningPrefix.Length..^".tsv".Length];
                if (!IsLanguageCode(language)) continue; // emails.tsv und Unbekanntes
                learned[language] = UserHistory.Load(new MemoryStream(ReadBytes(entry)));
            }
        }

        EmailMemory? emails = null;
        if (parts.Contains(PartEmails))
        {
            var entry = zip.GetEntry(EmailsEntry) ?? throw new BackupException(BackupError.Damaged, "emails.tsv fehlt.");
            emails = EmailMemory.Load(new MemoryStream(ReadBytes(entry)));
        }

        return new BackupData
        {
            AppVersion = appVersion,
            Created = created,
            Settings = settings,
            SettingsModified = settingsModified,
            PinnedClips = pinned,
            Learned = learned,
            Emails = emails,
        };
    }

    static Dictionary<string, object> ReadSettings(byte[] bytes)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new BackupException(BackupError.Damaged, "settings.json ist kein Objekt.");

        foreach (var group in doc.RootElement.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var setting in group.Value.EnumerateObject())
            {
                if (setting.Name.Length is 0 or > 100) continue;
                var element = setting.Value;
                object? value = group.Name switch
                {
                    "bool" when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
                    "int" when element.TryGetInt32(out int i) => i,
                    "long" when element.TryGetInt64(out long l) => l,
                    "float" when element.TryGetSingle(out float f) && float.IsFinite(f) => f,
                    "string" when element.ValueKind == JsonValueKind.String => element.GetString(),
                    _ => null, // unbekannter Typ (neuere Version) oder falscher Wert: auslassen
                };
                if (value == null) continue;
                if (result.Count >= MaxSettings) throw new BackupException(BackupError.Damaged, "Zu viele Einstellungen.");
                result[setting.Name] = value;
            }
        }

        return result;
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes) throw new BackupException(BackupError.Damaged, $"{entry.FullName} ist zu groß.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        CopyLimited(stream, memory, MaxEntryBytes); // Length kann bei manipulierten Dateien lügen
        return memory.ToArray();
    }

    static void CopyLimited(Stream from, Stream to, long limit)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = from.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > limit) throw new BackupException(BackupError.Damaged, "Die Datei ist zu groß.");
            to.Write(chunk, 0, read);
        }
    }
}
