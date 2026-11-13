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

/// <summary>Bewertete Vorschläge einer Sprache, bevor über die Autokorrektur entschieden wird.</summary>
/// <param name="Typed">Das getippte Wort.</param>
/// <param name="Candidates">Vorschläge in passender Groß-/Kleinschreibung mit Bewertung, bester zuerst.</param>
/// <param name="Correction">Beste Tippfehler-Korrektur (Wörterbuchschreibweise) mit Bewertung, oder null.</param>
/// <param name="NounForm">Großgeschriebene Form, wenn das Getippte ein klein geschriebenes reines Substantiv ist ("haus" → "Haus").</param>
/// <param name="TypedIsWord">Das getippte Wort ist bekannt (Wörterbuch oder selbst gelernt).</param>
/// <param name="CorrectionSuppressed">Der Nutzer hat eine Korrektur dieses Worts rückgängig gemacht.</param>
/// <param name="TypedFrequency">Häufigkeit des getippten Worts im Wörterbuch (0, wenn unbekannt).</param>
/// <param name="TypedLearned">Das getippte Wort wurde oft genug selbst geschrieben.</param>
/// <param name="CorrectionOverrides">
/// Das Getippte steht zwar im Wörterbuch, die Korrektur ist aber so viel wahrscheinlicher, dass sie trotzdem gilt
/// ("fur" → "für", "dont" → "don't").
/// </param>
public sealed record SuggestionAnalysis(
    string Typed,
    IReadOnlyList<(string Word, double Score)> Candidates,
    (string Word, double Score)? Correction,
    string? NounForm,
    bool TypedIsWord,
    bool CorrectionSuppressed,
    int TypedFrequency = 0,
    bool TypedLearned = false,
    bool CorrectionOverrides = false)
{
    public static readonly SuggestionAnalysis Empty = new(string.Empty, [], null, null, false, false);
}

/// <summary>
/// Vorschläge, Autokorrektur und Nächstes-Wort-Vorhersage.
/// Quellen: Wörterbuch, Sprachmodell und das, was zBoard aus den eigenen Eingaben gelernt hat.
/// </summary>
public sealed class SuggestionEngine
{
    const double ExactBoost = 10.0;
    const double CompletionWeight = 0.6;
    const double DistancePenaltyBase = 12.0;
    const double UserWordWeight = 5000.0;   // eine gelernte Verwendung zählt wie ein mittelhäufiges Wort
    const double ContextBoost = 4.0;        // Vorschlag folgt in den eigenen Texten auf das vorherige Wort
    const double UserBigramWeight = 0.5;    // je gelernte Wortfolge, gegenüber 0..1 aus dem Sprachmodell
    const int GlideFollowers = 24;          // so viele typische Folgewörter bekommen beim Wischen einen Bonus

    /// <summary>
    /// Ein Wort, das an dieser Stelle im Satz x-mal wahrscheinlicher ist als sonst, zählt x-fach – höchstens so viel,
    /// damit ein einzelner Kontext die Worthäufigkeit und den Tippabstand nicht überstimmt.
    /// Abgestimmt mit tools/SuggestionEval.
    /// </summary>
    public const double MaxContextFactor = 100.0;

    /// <summary>
    /// Bis zu dieser Editierdistanz gilt ein Tastendruck als „fast auf der Grenze zur Nachbartaste“ (nur mit
    /// Fingerpositionen, siehe <see cref="TouchTrace"/>). Dann darf auch ein echtes Wort korrigiert werden, wenn das
    /// Wort mit dem Nachbarbuchstaben im Satz um <see cref="NearMissRatio"/> wahrscheinlicher ist.
    /// Abgestimmt mit tools/SuggestionEval.
    /// </summary>
    public const float NearMissDistance = 0.25f;

    const float NeighborSlipCost = 0.75f;

    // Zusammengesetzte Wörter und fehlende Leerzeichen: Mindestlänge der Teile, und wie häufig ein Teil mindestens
    // sein muss (Anteil an allen Wörtern; schließt seltene Einträge aus, die oft selbst Tippfehler sind).
    const int CompoundPartLength = 3;
    const int MaxCompoundParts = 4;
    const double CompoundCompletionWeight = 0.02;
    const double MissingSpaceCost = 0.5; // ein vergessenes Leerzeichen ist ein häufiger Fehler: billiger als ein falscher Buchstabe
    const double MinPartShare = 2e-6;
    const int MinJoinedLength = 4;
    const double MinSingleLetterShare = 2e-3;

