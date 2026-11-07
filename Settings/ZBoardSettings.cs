using Android.Content;

namespace ZBoard.Settings;

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,

    /// <summary>Material You: Farben aus dem Hintergrundbild (ab Android 12).</summary>
    Dynamic = 3,
}

public enum OneHandedMode
{
    Off = 0,
    Right = 1,
    Left = 2,
}

/// <summary>Einstellungen in SharedPreferences. Werte werden bei jedem Zugriff frisch gelesen.</summary>
public sealed class ZBoardSettings
{
    const string PrefsName = "zboard";

    readonly ISharedPreferences _prefs;

    public ZBoardSettings(Context context)
    {
        _prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
    }

    /// <summary>Aktivierte Sprachen in Reihenfolge, z. B. ["de", "en"].</summary>
    public IReadOnlyList<string> Languages
    {
        get
        {
            var raw = _prefs.GetString("languages", "de,en") ?? "de";
            var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return list.Count > 0 ? list : new List<string> { "de" };
        }
        set => PutString("languages", string.Join(",", value));
    }

    public string CurrentLanguage
    {
        get
        {
            var current = _prefs.GetString("currentLanguage", null);
            return current != null && Languages.Contains(current) ? current : Languages[0];
        }
        set => PutString("currentLanguage", value);
    }

    public bool Haptics
    {
        get => _prefs.GetBoolean("haptics", true);
        set => PutBool("haptics", value);
    }

    public bool AutoCorrect
    {
        get => _prefs.GetBoolean("autocorrect", true);
        set => PutBool("autocorrect", value);
    }

    public bool AutoCaps
    {
        get => _prefs.GetBoolean("autocaps", true);
        set => PutBool("autocaps", value);
    }

    public bool DoubleSpacePeriod
    {
        get => _prefs.GetBoolean("doubleSpacePeriod", true);
        set => PutBool("doubleSpacePeriod", value);
    }

    /// <summary>Nächstes Wort nach dem Leerzeichen vorschlagen.</summary>
    public bool NextWordPrediction
    {
        get => _prefs.GetBoolean("nextWordPrediction", true);
        set => PutBool("nextWordPrediction", value);
    }

    /// <summary>Eigene Wörter und Wortfolgen lernen (nur auf dem Gerät).</summary>
    public bool LearnWords
    {
        get => _prefs.GetBoolean("learnWords", true);
        set => PutBool("learnWords", value);
    }

    /// <summary>In E-Mail-Feldern eingegebene Adressen merken und wieder vorschlagen (nur auf dem Gerät).</summary>
    public bool RememberEmails
    {
        get => _prefs.GetBoolean("rememberEmails", true);
        set => PutBool("rememberEmails", value);
    }

    public bool GlideTyping
    {
        get => _prefs.GetBoolean("glideTyping", true);
        set => PutBool("glideTyping", value);
    }

    public bool KeyPreview
    {
        get => _prefs.GetBoolean("keyPreview", true);
        set => PutBool("keyPreview", value);
    }

    public bool KeySound
    {
        get => _prefs.GetBoolean("keySound", false);
        set => PutBool("keySound", value);
    }

    /// <summary>Eigene Taste 🌐 zum Sprachwechsel (sonst: langer Druck auf die Leertaste).</summary>
    public bool ShowLanguageKey
    {
        get => _prefs.GetBoolean("showLanguageKey", false);
        set => PutBool("showLanguageKey", value);
    }

    /// <summary>Deutsch: eigene Tasten für ü, ö, ä (sonst per langem Druck auf u, o, a wie bei Gboard).</summary>
    public bool UmlautKeys
    {
        get => _prefs.GetBoolean("umlautKeys", false);
        set => PutBool("umlautKeys", value);
    }

    /// <summary>Ziffernreihe über den Buchstaben.</summary>
    public bool NumberRow
    {
        get => _prefs.GetBoolean("numberRow", false);
        set => PutBool("numberRow", value);
    }

    /// <summary>Tastaturhöhe in Prozent (80–130).</summary>
    public int HeightPercent
    {
        get => Math.Clamp(_prefs.GetInt("heightPercent", 100), 80, 130);
        set => PutInt("heightPercent", Math.Clamp(value, 80, 130));
    }

    public OneHandedMode OneHanded
    {
        get => (OneHandedMode)_prefs.GetInt("oneHanded", 0);
        set => PutInt("oneHanded", (int)value);
    }

    /// <summary>Verlauf der Zwischenablage führen.</summary>
    public bool ClipboardHistory
    {
        get => _prefs.GetBoolean("clipboardHistory", true);
        set => PutBool("clipboardHistory", value);
    }

    /// <summary>Spracheingabe nur, wenn das Gerät offline erkennen kann.</summary>
    public bool VoiceOfflineOnly
    {
        get => _prefs.GetBoolean("voiceOfflineOnly", true);
        set => PutBool("voiceOfflineOnly", value);
    }

    public string RecentEmoji
    {
        get => _prefs.GetString("recentEmoji", string.Empty) ?? string.Empty;
        set => PutString("recentEmoji", value);
    }

    /// <summary>Angeheftete Einträge der Zwischenablage.</summary>
    public string PinnedClips
    {
        get => _prefs.GetString("pinnedClips", string.Empty) ?? string.Empty;
        set => PutString("pinnedClips", value);
    }

    public ThemeMode Theme
    {
        get => (ThemeMode)_prefs.GetInt("theme", (int)ThemeMode.System);
        set => PutInt("theme", (int)value);
    }

    void PutBool(string key, bool value) => _prefs.Edit()!.PutBoolean(key, value)!.Apply();

    void PutInt(string key, int value) => _prefs.Edit()!.PutInt(key, value)!.Apply();

    void PutString(string key, string value) => _prefs.Edit()!.PutString(key, value)!.Apply();
}
