using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Sprachmodell: Satzkontext lesen, vorhersagen, Vorschläge und Korrekturen nach dem Satz ordnen.</summary>
public class NgramModelTests
{
    const string Start = NgramModel.SentenceStart;

    [Theory]
    [InlineData("", Start, null)]                  // leeres Feld
    [InlineData("Hallo ", "Hallo", Start)]
    [InlineData("Ich habe ", "habe", "Ich")]
    [InlineData("Ich glaube, ", ",", "glaube")]
    [InlineData("Das war's. ", Start, null)]
    [InlineData("Er sagte: „Ende.“ ", Start, null)]  // Satzende hinter Anführungszeichen
    [InlineData("Hallo\nWie ", "Wie", Start)]
    [InlineData("(siehe ", "siehe", null)]         // Klammer: davor kein Kontext
    [InlineData("in 3 Tagen ", "Tagen", "3")]
    [InlineData("Hallo", null, null)]              // kein Leerraum: hier wird noch am Wort geschrieben
    [InlineData("Hallo (", null, null)]
    public void FromText_ReadsTheTwoWordsBefore(string before, string? previous, string? beforePrevious)
    {
        Assert.Equal(new WordContext(previous, beforePrevious), WordContext.FromText(before));
    }

    [Fact]
    public void FromText_TruncatedText_DoesNotInventSentenceStart()
    {
        Assert.Equal(new WordContext("habe"), WordContext.FromText("habe ", startsAtFieldStart: false));
        Assert.Equal(WordContext.None, WordContext.FromText("", startsAtFieldStart: false));
    }

    static NgramModel Model() => new NgramModel.Builder()
        .Add("ich", "habe", 60).Add("ich", "bin", 30).Add("ich", "hatte", 10)
        .Add("habe", "ich", 50).Add("habe", "keine", 30).Add("habe", "das", 20)
        .Add("ich", "habe", "keine", 30).Add("ich", "habe", "das", 10)
        .Add(Start, "ich", 70).Add(Start, "das", 30)
        .Add(",", "dass", 80).Add(",", "aber", 20)
        .Add("eine", "Frage", 40).Add("eine", "gute", 60).Add("ich", "frage", 5)
        .Add("es", "ist", 500).Add("er", "hat", 300).Add("sie", "hat", 300)
        .Build();

    [Fact]
    public void Predict_PrefersThreeWordSequences_ThenFallsBack()
    {
        var model = Model();

        // Nach "habe" allein ist "ich" am häufigsten, nach "ich habe" aber "keine".
        Assert.Equal("ich", model.Predict(new WordContext("habe"))[0].Word);
        Assert.Equal("keine", model.Predict(new WordContext("habe", "ich"))[0].Word);
        Assert.Contains("ich", model.Predict(new WordContext("habe", "ich"), 5).Select(p => p.Word));
    }

    [Fact]
    public void Predict_AtSentenceStartAndAfterComma()
    {
        var model = Model();

        Assert.Equal("ich", model.Predict(WordContext.FromText(""))[0].Word);
        Assert.Equal("dass", model.Predict(WordContext.FromText("Ich glaube, "))[0].Word);
    }

    [Fact]
    public void Predict_IgnoresCaseOfContext_AndNeverOffersMarkers()
    {
        var model = Model();

        Assert.Equal("habe", model.Predict(new WordContext("Ich"))[0].Word);
        Assert.DoesNotContain(model.Predict(new WordContext("ich"), 10), p => p.Word is Start or ",");
        Assert.Empty(model.Predict(new WordContext("unbekannt")));
        Assert.Empty(model.Predict(WordContext.None));
    }

    [Fact]
    public void ContextFactor_IsOneForUnseen_AndLargerForTypicalFollowers()
    {
        var model = Model();

        Assert.Equal(1.0, model.ContextFactor(new WordContext("ich"), "ist"));
        Assert.Equal(1.0, model.ContextFactor(WordContext.None, "habe"));
        Assert.True(model.ContextFactor(new WordContext("ich"), "habe") > 5);
        Assert.True(model.ContextFactor(new WordContext("eine"), "frage") > 5); // zählt für "Frage" mit
    }

