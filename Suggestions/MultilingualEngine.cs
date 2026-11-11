using System.Numerics;

namespace ZBoard.Suggestions;

/// <summary>
/// Mehrsprachige Eingabe wie bei Gboard: Vorschläge, Autokorrektur, Wischeingabe und Vorhersage aus allen aktiven
/// Sprachen gleichzeitig (auf der Leertaste steht dann z. B. „DE · EN“). Die erste Sprache ist die des angezeigten
/// Layouts; ihre Tastennachbarn gelten für alle Sprachen. Mit nur einer Sprache verhält sich die Klasse genau wie
/// die <see cref="SuggestionEngine"/> dieser Sprache. Plattformunabhängig.
/// </summary>
/// <remarks>
/// Die Häufigkeiten der Wörterbücher müssen auf denselben Maßstab gebracht sein
/// (<see cref="WordDictionary.ReferenceTotal"/>), sonst gewinnt die Sprache mit dem größeren Korpus.
/// </remarks>
public sealed class MultilingualEngine
{
    /// <summary>Wie Gboard höchstens drei Sprachen gleichzeitig.</summary>
    public const int MaxLanguages = 3;

    /// <summary>Gehört das vorherige Wort eindeutig zu einer Sprache, zählen deren Vorschläge so viel mehr.</summary>
    public const double ContextLanguageBoost = 2.0;

    /// <summary>
    /// Mindesthäufigkeit (normiert, etwa 0,7 pro Million), ab der ein Wort einer weiteren Sprache als richtig
    /// geschrieben gilt. Die Untertitel-Listen enthalten seltene Tippfehler („teh“, „becuase“); die sollen das
    /// Korrigieren in der Layoutsprache nicht verhindern.
    /// </summary>
    public const int MinSecondaryFrequency = 100;

    /// <summary>
    /// „haus“ → „Haus“ unterbleibt, wenn eine andere Sprache das kleingeschriebene Wort mindestens so oft (relativ zum
    /// Substantiv) kennt: „kind“, „name“ (Englisch) bleiben, „tag“ und „haus“ werden großgeschrieben.
    /// </summary>
    public const double NounConflictRatio = 0.1;

    /// <summary>So viel häufiger muss ein Wort in einer Sprache sein, damit es eindeutig zu ihr gehört.</summary>
    const double DominanceRatio = 10.0;

    readonly (string Language, SuggestionEngine Engine)[] _engines;

    /// <param name="engines">Sprachen mit ihrer Engine; die erste ist die des angezeigten Layouts.</param>
    public MultilingualEngine(IReadOnlyList<(string Language, SuggestionEngine Engine)> engines)
    {
        if (engines.Count == 0) throw new ArgumentException("Mindestens eine Sprache nötig.", nameof(engines));
        _engines = [.. engines.Take(MaxLanguages)];
    }

    public MultilingualEngine(string language, SuggestionEngine engine) : this([(language, engine)])
    {
    }

    /// <summary>Engine der Layoutsprache.</summary>
    public SuggestionEngine Primary => _engines[0].Engine;

    /// <summary>Beteiligte Sprachen, Layoutsprache zuerst.</summary>
    public IReadOnlyList<string> Languages => [.. _engines.Select(e => e.Language)];

    public bool IsMultilingual => _engines.Length > 1;

    /// <summary>Die aktiven Sprachen für die Eingabe: <paramref name="current"/> zuerst, dann die übrigen in Reihenfolge.</summary>
    public static IReadOnlyList<string> ActiveLanguages(string current, IReadOnlyList<string> enabled, bool multilingual)
    {
        if (!multilingual) return [current];
        return [.. enabled.Where(l => l != current).Prepend(current).Distinct(StringComparer.Ordinal).Take(MaxLanguages)];
    }

    /// <summary>Beschriftung der Leertaste: „DE · EN“ bei mehreren Sprachen.</summary>
    public static string SpaceLabel(IReadOnlyList<string> languages) =>
        string.Join(" · ", languages.Select(l => l.ToUpperInvariant()));

