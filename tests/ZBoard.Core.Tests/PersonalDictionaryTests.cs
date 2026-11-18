using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

public class PersonalDictionaryTests
{
    static PersonalDictionary Sample() => PersonalDictionary.Empty
        .With(new PersonalEntry("Mit freundlichen Grüßen", "mfg"))
        .With(new PersonalEntry("zBoard", ""))
        .With(new PersonalEntry("Zeriadtke", ""))
        .With(new PersonalEntry("patrick@example.org", "mail"));

    [Fact]
    public void ShortcutExpandsCaseInsensitive()
    {
        var d = Sample();
        Assert.Equal(new[] { "Mit freundlichen Grüßen" }, d.Expansions("mfg"));
        Assert.Equal(new[] { "Mit freundlichen Grüßen" }, d.Expansions("Mfg"));
        Assert.True(d.IsShortcut("MFG"));
        Assert.Empty(d.Expansions("mf"));
        Assert.False(d.IsShortcut("zBoard"));
        Assert.False(d.IsShortcut(""));
    }

    [Fact]
    public void OneShortcutCanHaveSeveralTexts()
    {
        var d = Sample().With(new PersonalEntry("Mit freundlichem Gruß", "mfg"));
        Assert.Equal(new[] { "Mit freundlichem Gruß", "Mit freundlichen Grüßen" }, d.Expansions("mfg"));
    }

    [Theory]
    [InlineData("zboard", "zBoard")]
    [InlineData("ZBOARD", "zBoard")]
    [InlineData("zBoard", "zBoard")]
    [InlineData("zeriadtke", "Zeriadtke")]
    [InlineData("zboar", null)]
    [InlineData("mfg", null)] // Kürzel sind keine Wörter
    public void OwnWordKeepsItsSpelling(string typed, string? expected) => Assert.Equal(expected, Sample().Word(typed));

    [Fact]
    public void ExactSpellingWinsOverAnotherCase()
    {
        var d = PersonalDictionary.Empty.With(new PersonalEntry("Bar", "")).With(new PersonalEntry("bar", ""));
        Assert.Equal("bar", d.Word("bar"));
        Assert.Equal("Bar", d.Word("Bar"));
    }

    [Fact]
    public void TextsWithSpacesAreNotSingleWords()
    {
        var d = PersonalDictionary.Empty.With(new PersonalEntry("Guten Tag", ""));
        Assert.Null(d.Word("Guten Tag"));
        Assert.Equal(new[] { "Guten Tag" }, d.Completions("gut", 3));
    }

    [Fact]
    public void CompletionsNeedTwoLettersAndSkipTheFullWord()
    {
        var d = Sample();
        Assert.Empty(d.Completions("z", 3));
        Assert.Equal(new[] { "zBoard", "Zeriadtke" }, d.Completions("zb", 3).Concat(d.Completions("ze", 3)));
        Assert.Empty(d.Completions("zboard", 3));
        Assert.Single(d.Completions("pa", 1));
    }

    [Fact]
    public void SerializeAndParseRoundTrip()
    {
        var d = Sample();
        string raw = d.Serialize();
        Assert.Contains("Mit freundlichen Grüßen\tmfg", raw);
        var back = PersonalDictionary.Parse(raw);
        Assert.Equal(d.Entries, back.Entries);
        Assert.Empty(PersonalDictionary.Parse(null).Entries);
        Assert.Empty(PersonalDictionary.Parse("").Entries);
    }

    [Fact]
    public void ParseSkipsBrokenLinesAndDuplicates()
    {
        var d = PersonalDictionary.Parse("Haus\n\n  \nHaus\nText\tk!\nBoot\tbt\n" + new string('x', PersonalDictionary.MaxWordLength + 1));
        Assert.Equal(new[] { new PersonalEntry("Boot", "bt"), new PersonalEntry("Haus", "") }, d.Entries);
    }

    [Theory]
    [InlineData("  Hallo   Welt ", " hw ", "Hallo Welt", "hw")]
    [InlineData("Zeile1\nZeile2\tEnde", "", "Zeile1 Zeile2 Ende", "")]
    [InlineData("Wort", "Wort", "Wort", "")]
    [InlineData("Straße", "str1", "Straße", "str1")]
    public void NormalizeCleansInput(string word, string shortcut, string expectedWord, string expectedShortcut) =>
        Assert.Equal(new PersonalEntry(expectedWord, expectedShortcut), PersonalDictionary.Normalize(word, shortcut));

    [Theory]
    [InlineData("", "x")]
    [InlineData("   ", "")]
    [InlineData("Wort", "zwei wörter")]
    [InlineData("Wort", "a-b")]
    [InlineData("Wort", "a'b")]
    public void NormalizeRejectsInvalidInput(string word, string shortcut) =>
        Assert.Null(PersonalDictionary.Normalize(word, shortcut));

    [Fact]
    public void RejectsTooLongShortcut() =>
        Assert.Null(PersonalDictionary.Normalize("Wort", new string('a', PersonalDictionary.MaxShortcutLength + 1)));

    [Fact]
    public void EditReplacesTheOldEntryAndRemoveDeletes()
    {
        var old = new PersonalEntry("Mit freundlichen Grüßen", "mfg");
        var d = Sample().With(new PersonalEntry("Mit freundlichen Grüßen", "mfgr"), replaces: old);
        Assert.Empty(d.Expansions("mfg"));
        Assert.Single(d.Expansions("mfgr"));
        Assert.Equal(4, d.Entries.Count);

        d = d.Without(new PersonalEntry("zBoard", ""));
        Assert.Null(d.Word("zboard"));
        Assert.Equal(3, d.Entries.Count);
    }

    [Fact]
    public void EntriesAreSortedByWord()
    {
        Assert.Equal(new[] { "Mit freundlichen Grüßen", "patrick@example.org", "zBoard", "Zeriadtke" }, Sample().Entries.Select(e => e.Word));
    }
}
