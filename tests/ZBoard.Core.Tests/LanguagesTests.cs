using Xunit;
using ZBoard.Emoji;
using ZBoard.Keyboard;
using ZBoard.Suggestions;

namespace ZBoard.Core.Tests;

/// <summary>Alle Sprachen der Tastatur: Layout, Wörterbuch, Sprachmodell und Emoji-Suche passen zusammen.</summary>
public class LanguagesTests
{
    public static readonly string[] All = [.. Layouts.LanguageNames.Keys];

    static string Asset(params string[] parts) => Path.Combine([RepositoryRoot(), "Assets", .. parts]);

    static WordDictionary Dictionary(string language)
    {
        using var stream = File.OpenRead(Asset("dict", $"{language}.txt"));
        return WordDictionary.Load(stream, WordDictionary.ReferenceTotal, language);
    }

    static SuggestionEngine Engine(string language) =>
        new(Dictionary(language), KeyProximity.FromLayout(Layouts.Alphabet(language, showLanguageKey: false)));

    static IEnumerable<string> TopWords(string language, int count) =>
        File.ReadLines(Asset("dict", $"{language}.txt")).Where(l => !l.StartsWith('#')).Take(count).Select(l => l.Split('\t')[0]);

    [Fact]
    public void EighteenLanguages() => Assert.Equal(18, All.Length);

    [Fact]
    public void EveryLanguage_HasLayoutDictionaryModelAndEmojiKeywords()
    {
        foreach (var language in All)
        {
            Assert.Equal(4, Layouts.Alphabet(language, showLanguageKey: true).Rows.Count);
            Assert.Equal(5, Layouts.Alphabet(language, showLanguageKey: true, numberRow: true).Rows.Count);
            Assert.True(Dictionary(language).Count > 30_000, language);
            using (var stream = File.OpenRead(Asset("dict", $"{language}.ngrams.bin")))
            {
                Assert.True(NgramModel.Load(stream).BigramCount > 10_000, language);
            }

            Assert.True(File.Exists(Asset("emoji", $"keywords.{language}.txt")), language);
            Assert.True(language == "en" || Layouts.LocaleTag(language) != "en-US", language);
        }
    }

    /// <summary>Jede Buchstabenreihe füllt die ganze Breite; sonst stünden die Tasten linksbündig mit Lücke rechts.</summary>
    [Fact]
    public void EveryLanguage_LetterRowsFillTheWidth()
    {
        foreach (var language in All)
        {
            var layout = Layouts.Alphabet(language, showLanguageKey: false);
            foreach (var row in layout.Rows.Take(3))
            {
                Assert.Equal(layout.Units, 2 * row.Indent + row.Keys.Sum(k => k.Width));
            }
        }
    }

    [Fact]
    public void EveryLanguage_TopRowCarriesTheDigits()
    {
        foreach (var language in All)
        {
            var top = Layouts.Alphabet(language, showLanguageKey: false).Rows[0].Keys;
            for (int i = 0; i < 10; i++)
            {
                Assert.Contains(((i + 1) % 10).ToString(), top[i].Alternates);
            }
        }
    }

    /// <summary>
    /// Die 3000 häufigsten Wörter jeder Sprache lassen sich tippen: Jeder Buchstabe steht auf einer Taste oder
    /// in einem Popup. Findet vergessene Buchstaben (ő, ґ, ự …).
    /// </summary>
    [Fact]
    public void EveryLanguage_CommonWordsCanBeTyped()
    {
        foreach (var language in All.Where(l => l != Layouts.Chinese))
        {
            var keys = Layouts.Alphabet(language, showLanguageKey: false).Rows.SelectMany(r => r.Keys).Where(k => k.IsCharacter);
            var typable = keys.SelectMany(k => k.Alternates.Append(k.Label)).SelectMany(s => s).ToHashSet();
            var missing = TopWords(language, 3000).SelectMany(w => TextCase.Lower(w, language))
                .Where(c => c is not ('\'' or '-') && !typable.Contains(c)).Distinct().Order();
            Assert.Equal(string.Empty, $"{string.Concat(missing)}");
        }
    }

    [Fact]
    public void OnlyArabic_HasNoShiftKey()
    {
        foreach (var language in All)
        {
            bool shift = Layouts.Alphabet(language, false).Rows.SelectMany(r => r.Keys).Any(k => k.Action == KeyAction.Shift);
            Assert.Equal(language != "ar", shift);
        }
    }

    [Theory]
    [InlineData("ru", "й")]
    [InlineData("uk", "й")]
    [InlineData("bg", "я")]
    [InlineData("el", ";")]
    [InlineData("ar", "ض")]
    [InlineData("tr", "q")]
    [InlineData("cs", "q")]
    [InlineData("zh", "q")]
    public void FirstKey(string language, string expected) =>
        Assert.Equal(expected, Layouts.Alphabet(language, false).Rows[0].Keys[0].Label);

