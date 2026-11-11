namespace ZBoard.Suggestions;

/// <summary>
/// Wörterbuch mit Häufigkeiten, gespeichert als sortiertes Array (kompakt genug für 100 000+ Wörter in einer Tastatur).
/// Ein Eintrag kann mehrere Schreibweisen haben ("frage" und "Frage"); geordnet nach Häufigkeit.
/// Plattformunabhängig (kein Android-Code), damit es sich in Unit-Tests prüfen lässt.
/// </summary>
public sealed class WordDictionary
{
    const int TopK = 6;
    const int CachedPrefixLength = 2;

    Dictionary<string, List<(string Word, int Freq)>>? _builder = new(StringComparer.Ordinal);

    string[] _keys = [];      // kleingeschrieben, ordinal sortiert, eindeutig
    int[] _freq = [];            // höchste Häufigkeit je Schlüssel
    string[][] _forms = []; // Schreibweisen je Schlüssel, häufigste zuerst
    readonly Dictionary<string, (string Word, int Freq)[]> _prefixTop = new(StringComparer.Ordinal);

    public int Count => _keys.Length;

    /// <summary>
    /// Gemeinsamer Maßstab für die Häufigkeiten aller Sprachen (etwa die Summe der deutschen Liste). Die Wortlisten
    /// stammen aus unterschiedlich großen Korpora (die englische zählt rund viermal so viel wie die deutsche); erst auf
    /// denselben Maßstab gebracht, lassen sich Wörter verschiedener Sprachen bei der mehrsprachigen Eingabe vergleichen.
    /// </summary>
    public const long ReferenceTotal = 150_000_000;

    /// <summary>Summe aller Häufigkeiten (nach einer Normierung die normierte Summe).</summary>
    public long TotalFrequency { get; private set; }

    /// <summary>
    /// Lädt eine Wortliste: eine Zeile pro Schreibweise, "wort[Tab]häufigkeit". Zeilen mit # werden ignoriert.
    /// Dieselbe Kleinschreibung darf mehrfach vorkommen ("frage" und "Frage").
    /// </summary>
    /// <param name="normalizeTo">Häufigkeiten so skalieren, dass ihre Summe diesem Wert entspricht (siehe <see cref="ReferenceTotal"/>).</param>
    public static WordDictionary Load(Stream stream, long? normalizeTo = null)
    {
        var dict = new WordDictionary();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') continue;

            int tab = line.IndexOf('\t');
            if (tab < 0) tab = line.IndexOf(' ');
            string word = tab < 0 ? line.Trim() : line[..tab].Trim();
            int freq = tab >= 0 && int.TryParse(line.AsSpan(tab + 1).Trim(), out var f) ? f : 1;
            if (word.Length > 0) dict.Add(word, freq);
        }

