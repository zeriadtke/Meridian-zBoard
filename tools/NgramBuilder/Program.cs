using System.Globalization;
using System.Text;
using ZBoard.Suggestions;

// Zählt Zweier- und Dreierfolgen von Wörtern in Satzsammlungen und schreibt daraus ein Sprachmodell für zBoard.
// Aufruf und Quellen: README.md.

string? words = null, output = null;
int maxBigrams = 200_000, maxTrigrams = 150_000;
List<(string Path, double Weight)> sources = [];

for (int i = 0; i < args.Length; i++)
{
    string? Value() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--words" when Value() is { } v: words = v; break;
        case "--out" when Value() is { } v: output = v; break;
        case "--bigrams" when Value() is { } v && int.TryParse(v, out var n): maxBigrams = n; break;
        case "--trigrams" when Value() is { } v && int.TryParse(v, out var n): maxTrigrams = n; break;
        case "--common-voice" when Value() is { } v:
            // Rezept für die Satzsammlung von Common Voice: Alltagssätze zählen am meisten, Wikipedia am wenigsten.
            foreach (var file in Directory.GetFiles(v, "*.txt").Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("singleword", StringComparison.Ordinal)) continue;
                sources.Add((file, name.StartsWith("wiki", StringComparison.Ordinal) ? 0.15
                    : name.StartsWith("europarl", StringComparison.Ordinal) ? 1.0 : 10.0));
            }

            break;
        case "--source" when Value() is { } v:
            // Datei[:Gewicht] – das Gewicht steht hinter dem letzten Doppelpunkt, wenn dort eine Zahl steht
            int colon = v.LastIndexOf(':');
            if (colon > 1 && double.TryParse(v[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
            {
                sources.Add((v[..colon], weight));
            }
            else
            {
                sources.Add((v, 1.0));
            }

            break;
        default:
            words = null;
            i = args.Length;
            break;
    }
}

if (words == null || output == null || sources.Count == 0)
{
    Console.Error.WriteLine(
        """
        Aufruf: dotnet run -c Release --project tools/NgramBuilder -- --words Assets/dict/de.txt --out Assets/dict/de.ngrams.bin
                    --common-voice common-voice/server/data/de [--source Sätze.txt[:Gewicht] …]
                    [--bigrams 200000] [--trigrams 150000]
          --words         Wortliste der Sprache; nur Wörter daraus kommen ins Modell, in ihrer Schreibweise
          --common-voice  Ordner einer Sprache aus der Satzsammlung von Common Voice: alle *.txt darin, Wikipedia mit
                          Gewicht 0.15, Europarl mit 1, alles andere (eingesandte Alltagssätze) mit 10
          --source        Textdatei mit einem Satz pro Zeile; das Gewicht zählt jede Folge daraus mehrfach (oder anteilig)
          --bigrams       so viele Zweierfolgen behalten
          --trigrams      so viele Dreierfolgen behalten
        """);
    return 1;
}

WordDictionary dictionary;
using (var stream = File.OpenRead(words))
{
    dictionary = WordDictionary.Load(stream);
}

var builder = new NgramModel.Builder();
foreach (var (path, weight) in sources)
{
    long lines = 0, counted = 0;
    foreach (var raw in File.ReadLines(path, Encoding.UTF8))
    {
        lines++;
        string line = raw.Replace('’', '\'').Trim();
        if (!line.Contains(' ')) continue; // leer oder ein einzelnes Wort (Listen von Städten u. Ä.)

        int pos = 0;
        while (pos < line.Length)
        {
            if (!IsWordChar(line[pos]))
            {
                pos++;
                continue;
            }

            int start = pos;
            while (pos < line.Length && IsWordChar(line[pos])) pos++;

            // Dieselbe Sicht wie die Tastatur beim Tippen dieses Worts.
            var context = WordContext.FromText(line[..start]);
            if (context.Previous == null) continue;

            string? word = Form(line[start..pos], context.IsSentenceStart);
            string? previous = ContextForm(context.Previous, context.BeforePrevious == NgramModel.SentenceStart);
            if (word == null || previous == null) continue;

            builder.Add(previous, word, weight);
            counted++;
            if (context.BeforePrevious != null && ContextForm(context.BeforePrevious, false) is { } beforePrevious)
            {
                builder.Add(beforePrevious, previous, word, weight);
            }
        }
    }

    Console.WriteLine($"{Path.GetFileName(path)}: {lines} Zeilen, {counted} Wortfolgen, Gewicht {weight.ToString(CultureInfo.InvariantCulture)}");
}

var model = builder.Build(maxBigrams, maxTrigrams);
using (var stream = File.Create(output))
{
    model.Save(stream);
}

Console.WriteLine($"{output}: {model.WordCount} Wörter, {model.BigramCount} Zweierfolgen, {model.TrigramCount} Dreierfolgen, "
    + $"{new FileInfo(output).Length / 1024} KiB");
return 0;

static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-';

string? ContextForm(string token, bool atSentenceStart) =>
    token is NgramModel.SentenceStart or NgramModel.Comma ? token : Form(token, atSentenceStart);

// Schreibweise des Wörterbuchs für ein Wort aus dem Text; null, wenn das Wörterbuch es nicht kennt.
// Am Satzanfang sagt die Großschreibung nichts: Dann gilt bei mehrdeutigen Wörtern ("Frage"/"frage") die häufigere Form.
string? Form(string token, bool atSentenceStart)
{
    var forms = dictionary.Forms(token);
    if (forms.Count == 0) return null;
    if (forms.Count == 1) return forms[0];
    if (!(atSentenceStart && char.IsUpper(token[0])) && forms.Contains(token)) return token;
    return forms[0];
}
