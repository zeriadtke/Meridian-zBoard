namespace ZBoard.Keyboard;

using static KeyDef;

/// <summary>Alle Tastaturlayouts. Neue Sprachen bekommen hier eine eigene Buchstabenebene.</summary>
public static class Layouts
{
    public const string Symbols1 = "symbols1";
    public const string Symbols2 = "symbols2";

    /// <summary>Unterstützte Sprachen in Anzeigereihenfolge.</summary>
    public static readonly IReadOnlyDictionary<string, string> LanguageNames = new Dictionary<string, string>
    {
        ["de"] = "Deutsch",
        ["en"] = "English",
        ["fr"] = "Français",
        ["es"] = "Español",
        ["it"] = "Italiano",
    };

    /// <summary>Gebietsschema für die Spracheingabe.</summary>
    public static string LocaleTag(string language) => language switch
    {
        "de" => "de-DE",
        "fr" => "fr-FR",
        "es" => "es-ES",
        "it" => "it-IT",
        _ => "en-US",
    };

    /// <summary>
    /// Buchstabenebene; mit <paramref name="numberRow"/> steht eine Ziffernreihe darüber,
    /// <paramref name="umlautKeys"/> gibt Deutsch eigene Tasten für ü, ö, ä.
    /// </summary>
    public static KeyboardLayout Alphabet(string language, bool showLanguageKey, bool numberRow = false, bool umlautKeys = false)
    {
        var (rows, units) = language switch
        {
            "en" => (Qwerty(), 10f),
            "fr" => (Azerty(), 10f),
            "es" => (Spanish(), 10f),
            "it" => (Italian(), 10f),
            _ => (Qwertz(umlautKeys), umlautKeys ? 11f : 10f),
        };

        List<KeyRow> all = [];
        if (numberRow) all.Add(NumberRow(units));
        all.AddRange(rows);
        all.Add(BottomRow(KeyAction.ToSymbols, showLanguageKey));
        return new KeyboardLayout(language, true, all);
    }

    public const string NumpadId = "numpad";

    /// <summary>
    /// Ziffernfeld wie bei Gboard für Zahlen-, PIN-, Telefon- und Datumsfelder: 1–9 im Raster,
    /// rechts Minus, Leertaste, Löschen und Enter.
    /// </summary>
    public static KeyboardLayout Numpad(NumpadKind kind)
    {
        KeyDef left = kind switch
        {
            NumpadKind.Phone => Char("*", "#", "(", ")", "/", "N", ",", ";"),
            NumpadKind.Datetime => Char(":", "/", ".", "-"),
            _ => Char(",", "."),
        };
        KeyDef right = kind switch
        {
            NumpadKind.Phone => Char("#", "*"),
            _ => Char(".", ","),
        };
        KeyDef zero = kind == NumpadKind.Phone ? Char("0", "+") : Char("0");
        KeyDef top = kind == NumpadKind.Phone ? Char("+", "-") : Char("-", "+", "/", "*", "=");

        return new KeyboardLayout(NumpadId, false,
        [
            new KeyRow([Char("1"), Char("2"), Char("3"), top]),
            new KeyRow([Char("4"), Char("5"), Char("6"), Special(KeyAction.Space, 1f)]),
            new KeyRow([Char("7"), Char("8"), Char("9"), Special(KeyAction.Delete, 1f)]),
            new KeyRow([left, zero, right, Special(KeyAction.Enter, 1f)]),
        ]);
    }

    public static KeyboardLayout Symbols(int page, bool showLanguageKey) =>
        page == 2 ? SymbolsPage2(showLanguageKey) : SymbolsPage1(showLanguageKey);

    // ---------- Buchstabenreihen ----------
    //
    // Zusatzzeichen (langer Druck) je Sprache in der Reihenfolge von Gboard, die der Android-Open-Source-Tastatur
    // folgt (AOSP LatinIME, KeyboardTextsTable, Apache 2.0): Das erste Zeichen steht direkt über der Taste und ist
    // vorausgewählt. Die Ziffer der oberen Reihe steht dort, wo AOSP sie mit „%“ markiert, sonst vorne.