    /// <inheritdoc cref="NearMissDistance"/>
    public const double NearMissRatio = 10.0;

    /// <summary>
    /// So viel wahrscheinlicher muss die großgeschriebene Form eines mehrdeutigen Worts im Satz sein, damit zBoard sie
    /// von selbst einsetzt ("eine frage" → "Frage"). Bei kleineren Werten wird zu oft fälschlich großgeschrieben
    /// ("und liebe" → "Liebe").
    /// </summary>
    public const double NounContextRatio = 10.0;

    /// <summary>
    /// Die Wortlisten enthalten seltene Einträge, die fast immer bequeme Schreibweisen oder Tippfehler sind ("fur",
    /// "mussen", "dont", "yiu"). Ein solcher Eintrag wird trotzdem korrigiert, wenn die Korrektur um diesen Faktor
    /// wahrscheinlicher ist. Für weggelassene Umlaute, Akzente und Apostrophe reicht ein kleiner Faktor: Das tippt man
    /// mit Absicht so.
    /// </summary>
    public const double LazySpellingRatio = 30.0;

    /// <summary>
    /// Wie <see cref="LazySpellingRatio"/> für alle anderen Fehler (Nachbartaste, Buchstabe fehlt …). Sehr streng,
    /// damit seltene echte Wörter stehen bleiben ("fettig" neben "fertig"): Es trifft nur Einträge, die seltener als
    /// etwa einmal pro Million Wörter vorkommen und neben einem der häufigsten Wörter liegen.
    /// Abgestimmt mit tools/SuggestionEval.
    /// </summary>
    public const double RareWordRatio = 3000.0;

    readonly WordDictionary _dictionary;
    readonly KeyProximity _proximity;
    readonly NgramModel _model;
    readonly UserHistory _history;
    readonly HashSet<string> _noCorrection = new(StringComparer.OrdinalIgnoreCase);
    GlideDecoder? _glide;

    public SuggestionEngine(
        WordDictionary dictionary,
        KeyProximity proximity,
        NgramModel? model = null,
        UserHistory? history = null)
    {
        _dictionary = dictionary;
        _proximity = proximity;
        _model = model ?? NgramModel.Empty;
        _history = history ?? new UserHistory();
    }

    public UserHistory History => _history;

    /// <summary>Der Nutzer hat dieses Wort bewusst so gewollt (Autokorrektur rückgängig gemacht).</summary>
    public void SuppressCorrection(string word) => _noCorrection.Add(word);

    /// <summary>Vorschläge für das gerade getippte Wort; <paramref name="previousWord"/> verbessert die Reihenfolge.</summary>
    public SuggestionResult Suggest(string typed, string? previousWord = null, int max = 3) =>
        Suggest(typed, new WordContext(previousWord), max);

    /// <summary>Vorschläge für das gerade getippte Wort; der Satzkontext verbessert die Reihenfolge.</summary>
    /// <param name="touches">Wo der Finger bei jedem Buchstaben lag, falls bekannt: macht die Korrektur genauer.</param>
    public SuggestionResult Suggest(string typed, WordContext context, int max = 3, TouchTrace? touches = null)
    {
        if (string.IsNullOrWhiteSpace(typed)) return SuggestionResult.Empty;

        var analysis = Analyze(typed, context, max: max, touches: touches);
        var candidates = analysis.Candidates.Take(max).Select(c => c.Word).ToList();

        string? autoCorrection = null;
        if (!analysis.CorrectionSuppressed)
        {
            if ((!analysis.TypedIsWord || analysis.CorrectionOverrides) && analysis.Correction is { } correction && typed.Length >= 2)
            {
                autoCorrection = MatchCase(correction.Word, typed);
            }
            else if (analysis.NounForm is { } noun)
            {
                autoCorrection = noun; // "haus" → "Haus"
            }
        }

        if (autoCorrection == typed) autoCorrection = null;
        return new SuggestionResult(typed, candidates, autoCorrection, analysis.TypedIsWord);
    }

