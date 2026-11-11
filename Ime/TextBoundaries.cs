namespace ZBoard.Ime;

/// <summary>Wortgrenzen im Text vor dem Cursor. Plattformunabhängig.</summary>
public static class TextBoundaries
{
    /// <summary>
    /// Wie viele Zeichen am Ende von <paramref name="text"/> die letzten <paramref name="words"/> Wörter
    /// samt Leerraum einnehmen. Ein Satzzeichen zählt als eigenes Wort.
    /// </summary>
    public static int CharsForWords(string text, int words)
    {
        int i = text.Length;
        for (int w = 0; w < words && i > 0; w++)
        {
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            int wordEnd = i;
            while (i > 0 && IsWordChar(text[i - 1])) i--;
            if (i == wordEnd && i > 0) i--;
        }

        return text.Length - i;
    }

    /// <summary>
    /// Steht der Cursor am Satzanfang? Leeres Feld, neue Zeile oder Leerzeichen nach Satzende (. ! ? …), auch hinter
    /// schließenden Anführungszeichen und Klammern ("Ende.“ ", "(so!) "). <paramref name="before"/>: Text vor dem Cursor.
    /// </summary>
    public static bool IsSentenceStart(string before)
    {
        if (before.Length == 0) return true;
        if (before[^1] is '\n' or '\r') return true;
        if (!char.IsWhiteSpace(before[^1])) return false;

        string trimmed = before.TrimEnd(' ', '\t', '\u00a0');
        if (trimmed.Length == 0 || trimmed[^1] is '\n' or '\r') return true;

        int i = trimmed.Length - 1;
        while (i > 0 && trimmed[i] is '"' or '\'' or '“' or '”' or '‘' or '’' or '»' or '«' or ')' or ']') i--;
        return trimmed[i] is '.' or '!' or '?' or '…';
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-';
}
