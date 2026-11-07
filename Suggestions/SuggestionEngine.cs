namespace ZBoard.Suggestions;

/// <summary>Ergebnis für das aktuell getippte Wort.</summary>
/// <param name="Typed">Das getippte Wort, wie eingegeben.</param>
/// <param name="Candidates">Vorschläge, bester zuerst, bereits in passender Groß-/Kleinschreibung.</param>
/// <param name="AutoCorrection">Wort, das bei Leertaste/Satzzeichen statt <paramref name="Typed"/> eingefügt wird, oder null.</param>
/// <param name="TypedIsWord">Das getippte Wort ist bekannt (Wörterbuch oder selbst gelernt).</param>
public sealed record SuggestionResult(
    string Typed,
    IReadOnlyList<string> Candidates,
    string? AutoCorrection,
    bool TypedIsWord)
{
    public static readonly SuggestionResult Empty = new(string.Empty, [], null, false);
}

/// <summary>
/// Vorschläge, Autokorrektur und Nächstes-Wort-Vorhersage.
/// Quellen: Wörterbuch, mitgelieferte Wortpaare und das, was zBoard aus den eigenen Eingaben gelernt hat.
/// </summary>
public sealed class SuggestionEngine
{
    const double ExactBoost = 10.0;
    const double CompletionWeight = 0.6;
    const double DistancePenaltyBase = 12.0;
    const double UserWordWeight = 5000.0;   // eine gelernte Verwendung zählt wie ein mittelhäufiges Wort
    const double ContextBoost = 4.0;        // Vorschlag passt zum vorherigen Wort
    const double UserBigramWeight = 0.5;    // je gelernte Wortfolge, gegenüber 0..1 aus den Wortpaaren

    readonly WordDictionary _dictionary;
    readonly KeyProximity _proximity;
    readonly BigramModel _bigrams;
    readonly UserHistory _history;
    readonly HashSet<string> _noCorrection = new(StringComparer.OrdinalIgnoreCase);
    GlideDecoder? _glide;

    public SuggestionEngine(
        WordDictionary dictionary,
        KeyProximity proximity,
        BigramModel? bigrams = null,
        UserHistory? history = null)
    {
        _dictionary = dictionary;
        _proximity = proximity;
        _bigrams = bigrams ?? BigramModel.Empty;
        _history = history ?? new UserHistory();
    }

    public UserHistory History => _history;

    /// <summary>Der Nutzer hat dieses Wort bewusst so gewollt (Autokorrektur rückgängig gemacht).</summary>
    public void SuppressCorrection(string word) => _noCorrection.Add(word);

    /// <summary>Vorschläge für das gerade getippte Wort; <paramref name="previousWord"/> verbessert die Reihenfolge.</summary>
    public SuggestionResult Suggest(string typed, string? previousWord = null, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(typed)) return SuggestionResult.Empty;

        var lower = typed.ToLowerInvariant();
        bool typedIsWord = _dictionary.Contains(lower) || _history.IsKnown(lower);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        void Offer(string word, double score)
        {
            if (!scores.TryGetValue(word, out var existing) || score > existing) scores[word] = score;
        }

        // 1. Vervollständigungen aus Wörterbuch und gelernten Wörtern
        foreach (var (word, freq) in _dictionary.Complete(lower))
        {
            bool exact = word.Length == lower.Length;
            Offer(word, freq * (exact ? ExactBoost : CompletionWeight));
        }

        foreach (var (word, count) in _history.Complete(lower))
        {
            bool exact = word.Length == lower.Length;
            Offer(word, count * UserWordWeight * (exact ? ExactBoost : CompletionWeight));
        }

        // 2. Korrekturen per gewichteter Editierdistanz
        float maxDistance = lower.Length switch
        {
            <= 2 => 0f,
            <= 5 => 1f,
            _ => 2f,
        };

        string? bestCorrection = null;
        double bestCorrectionScore = 0;
        if (maxDistance > 0 && !lower.Any(char.IsDigit))
        {
            foreach (var (word, freq, distance) in _dictionary.FindSimilar(lower, maxDistance, _proximity.SubstitutionCost))
            {
                if (distance <= 0) continue;
                double score = freq / Math.Pow(DistancePenaltyBase, distance);
                Offer(word, score);
                if (score > bestCorrectionScore)
                {
                    bestCorrectionScore = score;
                    bestCorrection = word;
                }
            }
        }