    /// <summary>
    /// Bewertete Vorschläge und Korrekturen für <paramref name="typed"/>, ohne sich schon zu entscheiden. Grundlage für
    /// <see cref="Suggest"/> und für die mehrsprachige Eingabe, die mehrere Sprachen gegeneinander abwägt.
    /// </summary>
    /// <param name="proximity">Tastennachbarn des tatsächlich angezeigten Layouts (sonst das eigene der Sprache).</param>
    public SuggestionAnalysis Analyze(string typed, string? previousWord = null, KeyProximity? proximity = null, int max = 8) =>
        Analyze(typed, new WordContext(previousWord), proximity, max);

    /// <inheritdoc cref="Analyze(string, string?, KeyProximity?, int)"/>
    public SuggestionAnalysis Analyze(
        string typed, WordContext context, KeyProximity? proximity = null, int max = 8, TouchTrace? touches = null)
    {
        if (string.IsNullOrWhiteSpace(typed)) return SuggestionAnalysis.Empty;
        proximity ??= _proximity;

        var lower = typed.ToLowerInvariant();
        bool learned = _history.IsKnown(lower);
        int typedFrequency = _dictionary.Lookup(lower)?.Freq ?? ApostropheFormFrequency(lower);

        // Zusammengesetzte Substantive stehen nicht einzeln im Wörterbuch, sind aber richtig geschrieben.
        var compound = typedFrequency == 0 && !learned ? Compound(lower) : null;
        if (compound != null) typedFrequency = compound.Value.Freq;

        bool typedIsWord = typedFrequency > 0 || learned;
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

        if (compound != null) Offer(compound.Value.Form, compound.Value.Freq * ExactBoost);
        foreach (var (word, score) in CompoundCompletions(lower)) Offer(word, score);

        // 2. Korrekturen per gewichteter Editierdistanz
        float maxDistance = lower.Length switch
        {
            <= 1 => 0f,
            2 => WordDictionary.MissingApostropheCost, // nur Umlaut, Akzent oder Apostroph: "im" → "I'm", "ol" → "Öl"
            <= 5 => 1f,
            _ => 2f,
        };

        // Bewertung ohne und mit Satzkontext; NearMiss: nur knapp neben der gemeinten Taste gelandet
        var corrections = new List<(string Word, double Score, double InContext, bool NearMiss)>();
        bool hasTouches = touches != null && touches.Count == lower.Length;
        if (maxDistance > 0 && !lower.Any(char.IsDigit))
        {
            // Mit Fingerpositionen zählt der tatsächliche Abstand zur Taste; Umlaute und Akzente (keine Tippfehler,
            // sondern Abkürzungen) und Stellen ohne Position behalten die festen Kosten.
            Func<int, char, char, float> substitution = (_, a, b) => proximity.SubstitutionCost(a, b);
            if (hasTouches)
            {
                substitution = (position, a, b) =>
                {
                    float fixedCost = proximity.SubstitutionCost(a, b);
                    if (fixedCost <= KeyProximity.VariantCost || touches!.Cost(position, a, b, DistancePenaltyBase) is not { } cost)
                    {
                        return fixedCost;
                    }

                    // Eine Nachbartaste bleibt etwas billiger als ein beliebiger Buchstabe, auch wenn der Finger mitten
                    // auf der Taste lag: Wer sich vertut, landet meist nebenan.
                    return fixedCost < 1f ? Math.Min(cost, NeighborSlipCost) : cost;
                };
            }

            foreach (var (word, freq, distance) in _dictionary.FindSimilar(lower, maxDistance, substitution))
            {
                if (distance <= 0) continue;
                double score = freq / Math.Pow(DistancePenaltyBase, distance);
                Offer(word, score);
                bool nearMiss = hasTouches && distance <= NearMissDistance && word.Length == lower.Length
                    && !KeyProximity.IsLazySpelling(lower, word);
                corrections.Add((word, score, score, nearMiss));
            }
        }

        // 3. Satzkontext: Wörter, die an dieser Stelle typisch sind, nach vorn
        double typedInContext = typedFrequency;
        if (context.Previous != null)
        {
            HashSet<string>? ownFollowers = context.PreviousWord is { } previousWord
                ? new HashSet<string>(_history.Followers(previousWord).Select(f => f.Next), StringComparer.OrdinalIgnoreCase)
                : null;
            double Factor(string word)
            {
                double factor = Math.Min(MaxContextFactor, _model.ContextFactor(context, word));
                return ownFollowers?.Contains(word) == true ? Math.Max(factor, ContextBoost) : factor;
            }

            foreach (var word in scores.Keys.ToList()) scores[word] *= Factor(word);
            typedInContext *= Factor(lower);

            // Auch die Korrektur richtet sich nach dem Satz ("ich hae" → "habe", nicht das häufigere "hat").
            for (int i = 0; i < corrections.Count; i++)
            {
                corrections[i] = corrections[i] with { InContext = corrections[i].Score * Factor(corrections[i].Word) };
            }
        }

        // Welche Korrektur es wird, entscheidet der Satz mit; ob ein Wort aus dem Wörterbuch überhaupt korrigiert
        // wird (weiter unten), entscheidet allein die Bewertung ohne Kontext.
        (string Word, double Score, double InContext, bool NearMiss)? best = null, bestLazySpelling = null, bestNearMiss = null;
        foreach (var correction in corrections)
        {
            if (best == null || correction.InContext > best.Value.InContext) best = correction;
            if (correction.NearMiss && (bestNearMiss == null || correction.InContext > bestNearMiss.Value.InContext))
            {
                bestNearMiss = correction;
            }

            if ((bestLazySpelling == null || correction.InContext > bestLazySpelling.Value.InContext)
                && KeyProximity.IsLazySpelling(lower, correction.Word))
            {
                bestLazySpelling = correction;
            }
        }

        // Fehlendes Leerzeichen ("ichhabe"): zwei Wörter, wenn das besser passt als jede einzelne Korrektur. Manche
        // dieser Verschmelzungen stehen als seltene Einträge in den Wortlisten; dann gilt dieselbe Hürde wie für
        // bequeme Schreibweisen.
        bool splitOverrides = false;
        if (compound == null && !learned && _history.CountOf(lower) == 0 && Split(lower, context) is { } split
            && (typedFrequency == 0 || split.Score >= LazySpellingRatio * typedFrequency)
            && (best == null || split.Score > best.Value.InContext))
        {
            best = (split.Text, split.Score, split.Score, false);
            splitOverrides = typedFrequency > 0;
            Offer(split.Text, split.Score);
        }

        // Alle Schreibweisen der besten Wörter anbieten ("frage" und "Frage"); OrderByDescending ist stabil.
        var candidates = scores
            .OrderByDescending(s => s.Value)
            .SelectMany(s => FormsOf(s.Key).Select(form => (Word: MatchCase(form, typed), Score: s.Value)))
            .DistinctBy(c => c.Word, StringComparer.Ordinal)
            .Take(max)
            .ToList();

        string? nounForm = null;
        if ((_dictionary.Contains(lower) || compound != null) && IsLowerCase(typed)
            && (compound?.Form ?? NounOnly(lower) ?? NounInContext(lower, context)) is { } noun
            && _history.LearnedForm(lower) != lower) // bewusst klein geschrieben und gelernt
        {
            nounForm = noun;
        }

        // Steht das Getippte im Wörterbuch, gilt die Korrektur nur, wenn sie viel wahrscheinlicher ist. Die bequeme
        // Schreibweise hat dabei Vorrang vor einem anderen, häufigeren Wort ("wont" → "won't", nicht "want").
        bool overrides = splitOverrides;
        if (!overrides && best != null && typedFrequency > 0 && !learned && _history.CountOf(lower) == 0)
        {
            // Bei einem zusammengesetzten Wort zählt die Häufigkeit des letzten Teils nicht: "fussball" → "Fußball".
            if (bestLazySpelling is { } lazy && lazy.Score >= LazySpellingRatio * (compound != null ? 1 : typedFrequency))
            {
                (best, overrides) = (lazy, true);
            }
            else
            {
                // Großgeschriebene Einträge sind geprüfte Substantive und Namen ("Ost", "DDR") und bleiben stehen.
                overrides = best.Value.Word != bestLazySpelling?.Word
                    && best.Value.Score >= RareWordRatio * typedFrequency
                    && !char.IsUpper(_dictionary.Lookup(lower)?.Word[0] ?? 'A');
            }

            // Der Finger lag fast auf der Grenze zur Nachbartaste, und das Wort mit dem Nachbarbuchstaben passt viel
            // besser in den Satz ("heute drei" → "frei"): Hier darf auch ein echtes Wort korrigiert werden.
            if (!overrides && bestNearMiss is { } near && near.InContext >= NearMissRatio * typedInContext)
            {
                (best, overrides) = (near, true);
            }
        }

        return new SuggestionAnalysis(
            typed,
            candidates,
            best == null ? null : (best.Value.Word, best.Value.InContext),
            nounForm,
            typedIsWord,
            _noCorrection.Contains(lower),
            typedFrequency,
            learned,
            overrides);
    }