    [Fact]
    public void ArabicAndChinese_HaveTheirOwnPunctuationKeys()
    {
        var arabic = Layouts.Alphabet("ar", false).Rows[^1].Keys;
        Assert.Contains(arabic, k => k.Label == "،");
        Assert.Contains("؟", arabic.Single(k => k.Label == ".").Alternates);
        var chinese = Layouts.Alphabet("zh", false).Rows[^1].Keys;
        Assert.Contains(chinese, k => k.Label == "，");
        Assert.Contains(chinese, k => k.Label == "。");
    }

    // ---------- Mehrsprachige Eingabe ----------

    [Fact]
    public void MultilingualTyping_CombinesOnlyLanguagesOfTheSameScript()
    {
        string[] enabled = ["de", "ru", "pl", "uk", "zh", "el", "bg", "en"];
        Assert.Equal(["ru", "uk", "bg"], MultilingualEngine.ActiveLanguages("ru", enabled, multilingual: true));
        Assert.Equal(["pl", "de", "en"], MultilingualEngine.ActiveLanguages("pl", enabled, multilingual: true));
        Assert.Equal(["el"], MultilingualEngine.ActiveLanguages("el", enabled, multilingual: true));
        Assert.Equal(["zh"], MultilingualEngine.ActiveLanguages("zh", enabled, multilingual: true));
    }

    // ---------- Türkisch: i/İ und ı/I ----------

    [Fact]
    public void Turkish_DottedAndDotlessI()
    {
        Assert.Equal("İSTANBUL", TextCase.Upper("istanbul", "tr"));
        Assert.Equal("IŞIK", TextCase.Upper("ışık", "tr"));
        Assert.Equal("ışık", TextCase.Lower("IŞIK", "tr"));
        Assert.Equal("istanbul", TextCase.Lower("İSTANBUL", "tr"));
        Assert.Equal("ISTANBUL", TextCase.Upper("istanbul", "de"));
        Assert.Equal("İstanbul", SuggestionEngine.MatchCase("istanbul", "İst", "tr"));
        Assert.Equal("Işık", SuggestionEngine.MatchCase("ışık", "Iş", "tr"));
    }

    [Fact]
    public void Turkish_CapitalizedWordsAreFoundInTheDictionary()
    {
        var dictionary = Dictionary("tr");
        Assert.True(dictionary.Contains("Işık"));     // ışık
        Assert.True(dictionary.Contains("İSTANBUL")); // istanbul
        Assert.True(dictionary.Contains("IŞIK"));
    }

    // ---------- Buchstaben mit Zusatzzeichen ----------

    [Theory]
    [InlineData("zolw", "żółw")]
    [InlineData("viet", "việt")]
    [InlineData("ειναι", "είναι")]
    [InlineData("еще", "ещё")]
    [InlineData("multumesc", "mulțumesc")]
    [InlineData("انا", "أنا")]
    public void IsLazySpelling_CoversEveryLanguage(string typed, string word) =>
        Assert.True(KeyProximity.IsLazySpelling(typed, word));

    [Fact]
    public void IsLazySpelling_DoesNotConfuseDifferentCyrillicLetters() =>
        Assert.False(KeyProximity.IsLazySpelling("и", "й"));

    /// <summary>Ohne Akzente getippt: Die Autokorrektur setzt die richtige Schreibweise ein (echte Wortlisten).</summary>
    [Theory]
    [InlineData("pl", "dziekuje", "dziękuję")]
    [InlineData("cs", "dekuji", "děkuji")]
    [InlineData("hu", "koszonom", "köszönöm")]
    [InlineData("ro", "multumesc", "mulțumesc")]
    [InlineData("tr", "tesekkurler", "teşekkürler")]
    [InlineData("vi", "khong", "không")]
    [InlineData("el", "ευχαριστω", "ευχαριστώ")]
    public void Autocorrect_AddsTheMissingMarks(string language, string typed, string expected) =>
        Assert.Equal(expected, Engine(language).Suggest(typed).AutoCorrection);

    [Theory]
    [InlineData("ru", "спасмбо", "спасибо")]
    [InlineData("uk", "дякцю", "дякую")]
    [InlineData("bg", "благодпря", "благодаря")]
    [InlineData("nl", "bedsnkt", "bedankt")]
    [InlineData("ar", "شكرس", "شكرا")]
    public void Autocorrect_FixesNeighbouringKeys(string language, string typed, string expected) =>
        Assert.Equal(expected, Engine(language).Suggest(typed).AutoCorrection);

