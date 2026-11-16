using System.Text;

namespace ZBoard.Suggestions;

/// <summary>
/// Vietnamesisch mit Telex, wie bei Gboard: Zusatz- und Tonzeichen entstehen durch Buchstaben am Wort.
/// aa → â, aw → ă, ee → ê, oo → ô, ow → ơ, uw → ư, dd → đ; Töne mit s (sắc), f (huyền), r (hỏi), x (ngã), j (nặng),
/// z entfernt den Ton. Dieselbe Taste noch einmal nimmt die Umwandlung zurück ("as" → "á", noch ein "s" → "as"):
/// so lassen sich auch fremde Wörter tippen. Plattformunabhängig.
/// </summary>
public static class Telex
{
    // Je Vokal: ohne Ton, huyền (f), sắc (s), hỏi (r), ngã (x), nặng (j)
    static readonly string[] Tones =
    [
        "aàáảãạ", "ăằắẳẵặ", "âầấẩẫậ", "eèéẻẽẹ", "êềếểễệ", "iìíỉĩị",
        "oòóỏõọ", "ôồốổỗộ", "ơờớởỡợ", "uùúủũụ", "ưừứửữự", "yỳýỷỹỵ",
    ];

    const string ToneKeys = "\0fsrxj";

    /// <summary>Womit eine vietnamesische Silbe nach dem Vokal enden kann.</summary>
    static readonly HashSet<string> ValidFinals = ["", "c", "ch", "m", "n", "ng", "nh", "p", "t"];

    static readonly Dictionary<char, (char Plain, int Tone)> Decompose = BuildDecompose();

    static Dictionary<char, (char, int)> BuildDecompose()
    {
        var map = new Dictionary<char, (char, int)>();
        foreach (var row in Tones)
        {
            for (int tone = 0; tone < row.Length; tone++) map[row[tone]] = (row[0], tone);
        }

        return map;
    }

    static bool IsVowel(char c) => Decompose.ContainsKey(c);

    /// <summary>
    /// Das Wort in Bearbeitung, nachdem <paramref name="key"/> getippt wurde. Ohne Telex-Bedeutung wird die Taste
    /// einfach angehängt; ein vorhandener Ton rückt dabei an die richtige Stelle ("hoà" + "n" → "hoàn" bleibt,
    /// "toá" + "n" → "toán").
    /// </summary>
    public static string Apply(string word, char key)
    {
        if (!char.IsLetter(key) || !word.All(char.IsLetter)) return word + key;

        // Zerlegen: Buchstaben ohne Ton (klein), Großschreibung je Buchstabe, der Ton des Worts
        var letters = new List<char>(word.Length + 1);
        var upper = new List<bool>(word.Length + 1);
        int tone = 0;
        foreach (char c in word)
        {
            char lower = char.ToLowerInvariant(c);
            upper.Add(lower != c);
            if (Decompose.TryGetValue(lower, out var parts))
            {
                letters.Add(parts.Plain);
                if (parts.Tone != 0) tone = parts.Tone;
            }
            else
            {
                letters.Add(lower);
            }
        }

        char k = char.ToLowerInvariant(key);
        bool isUpper = k != key;
        bool handled = false;
        var (start, end) = VowelGroup(letters); // einzige Vokalgruppe [start, end); leer, wenn es keine oder mehrere gibt

        // Nach dem Vokal steht etwas, womit keine vietnamesische Silbe endet ("clas", "wor"): ein fremdes Wort, oder
        // eine Umwandlung wurde gerade zurückgenommen. Ab hier wird nur noch angehängt.
        if (end > start && !ValidFinals.Contains(string.Concat(letters.Skip(end)))) (start, end, k) = (0, 0, '\0');

        int toneKey = k == '\0' ? -1 : ToneKeys.IndexOf(k);
        if (toneKey > 0 && end > start)
        {
            if (tone == toneKey)
            {
                tone = 0; // zurücknehmen: der Buchstabe selbst war gemeint
            }
            else
            {
                tone = toneKey;
                handled = true;
            }
        }
        else if (k == 'z' && tone != 0)
        {
            tone = 0;
            handled = true;
        }
        else if (k is 'a' or 'e' or 'o' && end > start)
        {
            char marked = k switch { 'a' => 'â', 'e' => 'ê', _ => 'ô' };
            int at = letters.LastIndexOf(k, end - 1, end - start);
            if (at >= 0)
            {
                letters[at] = marked;
                handled = true;
            }
            else if (letters[^1] == marked)
            {
                letters[^1] = k; // "â" + "a" → "aa"
            }
        }
        else if (k == 'w')
        {
            handled = Horn(letters, start, end);
            if (!handled && letters.Count > 0 && letters[^1] is 'ă' or 'ơ' or 'ư')
            {
                letters[^1] = letters[^1] switch { 'ă' => 'a', 'ơ' => 'o', _ => 'u' }; // zurücknehmen
            }
            else if (!handled && (letters.Count == 0 || !IsVowel(letters[^1])))
            {
                letters.Add('ư'); // "w" allein steht für ư ("tw" → "tư")
                upper.Add(isUpper);
                handled = true;
            }
        }
        else if (k == 'd' && letters.Count > 0 && letters[0] is 'd' or 'đ')
        {
            if (letters[0] == 'd')
            {
                letters[0] = 'đ';
                handled = true;
            }
            else if (letters.Count == 1)
            {
                letters[0] = 'd'; // "đ" + "d" → "dd"
            }
        }

        if (!handled)
        {
            letters.Add(char.ToLowerInvariant(key));
            upper.Add(isUpper);
        }

        // Zusammensetzen, den Ton an seine Stelle
        int toneAt = tone == 0 ? -1 : TonePosition(letters);
        var result = new StringBuilder(letters.Count);
        for (int i = 0; i < letters.Count; i++)
        {
            char c = letters[i];
            if (i == toneAt) c = Tones.First(row => row[0] == c)[tone];
            result.Append(upper[i] ? char.ToUpperInvariant(c) : c);
        }

        // Kein Vokal mehr für den Ton (kommt beim Tippen nicht vor): Ton fällt weg
        return result.ToString();
    }