    /// <summary>
    /// Wörter mit Apostroph, die nicht einzeln im Wörterbuch stehen, aber aus bekannten Teilen bestehen: Genitiv und
    /// angehängtes "s" ("John's", "geht's", "parents'") und Elisionen ("l'amico", "j'ai": "l'" und "j'" stehen in
    /// der Wortliste). Liefert die Häufigkeit des Hauptworts, 0 für alles andere.
    /// </summary>
    int ApostropheFormFrequency(string lower)
    {
        int cut = lower.LastIndexOf('\'');
        if (cut <= 0) return 0;

        string head = lower[..cut], tail = lower[(cut + 1)..];
        if (tail is "" or "s") return _dictionary.Lookup(head)?.Freq ?? 0;
        if (tail.Contains('\'')) return 0;
        return _dictionary.Contains(lower[..(cut + 1)]) ? _dictionary.Lookup(tail)?.Freq ?? 0 : 0;
    }

    /// <summary>Steht das Wort im Wörterbuch dieser Sprache?</summary>
    public bool InDictionary(string word) => _dictionary.Contains(word);

    /// <summary>Ein gängiges Wort dieser Sprache (kein seltener Eintrag der Wortliste) oder oft genug selbst geschrieben.</summary>
    public bool IsCommonWord(string word) =>
        (_dictionary.Lookup(word)?.Freq ?? 0) >= Math.Max(1, _dictionary.TotalFrequency * MinPartShare) || _history.IsKnown(word);

