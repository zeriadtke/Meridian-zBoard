// zHeal Meridian – Tipp-Dynamik. Nicht Teil von zBoard (upstream), siehe Meridian/README.md.
namespace ZBoard.Meridian.Typing;

/// <summary>
/// Zusammenfassung einer Eingabesitzung (ein Textfeld von Öffnen bis Schließen). Enthält ausschließlich Zeiten
/// und Zählwerte – keine Zeichen, keine Wörter, keine App-Namen.
/// </summary>
public sealed class TypingSessionSummary
{
    public long StartedAtMs { get; init; }
    public long EndedAtMs { get; init; }
    public int TotalKeys { get; init; }
    public int AlphanumericKeys { get; init; }
    public int BackspaceKeys { get; init; }
    public int SpaceKeys { get; init; }
    public int EnterKeys { get; init; }
    public int PunctuationKeys { get; init; }
    public int Autocorrections { get; init; }
    public int SuggestionsPicked { get; init; }

    /// <summary>Zeit zwischen zwei Buchstaben/Ziffern (Inter-Key-Intervall), nur innerhalb von Schreibphasen.</summary>
    public double IkiMedianMs { get; init; }
    public double IkiMeanMs { get; init; }
    public double IkiSdMs { get; init; }
    public double IkiP90Ms { get; init; }

    /// <summary>Wie lange eine Taste gedrückt wird.</summary>
    public double HoldMedianMs { get; init; }

    /// <summary>Pausen ≥ 2 s zwischen zwei Tasten.</summary>
    public int Pauses { get; init; }

    /// <summary>Reine Schreibzeit (Lücken ≥ 5 s zählen nicht).</summary>
    public long ActiveTypingMs { get; init; }

    /// <summary>
    /// Stabile Schlüssel – identisch mit der Datenschnittstelle der Meridian-App
    /// (<c>TypingSessionIntake.FromValues</c> in Meridian.Core). Ganzzahlen als long, Messwerte als double.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object>> ToValues() =>
    [
        new("started_ms", StartedAtMs),
        new("ended_ms", EndedAtMs),
        new("total_keys", (long)TotalKeys),
        new("alnum_keys", (long)AlphanumericKeys),
        new("backspace_keys", (long)BackspaceKeys),
        new("space_keys", (long)SpaceKeys),
        new("enter_keys", (long)EnterKeys),
        new("punct_keys", (long)PunctuationKeys),
        new("autocorrections", (long)Autocorrections),
        new("suggestions_picked", (long)SuggestionsPicked),
        new("iki_median_ms", IkiMedianMs),
        new("iki_mean_ms", IkiMeanMs),
        new("iki_sd_ms", IkiSdMs),
        new("iki_p90_ms", IkiP90Ms),
        new("hold_median_ms", HoldMedianMs),
        new("pauses", (long)Pauses),
        new("active_ms", ActiveTypingMs),
    ];
}
