using System.Text;
using ZBoard.Suggestions;

namespace ZBoard.Tools.SuggestionEval;

/// <summary>Ein Wort des Testtexts mit dem, was die Tastatur an dieser Stelle über den Satz wüsste.</summary>
/// <param name="Target">Das Wort, wie es im Text steht (so soll es am Ende dastehen).</param>
/// <param name="Context">Die Wörter davor, gelesen wie im Tastaturdienst (<see cref="WordContext.FromText"/>).</param>
public sealed record TestWord(string Target, WordContext Context)
{
    /// <summary>Am Satzanfang schaltet die Tastatur von selbst auf Großschreibung.</summary>
    public bool SentenceStart => Context.IsSentenceStart;

    /// <summary>
    /// Was ein Nutzer fehlerfrei tippen würde: mitten im Satz alles klein (die Tastatur soll Substantive selbst
    /// großschreiben), am Satzanfang groß (automatische Umschalttaste).
    /// </summary>
    public string Intended => SentenceStart
        ? char.ToUpperInvariant(Target[0]) + Target[1..].ToLowerInvariant()
        : Target.ToLowerInvariant();
}

public static class Corpus
{
    /// <summary>Liest einen Text (ein Satz oder eine Nachricht pro Zeile, # leitet Kommentare ein) als Folge von Testwörtern.</summary>
    public static List<TestWord> Load(string path)
    {
        List<TestWord> words = [];
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            words.AddRange(Tokenize(line));
        }

        return words;
    }

    /// <summary>Die Wörter einer Zeile. Zahlen und Wörter mit Ziffern zählen als Kontext, werden aber nicht bewertet.</summary>
    public static IEnumerable<TestWord> Tokenize(string line)
    {
        line = line.Replace('’', '\'');
        int pos = 0;
        while (pos < line.Length)
        {
            if (!char.IsLetterOrDigit(line[pos]))
            {
                pos++;
                continue;
            }

            int start = pos;
            while (pos < line.Length && (char.IsLetterOrDigit(line[pos]) || IsInnerMark(line, pos))) pos++;
            string word = line[start..pos];
            if (word.All(c => char.IsLetter(c) || c is '\'' or '-'))
            {
                yield return new TestWord(word, WordContext.FromText(line[..start]));
            }
        }
    }

    /// <summary>Apostroph oder Bindestrich zwischen zwei Buchstaben gehört zum Wort ("don't", "E-Mail").</summary>
    static bool IsInnerMark(string line, int i) =>
        line[i] is '\'' or '-'
        && i > 0 && char.IsLetter(line[i - 1])
        && i + 1 < line.Length && char.IsLetter(line[i + 1]);
}
