// zHeal Meridian – Tipp-Dynamik. Nicht Teil von zBoard (upstream), siehe Meridian/README.md.
using ZBoard.Keyboard;

namespace ZBoard.Meridian.Typing;

/// <summary>
/// Übersetzt die Rückmeldungen der zBoard-Tastaturansicht in Messwerte für <see cref="TypingSessionRecorder"/>.
/// Ohne Android-Abhängigkeiten (unit-getestet).
/// <para>
/// Gezählt wird erst beim Loslassen, weil erst dann feststeht, was der Druck war: ein Zeichen, ein Zeichen aus dem
/// Popup (langer Druck), eine Wischeingabe, Wort-Löschen per Wischen oder Cursor-Bewegung über die Leertaste.
/// Für Tastenabstände und Pausen zählt trotzdem der Zeitpunkt des Drückens.
/// </para>
/// </summary>
public sealed class TypingTracker
{
    readonly TypingSessionRecorder _recorder = new();
    long _downMs = -1;

    public bool IsActive => _recorder.IsActive;

    /// <summary>Neues Eingabefeld. Eine laufende Sitzung wird vorher abgeschlossen und zurückgegeben.</summary>
    public TypingSessionSummary? Start(bool mayRecord, long wallMs)
    {
        var previous = Finish(wallMs);
        if (mayRecord) _recorder.Start(wallMs);
        return previous;
    }

    /// <summary>Eingabefeld geschlossen. <c>null</c>, wenn keine Sitzung lief oder sie zu kurz war.</summary>
    public TypingSessionSummary? Finish(long wallMs)
    {
        _downMs = -1;
        return _recorder.Finish(wallMs);
    }

    public void KeyDown(long uptimeMs)
    {
        if (_recorder.IsActive) _downMs = uptimeMs;
    }

    /// <param name="output">Von der Taste erzeugter Text (nur zur Einordnung, wird nicht behalten).</param>
    /// <param name="gesture">Wischeingabe oder Wort-Löschen per Wischen statt eines Tastendrucks.</param>
    public void KeyUp(KeyDef key, string? output, long uptimeMs, bool gesture)
    {
        long down = _downMs;
        _downMs = -1;
        if (!_recorder.IsActive || down < 0) return;

        var category = Classify(key, output, gesture);
        if (category is not { } c) return;

        _recorder.KeyDown(down, c);
        // Bei Gesten wäre es die Dauer des Wischens, beim Popup die Wartezeit – beides keine Haltedauer.
        if (!gesture && !IsPopup(key, output)) _recorder.AddHold(uptimeMs - down);
    }

    public void AutoCorrected() => _recorder.AutoCorrected();

    public void SuggestionPicked() => _recorder.SuggestionPicked();

    /// <summary>Tastenart oder <c>null</c>, wenn der Druck nicht als Taste zählt.</summary>
    internal static KeyCategory? Classify(KeyDef key, string? output, bool gesture)
    {
        if (gesture)
        {
            // Wort-Löschen per Wischen ist eine Korrektur; eine Wischeingabe von Wörtern liefert keine Einzeltasten.
            return key.Action == KeyAction.Delete ? KeyCategory.Backspace : null;
        }

        return key.Action switch
        {
            // Kein Text: Popup ohne Auswahl verlassen → kein Tastendruck
            KeyAction.Character => output is null ? null : KeyCategories.OfText(output),
            // Ohne Text war es Cursor-Bewegung per Wischen über die Leertaste
            KeyAction.Space => output is null ? null : KeyCategory.Space,
            KeyAction.Delete => KeyCategory.Backspace,
            KeyAction.Enter => KeyCategory.Enter,
            _ => KeyCategory.Function,
        };
    }

    /// <summary>Zeichen aus dem Popup (langer Druck): die Haltedauer wäre die Popup-Verzögerung, nicht das Tippen.</summary>
    static bool IsPopup(KeyDef key, string? output) =>
        key.Action == KeyAction.Character && output is not null
        && !string.Equals(output, key.Label, StringComparison.OrdinalIgnoreCase);
}
