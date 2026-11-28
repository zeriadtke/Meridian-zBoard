using System.Globalization;
using System.Numerics;
using System.Text;

namespace ZBoard.Suggestions;

/// <summary>
/// Persönliches Tippmuster: wie weit der Finger dieses Nutzers bei jeder Taste im Mittel neben der Tastenmitte landet
/// (viele tippen etwas zu tief, mit dem Daumen zur Mitte hin). Gelernt aus bestätigten Wörtern, nur auf dem Gerät,
/// ohne Inhalte: gespeichert werden je Taste eine Verschiebung und eine Zahl. Plattformunabhängig, threadsicher.
/// </summary>
/// <remarks>
/// Genutzt wird es zweimal: Die Tastatur verschiebt den Trefferpunkt um die mittlere Abweichung aller Tasten, bevor
/// sie die getroffene Taste bestimmt (<see cref="HitShift"/>), und die Autokorrektur rechnet mit den verschobenen
/// Tastenmitten (<see cref="Shift"/>), je Taste genauer, sobald für sie genug Treffer gesammelt sind.
/// </remarks>
public sealed class TouchModel
{
    /// <summary>So viele Treffer zählen höchstens; danach wiegt jeder neue wie ein gleitender Mittelwert (Muster ändert sich).</summary>
    public const int MaxCount = 400;

    /// <summary>Bis so viele Treffer gesammelt sind, wird die Verschiebung anteilig kleiner angesetzt (n / (n + Prior)).</summary>
    public const float GlobalPrior = 40f, KeyPrior = 60f;

    /// <summary>Weiter als so viele Tastenbreiten verschiebt das Muster nie: Es soll Fehler mindern, nicht Tasten vertauschen.</summary>
    public const float MaxShift = 0.4f;

    /// <summary>Punkte weiter als so weit von der Mitte des gemeinten Buchstabens zählen nicht (eher ein anderer Fehler).</summary>
    public const float MaxOffset = 1.2f;

    readonly Lock _lock = new();
    readonly Dictionary<char, (Vector2 Mean, int Count)> _keys = [];
    (Vector2 Mean, int Count) _global;

    public bool IsDirty { get; private set; }

    /// <summary>Wie viele Treffer bisher in das Muster eingegangen sind (höchstens <see cref="MaxCount"/>).</summary>
    public int Count
    {
        get { lock (_lock) return _global.Count; }
    }

    /// <summary>
    /// Lernt aus einem bestätigten Wort: <paramref name="word"/> ist, was am Ende im Text steht, <paramref name="points"/>
    /// je Zeichen der Fingerpunkt (in Tastenbreiten wie <paramref name="keys"/>). Nur bei gleicher Länge (dann entspricht
    /// Stelle i dem Buchstaben i; bei ausgelassenen oder doppelten Buchstaben wäre die Zuordnung unsicher).
    /// </summary>
    public void Learn(string word, IReadOnlyList<Vector2?> points, IReadOnlyDictionary<char, Vector2> keys)
    {
        if (word.Length != points.Count || word.Length == 0) return;

        lock (_lock)
        {
            for (int i = 0; i < word.Length; i++)
            {
                if (points[i] is not { } point || !keys.TryGetValue(char.ToLowerInvariant(word[i]), out var center)) continue;
                var offset = point - center;
                if (offset.Length() > MaxOffset) continue;

                _global = Update(_global, offset);
                char key = char.ToLowerInvariant(word[i]);
                _keys[key] = Update(_keys.GetValueOrDefault(key), offset);
                IsDirty = true;
            }
        }

        static (Vector2, int) Update((Vector2 Mean, int Count) old, Vector2 offset)
        {
            int count = Math.Min(old.Count + 1, MaxCount);
            return (old.Mean + (offset - old.Mean) / count, count);
        }
    }

    /// <summary>Um so viel liegt der Finger im Mittel daneben (alle Tasten); damit bestimmt die Tastatur die getroffene Taste.</summary>
    public Vector2 HitShift
    {
        get
        {
            lock (_lock) return Clamp(_global.Mean * (_global.Count / (_global.Count + GlobalPrior)));
        }
    }

    /// <summary>Mittlere Abweichung bei dieser Taste: das allgemeine Muster, verfeinert durch die Treffer dieser Taste.</summary>
    public Vector2 Offset(char key)
    {
        lock (_lock)
        {
            var global = _global.Mean * (_global.Count / (_global.Count + GlobalPrior));
            if (!_keys.TryGetValue(char.ToLowerInvariant(key), out var own)) return Clamp(global);
            return Clamp(global + (own.Mean - global) * (own.Count / (own.Count + KeyPrior)));
        }
    }

    /// <summary>Tastenmitten, wie dieser Nutzer sie trifft (für <see cref="TouchTrace"/>).</summary>
    public IReadOnlyDictionary<char, Vector2> Shift(IReadOnlyDictionary<char, Vector2> keys)
    {
        if (Count == 0) return keys;
        var shifted = new Dictionary<char, Vector2>(keys.Count);
        foreach (var (key, center) in keys) shifted[key] = center + Offset(key);
        return shifted;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _keys.Clear();
            _global = default;
            IsDirty = true;
        }
    }

    static Vector2 Clamp(Vector2 v) => v.Length() is var length && length > MaxShift ? v * (MaxShift / length) : v;

    // ---------- Speichern ----------
    // Eine Zeile je Taste: Zeichen<TAB>x<TAB>y<TAB>Anzahl; "*" ist das allgemeine Muster.

    public string Save()
    {
        var text = new StringBuilder();
        lock (_lock)
        {
            Line('*', _global);
            foreach (var (key, value) in _keys.OrderBy(k => k.Key)) Line(key, value);
            IsDirty = false;
        }

        return text.ToString();

        void Line(char key, (Vector2 Mean, int Count) value) => text
            .Append(key).Append('\t')
            .Append(value.Mean.X.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
            .Append(value.Mean.Y.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
            .Append(value.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    public static TouchModel Load(string text)
    {
        var model = new TouchModel();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length != 4 || parts[0].Length != 1
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || count <= 0 || !float.IsFinite(x) || !float.IsFinite(y))
            {
                continue; // beschädigte Zeile: überspringen, das Muster lernt neu
            }

            var value = (new Vector2(x, y), Math.Min(count, MaxCount));
            if (parts[0][0] == '*') model._global = value;
            else model._keys[parts[0][0]] = value;
        }

        return model;
    }
}
