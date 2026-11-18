namespace ZBoard.Suggestions;

/// <summary>Eintrag im persönlichen Wörterbuch: ein Wort oder Text, optional mit Kürzel ("mfg").</summary>
public sealed record PersonalEntry(string Word, string Shortcut)
{
    public bool HasShortcut => Shortcut.Length > 0;
}

/// <summary>
/// Persönliches Wörterbuch wie bei Gboard: eigene Wörter, die zBoard nie „korrigiert“ und beim Tippen vorschlägt,
/// und Kürzel, deren Text beim Tippen des Kürzels in der Vorschlagsleiste erscheint ("mfg" → "Mit freundlichen
/// Grüßen"). Gilt in allen Sprachen. Gespeichert als eine Zeichenkette in den Einstellungen (eine Zeile je Eintrag,
/// Wort und Kürzel durch Tabulator getrennt) – damit ist es in Sicherung und Synchronisation enthalten.
/// Unveränderlich: Änderungen liefern ein neues Wörterbuch.
/// </summary>
public sealed class PersonalDictionary
{
    public const int MaxWordLength = 200;
    public const int MaxShortcutLength = 32;
    public const int MaxEntries = 1000;

    /// <summary>So viele Buchstaben müssen getippt sein, bevor ein eigenes Wort als Ergänzung vorgeschlagen wird.</summary>
    const int MinPrefix = 2;

    public static readonly PersonalDictionary Empty = new([]);

    readonly List<PersonalEntry> _entries;

    PersonalDictionary(List<PersonalEntry> entries) => _entries = entries;

    /// <summary>Alle Einträge, nach Wort sortiert.</summary>
    public IReadOnlyList<PersonalEntry> Entries => _entries;

    /// <summary>
    /// Prüft und bereinigt eine Eingabe: Leerraum außen entfernt, Zeilenumbrüche und Tabulatoren im Text werden zu
    /// Leerzeichen. null, wenn das Wort leer oder zu lang ist oder das Kürzel anderes als Buchstaben und Ziffern
    /// enthält (nur daraus besteht ein getipptes Wort, an dem zBoard ein Kürzel erkennen kann).
    /// </summary>
    public static PersonalEntry? Normalize(string? word, string? shortcut)
    {
        string w = string.Join(' ', (word ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string s = (shortcut ?? string.Empty).Trim();
        if (w.Length == 0 || w.Length > MaxWordLength) return null;
        if (s.Length > MaxShortcutLength || !s.All(char.IsLetterOrDigit)) return null;
        if (string.Equals(w, s, StringComparison.Ordinal)) s = string.Empty; // Kürzel = Wort: einfach ein eigenes Wort
        return new PersonalEntry(w, s);
    }

    public static PersonalDictionary Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Empty;
        var entries = new List<PersonalEntry>();
        foreach (var line in raw.Split('\n'))
        {
            int tab = line.IndexOf('\t');
            var entry = tab < 0 ? Normalize(line, null) : Normalize(line[..tab], line[(tab + 1)..]);
            if (entry != null && !entries.Contains(entry) && entries.Count < MaxEntries) entries.Add(entry);
        }

        return new PersonalDictionary(Sorted(entries));
    }

    public string Serialize() => string.Join('\n', _entries.Select(e => e.HasShortcut ? e.Word + "\t" + e.Shortcut : e.Word));

    /// <summary>Fügt einen Eintrag hinzu; <paramref name="replaces"/> wird dabei entfernt (Bearbeiten).</summary>
    public PersonalDictionary With(PersonalEntry entry, PersonalEntry? replaces = null)
    {
        var entries = _entries.Where(e => e != replaces && e != entry).ToList();
        if (entries.Count >= MaxEntries) return this;
        entries.Add(entry);
        return new PersonalDictionary(Sorted(entries));
    }

    public PersonalDictionary Without(PersonalEntry entry) => new([.. _entries.Where(e => e != entry)]);

    static List<PersonalEntry> Sorted(IEnumerable<PersonalEntry> entries) =>
        [.. entries.OrderBy(e => e.Word, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Word, StringComparer.Ordinal)
            .ThenBy(e => e.Shortcut, StringComparer.Ordinal)];

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ist <paramref name="typed"/> ein Kürzel (Groß-/Kleinschreibung egal)?</summary>
    public bool IsShortcut(string typed) => typed.Length > 0 && _entries.Any(e => e.HasShortcut && Same(e.Shortcut, typed));

    /// <summary>Texte zu einem getippten Kürzel, in der Reihenfolge der Liste.</summary>
    public IReadOnlyList<string> Expansions(string typed)
    {
        if (typed.Length == 0 || _entries.Count == 0) return [];
        return [.. _entries.Where(e => e.HasShortcut && Same(e.Shortcut, typed)).Select(e => e.Word).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Das eigene Wort zu <paramref name="typed"/> in gespeicherter Schreibweise ("zboard" → "zBoard"), oder null.
    /// Gibt es genau die getippte Schreibweise, gilt sie. Nur einzelne Wörter, keine Texte mit Leerzeichen.
    /// </summary>
    public string? Word(string typed)
    {
        if (typed.Length == 0) return null;
        string? found = null;
        foreach (var entry in _entries)
        {
            if (entry.Word.Contains(' ') || !Same(entry.Word, typed)) continue;
            if (entry.Word == typed) return typed;
            found ??= entry.Word;
        }

        return found;
    }

    /// <summary>
    /// Eigene Wörter und Texte, die mit <paramref name="prefix"/> beginnen und länger sind (Groß-/Kleinschreibung
    /// egal), kürzeste zuerst.
    /// </summary>
    public IReadOnlyList<string> Completions(string prefix, int max)
    {
        if (prefix.Length < MinPrefix || _entries.Count == 0) return [];
        return [.. _entries
            .Where(e => e.Word.Length > prefix.Length && e.Word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Word).Distinct(StringComparer.Ordinal)
            .OrderBy(w => w.Length).ThenBy(w => w, StringComparer.OrdinalIgnoreCase)
            .Take(max)];
    }
}
