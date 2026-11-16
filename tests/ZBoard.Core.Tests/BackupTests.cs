using System.IO.Compression;
using System.Text;
using ZBoard.Backup;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

public class BackupTests
{
    static BackupData RoundTrip(BackupData data)
    {
        using var file = new MemoryStream();
        BackupArchive.Write(file, data);
        file.Position = 0;
        return BackupArchive.Read(file);
    }

    static UserHistory Learned(params string[] words)
    {
        var history = new UserHistory();
        string? previous = null;
        foreach (var word in words)
        {
            history.Learn(previous, word);
            previous = word;
        }

        return history;
    }

    [Fact]
    public void RoundTripKeepsEverything()
    {
        var emails = new EmailMemory();
        emails.Learn("patrick@example.de", 1000);

        var restored = RoundTrip(new BackupData
        {
            AppVersion = "0.11.0",
            Created = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(1)),
            Settings = new Dictionary<string, object>
            {
                ["haptics"] = false,
                ["heightPercent"] = 115,
                ["someLong"] = 1234567890123L,
                ["someFloat"] = 1.5f,
                ["languages"] = "de,en,fr",
            },
            PinnedClips = "Zeile 1\nmit Umlauten äöü 😀",
            Learned = new Dictionary<string, UserHistory>
            {
                ["de"] = Learned("freundlichen", "Grüßen", "freundlichen", "Grüßen"),
                ["en"] = Learned("kind", "regards"),
            },
            Emails = emails,
        });

