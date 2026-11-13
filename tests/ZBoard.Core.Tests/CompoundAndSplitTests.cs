using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Zusammengesetzte Substantive, fehlende Leerzeichen und das Altern von Gelerntem.</summary>
public class CompoundAndSplitTests
{
    static SuggestionEngine Engine(NgramModel? model = null, UserHistory? history = null)
    {
        var dictionary = new WordDictionary();
        foreach (var (word, freq) in new (string, int)[]
                 {
                     ("Haus", 50_000), ("Tür", 30_000), ("Schlüssel", 20_000), ("Geburtstag", 15_000), ("Kuchen", 10_000),
                     ("Fußball", 20_000), ("Fuss", 300), ("Ball", 12_000), ("Hausboot", 2_000),
                     ("ich", 3_000_000), ("habe", 900_000), ("das", 2_500_000), ("vielen", 80_000), ("dank", 60_000),
                     ("Dank", 50_000), ("schabe", 400), ("ichhabe", 40), ("gar", 200_000), ("nicht", 2_800_000),
                     ("garnicht", 900), ("es", 2_000_000), ("ist", 3_000_000),
                     ("danke", 100_000), ("da", 500_000), ("hallo", 300_000), ("ha", 6_000), ("wirklich", 200_000),
                     ("wirkt", 20_000), ("ein", 2_000_000), ("mal", 300_000), ("einmal", 400_000),
                 })
        {
            dictionary.Add(word, freq);
        }

        dictionary.BuildTopLists();
        return new SuggestionEngine(
            dictionary, KeyProximity.FromLayout(Layouts.Alphabet("de", showLanguageKey: false)), model, history);
    }

    static NgramModel Model() => new NgramModel.Builder()
        .Add("ich", "habe", 500).Add("das", "Haus", 200).Add("vielen", "Dank", 300).Add("gar", "nicht", 400)
        .Add("es", "ist", 5000).Add("ist", "das", 800).Add("habe", "das", 300).Add("ich", "dank", 2)
        .Build();

    [Theory]
    [InlineData("haustürschlüssel", "Haustürschlüssel")]
    [InlineData("geburtstagskuchen", "Geburtstagskuchen")] // Fugen-s
    [InlineData("haustür", "Haustür")]
    public void CompoundNoun_IsAWord_AndCapitalized(string typed, string expected)
    {
        var result = Engine().Suggest(typed);

        Assert.True(result.TypedIsWord);
        Assert.Equal(expected, result.AutoCorrection);
    }

    [Fact]
    public void CompoundNoun_TypedCapitalized_Stays()
    {
        Assert.Null(Engine().Suggest("Haustürschlüssel").AutoCorrection);
    }

    [Theory]
    [InlineData("hausbot", "Hausboot")]   // kein zusammengesetztes Wort: normale Korrektur
    [InlineData("fussball", "Fußball")]   // "Fuss" + "Ball" ginge auch, aber die Schreibweise mit ß hat Vorrang
    public void NearbyDictionaryWord_Wins(string typed, string expected)
    {
        Assert.Equal(expected, Engine().Suggest(typed).AutoCorrection);
    }

    [Fact]
    public void Compound_NeedsNounsAsParts()
    {
        Assert.False(Engine().Suggest("ichhaus").TypedIsWord); // "ich" ist kein Substantiv
        Assert.False(Engine().Suggest("hausxyz").TypedIsWord);
    }

    [Theory]
    [InlineData("dashaus", "das Haus")]
    [InlineData("vielendank", "vielen Dank")]  // Schreibweise, die nach "vielen" üblich ist
    [InlineData("ichhabe", "ich habe")]        // steht als seltener Eintrag in der Wortliste
    [InlineData("garnicht", "gar nicht")]
    public void MissingSpace_IsInserted(string typed, string expected)
    {
        Assert.Equal(expected, Engine(Model()).Suggest(typed).AutoCorrection);
    }

    [Fact]
    public void MissingSpace_AtSentenceStart_KeepsTheCapital()
    {
        Assert.Equal("Ich habe", Engine(Model()).Suggest("Ichhabe").AutoCorrection);
    }

    [Fact]
    public void MissingSpace_OnlyForWordsThatFollowEachOther()
    {
        // "habe ich" kennt das Modell nicht; ohne Modell wird nie getrennt.
        Assert.NotEqual("habe ich", Engine(Model()).Suggest("habeich").AutoCorrection);
        Assert.NotEqual("das Haus", Engine().Suggest("dashaus").AutoCorrection);
    }

    [Fact]
    public void MissingSpace_NotForLearnedWords()
    {
        var history = new UserHistory();
        history.Learn(null, "dashaus", accepted: true);

        Assert.Null(Engine(Model(), history).Suggest("dashaus").AutoCorrection);
    }

