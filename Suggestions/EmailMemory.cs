using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZBoard.Suggestions;

/// <summary>Vorschlag für ein E-Mail-Feld: <paramref name="Address"/> ersetzt das angefangene Wort, <paramref name="Display"/> steht in der Leiste.</summary>
public sealed record EmailSuggestion(string Display, string Address);

/// <summary>
/// Merkt sich E-Mail-Adressen, die in E-Mail-Feldern eingegeben wurden, und schlägt sie wieder vor – wie Gboard,
/// das in E-Mail-Feldern die eigene Adresse anbietet. Nur auf dem Gerät, plattformunabhängig.
/// Format der Datei: <c>adresse&lt;TAB&gt;anzahl&lt;TAB&gt;zuletzt (Unix-ms)</c>.
/// </summary>
public sealed partial class EmailMemory
{
    const int MaxAddresses = 50;
    const int MaxPerText = 5;

    /// <summary>Häufige Anbieter, nach "@" als Ergänzung (gemerkte Domains stehen davor).</summary>
    static readonly string[] CommonDomains =
    [
        "gmail.com", "web.de", "gmx.de", "t-online.de", "outlook.com", "outlook.de", "hotmail.com",
        "icloud.com", "yahoo.com", "gmx.net", "freenet.de", "posteo.de", "proton.me",
    ];

    sealed class Entry(string address, int count, long lastUsed)
    {
        public string Address { get; set; } = address;
        public int Count { get; set; } = count;
        public long LastUsed { get; set; } = lastUsed;
    }

    readonly Lock _lock = new();
    readonly List<Entry> _entries = [];

    public bool IsDirty { get; private set; }

    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,24}", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    /// <summary>Gültige Adresse (ganzer Text)?</summary>
    public static bool IsAddress(string text)
    {
        var m = AddressPattern().Match(text);
        return m.Success && m.Index == 0 && m.Length == text.Length && IsPlausible(text);
    }

    static bool IsPlausible(string address)
    {
        int at = address.IndexOf('@');
        string local = address[..at];
        string domain = address[(at + 1)..];
        return local.Length is > 0 and <= 64
            && !local.StartsWith('.') && !local.EndsWith('.') && !local.Contains("..")
            && !domain.Contains("..") && !domain.StartsWith('-') && address.Length <= 254;
    }

    /// <summary>Alle Adressen im Text (z. B. Inhalt eines E-Mail-Felds, auch "a@b.de, c@d.de") merken.</summary>
    /// <returns>Anzahl gemerkter Adressen.</returns>
    public int LearnFrom(string? text, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) return 0;
        int learned = 0;
        foreach (Match m in AddressPattern().Matches(text))
        {
            if (learned >= MaxPerText) break;
            // Teil eines längeren Worts (z. B. "x@y.de/abc") nicht übernehmen
            int end = m.Index + m.Length;
            bool boundedLeft = m.Index == 0 || IsSeparator(text[m.Index - 1]);
            bool boundedRight = end == text.Length || IsSeparator(text[end]);
            if (!boundedLeft || !boundedRight || !IsPlausible(m.Value)) continue;
            Learn(m.Value, nowMs);
            learned++;
        }

        return learned;
    }

    static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c is ',' or ';' or '<' or '>' or '"' or '(' or ')';

    public void Learn(string address, long nowMs)
    {
        if (!IsAddress(address)) return;
        lock (_lock)
        {
            var entry = _entries.Find(e => string.Equals(e.Address, address, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                _entries.Add(new Entry(address, 1, nowMs));
                if (_entries.Count > MaxAddresses) _entries.Remove(_entries.MinBy(Score)!);
            }
            else
            {
                entry.Address = address; // zuletzt verwendete Schreibweise
                entry.Count++;
                entry.LastUsed = nowMs;
            }

            IsDirty = true;
        }
    }

    public bool Remove(string address)
    {
        lock (_lock)
        {
            bool removed = _entries.RemoveAll(e => string.Equals(e.Address, address, StringComparison.OrdinalIgnoreCase)) > 0;
            IsDirty |= removed;
            return removed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            IsDirty = true;
        }
    }

    /// <summary>Gemerkte Adressen, meistgenutzte (und zuletzt verwendete) zuerst.</summary>
    public IReadOnlyList<string> Addresses()
    {
        lock (_lock) return [.. Ranked().Select(e => e.Address)];
    }

    /// <summary>
    /// Vorschläge für das angefangene Wort vor dem Cursor (<paramref name="typed"/>, kann leer sein):
    /// gemerkte Adressen, die damit beginnen, und nach "@" bekannte Anbieter.
    /// </summary>
    public IReadOnlyList<EmailSuggestion> Suggest(string typed, int max = 3)
    {
        var result = new List<EmailSuggestion>();
        lock (_lock)
        {
            foreach (var e in Ranked())
            {
                if (result.Count >= max) break;
                if (e.Address.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(e.Address, typed, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new EmailSuggestion(e.Address, e.Address));
                }
            }

            int at = typed.IndexOf('@');
            if (at <= 0 || typed.IndexOf('@', at + 1) >= 0) return result;

            string local = typed[..at];
            string domainTyped = typed[(at + 1)..];
            var domains = Ranked()
                .Select(e => e.Address[(e.Address.IndexOf('@') + 1)..].ToLowerInvariant())
                .Concat(CommonDomains)
                .Distinct();

            foreach (var domain in domains)
            {
                if (result.Count >= max) break;
                if (!domain.StartsWith(domainTyped, StringComparison.OrdinalIgnoreCase) || domain.Length == domainTyped.Length) continue;
                string address = local + "@" + domain;
                if (result.Any(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new EmailSuggestion("@" + domain, address));
            }
        }

        return result;
    }

    IEnumerable<Entry> Ranked() => _entries.OrderByDescending(Score).ThenByDescending(e => e.LastUsed);

    /// <summary>Häufigkeit zählt, jüngere Nutzung leicht mehr (Halbwertszeit 90 Tage).</summary>
    double Score(Entry e)
    {
        long newest = _entries.Count == 0 ? e.LastUsed : _entries.Max(x => x.LastUsed);
        double ageDays = Math.Max(0, newest - e.LastUsed) / 86_400_000.0;
        return e.Count * Math.Pow(0.5, ageDays / 90);
    }

    // ---------- Speichern ----------

    public static EmailMemory Load(Stream stream)
    {
        var memory = new EmailMemory();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !IsAddress(parts[0])) continue;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)) continue;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long last)) continue;
            if (memory._entries.Count >= MaxAddresses) break;
            memory._entries.Add(new Entry(parts[0], Math.Max(1, count), last));
        }

        return memory;
    }

    public void Save(Stream stream)
    {
        lock (_lock)
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var e in _entries)
            {
                writer.Write(e.Address);
                writer.Write('\t');
                writer.Write(e.Count.ToString(CultureInfo.InvariantCulture));
                writer.Write('\t');
                writer.WriteLine(e.LastUsed.ToString(CultureInfo.InvariantCulture));
            }

            IsDirty = false;
        }
    }
}
