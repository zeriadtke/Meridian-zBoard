using System.Numerics;
using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Mehrsprachige Eingabe: mehrere Wörterbücher gleichzeitig, auf der Leertaste „DE · EN“.</summary>
public class MultilingualTests
{
    static SuggestionEngine Engine(string language, params (string Word, int Freq)[] words)
    {
        var dictionary = new WordDictionary();
        foreach (var (word, freq) in words) dictionary.Add(word, freq);
        dictionary.BuildTopLists();
        return new SuggestionEngine(dictionary, KeyProximity.FromLayout(Layouts.Alphabet(language, showLanguageKey: false)));
    }

    static SuggestionEngine German() => Engine("de",
        ("die", 1000), ("tee", 100), ("Haus", 300), ("Kind", 300), ("hallo", 200), ("ich", 900), ("was", 100), ("für", 400));

    static SuggestionEngine English() => Engine("en",
        ("the", 1000), ("have", 200), ("kind", 300), ("house", 300), ("hello", 200), ("i", 900), ("want", 100));

    static MultilingualEngine DeEn(SuggestionEngine? de = null, SuggestionEngine? en = null) =>
        new([("de", de ?? German()), ("en", en ?? English())]);

    [Fact]
    public void ActiveLanguages_CurrentFirstThenOthers_AtMostThree()
    {
        Assert.Equal(["en", "de", "fr"], MultilingualEngine.ActiveLanguages("en", ["de", "en", "fr", "es"], multilingual: true));
        Assert.Equal(["en"], MultilingualEngine.ActiveLanguages("en", ["de", "en"], multilingual: false));
        Assert.Equal(["de"], MultilingualEngine.ActiveLanguages("de", ["de"], multilingual: true));
    }

    [Fact]
    public void SpaceLabel_LikeGboard()
    {
        Assert.Equal("DE · EN", MultilingualEngine.SpaceLabel(["de", "en"]));
    }

    [Fact]
    public void SingleLanguage_SameAsSuggestionEngine()
    {
        var de = German();
        var single = new MultilingualEngine("de", de);
        foreach (var typed in new[] { "tje", "ha", "haus", "fur", "Hal" })
        {
            var expected = de.Suggest(typed);
            var actual = single.Suggest(typed);
            Assert.Equal(expected.Candidates, actual.Candidates);
            Assert.Equal(expected.AutoCorrection, actual.AutoCorrection);
            Assert.Equal(expected.TypedIsWord, actual.TypedIsWord);
        }
    }

    [Fact]
    public void WordOfOtherLanguage_IsNotCorrected()
    {
        Assert.Equal("tee", German().Suggest("the").AutoCorrection); // nur Deutsch: würde „korrigiert“

        var result = DeEn().Suggest("the");

        Assert.Null(result.AutoCorrection);
        Assert.True(result.TypedIsWord);
    }

    [Fact]
    public void TypoIsCorrected_IntoEitherLanguage()
    {
        Assert.Equal("the", DeEn().Suggest("teh").AutoCorrection);
        Assert.Equal("für", DeEn().Suggest("fur").AutoCorrection);
    }

    [Fact]
    public void Candidates_ComeFromBothDictionaries()
    {
        var candidates = DeEn().Suggest("h", max: 6).Candidates;

        Assert.Contains("Haus", candidates);
        Assert.Contains("house", candidates);
    }

    [Fact]
    public void NounCapitalization_OnlyWhenNoOtherLanguageKnowsTheWord()
    {
        Assert.Equal("Kind", German().Suggest("kind").AutoCorrection);
        Assert.Null(DeEn().Suggest("kind").AutoCorrection);          // englisch „kind“
        Assert.Equal("Haus", DeEn().Suggest("haus").AutoCorrection); // kein englisches „haus“
    }

    [Fact]
    public void PreviousWord_FavorsItsLanguage()
    {
        var engine = DeEn();

        Assert.Equal("was", engine.Suggest("wa", "ich").Candidates[0]);
        Assert.Equal("want", engine.Suggest("wa", "i").Candidates[0]);
    }

