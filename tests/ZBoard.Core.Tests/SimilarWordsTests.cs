using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>
/// <see cref="WordDictionary.FindSimilar(string, float, Func{int, char, char, float}, float)"/> rechnet nur einen
/// Streifen der Tabelle und überspringt ganze Bereiche des Wörterbuchs. Hier wird es gegen die einfache Rechnung
/// geprüft: für jedes Wort die ganze Tabelle, ohne Abkürzungen.
/// </summary>
public class SimilarWordsTests
{
    static readonly string[] Words =
    [
        "a", "ab", "abend", "aber", "abends", "an", "das", "dass", "den", "der", "die", "don't", "dont", "du", "essen",
        "fur", "für", "geht", "geht's", "gehts", "hallo", "halle", "halt", "haus", "häuser", "i", "i'm", "ich", "im",
        "in", "ist", "l'", "l'amico", "o'clock", "oclock", "rock'n'roll", "rocknroll", "sad", "straße", "strasse",
        "tag", "tage", "tagen", "test", "teste", "tester", "testen", "zusammen", "zusammenhang", "zusammenhalt",
        "zusammenhänge", "zusammenhängt", "öl", "über", "übermorgen",
    ];

    static WordDictionary Dictionary()
    {
        var dictionary = new WordDictionary();
        for (int i = 0; i < Words.Length; i++) dictionary.Add(Words[i], 10 + i);
        dictionary.BuildTopLists();
        return dictionary;
    }

    /// <summary>Die Distanz nach derselben Vorschrift, aber mit der ganzen Tabelle für ein einzelnes Wort.</summary>
    static float Distance(string target, string key, Func<int, char, char, float> substitutionCost, float transpositionCost)
    {
        int n = target.Length;
        var rows = new float[key.Length + 1][];
        for (int d = 0; d <= key.Length; d++) rows[d] = new float[n + 1];
        for (int j = 0; j <= n; j++) rows[0][j] = j;

        for (int d = 1; d <= key.Length; d++)
        {
            char ch = key[d - 1];
            float deletion = ch == '\'' ? WordDictionary.MissingApostropheCost : 1;
            rows[d][0] = rows[d - 1][0] + deletion;
            for (int j = 1; j <= n; j++)
            {
                float insert = rows[d][j - 1] + 1;
                float delete = rows[d - 1][j] + deletion;
                float replace = rows[d - 1][j - 1] + (target[j - 1] == ch ? 0 : substitutionCost(j - 1, target[j - 1], ch));
                float v = Math.Min(Math.Min(insert, delete), replace);
                if (d >= 2 && j >= 2 && target[j - 1] == key[d - 2] && target[j - 2] == ch)
                {
                    v = Math.Min(v, rows[d - 2][j - 2] + transpositionCost);
                }

                rows[d][j] = v;
            }
        }

        return rows[key.Length][n];
    }

    static List<(string Word, float Distance)> Expected(
        string input, float maxDistance, Func<int, char, char, float> substitutionCost, float transpositionCost)
    {
        // Länger als die Eingabe darf ein Wort um die erlaubten Einfügungen sein – und um einen Apostroph
        float whole = MathF.Floor(maxDistance);
        int maxLength = input.Length + (int)whole + (maxDistance - whole >= WordDictionary.MissingApostropheCost ? 1 : 0);
        return [.. Words.Order(StringComparer.Ordinal)
            .Where(word => word.Length <= maxLength)
            .Select(word => (Word: word, Distance: Distance(input, word, substitutionCost, transpositionCost)))
            .Where(r => r.Distance <= maxDistance)];
    }

