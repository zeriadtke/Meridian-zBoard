using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ZBoard.Suggestions;

/// <summary>
/// Kleines neuronales Sprachmodell (LSTM, erzeugt mit tools/LanguageModel/train_lm.py): Wie wahrscheinlich ist jedes
/// Wort des Vokabulars nach dem ganzen Satz bis zur Schreibstelle? Ergänzt das <see cref="NgramModel"/>, das nur die
/// beiden Wörter davor kennt. Plattformunabhängig; ausgeführt wird das Modell von einer <see cref="NeuralRunner"/>
/// (in der App ONNX Runtime, siehe OnnxLanguageModel.cs).
/// </summary>
public interface INeuralLanguageModel
{
    /// <summary>
    /// Verteilung über das nächste Wort nach <paramref name="tokens"/> (ältestes zuerst, mit
    /// <see cref="NgramModel.SentenceStart"/> und <see cref="NgramModel.Comma"/>), oder null, wenn das Modell gerade
    /// nicht bereitsteht (noch beim Laden, abgeschaltet, Fehler). Darf nie lange blockieren.
    /// </summary>
    NeuralDistribution? Next(IReadOnlyList<string> tokens);
}

/// <summary>
/// Führt das Netz einmal aus: Wort-Ids (ältestes zuerst) → Wahrscheinlichkeit jedes Vokabular-Eintrags für das nächste
/// Wort (Länge = <see cref="NeuralVocabulary.Count"/>, Summe 1).
/// </summary>
public delegate float[] NeuralRunner(long[] ids);

/// <summary>
/// Vokabular des neuronalen Modells (Datei &lt;Sprache&gt;.vocab.txt): Zeile i = Eintrag i als „wort&lt;TAB&gt;anteil“, der
/// Anteil ist die Häufigkeit des Worts im Trainingstext (für <see cref="NeuralDistribution.ContextFactor"/>). Die ersten
/// vier Einträge sind fest: &lt;pad&gt;, &lt;unk&gt;, &lt;s&gt; (Satzanfang) und das Komma. Wörter stehen in der Schreibweise
/// der Wortliste ("Haus", "frage" und "Frage").
/// </summary>
public sealed class NeuralVocabulary
{
    public const int PadId = 0, UnknownId = 1, SentenceStartId = 2, CommaId = 3, FirstWordId = 4;
    public const string Pad = "<pad>", Unknown = "<unk>";

    readonly string[] _words;
    readonly float[] _prior;
    readonly Dictionary<string, int> _ids;
    readonly Dictionary<string, int[]> _idsIgnoringCase;
    readonly string? _language;

    NeuralVocabulary(string[] words, float[] prior, string? language)
    {
        (_words, _prior, _language) = (words, prior, language);
        _ids = new Dictionary<string, int>(words.Length, StringComparer.Ordinal);
        _idsIgnoringCase = new Dictionary<string, int[]>(words.Length, StringComparer.Ordinal);
        for (int i = FirstWordId; i < words.Length; i++)
        {
            _ids.TryAdd(words[i], i);
            ref int[]? same = ref CollectionsMarshal.GetValueRefOrAddDefault(_idsIgnoringCase, Lower(words[i]), out _);
            same = same == null ? [i] : [.. same, i];
        }
    }

    public int Count => _words.Length;

    public string this[int id] => _words[id];

    public float Prior(int id) => _prior[id];

