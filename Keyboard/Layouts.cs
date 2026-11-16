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
        ["nl"] = "Nederlands",
        ["pl"] = "Polski",
        ["cs"] = "Čeština",
        ["hu"] = "Magyar",
        ["ro"] = "Română",
        ["tr"] = "Türkçe",
        ["vi"] = "Tiếng Việt",
        ["el"] = "Ελληνικά",
        ["ru"] = "Русский",
        ["uk"] = "Українська",
        ["bg"] = "Български",
        ["ar"] = "العربية",
        ["zh"] = "中文",
    };

    /// <summary>Chinesisch: Pinyin auf QWERTY, die Schriftzeichen wählt man in der Vorschlagsleiste.</summary>
    public const string Chinese = "zh";

    /// <summary>Name der Tastenbelegung für die Einstellungen ("QWERTZ", "ЙЦУКЕН", "Pinyin" …).</summary>
    public static string LayoutName(string language) => language switch
    {
        "de" or "cs" or "hu" => "QWERTZ",
        "fr" => "AZERTY",
        "ru" or "uk" => "ЙЦУКЕН",
        "bg" => "ЯВЕРТЪ",
        "el" => "ΣΕΡΤΥ",
        "ar" => "ضصثق",
        "zh" => "Pinyin",
        _ => "QWERTY",
    };

    /// <summary>
    /// Schrift einer Sprache. Mehrsprachige Eingabe verbindet nur Sprachen derselben Schrift: Mit kyrillischen Tasten
    /// lässt sich kein deutsches Wort tippen, das zweite Wörterbuch wäre nur Ballast.
    /// </summary>
    public static string Script(string language) => language switch
    {
        "ru" or "uk" or "bg" => "Cyrl",
        "el" => "Grek",
        "ar" => "Arab",
        "zh" => "Hans",
        _ => "Latn",
    };

    /// <summary>Schreibrichtung von rechts nach links (Arabisch).</summary>
    public static bool IsRightToLeft(string language) => language == "ar";

    /// <summary>Gebietsschema für die Spracheingabe.</summary>
    public static string LocaleTag(string language) => language switch
    {
        "de" => "de-DE",
        "fr" => "fr-FR",
        "es" => "es-ES",
        "it" => "it-IT",
        "nl" => "nl-NL",
        "pl" => "pl-PL",
        "cs" => "cs-CZ",
        "hu" => "hu-HU",
        "ro" => "ro-RO",
        "tr" => "tr-TR",
        "vi" => "vi-VN",
        "el" => "el-GR",
        "ru" => "ru-RU",
        "uk" => "uk-UA",
        "bg" => "bg-BG",
        "ar" => "ar-SA",
        "zh" => "zh-CN",
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
            "nl" => (Dutch(), 10f),
            "pl" => (Polish(), 10f),
            "cs" => (Czech(), 10f),
            "hu" => (Hungarian(), 10f),
            "ro" => (Romanian(), 10f),
            "tr" => (Turkish(), 12f),
            "vi" => (Vietnamese(), 10f),
            "el" => (Greek(), 10f),
            "ru" => (Russian(), 11f),
            "uk" => (Ukrainian(), 12f),
            "bg" => (Bulgarian(), 11f),
            "ar" => (Arabic(), 11f),
            "zh" => (Qwerty(), 10f),
            _ => (Qwertz(umlautKeys), umlautKeys ? 11f : 10f),
        };

        List<KeyRow> all = [];
        if (numberRow) all.Add(NumberRow(units));
        all.AddRange(rows);
        all.Add(language switch
        {
            // Arabisch und Chinesisch haben eigene Satzzeichen auf den Tasten neben der Leertaste.
            "ar" => BottomRow(KeyAction.ToSymbols, showLanguageKey,
                Char("،", ","), Char(".", "؟", "!", "؛", ":", "…", "-", "\"", "'")),
            "zh" => BottomRow(KeyAction.ToSymbols, showLanguageKey,
                Char("，", "、", ","), Char("。", "？", "！", "；", "：", "…", "“", "”", ".")),
            "el" => BottomRow(KeyAction.ToSymbols, showLanguageKey,
                Char(","), Char(".", ";", "!", "·", ",", ":", "…", "-", "'", "\"")),
            _ => BottomRow(KeyAction.ToSymbols, showLanguageKey),
        });
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


    // ---------- Weitere lateinische Layouts ----------

    /// <summary>QWERTY-Reihen mit eigenen Zusatzzeichen je Buchstabe; nicht genannte Buchstaben haben keine.</summary>
    static KeyRow[] QwertyWith(Dictionary<char, string[]> alternates) => LatinRows("qwertyuiop", "asdfghjkl", "zxcvbnm", alternates);

    /// <summary>QWERTZ-Reihen (Tschechisch, Ungarisch) mit eigenen Zusatzzeichen.</summary>
    static KeyRow[] QwertzWith(Dictionary<char, string[]> alternates) => LatinRows("qwertzuiop", "asdfghjkl", "yxcvbnm", alternates);

    static KeyRow[] LatinRows(string top, string middle, string bottom, Dictionary<char, string[]> alternates)
    {
        KeyDef Key(char c, string? digit = null)
        {
            var extra = alternates.TryGetValue(c, out var a) ? a : [];
            // Die Ziffer steht vorn, außer die Sprache nennt ihren Platz selbst.
            return digit == null || extra.Contains(digit) ? Char(c.ToString(), extra) : Char(c.ToString(), [digit, .. extra]);
        }

        return
        [
            new KeyRow([.. top.Select((c, i) => Key(c, ((i + 1) % 10).ToString()))]),
            new KeyRow([.. middle.Select(c => Key(c))], Indent: 0.5f),
            new KeyRow(
            [
                Special(KeyAction.Shift, 1.5f), .. bottom.Select(c => Key(c)), Special(KeyAction.Delete, 1.5f),
            ]),
        ];
    }

    static KeyRow[] Dutch() => QwertyWith(new()
    {
        ['e'] = ["é", "ë", "ê", "è", "3", "ę", "ė", "ē"],
        ['y'] = ["ĳ", "6"],
        ['u'] = ["ú", "ü", "û", "ù", "7", "ū"],
        ['i'] = ["í", "ï", "ì", "î", "8", "į", "ī", "ĳ"],
        ['o'] = ["ó", "ö", "ô", "ò", "9", "õ", "œ", "ø", "ō"],
        ['a'] = ["á", "ä", "â", "à", "æ", "ã", "å", "ā"],
        ['n'] = ["ñ", "ń"],
    });

    static KeyRow[] Polish() => QwertyWith(new()
    {
        ['e'] = ["ę", "3", "è", "é", "ê", "ë", "ė", "ē"],
        ['o'] = ["ó", "9", "ö", "ô", "ò", "õ", "œ", "ø", "ō"],
        ['a'] = ["ą", "á", "à", "â", "ä", "æ", "ã", "å", "ā"],
        ['s'] = ["ś", "ß", "š"],
        ['l'] = ["ł"],
        ['z'] = ["ż", "ź", "ž"],
        ['c'] = ["ć", "ç", "č"],
        ['n'] = ["ń", "ñ"],
    });

    static KeyRow[] Czech() => QwertzWith(new()
    {
        ['e'] = ["é", "ě", "3", "è", "ê", "ë", "ę", "ė", "ē"],
        ['r'] = ["ř", "4"],
        ['t'] = ["ť", "5"],
        ['z'] = ["ž", "6", "ź", "ż"],
        ['u'] = ["ú", "ů", "7", "û", "ü", "ù", "ū"],
        ['i'] = ["í", "8", "î", "ï", "ì", "į", "ī"],
        ['o'] = ["ó", "9", "ö", "ô", "ò", "õ", "œ", "ø", "ō"],
        ['a'] = ["á", "à", "â", "ä", "æ", "ã", "å", "ā"],
        ['s'] = ["š", "ß", "ś"],
        ['d'] = ["ď"],
        ['y'] = ["ý", "ÿ"],
        ['c'] = ["č", "ç", "ć"],
        ['n'] = ["ň", "ñ", "ń"],
    });

    static KeyRow[] Hungarian() => QwertzWith(new()
    {
        ['e'] = ["é", "3", "è", "ê", "ë", "ę", "ė", "ē"],
        ['u'] = ["ú", "ü", "ű", "7", "û", "ù", "ū"],
        ['i'] = ["í", "8", "î", "ï", "ì", "į", "ī"],
        ['o'] = ["ó", "ö", "ő", "9", "ô", "ò", "õ", "œ", "ø", "ō"],
        ['a'] = ["á", "à", "â", "ä", "æ", "ã", "å", "ā"],
    });

    static KeyRow[] Romanian() => QwertyWith(new()
    {
        ['t'] = ["ț", "5"],
        ['i'] = ["î", "8", "ï", "ì", "í", "į", "ī"],
        ['a'] = ["ă", "â", "ã", "à", "á", "ä", "æ", "å", "ā"],
        ['s'] = ["ș", "ß", "ś", "š"],
    });

    /// <summary>Vietnamesisch: alle Vokale mit Ton- und Zusatzzeichen per langem Druck, đ auf d.</summary>
    static KeyRow[] Vietnamese() => QwertyWith(new()
    {
        ['e'] = ["è", "é", "ẻ", "ẽ", "ẹ", "ê", "ề", "ế", "ể", "ễ", "ệ", "3"],
        ['y'] = ["ỳ", "ý", "ỷ", "ỹ", "ỵ", "6"],
        ['u'] = ["ù", "ú", "ủ", "ũ", "ụ", "ư", "ừ", "ứ", "ử", "ữ", "ự", "7"],
        ['i'] = ["ì", "í", "ỉ", "ĩ", "ị", "8"],
        ['o'] = ["ò", "ó", "ỏ", "õ", "ọ", "ô", "ồ", "ố", "ổ", "ỗ", "ộ", "ơ", "ờ", "ớ", "ở", "ỡ", "ợ", "9"],
        ['a'] = ["à", "á", "ả", "ã", "ạ", "ă", "ằ", "ắ", "ẳ", "ẵ", "ặ", "â", "ầ", "ấ", "ẩ", "ẫ", "ậ"],
        ['d'] = ["đ"],
    });

    /// <summary>Türkisch (Q-Belegung) wie bei Gboard: ğ, ü, ş, i, ö, ç als eigene Tasten, ı neben u.</summary>
    static KeyRow[] Turkish() =>
    [
        new KeyRow(
        [
            Char("q", "1"), Char("w", "2"), Char("e", "3", "é", "è", "ê", "ë"), Char("r", "4"), Char("t", "5"), Char("y", "6"),
            Char("u", "7", "û", "ù", "ú", "ū"), Char("ı", "8", "î", "ï", "ì", "í", "į", "ī"),
            Char("o", "9", "ô", "œ", "ò", "ó", "õ", "ø", "ō"), Char("p", "0"), Char("ğ"), Char("ü"),
        ]),
        new KeyRow(
        [
            Char("a", "â", "ä", "á"), Char("s", "ß", "ś", "š"), Char("d"), Char("f"), Char("g"), Char("h"), Char("j"),
            Char("k"), Char("l"), Char("ş"), Char("i"),
        ], Indent: 0.5f),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("z"), Char("x"), Char("c", "ć", "č"), Char("v"), Char("b"), Char("n", "ñ"),
            Char("m"), Char("ö"), Char("ç"), Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    // ---------- Griechisch, Kyrillisch, Arabisch ----------

    /// <summary>Griechisch: Buchstaben mit Tonos und Dialytika per langem Druck, ς als eigene Taste.</summary>
    static KeyRow[] Greek() =>
    [
        new KeyRow(
        [
            Char(";", "1", ":"), Char("ς", "2"), Char("ε", "έ", "3"), Char("ρ", "4"), Char("τ", "5"),
            Char("υ", "ύ", "6", "ϋ", "ΰ"), Char("θ", "7"), Char("ι", "ί", "8", "ϊ", "ΐ"), Char("ο", "ό", "9"), Char("π", "0"),
        ]),
        new KeyRow(
        [
            Char("α", "ά"), Char("σ", "ς"), Char("δ"), Char("φ"), Char("γ"), Char("η", "ή"), Char("ξ"), Char("κ"), Char("λ"),
        ], Indent: 0.5f),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("ζ"), Char("χ"), Char("ψ"), Char("ω", "ώ"), Char("β"), Char("ν"), Char("μ"),
            Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    static KeyRow[] Russian() =>
    [
        new KeyRow(
        [
            Char("й", "1"), Char("ц", "2"), Char("у", "3"), Char("к", "4"), Char("е", "ё", "5"), Char("н", "6"), Char("г", "7"),
            Char("ш", "8"), Char("щ", "9"), Char("з", "0"), Char("х"),
        ]),
        new KeyRow(
        [
            Char("ф"), Char("ы"), Char("в"), Char("а"), Char("п"), Char("р"), Char("о"), Char("л"), Char("д"), Char("ж"), Char("э"),
        ]),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1f), Char("я"), Char("ч"), Char("с"), Char("м"), Char("и"), Char("т"), Char("ь", "ъ"),
            Char("б"), Char("ю"), Special(KeyAction.Delete, 1f),
        ]),
    ];

    static KeyRow[] Ukrainian() =>
    [
        new KeyRow(
        [
            Char("й", "1"), Char("ц", "2"), Char("у", "3"), Char("к", "4"), Char("е", "5", "ё"), Char("н", "6"),
            Char("г", "ґ", "7"), Char("ш", "8"), Char("щ", "9"), Char("з", "0"), Char("х"), Char("ї"),
        ]),
        new KeyRow(
        [
            Char("ф"), Char("і", "ї"), Char("в"), Char("а"), Char("п"), Char("р"), Char("о"), Char("л"), Char("д"), Char("ж"),
            Char("є"),
        ], Indent: 0.5f),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("я"), Char("ч"), Char("с"), Char("м"), Char("и"), Char("т"), Char("ь", "'", "ъ"),
            Char("б"), Char("ю"), Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    /// <summary>Bulgarisch in der phonetischen Belegung (ЯВЕРТЪ), wie Gboard sie vorgibt.</summary>
    static KeyRow[] Bulgarian() =>
    [
        new KeyRow(
        [
            Char("я", "1"), Char("в", "2"), Char("е", "3"), Char("р", "4"), Char("т", "5"), Char("ъ", "6"), Char("у", "7"),
            Char("и", "ѝ", "8"), Char("о", "9"), Char("п", "0"), Char("ч"),
        ]),
        new KeyRow(
        [
            Char("а"), Char("с"), Char("д"), Char("ф"), Char("г"), Char("х"), Char("й"), Char("к"), Char("л"), Char("ш"), Char("щ"),
        ]),
        new KeyRow(
        [
            Special(KeyAction.Shift, 1.5f), Char("з"), Char("ь"), Char("ц"), Char("ж"), Char("б"), Char("н"), Char("м"),
            Char("ю"), Special(KeyAction.Delete, 1.5f),
        ]),
    ];

    /// <summary>Arabisch wie bei Gboard: keine Groß- und Kleinschreibung, deshalb keine Umschalttaste.</summary>
    static KeyRow[] Arabic() =>
    [
        new KeyRow(
        [
            Char("ض", "1", "١"), Char("ص", "2", "٢"), Char("ث", "3", "٣"), Char("ق", "4", "٤", "ڨ"), Char("ف", "5", "٥", "ڤ", "ڢ"),
            Char("غ", "6", "٦"), Char("ع", "7", "٧"), Char("ه", "8", "٨"), Char("خ", "9", "٩"), Char("ح", "0", "٠"),
            Char("ج", "چ"),
        ]),
        new KeyRow(
        [
            Char("ش", "ڜ"), Char("س"), Char("ي", "ئ", "ى"), Char("ب", "پ"), Char("ل", "لا", "لأ", "لإ", "لآ"),
            Char("ا", "أ", "إ", "آ", "ء", "ٱ"), Char("ت"), Char("ن"), Char("م"), Char("ك", "گ", "ک"), Char("ط"),
        ]),
        new KeyRow(
        [
            Char("ذ"), Char("ء"), Char("ؤ"), Char("ر"), Char("ى", "ئ"), Char("ة"), Char("و"), Char("ز", "ژ"), Char("ظ"), Char("د"),
            Special(KeyAction.Delete, 1f),
        ]),
    ];

    /// <summary>Ziffernreihe über den Buchstaben; in breiteren Layouts (11 oder 12 Einheiten) mittig eingerückt.</summary>
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
    static KeyRow BottomRow(KeyAction modeKey, bool showLanguageKey, KeyDef? comma = null, KeyDef? period = null)
    {
        List<KeyDef> keys =
        [
            Special(modeKey, 1.5f),
            comma ?? Char(","),
            Special(KeyAction.Emoji, 1f),
        ];
        if (showLanguageKey) keys.Add(Special(KeyAction.Language, 1f));

        keys.Add(Fill(KeyAction.Space));
        keys.Add(period ?? Char(".", "?", "!", ",", ";", ":", "…", "-", "'", "\""));
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
