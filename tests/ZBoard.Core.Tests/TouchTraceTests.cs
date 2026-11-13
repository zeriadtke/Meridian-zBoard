using System.Numerics;
using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Autokorrektur mit Fingerpositionen: Wo die Taste getroffen wurde, entscheidet mit.</summary>
public class TouchTraceTests
{
    // Ausschnitt einer QWERTZ-Tastatur in Tastenbreiten
    static readonly Dictionary<char, Vector2> Keys = new()
    {
        ['r'] = new(3.5f, 0), ['t'] = new(4.5f, 0), ['e'] = new(2.5f, 0), ['i'] = new(7.5f, 0), ['u'] = new(6.5f, 0),
        ['a'] = new(1, 1), ['d'] = new(3, 1), ['f'] = new(4, 1), ['g'] = new(5, 1), ['h'] = new(6, 1),
    };

    static TouchTrace Trace(params Vector2?[] points) => new(points, Keys);

    static SuggestionEngine Engine(NgramModel? model, UserHistory? history, params (string Word, int Freq)[] words)
    {
        var dictionary = new WordDictionary();
        foreach (var (word, freq) in words) dictionary.Add(word, freq);
        dictionary.BuildTopLists();
        return new SuggestionEngine(
            dictionary, KeyProximity.FromLayout(Layouts.Alphabet("de", showLanguageKey: false)), model, history);
    }

    [Fact]
    public void Cost_IsLowAtTheBorder_AndHighAtTheCenter()
    {
        var border = Trace(new Vector2(5.5f, 1));   // genau zwischen g und h
        var center = Trace(new Vector2(5f, 1));     // mitten auf g

        Assert.Equal(0f, border.Cost(0, 'g', 'h', 12)!.Value, 3);
        Assert.Equal(1f, center.Cost(0, 'g', 'h', 12)!.Value, 3);
        Assert.True(Trace(new Vector2(5.3f, 1)).Cost(0, 'g', 'h', 12) is > 0.3f and < 0.9f);
    }

    [Fact]
    public void Cost_IsUnknown_WithoutPointOrKey()
    {
        Assert.Null(Trace((Vector2?)null).Cost(0, 'g', 'h', 12));
        Assert.Null(Trace(new Vector2(5, 1)).Cost(0, 'g', 'ö', 12));
        Assert.Null(Trace(new Vector2(5, 1)).Cost(1, 'g', 'h', 12));
    }

    [Fact]
    public void Correction_GoesToTheKeyTheFingerWasCloserTo()
    {
        // "gat": gemeint sein kann "hat" (h rechts neben g) oder "tat" (t über g) – beide gleich häufig.
        var engine = Engine(null, null, ("hat", 1000), ("tat", 1000));

        var towardsH = Trace(new Vector2(5.45f, 1.0f), null, null);
        var towardsT = Trace(new Vector2(4.8f, 0.55f), null, null);

        Assert.Equal("hat", engine.Suggest("gat", WordContext.None, touches: towardsH).AutoCorrection);
        Assert.Equal("tat", engine.Suggest("gat", WordContext.None, touches: towardsT).AutoCorrection);
    }

    static NgramModel Model() => new NgramModel.Builder()
        .Add("heute", "frei", 100).Add("um", "drei", 50).Add("es", "ist", 5000).Build();

    [Fact]
    public void RealWord_IsCorrected_WhenTheFingerWasAtTheBorderAndTheSentenceFitsTheNeighbour()
    {
        var engine = Engine(Model(), null, ("drei", 5000), ("frei", 4000));
        var context = new WordContext("heute");
        var atBorder = Trace(new Vector2(3.48f, 1), null, null, null);  // d, fast schon f
        var atCenter = Trace(new Vector2(3.0f, 1), null, null, null);

        Assert.Equal("frei", engine.Suggest("drei", context, touches: atBorder).AutoCorrection);
        Assert.Null(engine.Suggest("drei", context, touches: atCenter).AutoCorrection);
        Assert.Null(engine.Suggest("drei", context).AutoCorrection);                       // ohne Fingerposition
        Assert.Null(engine.Suggest("drei", new WordContext("um"), touches: atBorder).AutoCorrection); // Satz passt zu "drei"
    }

    [Fact]
    public void RealWord_TheUserTypedBefore_Stays()
    {
        var history = new UserHistory();
        history.Learn("heute", "drei");
        var engine = Engine(Model(), history, ("drei", 5000), ("frei", 4000));

        var atBorder = Trace(new Vector2(3.48f, 1), null, null, null);
        Assert.Null(engine.Suggest("drei", new WordContext("heute"), touches: atBorder).AutoCorrection);
    }

    [Fact]
    public void Touches_OfAnotherLength_AreIgnored()
    {
        var engine = Engine(null, null, ("hat", 1000));

        Assert.Equal("hat", engine.Suggest("gat", WordContext.None, touches: Trace(new Vector2(5, 1))).AutoCorrection);
    }
}
