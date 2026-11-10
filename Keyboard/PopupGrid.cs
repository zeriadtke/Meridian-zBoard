namespace ZBoard.Keyboard;

/// <summary>Position eines Zeichens im Popup: Spalte relativ zur gedrückten Taste (0 = direkt darüber) und Zeile
/// (0 = unterste, direkt über der Tastatur).</summary>
public readonly record struct PopupCell(int Column, int Row);

/// <summary>
/// Anordnung der Zusatzzeichen beim langen Druck wie bei Gboard: ein Raster mit höchstens fünf Spalten, die Zeichen
/// gleichmäßig auf die Zeilen verteilt.
/// Das erste Zeichen (das häufigste, z. B. „ö“ auf „o“) steht direkt über der Taste und ist vorausgewählt,
/// die weiteren abwechselnd rechts und links daneben – nur so weit, wie auf der Seite Platz ist. Was nicht in
/// die unterste Zeile passt, kommt in dieselben Spalten eine Zeile höher.
/// Plattformunabhängig, damit es Unit-Tests gibt.
/// </summary>
public static class PopupGrid
{
    public const int MaxColumns = 5;

    /// <param name="count">Anzahl der Zeichen.</param>
    /// <param name="leftRoom">Wie viele Spalten links neben der Taste noch auf den Bildschirm passen.</param>
    /// <param name="rightRoom">Wie viele Spalten rechts neben der Taste noch passen.</param>
    public static IReadOnlyList<PopupCell> Place(int count, int leftRoom, int rightRoom, int maxColumns = MaxColumns)
    {
        if (count <= 0) return [];
        leftRoom = Math.Max(0, leftRoom);
        rightRoom = Math.Max(0, rightRoom);
        // Wie Gboard gleichmäßig auf die Zeilen verteilen: 7 Zeichen → 4 + 3, 9 Zeichen → 5 + 4
        int room = leftRoom + rightRoom + 1;
        int rows = (count + maxColumns - 1) / maxColumns;
        int columns = Math.Min((count + rows - 1) / rows, room);

        // Spaltenreihenfolge: 0, dann abwechselnd rechts und links, solange die Seite Platz hat
        var order = new List<int>(columns) { 0 };
        int right = 0, left = 0;
        while (order.Count < columns)
        {
            bool canRight = right < rightRoom, canLeft = left < leftRoom;
            bool takeRight = canRight && (!canLeft || right <= left);
            if (takeRight) order.Add(++right);
            else order.Add(-++left);
        }

        var cells = new PopupCell[count];
        for (int i = 0; i < count; i++) cells[i] = new PopupCell(order[i % columns], i / columns);
        return cells;
    }

    /// <summary>
    /// Zeichen unter dem Finger. <paramref name="column"/> und <paramref name="row"/> sind Gleitkomma-Positionen
    /// in Zellen (Spalte relativ zur Taste, Zeile von unten). Unterhalb des Rasters gilt die unterste Zeile, darüber
    /// die oberste; in einer kürzeren Zeile das nächstgelegene Zeichen.
    /// </summary>
    public static int IndexAt(IReadOnlyList<PopupCell> cells, float column, float row)
    {
        if (cells.Count == 0) return -1;
        int rows = cells.Max(c => c.Row) + 1;
        int r = Math.Clamp((int)MathF.Floor(row), 0, rows - 1);
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].Row != r) continue;
            float d = MathF.Abs(cells[i].Column - column);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }
}
