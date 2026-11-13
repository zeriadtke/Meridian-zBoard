using System.Text;

namespace ZBoard.Suggestions;

/// <summary>
/// Was die Tastatur an der Schreibstelle über den Satz weiß: die beiden Wörter davor. Statt eines Worts kann
/// <see cref="NgramModel.SentenceStart"/> (Satzanfang) oder <see cref="NgramModel.Comma"/> stehen.
/// </summary>
/// <param name="Previous">Das Wort direkt davor, oder null (kein Kontext, etwa nach einer Klammer).</param>
/// <param name="BeforePrevious">Das Wort davor, oder null.</param>
public readonly record struct WordContext(string? Previous, string? BeforePrevious = null)
{
    public static readonly WordContext None = new(null);

    /// <summary>Das vorherige Wort, wenn es ein echtes Wort ist (kein Satzanfang, kein Komma).</summary>
    public string? PreviousWord => Previous is null or NgramModel.SentenceStart or NgramModel.Comma ? null : Previous;

    public bool IsSentenceStart => Previous == NgramModel.SentenceStart;

    /// <summary>
    /// Liest den Kontext aus dem Text vor dem Wort, das gerade geschrieben wird. Dieselbe Regel gilt beim Tippen und beim
    /// Erzeugen des Sprachmodells, damit beide dasselbe unter „vorherigem Wort“ verstehen:
    /// Wörter bestehen aus Buchstaben und Ziffern, innen auch Apostroph und Bindestrich; ein Komma ist ein eigenes
    /// Zeichen; nach Punkt, Ausrufe- und Fragezeichen und nach einem Zeilenumbruch beginnt ein Satz; jedes andere
    /// Zeichen beendet den Kontext. Zwischen Kontext und Schreibstelle muss Leerraum stehen.
    /// </summary>
    /// <param name="before">Text vor dem Wort (ohne das Wort selbst).</param>
    /// <param name="startsAtFieldStart">
    /// <paramref name="before"/> beginnt am Anfang des Eingabefelds (und ist kein abgeschnittenes Stück davon); nur dann
    /// zählt sein Anfang als Satzanfang.
    /// </param>
    public static WordContext FromText(string before, bool startsAtFieldStart = true)
    {
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1])) return None;

        string? first = null;
        int pos = before.Length;
        for (int found = 0; found < 2; found++)
        {
            string? token = null;
            bool sawNewline = false;
            while (pos > 0 && char.IsWhiteSpace(before[pos - 1]))
            {
                sawNewline |= before[pos - 1] is '\n' or '\r';
                pos--;
            }

            if (sawNewline || (pos == 0 && startsAtFieldStart))
            {
                token = NgramModel.SentenceStart;
            }
            else if (pos > 0)
            {
                int end = pos;
                // Schließende Anführungszeichen und Klammern hinter einem Satzende überspringen: „Ende.“ Dann …
                int q = pos;
                while (q > 0 && before[q - 1] is '"' or '“' or '”' or '‘' or '’' or '»' or '«' or ')' or ']') q--;
                if (q > 0 && before[q - 1] is '.' or '!' or '?' or '…')
                {
                    token = NgramModel.SentenceStart;
                }
                else if (before[pos - 1] == ',')
                {
                    token = NgramModel.Comma;
                    pos--;
                }
                else
                {
                    while (pos > 0 && (char.IsLetterOrDigit(before[pos - 1]) || before[pos - 1] is '\'' or '-')) pos--;
                    if (pos < end) token = before[pos..end];
                }
            }

            if (token == null) break;
            if (found == 0) first = token;
            else return new WordContext(first, token);
            if (token == NgramModel.SentenceStart) break; // vor dem Satzanfang gibt es nichts
        }

        return new WordContext(first);
    }
}

