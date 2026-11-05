using ZBoard.Keyboard;
using ZBoard.Meridian.Typing;
using Xunit;

namespace Meridian.Keyboard.Tests;

public class TypingSessionRecorderTests
{
    static TypingSessionRecorder Started(long wall = 1_000)
    {
        var r = new TypingSessionRecorder();
        r.Start(wall);
        return r;
    }

    [Fact]
    public void ZuKurzeSitzungWirdVerworfen()
    {
        var r = Started();
        for (int i = 0; i < TypingSessionRecorder.MinKeys - 1; i++) r.KeyDown(i * 100, KeyCategory.Alphanumeric);
        Assert.Null(r.Finish(2_000));
        Assert.False(r.IsActive);
    }

    [Fact]
    public void ZaehltTastenartenUndAbstaende()
    {
        var r = Started(wall: 10_000);
        r.KeyDown(0, KeyCategory.Alphanumeric);
        r.KeyDown(100, KeyCategory.Alphanumeric);   // IKI 100
        r.KeyDown(300, KeyCategory.Alphanumeric);   // IKI 200
        r.KeyDown(400, KeyCategory.Space);          // kein IKI (Leerzeichen)
        r.KeyDown(500, KeyCategory.Alphanumeric);   // kein IKI (nach Leerzeichen)
        r.KeyDown(3_000, KeyCategory.Backspace);    // Pause ≥ 2 s, Lücke < 5 s → zählt zur Schreibzeit
        r.KeyDown(9_000, KeyCategory.Punctuation);  // Pause, Lücke ≥ 5 s → nicht zur Schreibzeit
        r.KeyDown(9_100, KeyCategory.Enter);
        r.AddHold(80);
        r.AddHold(120);
        r.AddHold(5_000);                           // zu lang → keine Haltedauer
        r.AutoCorrected();
        r.SuggestionPicked();
        r.SuggestionPicked();

        var s = r.Finish(20_000);

        Assert.NotNull(s);
        Assert.Equal(10_000, s.StartedAtMs);
        Assert.Equal(20_000, s.EndedAtMs);
        Assert.Equal(8, s.TotalKeys);
        Assert.Equal(4, s.AlphanumericKeys);
        Assert.Equal(1, s.SpaceKeys);
        Assert.Equal(1, s.BackspaceKeys);
        Assert.Equal(1, s.PunctuationKeys);
        Assert.Equal(1, s.EnterKeys);
        Assert.Equal(2, s.Pauses);
        Assert.Equal(100 + 200 + 100 + 100 + 2_500 + 100, s.ActiveTypingMs);
        Assert.Equal(150, s.IkiMedianMs);
        Assert.Equal(150, s.IkiMeanMs);
        Assert.Equal(190, s.IkiP90Ms, 6);
        Assert.Equal(Math.Sqrt(5_000), s.IkiSdMs, 6);
        Assert.Equal(100, s.HoldMedianMs);
        Assert.Equal(1, s.Autocorrections);
        Assert.Equal(2, s.SuggestionsPicked);
    }

    [Fact]
    public void OhneStartWirdNichtsGezaehlt()
    {
        var r = new TypingSessionRecorder();
        r.KeyDown(0, KeyCategory.Alphanumeric);
        r.AutoCorrected();
        Assert.Null(r.Finish(1));
    }

    [Fact]
    public void SchluesselEntsprechenDerMeridianSchnittstelle()
    {
        var keys = new TypingSessionSummary().ToValues().Select(kv => kv.Key).ToArray();
        string[] expected =
        [
            "started_ms", "ended_ms", "total_keys", "alnum_keys", "backspace_keys", "space_keys", "enter_keys",
            "punct_keys", "autocorrections", "suggestions_picked", "iki_median_ms", "iki_mean_ms", "iki_sd_ms",
            "iki_p90_ms", "hold_median_ms", "pauses", "active_ms",
        ];
        Assert.Equal(expected, keys);
        Assert.All(new TypingSessionSummary().ToValues(), kv => Assert.True(kv.Value is long or double));
    }
}

public class TypingPrivacyTests
{
    const int Text = 0x1, Number = 0x2, Phone = 0x3;
    const int Password = 0x80, VisiblePassword = 0x90, WebPassword = 0xe0, Email = 0x20;
    const int NoPersonalizedLearning = 0x01000000;

