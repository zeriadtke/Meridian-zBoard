using System.Numerics;

namespace ZBoard.Suggestions;

/// <summary>
/// Wo der Finger bei jedem Buchstaben des getippten Worts die Tastatur getroffen hat. Damit rechnet die Autokorrektur
/// genauer als mit „Nachbartaste ja oder nein“: Ein Treffer am Rand zwischen „n“ und „m“ lässt beide Buchstaben
/// fast gleich wahrscheinlich, ein Treffer mitten auf dem „n“ spricht klar gegen das „m“. Plattformunabhängig.
/// </summary>
/// <param name="points">Ein Punkt je Buchstabe des Worts, in Tastenbreiten; null, wo keiner bekannt ist (Zeichen aus dem
/// Popup, wiederhergestelltes Wort).</param>
/// <param name="keys">Mitten der Buchstabentasten, in denselben Einheiten.</param>
public sealed class TouchTrace(IReadOnlyList<Vector2?> points, IReadOnlyDictionary<char, Vector2> keys)
{
    /// <summary>
    /// Streuung der Fingerposition um die Tastenmitte in Tastenbreiten, mit der gerechnet wird. Größer als die
    /// reine Treffgenauigkeit, weil Finger auch systematisch danebenliegen. Abgestimmt mit tools/SuggestionEval.
    /// </summary>
    public const float Sigma = 0.35f;

    public int Count => points.Count;

    /// <summary>
    /// Kosten dafür, dass an Stelle <paramref name="position"/> statt des getippten Buchstabens
    /// <paramref name="candidate"/> gemeint war, in denselben Einheiten wie die übrige Editierdistanz (0 = ebenso
    /// wahrscheinlich wie das Getippte, 1 = beliebiger anderer Buchstabe); null, wenn der Punkt oder eine der beiden
    /// Tasten unbekannt ist.
    /// </summary>
    /// <remarks>
    /// Bei normalverteilten Treffern ist das Verhältnis der Wahrscheinlichkeiten exp(−(d² Kandidat − d² getippt) / 2σ²).
    /// Die Editierdistanz geht als <paramref name="penaltyBase"/> hoch −Kosten in die Bewertung ein, daraus folgen die
    /// Kosten (d² Kandidat − d² getippt) / (2σ² · ln penaltyBase).
    /// </remarks>
    public float? Cost(int position, char typed, char candidate, double penaltyBase)
    {
        if (position < 0 || position >= points.Count || points[position] is not { } point) return null;
        if (!keys.TryGetValue(typed, out var typedKey) || !keys.TryGetValue(candidate, out var candidateKey)) return null;

        float extra = Vector2.DistanceSquared(point, candidateKey) - Vector2.DistanceSquared(point, typedKey);
        float cost = extra / (2 * Sigma * Sigma * (float)Math.Log(penaltyBase));
        return Math.Clamp(cost, 0f, 1f);
    }
}
