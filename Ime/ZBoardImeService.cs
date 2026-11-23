using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.InputMethodServices;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using ZBoard.Keyboard;
using ZBoard.Settings;
using ZBoard.Suggestions;
using ZBoard.Testing;
// Gleichnamige Typen in Android.InputMethodServices / Android.Content.Res eindeutig machen
using KeyboardView = ZBoard.Keyboard.KeyboardView;
using Keycode = Android.Views.Keycode;
using Orientation = Android.Widget.Orientation;

namespace ZBoard.Ime;

/// <summary>
/// Der Tastatur-Dienst. Verbindet Tastaturansicht und Vorschläge und schreibt Text über die InputConnection.
/// </summary>
[Service(
    Name = "de.World_of_Bytes.zBoard.ZBoardImeService",
    Label = "zBoard",
    Permission = "android.permission.BIND_INPUT_METHOD",
    Exported = true)]
[IntentFilter(new[] { "android.view.InputMethod" })]
[MetaData("android.view.im", Resource = "@xml/method")]
public sealed partial class ZBoardImeService : InputMethodService, IKeyboardActionListener
{
    const int DoubleTapShiftMs = 300;
    const int DoubleSpaceMs = 600;
    const string AutoPunctuation = ".,!?;:";

    static readonly ConcurrentDictionary<string, Task<SuggestionEngine>> Engines = new();

    sealed record Correction(string Original, string Corrected, string Separator);

    /// <summary>Das zuletzt mit der Leertaste abgeschlossene Wort: wie getippt, wie eingefügt, und was davor stand.</summary>
    sealed record SpaceCommit(string Typed, string Committed, WordContext Context, string? Previous);

    SpaceCommit? _lastSpaceCommit;

    readonly StringBuilder _composing = new();

    ZBoardSettings _settings = null!;

    KeyboardView? _keyboardView;
    SuggestionBarView? _suggestionBar;
    EditorProfile _profile = EditorProfile.From(null);

    string _language = "de";
    Task<MultilingualEngine>? _engineTask;  // alle Sprachen der Eingabe, Layoutsprache zuerst
    ShiftState _shift;
    bool _shiftManual;
    long _lastShiftTapMs;
    int _symbolsPage;           // 0 = Buchstaben, 1/2 = Symbolseiten, 3 = Ziffernfeld (Zahlen-, PIN-, Telefonfelder)
    const int NumpadPage = 3;
    long _lastSpaceMs;
    bool _autoSpaceInserted;    // Leerzeichen kam von einem Vorschlag
    bool _toolbarOpen;          // Werkzeugleiste über den Pfeil geöffnet; bleibt, bis getippt wird

    /// <summary>Je Zeichen des Worts in Bearbeitung: das Zeichen und wo der Finger die Taste traf (null = unbekannt).</summary>
    readonly List<(char Char, Vector2? Point)> _touches = [];
    Correction? _lastCorrection;
    string? _restoredWord;      // per Rücktaste wiederhergestelltes Original; das nächste Leerzeichen übernimmt und lernt es
    SuggestionResult _currentSuggestions = SuggestionResult.Empty;

    bool _glideActive;                          // das Wort in Bearbeitung kam aus der Wischeingabe
    IReadOnlyList<string> _glideCandidates = [];
    int _selStart;
    int _selEnd;
    int _swipeAnchor = -1;                      // Cursor zu Beginn des Wort-Löschens per Wischen
    string _swipeText = string.Empty;
    int _swipeChars;
    Android.Media.AudioManager? _audio;

    MultilingualEngine? Engine => _engineTask is { IsCompletedSuccessfully: true } t ? t.Result : null;

    IInputConnection? Ic => CurrentInputConnection;

    // ---------- Lebenszyklus ----------

    public override void OnCreate()
    {
        base.OnCreate();
        _settings = new ZBoardSettings(this);

        _audio = GetSystemService(AudioService) as Android.Media.AudioManager;
        InitClipboard();
        Perf.Log("process-to-service", Perf.SinceProcessStart);

        // Wörterbücher schon jetzt im Hintergrund laden, nicht erst, wenn die Tastatur aufgebaut ist: Nach einem
        // Neustart des Dienstes sind die Vorschläge so früher wieder da.
        foreach (var language in MultilingualEngine.ActiveLanguages(_settings.CurrentLanguage, _settings.Languages, _settings.MultilingualTyping))
        {
            EngineTask(language);
        }
    }

    static string LearningFile(Context context, string language) =>
        Path.Combine(context.FilesDir!.AbsolutePath, "learning", $"{language}.tsv");

