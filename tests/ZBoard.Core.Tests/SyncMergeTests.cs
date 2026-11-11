using ZBoard.Backup;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

public class SyncMergeTests
{
    static UserHistory Learned(params (string Word, int Times)[] words)
    {
        var history = new UserHistory();
        foreach (var (word, times) in words)
        {
            for (int i = 0; i < times; i++) history.Learn(null, word);
        }

        return history;
    }

    [Fact]
    public void LearnedWordsAreUnitedWithHigherCount()
    {
        var phone = Learned(("hallo", 3), ("Zeriadtke", 1));
        var tablet = Learned(("hallo", 5), ("Tablet", 2));
        tablet.Learn("freundlichen", "Grüßen");

        Assert.True(phone.MergeFrom(tablet));

        Assert.Equal(5, phone.CountOf("hallo"));
        Assert.Equal(1, phone.CountOf("Zeriadtke"));
        Assert.Equal(2, phone.CountOf("Tablet"));
        Assert.Contains(phone.Followers("freundlichen"), f => f.Next == "Grüßen");
        Assert.True(phone.IsDirty);
    }

    [Fact]
    public void MergingTheSameAgainChangesNothing()
    {
        var phone = Learned(("hallo", 3));
        var cloud = Learned(("hallo", 2));
        phone.MergeFrom(cloud);
        using (var sink = new MemoryStream()) phone.Save(sink);

        Assert.False(phone.MergeFrom(cloud));
        Assert.False(phone.IsDirty);
        Assert.Equal(3, phone.CountOf("hallo"));
    }

    [Fact]
    public void LearnedFormOfThisDeviceIsKept()
    {
        var phone = Learned(("iPhone", 1));
        var cloud = new UserHistory();
        cloud.Learn(null, "IPHONE");
        cloud.Learn(null, "IPHONE");

        phone.MergeFrom(cloud);

        Assert.Equal(2, phone.CountOf("iphone"));
        Assert.Equal("iPhone", phone.LearnedForm("iphone"));
    }

    [Fact]
    public void EmailsAreUnited()
    {
        var phone = new EmailMemory();
        phone.Learn("anna@web.de", 100);
        var cloud = new EmailMemory();
        cloud.Learn("Anna@web.de", 200);
        cloud.Learn("Anna@web.de", 300);
        cloud.Learn("bob@gmx.net", 50);

        Assert.True(phone.MergeFrom(cloud));

        Assert.Equal(new[] { "Anna@web.de", "bob@gmx.net" }, phone.Addresses().Order());
        Assert.False(phone.MergeFrom(cloud));
    }

    [Fact]
    public void DeviceKeysAreNeverSynced()
    {
        var all = new Dictionary<string, object>
        {
            ["haptics"] = false,
            ["currentLanguage"] = "en",
            ["recentEmoji"] = "😀",
            ["enabledPlugins"] = "x",
            ["syncPlugin"] = "de.World_of_Bytes.zBoard.Sync",
            [BackupMerge.SettingsModifiedKey] = 5L,
        };

        Assert.Equal(new[] { "haptics" }, BackupMerge.SyncedSettings(all).Keys);
    }

    [Theory]
    [InlineData(100, false, 200, true)]   // Cloud neuer
    [InlineData(300, false, 200, false)]  // eigene neuer
    [InlineData(300, true, 200, true)]    // erster Abgleich: neues Gerät übernimmt die Cloud
    [InlineData(0, false, 0, false)]      // beide unbekannt: eigene behalten
    public void NewerSettingsWin(long local, bool firstSync, long remote, bool takeRemote)
    {
        var cloud = new BackupData { Settings = new Dictionary<string, object> { ["haptics"] = true }, SettingsModified = remote };
        Assert.Equal(takeRemote, BackupMerge.TakeRemoteSettings(local, firstSync, cloud));
    }

    [Fact]
    public void NoRemoteSettingsNothingToTake()
    {
        Assert.False(BackupMerge.TakeRemoteSettings(0, true, null));
        Assert.False(BackupMerge.TakeRemoteSettings(0, true, new BackupData { Learned = new Dictionary<string, UserHistory>() }));
    }

    [Fact]
    public void CombinedSettingsKeepDeviceValues()
    {
        var local = new Dictionary<string, object> { ["haptics"] = true, ["numberRow"] = true, ["currentLanguage"] = "fr", ["syncEnabled"] = true };
        var cloud = new BackupData
        {
            Settings = new Dictionary<string, object> { ["haptics"] = false, ["currentLanguage"] = "de", ["theme"] = 2 },
            SettingsModified = 777,
        };

        var combined = BackupMerge.CombineSettings(local, cloud);

        Assert.Equal(false, combined["haptics"]);
        Assert.False(combined.ContainsKey("numberRow")); // nicht in der Cloud: Standardwert
        Assert.Equal(2, combined["theme"]);
        Assert.Equal("fr", combined["currentLanguage"]);
        Assert.Equal(true, combined["syncEnabled"]);
        Assert.Equal(777L, combined[BackupMerge.SettingsModifiedKey]);
    }

    [Fact]
    public void SettingsModifiedSurvivesTheArchive()
    {
        using var file = new MemoryStream();
        BackupArchive.Write(file, new BackupData { Settings = new Dictionary<string, object>(), SettingsModified = 1_700_000_000_000 });
        file.Position = 0;

        Assert.Equal(1_700_000_000_000, BackupArchive.Read(file).SettingsModified);
    }
}
