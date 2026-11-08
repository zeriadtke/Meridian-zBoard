using Android.Views.InputMethods;

namespace ZBoard.Ime;

/// <summary>
/// Eigenschaften des aktuellen Eingabefelds aus EditorInfo.
/// Bitmasken als Konstanten aus der Android-Doku (InputType / EditorInfo), damit es keine Rolle spielt,
/// wie die .NET-Bindings die Enums benennen.
/// </summary>
public sealed class EditorProfile
{
    // InputType
    const int TypeMaskClass = 0x0000000f;
    const int TypeClassText = 0x00000001;
    const int TypeClassNumber = 0x00000002;
    const int TypeClassPhone = 0x00000003;
    const int TypeClassDatetime = 0x00000004;
    const int TypeMaskVariation = 0x00000ff0;
    const int TextVariationUri = 0x00000010;
    const int TextVariationEmail = 0x00000020;
    const int TextVariationPassword = 0x00000080;
    const int TextVariationVisiblePassword = 0x00000090;
    const int TextVariationWebEmail = 0x000000d0;
    const int TextVariationWebPassword = 0x000000e0;
    const int NumberVariationPassword = 0x00000010;
    const int TextFlagCapCharacters = 0x00001000;
    const int TextFlagCapWords = 0x00002000;
    const int TextFlagCapSentences = 0x00004000;
    const int TextFlagMultiLine = 0x00020000;
    const int TextFlagNoSuggestions = 0x00080000;

    // EditorInfo.imeOptions
    const int ImeMaskAction = 0x000000ff;
    const int ImeFlagNoPersonalizedLearning = 0x01000000;
    const int ImeFlagNoEnterAction = 0x40000000;

    // EditorInfo.IME_ACTION_*
    public const int ActionGo = 2;
    public const int ActionSearch = 3;
    public const int ActionSend = 4;
    public const int ActionNext = 5;
    public const int ActionDone = 6;
    public const int ActionPrevious = 7;

    public bool IsPassword { get; private init; }
    public bool IsNumeric { get; private init; }
    public bool IsPhone { get; private init; }
    public bool IsDatetime { get; private init; }
    public bool IsEmailOrUrl { get; private init; }

    /// <summary>E-Mail-Feld (InputType oder, bei Web-Formularen ohne type="email", Hinweis/Feldname mit "mail").</summary>
    public bool IsEmail { get; private init; }
    public bool NoSuggestions { get; private init; }
    public bool NoPersonalizedLearning { get; private init; }
    public bool CapsRequested { get; private init; }
    public bool MultiLine { get; private init; }
    public int Action { get; private init; }
    public bool NoEnterAction { get; private init; }

    /// <summary>
    /// Vorschlagsleiste wie bei Gboard in jedem Textfeld außer Passwort- und Zahlenfeldern – auch wenn die App
    /// TYPE_TEXT_FLAG_NO_SUGGESTIONS setzt. Das tun z. B. Suchleisten (SearchView mit eigener Vorschlagsliste);
    /// dort zeigt Gboard trotzdem Wortvorschläge an, korrigiert aber nicht automatisch.
    /// </summary>
    public bool AllowSuggestions => !IsPassword && !IsNumeric;

    /// <summary>Automatisch ersetzen nur in normalen Textfeldern, nicht bei E-Mail/URL oder NO_SUGGESTIONS.</summary>
    public bool AllowAutocorrect => AllowSuggestions && !IsEmailOrUrl && !NoSuggestions;

    /// <summary>Lernen nur in normalen Textfeldern; nie in Passwort-, E-Mail-/URL-Feldern oder wenn die App es untersagt.</summary>
    public bool AllowLearning => AllowSuggestions && !IsEmailOrUrl && !NoSuggestions && !NoPersonalizedLearning;

    /// <summary>Enter löst eine Feldaktion aus (Suchen, Senden …) statt einen Zeilenumbruch.</summary>
    public bool HasEnterAction => Action is >= ActionGo and <= ActionPrevious && !NoEnterAction;

    public static EditorProfile From(EditorInfo? info)
    {
        int type = info == null ? 0 : (int)info.InputType;
        int options = info == null ? 0 : (int)info.ImeOptions;
        int cls = type & TypeMaskClass;
        int variation = type & TypeMaskVariation;

        bool email = cls == TypeClassText && variation is TextVariationEmail or TextVariationWebEmail;
        // Manche Formulare markieren E-Mail-Felder nicht: einzeiliges, normales Textfeld mit "mail" im Hinweis oder Namen
        // Suchfelder ("In E-Mails suchen") sind keine E-Mail-Felder – dort soll zBoard normal lernen.
        if (!email && cls == TypeClassText && variation == 0 && (type & TextFlagMultiLine) == 0 && info != null
            && (options & ImeMaskAction) != ActionSearch)
        {
            string? hint = info.HintText?.ToString();
            email = (MentionsMail(hint) && !MentionsSearch(hint)) || MentionsMail(info.FieldName);
        }

        bool password =
            (cls == TypeClassText && variation is TextVariationPassword or TextVariationVisiblePassword or TextVariationWebPassword)
            || (cls == TypeClassNumber && variation == NumberVariationPassword);

        return new EditorProfile
        {
            IsPassword = password,
            IsNumeric = cls is TypeClassNumber or TypeClassPhone or TypeClassDatetime,
            IsPhone = cls == TypeClassPhone,
            IsDatetime = cls == TypeClassDatetime,
            IsEmailOrUrl = email || (cls == TypeClassText && variation == TextVariationUri),
            IsEmail = email && !password,
            NoSuggestions = (type & TextFlagNoSuggestions) != 0,
            NoPersonalizedLearning = (options & ImeFlagNoPersonalizedLearning) != 0,
            CapsRequested = cls == TypeClassText
                && (type & (TextFlagCapCharacters | TextFlagCapWords | TextFlagCapSentences)) != 0,
            MultiLine = (type & TextFlagMultiLine) != 0,
            Action = options & ImeMaskAction,
            NoEnterAction = (options & ImeFlagNoEnterAction) != 0,
        };
    }

    static bool MentionsMail(string? text) =>
        text != null && text.Length <= 60
        && (text.Contains("mail", StringComparison.OrdinalIgnoreCase) || text.Contains("courriel", StringComparison.OrdinalIgnoreCase)
            || text.Contains("correo", StringComparison.OrdinalIgnoreCase));

    static bool MentionsSearch(string? text) =>
        text != null && (text.Contains("such", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("search", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("find", StringComparison.OrdinalIgnoreCase));

    /// <summary>Beschriftung der Enter-Taste je nach Feldaktion.</summary>
    public string EnterLabel => !HasEnterAction ? "⏎" : Action switch
    {
        ActionGo => "→",
        ActionSearch => "🔍",
        ActionSend => "➤",
        ActionNext => "⇥",
        ActionDone => "✓",
        ActionPrevious => "⇤",
        _ => "⏎",
    };
}
