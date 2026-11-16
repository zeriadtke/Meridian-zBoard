namespace ZBoard.Suggestions;

/// <summary>
/// Was der Nutzer bei der Pinyin-Eingabe gewählt hat: je Schreibweise die Schriftzeichen mit der Anzahl der Male.
/// Eigene Klasse, damit Sicherung und Synchronisation es ohne die große Pinyin-Tabelle lesen, schreiben und
/// zusammenführen können. Datei: "pinyin[Tab]schriftzeichen[Tab]anzahl" je Zeile. Plattformunabhängig.
/// </summary>
public sealed class PinyinLearned
{
    /// <summary>Obergrenzen beim Laden, gegen beschädigte oder untergeschobene Dateien.</summary>
    const int MaxEntries = 50_000;
    const int MaxKeyLength = 80;
    const int MaxWordLength = 40;

    readonly Lock _lock = new();
    readonly Dictionary<string, List<(string Word, int Count)>> _entries = new(StringComparer.Ordinal);

    /// <summary>Seit dem Laden oder Speichern hat sich etwas geändert.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Anzahl der gemerkten Paare aus Schreibweise und Schriftzeichen.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _entries.Values.Sum(list => list.Count);
        }
    }

    /// <summary>Längste gemerkte Schreibweise (für die Suche nach dem längsten passenden Wort).</summary>
    public int LongestKey { get; private set; }

    /// <summary>Zählt eine Wahl. <paramref name="pinyin"/>: nur Buchstaben a–z, sonst wird nichts gemerkt.</summary>
    public void Learn(string pinyin, string word)
    {
        if (!IsKey(pinyin) || word.Length is 0 or > MaxWordLength) return;
        lock (_lock)
        {
            Set(pinyin.ToLowerInvariant(), word, CountOfLocked(pinyin.ToLowerInvariant(), word) + 1);
            IsDirty = true;
        }
    }

    /// <summary>Wie oft diese Schriftzeichen für die Schreibweise gewählt wurden.</summary>
    public int CountOf(string pinyin, string word)
    {
        lock (_lock) return CountOfLocked(pinyin.ToLowerInvariant(), word);
    }

    /// <summary>Die gewählten Schriftzeichen einer Schreibweise, die häufigste Wahl zuerst.</summary>
    public IReadOnlyList<(string Word, int Count)> For(string key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var list) ? [.. list] : [];
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            LongestKey = 0;
            IsDirty = false;
        }
    }

    /// <summary>
    /// Übernimmt alles aus <paramref name="other"/>, je Eintrag die höhere Anzahl (wie bei gelernten Wörtern: zweimal
    /// abgleichen zählt nichts doppelt). true: es hat sich etwas geändert.
    /// </summary>
    public bool MergeFrom(PinyinLearned other)
    {
        if (ReferenceEquals(other, this)) return false;
        var theirs = other.Snapshot();
        bool changed = false;
        lock (_lock)
        {
            foreach (var (key, word, count) in theirs)
            {
                if (count <= CountOfLocked(key, word)) continue;
                Set(key, word, count);
                changed = true;
            }

            if (changed) IsDirty = true;
        }

        return changed;
    }

    /// <summary>Ersetzt den ganzen Inhalt (Wiederherstellen einer Sicherung); danach gilt er als gespeichert.</summary>
    public void ReplaceWith(PinyinLearned other)
    {
        if (ReferenceEquals(other, this)) return;
        var theirs = other.Snapshot();
        lock (_lock)
        {
            _entries.Clear();
            LongestKey = 0;
            foreach (var (key, word, count) in theirs) Set(key, word, count);
            IsDirty = false;
        }
    }

    public void Save(Stream stream)
    {
        SaveCopy(stream);
        IsDirty = false;
    }

    /// <summary>Schreibt wie <see cref="Save"/>, lässt aber <see cref="IsDirty"/> unverändert (für Sicherungen).</summary>
    public void SaveCopy(Stream stream)
    {
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
        foreach (var (key, word, count) in Snapshot().OrderBy(e => e.Key, StringComparer.Ordinal).ThenByDescending(e => e.Count))
        {
            writer.Write($"{key}\t{word}\t{count}\n");
        }
    }

    /// <summary>Lädt eine Datei im Format von <see cref="Save"/>; unlesbare Zeilen werden übergangen.</summary>
    public static PinyinLearned Load(Stream stream)
    {
        var learned = new PinyinLearned();
        using var reader = new StreamReader(stream);
        string? line;
        int entries = 0;
        while ((line = reader.ReadLine()) != null && entries < MaxEntries)
        {
            var parts = line.Split('\t');
            if (parts.Length != 3 || !IsKey(parts[0]) || parts[0] != parts[0].ToLowerInvariant()) continue;
            if (parts[1].Length is 0 or > MaxWordLength || !int.TryParse(parts[2], out int count) || count < 1) continue;
            if (learned.CountOfLocked(parts[0], parts[1]) >= count) continue;
            learned.Set(parts[0], parts[1], count);
            entries++;
        }

        return learned;
    }

    static bool IsKey(string pinyin) => pinyin.Length <= MaxKeyLength && PinyinEngine.IsPinyin(pinyin);

    List<(string Key, string Word, int Count)> Snapshot()
    {
        lock (_lock) return [.. _entries.SelectMany(e => e.Value.Select(w => (e.Key, w.Word, w.Count)))];
    }

    int CountOfLocked(string key, string word)
    {
        if (!_entries.TryGetValue(key, out var list)) return 0;
        int at = list.FindIndex(e => e.Word == word);
        return at < 0 ? 0 : list[at].Count;
    }

    /// <summary>Setzt die Anzahl und hält die Liste nach Anzahl geordnet; bei Gleichstand bleibt die frühere Wahl vorn.</summary>
    void Set(string key, string word, int count)
    {
        if (!_entries.TryGetValue(key, out var list)) _entries[key] = list = [];
        list.RemoveAll(e => e.Word == word);
        int at = list.FindIndex(e => e.Count < count);
        list.Insert(at < 0 ? list.Count : at, (word, count));
        LongestKey = Math.Max(LongestKey, key.Length);
    }
}
