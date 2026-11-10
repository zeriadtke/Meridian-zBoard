using ZBoard.Keyboard;
using Xunit;

namespace ZBoard.Core.Tests;

public class PopupGridTests
{
    static PopupCell C(int column, int row) => new(column, row);

    [Fact]
    public void RightEdge_LikeGboard_O()
    {
        // „o“ im Deutschen: rechts kein Platz → ö über der Taste, der Rest nach links, Rest eine Zeile höher
        var cells = PopupGrid.Place(9, leftRoom: 8, rightRoom: 0);
        Assert.Equal(new[] { C(0, 0), C(-1, 0), C(-2, 0), C(-3, 0), C(-4, 0), C(0, 1), C(-1, 1), C(-2, 1), C(-3, 1) }, cells);
    }

    [Fact]
    public void SevenCharacters_FourAndThree_LikeGboard_E()
    {
        // „e“ im Deutschen (Gboard-Screenshot): unten è 3 é ê, oben ē ë ė
        var cells = PopupGrid.Place(7, leftRoom: 2, rightRoom: 8);
        Assert.Equal(new[] { C(0, 0), C(1, 0), C(-1, 0), C(2, 0), C(0, 1), C(1, 1), C(-1, 1) }, cells);
    }

    [Fact]
    public void LeftEdge_GoesRight()
    {
        var cells = PopupGrid.Place(3, leftRoom: 0, rightRoom: 9);
        Assert.Equal(new[] { C(0, 0), C(1, 0), C(2, 0) }, cells);
    }

    [Fact]
    public void Middle_AlternatesRightThenLeft()
    {
        var cells = PopupGrid.Place(5, leftRoom: 4, rightRoom: 4);
        Assert.Equal(new[] { C(0, 0), C(1, 0), C(-1, 0), C(2, 0), C(-2, 0) }, cells);
    }

    [Fact]
    public void LittleRoom_UsesWhatIsThere()
    {
        // Rechts nur eine Spalte frei, links genug: 0, +1, dann nur noch links
        var cells = PopupGrid.Place(5, leftRoom: 6, rightRoom: 1);
        Assert.Equal(new[] { C(0, 0), C(1, 0), C(-1, 0), C(-2, 0), C(-3, 0) }, cells);
    }

    [Fact]
    public void FirstCharacterIsAboveTheKey_AndPreselected()
    {
        var cells = PopupGrid.Place(9, 8, 0);
        // Finger noch auf der Taste (unterhalb des Rasters, Spalte 0) → erstes Zeichen
        Assert.Equal(0, PopupGrid.IndexAt(cells, column: 0.1f, row: -1.5f));
    }

    [Fact]
    public void IndexAt_FollowsTheFinger()
    {
        var cells = PopupGrid.Place(9, 8, 0);
        Assert.Equal(3, PopupGrid.IndexAt(cells, -3.2f, 0.4f));     // unterste Zeile, dritte nach links
        Assert.Equal(6, PopupGrid.IndexAt(cells, -1f, 1.5f));       // obere Zeile
        Assert.Equal(8, PopupGrid.IndexAt(cells, -4f, 1.5f));       // obere Zeile ist kürzer → nächstes Zeichen
        Assert.Equal(5, PopupGrid.IndexAt(cells, 0f, 7f));          // weit darüber → oberste Zeile
        Assert.Equal(0, PopupGrid.IndexAt(cells, 3f, 0f));          // rechts daneben → nächstes Zeichen
    }

    [Theory]
    [InlineData("de", 1, "a", "ä â à á æ ã å ā")]
    [InlineData("de", 0, "i", "8")]
    [InlineData("de", 0, "e", "3 é è ê ë ė ē")]
    [InlineData("en", 0, "o", "9 ó ô ö ò œ ø ō õ")]
    [InlineData("fr", 0, "e", "é è ê ë 3 ę ė ē")]
    [InlineData("fr", 0, "a", "à â 1 æ á ä ã å ā ª")]
    [InlineData("es", 2, "n", "ñ ń")]
    [InlineData("it", 0, "o", "9 ò ó ô ö õ œ ø ō º")]
    public void Alternates_InGboardOrder(string language, int row, string key, string expected)
    {
        var k = Layouts.Alphabet(language, false).Rows[row].Keys.Single(k => k.Label == key);
        Assert.Equal(expected, string.Join(" ", k.Alternates));
    }

    [Fact]
    public void German_O_PrefersUmlaut_InGboardOrder()
    {
        var o = Layouts.Alphabet("de", false).Rows[0].Keys.Single(k => k.Label == "o");
        Assert.Equal(new[] { "ö", "9", "ô", "ò", "ó", "õ", "œ", "ø", "ō" }, o.Alternates);
    }
}
