using ZBoard.Keyboard;

namespace ZBoard.Suggestions;

/// <summary>
/// Ersetzungskosten für die Autokorrektur: Nachbartasten und Umlaut-Varianten sind "billiger" als beliebige Buchstaben,
/// weil sie typische Tippfehler sind ("fur" → "für", "tesr" → "test").
/// </summary>
public sealed class KeyProximity
{
    const float NeighborCost = 0.5f;
    public const float VariantCost = 0.3f;
    const float DefaultCost = 1f;

    /// <summary>
    /// Buchstaben mit Zusatzzeichen und ihr Grundbuchstabe, paarweise hintereinander ("äa", "ęe", "ắa", "άα", "ёе" …):
    /// alle lateinischen und griechischen Kleinbuchstaben, die Unicode in Grundbuchstabe und Zeichen zerlegt, dazu
    /// ł, đ, ı, ø, kyrillisch ё, ґ, ѝ und die arabischen Alif-Formen. ß steht für sich (siehe <see cref="Fold"/>).
    /// </summary>
    const string VariantPairs =
        "àaáaâaãaäaåaçcèeéeêeëeìiíiîiïiñnòoóoôoõoöoøoùuúuûuüuýyÿyāaăaąaćcĉcċcčcďdđdēeĕeėeęeěeĝgğgġgģgĥhĩiīiĭiįiıiĵjķkĺlļlľlłlńnņnňnōoŏoőoŕrŗrřrśsŝsşsšsţtťtũuūuŭuůuűuųuŵwŷyźzżzžzơoưuǎaǐiǒoǔuǖuǘuǚuǜuǟaǡaǧgǩkǫoǭoǰjǵgǹnǻaȁaȃaȅeȇeȉiȋiȍoȏoȑrȓrȕuȗușsțtȟhȧaȩeȫoȭoȯoȱoȳyΐιάαέεήηίιΰυϊιϋυόούυώωёеѝиґгآاأاإاٱاḁaḃbḅbḇbḉcḋdḍdḏdḑdḓdḕeḗeḙeḛeḝeḟfḡgḣhḥhḧhḩhḫhḭiḯiḱkḳkḵkḷlḹlḻlḽlḿmṁmṃmṅnṇnṉnṋnṍoṏoṑoṓoṕpṗpṙrṛrṝrṟrṡsṣsṥsṧsṩsṫtṭtṯtṱtṳuṵuṷuṹuṻuṽvṿvẁwẃwẅwẇwẉwẋxẍxẏyẑzẓzẕzẖhẗtẘwẙyạaảaấaầaẩaẫaậaắaằaẳaẵaặaẹeẻeẽeếeềeểeễeệeỉiịiọoỏoốoồoổoỗoộoớoờoởoỡoợoụuủuứuừuửuữuựuỳyỵyỷyỹy";

    static readonly Dictionary<char, char> BaseLetters = BuildBaseLetters();

    static Dictionary<char, char> BuildBaseLetters()
    {
        var map = new Dictionary<char, char>(VariantPairs.Length / 2);
        for (int i = 0; i + 1 < VariantPairs.Length; i += 2) map[VariantPairs[i]] = VariantPairs[i + 1];
        return map;
    }

    /// <summary>Der Buchstabe ohne Umlaut, Akzent oder Tonzeichen ("ä" → "a", "ệ" → "e"); sonst der Buchstabe selbst.</summary>
    public static char BaseLetter(char c) => BaseLetters.TryGetValue(c, out char plain) ? plain : c;

    readonly Dictionary<char, (int Row, float X)> _positions = new();

    /// <summary>
    /// Ist <paramref name="typed"/> nur eine bequeme Schreibweise von <paramref name="word"/> – ohne Umlaute, Akzente
    /// oder Apostroph getippt ("fur" für "für", "strasse" für "Straße", "dont" für "don't")? Solche Formen entstehen
    /// nicht durch Danebentippen, sondern mit Absicht, und kommen deshalb viel häufiger vor als andere Fehler.
    /// Die Richtung zählt: Wer "ändere" tippt, hat den Umlaut bewusst gewählt und meint nicht "andere".
    /// </summary>
    public static bool IsLazySpelling(string typed, string word)
    {
        if (Fold(typed, "s") != Fold(word, "s") && Fold(typed, "ss") != Fold(word, "ss")) return false;

        // Jedes Sonderzeichen der Eingabe muss auch im Wort stehen (in derselben Reihenfolge), und das Wort hat mehr.
        string typedMarks = Marks(typed), wordMarks = Marks(word);
        if (typedMarks.Length >= wordMarks.Length) return false;
        int i = 0;
        foreach (char mark in wordMarks)
        {
            if (i < typedMarks.Length && typedMarks[i] == mark) i++;
        }

        return i == typedMarks.Length;
    }

    /// <summary>Die Umlaute, Akzentbuchstaben und Apostrophe eines Worts in ihrer Reihenfolge.</summary>
    static string Marks(string text) =>
        string.Concat(text.Select(char.ToLowerInvariant).Where(c => c == '\'' || c == 'ß' || BaseLetters.ContainsKey(c)));

    /// <summary>Kleinbuchstaben ohne Umlaute, Akzente und Apostrophe; ß wird zu <paramref name="eszett"/>.</summary>
    static string Fold(string text, string eszett)
    {
        var folded = new System.Text.StringBuilder(text.Length + 2);
        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw);
            if (c == '\'') continue;
            if (c == 'ß')
            {
                folded.Append(eszett);
                continue;
            }

            folded.Append(BaseLetter(c));
        }

        return folded.ToString();
    }

    public static KeyProximity FromLayout(KeyboardLayout layout)
    {
        var proximity = new KeyProximity();
        for (int r = 0; r < layout.Rows.Count; r++)
        {
            var row = layout.Rows[r];
            float x = row.Indent;
            foreach (var key in row.Keys)
            {
                float w = key.Width > 0 ? key.Width : 1f;
                if (key.IsCharacter && key.Label.Length == 1 && char.IsLetter(key.Label[0]))
                {
                    proximity._positions[key.Label[0]] = (r, x + w / 2);
                }

                x += w;
            }
        }

        return proximity;
    }

    /// <summary>Buchstaben der untersten Reihe: Sie liegen direkt über der Leertaste und werden mit ihr verwechselt.</summary>
    public IReadOnlyList<char> BottomRowLetters
    {
        get
        {
            if (_positions.Count == 0) return [];
            int bottom = _positions.Values.Max(p => p.Row);
            return [.. _positions.Where(p => p.Value.Row == bottom).Select(p => p.Key).Order()];
        }
    }

    public float SubstitutionCost(char a, char b)
    {
        if (a == b) return 0f;
        if ((a == 's' && b == 'ß') || (a == 'ß' && b == 's')) return VariantCost;
        if (BaseLetter(a) == BaseLetter(b)) return VariantCost; // "a"/"ä", aber auch "é"/"è"

        if (_positions.TryGetValue(a, out var pa) && _positions.TryGetValue(b, out var pb))
        {
            int rowDistance = Math.Abs(pa.Row - pb.Row);
            float dx = Math.Abs(pa.X - pb.X);
            if (rowDistance == 0 && dx <= 1.01f) return NeighborCost;
            if (rowDistance == 1 && dx <= 1.01f) return NeighborCost;
        }

        return DefaultCost;
    }
}
