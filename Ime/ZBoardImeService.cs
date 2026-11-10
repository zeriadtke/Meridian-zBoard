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
    Label = "zHeal Meridian Tastatur", // Meridian: Name in den Android-Einstellungen
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

    readonly StringBuilder _composing = new();

    ZBoardSettings _settings = null!;

    KeyboardView? _keyboardView;
    SuggestionBarView? _suggestionBar;
    EditorProfile _profile = EditorProfile.From(null);

    string _language = "de";
    Task<SuggestionEngine>? _engineTask;
    ShiftState _shift;
    bool _shiftManual;
    long _lastShiftTapMs;
    int _symbolsPage;           // 0 = Buchstaben, 1/2 = Symbolseiten, 3 = Ziffernfeld (Zahlen-, PIN-, Telefonfelder)
    const int NumpadPage = 3;
    long _lastSpaceMs;
    bool _autoSpaceInserted;    // Leerzeichen kam von einem Vorschlag
    Correction? _lastCorrection;
    SuggestionResult _currentSuggestions = SuggestionResult.Empty;

    bool _glideActive;                          // das Wort in Bearbeitung kam aus der Wischeingabe
    IReadOnlyList<string> _glideCandidates = [];
    int _selStart;
    int _selEnd;
    int _swipeAnchor = -1;                      // Cursor zu Beginn des Wort-Löschens per Wischen
    string _swipeText = string.Empty;
    int _swipeChars;
    Android.Media.AudioManager? _audio;

    SuggestionEngine? Engine => _engineTask is { IsCompletedSuccessfully: true } t ? t.Result : null;

    IInputConnection? Ic => CurrentInputConnection;

    // ---------- Lebenszyklus ----------

    public override void OnCreate()
    {
        base.OnCreate();
        _settings = new ZBoardSettings(this);

        _audio = GetSystemService(AudioService) as Android.Media.AudioManager;
        InitClipboard();
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
    }

    public override View? OnCreateInputView() => BuildInputView();

    public override void OnStartInputView(EditorInfo? info, bool restarting)
    {
        base.OnStartInputView(info, restarting);
        MeridianStartInput(info); // Meridian: neue Messsitzung je Eingabefeld

        _profile = EditorProfile.From(info);
        _composing.Clear();
        _lastCorrection = null;
        _autoSpaceInserted = false;
        _glideActive = false;
        _swipeAnchor = -1;
        _reselect = null;
        _selStart = info?.InitialSelStart ?? 0;
        _selEnd = info?.InitialSelEnd ?? 0;
        _shiftManual = false;
        _shift = ShiftState.Off;
        // Wie Gboard: Zahlen-, PIN-, Telefon- und Datumsfelder bekommen ein Ziffernfeld statt Buchstaben
        _symbolsPage = _profile.IsNumeric ? NumpadPage : 0;

        StartPanels();
        RefreshSystemBarPadding();
        UpdatePopupSpace(); // Platz für Popups über der Tastatur (falls Android das Fenster neu aufgebaut hat)
        SetLanguage(_settings.CurrentLanguage);
        ApplyTheme();
        _keyboardView?.EnterLabel = _profile.EnterLabel;
        _keyboardView?.GlideEnabled = _settings.GlideTyping && _profile.AllowSuggestions;
        _keyboardView?.ShowKeyPreview = _settings.KeyPreview;
        StartEmailInput();
        StartPlugins();
        if (_suggestionBar != null)
        {
            _suggestionBar.Visibility = ViewStates.Visible;
            ShowToolbar();
            if (_profile.IsEmail) UpdateSuggestions(); // wie Gboard: gemerkte Adressen schon im leeren Feld
        }

        UpdateAutoShift();

    }

    public override void OnFinishInputView(bool finishingInput)
    {
        MeridianFinishInput(); // Meridian: Messsitzung abschließen und übergeben
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
            Ic?.FinishComposingText();
            UpdateSuggestions();
        }

        if (newSelStart != oldSelStart && _composing.Length == 0) _lastCorrection = null;
        if (_composing.Length == 0) UpdateAutoShift();

        // Wie Gboard: Antippen eines Worts im Text zeigt dessen Vorschläge erneut
        if (_composing.Length == 0 && (newSelStart != oldSelStart || newSelEnd != oldSelEnd)) OnCursorPlaced(newSelStart, newSelEnd);
    }

    // ---------- Tasten ----------

    public void OnKeyDown(KeyDef key, long uptimeMs)
    {
        MeridianKeyDown(uptimeMs); // Meridian: nur der Zeitpunkt
        if (_settings.Haptics) _keyboardView?.PerformKeyHaptic();
        if (_settings.KeySound) PlayKeySound(key.Action);
    }

    public void OnKeyUp(KeyDef key, string? output, long uptimeMs, bool gesture)
    {
        MeridianKeyUp(key, output, uptimeMs, gesture); // Meridian: nur die Tastenart, nie das Zeichen
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
            _composing.Append(text);
            ic.SetComposingText(J(_composing.ToString()), 1);
            UpdateSuggestions();
        }
        else if (!wordChar && text.Length == 1 && AutoPunctuation.Contains(text[0]))
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

        // Löschen direkt nach einer Autokorrektur stellt das ursprüngliche Wort wieder her.
        if (_lastCorrection is { } c && _composing.Length == 0
            && TextBeforeCursor(c.Corrected.Length + c.Separator.Length) == c.Corrected + c.Separator)
        {
            ic.DeleteSurroundingText(c.Corrected.Length + c.Separator.Length, 0);
            ic.CommitText(J(c.Original), 1);
            Engine?.SuppressCorrection(c.Original);
            Learn(null, c.Original, accepted: true);
            _lastCorrection = null;
            return;
        }

        _lastCorrection = null;

        if (_composing.Length > 0)
        {
            int remove = _composing.Length >= 2 && char.IsSurrogatePair(_composing[^2], _composing[^1]) ? 2 : 1;
            _composing.Length -= remove;
            ic.SetComposingText(J(_composing.ToString()), 1);
            if (_composing.Length == 0) ic.FinishComposingText();
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

        string typed = _composing.ToString();
        string final = typed;

        if (autocorrect && _settings.AutoCorrect && _profile.AllowAutocorrect)
        {
            var result = Engine?.Suggest(typed);
            if (result?.AutoCorrection is { } corrected && corrected != typed)
            {
                final = corrected;
                _lastCorrection = new Correction(typed, corrected, separator);
                MeridianAutoCorrected(); // Meridian: nur das Ereignis
            }
        }

        string? previous = PreviousWord();
        ic.CommitText(J(final + separator), 1);
        _composing.Clear();
        _glideActive = false;
        _currentSuggestions = SuggestionResult.Empty;
        Learn(previous, final);
    }

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

        MarkEdit();

        if (slot.IsLiteral) Engine?.SuppressCorrection(slot.Word);
        MeridianSuggestionPicked(); // Meridian: nur das Ereignis

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
        if (_settings.LearnWords && _profile.AllowLearning) Engine?.History.Learn(previous, word, accepted);
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

    // ---------- Wischeingabe ----------

    public void OnGlide(IReadOnlyList<Vector2> path, IReadOnlyDictionary<char, Vector2> keyCenters)
    {
        var ic = Ic;
        var engine = Engine;
        if (ic == null || engine == null) return;
        MarkEdit();

        if (_composing.Length > 0) CommitComposing(autocorrect: !_glideActive, separator: string.Empty);

        var words = engine.Glide(path, keyCenters, PreviousWord(adjacent: true));
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
        ShiftState.Locked => word.ToUpperInvariant(),
        ShiftState.OneShot => char.ToUpperInvariant(word[0]) + word[1..],
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
        if (!_glideActive && TryShowEmailSuggestions()) return;
        if (_reselect != null && _composing.Length == 0 && ShowReselect()) return;
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
        var result = engine.Suggest(typed, PreviousWord());
        _currentSuggestions = result;
        bool autocorrectActive = _settings.AutoCorrect && _profile.AllowAutocorrect;

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
    SuggestionSlot? PhraseSlot(SuggestionEngine engine, string word)
    {
        if (!_settings.NextWordPrediction || !_settings.LearnWords || !_profile.AllowLearning) return null;
        var continuation = engine.ContinuePhrase(word, maxWords: 2);
        if (continuation == null) return null;
        string phrase = word + " " + continuation;
        return new SuggestionSlot(phrase, phrase, false, false, IsPhrase: true);
    }

    /// <summary>Nächstes-Wort-Vorhersage nach einem Leerzeichen.</summary>
    void ShowPredictions(SuggestionEngine engine)
    {
        string? previous = _settings.NextWordPrediction ? PreviousWord() : null;
        IReadOnlyList<string> predictions = previous == null ? [] : engine.Predict(previous);
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

        string before = TextBeforeCursor(3);
        bool capitalize = before.Length == 0 || before.EndsWith('\n');
        if (!capitalize && before.Length >= 2 && char.IsWhiteSpace(before[^1]))
        {
            string trimmed = before.TrimEnd();
            capitalize = trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?';
        }

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
        _settings.CurrentLanguage = language;
        _engineTask = Engines.GetOrAdd(language, lang => Task.Run(() => LoadEngine(lang)));
        _engineTask.ContinueWith(
            _ => new Handler(Looper.MainLooper!).Post(UpdateSuggestions),
            TaskContinuationOptions.OnlyOnRanToCompletion);
        ApplyLayout();
    }

    SuggestionEngine LoadEngine(string language)
    {
        WordDictionary dictionary;
        using (var stream = Assets!.Open($"dict/{language}.txt"))
        {
            dictionary = WordDictionary.Load(stream);
        }

        var bigrams = BigramModel.Empty;
        try
        {
            using var stream = Assets!.Open($"dict/{language}.bigrams.txt");
            bigrams = BigramModel.Load(stream);
        }
        catch (Java.IO.FileNotFoundException)
        {
            // Sprache ohne Wortpaare: Vorhersage nur aus Gelerntem
        }

        var history = new UserHistory();
        var learningFile = LearningFile(this, language);
        if (File.Exists(learningFile))
        {
            using var stream = File.OpenRead(learningFile);
            history = UserHistory.Load(stream);
        }

        var proximity = KeyProximity.FromLayout(Layouts.Alphabet(language, showLanguageKey: false));
        return new SuggestionEngine(dictionary, proximity, bigrams, history);
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
        _keyboardView.SpaceLabel = Layouts.LanguageNames.TryGetValue(_language, out var name) ? name : _language;
        _keyboardView.Shift = _symbolsPage == 0 ? _shift : ShiftState.Off;
    }

    void ApplyTheme()
    {
        bool systemDark = (Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;
        var theme = _settings.Theme switch
        {
            ThemeMode.Light => KeyboardTheme.Light,
            ThemeMode.Dark => KeyboardTheme.Dark,
            // Wie Gboard: ab Android 12 standardmäßig Material-You-Farben passend zum Hintergrundbild
            _ when OperatingSystem.IsAndroidVersionAtLeast(31) => KeyboardTheme.FromSystem(this, systemDark),
            _ => systemDark ? KeyboardTheme.Dark : KeyboardTheme.Light,
        };
        _keyboardView?.Theme = theme;
        _suggestionBar?.Theme = theme;
        ApplyPanelTheme(theme);
    }

    // ---------- Hilfen ----------

    string TextBeforeCursor(int length) =>
        Ic?.GetTextBeforeCursorFormatted(length, 0)?.ToString() ?? string.Empty;

    static Java.Lang.String J(string text) => new(text);
}
