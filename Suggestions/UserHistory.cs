using System.Globalization;
using System.Text;

namespace ZBoard.Suggestions;

/// <summary>
/// Was zBoard aus den eigenen Eingaben lernt: eigene Wörter und Wortfolgen.
/// Bleibt auf dem Gerät; verlässt es nur in einer Sicherung, die der Nutzer selbst erstellt.
/// Plattformunabhängig.
/// </summary>
public sealed class UserHistory
{
    /// <summary>Ab so vielen Verwendungen gilt ein unbekanntes Wort als bekannt (keine Autokorrektur mehr).</summary>
    public const int KnownThreshold = 2;

    const int MaxWords = 5000;
    const int MaxBigrams = 20000;

    readonly Lock _lock = new();
    readonly Dictionary<string, (string Form, int Count)> _words = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, int>> _bigrams = new(StringComparer.Ordinal);
    int _bigramCount;

    public bool IsDirty { get; private set; }

    public int WordCount
    {
        get { lock (_lock) return _words.Count; }
    }

    /// <summary>Merkt sich <paramref name="word"/> und, falls vorhanden, die Folge <paramref name="previous"/> → <paramref name="word"/>.</summary>
    /// <param name="accepted">
    /// Der Nutzer hat das Wort ausdrücklich gewählt (links in der Leiste angetippt, Autokorrektur rückgängig gemacht):
    /// wie bei Gboard sofort bekannt, also nicht mehr in Anführungszeichen und nicht mehr korrigiert.
    /// </param>
    public void Learn(string? previous, string word, bool accepted = false)
    {
        if (!IsLearnable(word)) return;

        lock (_lock)
        {
            var key = word.ToLowerInvariant();
            // Kleinschreibung gewinnt: "Hallo" am Satzanfang soll ein späteres "hallo" nicht überschreiben.
            var learned = _words.TryGetValue(key, out var entry)
                ? (Form: char.IsLower(word[0]) ? word : entry.Form, Count: entry.Count + 1)
                : (Form: word, Count: 1);
            if (accepted) learned.Count = Math.Max(learned.Count, KnownThreshold);

            _words[key] = learned;

            if (previous != null && IsLearnable(previous))
            {
                var prevKey = previous.ToLowerInvariant();
                if (!_bigrams.TryGetValue(prevKey, out var followers))
                {
                    followers = new Dictionary<string, int>(StringComparer.Ordinal);
                    _bigrams[prevKey] = followers;
                }

                if (!followers.ContainsKey(word)) _bigramCount++;
                followers[word] = followers.GetValueOrDefault(word) + 1;
            }

            Prune();
            IsDirty = true;
        }
    }

    public int CountOf(string word)
    {
        lock (_lock) return _words.TryGetValue(word.ToLowerInvariant(), out var e) ? e.Count : 0;
    }

    public bool IsKnown(string word) => CountOf(word) >= KnownThreshold;

    /// <summary>Gelernte Schreibweise, sobald das Wort als bekannt gilt; sonst null.</summary>
    public string? LearnedForm(string word)
    {
        lock (_lock)
        {
            return _words.TryGetValue(word.ToLowerInvariant(), out var e) && e.Count >= KnownThreshold ? e.Form : null;
        }
    }

    /// <summary>Gelernte Wörter (ab <see cref="KnownThreshold"/>), die mit <paramref name="prefix"/> beginnen.</summary>
    public List<(string Word, int Count)> Complete(string prefix)
    {
        var lower = prefix.ToLowerInvariant();
        lock (_lock)
        {
            return _words
                .Where(kv => kv.Value.Count >= KnownThreshold && kv.Key.StartsWith(lower, StringComparison.Ordinal))
                .Select(kv => (kv.Value.Form, kv.Value.Count))
                .OrderByDescending(x => x.Count)
                .Take(6)
                .ToList();
        }
    }

