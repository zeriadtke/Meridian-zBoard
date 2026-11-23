using ZBoard.Ime;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Wörter in Links und Adressen werden nicht autokorrigiert (Text vor dem Wort, Wort).</summary>
public class UrlBoundaryTests
{
    [Theory]
    [InlineData("https://", "BeSoci", true)]            // Fehler aus dem Gerätetest: wurde zu "Besuch"
    [InlineData("https://Besocial.", "al", true)]
    [InlineData("Schau mal auf http://", "example", true)]
    [InlineData("ftp://server/", "daten", true)]
    [InlineData("www.", "zeriadtke", true)]
    [InlineData("Siehe WWW.", "beispiel", true)]
    [InlineData("zeriadtke.", "de", true)]
    [InlineData("github.com/", "World", true)]
    [InlineData("patrick@", "zeriadtke", true)]
    [InlineData("Danke @", "Niko", true)]
    [InlineData("", "https", true)]
    [InlineData("Link: ", "www", true)]
    [InlineData("https://BeSocial.de ", "Danke", false)] // nach dem Link geht normaler Text weiter
    [InlineData("", "Hallo", false)]
    [InlineData("Das ist ", "ein", false)]
    [InlineData("Ende. ", "Neuer", false)]
    [InlineData("und/", "oder", false)]
    [InlineData("Uhrzeit 12:", "30", false)]
    public void Detects_UrlOrAddress(string before, string word, bool expected)
    {
        Assert.Equal(expected, TextBoundaries.IsInUrlOrAddress(before, word));
    }
}
