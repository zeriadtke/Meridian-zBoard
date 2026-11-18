namespace ZBoard.Pen;

/// <summary>Die Sprachen von zBoard und die Kennungen der passenden ML-Kit-Schriftmodelle.</summary>
public static class PenLanguages
{
    /// <summary>zBoard-Sprache → (Name, ML-Kit-Sprachkennung).</summary>
    public static readonly IReadOnlyList<(string Code, string Name, string Tag)> All =
    [
        ("de", "Deutsch", "de-DE"),
        ("en", "English", "en-US"),
        ("fr", "Français", "fr-FR"),
        ("es", "Español", "es-ES"),
        ("it", "Italiano", "it-IT"),
        ("nl", "Nederlands", "nl-NL"),
        ("pl", "Polski", "pl-PL"),
        ("cs", "Čeština", "cs-CZ"),
        ("hu", "Magyar", "hu-HU"),
        ("ro", "Română", "ro-RO"),
        ("tr", "Türkçe", "tr-TR"),
        ("vi", "Tiếng Việt", "vi-VN"),
        ("el", "Ελληνικά", "el-GR"),
        ("ru", "Русский", "ru-RU"),
        ("uk", "Українська", "uk-UA"),
        ("bg", "Български", "bg-BG"),
        ("ar", "العربية", "ar"),
        ("zh", "中文", "zh-Hani-CN"),
    ];

    /// <summary>Kennungen, die ML Kit für eine zBoard-Sprache probieren soll: erst mit Region, dann nur die Sprache.</summary>
    public static IEnumerable<string> TagsFor(string code)
    {
        foreach (var (c, _, tag) in All)
        {
            if (c != code) continue;
            yield return tag;
            if (tag.Contains('-')) yield return tag[..tag.IndexOf('-')];
        }

        yield return code;
    }

    public static string NameOf(string code) => All.FirstOrDefault(l => l.Code == code).Name ?? code;
}