    [Theory]
    [InlineData("ru", "спасибо")]
    [InlineData("uk", "дякую")]
    [InlineData("bg", "благодаря")]
    [InlineData("ar", "شكرا")]
    [InlineData("el", "ευχαριστώ")]
    [InlineData("nl", "bedankt")]
    public void CorrectWords_StayAsTyped(string language, string word) =>
        Assert.Null(Engine(language).Suggest(word).AutoCorrection);

    // ---------- Vorhersage ----------

    [Theory]
    [InlineData("ru", "Я не ")]
    [InlineData("uk", "Я не ")]
    [InlineData("pl", "Nie wiem ")]
    [InlineData("tr", "Bu bir ")]
    [InlineData("nl", "Ik heb ")]
    [InlineData("cs", "To je ")]
    [InlineData("hu", "Nem tudom ")]
    [InlineData("bg", "Това е ")]
    [InlineData("ar", "لا أعرف ")]
    [InlineData("ro", "Nu am ")]
    [InlineData("el", "Δεν είναι ")]
    [InlineData("vi", "Tôi không ")]
    [InlineData("zh", "我们 ")]
    public void Model_PredictsANextWord(string language, string before)
    {
        using var stream = File.OpenRead(Asset("dict", $"{language}.ngrams.bin"));
        Assert.True(NgramModel.Load(stream).Predict(WordContext.FromText(before)).Count > 0);
    }

    // ---------- Emoji ----------

    [Theory]
    [InlineData("ru", "улыбка")]
    [InlineData("pl", "serce")]
    [InlineData("tr", "kalp")]
    [InlineData("ar", "قلب")]
    [InlineData("el", "καρδιά")]
    [InlineData("zh", "xiaolian")] // 笑脸, mit der Pinyin-Tastatur getippt
    public void EmojiSearch_FindsEmojiInTheLanguage(string language, string query)
    {
        using var emoji = File.OpenRead(Asset("emoji", "emoji.txt"));
        using var keywords = File.OpenRead(Asset("emoji", $"keywords.{language}.txt"));
        Assert.True(EmojiCatalog.Load(emoji, keywords).Search(query).Count > 0);
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

/// <summary>Chinesisch mit Pinyin: die mitgelieferte Tabelle und die Auswahl der Schriftzeichen.</summary>
public class PinyinTests
{
    static readonly Lazy<PinyinEngine> Table = new(() =>
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "Assets", "dict", "zh.pinyin.txt");
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                return PinyinEngine.Load(stream);
            }
        }

