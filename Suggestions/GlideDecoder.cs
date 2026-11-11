using System.Numerics;

namespace ZBoard.Suggestions;

/// <summary>
/// Wischeingabe (Glide Typing): vergleicht die gewischte Spur mit der Idealspur jedes passenden Wortes.
/// Koordinaten in Tastenbreiten (eine Standardtaste = 1). Plattformunabhängig.
/// Parameter mit simulierten Spuren auf dem deutschen Wörterbuch abgestimmt (siehe README).
/// </summary>
public sealed class GlideDecoder(WordDictionary dictionary, UserHistory? history = null)
{
    const int Samples = 40;        // Stützpunkte für den Formvergleich
    const float Sigma = 0.6f;      // Toleranz der mittleren Abweichung (Tastenbreiten)
    const float Alpha = 0.12f;     // Gewicht der Worthäufigkeit
    const float EndWeight = 0.5f;  // Gewicht für genauen Anfang und genaues Ende
    const float Radius = 1.3f;     // erster/letzter Buchstabe höchstens so weit von Spuranfang/-ende
    const float Cover = 1.2f;      // jeder Buchstabe höchstens so weit von der Spur
    const double ContextBonus = 1.0;
    const double UserWordWeight = 5000.0;

    /// <summary>Beste Wörter für die Spur, bestes zuerst, in Wörterbuchschreibweise.</summary>
    public IReadOnlyList<string> Decode(
        IReadOnlyList<Vector2> path,
        IReadOnlyDictionary<char, Vector2> keys,
        IReadOnlySet<string>? followers = null,
        int max = 4) =>
        [.. DecodeScored(path, keys, followers, max).Select(r => r.Word)];

    /// <summary>Wie <see cref="Decode"/>, mit Bewertung (logarithmisch, höher ist besser).</summary>
    public IReadOnlyList<(string Word, double Score)> DecodeScored(
        IReadOnlyList<Vector2> path,
        IReadOnlyDictionary<char, Vector2> keys,
        IReadOnlySet<string>? followers = null,
        int max = 4)
    {
        if (path.Count < 2) return [];
        float pathLength = Length(path);
        if (pathLength < 0.5f) return [];

        var sampled = Resample(path, Samples);
        var start = path[0];
        var end = path[^1];
        List<(double Score, string Word)> results = [];

        foreach (var (letter, position) in keys)
        {
            if (Vector2.Distance(position, start) > Radius) continue;

            foreach (var (key, form, freq) in dictionary.WithPrefix(letter.ToString()))
            {
                if (key.Length < 2) continue;
                if (!TryPosition(key[^1], keys, out var last) || Vector2.Distance(last, end) > Radius) continue;

                var template = Template(key, keys);
                if (template == null || template.Count < 2) continue;

                float templateLength = Length(template);
                if (pathLength < 0.5f * templateLength - 1f || pathLength > 1.6f * templateLength + 1f) continue;
                if (!Covered(template, sampled)) continue;

                var resampled = Resample(template, Samples);
                float deviation = 0;
                for (int j = 0; j < Samples; j++) deviation += Vector2.Distance(resampled[j], sampled[j]);
                deviation /= Samples;

                float ends = Vector2.Distance(template[0], start) + Vector2.Distance(template[^1], end);
                double frequency = freq + (history?.CountOf(key) ?? 0) * UserWordWeight;
                double score = -(deviation * deviation) / (2 * Sigma * Sigma)
                               - EndWeight * ends
                               + Alpha * Math.Log(Math.Max(1, frequency));
                if (followers?.Contains(key) == true) score += ContextBonus;

                results.Add((score, form));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .DistinctBy(r => r.Word, StringComparer.Ordinal)
            .Take(max)
            .Select(r => (r.Word, r.Score))
            .ToList();
    }

    /// <summary>Tastenmittelpunkte der Buchstaben; doppelte Buchstaben ("ll") zählen einmal.</summary>
    static List<Vector2>? Template(string word, IReadOnlyDictionary<char, Vector2> keys)
    {
        List<Vector2> points = [];
        foreach (var ch in word)
        {
            if (ch is '\'' or '-') continue;
            if (!TryPosition(ch, keys, out var p)) return null;
            if (points.Count == 0 || points[^1] != p) points.Add(p);
        }

        return points;
    }

    /// <summary>Position eines Buchstabens; ß liegt auf s, Akzente auf dem Grundbuchstaben (é → e).</summary>
    static bool TryPosition(char ch, IReadOnlyDictionary<char, Vector2> keys, out Vector2 position)
    {
        if (keys.TryGetValue(ch, out position)) return true;
        if (ch == 'ß') return keys.TryGetValue('s', out position);

        var decomposed = ch.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return decomposed.Length > 0 && keys.TryGetValue(decomposed[0], out position);
    }

    static bool Covered(List<Vector2> template, Vector2[] path)
    {
        foreach (var q in template)
        {
            float best = float.MaxValue;
            foreach (var p in path)
            {
                float d = Vector2.Distance(q, p);
                if (d < best) best = d;
            }

            if (best > Cover) return false;
        }

        return true;
    }

    static float Length(IReadOnlyList<Vector2> points)
    {
        float total = 0;
        for (int i = 1; i < points.Count; i++) total += Vector2.Distance(points[i - 1], points[i]);
        return total;
    }

    /// <summary>Verteilt <paramref name="n"/> Punkte gleichmäßig entlang der Linie.</summary>
    static Vector2[] Resample(IReadOnlyList<Vector2> points, int n)
    {
        var result = new Vector2[n];
        float total = Length(points);
        if (points.Count == 1 || total <= 0)
        {
            Array.Fill(result, points[0]);
            return result;
        }

        float step = total / (n - 1);
        result[0] = points[0];
        int count = 1;
        float accumulated = 0;
        var previous = points[0];
        int i = 1;

        while (i < points.Count && count < n)
        {
            float d = Vector2.Distance(previous, points[i]);
            if (accumulated + d >= step && d > 0)
            {
                float t = (step - accumulated) / d;
                var q = Vector2.Lerp(previous, points[i], t);
                result[count++] = q;
                previous = q;
                accumulated = 0;
            }
            else
            {
                accumulated += d;
                previous = points[i];
                i++;
            }
        }

        while (count < n) result[count++] = points[^1];
        return result;
    }
}
