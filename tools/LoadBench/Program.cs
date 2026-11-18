using System.Diagnostics;
using ZBoard.Keyboard;
using ZBoard.Suggestions;

// Aufruf: dotnet run -c Release --project tools/LoadBench -- de en
// Misst das Laden einer Sprache so, wie es ZBoardImeService.LoadEngine tut: Wortliste, Sprachmodell, Engine –
// und danach die Rechenzeit je Tastendruck beim Tippen.
// Jeder Lauf liest die Dateien neu von der Platte. „vorbereitet“ ist die Fassung, die die App nach dem ersten Laden
// im Zwischenspeicher ablegt (WordDictionary.Save / LoadCompiled).

string dictDir = Path.Combine(FindRepoRoot(), "Assets", "dict");
string[] languages = args.Where(a => !a.StartsWith('-')).DefaultIfEmpty("de").ToArray();
int runs = 7;

Console.WriteLine($"{"Sprache",-8}{"Schritt",-16}{"erster Lauf",12}{"Median",10}{"Speicher",12}");
foreach (string language in languages)
{
    Measure(language, "Wortliste", () => LoadDictionary(language));
    Measure(language, "Sprachmodell", () => LoadModel(language));
    Measure(language, "gesamt", () => LoadEngine(language));

    // Zweiter und jeder weitere Start: vorbereitete Fassung aus dem Zwischenspeicher der App
    string compiled = Path.Combine(Path.GetTempPath(), $"zboard-loadbench-{language}.zbwd");
    using (var stream = File.Create(compiled)) LoadDictionary(language).Save(stream);
    Measure(language, "vorbereitet", () => LoadCompiled(compiled, language));
    Measure(language, "gesamt (vorb.)", () => LoadEngine(language, compiled));
    File.Delete(compiled);

    var engine = LoadEngine(language);
    var sw = Stopwatch.StartNew();
    engine.Suggest("ha", WordContext.None);
    engine.Suggest("hallp", WordContext.None);
    Console.WriteLine($"{language,-8}{"1. Vorschlag",-16}{sw.Elapsed.TotalMilliseconds,10:F1}ms");
}

// Rechenzeit je Tastendruck: tippt den Beispieltext der Vorschlagsmessung Buchstabe für Buchstabe (mit Satzkontext),
// je Sprache allein und alle angegebenen Sprachen zusammen (mehrsprachige Eingabe).
var engines = languages.Select(l => (Language: l, Engine: LoadEngine(l))).ToList();
Console.WriteLine();
Console.WriteLine($"{"Tippen",-10}{"Tastendrücke",14}{"Mittel",10}{"95 %",10}{"99 %",10}{"größter",10}");
foreach (var (language, engine) in engines) Typing(language, new MultilingualEngine(language, engine), language);
if (engines.Count > 1) Typing(string.Join("+", languages), new MultilingualEngine(engines), languages[0]);

void Typing(string label, MultilingualEngine engine, string textLanguage)
{
    string sample = Path.Combine(FindRepoRoot(), "tools", "SuggestionEval", "samples", $"{textLanguage}.txt");
    if (!File.Exists(sample)) return;

    var times = new List<double>();
    for (int round = 0; round < 3; round++) // die letzte Runde zählt (davor übersetzt die Laufzeit den Code)
    {
        times.Clear();
        foreach (string line in File.ReadLines(sample))
        {
            var before = new System.Text.StringBuilder();
            foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string letters = word.Trim('.', ',', '!', '?', ';', ':', '"', '(', ')', '„', '“');
                var context = WordContext.FromText(before.ToString());
                for (int length = 1; length <= letters.Length; length++)
                {
                    var sw = Stopwatch.StartNew();
                    engine.Suggest(letters[..length], context);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                before.Append(word).Append(' ');
            }
        }
    }

    times.Sort();
    double At(double share) => times[Math.Min(times.Count - 1, (int)(times.Count * share))];
    Console.WriteLine($"{label,-10}{times.Count,14}{times.Average(),8:F2}ms{At(0.95),8:F2}ms{At(0.99),8:F2}ms{times[^1],8:F1}ms");
}

void Measure(string language, string step, Func<object> action)
{
    var times = new List<double>();
    long bytes = 0;
    for (int i = 0; i < runs; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        object result = action();
        sw.Stop();
        bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
        times.Add(sw.Elapsed.TotalMilliseconds);
        GC.KeepAlive(result);
    }

    double first = times[0];
    times.Sort();
    Console.WriteLine($"{language,-8}{step,-16}{first,10:F1}ms{times[times.Count / 2],8:F1}ms{bytes / 1048576.0,9:F1} MB");
}

WordDictionary LoadDictionary(string language)
{
    using var stream = File.OpenRead(Path.Combine(dictDir, $"{language}.txt"));
    return WordDictionary.Load(stream, WordDictionary.ReferenceTotal, language);
}

NgramModel LoadModel(string language)
{
    string path = Path.Combine(dictDir, $"{language}.ngrams.bin");
    if (!File.Exists(path)) return NgramModel.Empty;
    using var stream = File.OpenRead(path);
    return NgramModel.Load(stream);
}

WordDictionary LoadCompiled(string path, string language)
{
    using var stream = File.OpenRead(path);
    return WordDictionary.LoadCompiled(stream, language);
}

SuggestionEngine LoadEngine(string language, string? compiled = null)
{
    var proximity = KeyProximity.FromLayout(Layouts.Alphabet(language, showLanguageKey: false));
    var dictionary = compiled == null ? LoadDictionary(language) : LoadCompiled(compiled, language);
    return new SuggestionEngine(dictionary, proximity, LoadModel(language), new UserHistory());
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ZBoard.csproj"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("ZBoard.csproj nicht gefunden.");
}
