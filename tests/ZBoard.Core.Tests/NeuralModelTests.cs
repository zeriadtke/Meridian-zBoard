using System.Globalization;
using System.Text;
using ZBoard.Keyboard;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Neuronales Sprachmodell: langer Satzkontext, Vokabular, Verteilung und Mischung mit dem N-Gramm-Modell.</summary>
public class NeuralModelTests
{
    const string Start = NgramModel.SentenceStart;

    static readonly string[] Words = ["ich", "habe", "hat", "Grüßen", "Gruß", "keine", "Zeit", "Frage", "frage", "Dank", "Brief", "mit", "freundlichen"];

    static NeuralVocabulary Vocabulary()
    {
        var text = new StringBuilder("<pad>\t0\n<unk>\t0\n<s>\t0\n,\t0\n");
        foreach (var word in Words) text.Append(word).Append('\t').Append((1.0 / Words.Length).ToString(CultureInfo.InvariantCulture)).Append('\n');
        return NeuralVocabulary.Load(new MemoryStream(Encoding.UTF8.GetBytes(text.ToString())), "de");
    }

    /// <summary>Ein Netz, das nur ein paar Sätze kennt; sonst gleich verteilt.</summary>
    static NeuralRunner FakeNet(NeuralVocabulary vocab, List<long[]>? calls = null) => ids =>
    {
        calls?.Add(ids);
        var probs = new float[vocab.Count];
        for (int i = NeuralVocabulary.FirstWordId; i < probs.Length; i++) probs[i] = 1;
        var words = ids.Select(id => vocab[(int)id]).ToArray();
        string last = words[^1];
        if (last == "freundlichen" && words.Contains("Brief")) probs[vocab.IdOf("Grüßen")] = 50; // weit zurück im Satz
        if (last == "ich") probs[vocab.IdOf("habe")] = 40;
        float sum = probs.Sum();
        for (int i = 0; i < probs.Length; i++) probs[i] /= sum;
        return probs;
    };

    static SuggestionEngine Engine(INeuralLanguageModel? neural, NgramModel? model = null, params (string Word, int Freq)[] words)
    {
        var dictionary = new WordDictionary();
        foreach (var (word, freq) in words) dictionary.Add(word, freq);
        dictionary.BuildTopLists();
        return new SuggestionEngine(
            dictionary, KeyProximity.FromLayout(Layouts.Alphabet("de", showLanguageKey: false)), model, new UserHistory(), neural);
    }

    [Theory]
    [InlineData("", Start)]
    [InlineData("Ich habe ", "<s> Ich habe")]
    [InlineData("Ich glaube, dass es ", "<s> Ich glaube , dass es")]
    [InlineData("Gut. Mit freundlichen ", "<s> Mit freundlichen")]
    [InlineData("(siehe dazu ", "siehe dazu")]
    public void FromTextWithSentence_ReadsTheWholeSentence(string before, string sentence)
    {
        var context = WordContext.FromTextWithSentence(before);

        Assert.Equal(sentence, context.Sentence);
        Assert.Equal(sentence.Split(' '), context.Tokens);
        // Die beiden Wörter davor sind dieselben wie ohne Satz.
        var twoWords = WordContext.FromText(before);
        Assert.Equal((twoWords.Previous, twoWords.BeforePrevious), (context.Previous, context.BeforePrevious));
    }