    [Fact]
    public void Learning_GoesToTheWordsLanguage()
    {
        var de = German();
        var en = English();
        var engine = DeEn(de, en);

        engine.Learn(null, "house");
        engine.Learn("ich", "Quatschwort");  // unbekannt: Sprache des vorherigen Worts
        engine.Learn("i", "blorp");
        engine.Learn(null, "neuwort");       // ohne Anhaltspunkt: Layoutsprache

        Assert.Equal(1, en.History.CountOf("house"));
        Assert.Equal(0, de.History.CountOf("house"));
        Assert.Equal(1, de.History.CountOf("quatschwort"));
        Assert.Equal(1, en.History.CountOf("blorp"));
        Assert.Equal(1, de.History.CountOf("neuwort"));
        Assert.Equal("en", engine.LearningLanguage(null, "blorp")); // schon dort gelernt
    }

    [Fact]
    public void SuppressCorrection_AppliesToAllLanguages()
    {
        var engine = DeEn();
        engine.SuppressCorrection("teh");

        Assert.Null(engine.Suggest("teh").AutoCorrection);
    }

    [Fact]
    public void Predict_UsesLanguageModelsOfBothLanguages()
    {
        var deModel = new NgramModel.Builder().Add("ich", "habe", 100).Build();
        var enModel = new NgramModel.Builder().Add("i", "have", 100).Build();
        var de = new SuggestionEngine(new WordDictionary(), KeyProximity.FromLayout(Layouts.Alphabet("de", showLanguageKey: false)), deModel);
        var en = new SuggestionEngine(new WordDictionary(), KeyProximity.FromLayout(Layouts.Alphabet("en", showLanguageKey: false)), enModel);
        var engine = new MultilingualEngine([("de", de), ("en", en)]);

        Assert.Equal(["habe"], engine.Predict("ich"));
        Assert.Equal(["have"], engine.Predict("i"));
    }

    [Fact]
    public void Glide_FindsWordsOfBothLanguages()
    {
        var keys = new Dictionary<char, Vector2>();
        (string Letters, float Offset)[] rows = [("qwertzuiopü", 0f), ("asdfghjklöä", 0f), ("yxcvbnm", 2f)];
        for (int r = 0; r < rows.Length; r++)
        {
            for (int i = 0; i < rows[r].Letters.Length; i++) keys[rows[r].Letters[i]] = new Vector2(rows[r].Offset + i + 0.5f, r * 1.45f);
        }

        List<Vector2> Path(string letters)
        {
            var points = letters.Select(c => keys[c]).ToList();
            List<Vector2> path = [];
            for (int i = 0; i < points.Count - 1; i++)
            {
                for (int s = 0; s < 8; s++) path.Add(Vector2.Lerp(points[i], points[i + 1], s / 8f));
            }

            path.Add(points[^1]);
            return path;
        }

        var engine = DeEn();
        Assert.Equal("hello", engine.Glide(Path("helo"), keys)[0]);
        Assert.Equal("hallo", engine.Glide(Path("halo"), keys)[0]);
    }

    [Fact]
    public void Normalize_BringsDictionariesToTheSameScale()
    {
        var dictionary = new WordDictionary();
        dictionary.Add("a", 300);
        dictionary.Add("b", 100);
        dictionary.BuildTopLists(normalizeTo: 1000);

        Assert.Equal(1000, dictionary.TotalFrequency);
        Assert.Equal(("a", 750), dictionary.Lookup("a"));
    }

    [Fact]
    public void RareWordOfOtherLanguage_DoesNotBlockCorrectionOrCapitalization()
    {
        // Untertitel-Listen enthalten seltene Tippfehler: englisch „teh“, „haus“
        var de = Engine("de", ("geh", 5000), ("Haus", 60000));
        var en = Engine("en", ("the", 900000), ("teh", 30), ("haus", 25));
        var engine = new MultilingualEngine([("de", de), ("en", en)]);

        Assert.Equal("the", engine.Suggest("teh").AutoCorrection);
        Assert.Equal("Haus", engine.Suggest("haus").AutoCorrection);
    }

    [Fact]
    public void TypedWord_StaysInFront_OfFrequentForeignWords()
    {
        var de = Engine("de", ("rot", 5000), ("über", 9000), ("uber", 450));
        var en = Engine("en", ("for", 900000), ("not", 800000), ("rot", 1300), ("uber", 200));
        var engine = new MultilingualEngine([("de", de), ("en", en)]);

        Assert.Equal("rot", engine.Suggest("rot").Candidates[0]);
        Assert.Equal(de.Suggest("uber").Candidates[0], engine.Suggest("uber").Candidates[0]); // eigene Sprache: Reihenfolge bleibt
    }
}