    /// <summary>
    /// Deutsch wie bei Gboard: 10 Tasten je Reihe, Umlaute und ß per langem Druck (zuerst angeboten).
    /// Mit <paramref name="umlautKeys"/> stehen ü, ö, ä als eigene Tasten am rechten Rand (11 Tasten je Reihe).
    /// </summary>
    static KeyRow[] Qwertz(bool umlautKeys)
    {
        List<KeyDef> top =
        [
            Char("q", "1"), Char("w", "2"), Char("e", "3", "é", "è", "ê", "ë", "ė", "ē"), Char("r", "4"), Char("t", "5"),
            Char("z", "6"), Char("u", "ü", "7", "û", "ù", "ú", "ū"), Char("i", "8"),
            Char("o", "ö", "9", "ô", "ò", "ó", "õ", "œ", "ø", "ō"), Char("p", "0"),
        ];
        List<KeyDef> middle =
        [
            Char("a", "ä", "â", "à", "á", "æ", "ã", "å", "ā"), Char("s", "ß", "ś", "š"), Char("d"), Char("f"), Char("g"),
            Char("h"), Char("j"), Char("k"), Char("l"),
        ];
        float shiftWidth = 1.5f;
        if (umlautKeys)
        {
            top.Add(Char("ü"));
            middle.Add(Char("ö"));
            middle.Add(Char("ä"));
            shiftWidth = 2f;
        }

        return
        [
            new KeyRow(top),
            new KeyRow(middle, Indent: umlautKeys ? 0f : 0.5f),
            new KeyRow(
            [
                Special(KeyAction.Shift, shiftWidth), Char("y"), Char("x"), Char("c"), Char("v"),
                Char("b"), Char("n", "ñ", "ń"), Char("m"), Special(KeyAction.Delete, shiftWidth),
            ]),
        ];
    }

