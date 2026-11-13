using System.Globalization;
using System.Numerics;
using ZBoard.Keyboard;
using ZBoard.Suggestions;
using ZBoard.Tools.SuggestionEval;

// Misst Wortvorschläge und Autokorrektur mit simuliertem Tippen über einen Testtext. Aufruf und Kennzahlen: README.md.

var options = Options.Parse(args);
if (options == null) return 1;

string root = options.Root ?? FindRepositoryRoot()
    ?? throw new InvalidOperationException("zBoard-Ordner nicht gefunden; bitte --root angeben.");
string dictDir = Path.Combine(root, "Assets", "dict");
string textPath = options.Text ?? Path.Combine(root, "tools", "SuggestionEval", "samples", $"{options.Language}.txt");
if (!File.Exists(textPath))
{
    Console.Error.WriteLine($"Testtext nicht gefunden: {textPath} (eigenen Text mit --text angeben)");
    return 1;
}

WordDictionary dictionary;
using (var stream = File.OpenRead(Path.Combine(dictDir, $"{options.Language}.txt")))
{
    dictionary = WordDictionary.Load(stream, WordDictionary.ReferenceTotal);
}

var model = NgramModel.Empty;
string modelPath = options.Model ?? Path.Combine(dictDir, $"{options.Language}.ngrams.bin");
if (File.Exists(modelPath))
{
    using var stream = File.OpenRead(modelPath);
    model = NgramModel.Load(stream);
}

var layout = Layouts.Alphabet(options.Language, showLanguageKey: false);
var engine = new SuggestionEngine(dictionary, KeyProximity.FromLayout(layout), model, new UserHistory());
var words = Corpus.Load(textPath);
if (words.Count == 0)
{
    Console.Error.WriteLine("Der Testtext enthält keine Wörter.");
    return 1;
}

WordContext Context(TestWord word) => options.UseContext ? word.Context : WordContext.None;
TouchTrace? Touches(Vector2?[] points, IReadOnlyDictionary<char, Vector2> keys, int count) =>
    options.UseTouch ? new TouchTrace(points[..count], keys) : null;

// ---------- 1. Vorschläge bei fehlerfreiem Tippen ----------

int predictedTop1 = 0, predictedTop3 = 0;
int afterWord = 0, afterWordTop3 = 0; // nur Stellen mit einem Wort davor (ohne Satzanfang und Komma)
int halfWords = 0, halfTop1 = 0, halfTop3 = 0;
long keysNeeded = 0, keysBaseline = 0;
int unknown = 0;

var cleanTyping = new TypingNoise(layout, options.Seed - 1) { Sigma = options.Sigma };
foreach (var word in words)
{
    string target = word.Target, intended = word.Intended;
    if (!engine.InDictionary(target)) unknown++;
    var cleanPoints = cleanTyping.PointsFor(intended);

    int cost = intended.Length + 1; // ausschreiben und Leertaste
    if (options.UseContext)
    {
        // Am Satzanfang ist die Umschalttaste aktiv: Die Leiste zeigt die Vorhersagen großgeschrieben.
        var predictions = engine.Predict(word.Context, 3)
            .Select(p => word.SentenceStart ? char.ToUpperInvariant(p[0]) + p[1..] : p).ToList();
        bool hit = predictions.Contains(target);
        if (predictions.Count > 0 && predictions[0] == target) predictedTop1++;
        if (hit)
        {
            predictedTop3++;
            cost = 1; // antippen; das Leerzeichen kommt von selbst
        }

        if (word.Context.PreviousWord != null)
        {
            afterWord++;
            if (hit) afterWordTop3++;
        }
    }

    for (int typedLetters = 1; typedLetters < intended.Length; typedLetters++)
    {
        var bar = Bar.Of(engine.Suggest(
            intended[..typedLetters], Context(word), touches: Touches(cleanPoints, cleanTyping.Keys, typedLetters)));
        bool visible = bar.Contains(target);
        if (visible && cost > typedLetters + 1) cost = typedLetters + 1;

        if (intended.Length >= 4 && typedLetters == (intended.Length + 1) / 2)
        {
            halfWords++;
            if (bar.Middle == target) halfTop1++;
            if (visible) halfTop3++;
        }
    }

    keysNeeded += cost;
    keysBaseline += intended.Length + 1;
}

