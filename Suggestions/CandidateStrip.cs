namespace ZBoard.Suggestions;

/// <summary>
/// Anordnung einer durchgehenden Vorschlagszeile, die sich seitlich schieben lässt (Schriftzeichen der
/// Pinyin-Eingabe): Jeder Eintrag ist so breit wie sein Text, mindestens aber <c>minWidth</c>. Rechnet nur;
/// gezeichnet wird in <c>SuggestionBarView</c>. Plattformunabhängig.
/// </summary>
public sealed class CandidateStrip
{
    readonly float[] _left;

    /// <param name="textWidths">Breite des Texts je Eintrag.</param>
    /// <param name="padding">Rand links und rechts vom Text.</param>
    /// <param name="minWidth">Mindestbreite eines Eintrags (Fingerbreite).</param>
    public CandidateStrip(IReadOnlyList<float> textWidths, float padding, float minWidth)
    {
        _left = new float[textWidths.Count + 1];
        for (int i = 0; i < textWidths.Count; i++)
        {
            _left[i + 1] = _left[i] + Math.Max(minWidth, textWidths[i] + 2 * padding);
        }
    }

    public int Count => _left.Length - 1;

    /// <summary>Breite aller Einträge zusammen.</summary>
    public float TotalWidth => _left[^1];

    /// <summary>Linker und rechter Rand des Eintrags, ohne Verschiebung.</summary>
    public (float Left, float Right) Bounds(int index) => (_left[index], _left[index + 1]);

    /// <summary>Verschiebung in den erlaubten Bereich bringen: nie über den Anfang oder das Ende hinaus.</summary>
    public float Clamp(float scroll, float viewWidth) => Math.Clamp(scroll, 0, Math.Max(0, TotalWidth - viewWidth));

    /// <summary>Einträge, die bei dieser Verschiebung ganz oder teilweise zu sehen sind: [Erster, Ende).</summary>
    public (int First, int End) Visible(float scroll, float viewWidth)
    {
        int first = 0;
        while (first < Count && _left[first + 1] <= scroll) first++;
        int end = first;
        while (end < Count && _left[end] < scroll + viewWidth) end++;
        return (first, end);
    }
}