    [Fact]
    public void FromTextWithSentence_StopsAfterMaxTokens()
    {
        string before = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"w{i}")) + " ";

        var tokens = WordContext.FromTextWithSentence(before).Tokens;

        Assert.Equal(WordContext.MaxSentenceTokens, tokens.Count);
        Assert.Equal("w39", tokens[^1]);
    }

    [Fact]
    public void FromText_StillHasNoSentence()
    {
        Assert.Null(WordContext.FromText("Ich habe ").Sentence);
        Assert.Equal(new[] { "Ich", "habe" }, new WordContext("habe", "Ich").Tokens);
    }

    [Fact]
    public void Vocabulary_MapsUnknownAndOtherCase_AndKeepsTheLastWords()
    {
        var vocab = Vocabulary();

        Assert.Equal(NeuralVocabulary.SentenceStartId, vocab.IdOf(Start));
        Assert.Equal(NeuralVocabulary.CommaId, vocab.IdOf(","));
        Assert.Equal(NeuralVocabulary.UnknownId, vocab.IdOf("Quatsch"));
        Assert.Equal("Zeit", vocab[vocab.IdOf("zeit")]);   // nur großgeschrieben bekannt
        Assert.Equal("frage", vocab[vocab.IdOf("frage")]); // beide bekannt: genau diese Schreibweise
        Assert.Equal(2, vocab.IdsIgnoringCase("FRAGE").Count);

        var ids = vocab.Encode(["a", "b", "ich", "habe"], maxTokens: 2);
        Assert.Equal(new long[] { vocab.IdOf("ich"), vocab.IdOf("habe") }, ids);
    }

    [Fact]
    public void Vocabulary_RejectsOtherFiles()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("ich\t5\nhabe\t3\n"));
        Assert.Throws<InvalidDataException>(() => NeuralVocabulary.Load(stream));
    }

    [Fact]
    public void Distribution_RanksWords_AndComparesWithTheAverage()
    {
        var vocab = Vocabulary();
        var neural = new NeuralLanguageModel(vocab, FakeNet(vocab));

        var next = neural.Next([Start, "ich"])!;

        Assert.Equal("habe", next.Top(3)[0].Word);
        Assert.DoesNotContain(next.Top(20), t => t.Word is "<pad>" or "<unk>" or Start or ",");
        Assert.True(next.ContextFactor("habe") > 5);
        Assert.True(next.ContextFactor("hat") < 1);         // passt hier schlechter als im Durchschnitt
        Assert.Null(next.ContextFactor("unbekannt"));        // kennt das Netz nicht: N-Gramm-Modell entscheidet
        Assert.Equal(next.FormProbability("frage") + next.FormProbability("Frage"), next.Probability("frage"), 6);
    }

    [Fact]
    public void Model_RemembersRecentContexts()
    {
        var vocab = Vocabulary();
        var calls = new List<long[]>();
        var neural = new NeuralLanguageModel(vocab, FakeNet(vocab, calls));

        neural.Next([Start, "ich"]);
        neural.Next([Start, "ich"]);
        neural.Next(["ich"]);

        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void Predict_UsesMoreThanTwoWordsOfContext()
    {
        // Ob "Grüßen" passt, steht hier vier Wörter weiter vorn ("Brief"): Das sieht nur das Netz.
        var vocab = Vocabulary();
        var engine = Engine(new NeuralLanguageModel(vocab, FakeNet(vocab)), null, ("Grüßen", 100), ("Gruß", 500));

        var predictions = engine.Predict(WordContext.FromTextWithSentence("Am Ende vom Brief steht mit freundlichen "));

        Assert.Equal("Grüßen", predictions[0]);
        var near = engine.Neural!.Next(WordContext.FromTextWithSentence("Ich sage mit freundlichen ").Tokens)!;
        var far = engine.Neural!.Next(WordContext.FromTextWithSentence("Am Ende vom Brief steht mit freundlichen ").Tokens)!;
        Assert.True(far.Probability("Grüßen") > 10 * near.Probability("Grüßen"));
    }

    [Fact]
    public void Predict_MixesBothModels()
    {
        var vocab = Vocabulary();
        var model = new NgramModel.Builder().Add("ich", "bin", 80).Add("ich", "habe", 20).Build();
        var engine = Engine(new NeuralLanguageModel(vocab, FakeNet(vocab)), model);

        var predictions = engine.PredictScored(WordContext.FromTextWithSentence("ich "), max: 3);

        Assert.Equal("habe", predictions[0].Word);             // beide Modelle kennen es
        Assert.Contains(predictions, p => p.Word == "bin");     // nur das N-Gramm-Modell
    }

    [Fact]
    public void Correction_FollowsTheSentence_WithTheNetOnly()
    {
        // "hae" liegt gleich nah an "hat" und "habe"; "hat" ist häufiger, nach "ich" passt laut Netz aber "habe".
        var vocab = Vocabulary();
        var without = Engine(null, null, ("hat", 3000), ("habe", 1000), ("ich", 5000));
        var with = Engine(new NeuralLanguageModel(vocab, FakeNet(vocab)), null, ("hat", 3000), ("habe", 1000), ("ich", 5000));

        var context = WordContext.FromTextWithSentence("ich ");

        Assert.Equal("hat", without.Suggest("hae", context).AutoCorrection);
        Assert.Equal("habe", with.Suggest("hae", context).AutoCorrection);
    }

    [Fact]
    public void SwitchedOff_OrBroken_TheKeyboardKeepsWorking()
    {
        var vocab = Vocabulary();
        bool enabled = false;
        int loads = 0;
        var gated = new NeuralLanguageModel(() => { loads++; return (vocab, FakeNet(vocab)); }, () => enabled, loadSynchronously: false);

        Assert.Null(gated.Next([Start, "ich"]));
        Assert.Equal(0, loads); // abgeschaltet: wird gar nicht erst geladen

        var broken = new NeuralLanguageModel(vocab, _ => throw new InvalidOperationException("kaputt"));
        var engine = Engine(broken, null, ("hat", 3000), ("habe", 1000), ("ich", 5000));
        Assert.Equal("hat", engine.Suggest("hae", WordContext.FromTextWithSentence("ich ")).AutoCorrection);
        Assert.IsType<InvalidOperationException>(broken.Error);
    }

    [Fact]
    public void LoadsInTheBackground_WithoutBlocking()
    {
        var vocab = Vocabulary();
        using var release = new ManualResetEventSlim();
        var neural = new NeuralLanguageModel(() => { release.Wait(); return (vocab, FakeNet(vocab)); });

        Assert.Null(neural.Next([Start, "ich"])); // lädt noch: keine Vorhersage, aber auch kein Warten
        release.Set();
        SpinWait.SpinUntil(() => neural.IsReady, TimeSpan.FromSeconds(5));
        Assert.Equal("habe", neural.Next([Start, "ich"])!.Top(1)[0].Word);
    }
}