// ---------- 2. Autokorrektur bei ungenauem Tippen ----------

int wrongJoins = 0;
var noisy = new Outcome();
var clean = new Outcome();
List<string> examples = [];

for (int run = 0; run < options.Runs; run++)
{
    var noise = new TypingNoise(layout, options.Seed + run) { Sigma = options.Sigma, LazyAccentRate = options.Lazy };
    string? previousTyped = null;
    foreach (var word in words)
    {
        string target = word.Target, intended = word.Intended;
        var (typed, points) = noise.Type(intended);
        bool typo = !string.Equals(typed, intended, StringComparison.OrdinalIgnoreCase);

        var result = engine.Suggest(typed, Context(word), touches: Touches(points, noise.Keys, points.Length));
        string final = result.AutoCorrection ?? typed;

        // Fehlalarm der Leertasten-Regel: Zwei Wörter dürfen nicht verschmelzen, auch wenn eines vertippt ist –
        // weder das richtige vorherige mit diesem noch das vertippte vorherige mit dem richtigen hier.
        if (word.Context.PreviousWord is { } before)
        {
            var beforeContext = new WordContext(word.Context.BeforePrevious);
            var wronglyJoined = engine.JoinAcrossSpace(before, typed, beforeContext)
                ?? (previousTyped is { } beforeTyped ? engine.JoinAcrossSpace(beforeTyped, intended, beforeContext) : null);
            if (wronglyJoined != null)
            {
                final = "⟨" + wronglyJoined.Value.Word + "⟩";
                wrongJoins++;
            }
        }

        previousTyped = typed;
        var outcome = typo ? noisy : clean;
        outcome.Total++;

        if (final == target)
        {
            outcome.Correct++;
            continue;
        }

        string kind;
        if (string.Equals(final, target, StringComparison.OrdinalIgnoreCase)) (outcome.CaseOnly, kind) = (outcome.CaseOnly + 1, "Großschreibung");
        else if (Bar.Of(result).Contains(target)) (outcome.InBar, kind) = (outcome.InBar + 1, "in der Leiste");
        else if (final != typed) (outcome.WrongWord, kind) = (outcome.WrongWord + 1, "falsches Wort");
        else if (result.TypedIsWord) (outcome.RealWord, kind) = (outcome.RealWord + 1, "echtes Wort");
        else (outcome.Untouched, kind) = (outcome.Untouched + 1, "unverändert");

        if (options.Verbose && examples.Count < 400)
        {
            examples.Add($"{(typo ? "Tippfehler" : "richtig"),-10}  {kind,-14}  {word.Context.Previous ?? "–",-14} {typed,-18} → {final,-18} (gewollt: {target})");
        }
    }
}

// ---------- 3. Fehlendes Leerzeichen ----------

// Je zwei aufeinanderfolgende Wörter ohne Leerzeichen dazwischen getippt, sonst fehlerfrei.
int joined = 0, joinedSplit = 0, joinedOther = 0;
for (int i = 0; i + 1 < words.Count; i++)
{
    var (first, second) = (words[i], words[i + 1]);
    if (second.Context.PreviousWord != first.Target || second.Context.BeforePrevious != first.Context.Previous) continue;

    string typed = first.Intended + second.Intended;
    string wanted = first.Target + " " + second.Target;
    var result = engine.Suggest(typed, Context(first));
    string final = result.AutoCorrection ?? typed;
    joined++;
    if (final == wanted) joinedSplit++;
    else if (final != typed && !string.Equals(final, wanted, StringComparison.OrdinalIgnoreCase)) joinedOther++;

    if (options.Verbose && final != wanted && examples.Count < 480)
    {
        examples.Add($"{"Leerzeichen",-10}  {"",-14}  {first.Context.Previous ?? "–",-14} {typed,-18} → {final,-18} (gewollt: {wanted})");
    }
}