/// <summary>
/// Sprachmodell aus Wortfolgen: Wie wahrscheinlich ist ein Wort nach den ein oder zwei Wörtern davor? Grundlage der
/// Nächstes-Wort-Vorhersage und der Reihenfolge von Vorschlägen und Korrekturen. Gespeichert sind die häufigsten
/// Zweier- und Dreierfolgen mit ihrer Wahrscheinlichkeit; fehlt eine Dreierfolge, zählt die Zweierfolge mit Abschlag,
/// fehlt auch die, die Häufigkeit des Worts („Stupid Backoff“). Plattformunabhängig.
/// </summary>
/// <remarks>
/// Dateiformat (Little Endian): "ZBNG", Version (Byte), logMin und logStep (float; Wahrscheinlichkeit =
/// exp(logMin + q · logStep)), Zahl der Wörter und die Wörter (Länge als Byte, UTF-8; 0 = Satzanfang, 1 = Komma),
/// Häufigkeit je Wort (ushort q), Zweierfolgen (Startindex je erstem Wort, dann Folgewort und q), Dreierfolgen
/// (Schlüssel erstes · 65536 + zweites Wort, Folgewort und q; nach Schlüssel sortiert). Innerhalb eines Kontexts steht
/// das wahrscheinlichste Folgewort zuerst.
/// </remarks>
public sealed class NgramModel
{
    public const string SentenceStart = "<s>";
    public const string Comma = ",";

    /// <summary>Abschlag, wenn statt der Dreierfolge nur die Zweierfolge (oder nur das Wort) bekannt ist.</summary>
    public const double Backoff = 0.4;

    const byte FormatVersion = 1;
    const int MaxWords = ushort.MaxValue;
    const int SentenceStartId = 0, CommaId = 1;

    readonly string[] _words;
    readonly float[] _unigram;
    readonly int[] _bigramStart;
    readonly ushort[] _bigramNext;
    readonly float[] _bigramP;
    readonly uint[] _trigramKey;
    readonly ushort[] _trigramNext;
    readonly float[] _trigramP;
    readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    readonly Dictionary<string, int[]> _idsIgnoringCase = new(StringComparer.Ordinal);

    public static readonly NgramModel Empty = new Builder().Build();

    NgramModel(
        string[] words, float[] unigram, int[] bigramStart, ushort[] bigramNext, float[] bigramP,
        uint[] trigramKey, ushort[] trigramNext, float[] trigramP)
    {
        (_words, _unigram, _bigramStart, _bigramNext, _bigramP) = (words, unigram, bigramStart, bigramNext, bigramP);
        (_trigramKey, _trigramNext, _trigramP) = (trigramKey, trigramNext, trigramP);

        for (int i = 0; i < words.Length; i++) _ids[words[i]] = i;
        foreach (var group in Enumerable.Range(0, words.Length).GroupBy(i => words[i].ToLowerInvariant()))
        {
            _idsIgnoringCase[group.Key] = [.. group];
        }
    }

    public int WordCount => _words.Length;
    public int BigramCount => _bigramNext.Length;
    public int TrigramCount => _trigramNext.Length;

    /// <summary>Die wahrscheinlichsten nächsten Wörter, bestes zuerst, mit Wahrscheinlichkeit.</summary>
    public IReadOnlyList<(string Word, double Probability)> Predict(WordContext context, int max = 3)
    {
        int previous = IdOf(context.Previous);
        if (previous < 0 || max <= 0) return [];

        var result = new List<(string Word, double Probability)>(max * 2);
        var seen = new HashSet<int>();
        double scale = 1.0;

        var (from, to) = TrigramRange(IdOf(context.BeforePrevious), previous);
        if (from < to)
        {
            for (int i = from; i < to && i < from + max; i++)
            {
                seen.Add(_trigramNext[i]);
                result.Add((_words[_trigramNext[i]], _trigramP[i]));
            }

            scale = Backoff;
        }

        int end = _bigramStart[previous + 1];
        for (int i = _bigramStart[previous], taken = 0; i < end && taken < max; i++)
        {
            if (!seen.Add(_bigramNext[i])) continue;
            result.Add((_words[_bigramNext[i]], scale * _bigramP[i]));
            taken++;
        }

        result.Sort((a, b) => b.Probability.CompareTo(a.Probability));
        if (result.Count > max) result.RemoveRange(max, result.Count - max);
        return result;
    }