    /// <summary>Liest das Vokabular; <paramref name="language"/> bestimmt die Kleinschreibung (Türkisch: I/ı).</summary>
    public static NeuralVocabulary Load(Stream stream, string? language = null)
    {
        var words = new List<string>();
        var prior = new List<float>();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            int tab = line.IndexOf('\t');
            words.Add(tab < 0 ? line : line[..tab]);
            prior.Add(tab < 0 ? 0f : float.Parse(line.AsSpan(tab + 1), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        if (words.Count < FirstWordId || words[PadId] != Pad || words[UnknownId] != Unknown
            || words[SentenceStartId] != NgramModel.SentenceStart || words[CommaId] != NgramModel.Comma)
        {
            throw new InvalidDataException("Kein Vokabular eines zBoard-Sprachmodells");
        }

        return new NeuralVocabulary([.. words], [.. prior], language);
    }

    /// <summary>Id eines Kontext-Worts: genaue Schreibweise, sonst die häufigste Schreibweise, sonst &lt;unk&gt;.</summary>
    public int IdOf(string token)
    {
        if (token == NgramModel.SentenceStart) return SentenceStartId;
        if (token == NgramModel.Comma) return CommaId;
        if (_ids.TryGetValue(token, out int id)) return id;
        return _idsIgnoringCase.TryGetValue(Lower(token), out var ids) ? ids[0] : UnknownId;
    }

    /// <summary>Alle Einträge mit dieser Schreibweise ohne Rücksicht auf Groß-/Kleinschreibung (leer, wenn unbekannt).</summary>
    public IReadOnlyList<int> IdsIgnoringCase(string word) =>
        _idsIgnoringCase.TryGetValue(Lower(word), out var ids) ? ids : [];

    public long[] Encode(IReadOnlyList<string> tokens, int maxTokens)
    {
        int skip = Math.Max(0, tokens.Count - maxTokens);
        var ids = new long[tokens.Count - skip];
        for (int i = 0; i < ids.Length; i++) ids[i] = IdOf(tokens[skip + i]);
        return ids;
    }

    string Lower(string word) => _language == "tr"
        ? word.Replace('I', 'ı').Replace('İ', 'i').ToLowerInvariant()
        : word.ToLowerInvariant();
}

/// <summary>Was das neuronale Modell an einer Stelle im Satz über das nächste Wort sagt.</summary>
public sealed class NeuralDistribution
{
    readonly NeuralVocabulary _vocabulary;
    readonly float[] _probabilities;
    List<(string Word, double Probability)>? _ranked;

    public NeuralDistribution(NeuralVocabulary vocabulary, float[] probabilities)
    {
        if (probabilities.Length != vocabulary.Count)
        {
            throw new ArgumentException($"Modell liefert {probabilities.Length} Werte, das Vokabular hat {vocabulary.Count} Einträge");
        }

        (_vocabulary, _probabilities) = (vocabulary, probabilities);
    }

    /// <summary>Wahrscheinlichkeit des Worts als nächstes, alle Schreibweisen zusammen ("frage" und "Frage"); 0, wenn unbekannt.</summary>
    public double Probability(string word)
    {
        double sum = 0;
        foreach (int id in _vocabulary.IdsIgnoringCase(word)) sum += _probabilities[id];
        return sum;
    }

    /// <summary>Wahrscheinlichkeit genau dieser Schreibweise.</summary>
    public double FormProbability(string form)
    {
        foreach (int id in _vocabulary.IdsIgnoringCase(form))
        {
            if (_vocabulary[id] == form) return _probabilities[id];
        }

        return 0;
    }

    /// <summary>
    /// Wie viel wahrscheinlicher das Wort hier ist als im Durchschnitt aller Texte (wie
    /// <see cref="NgramModel.ContextFactor"/>, kann aber auch kleiner als 1 sein: „passt hier schlecht“). Null, wenn das
    /// Modell das Wort nicht kennt – dann entscheidet das N-Gramm-Modell allein.
    /// </summary>
    public double? ContextFactor(string word)
    {
        double p = 0, prior = 0;
        foreach (int id in _vocabulary.IdsIgnoringCase(word))
        {
            p += _probabilities[id];
            prior += _vocabulary.Prior(id);
        }

        return prior > 0 ? p / prior : null;
    }

    /// <summary>Die wahrscheinlichsten nächsten Wörter (ohne Satzanfang, Komma und Unbekanntes), bestes zuerst.</summary>
    public IReadOnlyList<(string Word, double Probability)> Top(int max)
    {
        if (_ranked == null || _ranked.Count < max)
        {
            // Teilweise Auswahl statt komplettem Sortieren: Das Vokabular hat Zehntausende Einträge.
            var best = new PriorityQueue<int, float>(max + 1);
            for (int id = NeuralVocabulary.FirstWordId; id < _probabilities.Length; id++)
            {
                best.Enqueue(id, _probabilities[id]);
                if (best.Count > max) best.Dequeue();
            }

            var ranked = new List<(string Word, double Probability)>(best.Count);
            while (best.TryDequeue(out int id, out float p)) ranked.Add((_vocabulary[id], p));
            ranked.Reverse();
            _ranked = ranked;
        }

        return _ranked.Count > max ? _ranked.GetRange(0, max) : _ranked;
    }
}

/// <summary>
/// Das neuronale Modell einer Sprache, wie die Tastatur es benutzt: lädt beim ersten Gebrauch im Hintergrund (bis dahin
/// gibt es keine Vorhersage aus dem Netz, das Tippen wartet nie), merkt sich die Ergebnisse der letzten Satzstellen
/// (beim Tippen eines Worts bleibt der Kontext gleich) und lässt sich über <paramref name="enabled"/> abschalten.
/// Threadsicher.
/// </summary>
public sealed class NeuralLanguageModel : INeuralLanguageModel
{
    /// <summary>So viele Wörter Kontext bekommt das Netz höchstens (wie beim Training).</summary>
    public const int MaxContext = WordContext.MaxSentenceTokens;

    const int CacheSize = 8;

    readonly Func<(NeuralVocabulary Vocabulary, NeuralRunner Run)> _load;
    readonly Func<bool> _enabled;
    readonly object _lock = new();
    readonly LinkedList<(string Key, NeuralDistribution Result)> _cache = new();
    Task<(NeuralVocabulary Vocabulary, NeuralRunner Run)>? _loading;

    /// <param name="load">Lädt Vokabular und Netz (läuft einmal, im Hintergrund).</param>
    /// <param name="enabled">Wird bei jeder Anfrage gefragt; false: keine Vorhersage aus dem Netz (und kein Laden).</param>
    /// <param name="loadSynchronously">Beim ersten Gebrauch auf das Laden warten (Tests, Messwerkzeuge).</param>
    public NeuralLanguageModel(
        Func<(NeuralVocabulary Vocabulary, NeuralRunner Run)> load, Func<bool>? enabled = null, bool loadSynchronously = false)
    {
        _load = load;
        _enabled = enabled ?? (() => true);
        if (loadSynchronously) _loading = Task.FromResult(load());
    }

    /// <summary>Für Tests: ein fertiges Modell.</summary>
    public NeuralLanguageModel(NeuralVocabulary vocabulary, NeuralRunner run)
        : this(() => (vocabulary, run), loadSynchronously: true)
    {
    }

    /// <summary>Fehler beim Laden oder Ausführen; danach liefert das Modell nichts mehr.</summary>
    public Exception? Error { get; private set; }

    public bool IsReady => _loading is { IsCompletedSuccessfully: true } && Error == null;

    /// <summary>Beginnt das Laden, ohne auf das Ergebnis zu warten.</summary>
    public void Preload()
    {
        if (_enabled()) EnsureLoading();
    }

    public NeuralDistribution? Next(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0 || Error != null || !_enabled()) return null;

        var loading = EnsureLoading();
        if (!loading.IsCompleted) return null;
        if (!loading.IsCompletedSuccessfully)
        {
            Error ??= loading.Exception?.GetBaseException() ?? new OperationCanceledException();
            return null;
        }

        var (vocabulary, run) = loading.Result;
        long[] ids = vocabulary.Encode(tokens, MaxContext);
        string key = string.Join(',', ids);

        lock (_lock)
        {
            for (var node = _cache.First; node != null; node = node.Next)
            {
                if (node.Value.Key != key) continue;
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value.Result;
            }

            NeuralDistribution result;
            try
            {
                result = new NeuralDistribution(vocabulary, run(ids));
            }
            catch (Exception e)
            {
                // Das Netz ist nur eine Verbesserung: Ein Fehler schaltet es ab, die Tastatur läuft mit dem N-Gramm-Modell weiter.
                Error = e;
                return null;
            }

            _cache.AddFirst((key, result));
            if (_cache.Count > CacheSize) _cache.RemoveLast();
            return result;
        }
    }

    Task<(NeuralVocabulary Vocabulary, NeuralRunner Run)> EnsureLoading()
    {
        if (_loading != null) return _loading;
        lock (_lock)
        {
            return _loading ??= Task.Run(_load);
        }
    }
}