// ---------- 4. Leertaste mitten im Wort ----------

// Die Leertaste trifft statt eines Buchstabens der untersten Reihe ("da ke") oder zusätzlich ("dan ke").
var bottomRow = KeyProximity.FromLayout(layout).BottomRowLetters;
var spaceRandom = new Random(options.Seed);
int insteadOf = 0, insteadOfFixed = 0, extra = 0, extraFixed = 0;
foreach (var word in words)
{
    string target = word.Target, intended = word.Intended;
    if (intended.Length < 5 || !intended.All(char.IsLetter)) continue;

    string Typed(string first, string second)
    {
        if (engine.JoinAcrossSpace(first, second, Context(word)) is { } joinedWord) return joinedWord.Word;
        string firstFinal = engine.Suggest(first, Context(word)).AutoCorrection ?? first;
        string secondFinal = engine.Suggest(second, new WordContext(firstFinal, word.Context.Previous)).AutoCorrection ?? second;
        return firstFinal + " " + secondFinal;
    }

    int cut = Enumerable.Range(2, intended.Length - 4).Where(i => bottomRow.Contains(char.ToLowerInvariant(intended[i])))
        .DefaultIfEmpty(-1).First();
    if (cut > 0)
    {
        insteadOf++;
        if (Typed(intended[..cut], intended[(cut + 1)..]) == target) insteadOfFixed++;
    }

    int at = spaceRandom.Next(2, intended.Length - 1);
    extra++;
    string result = Typed(intended[..at], intended[at..]);
    if (result == target) extraFixed++;
    else if (options.Verbose && examples.Count < 520)
    {
        examples.Add($"{"Leertaste",-10}  {"",-14}  {word.Context.Previous ?? "–",-14} {intended[..at] + " " + intended[at..],-18} → {result,-18} (gewollt: {target})");
    }
}

// ---------- Ausgabe ----------

// Deutsche Schreibweise von Hand, damit die Ausgabe nicht von den Spracheinstellungen des Rechners abhängt.
string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture).Replace('.', ',');
string Pct(long part, long total) => total == 0 ? "–" : Number(100.0 * part / total, "0.0") + " %";

int all = noisy.Total + clean.Total;
int wrongAfter = all - noisy.Correct - clean.Correct;
int wrongAfterIgnoringCase = wrongAfter - noisy.CaseOnly - clean.CaseOnly;

Console.WriteLine($"zBoard Vorschlagsmessung – Sprache {options.Language}, {words.Count} Wörter aus {Path.GetFileName(textPath)}");
Console.WriteLine($"Wörterbuch {dictionary.Count} Einträge, davon unbekannt im Text: {Pct(unknown, words.Count)}"
    + (options.UseContext ? "" : " – ohne Satzkontext"));
