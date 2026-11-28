using ZBoard.Ime;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Rücktaste bis an ein Wort heran nimmt es wieder in Bearbeitung (wie Gboard).</summary>
public class DeleteIntoWordTests
{
    [Theory]
    [InlineData("Haste rene ", "", "rene")]          // Leerzeichen löschen: Wort davor
    [InlineData("Haste rene ", " geht", "rene")]     // dahinter geht es mit Leerraum weiter
    [InlineData("Haste renee", "", "rene")]          // Buchstabe löschen: Rest des Worts
    [InlineData("Haste rene.", "", "rene")]          // Satzzeichen löschen
    [InlineData("Haste rene\n", "", "rene")]         // Zeilenumbruch löschen
    [InlineData("rene ", "", "rene")]                // Wort am Feldanfang
    [InlineData("Haste 'rene ", "", "rene")]         // Apostroph davor gehört nicht zum Wort
    [InlineData("Weg ist E-Mail ", "", "E-Mail")]    // Bindestrich im Wort
    [InlineData("Größe ", "", "Größe")]
    public void Resumes_word(string before, string after, string expected) =>
        Assert.Equal(expected, TextBoundaries.WordAfterDelete(before, after, windowFull: false));

    [Theory]
    [InlineData("Haste rene ", "Bescheid")]          // Cursor stünde mitten in "reneBescheid"
    [InlineData("Haste  ", "")]                      // davor nur Leerraum
    [InlineData("Haste 42 ", "")]                    // Zahl ohne Buchstaben
    [InlineData("a", "")]                            // letztes Zeichen im Feld
    [InlineData("", "")]
    [InlineData("mail@example ", "")]                // Teil einer Adresse
    [InlineData("www.example ", "")]
    [InlineData("#zboard ", "")]                     // Hashtag
    [InlineData("Hallo 😀", "")]                     // Emoji löscht der Editor
    [InlineData("Hallo ❤\uFE0F", "")]               // Variantenwahl
    public void Leaves_deletion_to_editor(string before, string after) =>
        Assert.Null(TextBoundaries.WordAfterDelete(before, after, windowFull: false));

    [Fact]
    public void Word_reaching_window_start_is_not_resumed() =>
        Assert.Null(TextBoundaries.WordAfterDelete("abschnittsweise ", "", windowFull: true));

    [Fact]
    public void Overlong_word_is_not_resumed() =>
        Assert.Null(TextBoundaries.WordAfterDelete(new string('a', 41) + " ", "", windowFull: false));
}
