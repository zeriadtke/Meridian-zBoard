using System.Numerics;
using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Höflichkeitsform, persönliches Tippmuster und ähnliche echte Wörter in der Leiste.</summary>
public class FormalTouchConfusionTests
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
        ("sie", 3_806_767), ("ihnen", 292_370), ("ihre", 208_915), ("erhalten", 40_000), ("danke", 100_000),
        ("ich", 5_890_279), ("bin", 900_000), ("in", 1_231_372), ("im", 403_737), ("urlaub", 20_000), ("ins", 80_000),
        ("hamburg", 10_000), ("wohne", 8_000), ("innen", 300_000), ("inzwischen", 300_000), ("insgesamt", 300_000),
    ];

    // ---------- Höflichkeitsform ----------

    [Theory]
    [InlineData("Sehr geehrte Frau Weber, anbei erhalten ", true)]
    [InlineData("Vielen Dank für Ihre Nachricht. Anbei ", true)]
    [InlineData("Hallo Anna, ich habe ", false)]
    [InlineData("Sie kommt morgen. Dann ", false)] // "Sie" am Satzanfang könnte auch "sie" sein
    public void FormalAddress_IsDetected(string text, bool formal)
    {
        Assert.Equal(formal, WordContext.HasFormalAddress(text));
    }

    [Theory]
    [InlineData("sie", "erhalten", "Sie")]
    [InlineData("ihnen", "danke", "Ihnen")]
    public void FormalField_CapitalizesPronouns(string typed, string previous, string expected)
    {
        var engine = Engine("de", null, German);

        Assert.Equal(expected, engine.Suggest(typed, new WordContext(previous) { Formal = true }).AutoCorrection);
        Assert.Null(engine.Suggest(typed, new WordContext(previous)).AutoCorrection);          // nicht förmlich: bleibt klein
        Assert.Contains(expected, engine.Suggest(typed, new WordContext(previous)).Candidates); // aber in der Leiste
    }

    [Fact]
    public void FormalForms_OnlyInGerman()
    {
        var engine = Engine("en", null, ("sie", 100), ("see", 500_000));
        Assert.DoesNotContain("Sie", engine.Suggest("sie", new WordContext("I") { Formal = true }).Candidates);
    }

    // ---------- Tippmuster ----------

    static readonly Dictionary<char, Vector2> Keys = new()
    {
        ['a'] = new(0.5f, 1.5f), ['s'] = new(1.5f, 1.5f), ['q'] = new(0.5f, 0f), ['w'] = new(1.5f, 0f),
    };

    [Fact]
    public void TouchModel_LearnsTheAverageOffset_AndShrinksEarlyGuesses()
    {
        var model = new TouchModel();
        Assert.Equal(Vector2.Zero, model.HitShift);

        for (int i = 0; i < 300; i++) model.Learn("as", [new Vector2(0.5f, 1.8f), new Vector2(1.5f, 1.8f)], Keys);

        Assert.InRange(model.HitShift.Y, 0.25f, 0.3f); // 0,3 zu tief, nach 600 Treffern fast ganz übernommen
        Assert.InRange(model.HitShift.X, -0.01f, 0.01f);
        Assert.InRange(model.Shift(Keys)['a'].Y, 1.75f, 1.8f);
    }

    [Fact]
    public void TouchModel_IgnoresMismatchedWordsAndFarPoints_AndCapsTheShift()
    {
        var model = new TouchModel();
        model.Learn("asa", [new Vector2(0.5f, 1.5f), null], Keys);              // andere Länge: nichts gelernt
        model.Learn("a", [new Vector2(3f, 1.5f)], Keys);                        // 2,5 Tasten daneben: ein anderer Fehler
        Assert.Equal(0, model.Count);

        for (int i = 0; i < 400; i++) model.Learn("a", [new Vector2(0.5f, 2.6f)], Keys); // 1,1 zu tief
        Assert.True(model.HitShift.Length() <= TouchModel.MaxShift + 1e-4f);
    }

    [Fact]
    public void TouchModel_SavesAndLoads()
    {
        var model = new TouchModel();
        for (int i = 0; i < 50; i++) model.Learn("w", [new Vector2(1.7f, 0.2f)], Keys);

        var loaded = TouchModel.Load(model.Save() + "kaputt\tx\n");

        Assert.Equal(model.Offset('w'), loaded.Offset('w'));
        Assert.Equal(model.Count, loaded.Count);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void TouchTrace_NeverMakesACorrectionFree()
    {
        // Mit verschobenen Mitten liegt der Punkt näher an "w" als am getippten "q": fast, aber nicht ganz kostenlos.
        var shifted = new Dictionary<char, Vector2>(Keys) { ['w'] = new(0.6f, 0f) };
        var trace = new TouchTrace([new Vector2(0.65f, 0f)], shifted);

        Assert.Equal(TouchTrace.MinCost, trace.Cost(0, 'q', 'w', 12));
    }

    // ---------- Ähnliche echte Wörter ----------

    [Fact]
    public void SimilarRealWord_GoesIntoTheBar_WithoutCorrecting()
    {
        var model = new NgramModel.Builder()
            .Add("bin", "im", 900).Add("bin", "in", 50).Add("wohne", "in", 900).Add("es", "ist", 50_000).Add("ich", "bin", 3000)
            .Build();
        var engine = Engine("de", model, German);

        var result = engine.Suggest("in", new WordContext("bin", "ich"));
        Assert.Null(result.AutoCorrection);                    // "in" ist ein echtes Wort: bleibt
        Assert.Contains("im", result.Candidates.Take(3));      // aber "im" steht in der Leiste

        var fits = engine.Suggest("in", new WordContext("wohne", "ich"));
        Assert.DoesNotContain("im", fits.Candidates.Take(3));  // passt "in", bleiben die Vervollständigungen
    }
}
