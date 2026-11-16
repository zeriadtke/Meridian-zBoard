using Xunit;
using ZBoard.Suggestions;

namespace ZBoard.Core.Tests;

public class TelexTests
{
    static string Type(string keys) => keys.Aggregate(string.Empty, Telex.Apply);

    [Theory]
    [InlineData("tieengs", "tiếng")]
    [InlineData("vieetj", "việt")]
    [InlineData("Vieetj", "Việt")]
    [InlineData("khoong", "không")]
    [InlineData("toi", "toi")]
    [InlineData("tooi", "tôi")]
    [InlineData("ddi", "đi")]
    [InlineData("dduowcj", "được")]
    [InlineData("nguowif", "người")]
    [InlineData("muwa", "mưa")]
    [InlineData("tw", "tư")]
    [InlineData("cuar", "của")]
    [InlineData("quar", "quả")]
    [InlineData("gif", "gì")]
    [InlineData("giaf", "già")]
    [InlineData("hoaf", "hòa")]
    [InlineData("toans", "toán")]
    [InlineData("tosan", "toán")] // Ton früh getippt: rückt mit dem nächsten Buchstaben an seine Stelle
    [InlineData("ngoaif", "ngoài")]
    [InlineData("thuyr", "thủy")]
    [InlineData("hoawcj", "hoặc")]
    [InlineData("truwowngf", "trường")]
    [InlineData("tieng", "tieng")]
    [InlineData("tienge", "tiêng")]  // Zusatzzeichen auch nach dem Auslaut
    [InlineData("camr", "cảm")]
    [InlineData("own", "ơn")]
    [InlineData("anh", "anh")]
    [InlineData("AS", "Á")]
    [InlineData("DDAF", "ĐÀ")]
    public void Types(string keys, string expected) => Assert.Equal(expected, Type(keys));

    [Theory]
    [InlineData("ass", "as")]
    [InlineData("class", "clas")]
    [InlineData("classs", "class")]
    [InlineData("aaa", "aa")]
    [InlineData("ddd", "dd")]
    [InlineData("tuww", "tuw")]
    [InlineData("masz", "ma")]
    [InlineData("masf", "mà")] // anderer Ton ersetzt den ersten
    public void SameKeyAgain_TakesItBack(string keys, string expected) => Assert.Equal(expected, Type(keys));

    [Theory]
    [InlineData("online", "online")]
    [InlineData("idea", "idea")]
    [InlineData("str", "str")]
    public void WordsWithSeveralVowelGroupsOrNone_StayAsTyped(string keys, string expected) =>
        Assert.Equal(expected, Type(keys));

    /// <summary>Einsilbige fremde Wörter verändert Telex wie jede Telex-Tastatur; dieselbe Taste noch einmal hilft.</summary>
    [Theory]
    [InlineData("zoo", "zô")]
    [InlineData("zooo", "zoo")]
    [InlineData("data", "dât")]
    public void SingleSyllableForeignWords_AreTransformed(string keys, string expected) =>
        Assert.Equal(expected, Type(keys));

    [Fact]
    public void DigitsAndPunctuation_AreAppended()
    {
        Assert.Equal("a1", Telex.Apply("a", '1'));
        Assert.Equal("a1s", Telex.Apply("a1", 's'));
    }
}