    /// <summary>Nimmt ein eben gelerntes Wort zurück (es war Teil eines Tippfehlers).</summary>
    public void Unlearn(string? previous, string word) => _history.Unlearn(previous, word);

    /// <summary>Kennt diese Sprache das Wort (Wörterbuch oder oft genug selbst geschrieben)?</summary>
    public bool Knows(string word) => _dictionary.Contains(word) || _history.IsKnown(word);

    /// <summary>Wie gebräuchlich das Wort in dieser Sprache ist: Häufigkeit im Wörterbuch plus eigene Verwendungen.</summary>
    public double Weight(string word) => (_dictionary.Lookup(word)?.Freq ?? 0) + _history.CountOf(word) * UserWordWeight;

    /// <summary>Tastennachbarn des Layouts dieser Sprache.</summary>
    public KeyProximity Proximity => _proximity;

    /// <summary>Wörter für eine Wischspur (Koordinaten in Tastenbreiten), bestes zuerst.</summary>
    public IReadOnlyList<string> Glide(
        IReadOnlyList<System.Numerics.Vector2> path,
        IReadOnlyDictionary<char, System.Numerics.Vector2> keys,
        string? previousWord = null,
        int max = 4) =>
        [.. GlideScored(path, keys, new WordContext(previousWord), max).Select(r => r.Word)];

    /// <summary>Wie <see cref="Glide"/>, mit Bewertung (logarithmisch, höher ist besser).</summary>
    public IReadOnlyList<(string Word, double Score)> GlideScored(
        IReadOnlyList<System.Numerics.Vector2> path,
        IReadOnlyDictionary<char, System.Numerics.Vector2> keys,
        string? previousWord = null,
        int max = 4) =>
        GlideScored(path, keys, new WordContext(previousWord), max);

