using Android.OS;
using ZBoard.Suggestions;

namespace ZBoard.Ime;

/// <summary>
/// Wie Gboard: Tippt man im Text auf ein Wort (Cursor im oder direkt am Wort), zeigt die Leiste dafür erneut
/// Vorschläge und Korrekturen. Antippen ersetzt das ganze Wort.
/// </summary>
public sealed partial class ZBoardImeService
{
    /// <summary>So lange nach einer eigenen Eingabe gilt eine Cursorbewegung nicht als Antippen im Text.</summary>
    const long OwnEditWindowMs = 500;

    /// <summary>Angetipptes Wort: Bereich im Text, das Wort selbst und das Wort davor (für den Satzkontext).</summary>
    sealed record ReselectedWord(int Start, int End, string Word, string? Previous);

    ReselectedWord? _reselect;
    long _lastEditUptime;

    /// <summary>Eigene Eingabe (Taste, Vorschlag, Wischen): folgende Cursor-Meldungen stammen von uns.</summary>
    void MarkEdit()
    {
        _lastEditUptime = SystemClock.UptimeMillis();
        _reselect = null;
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-';

    /// <summary>Cursor wurde von außen gesetzt: steht er in einem Wort, dessen Vorschläge zeigen.</summary>
    void OnCursorPlaced(int selStart, int selEnd)
    {
        bool hadReselect = _reselect != null;
        _reselect = null;

        bool ownEdit = SystemClock.UptimeMillis() - _lastEditUptime < OwnEditWindowMs;
        if (!ownEdit && selStart == selEnd && FindWordAtCursor(selStart) is { } word)
        {
            _reselect = word;
            if (ShowReselect()) return;
            _reselect = null;
        }

        if (hadReselect) UpdateSuggestions(); // Cursor hat das Wort verlassen: Vorschläge zurücksetzen
    }

    ReselectedWord? FindWordAtCursor(int cursor)
    {
        if (!_profile.AllowSuggestions || _profile.IsEmail || _emojiSearchActive || _voiceActive) return null;
        var ic = Ic;
        if (ic == null) return null;

        string before = TextBeforeCursor(64);
        string after = ic.GetTextAfterCursorFormatted(64, 0)?.ToString() ?? string.Empty;

        int left = before.Length;
        while (left > 0 && IsWordChar(before[left - 1])) left--;
        int right = 0;
        while (right < after.Length && IsWordChar(after[right])) right++;

        string word = before[left..] + after[..right];
        // Teil einer Adresse oder eines Links ("x@y", "www.") nicht anfassen
        bool partOfAddress = (left > 0 && before[left - 1] is '@' or '.' or '/' or '_')
                             || (right < after.Length && after[right] is '@' or '/' or '_');
        if (word.Length is 0 or > 40 || !word.Any(char.IsLetter) || partOfAddress) return null;

        int start = cursor - (before.Length - left);
        return start < 0 ? null : new ReselectedWord(start, start + word.Length, word, WordBefore(before[..left]));
    }

    /// <summary>Wort vor <paramref name="text"/>s Ende, wenn dazwischen nur Leerraum steht (wie <see cref="PreviousWord"/>).</summary>
    static string? WordBefore(string text)
    {
        if (text.Length == 0 || !char.IsWhiteSpace(text[^1])) return null;
        string trimmed = text.TrimEnd();
        int start = trimmed.Length;
        while (start > 0 && IsWordChar(trimmed[start - 1])) start--;
        return start < trimmed.Length ? trimmed[start..] : null;
    }

    /// <summary>Vorschläge für das angetippte Wort; false, wenn es keine anderen gibt.</summary>
    bool ShowReselect()
    {
        if (_reselect is not { } r || _suggestionBar == null || Engine is not { } engine) return false;

        var result = engine.Suggest(r.Word, r.Previous, max: 4);
        bool autocorrect = _settings.AutoCorrect && _profile.AllowAutocorrect;
        var options = new List<string>();
        if (autocorrect && result.AutoCorrection is { } correction) options.Add(correction);
        options.AddRange(result.Candidates);
        options = [.. options.Where(o => o != r.Word).Distinct(StringComparer.Ordinal).Take(3)];
        if (options.Count == 0) return false;

        bool emphasize = autocorrect && result.AutoCorrection != null;
        SuggestionSlot? Slot(int i) => i < options.Count
            ? new SuggestionSlot(options[i], options[i], i == 0 && emphasize, false, IsReplacement: true)
            : null;
        _currentSuggestions = SuggestionResult.Empty;
        _suggestionBar.SetSlots(Slot(1), Slot(0), Slot(2));
        return true;
    }

    /// <summary>Ersetzt das angetippte Wort; der Cursor steht danach hinter dem neuen Wort.</summary>
    void PickReplacement(SuggestionSlot slot)
    {
        var ic = Ic;
        var r = _reselect;
        MarkEdit();
        if (ic == null || r == null) return;
        MeridianSuggestionPicked(); // Meridian: nur das Ereignis

        // MarkEdit oben sorgt dafür, dass die folgende Cursor-Meldung nicht erneut als Antippen gilt
        ic.BeginBatchEdit();
        ic.SetComposingRegion(r.Start, r.End);
        ic.CommitText(J(slot.Word), 1);
        ic.EndBatchEdit();

        _lastCorrection = null;
        _autoSpaceInserted = false;
        Learn(r.Previous, slot.Word);
        UpdateSuggestions();
    }
}