        throw new InvalidOperationException("zh.pinyin.txt nicht gefunden.");
    });

    static PinyinEngine Small() => PinyinEngine.FromEntries(
    [
        ("ni", "你", 100), ("ni", "尼", 10), ("hao", "好", 80), ("hao", "号", 20), ("nihao", "你好", 50),
        ("ma", "吗", 60), ("ma", "妈", 30), ("nihaoma", "你好吗", 5), ("nin", "您", 40),
    ]);

    [Theory]
    [InlineData("nihao", "你好")]
    [InlineData("zhongguo", "中国")]
    [InlineData("women", "我们")]
    [InlineData("xiexie", "谢谢")]
    [InlineData("wo", "我")]
    [InlineData("lv", "绿")]
    [InlineData("Ni", "你")]
    public void Table_BestCandidate(string typed, string expected)
    {
        var best = Table.Value.Candidates(typed)[0];
        Assert.Equal(expected, best.Text);
        Assert.Equal(typed.Length, best.Consumed);
    }

    [Fact]
    public void Table_ConvertsWholeSentences()
    {
        Assert.Equal("我们是朋友", Table.Value.Convert("womenshipengyou"));
        Assert.Equal(new PinyinCandidate("你好吗", 7), Small().Candidates("nihaoma")[0]);
        Assert.Equal("我们是朋友", Table.Value.Candidates("womenshipengyou")[0].Text);
    }

    [Fact]
    public void Candidates_ExactWordsFirst_ThenCompletions_ThenShorterWords()
    {
        var candidates = Small().Candidates("nihao");
        Assert.Equal(
            [new("你好", 5), new("你好吗", 5), new("你", 2), new("尼", 2)],
            candidates);
    }

    [Fact]
    public void Candidates_SentenceFromLongestWords_WhenNoWordMatches()
    {
        var candidates = Small().Candidates("nihaohao");
        Assert.Equal(new PinyinCandidate("你好好", 8), candidates[0]);
        Assert.Contains(new PinyinCandidate("你好", 5), candidates);
    }

    [Fact]
    public void Candidates_SameSpellingByFrequency()
    {
        Assert.Equal(["吗", "妈"], Small().Candidates("ma").Select(c => c.Text));
    }

    [Fact]
    public void Candidates_ShorterWordLeavesTheRestToType()
    {
        var you = Small().Candidates("nix").Single(c => c.Text == "你");
        Assert.Equal(2, you.Consumed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ni3")]
    [InlineData("e-mail")]
    [InlineData("你")]
    public void Candidates_OnlyForLetters(string typed) => Assert.Empty(Small().Candidates(typed));

    [Fact]
    public void Convert_KeepsWhatIsNotPinyin()
    {
        Assert.Equal("你好xq", Small().Convert("nihaoxq"));
        Assert.Equal("xq", Small().Convert("xq"));
        Assert.Equal("WLAN-1", Small().Convert("WLAN-1"));
    }

    [Fact]
    public void Candidates_RespectMax() => Assert.Equal(10, Table.Value.Candidates("shi", max: 10).Count);

    [Fact]
    public void Table_CompletesUnfinishedWords() =>
        Assert.Contains("中国", Table.Value.Candidates("zhongg").Select(c => c.Text));

    // ---------- Lernen ----------

    [Fact]
    public void Learn_ChosenOnce_MovesToSecondPlace_Twice_ToFirst()
    {
        var engine = Small();
        engine.Learn("ni", "尼");
        Assert.Equal(["你", "尼"], engine.Candidates("ni").Take(2).Select(c => c.Text));
        Assert.Equal("你", engine.Convert("ni"));
        engine.Learn("ni", "尼");
        Assert.Equal(["尼", "你"], engine.Candidates("ni").Take(2).Select(c => c.Text));
        Assert.Equal("尼", engine.Convert("ni"));
    }

    [Fact]
    public void Learn_KeepsPhrasesThatAreNotInTheTable()
    {
        var engine = Small();
        engine.Learn("nihaohao", "你好好");
        Assert.Equal(new PinyinCandidate("你好好", 8), engine.Candidates("nihaohao")[0]);
        Assert.Equal(1, engine.Candidates("nihaohao").Count(c => c.Text == "你好好"));
        Assert.Equal("你好好吗", engine.Convert("nihaohaoma"));
    }

    [Fact]
    public void Learned_SurvivesSaveAndLoad_AndCanBeCleared()
    {
        var engine = Small();
        engine.Learn("ma", "妈");
        engine.Learn("ma", "妈");
        Assert.True(engine.Learned.IsDirty);
        using var saved = new MemoryStream();
        engine.Learned.Save(saved);
        Assert.False(engine.Learned.IsDirty);

        var other = Small();
        other.Learned.ReplaceWith(PinyinLearned.Load(new MemoryStream(saved.ToArray())));
        Assert.Equal("妈", other.Candidates("ma")[0].Text);
        Assert.False(other.Learned.IsDirty);

        other.Learned.Clear();
        Assert.Equal("吗", other.Candidates("ma")[0].Text);
    }

    [Fact]
    public void LoadLearned_IgnoresBrokenLines()
    {
        var engine = Small();
        engine.Learned.ReplaceWith(PinyinLearned.Load(new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("ma\t妈\t5\nkaputt\n\nma\t\t3\nm1\t妈\t2\nma\t麻\tx\nMA\t马\t9\n"))));
        Assert.Equal(["妈", "吗"], engine.Candidates("ma").Select(c => c.Text));
    }
}

public class CandidateStripTests
{
    [Fact]
    public void EntriesAreAsWideAsTheirText_ButAtLeastAFingerWide()
    {
        var strip = new CandidateStrip([10, 100, 10], padding: 12, minWidth: 44);
        Assert.Equal((0f, 44f), strip.Bounds(0));
        Assert.Equal((44f, 168f), strip.Bounds(1));
        Assert.Equal(212f, strip.TotalWidth);
        Assert.Equal(3, strip.Count);
    }

    [Fact]
    public void Scroll_StaysBetweenStartAndEnd()
    {
        var strip = new CandidateStrip([10, 100, 10], 12, 44);
        Assert.Equal(0f, strip.Clamp(-30, 100));
        Assert.Equal(112f, strip.Clamp(500, 100));
        Assert.Equal(50f, strip.Clamp(50, 100));
        Assert.Equal(0f, strip.Clamp(50, 400)); // alles passt: nichts zu schieben
    }

    [Fact]
    public void Visible_ListsPartlyVisibleEntries()
    {
        var strip = new CandidateStrip([10, 100, 10], 12, 44);
        Assert.Equal((0, 2), strip.Visible(0, 100));
        Assert.Equal((1, 3), strip.Visible(50, 150));
        Assert.Equal((2, 3), strip.Visible(168, 100));
        Assert.Equal((0, 0), new CandidateStrip([], 12, 44).Visible(0, 100));
    }
}
