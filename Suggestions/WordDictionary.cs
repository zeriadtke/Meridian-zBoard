using System.Runtime.InteropServices;
using System.Text;

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

    // Aufbau (bis BuildTopLists): je Schlüssel ein Platz. Fast jeder Schlüssel hat genau eine Schreibweise; nur für
    // die übrigen gibt es eine Liste. Das spart beim Laden von 100 000+ Wörtern ebenso viele kleine Listen.
    Dictionary<string, int>? _slots = new(StringComparer.Ordinal);
    List<string> _slotKeys = [];
    List<(string Word, int Freq)> _slotFirst = [];
    Dictionary<int, List<(string Word, int Freq)>> _slotForms = [];

    string[] _keys = [];      // kleingeschrieben, ordinal sortiert, eindeutig
    int[] _freq = [];         // höchste Häufigkeit je Schlüssel
    string[] _top = [];       // häufigste Schreibweise je Schlüssel
    string[]?[] _forms = [];  // alle Schreibweisen, häufigste zuerst; null, wenn es nur eine gibt
    byte[] _shared = [];      // gemeinsame Anfangslänge mit dem nächsten Schlüssel (höchstens 255; letzter: 0)
    readonly Dictionary<string, (string Word, int Freq)[]> _prefixTop = new(StringComparer.Ordinal);

    public int Count => _keys.Length;

    /// <summary>
    /// Sprache der Wortliste, soweit sie die Groß- und Kleinschreibung bestimmt: Im Türkischen gehören i/İ und ı/I
    /// zusammen. null: Regeln wie in den meisten Sprachen.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>Kleinschreibung nach den Regeln der Sprache dieses Wörterbuchs (Schlüssel der Einträge).</summary>
    public string Lower(string text) => TextCase.Lower(text, Language);

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
    /// <param name="language">Sprache der Liste (siehe <see cref="Language"/>).</param>
    public static WordDictionary Load(Stream stream, long? normalizeTo = null, string? language = null)
    {
        var dict = new WordDictionary { Language = language };

        // Die ganze Datei auf einmal lesen und umwandeln: Zeile für Zeile über einen StreamReader entsteht je Zeile
        // eine Zeichenkette, die sofort wieder weggeworfen wird.
        var bytes = ReadAll(stream);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        ReadOnlySpan<char> text = chars.AsSpan(0, Encoding.UTF8.GetChars(bytes, chars));
        dict.Reserve(text.Length / 12);

        while (text.Length > 0)
        {
            int end = text.IndexOfAny('\n', '\r');
            var line = end < 0 ? text : text[..end];
            text = end < 0 ? default : text[(end + 1)..];
            if (line.Length == 0 || line[0] == '#') continue;

            int tab = line.IndexOf('\t');
            if (tab < 0) tab = line.IndexOf(' ');
            var word = (tab < 0 ? line : line[..tab]).Trim();
            int freq = tab >= 0 && int.TryParse(line[(tab + 1)..].Trim(), out var f) ? f : 1;
            if (word.Length > 0) dict.Add(new string(word), freq);
        }

        dict.BuildTopLists(normalizeTo);
        return dict;
    }

    /// <summary>Liest den Rest des Streams; ohne Kopie, wenn er schon ganz im Speicher liegt.</summary>
    static ReadOnlySpan<byte> ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment))
        {
            return segment.AsSpan((int)memory.Position);
        }

        var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Max(0, stream.Length - stream.Position) : 2 << 20);
        stream.CopyTo(buffer);
        return buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
    }

    void Reserve(int words)
    {
        _slots?.EnsureCapacity(words);
        _slotKeys.EnsureCapacity(words);
        _slotFirst.EnsureCapacity(words);
    }

    /// <summary>Fügt eine Schreibweise hinzu. Muss vor <see cref="BuildTopLists"/> aufgerufen werden.</summary>
    public void Add(string word, int freq)
    {
        if (_slots == null) throw new InvalidOperationException("Wörterbuch ist bereits fertig aufgebaut.");

        var key = Lower(word);
        ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, key, out bool known);
        if (!known)
        {
            slot = _slotKeys.Count;
            _slotKeys.Add(key);
            _slotFirst.Add((word, freq));
            return;
        }

        if (_slotForms.TryGetValue(slot, out var forms))
        {
            int existing = forms.FindIndex(x => x.Word == word);
            if (existing < 0) forms.Add((word, freq));
            else if (freq > forms[existing].Freq) forms[existing] = (word, freq);
            return;
        }

        var first = _slotFirst[slot];
        if (first.Word != word) _slotForms[slot] = [first, (word, freq)];
        else if (freq > first.Freq) _slotFirst[slot] = (word, freq);
    }

    /// <summary>Schließt den Aufbau ab: sortiert und berechnet die Vorschlagslisten für kurze Präfixe.</summary>
    /// <param name="normalizeTo">Häufigkeiten so skalieren, dass ihre Summe diesem Wert entspricht; null lässt sie unverändert.</param>
    public void BuildTopLists(long? normalizeTo = null)
    {
        if (_slots == null) return;

        int count = _slotKeys.Count;
        var first = CollectionsMarshal.AsSpan(_slotFirst);

        long Total(Span<(string Word, int Freq)> single)
        {
            long sum = 0;
            for (int slot = 0; slot < single.Length; slot++)
            {
                if (!_slotForms.ContainsKey(slot)) sum += single[slot].Freq;
            }

            foreach (var forms in _slotForms.Values)
            {
                foreach (var form in forms) sum += form.Freq;
            }

            return sum;
        }

        long total = Total(first);
        if (normalizeTo is > 0 && total > 0)
        {
            double factor = (double)normalizeTo.Value / total;
            int Scale(int freq) => (int)Math.Clamp(Math.Round(freq * factor), 1, int.MaxValue);

            for (int slot = 0; slot < count; slot++) first[slot].Freq = Scale(first[slot].Freq);
            foreach (var forms in _slotForms.Values)
            {
                for (int k = 0; k < forms.Count; k++) forms[k] = (forms[k].Word, Scale(forms[k].Freq));
            }

            total = Total(first);
        }

        TotalFrequency = total;

        var keys = _slotKeys.ToArray();
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = i;
        keys.AsSpan().Sort(slots.AsSpan(), new OrdinalComparer());

        _keys = keys;
        _freq = new int[count];
        _top = new string[count];
        _forms = new string[count][];
        for (int i = 0; i < count; i++)
        {
            int slot = slots[i];
            if (!_slotForms.TryGetValue(slot, out var forms))
            {
                (_top[i], _freq[i]) = first[slot];
                continue;
            }

            // Häufigste zuerst; bei gleicher Häufigkeit bleibt die Reihenfolge der Wortliste.
            for (int a = 1; a < forms.Count; a++)
            {
                var moved = forms[a];
                int b = a;
                for (; b > 0 && forms[b - 1].Freq < moved.Freq; b--) forms[b] = forms[b - 1];
                forms[b] = moved;
            }

            var words = new string[forms.Count];
            for (int k = 0; k < words.Length; k++) words[k] = forms[k].Word;
            _forms[i] = words;
            _top[i] = words[0];
            _freq[i] = forms[0].Freq;
        }

        (_slots, _slotKeys, _slotFirst, _slotForms) = (null, [], [], []);
        BuildPrefixTop();
    }

    readonly struct OrdinalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
    }

    /// <summary>
    /// Für ein- und zweibuchstabige Präfixe sind die Bereiche groß: einmal vorberechnen. Wörter mit demselben Anfang
    /// stehen in der sortierten Liste hintereinander, ein Durchgang genügt.
    /// </summary>
    void BuildPrefixTop()
    {
        // Für die Suche nach ähnlichen Wörtern: wie viele Zeichen jeder Schlüssel mit seinem Nachfolger teilt.
        _shared = new byte[_keys.Length];
        for (int i = 0; i + 1 < _keys.Length; i++)
        {
            _shared[i] = (byte)Math.Min(byte.MaxValue, CommonPrefixLength(_keys[i], _keys[i].Length, _keys[i + 1]));
        }

        _prefixTop.Clear();
        for (int length = 1; length <= CachedPrefixLength; length++)
        {
            int i = 0;
            while (i < _keys.Length)
            {
                var key = _keys[i];
                if (key.Length < length)
                {
                    i++;
                    continue;
                }

                var prefix = key.AsSpan(0, length);
                int end = i + 1;
                while (end < _keys.Length && _keys[end].AsSpan().StartsWith(prefix, StringComparison.Ordinal)) end++;
                _prefixTop[key[..length]] = ScanTop(i, end);
                i = end;
            }
        }
    }

    // ---------- Vorbereitete Fassung (Zwischenspeicher auf dem Gerät) ----------

    const uint CompiledMagic = 0x4457425A; // "ZBWD"
    const int CompiledVersion = 1;

    /// <summary>
    /// Schreibt das fertig aufgebaute Wörterbuch so, dass <see cref="LoadCompiled"/> es ohne Auswerten, Normieren und
    /// Sortieren wieder einlesen kann. Die Tastatur legt diese Fassung nach dem ersten Laden einer Wortliste ab; jeder
    /// weitere Start lädt daraus um ein Vielfaches schneller.
    /// </summary>
    /// <remarks>
    /// Format (Little Endian): "ZBWD", Version, Summe der Häufigkeiten (long), Zahl der Schlüssel, Zahl der weiteren
    /// Schreibweisen, Zahl der Zeichen, Länge des UTF-8-Texts; dann je Schlüssel Häufigkeit (int), Länge (ushort) und
    /// Zahl der Schreibweisen (ushort; 0 = nur der Schlüssel selbst), die Längen der Schreibweisen (ushort) und der
    /// Text: alle Schlüssel, danach alle Schreibweisen, hintereinander als UTF-8.
    /// </remarks>
    public void Save(Stream stream)
    {
        if (_slots != null) throw new InvalidOperationException("Wörterbuch ist noch nicht fertig aufgebaut.");

        int count = _keys.Length;
        var formCounts = new ushort[count];
        var keyLengths = new ushort[count];
        List<ushort> formLengths = [];
        var text = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            keyLengths[i] = checked((ushort)_keys[i].Length);
            text.Append(_keys[i]);
        }

        for (int i = 0; i < count; i++)
        {
            // Dieselbe Zeichenkette wie der Schlüssel (kleingeschriebenes Wort ohne zweite Form): nichts ablegen.
            if (_forms[i] == null && _top[i] == _keys[i]) continue;
            var forms = _forms[i] ?? [_top[i]];
            formCounts[i] = checked((ushort)forms.Length);
            foreach (var form in forms)
            {
                formLengths.Add(checked((ushort)form.Length));
                text.Append(form);
            }
        }

        var utf8 = Encoding.UTF8.GetBytes(text.ToString());
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(CompiledMagic);
        writer.Write(CompiledVersion);
        writer.Write(TotalFrequency);
        writer.Write(count);
        writer.Write(formLengths.Count);
        writer.Write(text.Length);
        writer.Write(utf8.Length);
        writer.Write(MemoryMarshal.AsBytes(_freq.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(keyLengths.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(formCounts.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(formLengths)));
        writer.Write(utf8);
    }

    /// <summary>Liest eine mit <see cref="Save"/> geschriebene Fassung.</summary>
    /// <exception cref="InvalidDataException">Die Daten sind keine (vollständige) Fassung dieser Version.</exception>
    public static WordDictionary LoadCompiled(Stream stream, string? language = null)
    {
        if (!BitConverter.IsLittleEndian) throw new InvalidDataException("Nur für Little-Endian-Geräte.");

        var data = ReadAll(stream);
        const int Header = 32;
        if (data.Length < Header || MemoryMarshal.Read<uint>(data) != CompiledMagic
            || MemoryMarshal.Read<int>(data[4..]) != CompiledVersion)
        {
            throw new InvalidDataException("Kein vorbereitetes zBoard-Wörterbuch dieser Version.");
        }

        long total = MemoryMarshal.Read<long>(data[8..]);
        int count = MemoryMarshal.Read<int>(data[16..]);
        int formCount = MemoryMarshal.Read<int>(data[20..]);
        int charCount = MemoryMarshal.Read<int>(data[24..]);
        int utf8Length = MemoryMarshal.Read<int>(data[28..]);
        long expected = Header + 8L * count + 2L * formCount + utf8Length;
        if (count < 0 || formCount < 0 || charCount < 0 || utf8Length < 0 || data.Length != expected)
        {
            throw new InvalidDataException("Vorbereitetes Wörterbuch ist unvollständig.");
        }

        data = data[Header..];
        var freq = new int[count];
        var keyLengths = new ushort[count];
        var formCounts = new ushort[count];
        var formLengths = new ushort[formCount];
        data = Take(data, MemoryMarshal.AsBytes(freq.AsSpan()));
        data = Take(data, MemoryMarshal.AsBytes(keyLengths.AsSpan()));
        data = Take(data, MemoryMarshal.AsBytes(formCounts.AsSpan()));
        data = Take(data, MemoryMarshal.AsBytes(formLengths.AsSpan()));

        var chars = new char[charCount];
        if (Encoding.UTF8.GetChars(data, chars) != charCount) throw new InvalidDataException("Vorbereitetes Wörterbuch ist beschädigt.");
        ReadOnlySpan<char> text = chars;

        var keys = new string[count];
        var top = new string[count];
        var forms = new string[count][];
        try
        {
            int pos = 0;
            for (int i = 0; i < count; i++)
            {
                keys[i] = new string(text.Slice(pos, keyLengths[i]));
                pos += keyLengths[i];
            }

            int next = 0;
            for (int i = 0; i < count; i++)
            {
                int n = formCounts[i];
                if (n == 0)
                {
                    top[i] = keys[i];
                    continue;
                }

                var words = new string[n];
                for (int k = 0; k < n; k++)
                {
                    int length = formLengths[next++];
                    words[k] = new string(text.Slice(pos, length));
                    pos += length;
                }

                top[i] = words[0];
                if (n > 1) forms[i] = words;
            }

            if (pos != charCount || next != formCount) throw new InvalidDataException("Vorbereitetes Wörterbuch ist beschädigt.");
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Vorbereitetes Wörterbuch ist beschädigt.", e);
        }

        var dict = new WordDictionary { Language = language };
        (dict._slots, dict._keys, dict._freq, dict._top, dict._forms) = (null, keys, freq, top, forms);
        dict.TotalFrequency = total;
        dict.BuildPrefixTop();
        return dict;

        static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> source, Span<byte> target)
        {
            source[..target.Length].CopyTo(target);
            return source[target.Length..];
        }
    }

    /// <summary>Häufigste Schreibweise und Häufigkeit eines Wortes (Groß-/Kleinschreibung egal).</summary>
    public (string Word, int Freq)? Lookup(string word)
    {
        int i = IndexOf(Lower(word));
        return i < 0 ? null : (_top[i], _freq[i]);
    }

    public bool Contains(string word) => IndexOf(Lower(word)) >= 0;

    /// <summary>Alle Schreibweisen eines Wortes, häufigste zuerst; leer, wenn unbekannt.</summary>
    public IReadOnlyList<string> Forms(string word)
    {
        int i = IndexOf(Lower(word));
        return i < 0 ? [] : _forms[i] ?? [_top[i]];
    }

    /// <summary>Alle Einträge mit diesem Präfix: Schlüssel (klein), häufigste Schreibweise, Häufigkeit.</summary>
    public IEnumerable<(string Key, string Form, int Freq)> WithPrefix(string prefix)
    {
        var lower = Lower(prefix);
        int lo = LowerBound(lower);
        int hi = LowerBound(lower + char.MaxValue);
        for (int i = lo; i < hi; i++) yield return (_keys[i], _top[i], _freq[i]);
    }

    /// <summary>Häufigste Wörter, die mit <paramref name="prefix"/> beginnen (inkl. des Präfix selbst).</summary>
    public IReadOnlyList<(string Word, int Freq)> Complete(string prefix)
    {
        var lower = Lower(prefix);
        if (lower.Length <= CachedPrefixLength && _prefixTop.TryGetValue(lower, out var cached)) return cached;
        return ScanTop(LowerBound(lower), LowerBound(lower + char.MaxValue));
    }

    /// <summary>Die häufigsten Wörter im Bereich [<paramref name="lo"/>, <paramref name="hi"/>) der sortierten Liste.</summary>
    (string Word, int Freq)[] ScanTop(int lo, int hi)
    {
        var best = new List<(string Word, int Freq)>(TopK + 1);

        for (int i = lo; i < hi; i++)
        {
            int f = _freq[i];
            if (best.Count == TopK && f <= best[^1].Freq) continue;

            int pos = best.FindIndex(x => x.Freq < f);
            if (pos < 0) pos = best.Count;
            best.Insert(pos, (_top[i], f));
            if (best.Count > TopK) best.RemoveAt(TopK);
        }

        return best.ToArray();
    }

    /// <summary>Ein weggelassener Apostroph ("dont" für "don't") kostet so wenig wie ein weggelassener Umlaut.</summary>
    public const float MissingApostropheCost = 0.3f;

    /// <summary>
    /// Wörter mit gewichteter Editierdistanz ≤ <paramref name="maxDistance"/> zu <paramref name="input"/>.
    /// Damerau-Levenshtein (mit vertauschten Nachbarbuchstaben) über die sortierte Liste: Zeilen gemeinsamer
    /// Präfixe werden wiederverwendet und aussichtslose Präfixe komplett übersprungen (wie die Suche in einem Trie).
    /// </summary>
    public List<(string Word, int Freq, float Distance)> FindSimilar(
        string input, float maxDistance, Func<char, char, float> substitutionCost, float transpositionCost = 0.6f) =>
        FindSimilar(input, maxDistance, (_, typed, candidate) => substitutionCost(typed, candidate), transpositionCost);

    /// <summary>
    /// Wie <see cref="FindSimilar(string, float, Func{char, char, float}, float)"/>; die Ersetzungskosten hängen
    /// zusätzlich von der Stelle im getippten Wort ab (Position, getippter Buchstabe, Buchstabe des Kandidaten).
    /// </summary>
    /// <remarks>
    /// Läuft bei jedem Tastendruck über das ganze Wörterbuch und bestimmt die Rechenzeit je Taste. Deshalb:
    /// <list type="bullet">
    /// <item>Die Ersetzungskosten werden je Buchstabe des Wörterbuchs einmal für alle Stellen der Eingabe berechnet
    /// und danach nur noch nachgeschlagen. <paramref name="substitutionCost"/> muss dafür bei gleichen Argumenten
    /// immer dasselbe liefern.</item>
    /// <item>Von jeder Zeile der Tabelle wird nur der Streifen um die Diagonale berechnet, in dem die Distanz
    /// überhaupt noch ≤ <paramref name="maxDistance"/> sein kann: Jeder überzählige Buchstabe der Eingabe kostet 1,
    /// jeder fehlende ebenfalls (außer einem Apostroph).</item>
    /// </list>
    /// </remarks>
    public List<(string Word, int Freq, float Distance)> FindSimilar(
        string input, float maxDistance, Func<int, char, char, float> substitutionCost, float transpositionCost = 0.6f)
    {
        var target = Lower(input);
        int n = target.Length;
        // Länger als die Eingabe darf ein Wort um die erlaubten Einfügungen sein – und um einen Apostroph, wenn der
        // Rest der Distanz dafür reicht ("im" → "I'm" bei maxDistance 0,3).
        float whole = MathF.Floor(maxDistance);
        int maxLen = n + (int)whole + (maxDistance - whole >= MissingApostropheCost ? 1 : 0);
        List<(string, int, float)> results = [];
        if (_keys.Length == 0) return results;

        int band = (int)whole;
        var rows = new float[maxLen + 1][];
        for (int d = 0; d <= maxLen; d++) rows[d] = new float[n + 1];
        for (int j = 0; j <= n; j++) rows[0][j] = j;
        var rowMin = new float[maxLen + 1];   // kleinster Wert je Zeile (Zeile 0: 0)
        var apostrophes = new int[maxLen + 1]; // Apostrophe in den ersten d Buchstaben des Worts
        // Ersetzungskosten je Buchstabe des Wörterbuchs: für die üblichen Alphabete (lateinisch, griechisch,
        // kyrillisch) direkt über den Zeichencode, sonst über eine Tabelle.
        const int DirectChars = 0x530;
        var directCosts = new float[DirectChars][];
        Dictionary<char, float[]>? otherCosts = null;

        // _shared zählt nur bis 255: Bei (nie getippten) längeren Eingaben wird der gemeinsame Anfang verglichen.
        bool useShared = maxLen < byte.MaxValue;
        ReadOnlySpan<byte> shared = _shared;

        int common = 0; // rows[0..common] gelten schon für den Schlüssel an der Stelle i
        int i = 0;
        while (i < _keys.Length)
        {
            var key = _keys[i];
            int limit = Math.Min(key.Length, maxLen);
            int depth = common + 1;
            bool pruned = false;

            for (; depth <= limit; depth++)
            {
                var prev = rows[depth - 1];
                var row = rows[depth];
                char ch = key[depth - 1];
                bool apostrophe = ch == '\'';
                float deletion = apostrophe ? MissingApostropheCost : 1; // Buchstabe des Worts fehlt in der Eingabe
                int apostropheCount = apostrophes[depth - 1] + (apostrophe ? 1 : 0);
                apostrophes[depth] = apostropheCount;

                float[]? cost = ch < DirectChars ? directCosts[ch] : (otherCosts ??= []).GetValueOrDefault(ch);
                if (cost == null)
                {
                    cost = new float[n];
                    for (int j = 0; j < n; j++) cost[j] = target[j] == ch ? 0 : substitutionCost(j, target[j], ch);
                    if (ch < DirectChars) directCosts[ch] = cost;
                    else otherCosts![ch] = cost;
                }

                // Außerhalb des Streifens liegt die Distanz sicher über maxDistance: An seinen Rändern steht
                // "unendlich" – so weit, wie die nächsten beiden Zeilen (Vertauschung) darüber hinaus lesen.
                int lo = Math.Max(0, depth - band - apostropheCount), hi = Math.Min(n, depth + band);
                float min = float.PositiveInfinity;
                if (hi < n) row[hi + 1] = float.PositiveInfinity;
                if (lo == 0)
                {
                    min = row[0] = prev[0] + deletion;
                    lo = 1;
                }
                else
                {
                    row[lo - 1] = float.PositiveInfinity;
                    if (lo >= 2) row[lo - 2] = float.PositiveInfinity;
                }

                // Vertauschte Nachbarbuchstaben ("dsa" → "das"): nur möglich, wo die Eingabe diesen und den vorigen
                // Buchstaben des Worts in umgekehrter Reihenfolge enthält.
                bool swappable = depth >= 2;
                char before = swappable ? key[depth - 2] : '\0';

                float left = row[lo - 1];
                for (int j = lo; j <= hi; j++)
                {
                    float v = left + 1;               // Buchstabe der Eingabe ist zu viel
                    float delete = prev[j] + deletion;
                    if (delete < v) v = delete;
                    float replace = prev[j - 1] + cost[j - 1];
                    if (replace < v) v = replace;
                    if (swappable && j >= 2 && target[j - 1] == before && target[j - 2] == ch)
                    {
                        float swap = rows[depth - 2][j - 2] + transpositionCost;
                        if (swap < v) v = swap;
                    }

                    row[j] = left = v;
                    if (v < min) min = v;
                }

                rowMin[depth] = min;

                // Abbrechen nur, wenn weder diese Zeile noch (per Vertauschung) die vorherige weiterhelfen kann.
                if (min > maxDistance && rowMin[depth - 1] + transpositionCost > maxDistance)
                {
                    pruned = true;
                    break;
                }
            }

            if (pruned)
            {
                // Kein Wort mit diesem Präfix kann noch passen: alle überspringen.
                Skip(ref i, ref common, depth, shared);
                continue;
            }

            if (key.Length > maxLen)
            {
                // Zu lang, wie alle weiteren Wörter mit diesem Anfang.
                Skip(ref i, ref common, maxLen, shared);
                continue;
            }

            // Das Ergebnis steht am Ende der letzten Zeile – sofern der Streifen bis dorthin reicht; sonst ist das
            // Wort zu kurz oder zu lang, und in der Zelle steht kein berechneter Wert.
            int length = key.Length;
            bool reachesEnd = length + band >= n && length - band - apostrophes[length] <= n;
            if (reachesEnd && rows[length][n] <= maxDistance) results.Add((_top[i], _freq[i], rows[length][n]));
            i++;
            if (i < _keys.Length) common = useShared ? shared[i - 1] : CommonPrefixLength(key, length, _keys[i]);
        }

        return results;

        // Weiter zum ersten Schlüssel, der nicht mit den ersten prefixLength Zeichen des jetzigen beginnt.
        void Skip(ref int index, ref int commonWithNext, int prefixLength, ReadOnlySpan<byte> shared)
        {
            if (useShared)
            {
                // Der erste Nachbar, der weniger als prefixLength Zeichen teilt (der letzte Schlüssel teilt 0).
                int last = index + shared[index..].IndexOfAnyInRange((byte)0, (byte)(prefixLength - 1));
                (index, commonWithNext) = (last + 1, shared[last]);
                return;
            }

            string from = _keys[index];
            index = SkipPrefix(index, prefixLength);
            commonWithNext = index < _keys.Length ? CommonPrefixLength(from, prefixLength - 1, _keys[index]) : 0;
        }
    }

    /// <summary>
    /// Erste Stelle hinter <paramref name="index"/>, deren Schlüssel nicht mit den ersten <paramref name="length"/>
    /// Zeichen des Schlüssels an <paramref name="index"/> beginnt. Solche Schlüssel stehen in der sortierten Liste
    /// hintereinander: erst in wachsenden Schritten vor, dann dazwischen halbieren.
    /// </summary>
    int SkipPrefix(int index, int length)
    {
        string key = _keys[index];
        bool Shares(int at) => _keys[at].Length >= length && string.CompareOrdinal(_keys[at], 0, key, 0, length) == 0;

        int step = 1;
        while (index + step < _keys.Length && Shares(index + step)) step <<= 1;

        int lo = index + (step >> 1) + 1, hi = Math.Min(index + step, _keys.Length); // lo − 1 teilt den Anfang, hi nicht (oder Ende)
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Shares(mid)) lo = mid + 1;
            else hi = mid;
        }

        return lo;
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

    /// <summary>Länge des gemeinsamen Anfangs von <paramref name="a"/>[..<paramref name="aLength"/>] und <paramref name="b"/>.</summary>
    static int CommonPrefixLength(string a, int aLength, string b)
    {
        int max = Math.Min(aLength, b.Length);
        int k = 0;
        while (k < max && a[k] == b[k]) k++;
        return k;
    }
}

/// <summary>
/// Groß- und Kleinschreibung nach den Regeln einer Sprache. Fast überall genügt die sprachunabhängige Umwandlung;
/// Türkisch unterscheidet i mit Punkt (i/İ) und ohne (ı/I).
/// </summary>
public static class TextCase
{
    public static string Upper(string text, string? language) =>
        language == "tr" ? text.Replace('i', 'İ').Replace('ı', 'I').ToUpperInvariant() : text.ToUpperInvariant();

    public static string Lower(string text, string? language) =>
        language == "tr" ? text.Replace('I', 'ı').Replace('İ', 'i').ToLowerInvariant() : text.ToLowerInvariant();

    /// <summary>Erster Buchstabe groß, der Rest unverändert.</summary>
    public static string Capitalize(string text, string? language) =>
        text.Length == 0 ? text : Upper(text[..1], language) + text[1..];
}