    [Theory]
    [InlineData(Text, 0, true)]
    [InlineData(Text | Email, 0, true)]
    [InlineData(Text | 0x00004000, 0, true)]          // CapSentences
    [InlineData(Text | Password, 0, false)]
    [InlineData(Text | VisiblePassword, 0, false)]
    [InlineData(Text | WebPassword, 0, false)]
    [InlineData(Number, 0, false)]
    [InlineData(Number | 0x10, 0, false)]             // PIN
    [InlineData(Phone, 0, false)]
    [InlineData(0, 0, false)]                         // TYPE_NULL
    [InlineData(Text, NoPersonalizedLearning, false)] // privater Tab, Passwortmanager
    public void MayRecord(int inputType, int imeOptions, bool expected) =>
        Assert.Equal(expected, TypingPrivacy.MayRecord(inputType, imeOptions));
}

public class TypingTrackerTests
{
    static readonly KeyDef A = KeyDef.Char("a", "à", "ä");
    static readonly KeyDef Comma = KeyDef.Char(",");
    static readonly KeyDef Space = KeyDef.Fill(KeyAction.Space);
    static readonly KeyDef Delete = KeyDef.Special(KeyAction.Delete, 1.5f);
    static readonly KeyDef Shift = KeyDef.Special(KeyAction.Shift, 1.5f);

    static void Press(TypingTracker t, KeyDef key, string? output, long down, long up, bool gesture = false)
    {
        t.KeyDown(down);
        t.KeyUp(key, output, up, gesture);
    }

    [Fact]
    public void OhneFreigabeDesFeldesKeineSitzung()
    {
        var t = new TypingTracker();
        t.Start(mayRecord: false, wallMs: 0);
        for (int i = 0; i < 10; i++) Press(t, A, "a", i * 100, i * 100 + 50);
        Assert.Null(t.Finish(5_000));
    }

    [Fact]
    public void OrdnetTastenEinUndUeberspringtGesten()
    {
        var t = new TypingTracker();
        t.Start(mayRecord: true, wallMs: 0);
        Press(t, A, "a", 0, 60);
        Press(t, A, "A", 100, 170);                 // Großbuchstabe: normale Haltedauer
        Press(t, A, "ä", 200, 900);                 // Popup: zählt, aber ohne Haltedauer
        Press(t, Comma, ",", 1_000, 1_050);
        Press(t, Space, " ", 1_100, 1_160);
        Press(t, Space, null, 1_200, 1_800);        // Cursor per Leertaste → keine Taste
        Press(t, A, null, 1_900, 2_600);            // Popup ohne Auswahl → keine Taste
        Press(t, A, null, 2_700, 3_400, gesture: true);      // Wischeingabe → keine Taste
        Press(t, Delete, null, 3_500, 3_900, gesture: true); // Wort-Löschen per Wischen → Korrektur
        Press(t, Delete, null, 4_000, 4_080);
        Press(t, Shift, null, 4_100, 4_150);

        var s = t.Finish(10_000);

        Assert.NotNull(s);
        Assert.Equal(8, s.TotalKeys);
        Assert.Equal(3, s.AlphanumericKeys);
        Assert.Equal(1, s.PunctuationKeys);
        Assert.Equal(1, s.SpaceKeys);
        Assert.Equal(2, s.BackspaceKeys);
        Assert.Equal(100, s.IkiMedianMs);           // a→A und A→ä, je 100 ms ab Drücken
        Assert.Equal(60, s.HoldMedianMs);           // 60, 70, 50, 60, 80, 50 (ohne Popup und Geste) → Median 60
    }

    [Fact]
    public void NeuesFeldSchliesstLaufendeSitzungAb()
    {
        var t = new TypingTracker();
        t.Start(mayRecord: true, wallMs: 0);
        for (int i = 0; i < 6; i++) Press(t, A, "a", i * 100, i * 100 + 50);

        var previous = t.Start(mayRecord: true, wallMs: 5_000);

        Assert.NotNull(previous);
        Assert.Equal(6, previous.TotalKeys);
        Assert.Equal(5_000, previous.EndedAtMs);
        Assert.True(t.IsActive);
    }

    [Fact]
    public void LoslassenOhneDrueckenZaehltNicht()
    {
        var t = new TypingTracker();
        t.Start(mayRecord: true, wallMs: 0);
        for (int i = 0; i < 10; i++) t.KeyUp(A, "a", i * 100, gesture: false);
        Assert.Null(t.Finish(1_000));
    }
}