        dict.BuildTopLists(normalizeTo);
        return dict;
    }

    /// <summary>Fügt eine Schreibweise hinzu. Muss vor <see cref="BuildTopLists"/> aufgerufen werden.</summary>
    public void Add(string word, int freq)
    {
        if (_builder == null) throw new InvalidOperationException("Wörterbuch ist bereits fertig aufgebaut.");

        var key = word.ToLowerInvariant();
        if (!_builder.TryGetValue(key, out var forms))
        {
            forms = new List<(string, int)>(1);
            _builder[key] = forms;
        }

        int existing = forms.FindIndex(x => x.Word == word);
        if (existing < 0) forms.Add((word, freq));
        else if (freq > forms[existing].Freq) forms[existing] = (word, freq);
    }

    /// <summary>Schließt den Aufbau ab: sortiert und berechnet die Vorschlagslisten für kurze Präfixe.</summary>
    /// <param name="normalizeTo">Häufigkeiten so skalieren, dass ihre Summe diesem Wert entspricht; null lässt sie unverändert.</param>
    public void BuildTopLists(long? normalizeTo = null)
    {
        if (_builder == null) return;

        long total = _builder.Values.Sum(forms => forms.Sum(x => (long)x.Freq));
        if (normalizeTo is > 0 && total > 0)
        {
            double factor = (double)normalizeTo.Value / total;
            foreach (var forms in _builder.Values)
            {
                for (int k = 0; k < forms.Count; k++)
                {
                    forms[k] = (forms[k].Word, (int)Math.Clamp(Math.Round(forms[k].Freq * factor), 1, int.MaxValue));
                }
            }

            total = _builder.Values.Sum(forms => forms.Sum(x => (long)x.Freq));
        }

        TotalFrequency = total;

        var keys = _builder.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);

        _keys = keys;
        _freq = new int[keys.Length];
        _forms = new string[keys.Length][];
        for (int i = 0; i < keys.Length; i++)
        {
            var forms = _builder[keys[i]].OrderByDescending(x => x.Freq).ToArray();
            _freq[i] = forms[0].Freq;
            _forms[i] = forms.Select(x => x.Word).ToArray();
        }

        _builder = null;

        // Für ein- und zweibuchstabige Präfixe sind die Bereiche groß: einmal vorberechnen.
        var prefixes = _keys.Where(k => k.Length >= 1).Select(k => k[..1])
            .Concat(_keys.Where(k => k.Length >= 2).Select(k => k[..2]))
            .Distinct(StringComparer.Ordinal);
        foreach (var prefix in prefixes)
        {
            _prefixTop[prefix] = ScanTop(prefix);
        }
    }

    /// <summary>Häufigste Schreibweise und Häufigkeit eines Wortes (Groß-/Kleinschreibung egal).</summary>
    public (string Word, int Freq)? Lookup(string word)
    {
        int i = IndexOf(word.ToLowerInvariant());
        return i < 0 ? null : (_forms[i][0], _freq[i]);
    }

    public bool Contains(string word) => IndexOf(word.ToLowerInvariant()) >= 0;

    /// <summary>Alle Schreibweisen eines Wortes, häufigste zuerst; leer, wenn unbekannt.</summary>
    public IReadOnlyList<string> Forms(string word)
    {
        int i = IndexOf(word.ToLowerInvariant());
        return i < 0 ? [] : _forms[i];
    }

    /// <summary>Alle Einträge mit diesem Präfix: Schlüssel (klein), häufigste Schreibweise, Häufigkeit.</summary>
    public IEnumerable<(string Key, string Form, int Freq)> WithPrefix(string prefix)
    {
        var lower = prefix.ToLowerInvariant();
        int lo = LowerBound(lower);
        int hi = LowerBound(lower + char.MaxValue);
        for (int i = lo; i < hi; i++) yield return (_keys[i], _forms[i][0], _freq[i]);
    }

    /// <summary>Häufigste Wörter, die mit <paramref name="prefix"/> beginnen (inkl. des Präfix selbst).</summary>
    public IReadOnlyList<(string Word, int Freq)> Complete(string prefix)
    {
        var lower = prefix.ToLowerInvariant();
        if (lower.Length <= CachedPrefixLength && _prefixTop.TryGetValue(lower, out var cached)) return cached;
        return ScanTop(lower);
    }

    (string Word, int Freq)[] ScanTop(string lowerPrefix)
    {
        int lo = LowerBound(lowerPrefix);
        int hi = LowerBound(lowerPrefix + char.MaxValue);
        var best = new List<(string Word, int Freq)>(TopK + 1);

        for (int i = lo; i < hi; i++)
        {
            int f = _freq[i];
            if (best.Count == TopK && f <= best[^1].Freq) continue;

            int pos = best.FindIndex(x => x.Freq < f);
            if (pos < 0) pos = best.Count;
            best.Insert(pos, (_forms[i][0], f));
            if (best.Count > TopK) best.RemoveAt(TopK);
        }

        return best.ToArray();
    }

    /// <summary>
    /// Wörter mit gewichteter Editierdistanz ≤ <paramref name="maxDistance"/> zu <paramref name="input"/>.
    /// Damerau-Levenshtein (mit vertauschten Nachbarbuchstaben) über die sortierte Liste: Zeilen gemeinsamer
    /// Präfixe werden wiederverwendet und aussichtslose Präfixe komplett übersprungen (wie die Suche in einem Trie).
    /// </summary>
    public List<(string Word, int Freq, float Distance)> FindSimilar(
        string input, float maxDistance, Func<char, char, float> substitutionCost, float transpositionCost = 0.6f)
    {
        var target = input.ToLowerInvariant();
        int n = target.Length;
        int maxLen = n + (int)Math.Floor(maxDistance);
        List<(string, int, float)> results = [];
        if (_keys.Length == 0) return results;

        var rows = new float[maxLen + 1][];
        for (int d = 0; d <= maxLen; d++) rows[d] = new float[n + 1];
        for (int j = 0; j <= n; j++) rows[0][j] = j;

        string validPrefix = string.Empty; // rows[0..validPrefix.Length] gehören zu diesem Präfix
        int i = 0;
        while (i < _keys.Length)
        {
            var key = _keys[i];
            int common = CommonPrefixLength(validPrefix, key);
            int limit = Math.Min(key.Length, maxLen);
            int depth = common + 1;
            bool pruned = false;

            for (; depth <= limit; depth++)
            {
                var prev = rows[depth - 1];
                var row = rows[depth];
                char ch = key[depth - 1];
                row[0] = prev[0] + 1;
                float rowMin = row[0];
                for (int j = 1; j <= n; j++)
                {
                    float insert = row[j - 1] + 1;
                    float delete = prev[j] + 1;
                    float replace = prev[j - 1] + (target[j - 1] == ch ? 0 : substitutionCost(target[j - 1], ch));
                    float v = Math.Min(Math.Min(insert, delete), replace);
                    if (depth >= 2 && j >= 2 && target[j - 1] == key[depth - 2] && target[j - 2] == ch)
                    {
                        v = Math.Min(v, rows[depth - 2][j - 2] + transpositionCost); // "dsa" → "das"
                    }

                    row[j] = v;
                    if (v < rowMin) rowMin = v;
                }

                // Abbrechen nur, wenn weder diese Zeile noch (per Vertauschung) die vorherige weiterhelfen kann.
                if (rowMin > maxDistance && MinOf(prev) + transpositionCost > maxDistance)
                {
                    pruned = true;
                    break;
                }
            }

            if (pruned)
            {
                // Kein Wort mit diesem Präfix kann noch passen: alle überspringen.
                var deadPrefix = key[..depth];
                validPrefix = key[..(depth - 1)];
                i = LowerBound(deadPrefix + char.MaxValue);
                continue;
            }

            if (key.Length <= maxLen && rows[key.Length][n] <= maxDistance)
            {
                results.Add((_forms[i][0], _freq[i], rows[key.Length][n]));
            }

            validPrefix = key[..limit];
            i++;
        }

        return results;
    }

    int IndexOf(string lowerKey)
    {
        int i = LowerBound(lowerKey);
        return i < _keys.Length && string.Equals(_keys[i], lowerKey, StringComparison.Ordinal) ? i : -1;
    }

    int LowerBound(string value)
    {
        int lo = 0, hi = _keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(_keys[mid], value) < 0) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    static float MinOf(float[] row)
    {
        float min = row[0];
        for (int j = 1; j < row.Length; j++)
        {
            if (row[j] < min) min = row[j];
        }

        return min;
    }

    static int CommonPrefixLength(string a, string b)
    {
        int max = Math.Min(a.Length, b.Length);
        int k = 0;
        while (k < max && a[k] == b[k]) k++;
        return k;
    }
}
