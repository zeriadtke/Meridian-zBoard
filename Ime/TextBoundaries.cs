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
        return trimmed[i] is '.' or '!' or '?' or '…' or '。' or '！' or '？' or '؟';
    }

    /// <summary>
    /// Gehört das Wort <paramref name="word"/> zu einem Link, einer Adresse oder einem Domainnamen? Dann darf es weder
    /// autokorrigiert noch gelernt werden ("https://BeSoci" + "." darf nicht zu "https://Besuch." werden).
    /// <paramref name="before"/>: Text vor dem Wort (ohne das Wort selbst).
    /// </summary>
    public static bool IsInUrlOrAddress(string before, string word)
    {
        // Das Wort selbst beginnt einen Link ("https" vor ":", "www" vor ".")
        if (word.ToLowerInvariant() is "http" or "https" or "ftp" or "www") return true;

        // Zeichen seit dem letzten Leerraum: das Stück des Links vor dem Wort
        int start = before.Length;
        while (start > 0 && !char.IsWhiteSpace(before[start - 1])) start--;
        var token = before.AsSpan(start);
        if (token.Length == 0) return false;

        if (token.Contains("://", StringComparison.Ordinal)) return true;                // https://…, ftp://…
        if (token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;  // www.…
        if (token.Contains('@')) return true;                                           // name@…, @Erwähnung

        // Direkt hinter einem Punkt ohne Leerzeichen ("zeriadtke.de", "z.B"): in Fließtext folgt auf einen Punkt ein
        // Leerzeichen, hier also Domain oder Abkürzung.
        if (token.Length >= 2 && token[^1] == '.' && char.IsLetterOrDigit(token[^2])) return true;

        // Pfad hinter einer Domain ("github.com/World"); ein einzelner Schrägstrich ("und/oder") zählt nicht
        if (token[^1] == '/' && token.Contains('.')) return true;

        return false;
    }

    /// <summary>
    /// Wie Gboard: Löscht die Rücktaste bis an ein Wort heran ("rene␣" → "rene"), wird es wieder zum Wort in
    /// Bearbeitung und seine Vorschläge erscheinen erneut. Liefert das Wort, das nach dem Löschen des letzten Zeichens
    /// von <paramref name="before"/> direkt vor dem Cursor steht, oder null, wenn das nicht passt: Cursor mitten im Wort,
    /// zu löschendes Zeichen ist Emoji o. Ä. (das löscht weiter die Entf-Taste des Editors), Teil einer Adresse.
    /// </summary>
    /// <param name="before">Text vor dem Cursor (Ausschnitt).</param>
    /// <param name="after">Mindestens das erste Zeichen hinter dem Cursor.</param>
    /// <param name="windowFull">Der Ausschnitt ist voll, links davon kann noch mehr Text stehen.</param>
    public static string? WordAfterDelete(string before, string after, bool windowFull)
    {
        if (before.Length < 2) return null;
        if (after.Length > 0 && IsWordChar(after[0])) return null; // mitten im Wort: nichts zusammenfügen

        // Nur einfache Zeichen selbst löschen; Emoji, Ersatzpaare, Verbinder und Kombinationszeichen bleiben dem Editor.
        char deleted = before[^1];
        if (char.IsSurrogate(deleted) || before[^2] == '\u200D' || deleted is '\u200D' or '\uFE0F'
            || char.GetUnicodeCategory(deleted) is System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark
                or System.Globalization.UnicodeCategory.EnclosingMark
                or System.Globalization.UnicodeCategory.Format)
        {
            return null;
        }

        if (!char.IsWhiteSpace(deleted) && !char.IsPunctuation(deleted) && !char.IsLetterOrDigit(deleted)) return null;

        string rest = before[..^1];
        int left = rest.Length;
        while (left > 0 && IsWordChar(rest[left - 1])) left--;
        while (left < rest.Length && rest[left] is '\'' or '-') left++; // "'rene" → "rene"

        if (left == 0 && windowFull) return null; // Wortanfang liegt außerhalb des Ausschnitts
        if (left > 0 && rest[left - 1] is '@' or '.' or '/' or '_' or '#') return null; // Adresse, Link, Hashtag

        string word = rest[left..];
        return word.Length is 0 or > 40 || !word.Any(char.IsLetter) ? null : word;
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-';
}