    /// <summary>
    /// Wie viel besser <paramref name="word"/> an diese Stelle passt als ein Wort, das das Modell hier nie gesehen hat:
    /// 1 für Unbekanntes und ohne Kontext, größer für typische Folgewörter („habe“ nach „ich“). Groß-/Kleinschreibung
    /// des Worts spielt keine Rolle („frage“ zählt für „Frage“ mit).
    /// </summary>
    public double ContextFactor(WordContext context, string word)
    {
        int previous = IdOf(context.Previous);
        if (previous < 0 || !_idsIgnoringCase.TryGetValue(word.ToLowerInvariant(), out var ids)) return 1.0;

        double unigram = 0;
        foreach (int id in ids) unigram += _unigram[id];
        if (unigram <= 0) return 1.0;

        var followers = FollowersOf(IdOf(context.BeforePrevious), previous);
        double best = 0;
        foreach (int id in ids) best += followers.GetValueOrDefault(id);
        return best <= 0 ? 1.0 : Math.Max(1.0, best / unigram);
    }

    /// <summary>
    /// Wahrscheinlichkeit genau dieser Schreibweise an dieser Stelle, mit Abschlag aus kürzeren Folgen und zuletzt der
    /// Häufigkeit des Worts; 0, wenn das Modell die Schreibweise oder den Kontext nicht kennt. Für die Wahl zwischen
    /// „frage“ und „Frage“.
    /// </summary>
    public double FormProbability(WordContext context, string form)
    {
        int previous = IdOf(context.Previous);
        if (previous < 0 || !_ids.TryGetValue(form, out int id)) return 0;

        var (from, to) = TrigramRange(IdOf(context.BeforePrevious), previous);
        for (int i = from; i < to; i++)
        {
            if (_trigramNext[i] == id) return _trigramP[i];
        }

        double scale = from < to ? Backoff : 1.0;
        double bigram = BigramP(previous, id);
        return bigram > 0 ? scale * bigram : scale * Backoff * _unigram[id];
    }

    /// <summary>Kennt das Modell das Wort als Kontext (in irgendeiner Schreibweise)?</summary>
    public bool Knows(string word) => IdOf(word) >= 0;

    /// <summary>
    /// Alle Folgewörter eines Kontexts mit P(Wort | Kontext), geteilt durch den Abschlag, den ein hier nie gesehenes Wort
    /// bekäme. Der zuletzt gefragte Kontext bleibt gemerkt: Während ein Wort getippt wird, ändert er sich nicht, und
    /// jeder Tastendruck bewertet viele Kandidaten.
    /// </summary>
    Dictionary<int, double> FollowersOf(int beforePrevious, int previous)
    {
        var cached = _lastFollowers;
        if (cached != null && cached.BeforePrevious == beforePrevious && cached.Previous == previous) return cached.Relative;

        var relative = new Dictionary<int, double>();
        int end = _bigramStart[previous + 1];
        for (int i = _bigramStart[previous]; i < end; i++) relative[_bigramNext[i]] = _bigramP[i] / Backoff;

        var (from, to) = TrigramRange(beforePrevious, previous);
        for (int i = from; i < to; i++) relative[_trigramNext[i]] = _trigramP[i] / (Backoff * Backoff);

        _lastFollowers = new FollowerCache(beforePrevious, previous, relative);
        return relative;
    }

    sealed record FollowerCache(int BeforePrevious, int Previous, Dictionary<int, double> Relative);

    volatile FollowerCache? _lastFollowers;