Console.WriteLine();
Console.WriteLine("| Vorschläge (fehlerfrei getippt) | Wert |");
Console.WriteLine("| --- | --- |");
Console.WriteLine($"| Nächstes Wort vorhergesagt, Platz 1 | {Pct(predictedTop1, words.Count)} |");
Console.WriteLine($"| Nächstes Wort vorhergesagt, unter den ersten 3 | {Pct(predictedTop3, words.Count)} |");
Console.WriteLine($"| … nur nach einem Wort (ohne Satzanfang und Komma) | {Pct(afterWordTop3, afterWord)} |");
Console.WriteLine($"| Wort nach der Hälfte der Buchstaben in der Mitte der Leiste | {Pct(halfTop1, halfWords)} |");
Console.WriteLine($"| Wort nach der Hälfte der Buchstaben in der Leiste | {Pct(halfTop3, halfWords)} |");
Console.WriteLine($"| Gesparte Tastendrücke | {Pct(keysBaseline - keysNeeded, keysBaseline)} |");
Console.WriteLine();
Console.WriteLine($"| Autokorrektur ({options.Runs} Durchläufe, Streuung {Number(options.Sigma, "0.00")}) | Wert |");
Console.WriteLine("| --- | --- |");
Console.WriteLine($"| Wörter mit Tippfehler | {Pct(noisy.Total, all)} ({noisy.Total}) |");
Console.WriteLine($"| … davon richtig korrigiert | {Pct(noisy.Correct, noisy.Total)} |");
Console.WriteLine($"| … nur Groß-/Kleinschreibung falsch | {Pct(noisy.CaseOnly, noisy.Total)} |");
Console.WriteLine($"| … nicht korrigiert, richtiges Wort steht in der Leiste | {Pct(noisy.InBar, noisy.Total)} |");
Console.WriteLine($"| … in ein falsches Wort korrigiert | {Pct(noisy.WrongWord, noisy.Total)} |");
Console.WriteLine($"| … Tippfehler ergibt ein anderes echtes Wort | {Pct(noisy.RealWord, noisy.Total)} |");
Console.WriteLine($"| … unverändert stehen gelassen | {Pct(noisy.Untouched, noisy.Total)} |");
Console.WriteLine($"| Richtig getippte Wörter, die verändert wurden | {Pct(clean.WrongWord + clean.InBar, clean.Total)} |");
Console.WriteLine($"| Richtig getippte Wörter mit falscher Groß-/Kleinschreibung | {Pct(clean.CaseOnly, clean.Total)} |");
Console.WriteLine($"| **Falsche Wörter im Text nach der Korrektur** | **{Pct(wrongAfter, all)}** |");
Console.WriteLine($"| … ohne reine Groß-/Kleinschreibung | {Pct(wrongAfterIgnoringCase, all)} |");

Console.WriteLine();
Console.WriteLine($"| Fehlendes Leerzeichen ({joined} Wortpaare, sonst fehlerfrei getippt) | Wert |");
Console.WriteLine("| --- | --- |");
Console.WriteLine($"| In die beiden Wörter getrennt | {Pct(joinedSplit, joined)} |");
Console.WriteLine($"| In etwas anderes korrigiert | {Pct(joinedOther, joined)} |");

Console.WriteLine();
Console.WriteLine("| Leertaste mitten im Wort (sonst fehlerfrei getippt) | Wert |");
Console.WriteLine("| --- | --- |");
Console.WriteLine($"| Leertaste statt Buchstabe der untersten Reihe: Wort wiederhergestellt | {Pct(insteadOfFixed, insteadOf)} ({insteadOf}) |");
Console.WriteLine($"| Zusätzliche Leertaste: Wort wiederhergestellt | {Pct(extraFixed, extra)} ({extra}) |");
Console.WriteLine($"| Fehlalarm: zwei richtige Wörter verschmolzen (Durchläufe mit Tippfehlern) | {Pct(wrongJoins, all)} |");

if (options.Verbose)
{
    Console.WriteLine();
    Console.WriteLine("Fehlerbeispiele (Art, vorheriges Wort, getippt → Ergebnis):");
    foreach (var line in examples) Console.WriteLine("  " + line);
}

return 0;

static string? FindRepositoryRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Assets", "dict"))) return dir.FullName;
        }
    }

    return null;
}

namespace ZBoard.Tools.SuggestionEval
{
    /// <summary>Zählt, was aus den Wörtern einer Gruppe (mit oder ohne Tippfehler) nach dem Leerzeichen geworden ist.</summary>
    sealed class Outcome
    {
        public int Total, Correct, CaseOnly, InBar, WrongWord, RealWord, Untouched;
    }

