using System.Text;
using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

public class PredictionTests
{
    static WordDictionary Dictionary(params (string Word, int Freq)[] words)
    {
        var dictionary = new WordDictionary();
        foreach (var (w, f) in words) dictionary.Add(w, f);
        dictionary.BuildTopLists();
        return dictionary;
    }

    static KeyProximity Proximity() => KeyProximity.FromLayout(Layouts.Alphabet("de", false));

    [Fact]
    public void Transposition_IsCorrected()
    {
        var engine = new SuggestionEngine(Dictionary(("das", 1000), ("da", 500)), Proximity());

        Assert.Equal("das", engine.Suggest("dsa").AutoCorrection);
    }

    [Fact]
    public void Predict_UsesLanguageModel()
    {
        var model = new NgramModel.Builder()
            .Add("ich", "habe", 100).Add("ich", "bin", 80).Add("ich", "kann", 60).Add("du", "bist", 50).Build();
        var engine = new SuggestionEngine(Dictionary(("ich", 10)), Proximity(), model);

        Assert.Equal(new[] { "habe", "bin", "kann" }, engine.Predict("Ich"));
    }

    [Fact]
    public void Predict_LearnedSequence_WinsOverLanguageModel()
    {
        var bigrams = new NgramModel.Builder().Add("guten", "Tag", 100).Add("guten", "Morgen", 90).Build();
        var history = new UserHistory();
        history.Learn("guten", "Abend");
        history.Learn("guten", "Abend");
        history.Learn("guten", "Abend");
        var engine = new SuggestionEngine(Dictionary(("guten", 10)), Proximity(), bigrams, history);

        Assert.Equal("Abend", engine.Predict("guten")[0]);
    }

    [Fact]
    public void LearnedWord_IsNoLongerAutocorrected()
    {
        var history = new UserHistory();
        var engine = new SuggestionEngine(Dictionary(("hallo", 1000)), Proximity(), history: history);
        Assert.Equal("hallo", engine.Suggest("halli").AutoCorrection);

        history.Learn(null, "halli");
        history.Learn(null, "halli");

        Assert.Null(engine.Suggest("halli").AutoCorrection);
        Assert.Contains("halli", engine.Suggest("hal").Candidates);
    }

    [Fact]
    public void Context_PrefersFollowerOfPreviousWord()
    {
        var model = new NgramModel.Builder()
            .Add("ich", "bin", 100).Add("ich", "habe", 100).Add("wir", "sind", 400).Add("er", "bis", 300).Add("es", "ist", 2000).Build();
        var engine = new SuggestionEngine(Dictionary(("bis", 300), ("bin", 100)), Proximity(), model);

        Assert.Equal("bis", engine.Suggest("bi").Candidates[0]);
        Assert.Equal("bin", engine.Suggest("bi", previousWord: "ich").Candidates[0]);
    }

    [Fact]
    public void UserHistory_SurvivesSaveAndLoad()
    {
        var history = new UserHistory();
        history.Learn("guten", "Abend");
        history.Learn("guten", "Abend");

        var stream = new MemoryStream();
        history.Save(stream);
        stream.Position = 0;
        var loaded = UserHistory.Load(stream);

        Assert.True(loaded.IsKnown("abend"));
        Assert.Equal("Abend", loaded.Followers("Guten")[0].Next);
    }

    [Fact]
    public void UserHistory_IgnoresEmailsAndNumbers()
    {
        var history = new UserHistory();
        history.Learn(null, "max@example.com");
        history.Learn(null, "12345");

        Assert.Equal(0, history.WordCount);
    }
}
