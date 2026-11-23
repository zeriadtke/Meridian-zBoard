using ZBoard.Suggestions;

namespace ZBoard.Ime;

/// <summary>
/// Persönliches Wörterbuch wie bei Gboard (Einstellungen → Wörterbuch): Eigene Wörter schlägt die Leiste beim Tippen
/// vor und die Autokorrektur lässt sie stehen bzw. setzt die gespeicherte Schreibweise ein ("zboard" → "zBoard").
/// Zu einem getippten Kürzel zeigt die Leiste den hinterlegten Text ("mfg" → "Mit freundlichen Grüßen"); eingesetzt
/// wird er wie bei Gboard erst durch Antippen.
/// </summary>
public sealed partial class ZBoardImeService
{
    const int ShortcutDisplayLength = 32;

    PersonalDictionary _personal = PersonalDictionary.Empty;
    string _personalRaw = string.Empty;

    /// <summary>Beim Öffnen der Tastatur: Wörterbuch aus den Einstellungen übernehmen, wenn es sich geändert hat.</summary>
    void StartPersonal()
    {
        string raw = _settings.PersonalDictionary;
        if (raw == _personalRaw) return;
        _personalRaw = raw;
        _personal = PersonalDictionary.Parse(raw);
    }

    static string ShortenForBar(string text) =>
        text.Length <= ShortcutDisplayLength ? text : text[..(ShortcutDisplayLength - 1)] + "…";

    /// <summary>
    /// Vorschläge aus dem persönlichen Wörterbuch zum Wort in Bearbeitung. false: nichts Passendes, die normalen
    /// Vorschläge gelten.
    /// </summary>
    bool ShowPersonalSuggestions(string typed)
    {
        if (_suggestionBar == null || _personal.Entries.Count == 0) return false;

        string? own = _personal.Word(typed);
        var expansions = _personal.Expansions(typed);
        var completions = _personal.Completions(typed, 2);
        if (own == null && expansions.Count == 0 && completions.Count == 0) return false;

        var result = Engine?.Suggest(typed, SentenceContext(), touches: Touches()) ?? SuggestionResult.Empty;
        _currentSuggestions = result;

        // Fett ist nur, was die Leertaste einsetzt: die gespeicherte Schreibweise eines eigenen Worts
        bool corrects = own != null && own != typed && _settings.AutoCorrect && _profile.AllowAutocorrect && !InUrlOrAddress();
        var slots = new List<SuggestionSlot>();
        if (own != null) slots.Add(new SuggestionSlot(own, own, corrects, false));
        foreach (var text in expansions)
        {
            if (slots.All(s => s.Word != text)) slots.Add(new SuggestionSlot(ShortenForBar(text), text, false, false, IsShortcut: true));
        }

        foreach (var word in completions)
        {
            // Ein einzelnes Wort verhält sich wie jeder Vorschlag; ein Text mit Leerzeichen wird nur eingesetzt
            if (slots.All(s => s.Word != word)) slots.Add(new SuggestionSlot(ShortenForBar(word), word, false, false, IsShortcut: word.Contains(' ')));
        }

        string? other = result.Candidates.FirstOrDefault(c => c != typed && slots.All(s => s.Word != c));
        var otherSlot = other == null ? null : new SuggestionSlot(other, other, false, false);

        // Links wie gewohnt das Getippte in Anführungszeichen – außer es ist selbst das eigene Wort in der Mitte
        var left = own == typed ? otherSlot : new SuggestionSlot($"„{typed}“", typed, false, true);
        var right = slots.Count > 1 ? slots[1] : own == typed ? null : otherSlot;
        _suggestionBar.SetSlots(left, slots[0], right);
        return true;
    }

    /// <summary>Text eines Kürzels (oder ein eigener Text mit Leerzeichen) angetippt: ersetzt das Wort in Bearbeitung.</summary>
    void PickShortcut(SuggestionSlot slot)
    {
        var ic = Ic;
        if (ic == null) return;
        MarkEdit();
        ic.CommitText(J(slot.Word + " "), 1);
        _composing.Clear();
        _glideActive = false;
        _lastCorrection = null;
        _restoredWord = null;
        _lastSpaceCommit = null;
        _autoSpaceInserted = true;
        _currentSuggestions = SuggestionResult.Empty;
        UpdateAutoShift();
        UpdateSuggestions();
    }
}