    static IEnumerable<string> Inputs()
    {
        var random = new Random(5);
        const string Letters = "abdehilmnorstuäöüß'";
        foreach (string word in Words)
        {
            yield return word;
            yield return word.Replace("'", string.Empty);
            for (int k = 0; k < 6; k++)
            {
                var chars = word.ToList();
                for (int edits = random.Next(1, 4); edits > 0; edits--)
                {
                    int at = random.Next(chars.Count + 1);
                    switch (random.Next(4))
                    {
                        case 0: chars.Insert(at, Letters[random.Next(Letters.Length)]); break;
                        case 1 when chars.Count > 1: chars.RemoveAt(Math.Min(at, chars.Count - 1)); break;
                        case 2 when chars.Count > 0: chars[Math.Min(at, chars.Count - 1)] = Letters[random.Next(Letters.Length)]; break;
                        case 3 when chars.Count > 1:
                            int first = Math.Min(at, chars.Count - 2);
                            (chars[first], chars[first + 1]) = (chars[first + 1], chars[first]);
                            break;
                    }
                }

                if (chars.Count > 0) yield return new string([.. chars]);
            }
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(1f)]
    [InlineData(1.3f)]
    [InlineData(2f)]
    [InlineData(2.6f)]
    [InlineData(3f)]
    public void FindSimilar_MatchesTheFullTable(float maxDistance)
    {
        var dictionary = Dictionary();
        var costs = new Func<int, char, char, float>[]
        {
            (_, _, _) => 1f,
            (_, a, b) => KeyProximity.BaseLetter(a) == KeyProximity.BaseLetter(b) ? 0.3f : Math.Abs(a - b) == 1 ? 0.5f : 1f,
            (position, a, b) => (a + b + position) % 4 * 0.25f, // auch Kosten 0, je nach Stelle verschieden
        };

        foreach (string input in Inputs())
        {
            foreach (var cost in costs)
            {
                foreach (float transposition in new[] { 0.6f, 0f, 1f })
                {
                    var expected = Expected(input, maxDistance, cost, transposition);
                    var actual = dictionary.FindSimilar(input, maxDistance, cost, transposition)
                        .Select(r => (r.Word, r.Distance)).ToList();
                    Assert.True(expected.SequenceEqual(actual),
                        $"„{input}“ bis {maxDistance}: erwartet [{string.Join(", ", expected)}], erhalten [{string.Join(", ", actual)}]");
                }
            }
        }
    }

    [Fact]
    public void FindSimilar_ShortAndLongWordsAreNotConfused()
    {
        var dictionary = Dictionary();

        // Kurze Wörter liegen weit von einer langen Eingabe entfernt (und umgekehrt), auch wenn sie so beginnen.
        Assert.Equal(["zusammenhalt", "zusammenhang"], dictionary.FindSimilar("zusammenhagn", 2f, (_, _) => 1f).Select(r => r.Word));
        Assert.Empty(dictionary.FindSimilar("a", 0f, (_, _) => 1f).Where(r => r.Word != "a"));
        Assert.DoesNotContain("zusammen", dictionary.FindSimilar("zusammenhang", 2f, (_, _) => 1f).Select(r => r.Word));
    }

    [Fact]
    public void FindSimilar_VeryLongInput()
    {
        var dictionary = new WordDictionary();
        string longWord = new('a', 300);
        dictionary.Add(longWord, 5);
        dictionary.Add(longWord + "b", 4);
        dictionary.Add("abc", 3);
        dictionary.BuildTopLists();

        var found = dictionary.FindSimilar(longWord[..299] + "c", 1f, (_, _) => 1f);

        Assert.Equal([(longWord, 5, 1f)], found);
    }

    [Fact]
    public void FindSimilar_AsksForEachSubstitutionCostOnlyOnce()
    {
        var dictionary = Dictionary();
        var asked = new HashSet<(int, char, char)>();

        dictionary.FindSimilar("zusamenhang", 2f, (position, typed, candidate) =>
        {
            Assert.True(asked.Add((position, typed, candidate)), $"zweimal gefragt: {position} {typed} {candidate}");
            return 1f;
        });

        Assert.NotEmpty(asked);
    }
}