    /// <summary>
    /// Was die Vorschlagsleiste zu einem Ergebnis zeigt – dieselbe Anordnung wie <c>UpdateSuggestions</c> im
    /// Tastaturdienst (ohne gelernte Wortfolgen): bei einer Autokorrektur links das Getippte, in der Mitte die
    /// Korrektur, rechts eine Alternative; sonst die drei besten Vorschläge, der beste in der Mitte.
    /// </summary>
    sealed record Bar(string? Left, string? Middle, string? Right)
    {
        public bool Contains(string word) => Left == word || Middle == word || Right == word;

        public static Bar Of(SuggestionResult result)
        {
            var candidates = result.Candidates;
            if (result.AutoCorrection is { } correction)
            {
                return new Bar(result.Typed, correction, candidates.FirstOrDefault(c => c != correction && c != result.Typed));
            }

            string? At(int i) => i < candidates.Count ? candidates[i] : null;
            string? left = !result.TypedIsWord && !candidates.Contains(result.Typed) ? result.Typed : At(1);
            return new Bar(left, At(0), At(2));
        }
    }

    sealed record Options(
        string Language, string? Text, string? Root, string? Model, int Seed, int Runs, double Sigma, double Lazy, bool UseContext, bool UseTouch, bool Verbose)
    {
        public static Options? Parse(string[] args)
        {
            var options = new Options("de", null, null, null, Seed: 1, Runs: 5, Sigma: 0.24, Lazy: 0.3, UseContext: true, UseTouch: true, Verbose: false);
            for (int i = 0; i < args.Length; i++)
            {
                string? Value() => i + 1 < args.Length ? args[++i] : null;
                switch (args[i])
                {
                    case "--lang" when Value() is { } v: options = options with { Language = v }; break;
                    case "--text" when Value() is { } v: options = options with { Text = v }; break;
                    case "--model" when Value() is { } v: options = options with { Model = v }; break;
                    case "--root" when Value() is { } v: options = options with { Root = v }; break;
                    case "--seed" when Value() is { } v && int.TryParse(v, out var n): options = options with { Seed = n }; break;
                    case "--runs" when Value() is { } v && int.TryParse(v, out var n) && n > 0: options = options with { Runs = n }; break;
                    case "--sigma" when Value() is { } v
                        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0:
                        options = options with { Sigma = s };
                        break;
                    case "--lazy" when Value() is { } v
                        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && l is >= 0 and <= 1:
                        options = options with { Lazy = l };
                        break;
                    case "--no-touch": options = options with { UseTouch = false }; break;
                    case "--no-context": options = options with { UseContext = false }; break;
                    case "--verbose": options = options with { Verbose = true }; break;
                    default:
                        Console.Error.WriteLine(
                            """
                            Aufruf: dotnet run -c Release --project tools/SuggestionEval -- [Optionen]
                              --lang de|en|fr|es|it   Sprache (Wörterbuch, Sprachmodell, Layout); Vorgabe de
                              --text <Datei>          eigener Testtext, ein Satz pro Zeile (Vorgabe: samples/<Sprache>.txt)
                              --runs <n>              Durchläufe mit unterschiedlichen Tippfehlern; Vorgabe 5
                              --sigma <x>             Streuung der Fingerposition in Tastenbreiten; Vorgabe 0.24
                              --lazy <x>              Anteil der Umlaute/Akzente, die als Grundbuchstabe getippt werden; Vorgabe 0.3
                              --seed <n>              Startwert des Zufallsgenerators; Vorgabe 1
                              --no-touch              ohne Fingerpositionen messen (nur Nachbartasten)
                              --no-context            ohne vorheriges Wort messen
                              --verbose               Fehlerbeispiele ausgeben
                              --model <Datei>         anderes Sprachmodell (Vorgabe: Assets/dict/<Sprache>.ngrams.bin)
                              --root <Ordner>         zBoard-Ordner, falls nicht von dort gestartet
                            """);
                        return null;
                }
            }

            return options;
        }
    }
}
