using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

public class EmailMemoryTests
{
    const long Day = 86_400_000;

    [Theory]
    [InlineData("patrick@zeriadtke.de", true)]
    [InlineData("a.b+c@sub.example.co.uk", true)]
    [InlineData("patrick@gmail", false)]
    [InlineData("@gmail.com", false)]
    [InlineData("a..b@x.de", false)]
    [InlineData("nicht eine adresse", false)]
    public void RecognizesAddresses(string text, bool expected) => Assert.Equal(expected, EmailMemory.IsAddress(text));

    [Fact]
    public void LearnsAllAddressesInField()
    {
        var m = new EmailMemory();
        Assert.Equal(2, m.LearnFrom("Anna <anna@web.de>, bob@gmx.net; kaputt@x", 1));
        Assert.Equal(new[] { "anna@web.de", "bob@gmx.net" }, m.Addresses().Order());
        Assert.Equal(0, m.LearnFrom("www.x@y.de/pfad", 2));
    }

    [Fact]
    public void EmptyFieldSuggestsMostUsedFirst()
    {
        var m = new EmailMemory();
        m.Learn("selten@web.de", 1);
        m.Learn("oft@gmail.com", 2);
        m.Learn("oft@gmail.com", 3);
        var s = m.Suggest("");
        Assert.Equal("oft@gmail.com", s[0].Address);
        Assert.Equal("selten@web.de", s[1].Address);
    }

    [Fact]
    public void PrefixMatchesSavedAddressCaseInsensitive()
    {
        var m = new EmailMemory();
        m.Learn("zeriadtke@gmail.com", 1);
        var s = m.Suggest("Zeri");
        Assert.Single(s);
        Assert.Equal("zeriadtke@gmail.com", s[0].Address);
        Assert.Empty(m.Suggest("zeriadtke@gmail.com")); // schon vollständig
    }

    [Fact]
    public void AfterAtSuggestsDomains()
    {
        var m = new EmailMemory();
        m.Learn("ich@posteo.de", 1);
        var s = m.Suggest("max@");
        Assert.Equal(3, s.Count);
        Assert.Equal(new EmailSuggestion("@posteo.de", "max@posteo.de"), s[0]); // eigene Domain zuerst
        Assert.Equal("max@gmail.com", s[1].Address);

        var gm = m.Suggest("max@gm");
        Assert.All(gm, x => Assert.StartsWith("max@gm", x.Address));
        Assert.Contains(gm, x => x.Address == "max@gmx.de");
    }

    [Fact]
    public void RemoveAndClear()
    {
        var m = new EmailMemory();
        m.Learn("a@b.de", 1);
        m.Learn("c@d.de", 1);
        Assert.True(m.Remove("A@B.DE"));
        Assert.Equal(new[] { "c@d.de" }, m.Addresses());
        m.Clear();
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var m = new EmailMemory();
        m.Learn("a@b.de", 5 * Day);
        m.Learn("a@b.de", 6 * Day);
        m.Learn("c@d.de", 7 * Day);
        var stream = new MemoryStream();
        m.Save(stream);
        Assert.False(m.IsDirty);

        var loaded = EmailMemory.Load(new MemoryStream(stream.ToArray()));
        Assert.Equal(m.Addresses(), loaded.Addresses());
        Assert.Equal("a@b.de", loaded.Addresses()[0]);
    }

    [Fact]
    public void KeepsAtMostFiftyAddresses()
    {
        var m = new EmailMemory();
        for (int i = 0; i < 60; i++) m.Learn($"user{i}@x.de", i);
        Assert.Equal(50, m.Count);
    }
}
