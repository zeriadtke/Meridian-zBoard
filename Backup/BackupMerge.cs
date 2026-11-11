namespace ZBoard.Backup;

/// <summary>
/// Regeln für die Synchronisation zwischen Geräten. Plattformunabhängig.
/// <list type="bullet">
/// <item>Gelernte Wörter, Wortfolgen und E-Mail-Adressen werden vereint (je Eintrag die höhere Anzahl), siehe
/// <c>UserHistory.MergeFrom</c> und <c>EmailMemory.MergeFrom</c>.</item>
/// <item>Einstellungen: die zuletzt geänderten gewinnen als Ganzes. Gerätebezogene Werte (<see cref="IsDeviceKey"/>)
/// bleiben immer beim Gerät.</item>
/// </list>
/// </summary>
public static class BackupMerge
{
    /// <summary>Schlüssel der letzten Änderung an synchronisierten Einstellungen (Unix-ms).</summary>
    public const string SettingsModifiedKey = "settingsModifiedMs";

    /// <summary>Gehört zu diesem Gerät und wird nie synchronisiert.</summary>
    static readonly HashSet<string> DeviceKeys = new(StringComparer.Ordinal)
    {
        "currentLanguage",  // zuletzt benutzte Tastatursprache
        "recentEmoji",      // ändert sich bei jedem Emoji
        "pinnedClips",      // Zwischenablage ist bewusst nicht dabei
        "enabledPlugins",   // Plugins sind pro Gerät installiert und freigegeben
        SettingsModifiedKey,
    };

    /// <summary>Gerätebezogen: feste Liste und alles, was mit "sync" beginnt (Einstellungen der Synchronisation selbst).</summary>
    public static bool IsDeviceKey(string key) => DeviceKeys.Contains(key) || key.StartsWith("sync", StringComparison.Ordinal);

    /// <summary>Nur die Einstellungen, die zwischen Geräten abgeglichen werden.</summary>
    public static Dictionary<string, object> SyncedSettings(IReadOnlyDictionary<string, object> all) =>
        all.Where(kv => !IsDeviceKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>
    /// Sollen die Einstellungen aus der Cloud die eigenen ersetzen? Ja, wenn sie neuer sind – oder beim ersten Abgleich
    /// dieses Geräts (ein neues Gerät soll die gewohnten Einstellungen bekommen).
    /// </summary>
    public static bool TakeRemoteSettings(long localModified, bool firstSyncOnThisDevice, BackupData? remote) =>
        remote?.Settings != null && (firstSyncOnThisDevice || remote.SettingsModified > localModified);

    /// <summary>
    /// Neue Einstellungen dieses Geräts, wenn die aus der Cloud gewinnen: deren synchronisierte Werte plus die eigenen
    /// gerätebezogenen; Zeitpunkt der letzten Änderung wie in der Cloud (sonst würde er sofort wieder gewinnen).
    /// </summary>
    public static Dictionary<string, object> CombineSettings(IReadOnlyDictionary<string, object> local, BackupData remote)
    {
        var result = SyncedSettings(remote.Settings ?? new Dictionary<string, object>());
        foreach (var (key, value) in local)
        {
            if (IsDeviceKey(key)) result[key] = value;
        }

        result[SettingsModifiedKey] = remote.SettingsModified;
        return result;
    }
}