    /// <summary>Der Nutzer hat dieses Wort bewusst so gewollt: in keiner Sprache mehr korrigieren.</summary>
    public void SuppressCorrection(string word)
    {
        foreach (var (_, engine) in _engines) engine.SuppressCorrection(word);
    }

    /// <summary>Vorschläge für das gerade getippte Wort aus allen Sprachen.</summary>
    public SuggestionResult Suggest(string typed, string? previousWord = null, int max = 3)
    {
        if (!IsMultilingual) return Primary.Suggest(typed, previousWord, max);
        if (string.IsNullOrWhiteSpace(typed)) return SuggestionResult.Empty;

        int context = ContextLanguage(previousWord);
        var proximity = Primary.Proximity;
        var analyses = _engines.Select(e => e.Engine.Analyze(typed, previousWord, proximity, max)).ToArray();

        var ranked = analyses
            .SelectMany((a, i) => a.Candidates.Select(c => (c.Word, Language: i, Score: c.Score * Weight(i, context))))
            .OrderByDescending(c => c.Score)
            .DistinctBy(c => c.Word, StringComparer.Ordinal)
            .ToList();
        var candidates = ranked.Select(c => c.Word).Take(max).ToList();

        // In einer der Sprachen richtig geschrieben: nicht in eine andere „korrigieren“.
        bool typedIsWord = analyses.Where((a, i) => IsWordIn(a, i)).Any();
        if (typedIsWord && ranked.Count > 0)
        {
            // Das richtig getippte Wort gehört nach vorn, wenn ein häufigeres Wort einer anderen Sprache vorne stünde
            // ("rot" vor "for"); innerhalb der eigenen Sprache bleibt die Reihenfolge ("über" vor "uber").
            int home = Array.IndexOf(analyses, analyses.MaxBy(a => (long)a.TypedFrequency + (a.TypedLearned ? int.MaxValue : 0)));
            if (ranked[0].Language != home)
            {
                string lower = typed.ToLowerInvariant();
                candidates = [.. ranked.Where(c => c.Word.ToLowerInvariant() == lower)
                    .Concat(ranked.Where(c => c.Word.ToLowerInvariant() != lower)).Select(c => c.Word).Take(max)];
            }
        }

        string? autoCorrection = null;
        if (!analyses.Any(a => a.CorrectionSuppressed))
        {
            (string Word, double Score)? best = null;
            for (int i = 0; i < analyses.Length; i++)
            {
                if (analyses[i].Correction is not { } c) continue;
                double score = c.Score * Weight(i, context);
                if (best == null || score > best.Value.Score) best = (c.Word, score);
            }

            if (!typedIsWord && best is { } correction && typed.Length >= 2)
            {
                autoCorrection = SuggestionEngine.MatchCase(correction.Word, typed);
            }
            else
            {
                // "haus" → "Haus", außer eine andere Sprache kennt das kleingeschriebene Wort ebenfalls gut ("kind")
                for (int i = 0; i < analyses.Length && autoCorrection == null; i++)
                {
                    if (analyses[i].NounForm is not { } noun) continue;
                    double nounFrequency = analyses[i].TypedFrequency;
                    bool otherKnows = analyses.Where((_, j) => j != i)
                        .Any(a => a.TypedLearned || a.TypedFrequency >= NounConflictRatio * nounFrequency);
                    if (!otherKnows) autoCorrection = noun;
                }
            }
        }

        if (autoCorrection == typed) autoCorrection = null;
        return new SuggestionResult(typed, candidates, autoCorrection, typedIsWord);
    }

