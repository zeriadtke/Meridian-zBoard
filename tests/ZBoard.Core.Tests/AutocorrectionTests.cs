using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>
/// Autokorrektur von Vertippern, die zufällig als seltener Eintrag in der Wortliste stehen, kurze Wörter, Abkürzungen,
/// fehlende Leerzeichen und die Großschreibung der Korrektur. Häufigkeiten wie in Assets/dict/de.txt und en.txt.
/// </summary>
public class AutocorrectionTests
{
    static SuggestionEngine Engine(string language, NgramModel? model, params (string Word, int Freq)[] words)
    {
        var dictionary = new WordDictionary { Language = language };
        foreach (var (word, freq) in words) dictionary.Add(word, freq);
        dictionary.BuildTopLists();
        return new SuggestionEngine(
            dictionary, KeyProximity.FromLayout(Layouts.Alphabet(language, showLanguageKey: false)), model, new UserHistory());
    }

    static readonly (string, int)[] German =
    [
        ("ich", 5_890_279), ("IC", 31), ("ist", 3_025_610), ("Ost", 430), ("das", 2_400_000), ("für", 1_200_000),
        ("dich", 688_780), ("dic", 62), ("ganz", 300_000), ("fertig", 53_738), ("fettig", 140), ("gut", 500_000),
        ("treffen", 38_758), ("Treffen", 23_254), ("auf", 881_418), ("ab", 200_000), ("an", 900_000),
    ];

    /// <summary>Viele andere Folgen, damit einzelne Folgen nicht schon für sich als "typisch" gelten.</summary>
    static NgramModel.Builder Background() => new NgramModel.Builder()
        .Add("es", "ist", 5000).Add("ist", "gut", 3000).Add("ich", "bin", 4000).Add("das", "auto", 2000);

    [Fact]
    public void TwoLetters_GetCorrected_AndCompletionsStayInTheBar()
    {
        var engine = Engine("de", null, German);

        var result = engine.Suggest("ic");

        Assert.Equal("ich", result.AutoCorrection);      // "IC" ist eine Abkürzung, klein getippt ein Vertipper
        Assert.Contains("ich", result.Candidates);
    }

    [Fact]
    public void Acronym_TypedInCapitals_Stays()
    {
        Assert.Null(Engine("de", null, German).Suggest("IC").AutoCorrection);
    }

    [Fact]
    public void CapitalizedEntry_StillProtected()
    {
        // "Ost" ist ein geprüftes Substantiv: "ost" wird nur großgeschrieben, nicht zum viel häufigeren "ist".
        Assert.Equal("Ost", Engine("de", null, German).Suggest("ost", new WordContext("das")).AutoCorrection);
    }

    [Fact]
    public void RareEntry_CorrectedWhenTheSentenceFits()
    {
        var model = Background().Add("für", "dich", 800).Build();
        var withSentence = Engine("de", model, German);
        var withoutSentence = Engine("de", null, German);

        Assert.Equal("dich", withSentence.Suggest("dic", new WordContext("für")).AutoCorrection);
        Assert.Null(withoutSentence.Suggest("dic", new WordContext("für")).AutoCorrection); // ohne Satz zu unsicher
    }

    [Fact]
    public void RareRealWord_StaysEvenIfTheSentenceFavorsTheOther()
    {
        // "fertig" ist nur etwa 400-mal häufiger als "fettig": unter RareWordContextRatio, das Wort bleibt.
        var model = Background().Add("ganz", "fertig", 2000).Build();

        Assert.Null(Engine("de", model, German).Suggest("fettig", new WordContext("ganz")).AutoCorrection);
    }

    [Fact]
    public void Correction_TakesTheCaseThatFitsTheSentence()
    {
        var model = Background().Add("das", "Treffen", 400).Add("das", "treffen", 5).Build();

        Assert.Equal("Treffen", Engine("de", model, German).Suggest("trwffen", new WordContext("das")).AutoCorrection);
    }

    [Fact]
    public void MissingSpace_BeatsDroppingAWord()
    {
        // "fora" steht als seltener Eintrag in der Wortliste; "for" allein wäre häufiger, gemeint ist "for a".
        var model = new NgramModel.Builder()
            .Add("for", "a", 3000).Add("is", "a", 2000).Add("it", "is", 3000).Add("of", "the", 5000).Build();
        var engine = Engine("en", model,
            ("for", 1_500_000), ("a", 4_000_000), ("fora", 120), ("the", 8_000_000), ("is", 3_000_000), ("it", 3_000_000),
            ("of", 3_500_000), ("me", 2_000_000));

        Assert.Equal("for a", engine.Suggest("fora", new WordContext("me")).AutoCorrection);
    }
}