    /// <summary>Löscht alles Gelernte (Dateien und geladene Daten). Aufgerufen aus den Einstellungen.</summary>
    public static void ClearLearned(Context context)
    {
        foreach (var task in Engines.Values)
        {
            if (task.IsCompletedSuccessfully) task.Result.History.Clear();
        }

        ClearPinyinLearning();

        var dir = Path.Combine(context.FilesDir!.AbsolutePath, "learning");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    void SaveLearning()
    {
        foreach (var (language, task) in Engines)
        {
            if (!task.IsCompletedSuccessfully || !task.Result.History.IsDirty) continue;
            var path = LearningFile(this, language);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            task.Result.History.Save(stream);
        }

        SavePinyinLearning();
    }

    public override View? OnCreateInputView()
    {
        long started = Perf.Now;
        var view = BuildInputView();
        Perf.Log("create-input-view", Perf.Since(started));
        return view;
    }

    public override void OnStartInputView(EditorInfo? info, bool restarting)
    {
        base.OnStartInputView(info, restarting);
        long started = Perf.Now;

        _profile = EditorProfile.From(info);
        _composing.Clear();
        _lastCorrection = null;
        _restoredWord = null;
        _autoSpaceInserted = false;
        _toolbarOpen = false;
        _glideActive = false;
        _swipeAnchor = -1;
        _reselect = null;
        _selStart = info?.InitialSelStart ?? 0;
        _selEnd = info?.InitialSelEnd ?? 0;
        _shiftManual = false;
        _shift = ShiftState.Off;
        // Wie Gboard: Zahlen-, PIN-, Telefon- und Datumsfelder bekommen ein Ziffernfeld statt Buchstaben
        _symbolsPage = _profile.IsNumeric ? NumpadPage : 0;

        ReloadRestoredData();
        StartPersonal();
        StartPanels();
        RefreshSystemBarPadding();
        UpdatePopupSpace(); // Platz für Popups über der Tastatur (falls Android das Fenster neu aufgebaut hat)
        Perf.Log("start-panels-window", Perf.Since(started));
        long step = Perf.Now;
        SetLanguage(_settings.CurrentLanguage);
        Perf.Log("start-language", Perf.Since(step));
        step = Perf.Now;
        ApplyTheme();
        Perf.Log("start-theme", Perf.Since(step));
        step = Perf.Now;
        _keyboardView?.EnterLabel = _profile.EnterLabel;
        _keyboardView?.GlideEnabled = _settings.GlideTyping && _profile.AllowSuggestions;
        _keyboardView?.ShowKeyPreview = _settings.KeyPreview;
        StartEmailInput();
        Perf.Log("start-email", Perf.Since(step));
        step = Perf.Now;
        StartPlugins();
        Perf.Log("start-plugins", Perf.Since(step));
        if (_suggestionBar != null)
        {
            _suggestionBar.Visibility = ViewStates.Visible;
            ShowToolbar();
            if (_profile.IsEmail) UpdateSuggestions(); // wie Gboard: gemerkte Adressen schon im leeren Feld
        }

        UpdateAutoShift();
        Sync.SyncManager.SyncIfDue(this); // holt Neues von anderen Geräten (höchstens alle 15 Minuten)
        Perf.Log("start-input-view", Perf.Since(started));
    }

    public override void OnFinishInputView(bool finishingInput)
    {
        LearnEmailsFromField(); // solange das Feld noch erreichbar ist
        base.OnFinishInputView(finishingInput);
        _composing.Clear();
        _reselect = null;
        _suggestionBar?.Clear();
        FinishPanels();
        FinishPlugins();
        StopVoice(cancel: true);

        try
        {
            SaveLearning();
            SaveEmails();
        }
        catch (IOException)
        {
            // Gelerntes geht schlimmstenfalls seit dem letzten Speichern verloren; Tippen bleibt unbeeinträchtigt.
        }

        Sync.SyncManager.SyncIfDue(this);
    }

    public override void OnUpdateSelection(
        int oldSelStart, int oldSelEnd, int newSelStart, int newSelEnd, int candidatesStart, int candidatesEnd)
    {
#if DEBUG || ZBOARD_TESTHOOKS
        Android.Util.Log.Info("zBoardTest", $"SEL {newSelStart} {newSelEnd} composing {candidatesStart} {candidatesEnd}");
#endif
        base.OnUpdateSelection(oldSelStart, oldSelEnd, newSelStart, newSelEnd, candidatesStart, candidatesEnd);
        _selStart = newSelStart;
        _selEnd = newSelEnd;
        if (_swipeAnchor >= 0) return; // eigene Markierung beim Wort-Löschen per Wischen

        // Cursor wurde von außen bewegt (Antippen im Text): Wortbearbeitung beenden.
        bool cursorLeftWord = _composing.Length > 0
            && (newSelStart != candidatesEnd || newSelEnd != candidatesEnd);
        if (cursorLeftWord)
        {
            _composing.Clear();
            _glideActive = false;
            _restoredWord = null;
            Ic?.FinishComposingText();
            UpdateSuggestions();
        }

        // Die eigene Autokorrektur bewegt den Cursor auch; nur wenn er danach woanders steht, ist "Rückgängig" vorbei.
        if (newSelStart != oldSelStart && _composing.Length == 0 && _lastCorrection is { } c
            && (newSelStart != newSelEnd || !CursorFollows(c)))
        {
            _lastCorrection = null;
        }
        if (_composing.Length == 0) UpdateAutoShift();

        // Wie Gboard: Antippen eines Worts im Text zeigt dessen Vorschläge erneut
        if (_composing.Length == 0 && (newSelStart != oldSelStart || newSelEnd != oldSelEnd)) OnCursorPlaced(newSelStart, newSelEnd);
    }

    // ---------- Tasten ----------

    public void OnKeyDown(KeyDef key, long uptimeMs)
    {
        if (_settings.Haptics) _keyboardView?.PerformKeyHaptic();
        if (_settings.KeySound) PlayKeySound(key.Action);
    }

    public void OnKeyUp(KeyDef key, string? output, long uptimeMs, bool gesture)
    {
    }

    void PlayKeySound(KeyAction action)
    {
        // AudioManager.FX_KEYPRESS_STANDARD = 5, _SPACEBAR = 6, _DELETE = 7, _RETURN = 8
        int effect = action switch
        {
            KeyAction.Space => 6,
            KeyAction.Delete => 7,
            KeyAction.Enter => 8,
            _ => 5,
        };
        _audio?.PlaySoundEffect((Android.Media.SoundEffect)effect, -1f);
    }

    public void OnText(string text)
    {
        var ic = Ic;
        if (ic == null) return;
        if (_emojiSearchActive)
        {
            EmojiSearchType(text);
            return;
        }

        MarkEdit();

        _lastCorrection = null;
        bool wordChar = text.Length > 0 && (char.IsLetterOrDigit(text, 0) || text == "'" || (text == "-" && _composing.Length > 0));

        // Nach einem gewischten Wort beginnt ein getippter Buchstabe ein neues Wort.
        if (_glideActive) CommitComposing(autocorrect: false, separator: wordChar ? " " : string.Empty);

        if (wordChar && _profile.AllowSuggestions)
        {
            _autoSpaceInserted = false;
            TrackTouches(text, _keyboardView?.TapPoint);
            if (text.Length == 1 && _language == "vi" && _settings.VietnameseTelex)
            {
                // Telex: Der Buchstabe kann das Wort verändern statt angehängt zu werden ("tieng" + "s" → "tiếng").
                string word = Telex.Apply(_composing.ToString(), text[0]);
                _composing.Clear().Append(word);
            }
            else
            {
                _composing.Append(text);
            }

            ic.SetComposingText(J(_composing.ToString()), 1);
            UpdateSuggestions();
        }
        else if (!wordChar && text.Length == 1
                 && (AutoPunctuation.Contains(text[0]) || (IsChinese && ChinesePunctuation.Contains(text[0]))))
        {
            CommitSeparator(text);
        }
        else
        {
            CommitComposing(autocorrect: false, separator: string.Empty);
            ic.CommitText(J(text), 1);
            _autoSpaceInserted = false;
        }

        if (_shift == ShiftState.OneShot)
        {
            SetShift(ShiftState.Off, manual: false);
        }

        if (!wordChar)
        {
            UpdateAutoShift();
            UpdateSuggestions();
        }
    }

    public void OnAction(KeyAction action)
    {
        if (_emojiSearchActive && EmojiSearchAction(action)) return;
        if (action is KeyAction.Space or KeyAction.Delete or KeyAction.Enter) MarkEdit();

        switch (action)
        {
            case KeyAction.Space:
                HandleSpace();
                break;
            case KeyAction.Delete:
                HandleDelete();
                break;
            case KeyAction.Enter:
                HandleEnter();
                break;
            case KeyAction.Shift:
                HandleShiftTap();
                break;
            case KeyAction.ToSymbols:
                _symbolsPage = 1;
                ApplyLayout();
                break;
            case KeyAction.ToSymbols2:
                _symbolsPage = 2;
                ApplyLayout();
                break;
            case KeyAction.ToAlpha:
                _symbolsPage = 0;
                ApplyLayout();
                UpdateAutoShift();
                break;
            case KeyAction.Language:
                SwitchLanguage();
                break;
            case KeyAction.Emoji:
                ShowEmojiPanel();
                break;
        }
    }

    public void OnCursorMove(int delta)
    {
        MarkEdit();
        CommitComposing(autocorrect: false, separator: string.Empty);
        var keycode = delta > 0 ? Keycode.DpadRight : Keycode.DpadLeft;
        for (int i = 0; i < Math.Abs(delta); i++) SendDownUpKeyEvents(keycode);
        UpdateSuggestions();
    }

    // ---------- Einzelne Aktionen ----------

    void HandleSpace()
    {
        var ic = Ic;
        if (ic == null) return;
        long now = SystemClock.UptimeMillis();

        if (_glideActive)
        {
            CommitComposing(autocorrect: false, separator: " ");
        }
        else if (_composing.Length > 0)
        {
            CommitComposing(autocorrect: true, separator: " ");
        }
        else if (_settings.DoubleSpacePeriod && now - _lastSpaceMs < DoubleSpaceMs && EndsWithWordAndSpace())
        {
            // Doppelte Leertaste: "Wort " → "Wort. "
            ic.DeleteSurroundingText(1, 0);
            ic.CommitText(J(". "), 1);
            now = 0; // dreifache Leertaste soll keinen zweiten Punkt erzeugen
        }
        else
        {
            ic.CommitText(J(" "), 1);
        }

        _lastSpaceMs = now;
        _autoSpaceInserted = false;
        UpdateAutoShift();
        UpdateSuggestions();
    }

    void HandleDelete()
    {
        var ic = Ic;
        if (ic == null) return;
        _autoSpaceInserted = false;

        // Löschen direkt nach der Wischeingabe entfernt das ganze Wort.
        if (_glideActive)
        {
            _composing.Clear();
            _glideActive = false;
            ic.SetComposingText(J(string.Empty), 1);
            ic.FinishComposingText();
            UpdateSuggestions();
            UpdateAutoShift();
            return;
        }

        // Wie Gboard: Löschen direkt nach einer Autokorrektur stellt das getippte Wort wieder her ("brechen " → "umbrechen").
        // Es bleibt in Bearbeitung; erst das nächste Leerzeichen übernimmt es unverändert und merkt es sich.
        if (_lastCorrection is { } c && _composing.Length == 0 && CursorFollows(c))
        {
            ic.DeleteSurroundingText(c.Corrected.Length + c.Separator.Length, 0);
            _composing.Append(c.Original);
            ic.SetComposingText(J(c.Original), 1);
            Engine?.SuppressCorrection(c.Original);
            _restoredWord = c.Original;
            _lastCorrection = null;
            UpdateSuggestions();
            UpdateAutoShift();
            return;
        }

        _lastCorrection = null;

        if (_composing.Length > 0)
        {
            int remove = _composing.Length >= 2 && char.IsSurrogatePair(_composing[^2], _composing[^1]) ? 2 : 1;
            _composing.Length -= remove;
            ic.SetComposingText(J(_composing.ToString()), 1);
            if (_composing.Length == 0)
            {
                ic.FinishComposingText();
                _restoredWord = null;
            }
            UpdateSuggestions();
        }
        else
        {
            SendDownUpKeyEvents(Keycode.Del);
            UpdateSuggestions();
        }

        UpdateAutoShift();
    }

    void HandleEnter()
    {
        var ic = Ic;
        if (ic == null) return;
        CommitComposing(autocorrect: false, separator: string.Empty);
        LearnEmailsFromField(); // vor "Weiter"/"Senden", danach ist das Feld oft schon gewechselt

        if (_profile.HasEnterAction && !_profile.MultiLine)
        {
            ic.PerformEditorAction((ImeAction)_profile.Action);
        }
        else
        {
            SendDownUpKeyEvents(Keycode.Enter);
        }

        UpdateAutoShift();
        UpdateSuggestions();
    }

    void HandleShiftTap()
    {
        long now = SystemClock.UptimeMillis();
        var next = _shift switch
        {
            ShiftState.Locked => ShiftState.Off,
            ShiftState.OneShot when now - _lastShiftTapMs < DoubleTapShiftMs => ShiftState.Locked,
            ShiftState.OneShot => ShiftState.Off,
            _ => ShiftState.OneShot,
        };
        _lastShiftTapMs = now;
        SetShift(next, manual: true);
    }

    /// <summary>Satzzeichen beendet ein Wort (mit Autokorrektur). Nach einem Vorschlags-Leerzeichen rückt das Zeichen ans Wort.</summary>
    void CommitSeparator(string punctuation)
    {
        var ic = Ic!;
        if (_composing.Length > 0)
        {
            CommitComposing(autocorrect: true, separator: punctuation);
        }
        else if (_autoSpaceInserted && TextBeforeCursor(1) == " ")
        {
            ic.DeleteSurroundingText(1, 0);
            ic.CommitText(J(punctuation + " "), 1);
        }
        else
        {
            ic.CommitText(J(punctuation), 1);
        }

        _autoSpaceInserted = false;
    }

    /// <summary>Schließt das aktuelle Wort ab, wahlweise mit Autokorrektur, und hängt <paramref name="separator"/> an.</summary>
    void CommitComposing(bool autocorrect, string separator)
    {
        var ic = Ic;
        if (ic == null) return;

        if (_composing.Length == 0)
        {
            if (separator.Length > 0) ic.CommitText(J(separator), 1);
            return;
        }

        if (IsChinese && !_glideActive)
        {
            CommitChinese(convert: autocorrect, separator);
            return;
        }

        string typed = _composing.ToString();
        string final = typed;
        bool restored = typed == _restoredWord; // wiederhergestelltes Original: nicht korrigieren, sondern lernen
        _restoredWord = null;

        // Derselbe Kontext wie in der Vorschlagsleiste, damit eingesetzt wird, was dort hervorgehoben ist.
        string? previous = PreviousWord();
        var context = SentenceContext();
        bool inUrl = InUrlOrAddress();
        bool mayCorrect = autocorrect && !restored && !inUrl && _settings.AutoCorrect && _profile.AllowAutocorrect;

        // Leertaste mitten im Wort ("da ke", "ha llo"): Das Stück davor und dieses ergeben zusammen das gemeinte Wort.
        var before = _lastSpaceCommit;
        _lastSpaceCommit = null;
        if (mayCorrect && !_glideActive && before != null
            && TextBeforeCursor(before.Committed.Length + 1 + typed.Length) == before.Committed + " " + typed
            && Engine?.JoinAcrossSpace(before.Typed, typed, before.Context) is { } joined)
        {
            ic.BeginBatchEdit();
            ic.FinishComposingText();
            ic.DeleteSurroundingText(before.Committed.Length + 1 + typed.Length, 0);
            ic.CommitText(J(joined + separator), 1);
            ic.EndBatchEdit();

            _lastCorrection = new Correction(before.Typed + " " + typed, joined, separator); // Löschen stellt das Getippte wieder her
            _composing.Clear();
            _currentSuggestions = SuggestionResult.Empty;
            if (_settings.LearnWords && _profile.AllowLearning) Engine?.Unlearn(before.Previous, before.Committed);
            Learn(before.Previous, joined);
            return;
        }

        if (mayCorrect && _personal.Word(typed) is { } own)
        {
            // Eigenes Wort: nie korrigieren, nur die gespeicherte Schreibweise einsetzen ("zboard" → "zBoard")
            if (own != typed)
            {
                final = own;
                _lastCorrection = new Correction(typed, own, separator);
            }
        }
        else if (mayCorrect && !_personal.IsShortcut(typed)) // ein Kürzel bleibt stehen, bis sein Text angetippt wird
        {
            var result = Engine?.Suggest(typed, context, touches: Touches());
            if (result?.AutoCorrection is { } corrected && corrected != typed)
            {
                final = corrected;
                _lastCorrection = new Correction(typed, corrected, separator);
            }
        }

        ic.CommitText(J(final + separator), 1);
        if (separator == " " && !_glideActive && !restored) _lastSpaceCommit = new SpaceCommit(typed, final, context, previous);
        _composing.Clear();
        _glideActive = false;
        _currentSuggestions = SuggestionResult.Empty;
        if (!inUrl) Learn(previous, final, accepted: restored); // Teile von Links nicht als Wörter lernen
    }

    /// <summary>
    /// Gehört das Wort in Bearbeitung zu einem Link oder einer Adresse ("https://…", "www.…", "name@…", "example.de")?
    /// Dann wie bei Gboard keine Autokorrektur und nichts lernen, auch wenn das Feld selbst ein normales Textfeld ist.
    /// </summary>
    bool InUrlOrAddress()
    {
        if (_composing.Length == 0) return false;
        string current = _composing.ToString();
        string before = TextBeforeCursor(256);
        if (before.EndsWith(current, StringComparison.Ordinal)) before = before[..^current.Length];
        return TextBoundaries.IsInUrlOrAddress(before, current);
    }

    /// <summary>Steht der Cursor direkt hinter dem autokorrigierten Wort samt Trennzeichen?</summary>
    bool CursorFollows(Correction c) =>
        TextBeforeCursor(c.Corrected.Length + c.Separator.Length) == c.Corrected + c.Separator;

    void OnSuggestionPicked(SuggestionSlot slot, long downUptime, long upUptime)
    {
        var ic = Ic;
        if (ic == null) return;
        if (slot.IsEmail)
        {
            PickEmail(slot);
            return;
        }

        if (slot.IsReplacement)
        {
            PickReplacement(slot);
            return;
        }

        if (PickChinese(slot)) return;

        if (slot.IsShortcut)
        {
            PickShortcut(slot);
            return;
        }

        MarkEdit();

        if (slot.IsLiteral) Engine?.SuppressCorrection(slot.Word);

        string? previous = PreviousWord();
        ic.CommitText(J(slot.Word + " "), 1);
        _composing.Clear();
        _glideActive = false;
        _lastCorrection = null;
        _autoSpaceInserted = true;
        if (slot.IsPhrase)
        {
            foreach (var word in slot.Word.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Learn(previous, word);
                previous = word;
            }
        }
        else
        {
            Learn(previous, slot.Word, accepted: slot.IsLiteral);
        }

        UpdateAutoShift();
        UpdateSuggestions();
    }

    void Learn(string? previous, string word, bool accepted = false)
    {
        if (!_settings.LearnWords || !_profile.AllowLearning) return;

        // Eine Korrektur kann zwei Wörter liefern ("ichhabe" → "ich habe"): jedes einzeln lernen.
        foreach (var part in word.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Engine?.Learn(previous, part, accepted);
            previous = part;
        }
    }

    /// <summary>
    /// Das Wort vor dem Cursor (bzw. vor dem gerade getippten Wort), wenn dazwischen nur Leerraum steht.
    /// Nach Satzzeichen gibt es keinen Kontext (null).
    /// </summary>
    string? PreviousWord(bool adjacent = false)
    {
        string before = TextBeforeCursor(64);
        if (adjacent && _composing.Length == 0 && before.Length > 0 && char.IsLetterOrDigit(before[^1]))
        {
            before += " "; // Wort direkt vor dem Cursor zählt als vorheriges Wort
        }

        if (_composing.Length > 0)
        {
            string current = _composing.ToString();
            if (!before.EndsWith(current, StringComparison.Ordinal)) return null;
            before = before[..^current.Length];
        }

        if (before.Length == 0 || !char.IsWhiteSpace(before[^1])) return null;
        string trimmed = before.TrimEnd();
        if (trimmed.Length == 0) return null;

        int start = trimmed.Length;
        while (start > 0 && (char.IsLetterOrDigit(trimmed[start - 1]) || trimmed[start - 1] is '\'' or '-')) start--;
        return start < trimmed.Length ? trimmed[start..] : null;
    }

    /// <summary>
    /// Die beiden Wörter vor dem Cursor (bzw. vor dem gerade getippten Wort) für das Sprachmodell; auch Satzanfang und
    /// Komma zählen. <paramref name="adjacent"/>: ein Wort direkt vor dem Cursor zählt als vorheriges Wort.
    /// </summary>
    WordContext SentenceContext(bool adjacent = false)
    {
        const int Window = 96;
        string before = TextBeforeCursor(Window);
        bool fromFieldStart = before.Length < Window;
        if (adjacent && _composing.Length == 0 && before.Length > 0 && char.IsLetterOrDigit(before[^1]))
        {
            before += " ";
        }

        if (_composing.Length > 0)
        {
            string current = _composing.ToString();
            if (!before.EndsWith(current, StringComparison.Ordinal)) return WordContext.None;
            before = before[..^current.Length];
        }

        return WordContext.FromText(before, fromFieldStart);
    }

    /// <summary>Merkt sich zu den Zeichen, die gleich an das Wort in Bearbeitung angehängt werden, den Fingerpunkt.</summary>
    void TrackTouches(string text, Vector2? point)
    {
        // Das Wort kann sich anders als durch Tippen geändert haben (Löschen, Wiederherstellen): Liste angleichen.
        int length = _composing.Length;
        if (_touches.Count > length) _touches.RemoveRange(length, _touches.Count - length);
        while (_touches.Count < length) _touches.Add((_composing[_touches.Count], null));

        foreach (char c in text) _touches.Add((c, text.Length == 1 ? point : null));
    }

    /// <summary>
    /// Fingerpunkte zum Wort in Bearbeitung für die Autokorrektur; null, wenn keine bekannt sind. Punkte zählen nur,
    /// wo das gemerkte Zeichen noch an derselben Stelle steht.
    /// </summary>
    TouchTrace? Touches()
    {
        if (_keyboardView == null || _glideActive || _composing.Length == 0 || _touches.Count < _composing.Length) return null;

        var points = new Vector2?[_composing.Length];
        bool any = false;
        for (int i = 0; i < points.Length; i++)
        {
            if (char.ToLowerInvariant(_touches[i].Char) != char.ToLowerInvariant(_composing[i])) continue;
            points[i] = _touches[i].Point;
            any |= points[i] != null;
        }

        return any ? new TouchTrace(points, _keyboardView.KeyCenters) : null;
    }

    // ---------- Wischeingabe ----------

    public void OnGlide(IReadOnlyList<Vector2> path, IReadOnlyDictionary<char, Vector2> keyCenters)
    {
        var ic = Ic;
        var engine = Engine;
        if (ic == null || engine == null) return;
        MarkEdit();

        if (_composing.Length > 0) CommitComposing(autocorrect: !_glideActive, separator: string.Empty);

        var words = engine.Glide(path, keyCenters, SentenceContext(adjacent: true));
        if (words.Count == 0) return;
        _glideCandidates = [.. words.Select(ApplyShiftCase)];

        // Leerzeichen vor dem gewischten Wort, außer am Anfang oder nach öffnender Klammer/Anführung
        string before = TextBeforeCursor(1);
        if (before.Length > 0 && !char.IsWhiteSpace(before[0]) && !"([{\"„‚'¿¡".Contains(before[0]))
        {
            ic.CommitText(J(" "), 1);
        }

        _composing.Clear().Append(_glideCandidates[0]);
        ic.SetComposingText(J(_composing.ToString()), 1);
        _glideActive = true;
        _lastCorrection = null;
        _autoSpaceInserted = false;
        if (_shift == ShiftState.OneShot) SetShift(ShiftState.Off, manual: false);
        ShowGlideCandidates();
    }

    string ApplyShiftCase(string word) => _shift switch
    {
        ShiftState.Locked => TextCase.Upper(word, _language),
        ShiftState.OneShot => TextCase.Capitalize(word, _language),
        _ => word,
    };

    void ShowGlideCandidates()
    {
        SuggestionSlot? Slot(int i) => i < _glideCandidates.Count
            ? new SuggestionSlot(_glideCandidates[i], _glideCandidates[i], i == 0, false)
            : null;
        _suggestionBar?.SetSlots(Slot(1), Slot(0), Slot(2));
    }

    // ---------- Wort-Löschen per Wischen von ⌫ ----------

    public void OnDeleteSwipe(int words)
    {
        var ic = Ic;
        if (ic == null) return;

        MarkEdit();
        if (_swipeAnchor < 0)
        {
            if (_composing.Length > 0) CommitComposing(autocorrect: false, separator: string.Empty);
            _swipeAnchor = _selEnd;
            _swipeText = TextBeforeCursor(1000);
        }

        _swipeChars = TextBoundaries.CharsForWords(_swipeText, words);
        ic.SetSelection(_swipeAnchor - _swipeChars, _swipeAnchor);
    }

    public void OnDeleteSwipeEnd(bool commit)
    {
        var ic = Ic;
        if (ic != null && _swipeAnchor >= 0)
        {
            if (commit && _swipeChars > 0) ic.CommitText(J(string.Empty), 1); // ersetzt die Markierung
            else ic.SetSelection(_swipeAnchor, _swipeAnchor);
        }

        _swipeAnchor = -1;
        _swipeChars = 0;
        _swipeText = string.Empty;
        _lastCorrection = null;
        UpdateAutoShift();
        UpdateSuggestions();
    }

    // ---------- Vorschläge ----------

    void UpdateSuggestions()
    {
        if (_suggestionBar == null || _emojiSearchActive || _voiceActive) return;
        if (HandwritingOpen)
        {
            ShowHandwritingBar();
            return;
        }

        if (_toolbarOpen || TextEditOpen) // Textbearbeitung: Die Leiste bleibt ruhig, während der Cursor wandert
        {
            ShowToolbar();
            return;
        }

        if (!_glideActive && TryShowEmailSuggestions()) return;
        if (_reselect != null && _composing.Length == 0 && ShowReselect()) return;
        if (IsChinese && _composing.Length > 0 && !_glideActive && _profile.AllowSuggestions)
        {
            ShowPinyinCandidates();
            return;
        }

        // Persönliches Wörterbuch: eigene Wörter und Kürzel, auch solange die Wortlisten noch laden
        if (_composing.Length > 0 && !_glideActive && _profile.AllowSuggestions && ShowPersonalSuggestions(_composing.ToString())) return;

        var engine = Engine;
        if (engine == null || !_profile.AllowSuggestions)
        {
            _currentSuggestions = SuggestionResult.Empty;
            ShowToolbar();
            return;
        }

        if (_glideActive)
        {
            ShowGlideCandidates();
            return;
        }

        if (_composing.Length == 0)
        {
            _currentSuggestions = SuggestionResult.Empty;
            ShowPredictions(engine);
            return;
        }

        var typed = _composing.ToString();
        long suggestStarted = Perf.Now;
        var result = engine.Suggest(typed, SentenceContext(), touches: Touches());
        Perf.Log(typed.Length >= 10 ? "suggest-long" : "suggest", Perf.Since(suggestStarted));
        _currentSuggestions = result;
        bool autocorrectActive = _settings.AutoCorrect && _profile.AllowAutocorrect && !InUrlOrAddress();

        if (autocorrectActive && result.AutoCorrection is { } correction)
        {
            // Links das Getippte in Anführungszeichen, Mitte (fett) die Korrektur, rechts eine Alternative
            var alternative = result.Candidates.FirstOrDefault(c => c != correction && c != typed);
            _suggestionBar.SetSlots(
                new SuggestionSlot($"„{typed}“", typed, false, true),
                new SuggestionSlot(correction, correction, true, false),
                PhraseSlot(engine, correction)
                ?? (alternative == null ? null : new SuggestionSlot(alternative, alternative, false, false)));
            return;
        }

        var candidates = result.Candidates.ToList();
        SuggestionSlot? Slot(int i) => i < candidates.Count ? new SuggestionSlot(candidates[i], candidates[i], false, false) : null;

        var left = Slot(1);
        if (!result.TypedIsWord && !candidates.Contains(typed))
        {
            left = new SuggestionSlot($"„{typed}“", typed, false, true);
        }

        // Gelernte Wortfolge zum besten Vorschlag (bzw. zum fertig getippten Wort) rechts anbieten
        string? head = candidates.Count > 0 ? candidates[0] : result.TypedIsWord ? typed : null;
        _suggestionBar.SetSlots(left, Slot(0), (head == null ? null : PhraseSlot(engine, head)) ?? Slot(2));
    }

    /// <summary>
    /// Wie Gboard: Ganze Wortfolgen vorschlagen, die man oft so schreibt ("freundlichen Grüßen"). Nur aus Gelerntem und
    /// nur, wenn Lernen und Nächstes-Wort-Vorhersage an sind.
    /// </summary>
    SuggestionSlot? PhraseSlot(MultilingualEngine engine, string word)
    {
        if (!_settings.NextWordPrediction || !_settings.LearnWords || !_profile.AllowLearning) return null;
        var continuation = engine.ContinuePhrase(word, maxWords: 2);
        if (continuation == null) return null;
        string phrase = word + " " + continuation;
        return new SuggestionSlot(phrase, phrase, false, false, IsPhrase: true);
    }

    /// <summary>Nächstes-Wort-Vorhersage nach einem Leerzeichen.</summary>
    void ShowPredictions(MultilingualEngine engine)
    {
        WordContext context = !_settings.NextWordPrediction ? WordContext.None
            : IsChinese ? ChineseContext() // ohne Leerzeichen gibt es kein "Wort vor dem Cursor": das zuletzt eingesetzte
            : SentenceContext();

        // Im leeren Feld bleibt die Werkzeugleiste (wie bei Gboard); Satzanfänge sagt zBoard erst im Text vorher.
        if (context.IsSentenceStart && TextBeforeCursor(1).Length == 0) context = WordContext.None;

        IReadOnlyList<string> predictions = context.Previous == null ? [] : engine.Predict(context);
        if (context.IsSentenceStart && _shift != ShiftState.Off)
        {
            // Die Umschalttaste ist aktiv: Vorhersagen so zeigen, wie sie eingefügt werden.
            predictions = [.. predictions.Select(p => TextCase.Capitalize(p, _language))];
        }

        if (predictions.Count == 0)
        {
            ShowToolbar();
            return;
        }

        SuggestionSlot? Slot(int i) => i < predictions.Count ? new SuggestionSlot(predictions[i], predictions[i], false, false) : null;
        _suggestionBar!.SetSlots(Slot(1), Slot(0), PhraseSlot(engine, predictions[0]) ?? Slot(2));
    }

    // ---------- Umschalt / Großschreibung ----------

    void SetShift(ShiftState state, bool manual)
    {
        _shift = state;
        _shiftManual = manual && state != ShiftState.Off;
        _keyboardView?.Shift = state;
    }

    /// <summary>Großschreibung am Satzanfang, wenn das Feld es verlangt.</summary>
    void UpdateAutoShift()
    {
        if (_shift == ShiftState.Locked || _shiftManual || _symbolsPage != 0) return;
        if (!_settings.AutoCaps || !_profile.CapsRequested)
        {
            SetShift(ShiftState.Off, manual: false);
            return;
        }

        bool capitalize = TextBoundaries.IsSentenceStart(TextBeforeCursor(8));
        SetShift(capitalize ? ShiftState.OneShot : ShiftState.Off, manual: false);
    }

    bool EndsWithWordAndSpace()
    {
        string before = TextBeforeCursor(2);
        return before.Length == 2 && before[1] == ' ' && char.IsLetterOrDigit(before[0]);
    }

    // ---------- Layout, Sprache, Theme ----------

    void SetLanguage(string language)
    {
        _language = language;
        if (_settings.CurrentLanguage != language) _settings.CurrentLanguage = language; // nicht bei jedem Öffnen schreiben
        EnsurePinyin();
        var languages = InputLanguages();
        long started = Perf.Now;
        var tasks = languages.Select(EngineTask).ToArray();
        bool loading = tasks.Any(t => !t.IsCompleted);
        _engineTask = CombineEngines(languages, tasks);
        _engineTask.ContinueWith(
            _ =>
            {
                if (loading)
                {
                    Perf.Log("engines-ready", Perf.Since(started));
                    Perf.Log("engines-ready-since-process-start", Perf.SinceProcessStart);
                    Perf.Log("managed-heap-mb", GC.GetTotalMemory(false) / 1048576.0);
                }

                new Handler(Looper.MainLooper!).Post(UpdateSuggestions);
            },
            TaskContinuationOptions.OnlyOnRanToCompletion);
        ApplyLayout();
    }

    /// <summary>
    /// Sprachen, deren Wörterbücher gerade gelten: wie bei Gboard bei mehrsprachiger Eingabe alle aktiven (höchstens drei),
    /// die des Layouts zuerst; sonst nur diese.
    /// </summary>
    IReadOnlyList<string> InputLanguages() =>
        MultilingualEngine.ActiveLanguages(_language, _settings.Languages, _settings.MultilingualTyping);

    /// <summary>Fasst die geladenen Sprachen zusammen; fehlt ein Wörterbuch, geht es ohne diese Sprache weiter.</summary>
    static async Task<MultilingualEngine> CombineEngines(IReadOnlyList<string> languages, Task<SuggestionEngine>[] tasks)
    {
        var loaded = new List<(string Language, SuggestionEngine Engine)>(tasks.Length);
        for (int i = 0; i < tasks.Length; i++)
        {
            try
            {
                loaded.Add((languages[i], await tasks[i].ConfigureAwait(false)));
            }
            catch (Exception e) when (i > 0)
            {
                Android.Util.Log.Warn("zBoard", $"Wörterbuch {languages[i]} nicht geladen: {e.Message}");
                Engines.TryRemove(languages[i], out _);
            }
        }

        return new MultilingualEngine(loaded);
    }

    /// <summary>
    /// Lässt Vorschläge, Korrektur, Vorhersage und Wischeingabe einmal im Hintergrund laufen. Der erste Aufruf ist
    /// langsam, weil die Laufzeit den Code dafür erst übersetzt; ohne das Aufwärmen träfe diese Wartezeit den ersten
    /// Tastendruck. Die Ergebnisse werden verworfen.
    /// </summary>
    /// <param name="engine">
    /// Eine eigene Engine nur für diesen Zweck (mit leerer Lernliste): Die echte wird währenddessen schon benutzt,
    /// und ihre Lernliste verträgt keinen gleichzeitigen Zugriff. Wortliste und Sprachmodell ändern sich nicht mehr
    /// und dürfen geteilt werden.
    /// </param>
    static void WarmUp(string language, SuggestionEngine engine)
    {
        long started = Perf.Now;
        try
        {
            var context = new WordContext("und", "ich");
            new MultilingualEngine(language, engine).Suggest("hal", context);

            // Der Verbund mehrerer Sprachen hat eigenen Code: dieselbe Engine doppelt, nur für diesen Zweck
            var warm = new MultilingualEngine([(language, engine), (language, engine)]);
            warm.Suggest("h", WordContext.None);
            warm.Suggest("hallp", context);
            warm.Suggest("esist", WordContext.FromText("Das ist ein ")); // fehlendes Leerzeichen
            warm.Predict(context);
            warm.ContinuePhrase("mit");

            // Kurze Wischspur von q nach p auf einem gedachten QWERTY-Feld
            var keys = new Dictionary<char, Vector2>();
            string[] rows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
            for (int r = 0; r < rows.Length; r++)
            {
                for (int i = 0; i < rows[r].Length; i++) keys[rows[r][i]] = new Vector2(i + 0.5f * (r + 1), r * 1.45f);
            }

            Vector2 from = keys['q'], to = keys['p'];
            warm.Glide([.. Enumerable.Range(0, 12).Select(i => Vector2.Lerp(from, to, i / 11f))], keys, context);
        }
        catch (Exception e)
        {
            // Das Aufwärmen ist nur eine Beschleunigung: Ein Fehler hier darf nichts weiter stören.
            Android.Util.Log.Warn("zBoard", $"Aufwärmen für {language} abgebrochen: {e.Message}");
        }

        Perf.Log($"warm-up-{language}", Perf.Since(started));
    }

    /// <summary>Die Engine einer Sprache; beim ersten Mal beginnt das Laden im Hintergrund.</summary>
    Task<SuggestionEngine> EngineTask(string language) =>
        Engines.GetOrAdd(language, lang => Task.Run(() => LoadEngine(lang)));

    SuggestionEngine LoadEngine(string language)
    {
        long started = Perf.Now;

        // Sprachmodell und Wortliste gleichzeitig laden
        var modelTask = Task.Run(() => LoadModel(language));
        var dictionary = LoadDictionary(language);
        var model = modelTask.GetAwaiter().GetResult();

        var history = new UserHistory();
        var learningFile = LearningFile(this, language);
        if (File.Exists(learningFile))
        {
            using var stream = File.OpenRead(learningFile);
            history = UserHistory.Load(stream);
        }

        var proximity = KeyProximity.FromLayout(Layouts.Alphabet(language, showLanguageKey: false));
        Perf.Log($"load-{language}", Perf.Since(started));
        Perf.Log($"ready-{language}-since-process-start", Perf.SinceProcessStart);
        Task.Run(() => WarmUp(language, new SuggestionEngine(dictionary, proximity, model))); // hält das Laden nicht auf
        return new SuggestionEngine(dictionary, proximity, model, history);
    }

    NgramModel LoadModel(string language)
    {
        long started = Perf.Now;
        try
        {
            using var stream = Assets!.Open($"dict/{language}.ngrams.bin");
            var model = NgramModel.Load(stream);
            Perf.Log($"load-{language}-model", Perf.Since(started));
            return model;
        }
        catch (Java.IO.FileNotFoundException)
        {
            // Sprache ohne Sprachmodell: Vorhersage nur aus Gelerntem
            return NgramModel.Empty;
        }
    }

    /// <summary>
    /// Lädt die Wortliste einer Sprache. Das Auswerten der mitgelieferten Textdatei (Normieren, Sortieren) dauert auf
    /// einem Telefon spürbar; deshalb legt zBoard nach dem ersten Laden eine fertig vorbereitete Fassung im
    /// Zwischenspeicher der App ab. Jedes weitere Neuladen der Tastatur liest diese um ein Vielfaches schneller.
    /// </summary>
    WordDictionary LoadDictionary(string language)
    {
        long started = Perf.Now;
        string? cache = DictionaryCacheFile(language);
        if (cache != null && File.Exists(cache))
        {
            try
            {
                using var stream = File.OpenRead(cache);
                var compiled = WordDictionary.LoadCompiled(stream, language);
                Perf.Log($"load-{language}-dictionary-cached", Perf.Since(started));
                return compiled;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Android.Util.Log.Warn("zBoard", $"Zwischenspeicher für {language} unbrauchbar, lade die Wortliste: {e.Message}");
            }
        }

        WordDictionary dictionary;
        using (var stream = Assets!.Open($"dict/{language}.txt"))
        {
            // Gemeinsamer Maßstab, damit sich die Sprachen bei mehrsprachiger Eingabe vergleichen lassen
            dictionary = WordDictionary.Load(stream, WordDictionary.ReferenceTotal, language);
        }

        Perf.Log($"load-{language}-dictionary", Perf.Since(started));
        if (cache != null) Task.Run(() => SaveDictionaryCache(dictionary, cache, language)); // hält das Laden nicht auf
        return dictionary;
    }

    static string? s_dictionaryCacheStamp;

    /// <summary>
    /// Datei der vorbereiteten Wortliste. Der Name enthält den Zeitpunkt der Installation: Nach einem Update (neue
    /// Wortlisten, anderer Maßstab) gilt der alte Zwischenspeicher nicht mehr. null: kein Zwischenspeicher möglich.
    /// </summary>
    string? DictionaryCacheFile(string language)
    {
        try
        {
            if (s_dictionaryCacheStamp == null)
            {
                var installed = File.GetLastWriteTimeUtc(ApplicationInfo!.SourceDir!);
                if (installed.Year < 2000) return null; // Zeitpunkt unbekannt: lieber ohne Zwischenspeicher
                s_dictionaryCacheStamp = installed.Ticks.ToString("x");
            }

            return Path.Combine(CacheDir!.AbsolutePath, "dict", $"{language}.{s_dictionaryCacheStamp}.zbwd");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NullReferenceException)
        {
            return null;
        }
    }

    static void SaveDictionaryCache(WordDictionary dictionary, string file, string language)
    {
        try
        {
            string dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);

            // Erst fertig schreiben, dann umbenennen: Eine halb geschriebene Datei wird nie gelesen.
            string temp = $"{file}.{System.Environment.CurrentManagedThreadId}.tmp";
            using (var stream = File.Create(temp)) dictionary.Save(stream);
            File.Move(temp, file, overwrite: true);

            foreach (var old in Directory.GetFiles(dir, $"{language}.*"))
            {
                if (old != file) File.Delete(old); // Fassungen früherer Versionen
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Ohne Zwischenspeicher lädt die Tastatur beim nächsten Mal wieder aus der Wortliste.
            Android.Util.Log.Warn("zBoard", $"Zwischenspeicher für {language} nicht geschrieben: {e.Message}");
        }
    }

    public void OnLanguageSelected(string language)
    {
        if (language == _language) return;
        CommitComposing(autocorrect: false, separator: string.Empty);
        SetLanguage(language);
        UpdateAutoShift();
        UpdateSuggestions();
    }

    void SwitchLanguage()
    {
        CommitComposing(autocorrect: false, separator: string.Empty);
        var languages = _settings.Languages;
        int index = languages.ToList().IndexOf(_language);
        SetLanguage(languages[(index + 1) % languages.Count]);
        UpdateAutoShift();
    }

    void ApplyLayout()
    {
        if (_keyboardView == null) return;
        var languages = _settings.Languages;
        bool multipleLanguages = _settings.ShowLanguageKey && languages.Count > 1;
        _keyboardView.Languages = [.. languages.Select(l => (l, Layouts.LanguageNames.TryGetValue(l, out var n) ? n : l))];
        bool numberRow = _settings.NumberRow;
        var layout = _symbolsPage switch
        {
            0 => Layouts.Alphabet(_language, multipleLanguages, numberRow, _settings.UmlautKeys),
            NumpadPage => Layouts.Numpad(_profile.IsPhone ? NumpadKind.Phone : _profile.IsDatetime ? NumpadKind.Datetime : NumpadKind.Number),
            _ => Layouts.Symbols(_symbolsPage, multipleLanguages),
        };

        _keyboardView.ShowDigitHints = !numberRow;
        _keyboardView.SetLayout(layout);
        var inputLanguages = InputLanguages();
        _keyboardView.CurrentLanguage = _language;
        _keyboardView.SpaceLabel = inputLanguages.Count > 1
            ? MultilingualEngine.SpaceLabel(inputLanguages) // wie Gboard: "DE · EN"
            : Layouts.LanguageNames.TryGetValue(_language, out var name) ? name : _language;
        _keyboardView.Shift = _symbolsPage == 0 ? _shift : ShiftState.Off;
    }

    ImageBackgroundDrawable? _imageBackground;

    void ApplyTheme()
    {
        bool systemDark = (Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;
        var system = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? KeyboardTheme.FromSystem(this, systemDark) // wie Gboard: ab Android 12 Material You passend zum Hintergrundbild
            : systemDark ? KeyboardTheme.Dark : KeyboardTheme.Light;
        var image = _settings.Theme == ThemeMode.Image ? ThemeImage.Load(this) : null;
        float dim = _settings.ThemeImageDim / 100f;
        var theme = image != null
            ? KeyboardTheme.FromImage(ThemeImage.Dimmed(image.Average, dim), system.Accent)
            : _settings.Theme switch
            {
                ThemeMode.Light => KeyboardTheme.Light,
                ThemeMode.Dark => KeyboardTheme.Dark,
                _ => system,
            };

        _keyboardView?.Theme = theme;
        _keyboardView?.KeyBorders = _settings.KeyBorders;
        _suggestionBar?.Theme = theme;
        ApplyPanelTheme(theme);

        // Das Bild liegt unter Leiste und Tasten (auch hinter der Navigationsleiste)
        if (image == null) _imageBackground = null;
        else if (_imageBackground == null || _imageBackground.Bitmap != image.Bitmap || _imageBackground.Dim != dim)
        {
            _imageBackground = new ImageBackgroundDrawable(image.Bitmap, dim);
        }

        if (_imageBackground != null) _inputRoot?.Background = _imageBackground;
#if DEBUG || ZBOARD_TESTHOOKS
        Android.Util.Log.Info("zBoardTest", $"THEME image={image != null} dark={ThemeImage.IsDark(theme.BarColor)} borders={_settings.KeyBorders}");
#endif
    }

    // ---------- Hilfen ----------

    string TextBeforeCursor(int length) =>
        Ic?.GetTextBeforeCursorFormatted(length, 0)?.ToString() ?? string.Empty;

    static Java.Lang.String J(string text) => new(text);
}