        // 3. Satzkontext: Wörter, die typischerweise auf das vorherige folgen, nach vorn
        if (previousWord != null)
        {
            var followers = new HashSet<string>(
                _bigrams.Followers(previousWord).Select(f => f.Next)
                    .Concat(_history.Followers(previousWord).Select(f => f.Next)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var word in scores.Keys.ToList())
            {
                if (followers.Contains(word)) scores[word] *= ContextBoost;
            }
        }

        // Alle Schreibweisen der besten Wörter anbieten ("frage" und "Frage")
        var candidates = scores
            .OrderByDescending(s => s.Value)
            .SelectMany(s => FormsOf(s.Key))
            .Select(form => MatchCase(form, typed))
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();

        string? autoCorrection = null;
        if (!_noCorrection.Contains(lower))
        {
            if (!typedIsWord && bestCorrection != null && lower.Length >= 2)
            {
                autoCorrection = MatchCase(bestCorrection, typed);
            }
            else if (_dictionary.Contains(lower) && IsLowerCase(typed) && NounOnly(lower) is { } noun
                     && _history.LearnedForm(lower) != lower) // bewusst klein geschrieben und gelernt
            {
                autoCorrection = noun; // "haus" → "Haus"
            }
        }

        if (autoCorrection == typed) autoCorrection = null;
        return new SuggestionResult(typed, candidates, autoCorrection, typedIsWord);
    }

    /// <summary>Wörter für eine Wischspur (Koordinaten in Tastenbreiten), bestes zuerst.</summary>
    public IReadOnlyList<string> Glide(
        IReadOnlyList<System.Numerics.Vector2> path,
        IReadOnlyDictionary<char, System.Numerics.Vector2> keys,
        string? previousWord = null,
        int max = 4)
    {
        HashSet<string>? followers = null;
        if (previousWord != null)
        {
            followers = new HashSet<string>(
                _bigrams.Followers(previousWord).Select(f => f.Next.ToLowerInvariant())
                    .Concat(_history.Followers(previousWord).Select(f => f.Next.ToLowerInvariant())),
                StringComparer.Ordinal);
        }

        _glide ??= new GlideDecoder(_dictionary, _history);
        return _glide.Decode(path, keys, followers, max);
    }

    /// <summary>Nächstes Wort nach <paramref name="previousWord"/>; eigene Wortfolgen haben Vorrang.</summary>
    public IReadOnlyList<string> Predict(string previousWord, int max = 3)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        var seed = _bigrams.Followers(previousWord);
        if (seed.Count > 0)
        {
            double top = seed[0].Freq;
            foreach (var (next, freq) in seed) scores[next] = freq / top;
        }

        foreach (var (next, count) in _history.Followers(previousWord))
        {
            scores[next] = scores.GetValueOrDefault(next) + count * UserBigramWeight;
        }

        return scores
            .OrderByDescending(s => s.Value)
            .Select(s => s.Key)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>Ab so vielen Verwendungen einer Wortfolge schlägt zBoard sie als Ganzes vor.</summary>
    public const int PhraseThreshold = 2;

    /// <summary>
    /// Wie Gboard ganze Wortfolgen anbieten: die gelernten Wörter, die verlässlich auf <paramref name="word"/> folgen
    /// ("Mit freundlichen" → "Grüßen"), höchstens <paramref name="maxWords"/>. Nur aus Gelerntem; null, wenn keine
    /// Folge oft genug und eindeutig genug war (das häufigste Folgewort muss mindestens die Hälfte ausmachen).
    /// </summary>
    public string? ContinuePhrase(string word, int maxWords = 3)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { word };
        string current = word;
        while (parts.Count < maxWords)
        {
            var followers = _history.Followers(current);
            if (followers.Count == 0) break;
            var (next, count) = followers[0];
            int total = followers.Sum(f => f.Count);
            if (count < PhraseThreshold || count * 2 < total || !seen.Add(next)) break;
            parts.Add(next);
            current = next;
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Schreibweisen aus dem Wörterbuch, sonst das (gelernte) Wort selbst.</summary>
    IReadOnlyList<string> FormsOf(string word)
    {
        var forms = _dictionary.Forms(word);
        return forms.Count > 0 ? forms : [word];
    }

    /// <summary>Die großgeschriebene Form, wenn das Wort ausschließlich großgeschrieben vorkommt.</summary>
    string? NounOnly(string lower)
    {
        var forms = _dictionary.Forms(lower);
        return forms.Count > 0 && forms.All(f => f.Length > 0 && char.IsUpper(f[0])) ? forms[0] : null;
    }

    static bool IsLowerCase(string text) => text.Length > 0 && !text.Any(char.IsUpper);

    /// <summary>Übernimmt die Großschreibung der Eingabe: "Hal" → "Hallo", "HAL" → "HALLO", "haus" → "Haus" (Wörterbuchform).</summary>
    public static string MatchCase(string word, string typed)
    {
        if (typed.Length == 0) return word;
        bool allUpper = typed.Length > 1 && typed.Where(char.IsLetter).All(char.IsUpper);
        if (allUpper) return word.ToUpperInvariant();
        if (char.IsUpper(typed[0])) return char.ToUpperInvariant(word[0]) + word[1..];
        return word;
    }
}