    static KeyRow[] Qwerty() =>
    [
        new KeyRow(
        [
            Char("q", "1"), Char("w", "2"), Char("e", "3", "é", "è", "ê", "ë", "ē"), Char("r", "4"), Char("t", "5"),
            Char("y", "6"), Char("u", "7", "ú", "û", "ü", "ù", "ū"), Char("i", "8", "í", "î", "ï", "ī", "ì"),
            Char("o", "9", "ó", "ô", "ö", "ò", "œ", "ø", "ō", "õ"),
            Char("p", "0"),
        ]),
        new KeyRow(
        [
            Char("a", "à", "á", "â", "ä", "æ", "ã", "å", "ā"), Char("s", "ß"), Char("d"), Char("f"), Char("g"), Char("h"), Char("j"),
            Char("k"), Char("l"),
        ], Indent: 0.5f),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("z"), Char("x"), Char("c", "ç"), Char("v"), Char("b"), Char("n", "ñ"),
            Char("m"), Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    static KeyRow[] Azerty() =>
    [
        new KeyRow(
        [
            Char("a", "à", "â", "1", "æ", "á", "ä", "ã", "å", "ā", "ª"), Char("z", "2"),
            Char("e", "é", "è", "ê", "ë", "3", "ę", "ė", "ē"), Char("r", "4"), Char("t", "5"), Char("y", "6", "ÿ"),
            Char("u", "ù", "û", "7", "ü", "ú", "ū"), Char("i", "î", "8", "ï", "ì", "í", "į", "ī"),
            Char("o", "ô", "œ", "9", "ö", "ò", "ó", "õ", "ø", "ō", "º"), Char("p", "0"),
        ]),
        new KeyRow(
        [
            Char("q"), Char("s"), Char("d"), Char("f"), Char("g"), Char("h"), Char("j"), Char("k"), Char("l"), Char("m"),
        ]),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("w"), Char("x"), Char("c", "ç", "ć", "č"), Char("v"), Char("b"),
            Char("n"), Char("'", "’", "\""), Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    static KeyRow[] Spanish() =>
    [
        new KeyRow(
        [
            Char("q", "1"), Char("w", "2"), Char("e", "3", "é", "è", "ë", "ê", "ę", "ė", "ē"), Char("r", "4"), Char("t", "5"),
            Char("y", "6"), Char("u", "7", "ú", "ü", "ù", "û", "ū"), Char("i", "8", "í", "ï", "ì", "î", "į", "ī"),
            Char("o", "9", "ó", "ò", "ö", "ô", "õ", "ø", "œ", "ō", "º"),
            Char("p", "0"),
        ]),
        new KeyRow(
        [
            Char("a", "á", "à", "ä", "â", "ã", "å", "ą", "æ", "ā", "ª"), Char("s"), Char("d"), Char("f"), Char("g"), Char("h"), Char("j"), Char("k"),
            Char("l"), Char("ñ"),
        ]),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("z"), Char("x"), Char("c", "ç", "ć", "č"), Char("v"), Char("b"),
            Char("n", "ñ", "ń"), Char("m"), Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    static KeyRow[] Italian() =>
    [
        new KeyRow(
        [
            Char("q", "1"), Char("w", "2"), Char("e", "3", "è", "é", "ê", "ë", "ę", "ė", "ē"), Char("r", "4"), Char("t", "5"),
            Char("y", "6"), Char("u", "7", "ù", "ú", "û", "ü", "ū"), Char("i", "8", "ì", "í", "î", "ï", "į", "ī"),
            Char("o", "9", "ò", "ó", "ô", "ö", "õ", "œ", "ø", "ō", "º"),
            Char("p", "0"),
        ]),
        new KeyRow(
        [
            Char("a", "à", "á", "â", "ä", "æ", "ã", "å", "ā", "ª"), Char("s"), Char("d"), Char("f"), Char("g"), Char("h"),
            Char("j"), Char("k"), Char("l"),
        ], Indent: 0.5f),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("z"), Char("x"), Char("c"), Char("v"), Char("b"), Char("n"),
            Char("m"), Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    /// <summary>Ziffernreihe über den Buchstaben; bei 11 Einheiten (QWERTZ) mittig eingerückt.</summary>
    static KeyRow NumberRow(float units) => new(
    [
        Char("1", "¹", "½", "⅓", "¼"), Char("2", "²", "⅔"), Char("3", "³", "¾"), Char("4", "⁴"), Char("5"),
        Char("6"), Char("7"), Char("8"), Char("9"), Char("0", "ⁿ", "∅"),
    ], Indent: (units - 10f) / 2);

    // ---------- Symbole ----------

    static KeyboardLayout SymbolsPage1(bool showLanguageKey) => new(Symbols1, false,
    [
        new KeyRow(
        [
            Char("1", "¹", "½", "⅓", "¼"), Char("2", "²", "⅔"), Char("3", "³", "¾"), Char("4", "⁴"), Char("5"),
            Char("6"), Char("7"), Char("8"), Char("9"), Char("0", "ⁿ", "∅"),
        ]),
        new KeyRow(
        [
            Char("@"), Char("#"), Char("€", "$", "£", "¥", "¢"), Char("_"), Char("&"), Char("-", "–", "—", "·"),
            Char("+", "±"), Char("(", "<", "{", "["), Char(")", ">", "}", "]"), Char("/", "\\"),
        ]),
        new KeyRow(
        [
            Special(KeyAction.ToSymbols2, 1.5f), Char("*", "★", "†"), Char("\"", "„", "“", "”", "«", "»"),
            Char("'", "‚", "‘", "’", "‹", "›"), Char(":"), Char(";"), Char("!", "¡"), Char("?", "¿"),
            Special(KeyAction.Delete, 1.5f),
        ]),
        BottomRow(KeyAction.ToAlpha, showLanguageKey),
    ]);

    static KeyboardLayout SymbolsPage2(bool showLanguageKey) => new(Symbols2, false,
    [
        new KeyRow(
        [
            Char("~"), Char("`"), Char("|"), Char("•", "♣", "♠", "♪", "♥", "♦"), Char("√"), Char("π", "Π"),
            Char("÷"), Char("×"), Char("¶", "§"), Char("∆"),
        ]),
        new KeyRow(
        [
            Char("£"), Char("¥"), Char("$"), Char("¢"), Char("^", "↑", "↓", "←", "→"), Char("°", "′", "″"),
            Char("=", "≠", "≈", "∞"), Char("{"), Char("}"), Char("\\"),
        ]),
        new KeyRow(
        [
            Special(KeyAction.ToSymbols, 1.5f), Char("%", "‰"), Char("©"), Char("®"), Char("™"), Char("✓"),
            Char("["), Char("]"), Special(KeyAction.Delete, 1.5f),
        ]),
        BottomRow(KeyAction.ToAlpha, showLanguageKey),
    ]);

    /// <summary>Untere Reihe wie bei Gboard: Ebenen-Taste, Komma, Emoji, (Sprache), Leertaste, Punkt, Enter.</summary>
    static KeyRow BottomRow(KeyAction modeKey, bool showLanguageKey)
    {
        List<KeyDef> keys =
        [
            Special(modeKey, 1.5f),
            Char(","),
            Special(KeyAction.Emoji, 1f),
        ];
        if (showLanguageKey) keys.Add(Special(KeyAction.Language, 1f));

        keys.Add(Fill(KeyAction.Space));
        keys.Add(Char(".", "?", "!", ",", ";", ":", "…", "-", "'", "\""));
        keys.Add(Special(KeyAction.Enter, 1.5f));
        return new KeyRow(keys);
    }
}

/// <summary>Art des Ziffernfelds.</summary>
public enum NumpadKind
{
    Number,
    Phone,
    Datetime,
}
