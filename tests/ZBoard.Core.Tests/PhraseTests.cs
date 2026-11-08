using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

public class PhraseTests
{
    static SuggestionEngine Engine(UserHistory history)
    {
        var dictionary = new WordDictionary();
        dictionary.Add("mit", 1000);
        dictionary.Add("freundlichen", 100);
        dictionary.Add("Grüßen", 100);
        dictionary.BuildTopLists();
        return new SuggestionEngine(dictionary, KeyProximity.FromLayout(Layouts.Alphabet("de", false)), null, history);
    }

    static void Type(UserHistory h, params string[] words)
    {
        string? previous = null;
        foreach (var w in words)
        {
            h.Learn(previous, w);
            previous = w;
        }
    }

    [Fact]
    public void RepeatedSequence_IsOfferedAsPhrase()
    {
        var h = new UserHistory();
        Type(h, "Mit", "freundlichen", "Grüßen", "Patrick");
        Type(h, "Mit", "freundlichen", "Grüßen", "Patrick");
        var engine = Engine(h);

        Assert.Equal("Grüßen Patrick", engine.ContinuePhrase("freundlichen"));
        Assert.Equal("freundlichen Grüßen Patrick", engine.ContinuePhrase("mit"));
        Assert.Equal("freundlichen Grüßen", engine.ContinuePhrase("Mit", maxWords: 2));
    }

    [Fact]
    public void OnceTyped_IsNoPhraseYet()
    {
        var h = new UserHistory();
        Type(h, "Mit", "freundlichen", "Grüßen");
        Assert.Null(Engine(h).ContinuePhrase("freundlichen"));
    }

    [Fact]
    public void AmbiguousFollower_StopsPhrase()
    {
        var h = new UserHistory();
        Type(h, "ich", "gehe"); Type(h, "ich", "gehe");
        Type(h, "ich", "bin"); Type(h, "ich", "bin");
        Type(h, "ich", "kann");
        Assert.Null(Engine(h).ContinuePhrase("ich")); // "gehe" nur 2 von 5
    }

    [Fact]
    public void Loop_DoesNotRepeat()
    {
        var h = new UserHistory();
        Type(h, "ja", "ja", "ja", "ja");
        Assert.Null(Engine(h).ContinuePhrase("ja"));
    }
}

public class AcceptedWordTests
{
    [Fact]
    public void LiteralPick_MakesWordKnownImmediately()
    {
        var h = new UserHistory();
        h.Learn(null, "kacke");
        Assert.False(h.IsKnown("kacke"));

        var g = new UserHistory();
        g.Learn(null, "kacke", accepted: true);
        Assert.True(g.IsKnown("kacke"));
        Assert.Equal("kacke", g.LearnedForm("kacke"));
    }

    [Fact]
    public void AcceptedWord_IsNoLongerOfferedInQuotes()
    {
        var dictionary = new WordDictionary();
        dictionary.Add("lacks", 500);
        dictionary.Add("macie", 50);
        dictionary.BuildTopLists();
        var history = new UserHistory();
        var engine = new SuggestionEngine(dictionary, ZBoard.Suggestions.KeyProximity.FromLayout(ZBoard.Keyboard.Layouts.Alphabet("en", false)), null, history);

        Assert.False(engine.Suggest("kacke").TypedIsWord);
        history.Learn(null, "kacke", accepted: true);
        var result = engine.Suggest("kacke");
        Assert.True(result.TypedIsWord);
        Assert.Null(result.AutoCorrection);
    }
}