    [Fact]
    public void CompoundNoun_IsCompleted()
    {
        Assert.Contains("Haustürschlüssel", Engine().Suggest("haustürschl").Candidates);
        Assert.Contains("Geburtstagskuchen", Engine().Suggest("geburtstagsku").Candidates);
    }

    [Theory]
    [InlineData("da", "ke", "danke")]         // Leertaste statt "n"
    [InlineData("ha", "llo", "hallo")]        // Leertaste zusätzlich
    [InlineData("Ha", "llo", "Hallo")]
    [InlineData("wirkl", "ich", "wirklich")]  // zweites Stück ist ein Wort, das erste nicht
    public void SpaceInsideAWord_IsRemoved(string first, string second, string expected)
    {
        Assert.Equal(expected, Engine(Model()).JoinAcrossSpace(first, second, WordContext.None)?.Word);
    }

    [Theory]
    [InlineData("ein", "mal")]  // zwei richtige Wörter bleiben getrennt, auch wenn es "einmal" gibt
    [InlineData("da", "ich")]
    [InlineData("da", "xq")]    // ergibt kein Wort
    public void SpaceBetweenWords_Stays(string first, string second)
    {
        Assert.Null(Engine(Model()).JoinAcrossSpace(first, second, WordContext.None));
    }

    [Fact]
    public void SpaceInsideAWord_NotIfThePieceWasTypedBefore()
    {
        var history = new UserHistory();
        history.Learn(null, "ke");

        Assert.Null(Engine(Model(), history).JoinAcrossSpace("da", "ke", WordContext.None));
    }

    [Fact]
    public void SpaceInsideAWord_Multilingual_KeepsWordsOfTheOtherLanguage()
    {
        var english = new WordDictionary();
        english.Add("ke", 50_000);
        english.BuildTopLists();
        var en = new SuggestionEngine(english, KeyProximity.FromLayout(Layouts.Alphabet("en", showLanguageKey: false)));

        Assert.Equal("danke", new MultilingualEngine([("de", Engine(Model()))]).JoinAcrossSpace("da", "ke", WordContext.None));
        Assert.Null(new MultilingualEngine([("de", Engine(Model())), ("en", en)]).JoinAcrossSpace("da", "ke", WordContext.None));
    }

    [Fact]
    public void Unlearn_TakesBackOneUse_ButKnownWordsStayKnown()
    {
        var history = new UserHistory();
        history.Learn("sag", "ha");
        history.Unlearn("sag", "ha");
        Assert.Equal(0, history.CountOf("ha"));
        Assert.Empty(history.Followers("sag"));

        history.Learn(null, "Kiez");
        history.Learn(null, "Kiez");
        history.Learn(null, "Kiez");
        history.Unlearn(null, "Kiez");
        history.Unlearn(null, "Kiez");
        Assert.Equal(2, history.CountOf("Kiez"));
    }

    [Fact]
    public void Aging_ForgetsOneOffs_ButKeepsKnownWords()
    {
        var history = new UserHistory();
        history.Learn("im", "Kiez");
        history.Learn("im", "Kiez");
        history.Learn("im", "Kiez");
        history.Learn("im", "Kiez");
        history.Learn("ein", "Vertipper");

        for (int i = 0; i < UserHistory.AgingInterval; i++) history.Learn(null, "und");

        Assert.True(history.IsKnown("Kiez"));
        Assert.Equal(2, history.CountOf("Kiez"));
        Assert.Equal(0, history.CountOf("Vertipper"));
        Assert.Equal([("Kiez", 2)], history.Followers("im"));
        Assert.Empty(history.Followers("ein"));
    }

    [Fact]
    public void Aging_ProgressSurvivesSaveAndLoad()
    {
        var history = new UserHistory();
        history.Learn("ein", "Vertipper");
        for (int i = 0; i < UserHistory.AgingInterval - 3; i++) history.Learn(null, "und");

        var stream = new MemoryStream();
        history.Save(stream);
        stream.Position = 0;
        var loaded = UserHistory.Load(stream);
        Assert.Equal(1, loaded.CountOf("Vertipper"));

        loaded.Learn(null, "und");
        loaded.Learn(null, "und");
        Assert.Equal(0, loaded.CountOf("Vertipper"));
    }

    [Fact]
    public void Load_ReadsFilesWithoutAgingLine()
    {
        var loaded = UserHistory.Load(new MemoryStream("w\tKiez\t3\nb\tim\tKiez\t2\n"u8.ToArray()));

        Assert.Equal(3, loaded.CountOf("Kiez"));
        Assert.Equal([("Kiez", 2)], loaded.Followers("im"));
    }
}
