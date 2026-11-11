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
}