    /// <inheritdoc cref="GlideScored(IReadOnlyList{System.Numerics.Vector2}, IReadOnlyDictionary{char, System.Numerics.Vector2}, string?, int)"/>
    public IReadOnlyList<(string Word, double Score)> GlideScored(
        IReadOnlyList<System.Numerics.Vector2> path,
        IReadOnlyDictionary<char, System.Numerics.Vector2> keys,
        WordContext context,
        int max = 4)
    {
        HashSet<string>? followers = null;
        if (context.Previous != null)
        {
            followers = new HashSet<string>(
                _model.Predict(context, GlideFollowers).Select(f => f.Word.ToLowerInvariant()), StringComparer.Ordinal);
            if (context.PreviousWord is { } previousWord)
            {
                followers.UnionWith(_history.Followers(previousWord).Select(f => f.Next.ToLowerInvariant()));
            }
        }

        _glide ??= new GlideDecoder(_dictionary, _history);
        return _glide.DecodeScored(path, keys, followers, max);
    }

    /// <summary>Nächstes Wort nach <paramref name="previousWord"/>; eigene Wortfolgen haben Vorrang.</summary>
    public IReadOnlyList<string> Predict(string previousWord, int max = 3) =>
        [.. PredictScored(new WordContext(previousWord), max).Select(p => p.Word)];

    /// <summary>Nächstes Wort an dieser Stelle im Satz; eigene Wortfolgen haben Vorrang.</summary>
    public IReadOnlyList<string> Predict(WordContext context, int max = 3) =>
        [.. PredictScored(context, max).Select(p => p.Word)];

    /// <summary>Wie <see cref="Predict(string, int)"/>, mit Bewertung.</summary>
    public IReadOnlyList<(string Word, double Score)> PredictScored(string previousWord, int max = 3) =>
        PredictScored(new WordContext(previousWord), max);

    /// <summary>Wie <see cref="Predict(WordContext, int)"/>, mit Bewertung (Sprachmodell 0..1, je gelernte Folge +0,5).</summary>
    public IReadOnlyList<(string Word, double Score)> PredictScored(WordContext context, int max = 3)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        var seed = _model.Predict(context, Math.Max(max, 6));
        if (seed.Count > 0)
        {
            double top = seed[0].Probability;
            foreach (var (next, probability) in seed) scores[next] = probability / top;
        }

        if (context.PreviousWord is { } previousWord)
        {
            foreach (var (next, count) in _history.Followers(previousWord))
            {
                scores[next] = scores.GetValueOrDefault(next) + count * UserBigramWeight;
            }
        }

        return scores
            .OrderByDescending(s => s.Value)
            .Select(s => (s.Key, s.Value))
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

    /// <summary>
    /// Deutsches zusammengesetztes Substantiv aus bekannten Substantiven ("Haustürschlüssel" = Haus + Tür + Schlüssel,
    /// "Geburtstagskuchen" mit Fugen-s): großgeschriebene Form und Häufigkeit des letzten Teils, sonst null. Greift nur,
    /// wo die Wortliste Substantive großschreibt, also im Deutschen.
    /// </summary>
    (string Form, int Freq)? Compound(string lower)
    {
        int n = lower.Length;
        if (n < 2 * CompoundPartLength || n > 40 || !lower.All(char.IsLetter)) return null;

        var parts = CompoundPrefixes(lower);
        for (int start = CompoundPartLength; start <= n - CompoundPartLength; start++)
        {
            if (parts[start] > 0 && CompoundPartFrequency(lower[start..]) is > 0 and var freq)
            {
                return (char.ToUpperInvariant(lower[0]) + lower[1..], freq);
            }
        }

        return null;
    }