    double BigramP(int previous, int id)
    {
        int end = _bigramStart[previous + 1];
        for (int i = _bigramStart[previous]; i < end; i++)
        {
            if (_bigramNext[i] == id) return _bigramP[i];
        }

        return 0;
    }

    (int From, int To) TrigramRange(int beforePrevious, int previous)
    {
        if (beforePrevious < 0 || previous < 0 || _trigramKey.Length == 0) return (0, 0);
        uint key = ((uint)beforePrevious << 16) | (uint)previous;
        int lo = 0, hi = _trigramKey.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_trigramKey[mid] < key) lo = mid + 1;
            else hi = mid;
        }

        int end = lo;
        while (end < _trigramKey.Length && _trigramKey[end] == key) end++;
        return (lo, end);
    }

    /// <summary>Nummer eines Kontextworts: genaue Schreibweise, sonst die häufigste mit anderer Großschreibung.</summary>
    int IdOf(string? word)
    {
        if (word == null) return -1;
        if (_ids.TryGetValue(word, out int id)) return id;
        if (!_idsIgnoringCase.TryGetValue(word.ToLowerInvariant(), out var ids)) return -1;

        int best = ids[0];
        foreach (int candidate in ids)
        {
            if (_unigram[candidate] > _unigram[best]) best = candidate;
        }

        return best;
    }

    // ---------- Laden und Speichern ----------

    public static NgramModel Load(Stream stream)
    {
        // Erst ganz in den Speicher: Das Modell wird in vielen kleinen Schritten gelesen, und die Asset-Streams von
        // Android sind dafür zu langsam.
        if (stream is not MemoryStream)
        {
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            stream = buffer;
        }

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadByte() != 'Z' || reader.ReadByte() != 'B' || reader.ReadByte() != 'N' || reader.ReadByte() != 'G')
        {
            throw new InvalidDataException("Kein zBoard-Sprachmodell.");
        }

        byte version = reader.ReadByte();
        if (version != FormatVersion) throw new InvalidDataException($"Unbekannte Version des Sprachmodells: {version}.");

        float logMin = reader.ReadSingle(), logStep = reader.ReadSingle();
        float Probability() => MathF.Exp(logMin + reader.ReadUInt16() * logStep);

        var words = new string[reader.ReadInt32()];
        for (int i = 0; i < words.Length; i++) words[i] = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte()));
        var unigram = new float[words.Length];
        for (int i = 0; i < unigram.Length; i++) unigram[i] = Probability();

        int bigrams = reader.ReadInt32();
        var bigramStart = new int[words.Length + 1];
        for (int i = 0; i < bigramStart.Length; i++) bigramStart[i] = reader.ReadInt32();
        var bigramNext = new ushort[bigrams];
        var bigramP = new float[bigrams];
        for (int i = 0; i < bigrams; i++) bigramNext[i] = reader.ReadUInt16();
        for (int i = 0; i < bigrams; i++) bigramP[i] = Probability();

        int trigrams = reader.ReadInt32();
        var trigramKey = new uint[trigrams];
        var trigramNext = new ushort[trigrams];
        var trigramP = new float[trigrams];
        for (int i = 0; i < trigrams; i++) trigramKey[i] = reader.ReadUInt32();
        for (int i = 0; i < trigrams; i++) trigramNext[i] = reader.ReadUInt16();
        for (int i = 0; i < trigrams; i++) trigramP[i] = Probability();

        return new NgramModel(words, unigram, bigramStart, bigramNext, bigramP, trigramKey, trigramNext, trigramP);
    }

    public void Save(Stream stream)
    {
        // Wahrscheinlichkeiten logarithmisch auf 16 Bit: Abstufung feiner als 0,1 %.
        float min = float.MaxValue;
        foreach (var values in new[] { _unigram, _bigramP, _trigramP })
        {
            foreach (float p in values)
            {
                if (p > 0 && p < min) min = p;
            }
        }

        float logMin = min == float.MaxValue ? 0f : MathF.Log(min);
        float logStep = -logMin / ushort.MaxValue;
        ushort Quantize(float p) =>
            p <= 0 || logStep == 0 ? (ushort)0 : (ushort)Math.Clamp(MathF.Round((MathF.Log(p) - logMin) / logStep), 0, ushort.MaxValue);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("ZBNG"u8);
        writer.Write(FormatVersion);
        writer.Write(logMin);
        writer.Write(logStep);

        writer.Write(_words.Length);
        foreach (var word in _words)
        {
            var bytes = Encoding.UTF8.GetBytes(word);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        foreach (float p in _unigram) writer.Write(Quantize(p));

        writer.Write(_bigramNext.Length);
        foreach (int start in _bigramStart) writer.Write(start);
        foreach (ushort next in _bigramNext) writer.Write(next);
        foreach (float p in _bigramP) writer.Write(Quantize(p));

        writer.Write(_trigramNext.Length);
        foreach (uint key in _trigramKey) writer.Write(key);
        foreach (ushort next in _trigramNext) writer.Write(next);
        foreach (float p in _trigramP) writer.Write(Quantize(p));
    }

    // ---------- Aufbauen ----------

    /// <summary>
    /// Sammelt gezählte Wortfolgen und macht daraus ein Modell. Wörter sind in der Schreibweise des Wörterbuchs
    /// anzugeben; <see cref="SentenceStart"/> und <see cref="Comma"/> dürfen nur als Kontext vorkommen.
    /// </summary>
    public sealed class Builder
    {
        readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        readonly List<string> _names = [];
        readonly Dictionary<(int, int), double> _bigrams = [];
        readonly Dictionary<(int, int, int), double> _trigrams = [];

        /// <summary>Zählt die Folge <paramref name="previous"/> → <paramref name="word"/>.</summary>
        public Builder Add(string previous, string word, double count = 1)
        {
            var key = (Id(previous), Id(word));
            _bigrams[key] = _bigrams.GetValueOrDefault(key) + count;
            return this;
        }

        /// <summary>Zählt die Dreierfolge. Die enthaltene Zweierfolge muss zusätzlich mit <see cref="Add(string, string, double)"/> gezählt werden.</summary>
        public Builder Add(string beforePrevious, string previous, string word, double count = 1)
        {
            var key = (Id(beforePrevious), Id(previous), Id(word));
            _trigrams[key] = _trigrams.GetValueOrDefault(key) + count;
            return this;
        }

        int Id(string word)
        {
            if (_ids.TryGetValue(word, out int id)) return id;
            _ids[word] = id = _names.Count;
            _names.Add(word);
            return id;
        }

        /// <summary>Behält die häufigsten Folgen (0 = alle) und rechnet die Zählungen in Wahrscheinlichkeiten um.</summary>
        public NgramModel Build(int maxBigrams = 0, int maxTrigrams = 0)
        {
            // Wahrscheinlichkeiten beziehen sich auf alle gezählten Folgen, auch die, die danach wegfallen.
            var bigramTotal = new double[_names.Count];
            var unigramCount = new double[_names.Count];
            double total = 0;
            foreach (var ((previous, word), count) in _bigrams)
            {
                bigramTotal[previous] += count;
                unigramCount[word] += count;
                total += count;
            }

            var trigramTotal = new Dictionary<(int, int), double>();
            foreach (var ((beforePrevious, previous, _), count) in _trigrams)
            {
                trigramTotal[(beforePrevious, previous)] = trigramTotal.GetValueOrDefault((beforePrevious, previous)) + count;
            }

            bool Predictable(int id) => _names[id] is not (SentenceStart or Comma);
            int Compare(int a, int b) => string.CompareOrdinal(_names[a], _names[b]);

            var keptBigrams = Top(
                _bigrams.Where(b => Predictable(b.Key.Item2)), maxBigrams,
                (x, y) => Compare(x.Item1, y.Item1) is var c and not 0 ? c : Compare(x.Item2, y.Item2));
            var keptTrigrams = Top(
                _trigrams.Where(t => Predictable(t.Key.Item3)), maxTrigrams,
                (x, y) => Compare(x.Item1, y.Item1) is var c1 and not 0 ? c1
                    : Compare(x.Item2, y.Item2) is var c2 and not 0 ? c2 : Compare(x.Item3, y.Item3));

            var vocabulary = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var ((a, b), _) in keptBigrams) { vocabulary.Add(_names[a]); vocabulary.Add(_names[b]); }
            foreach (var ((a, b, c), _) in keptTrigrams) { vocabulary.Add(_names[a]); vocabulary.Add(_names[b]); vocabulary.Add(_names[c]); }
            vocabulary.Remove(SentenceStart);
            vocabulary.Remove(Comma);
            if (vocabulary.Count + 2 > MaxWords)
            {
                throw new InvalidOperationException($"Zu viele verschiedene Wörter ({vocabulary.Count}); weniger Wortfolgen behalten.");
            }

            string[] words = [SentenceStart, Comma, .. vocabulary];
            var finalId = new int[_names.Count];
            Array.Fill(finalId, -1);
            for (int i = 0; i < words.Length; i++)
            {
                if (_ids.TryGetValue(words[i], out int old)) finalId[old] = i;
            }

            var unigram = new float[words.Length];
            for (int i = 0; i < words.Length; i++)
            {
                unigram[i] = total > 0 && _ids.TryGetValue(words[i], out int old) ? (float)(unigramCount[old] / total) : 0f;
            }

            var bigrams = keptBigrams
                .Select(b => (Previous: finalId[b.Key.Item1], Next: finalId[b.Key.Item2], P: b.Value / bigramTotal[b.Key.Item1]))
                .OrderBy(b => b.Previous).ThenByDescending(b => b.P).ThenBy(b => b.Next).ToList();
            var bigramStart = new int[words.Length + 1];
            foreach (var b in bigrams) bigramStart[b.Previous + 1]++;
            for (int i = 0; i < words.Length; i++) bigramStart[i + 1] += bigramStart[i];

            var trigrams = keptTrigrams
                .Select(t => (Key: ((uint)finalId[t.Key.Item1] << 16) | (uint)finalId[t.Key.Item2], Next: finalId[t.Key.Item3],
                    P: t.Value / trigramTotal[(t.Key.Item1, t.Key.Item2)]))
                .OrderBy(t => t.Key).ThenByDescending(t => t.P).ThenBy(t => t.Next).ToList();

            return new NgramModel(
                words, unigram, bigramStart,
                [.. bigrams.Select(b => (ushort)b.Next)], [.. bigrams.Select(b => (float)b.P)],
                [.. trigrams.Select(t => t.Key)], [.. trigrams.Select(t => (ushort)t.Next)], [.. trigrams.Select(t => (float)t.P)]);
        }

        /// <summary>Die <paramref name="max"/> häufigsten Einträge (0 = alle); bei gleicher Zählung entscheidet die Wortfolge.</summary>
        static List<KeyValuePair<TKey, double>> Top<TKey>(
            IEnumerable<KeyValuePair<TKey, double>> entries, int max, Comparison<TKey> tieBreak)
        {
            var all = entries.ToList();
            if (max <= 0 || all.Count <= max) return all;

            var counts = all.Select(e => e.Value).ToArray();
            Array.Sort(counts);
            double threshold = counts[^max];

            var kept = all.Where(e => e.Value > threshold).ToList();
            var ties = all.Where(e => e.Value == threshold).ToList();
            ties.Sort((x, y) => tieBreak(x.Key, y.Key));
            kept.AddRange(ties.Take(max - kept.Count));
            return kept;
        }
    }
}