    [Fact]
    public void SaveAndLoad_KeepPredictions()
    {
        var model = Model();
        var stream = new MemoryStream();
        model.Save(stream);
        stream.Position = 0;
        var loaded = NgramModel.Load(stream);

        Assert.Equal(model.WordCount, loaded.WordCount);
        Assert.Equal(model.BigramCount, loaded.BigramCount);
        Assert.Equal(model.TrigramCount, loaded.TrigramCount);
        foreach (var context in new[] { new WordContext("ich"), new WordContext("habe", "ich"), new WordContext(Start), new WordContext(",") })
        {
            Assert.Equal(model.Predict(context, 5).Select(p => p.Word), loaded.Predict(context, 5).Select(p => p.Word));
            var (expected, actual) = (model.Predict(context)[0].Probability, loaded.Predict(context)[0].Probability);
            Assert.True(Math.Abs(expected - actual) < expected * 0.01, $"{expected} / {actual}");
        }
    }

    [Fact]
    public void Build_KeepsOnlyTheMostFrequentSequences()
    {
        var model = new NgramModel.Builder().Add("a", "b", 5).Add("a", "c", 3).Add("a", "d", 1).Build(maxBigrams: 2);

        Assert.Equal(["b", "c"], model.Predict(new WordContext("a"), 5).Select(p => p.Word));
        // Die Wahrscheinlichkeit bezieht sich auf alle gezählten Folgen, auch die weggefallene.
        Assert.Equal(5.0 / 9, model.Predict(new WordContext("a"))[0].Probability, 3);
    }

    [Fact]
    public void Load_RejectsOtherFiles()
    {
        Assert.Throws<InvalidDataException>(() => NgramModel.Load(new MemoryStream("kein Modell"u8.ToArray())));
    }

    static SuggestionEngine Engine(NgramModel model, params (string Word, int Freq)[] words)
    {
        var dictionary = new WordDictionary();
        foreach (var (word, freq) in words) dictionary.Add(word, freq);
        dictionary.BuildTopLists();
        return new SuggestionEngine(dictionary, KeyProximity.FromLayout(Layouts.Alphabet("de", showLanguageKey: false)), model);
    }

    [Fact]
    public void Correction_FollowsTheSentence()
    {
        // "hae" liegt gleich nah an "hat" und "habe"; "hat" ist häufiger, nach "ich" passt aber nur "habe".
        var engine = Engine(Model(), ("hat", 3000), ("habe", 1000), ("ich", 5000));

        Assert.Equal("hat", engine.Suggest("hae").AutoCorrection);
        Assert.Equal("habe", engine.Suggest("hae", new WordContext("ich")).AutoCorrection);
    }

    [Fact]
    public void AmbiguousWord_IsCapitalized_WhereTheNounIsExpected()
    {
        var engine = Engine(Model(), ("frage", 600), ("Frage", 500), ("eine", 5000), ("ich", 5000));

        Assert.Equal("Frage", engine.Suggest("frage", new WordContext("eine")).AutoCorrection);
        Assert.Null(engine.Suggest("frage", new WordContext("ich")).AutoCorrection);
        Assert.Null(engine.Suggest("frage").AutoCorrection);
    }

    [Fact]
    public void Engine_PredictsAtSentenceStart_AndStillPrefersOwnSequences()
    {
        var history = new UserHistory();
        for (int i = 0; i < 3; i++) history.Learn("ich", "gehe");
        var dictionary = new WordDictionary();
        dictionary.BuildTopLists();
        var engine = new SuggestionEngine(
            dictionary, KeyProximity.FromLayout(Layouts.Alphabet("de", showLanguageKey: false)), Model(), history);

        Assert.Equal("ich", engine.Predict(WordContext.FromText(""))[0]);
        Assert.Equal("gehe", engine.Predict(new WordContext("ich"))[0]);
    }

    [Theory]
    [InlineData("de", "Vielen ", "Dank")]
    [InlineData("de", "Mit freundlichen ", "Grüßen")]
    [InlineData("en", "How are ", "you")]
    [InlineData("fr", "Il y ", "a")]
    [InlineData("es", "Yo no ", "he")]
    [InlineData("it", "Io non ", "ho")]
    public void ShippedModels_LoadAndPredict(string language, string before, string expected)
    {
        using var stream = File.OpenRead(Path.Combine(RepositoryRoot(), "Assets", "dict", $"{language}.ngrams.bin"));
        var model = NgramModel.Load(stream);

        Assert.Equal(expected, model.Predict(WordContext.FromText(before))[0].Word);
    }

    static string RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ZBoard.csproj"))) return dir.FullName;
            }
        }

        throw new InvalidOperationException("zBoard-Ordner nicht gefunden.");
    }
}
