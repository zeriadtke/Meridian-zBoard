using Android.Content;

namespace ZBoard.DeepL;

/// <summary>Zielsprache der DeepL-API mit Namen auf Deutsch und Englisch.</summary>
public sealed record TargetLanguage(string Code, string German, string English)
{
    public string Name => Java.Util.Locale.Default.Language == "de" ? German : English;
}

/// <summary>API-Schlüssel und gewählte Zielsprachen (app-privater Speicher, nur auf dem Gerät).</summary>
public sealed class DeepLSettings(Context context)
{
    public static readonly IReadOnlyList<TargetLanguage> Languages =
    [
        new("DE", "Deutsch", "German"),
        new("EN-US", "Englisch (US)", "English (US)"),
        new("EN-GB", "Englisch (UK)", "English (UK)"),
        new("FR", "Französisch", "French"),
        new("ES", "Spanisch", "Spanish"),
        new("IT", "Italienisch", "Italian"),
        new("NL", "Niederländisch", "Dutch"),
        new("PL", "Polnisch", "Polish"),
        new("PT-PT", "Portugiesisch", "Portuguese"),
        new("PT-BR", "Portugiesisch (BR)", "Portuguese (BR)"),
        new("TR", "Türkisch", "Turkish"),
        new("UK", "Ukrainisch", "Ukrainian"),
        new("RU", "Russisch", "Russian"),
        new("JA", "Japanisch", "Japanese"),
        new("ZH", "Chinesisch", "Chinese"),
    ];

    readonly ISharedPreferences _prefs = context.GetSharedPreferences("deepl", FileCreationMode.Private)!;

    public string ApiKey
    {
        get => _prefs.GetString("apiKey", string.Empty) ?? string.Empty;
        set => _prefs.Edit()!.PutString("apiKey", value.Trim())!.Apply();
    }

    /// <summary>Zielsprachen, die zBoard als Aktionen anbietet (Reihenfolge wie in <see cref="Languages"/>).</summary>
    public IReadOnlyList<TargetLanguage> Targets
    {
        get
        {
            var codes = (_prefs.GetString("targets", "EN-US,DE") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            return [.. Languages.Where(l => codes.Contains(l.Code))];
        }
        set => _prefs.Edit()!.PutString("targets", string.Join(",", value.Select(l => l.Code)))!.Apply();
    }
}