    /// <summary>Gelernte Folgewörter von <paramref name="previous"/>, häufigstes zuerst.</summary>
    public List<(string Next, int Count)> Followers(string previous)
    {
        lock (_lock)
        {
            return _bigrams.TryGetValue(previous.ToLowerInvariant(), out var followers)
                ? followers.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).Take(6).ToList()
                : [];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _words.Clear();
            _bigrams.Clear();
            _bigramCount = 0;
            IsDirty = true;
        }
    }

    // ---------- Speichern / Laden (Textformat: w<TAB>wort<TAB>anzahl, b<TAB>vorher<TAB>wort<TAB>anzahl) ----------

    public void Save(Stream stream)
    {
        lock (_lock)
        {
            WriteTo(stream);
            IsDirty = false;
        }
    }

    /// <summary>Schreibt wie <see cref="Save"/>, lässt aber <see cref="IsDirty"/> unverändert (für Sicherungen).</summary>
    public void SaveCopy(Stream stream)
    {
        lock (_lock) WriteTo(stream);
    }

    /// <summary>Übernimmt den Inhalt von <paramref name="other"/> (Wiederherstellen einer Sicherung, Datei ist schon geschrieben).</summary>
    public void ReplaceWith(UserHistory other)
    {
        if (ReferenceEquals(other, this)) return;
        lock (other._lock)
        lock (_lock)
        {
            _words.Clear();
            foreach (var (key, value) in other._words) _words[key] = value;
            _bigrams.Clear();
            foreach (var (prev, followers) in other._bigrams) _bigrams[prev] = new Dictionary<string, int>(followers, StringComparer.Ordinal);
            _bigramCount = other._bigramCount;
            IsDirty = false;
        }
    }

    /// <summary>
    /// Synchronisation: übernimmt Wörter und Wortfolgen von <paramref name="other"/>, ohne eigene zu verlieren
    /// (je Eintrag die höhere Anzahl). Gelöschtes kommt dadurch wieder, wenn ein anderes Gerät es noch kennt.
    /// </summary>
    /// <returns>true, wenn sich etwas geändert hat.</returns>
    public bool MergeFrom(UserHistory other)
    {
        if (ReferenceEquals(other, this)) return false;
        lock (other._lock)
        lock (_lock)
        {
            bool changed = false;
            foreach (var (key, (form, count)) in other._words)
            {
                if (_words.TryGetValue(key, out var mine))
                {
                    if (count <= mine.Count) continue;
                    _words[key] = (mine.Form, count);
                }
                else
                {
                    _words[key] = (form, count);
                }

                changed = true;
            }

            foreach (var (prev, theirs) in other._bigrams)
            {
                if (!_bigrams.TryGetValue(prev, out var followers))
                {
                    followers = new Dictionary<string, int>(StringComparer.Ordinal);
                    _bigrams[prev] = followers;
                }

                foreach (var (next, count) in theirs)
                {
                    if (followers.TryGetValue(next, out int mine))
                    {
                        if (count <= mine) continue;
                    }
                    else
                    {
                        _bigramCount++;
                    }

                    followers[next] = count;
                    changed = true;
                }
            }

            if (changed)
            {
                Prune();
                IsDirty = true;
            }

            return changed;
        }
    }

    void WriteTo(Stream stream)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        foreach (var (_, (form, count)) in _words)
        {
            writer.Write("w\t"); writer.Write(form); writer.Write('\t');
            writer.WriteLine(count.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (prev, followers) in _bigrams)
        {
            foreach (var (next, count) in followers)
            {
                writer.Write("b\t"); writer.Write(prev); writer.Write('\t'); writer.Write(next); writer.Write('\t');
                writer.WriteLine(count.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    public static UserHistory Load(Stream stream)
    {
        var history = new UserHistory();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var p = line.Split('\t');
            if (p.Length == 3 && p[0] == "w" && int.TryParse(p[2], out var wc))
            {
                history._words[p[1].ToLowerInvariant()] = (p[1], wc);
            }
            else if (p.Length == 4 && p[0] == "b" && int.TryParse(p[3], out var bc))
            {
                if (!history._bigrams.TryGetValue(p[1], out var followers))
                {
                    followers = new Dictionary<string, int>(StringComparer.Ordinal);
                    history._bigrams[p[1]] = followers;
                }

                if (!followers.ContainsKey(p[2])) history._bigramCount++;
                followers[p[2]] = bc;
            }
        }

        return history;
    }

    // ---------- Hilfen ----------

    /// <summary>Nur echte Wörter lernen: mit Buchstaben, ohne @ (E-Mail) und nicht überlang.</summary>
    static bool IsLearnable(string word) =>
        word.Length is > 0 and <= 40
        && word.Any(char.IsLetter)
        && word.All(c => char.IsLetterOrDigit(c) || c is '\'' or '-');

    /// <summary>Hält die Größe begrenzt: die seltensten Einträge fallen zuerst heraus.</summary>
    void Prune()
    {
        if (_words.Count > MaxWords)
        {
            foreach (var key in _words.OrderBy(kv => kv.Value.Count).Take(_words.Count - MaxWords * 9 / 10)
                         .Select(kv => kv.Key).ToList())
            {
                _words.Remove(key);
            }
        }

        if (_bigramCount > MaxBigrams)
        {
            var all = _bigrams.SelectMany(kv => kv.Value.Select(f => (Prev: kv.Key, Next: f.Key, Count: f.Value)))
                .OrderBy(x => x.Count)
                .Take(_bigramCount - MaxBigrams * 9 / 10)
                .ToList();
            foreach (var (prev, next, _) in all)
            {
                _bigrams[prev].Remove(next);
                if (_bigrams[prev].Count == 0) _bigrams.Remove(prev);
                _bigramCount--;
            }
        }
    }
}