    /// <summary>
    /// Vervollständigt das letzte Glied eines zusammengesetzten Substantivs: "haustürschl" → "Haustürschlüssel".
    /// Weit schwächer bewertet als Wörter, die als Ganzes im Wörterbuch stehen.
    /// </summary>
    IEnumerable<(string Word, double Score)> CompoundCompletions(string lower)
    {
        int n = lower.Length;
        if (n < CompoundPartLength + 2 || n > 40 || !lower.All(char.IsLetter)) yield break;

        var parts = CompoundPrefixes(lower);
        for (int start = CompoundPartLength; start <= n - 2; start++)
        {
            if (parts[start] == 0) continue;
            string rest = lower[start..];
            foreach (var (word, _) in _dictionary.Complete(rest))
            {
                if (word.Length <= rest.Length || CompoundPartFrequency(word.ToLowerInvariant()) is not (> 0 and var freq)) continue;
                yield return (char.ToUpperInvariant(lower[0]) + lower[1..start] + word.ToLowerInvariant(),
                    freq * CompletionWeight * CompoundCompletionWeight);
            }
        }
    }

    /// <summary>
    /// Für jede Stelle i: aus wie vielen Substantiven (mit Fugen-s) der Anfang lower[..i] besteht; 0 = nicht zerlegbar
    /// oder schon <see cref="MaxCompoundParts"/> Glieder.
    /// </summary>
    int[] CompoundPrefixes(string lower)
    {
        int n = lower.Length;
        var parts = new int[n + 1];
        for (int end = CompoundPartLength; end < n; end++)
        {
            for (int start = 0; start <= end - CompoundPartLength; start++)
            {
                if (start > 0 && parts[start] == 0) continue;
                if (CompoundPartFrequency(lower[start..end]) == 0) continue;
                int count = (start > 0 ? parts[start] : 0) + 1;
                if (count >= MaxCompoundParts) continue;
                if (parts[end] == 0 || count < parts[end]) parts[end] = count;
                if (lower[end] == 's' && end + 1 < n && (parts[end + 1] == 0 || count < parts[end + 1])) parts[end + 1] = count; // Fugen-s
            }
        }

        return parts;
    }

    /// <summary>Häufigkeit, wenn <paramref name="part"/> als Glied taugt (gängiges, nur großgeschriebenes Substantiv), sonst 0.</summary>
    int CompoundPartFrequency(string part)
    {
        if (part.Length < CompoundPartLength || NounOnly(part) == null) return 0;
        int freq = _dictionary.Lookup(part)!.Value.Freq;
        return freq >= _dictionary.TotalFrequency * MinPartShare ? freq : 0;
    }

    /// <summary>
    /// Zwei Wörter ohne Leerzeichen dazwischen ("ichhabe" → "ich habe"): beide müssen gängige Wörter sein und im
    /// Sprachmodell aufeinander folgen. Bewertung wie eine Korrektur mit einem fehlenden Zeichen.
    /// </summary>
    (string Text, double Score)? Split(string lower, WordContext context)
    {
        if (lower.Length < 3 || !lower.All(char.IsLetter) || _dictionary.TotalFrequency == 0) return null;

        double total = _dictionary.TotalFrequency;
        (string Text, double Score)? best = null;
        for (int cut = 1; cut < lower.Length; cut++)
        {
            if (SplitPart(lower[..cut]) is not { } first || SplitPart(lower[cut..]) is not { } second) continue;

            var afterFirst = new WordContext(first.Word, context.Previous);
            double together = _model.ContextFactor(afterFirst, second.Word);
            if (together <= 1.0) continue; // im Sprachmodell nie hintereinander gesehen

            // "vielen dank" → "vielen Dank": die Schreibweise, die nach dem ersten Wort üblich ist
            string secondForm = _dictionary.Forms(second.Word)
                .Select(form => (Form: form, P: _model.FormProbability(afterFirst, form)))
                .Where(f => f.P > 0).OrderByDescending(f => f.P).Select(f => f.Form).FirstOrDefault() ?? second.Word;

            double score = first.Freq * second.Freq / total * Math.Min(MaxContextFactor, together) / Math.Pow(DistancePenaltyBase, MissingSpaceCost);
            if (best == null || score > best.Value.Score) best = (first.Word + " " + secondForm, score);
        }

        return best;

        // Ein Teil muss ein gängiges Wort sein; ein einzelner Buchstabe nur, wenn er ein sehr häufiges Wort ist ("I", "a").
        (string Word, double Freq)? SplitPart(string part)
        {
            if (_dictionary.Lookup(part) is not { } entry) return null;
            double share = entry.Freq / total;
            return share >= (part.Length == 1 ? MinSingleLetterShare : MinPartShare) ? (entry.Word, entry.Freq) : null;
        }
    }

