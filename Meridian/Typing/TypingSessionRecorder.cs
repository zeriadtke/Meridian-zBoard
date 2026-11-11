// zHealth Meridian – Tipp-Dynamik. Nicht Teil von zBoard (upstream), siehe Meridian/README.md.
namespace ZBoard.Meridian.Typing;

/// <summary>
/// Misst die Tipp-Dynamik einer Eingabesitzung (angelehnt an BiAffect): Zeitabstände zwischen Tasten,
/// Haltedauer, Korrekturen, Pausen. Ohne Android-Abhängigkeiten (unit-getestet). Es werden nur Zeiten und
/// Tastenarten verarbeitet – nie Zeichen.
/// <para>
/// Rechenregeln wie Meridian.Core (<c>TypingSessionRecorder</c>) und die frühere Meridian-Tastatur
/// (TypingSessionRecorder.java), damit die Werte über Geräte und Tastatur-Versionen hinweg dasselbe bedeuten.
/// </para>
/// </summary>
public sealed class TypingSessionRecorder
{
    /// <summary>Sitzungen mit weniger Tasten werden verworfen (zu wenig Information, zu leicht zuordenbar).</summary>
    public const int MinKeys = 5;

    /// <summary>Abstand, ab dem eine Pause gezählt wird.</summary>
    public const long PauseMs = 2_000;

    /// <summary>Lücken ab dieser Länge zählen nicht zur Schreibzeit und nicht zu den Tastenabständen.</summary>
    public const long BurstGapMs = 5_000;

    /// <summary>Längere Drücke sind Halten (Wiederholung, Sonderzeichen), keine Haltedauer.</summary>
    public const long MaxHoldMs = 3_000;

    readonly List<long> _ikis = [];
    readonly List<long> _holds = [];
    long _lastDownMs = -1;
    KeyCategory? _lastDownCategory;
    long _startWallMs;
    int _total, _alnum, _backspace, _space, _enter, _punct, _pauses, _autocorrections, _suggestions;
    long _activeMs;

    public bool IsActive { get; private set; }

    /// <summary>Anzahl der bisher gezählten Tasten der laufenden Sitzung.</summary>
    public int TotalKeys => _total;

    /// <summary>Beginnt eine Sitzung; eine laufende wird ohne Ergebnis verworfen.</summary>
    public void Start(long wallMs)
    {
        Reset();
        IsActive = true;
        _startWallMs = wallMs;
    }

    /// <summary>Taste gedrückt. <paramref name="monotonicMs"/> ist eine stetige Uhr (SystemClock.uptimeMillis).</summary>
    public void KeyDown(long monotonicMs, KeyCategory category)
    {
        if (!IsActive) return;
        _total++;
        switch (category)
        {
            case KeyCategory.Alphanumeric: _alnum++; break;
            case KeyCategory.Backspace: _backspace++; break;
            case KeyCategory.Space: _space++; break;
            case KeyCategory.Enter: _enter++; break;
            case KeyCategory.Punctuation: _punct++; break;
        }

        if (_lastDownMs >= 0)
        {
            long gap = monotonicMs - _lastDownMs;
            if (gap >= PauseMs) _pauses++;
            if (gap is >= 0 and < BurstGapMs)
            {
                _activeMs += gap;
                if (category == KeyCategory.Alphanumeric && _lastDownCategory == KeyCategory.Alphanumeric) _ikis.Add(gap);
            }
        }

        _lastDownMs = monotonicMs;
        _lastDownCategory = category;
    }

    /// <summary>Haltedauer einer Taste (Loslassen minus Drücken).</summary>
    public void AddHold(long holdMs)
    {
        if (IsActive && holdMs is >= 0 and < MaxHoldMs) _holds.Add(holdMs);
    }

    public void AutoCorrected()
    {
        if (IsActive) _autocorrections++;
    }

    public void SuggestionPicked()
    {
        if (IsActive) _suggestions++;
    }

    /// <summary>Beendet die Sitzung. <c>null</c>, wenn keine lief oder sie zu kurz war.</summary>
    public TypingSessionSummary? Finish(long wallMs)
    {
        if (!IsActive) return null;
        IsActive = false;
        if (_total < MinKeys)
        {
            Reset();
            return null;
        }

        double ikiMean = _ikis.Count > 0 ? _ikis.Average() : 0;
        var summary = new TypingSessionSummary
        {
            StartedAtMs = _startWallMs,
            EndedAtMs = Math.Max(wallMs, _startWallMs),
            TotalKeys = _total,
            AlphanumericKeys = _alnum,
            BackspaceKeys = _backspace,
            SpaceKeys = _space,
            EnterKeys = _enter,
            PunctuationKeys = _punct,
            Autocorrections = _autocorrections,
            SuggestionsPicked = _suggestions,
            Pauses = _pauses,
            ActiveTypingMs = _activeMs,
            IkiMedianMs = _ikis.Count > 0 ? Percentile(_ikis, 0.5) : 0,
            IkiP90Ms = _ikis.Count > 0 ? Percentile(_ikis, 0.9) : 0,
            IkiMeanMs = ikiMean,
            IkiSdMs = _ikis.Count > 0 ? Sd(_ikis, ikiMean) : 0,
            HoldMedianMs = _holds.Count > 0 ? Percentile(_holds, 0.5) : 0,
        };
        Reset();
        return summary;
    }

    void Reset()
    {
        IsActive = false;
        _lastDownMs = -1;
        _lastDownCategory = null;
        _ikis.Clear();
        _holds.Clear();
        _total = _alnum = _backspace = _space = _enter = _punct = _pauses = _autocorrections = _suggestions = 0;
        _activeMs = 0;
    }

    /// <summary>Lineare Interpolation zwischen den Rängen.</summary>
    internal static double Percentile(IReadOnlyList<long> values, double p)
    {
        var sorted = values.Order().ToArray();
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    static double Sd(IReadOnlyList<long> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (values.Count - 1));
    }
}