    /// <summary>Wörter für eine Wischspur aus allen Sprachen, bestes zuerst.</summary>
    public IReadOnlyList<string> Glide(
        IReadOnlyList<Vector2> path,
        IReadOnlyDictionary<char, Vector2> keys,
        string? previousWord = null,
        int max = 4)
    {
        if (!IsMultilingual) return Primary.Glide(path, keys, previousWord, max);

        int context = ContextLanguage(previousWord);
        return _engines
            .SelectMany((e, i) => e.Engine.GlideScored(path, keys, previousWord, max)
                .Select(r => (r.Word, Score: r.Score + Math.Log(Weight(i, context)))))
            .OrderByDescending(r => r.Score)
            .Select(r => r.Word)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>Nächstes Wort nach <paramref name="previousWord"/> aus allen Sprachen.</summary>
    public IReadOnlyList<string> Predict(string previousWord, int max = 3)
    {
        if (!IsMultilingual) return Primary.Predict(previousWord, max);

        int context = ContextLanguage(previousWord);
        return _engines
            .SelectMany((e, i) => e.Engine.PredictScored(previousWord, max)
                .Select(p => (p.Word, Score: p.Score * Weight(i, context))))
            .OrderByDescending(p => p.Score)
            .Select(p => p.Word)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>Gelernte Wortfolge nach <paramref name="word"/>, zuerst in der Sprache des Worts.</summary>
    public string? ContinuePhrase(string word, int maxWords = 3)
    {
        if (!IsMultilingual) return Primary.ContinuePhrase(word, maxWords);

        int own = ContextLanguage(word);
        var order = own < 0 ? Enumerable.Range(0, _engines.Length) : Enumerable.Range(0, _engines.Length).Where(i => i != own).Prepend(own);
        foreach (int i in order)
        {
            if (_engines[i].Engine.ContinuePhrase(word, maxWords) is { } phrase) return phrase;
        }

        return null;
    }

    /// <summary>Merkt sich das Wort in der Sprache, zu der es gehört (siehe <see cref="LearningLanguage"/>).</summary>
    public void Learn(string? previous, string word, bool accepted = false) =>
        _engines[LearningIndex(previous, word)].Engine.History.Learn(previous, word, accepted);

    /// <summary>
    /// Sprache, in der ein geschriebenes Wort gelernt wird: die einzige, deren Wörterbuch es kennt; kennen es mehrere,
    /// die, in der es viel gebräuchlicher ist, sonst die des vorherigen Worts (sonst die erste); kennt es keine, die, die es schon gelernt hat, sonst die des
    /// vorherigen Worts, sonst die Layoutsprache.
    /// </summary>
    public string LearningLanguage(string? previous, string word) => _engines[LearningIndex(previous, word)].Language;

    int LearningIndex(string? previous, string word)
    {
        if (!IsMultilingual) return 0;

        var inDictionary = Indices(e => e.InDictionary(word));
        if (inDictionary.Count == 1) return inDictionary[0];

        int own = ContextLanguage(word); // in mehreren Wörterbüchern, aber in einer Sprache viel gebräuchlicher
        if (own >= 0) return own;

        int context = ContextLanguage(previous);
        if (inDictionary.Count > 1) return inDictionary.Contains(context) ? context : inDictionary[0];

        var learned = Indices(e => e.History.CountOf(word) > 0);
        if (learned.Count > 0) return learned.Contains(context) ? context : learned[0];
        return context >= 0 ? context : 0;
    }

    /// <summary>
    /// Index der Sprache, zu der <paramref name="word"/> eindeutig gehört (dort mindestens zehnmal so gebräuchlich wie
    /// in jeder anderen); -1, wenn unbekannt oder mehrdeutig ("will", "die").
    /// </summary>
    int ContextLanguage(string? word)
    {
        if (word == null || !IsMultilingual) return -1;
        var weights = _engines.Select(e => e.Engine.Weight(word)).ToArray();
        int best = Array.IndexOf(weights, weights.Max());
        if (weights[best] <= 0) return -1;
        return weights.Where((_, i) => i != best).All(w => w * DominanceRatio <= weights[best]) ? best : -1;
    }

    /// <summary>Gilt das Getippte in Sprache <paramref name="index"/> als richtig geschrieben?</summary>
    static bool IsWordIn(SuggestionAnalysis analysis, int index) =>
        analysis.TypedLearned
        || analysis.TypedFrequency > 0 && (index == 0 || analysis.TypedFrequency >= MinSecondaryFrequency);

    List<int> Indices(Func<SuggestionEngine, bool> predicate)
    {
        List<int> result = [];
        for (int i = 0; i < _engines.Length; i++)
        {
            if (predicate(_engines[i].Engine)) result.Add(i);
        }

        return result;
    }

    static double Weight(int index, int context) => index == context ? ContextLanguageBoost : 1.0;
}
