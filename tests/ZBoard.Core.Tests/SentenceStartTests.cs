using ZBoard.Ime;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Automatische Großschreibung am Satzanfang.</summary>
public class SentenceStartTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("Hallo. ", true)]
    [InlineData("Wirklich? ", true)]
    [InlineData("Toll! ", true)]
    [InlineData("Na ja… ", true)]
    [InlineData("Er sagte „Ende.“ ", true)]
    [InlineData("(Das stimmt.) ", true)]
    [InlineData("Zeile eins\n", true)]
    [InlineData("Hallo.  ", true)]
    [InlineData("Hallo ", false)]
    [InlineData("Hallo.", false)]
    [InlineData("3,5 ", false)]
    [InlineData("Hallo, ", false)]
    public void Detects_SentenceStart(string before, bool expected)
    {
        Assert.Equal(expected, TextBoundaries.IsSentenceStart(before));
    }
}