        Assert.Equal("0.11.0", restored.AppVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero), restored.Created.ToUniversalTime());

        var settings = restored.Settings!;
        Assert.Equal(false, settings["haptics"]);
        Assert.IsType<int>(settings["heightPercent"]);
        Assert.Equal(115, settings["heightPercent"]);
        Assert.IsType<long>(settings["someLong"]);
        Assert.Equal(1.5f, settings["someFloat"]);
        Assert.Equal("de,en,fr", settings["languages"]);

        Assert.Equal("Zeile 1\nmit Umlauten äöü 😀", restored.PinnedClips);

        Assert.Equal(new[] { "de", "en" }, restored.Learned!.Keys.Order());
        Assert.Equal(2, restored.Learned["de"].CountOf("Grüßen"));
        Assert.Contains(restored.Learned["de"].Followers("freundlichen"), f => f.Next == "Grüßen");
        Assert.Equal(1, restored.Learned["en"].CountOf("regards"));

        Assert.Equal(new[] { "patrick@example.de" }, restored.Emails!.Addresses());
    }

    [Fact]
    public void MissingPartsStayNull()
    {
        var restored = RoundTrip(new BackupData { Learned = new Dictionary<string, UserHistory>() });

        Assert.Null(restored.Settings);
        Assert.Null(restored.PinnedClips);
        Assert.Null(restored.Emails);
        Assert.NotNull(restored.Learned); // "gelernte Wörter gesichert, aber leer" ist etwas anderes als "nicht gesichert"
        Assert.Empty(restored.Learned);
    }

    [Fact]
    public void BackupDoesNotMarkLiveDataAsSaved()
    {
        var history = Learned("hallo");
        var emails = new EmailMemory();
        emails.Learn("a@b.de", 1);
        Assert.True(history.IsDirty);
        Assert.True(emails.IsDirty);

        RoundTrip(new BackupData { Learned = new Dictionary<string, UserHistory> { ["de"] = history }, Emails = emails });

        // Sonst würde die Tastatur ungespeichertes Gelerntes nicht mehr in ihre Datei schreiben.
        Assert.True(history.IsDirty);
        Assert.True(emails.IsDirty);
    }

    [Fact]
    public void ReplaceWithTakesOverContent()
    {
        var live = Learned("alt", "alt");
        var restored = Learned("neu", "wort");

        live.ReplaceWith(restored);

        Assert.Equal(0, live.CountOf("alt"));
        Assert.Equal(1, live.CountOf("neu"));
        Assert.Contains(live.Followers("neu"), f => f.Next == "wort");
        Assert.False(live.IsDirty);

        restored.Learn("neu", "anders"); // Kopie, keine geteilten Daten
        Assert.DoesNotContain(live.Followers("neu"), f => f.Next == "anders");
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        var error = Assert.Throws<BackupException>(() => BackupArchive.Read(new MemoryStream(Encoding.UTF8.GetBytes("kein zip"))));
        Assert.Equal(BackupError.NotABackup, error.Error);

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
            writer.Write("irgendein ZIP");
        }

        zip.Position = 0;
        Assert.Equal(BackupError.NotABackup, Assert.Throws<BackupException>(() => BackupArchive.Read(zip)).Error);
    }

    [Fact]
    public void RejectsNewerFormat()
    {
        var file = ArchiveWithManifest("""{"format":"zboard-backup","version":99,"contents":[]}""");
        Assert.Equal(BackupError.NewerVersion, Assert.Throws<BackupException>(() => BackupArchive.Read(file)).Error);
    }

    [Fact]
    public void RejectsMissingPart()
    {
        var file = ArchiveWithManifest("""{"format":"zboard-backup","version":1,"contents":["settings"]}""");
        Assert.Equal(BackupError.Damaged, Assert.Throws<BackupException>(() => BackupArchive.Read(file)).Error);
    }

    [Fact]
    public void IgnoresUnsafeLanguageFileNames()
    {
        var file = ArchiveWithManifest("""{"format":"zboard-backup","version":1,"contents":["learned"]}""",
            ("learning/../../evil.tsv", "w\tx\t1\n"),
            ("learning/DE.tsv", "w\tx\t1\n"),
            ("learning/de.tsv", "w\thallo\t3\n"));

        var restored = BackupArchive.Read(file);

        Assert.Equal(new[] { "de" }, restored.Learned!.Keys);
        Assert.Equal(3, restored.Learned["de"].CountOf("hallo"));
    }

    [Fact]
    public void SkipsSettingsWithWrongType()
    {
        var file = ArchiveWithManifest("""{"format":"zboard-backup","version":1,"contents":["settings"]}""",
            ("settings.json", """{"bool":{"haptics":"ja","glideTyping":true},"int":{"heightPercent":1.5},"color":{"x":1}}"""));

        var settings = BackupArchive.Read(file).Settings!;

        Assert.Equal(new Dictionary<string, object> { ["glideTyping"] = true }, settings);
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("fr", true)]
    [InlineData("emails", false)]
    [InlineData("../de", false)]
    [InlineData("", false)]
    public void ValidatesLanguageCodes(string code, bool expected) => Assert.Equal(expected, BackupArchive.IsLanguageCode(code));

    static MemoryStream ArchiveWithManifest(string manifest, params (string Name, string Content)[] entries)
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries.Prepend(("manifest.json", manifest)))
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        zip.Position = 0;
        return zip;
    }

    // ---------- Bei Pinyin gewählte Schriftzeichen ----------

    static PinyinLearned Chosen(params (string Pinyin, string Word, int Times)[] choices)
    {
        var learned = new PinyinLearned();
        foreach (var (pinyin, word, times) in choices)
        {
            for (int i = 0; i < times; i++) learned.Learn(pinyin, word);
        }

        return learned;
    }

    [Fact]
    public void PinyinChoices_AreSavedWithTheLearnedPart()
    {
        var restored = RoundTrip(new BackupData
        {
            Learned = new Dictionary<string, UserHistory> { ["zh"] = Learned("你好") },
            Pinyin = Chosen(("ni", "尼", 3), ("nihaohao", "你好好", 1)),
        });

        Assert.Equal(2, restored.Pinyin!.Count);
        Assert.Equal(3, restored.Pinyin.CountOf("ni", "尼"));
        Assert.Equal(1, restored.Pinyin.CountOf("nihaohao", "你好好"));
        Assert.Equal(new[] { "zh" }, restored.Learned!.Keys); // die Datei zählt nicht als Sprache "zh.pinyin"
    }

    [Fact]
    public void PinyinChoices_AreNotWrittenWithoutTheLearnedPart()
    {
        var restored = RoundTrip(new BackupData { PinnedClips = "x", Pinyin = Chosen(("ni", "尼", 1)) });
        Assert.Null(restored.Learned);
        Assert.Null(restored.Pinyin);
    }

    /// <summary>Sicherung ohne solche Datei (ältere Version, oder nie Chinesisch getippt): leer, nicht null – Wiederherstellen ersetzt.</summary>
    [Fact]
    public void BackupWithLearnedPartButNoPinyinChoices_ReadsAsEmpty()
    {
        var restored = RoundTrip(new BackupData { Learned = new Dictionary<string, UserHistory>(), Pinyin = new PinyinLearned() });
        Assert.NotNull(restored.Pinyin);
        Assert.Equal(0, restored.Pinyin.Count);
    }

    [Fact]
    public void PinyinChoices_Merge_TakesTheHigherCount_AndIsIdempotent()
    {
        var mine = Chosen(("ni", "你", 1), ("ni", "尼", 4));
        var theirs = Chosen(("ni", "你", 3), ("ma", "妈", 2));

        Assert.True(mine.MergeFrom(theirs));
        Assert.Equal(3, mine.CountOf("ni", "你"));
        Assert.Equal(4, mine.CountOf("ni", "尼"));
        Assert.Equal(2, mine.CountOf("ma", "妈"));
        Assert.True(mine.IsDirty);
        Assert.Equal(new[] { "尼", "你" }, mine.For("ni").Select(e => e.Word));

        Assert.False(mine.MergeFrom(theirs)); // zweimal abgleichen zählt nichts doppelt
        Assert.False(mine.MergeFrom(mine));
    }

    [Fact]
    public void PinyinChoices_ReplaceWith_DropsWhatWasThere()
    {
        var mine = Chosen(("ni", "尼", 4));
        mine.ReplaceWith(Chosen(("ma", "妈", 2)));
        Assert.Equal(0, mine.CountOf("ni", "尼"));
        Assert.Equal(2, mine.CountOf("ma", "妈"));
        Assert.False(mine.IsDirty);

        mine.ReplaceWith(new PinyinLearned());
        Assert.Equal(0, mine.Count);
    }

    /// <summary>Was aus der Sicherung kommt, wirkt in der Eingabe: das Gewählte steht wieder vorn.</summary>
    [Fact]
    public void RestoredPinyinChoices_RankCandidatesAgain()
    {
        var restored = RoundTrip(new BackupData
        {
            Learned = new Dictionary<string, UserHistory>(),
            Pinyin = Chosen(("ni", "尼", 2)),
        });

        var engine = PinyinEngine.FromEntries([("ni", "你", 100), ("ni", "尼", 10)]);
        Assert.Equal("你", engine.Candidates("ni")[0].Text);
        engine.Learned.ReplaceWith(restored.Pinyin!);
        Assert.Equal("尼", engine.Candidates("ni")[0].Text);
    }

    [Fact]
    public void PinyinChoices_SaveCopy_KeepsDirty_AndLoadSkipsBrokenLines()
    {
        var learned = Chosen(("ni", "尼", 2));
        using var copy = new MemoryStream();
        learned.SaveCopy(copy);
        Assert.True(learned.IsDirty);
        Assert.Equal("ni\t尼\t2\n", System.Text.Encoding.UTF8.GetString(copy.ToArray()));

        var loaded = PinyinLearned.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("ni\t尼\t2\n../x\t尼\t2\nni\t\t1\nni\t你\t-3\n")));
        Assert.Equal(1, loaded.Count);
        Assert.False(loaded.IsDirty);
    }
}