    /// <summary>aw → ă, ow → ơ, uw → ư, uow → ươ; im Wort mit "ua" trifft es das u ("mưa"), mit "oa" das a ("hoặc").</summary>
    static bool Horn(List<char> letters, int start, int end)
    {
        if (end <= start) return false;
        int u = letters.IndexOf('u', start, end - start);
        if (u > 0 && letters[u - 1] == 'q') u = letters.IndexOf('u', u + 1, end - u - 1); // "qu": das u gehört zum Anlaut
        int o = letters.IndexOf('o', start, end - start);
        int a = letters.IndexOf('a', start, end - start);

        if (u >= 0 && o == u + 1)
        {
            (letters[u], letters[o]) = ('ư', 'ơ');
        }
        else if (u >= 0 && a == u + 1)
        {
            letters[u] = 'ư';
        }
        else if (a >= 0)
        {
            letters[a] = 'ă';
        }
        else if (o >= 0)
        {
            letters[o] = 'ơ';
        }
        else if (u >= 0)
        {
            letters[u] = 'ư';
        }
        else if (letters.IndexOf('ư', start, end - start) is var horned and >= 0 && horned + 1 < end && letters[horned + 1] == 'ô')
        {
            letters[horned + 1] = 'ơ'; // "ưô" + w → "ươ"
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Die Vokalgruppe des Worts als [Start, Ende). Leer (0, 0), wenn das Wort keinen Vokal hat oder mehrere
    /// getrennte Gruppen: Dann ist es keine vietnamesische Silbe, und Telex lässt es in Ruhe ("data", "online").
    /// </summary>
    static (int Start, int End) VowelGroup(List<char> letters)
    {
        int start = -1, end = -1;
        for (int i = 0; i < letters.Count; i++)
        {
            if (!IsVowel(letters[i])) continue;
            if (start < 0) start = i;
            else if (end != i) return (0, 0);
            end = i + 1;
        }

        return start < 0 ? (0, 0) : (start, end);
    }

    /// <summary>
    /// Stelle des Tonzeichens (herkömmliche Schreibweise: "hòa", "thủy"): auf ơ oder ê, sonst auf dem Vokal mit
    /// Zusatzzeichen; ohne solche bei zwei Vokalen auf dem zweiten, wenn ein Konsonant folgt ("toán"), sonst auf dem
    /// ersten ("hòa", "mía"); bei drei Vokalen auf dem mittleren ("ngoài"). "qu" und "gi" zählen zum Anlaut.
    /// </summary>
    static int TonePosition(List<char> letters)
    {
        var (start, end) = VowelGroup(letters);
        if (end <= start)
        {
            // Mehrere Gruppen oder keine: der letzte Vokal trägt den Ton, damit er nicht verloren geht
            return letters.FindLastIndex(IsVowel);
        }

        if (end - start > 1 && start > 0
            && ((letters[start] == 'u' && letters[start - 1] == 'q') || (letters[start] == 'i' && letters[start - 1] == 'g')))
        {
            start++;
        }

        foreach (char preferred in "ơêâăôư")
        {
            int at = letters.IndexOf(preferred, start, end - start);
            if (at >= 0) return at;
        }

        return (end - start) switch
        {
            1 => start,
            2 => end < letters.Count ? start + 1 : start,
            _ => start + 1,
        };
    }
}