    /// <summary>
    /// Leertaste mitten im Wort: statt eines Buchstabens der untersten Reihe ("da ke" für "danke") oder zusätzlich
    /// ("ha llo" für "hallo"). Prüft beim Abschluss des zweiten Stücks, ob beide Stücke zusammen ein
    /// gängiges Wort ergeben, das besser passt, als das zweite Stück für sich zu korrigieren.
    /// </summary>
    /// <param name="previousTyped">Das Stück vor dem Leerzeichen, wie es getippt wurde (vor einer Autokorrektur).</param>
    /// <param name="typed">Das Stück nach dem Leerzeichen.</param>
    /// <param name="context">Satzkontext vor dem ersten Stück.</param>
    /// <returns>Das zusammengesetzte Wort mit Bewertung, oder null.</returns>
    public (string Word, double Score)? JoinAcrossSpace(
        string previousTyped, string typed, WordContext context, KeyProximity? proximity = null)
    {
        string first = previousTyped.ToLowerInvariant(), second = typed.ToLowerInvariant();
        if (first.Length == 0 || second.Length == 0 || first.Length + second.Length < MinJoinedLength) return null;
        if (!first.All(char.IsLetter) || !second.All(char.IsLetter) || _dictionary.TotalFrequency == 0) return null;

        // Zwei richtige Wörter bleiben, wie sie sind – auch wenn sie zusammen ein drittes ergäben ("ein mal").
        double minFrequency = _dictionary.TotalFrequency * MinPartShare;
        bool firstIsWord = IsCommonWord(first);
        bool secondIsWord = IsCommonWord(second) || _history.CountOf(second) > 0 || Compound(second) != null;
        if (firstIsWord && secondIsWord) return null;

        (string Word, double Score)? best = null;
        void Consider(string joined)
        {
            if (_dictionary.Lookup(joined) is not { } entry || entry.Freq < minFrequency) return;
            double score = entry.Freq * Math.Min(MaxContextFactor, _model.ContextFactor(context, entry.Word))
                / Math.Pow(DistancePenaltyBase, MissingSpaceCost);
            if (best == null || score > best.Value.Score) best = (entry.Word, score);
        }

        Consider(first + second);
        foreach (char letter in (proximity ?? _proximity).BottomRowLetters) Consider(first + letter + second);
        if (best == null) return null;

        // Die Alternative: Nur das Stück, das kein Wort ist, wird für sich korrigiert. Sind beide keine Wörter,
        // müssten beide korrigiert werden; das ist unwahrscheinlicher.
        double aloneScore = secondIsWord
            ? Analyze(previousTyped, context, proximity).Correction?.Score ?? 0
            : Analyze(typed, new WordContext(previousTyped, context.Previous), proximity).Correction?.Score ?? 0;
        if (!firstIsWord && !secondIsWord) aloneScore /= DistancePenaltyBase;

        return best.Value.Score > aloneScore ? (MatchCase(best.Value.Word, previousTyped), best.Value.Score) : null;
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

    /// <summary>
    /// Bei Wörtern mit beiden Schreibweisen ("frage"/"Frage", "essen"/"Essen") die großgeschriebene, wenn sie an
    /// dieser Stelle im Satz klar wahrscheinlicher ist ("eine Frage", "das Essen", "vielen Dank").
    /// </summary>
    string? NounInContext(string lower, WordContext context)
    {
        if (context.Previous == null) return null;
        var forms = _dictionary.Forms(lower);
        if (forms.Count < 2 || !forms.Contains(lower)) return null;

        string? best = null;
        double bestProbability = _model.FormProbability(context, lower) * NounContextRatio;
        if (bestProbability <= 0) return null;
        foreach (var form in forms)
        {
            if (form == lower || !char.IsUpper(form[0])) continue;
            double probability = _model.FormProbability(context, form);
            if (probability >= bestProbability) (best, bestProbability) = (form, probability);
        }

        return best;
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
