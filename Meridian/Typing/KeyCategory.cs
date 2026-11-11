// zHealth Meridian – Tipp-Dynamik. Nicht Teil von zBoard (upstream), siehe Meridian/README.md.
namespace ZBoard.Meridian.Typing;

/// <summary>
/// Art einer Taste. Das ist das EINZIGE, was aus einem Tastendruck abgeleitet wird – das Zeichen selbst wird
/// sofort verworfen und nie gespeichert. Gleiche Einteilung wie Meridian.Core (KeyCategory) und die frühere
/// Meridian-Tastatur auf Basis von AnySoftKeyboard.
/// </summary>
public enum KeyCategory
{
    /// <summary>Buchstabe oder Ziffer.</summary>
    Alphanumeric,
    Space,

    /// <summary>Löschen (auch Wort-Löschen per Wischen).</summary>
    Backspace,
    Enter,

    /// <summary>Satz- und Sonderzeichen, Emoji.</summary>
    Punctuation,

    /// <summary>Umschalt, Symbole, Sprache, Emoji-Taste und sonstige Funktionstasten.</summary>
    Function,
}

public static class KeyCategories
{
    /// <summary>Einordnung eines erzeugten Textes (nur das erste Zeichen zählt, danach wird es verworfen).</summary>
    public static KeyCategory OfText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return KeyCategory.Function;
        if (text == " ") return KeyCategory.Space;
        return char.IsLetterOrDigit(text, 0) ? KeyCategory.Alphanumeric : KeyCategory.Punctuation;
    }
}
