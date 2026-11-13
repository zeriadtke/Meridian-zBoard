using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>
/// Seltene Wörterbucheinträge, die fast immer Tippfehler oder bequeme Schreibweisen sind ("fur", "dont"), und Wörter
/// mit Apostroph.
/// </summary>
public class RareWordTests
{
    static SuggestionEngine Engine(string language, UserHistory? history, params (string Word, int Freq)[] words)
    {
        var dictionary = new WordDictionary();
        foreach (var (word, freq) in words) dictionary.Add(word, freq);
        dictionary.BuildTopLists();
        var proximity = KeyProximity.FromLayout(Layouts.Alphabet(language, showLanguageKey: false));
        return new SuggestionEngine(dictionary, proximity, history: history);
    }

    static SuggestionEngine German(UserHistory? history = null) => Engine("de", history,
        ("für", 700_000), ("fur", 1_300), ("andere", 100_000), ("ändere", 900), ("fertig", 90_000), ("fettig", 150),
        ("nicht", 3_000_000), ("micht", 10), ("ist", 3_000_000), ("Ost", 400), ("Tür", 40_000), ("tur", 150),
        ("weiß", 600_000), ("weis", 500), ("Straße", 30_000));

    static SuggestionEngine English() => Engine("en", null,
        ("don't", 4_000_000), ("dont", 9_500), ("done", 300_000), ("I'm", 4_400_000), ("im", 6_500),
        ("won't", 290_000), ("wont", 3_700), ("want", 1_500_000), ("its", 180_000), ("it's", 3_000_000),
        ("well", 2_000_000), ("we'll", 400_000), ("john", 50_000), ("parents", 40_000), ("for", 3_000_000), ("fur", 2_000));

    [Theory]
    [InlineData("fur", "für")]
    [InlineData("tur", "Tür")]
    [InlineData("weis", "weiß")]
    public void MissingUmlaut_IsCorrected_EvenIfTheWordListKnowsIt(string typed, string expected)
    {
        Assert.Equal(expected, German().Suggest(typed).AutoCorrection);
    }

    [Fact]
    public void TypedUmlaut_IsDeliberate_AndStays()
    {
        Assert.Null(German().Suggest("ändere").AutoCorrection);
    }

    [Fact]
    public void RareRealWord_NextToFrequentWord_Stays()
    {
        var result = German().Suggest("fettig");

        Assert.Null(result.AutoCorrection);
        Assert.Contains("fertig", result.Candidates);
    }

    [Fact]
    public void VeryRareEntry_NextToVeryFrequentWord_IsCorrected()
    {
        Assert.Equal("nicht", German().Suggest("micht").AutoCorrection);
    }

    [Fact]
    public void CapitalizedEntry_IsNotReplacedByNeighbourKeyWord()
    {
        Assert.Equal("Ost", German().Suggest("ost").AutoCorrection); // nur die Großschreibung, nicht "ist"
    }

    [Fact]
    public void LearnedWord_Stays()
    {
        var history = new UserHistory();
        history.Learn(null, "fur");
        history.Learn(null, "fur");

        Assert.Null(German(history).Suggest("fur").AutoCorrection);
    }

    [Fact]
    public void WordTypedOnceBefore_Stays()
    {
        var history = new UserHistory();
        history.Learn(null, "fur");

        Assert.Null(German(history).Suggest("fur").AutoCorrection);
    }

    [Theory]
    [InlineData("dont", "don't")]
    [InlineData("im", "I'm")]
    [InlineData("wont", "won't")] // die Kurzform, nicht das häufigere "want"
    public void MissingApostrophe_IsCorrected(string typed, string expected)
    {
        Assert.Equal(expected, English().Suggest(typed).AutoCorrection);
    }

    [Theory]
    [InlineData("don't")]
    [InlineData("its")]   // "it's" ist häufiger, aber nicht um Größenordnungen
    [InlineData("well")]
    [InlineData("fur")]   // seltenes echtes Wort neben "for"
    public void CorrectEnglishWords_Stay(string typed)
    {
        Assert.Null(English().Suggest(typed).AutoCorrection);
    }

    [Theory]
    [InlineData("john's")]
    [InlineData("parents'")]
    public void PossessiveOfKnownWord_IsAWord(string typed)
    {
        var result = English().Suggest(typed);

        Assert.True(result.TypedIsWord);
        Assert.Null(result.AutoCorrection);
    }

    [Fact]
    public void Elision_OfKnownParts_IsAWord()
    {
        var italian = Engine("it", null, ("l'", 1_800_000), ("amico", 90_000), ("lamico", 5));

        var result = italian.Suggest("l'amico");

        Assert.True(result.TypedIsWord);
        Assert.Null(result.AutoCorrection);
    }

    [Fact]
    public void ApostropheWithUnknownParts_IsNotAWord()
    {
        Assert.False(English().Suggest("jhn's").TypedIsWord);
    }

    [Fact]
    public void Multilingual_WordOfAnotherLanguage_Stays()
    {
        // "fur" ist im Englischen ein echtes Wort: bei "DE · EN" nicht zu "für" machen.
        var engine = new MultilingualEngine([("de", German()), ("en", English())]);

        Assert.Null(engine.Suggest("fur").AutoCorrection);
    }

    [Fact]
    public void Multilingual_RareEntryInOneLanguageOnly_IsCorrected()
    {
        var engine = new MultilingualEngine([("de", German()), ("en", English())]);

        Assert.Equal("Tür", engine.Suggest("tur").AutoCorrection);
    }

    [Theory]
    [InlineData("fur", "für", true)]
    [InlineData("grussen", "grüßen", true)]
    [InlineData("strasse", "Straße", true)]
    [InlineData("dont", "don't", true)]
    [InlineData("übermassig", "übermäßig", true)]
    [InlineData("ändere", "andere", false)]  // Umlaut getippt: Absicht
    [InlineData("daß", "das", false)]
    [InlineData("fur", "for", false)]
    [InlineData("für", "für", false)]
    public void IsLazySpelling_OnlyForLeftOutMarks(string typed, string word, bool expected)
    {
        Assert.Equal(expected, KeyProximity.IsLazySpelling(typed, word));
    }
}
