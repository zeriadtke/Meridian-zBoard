using Android.OS;
using Android.Views;
using ZBoard.Keyboard;
using ZBoard.Localization;
using ZBoard.Plugins;
using ZBoard.Suggestions;

namespace ZBoard.Ime;

/// <summary>
/// Handschrift wie bei Gboard, erkannt von einem Handschrift-Plugin (zBoard Pen). Der Stift in der Werkzeugleiste
/// öffnet die Schreibfläche. Nach jedem Strich fragt zBoard das Plugin; das beste Ergebnis steht unterstrichen im Feld,
/// die übrigen in der Leiste. Schreibt man nach einer Pause weiter, wird das bisherige Wort übernommen (mit
/// Leerzeichen, außer im Chinesischen); Leertaste, Satzzeichen und Enter übernehmen es ebenso.
/// </summary>
public sealed partial class ZBoardImeService : IHandwritingListener
{
    const int RecognizeDelayMs = 250;
    const int NewWordPauseMs = 900;
    const int RetryWhileLoadingMs = 2000;

    HandwritingPanelView? _handwritingPanel;
    PluginInfo? _inkPlugin;
    string? _inkText;                      // erkannter Text, als Wort in Bearbeitung im Feld
    IReadOnlyList<string> _inkCandidates = [];
    int _inkGeneration;                    // ändert sich mit jedem neuen Strich: späte Antworten verwerfen
    long _lastStrokeEnd;
    Handler? _inkHandler;
    Java.Lang.Runnable? _inkRecognize;

    bool HandwritingOpen => _handwritingPanel?.Visibility == ViewStates.Visible;

    void ShowHandwritingPanel()
    {
        if (_handwritingPanel == null || _keyboardView == null || _inkPlugin == null) return;
        if (_composing.Length > 0) CommitComposing(autocorrect: false, separator: string.Empty);
        if (_emojiSearchActive) ExitEmojiSearch(backToPanel: false);

        ResetInk();
        _handwritingPanel.PanelHeight = PanelHeight();
        _handwritingPanel.Haptics = _settings.Haptics;
        _handwritingPanel.EnterLabel = _profile.EnterLabel;
        _handwritingPanel.Hint = Layouts.LanguageNames.TryGetValue(_language, out var name)
            ? L.T($"Hier schreiben ({name})", $"Write here ({name})")
            : string.Empty;
        _handwritingPanel.RequestLayout();
        _keyboardView.Visibility = ViewStates.Gone;
        _emojiPanel?.Visibility = ViewStates.Gone;
        _clipboardPanel?.Visibility = ViewStates.Gone;
        _textEditPanel?.Visibility = ViewStates.Gone;
        _handwritingPanel.Visibility = ViewStates.Visible;
        _toolbarOpen = false;
        ShowToolbar();
    }

    /// <summary>Striche, Ergebnis und offene Anfragen verwerfen (das Feld bleibt, wie es ist).</summary>
    void ResetInk()
    {
        _inkGeneration++;
        if (_inkRecognize != null) _inkHandler?.RemoveCallbacks(_inkRecognize);
        _inkText = null;
        _inkCandidates = [];
        _handwritingPanel?.ClearInk();
    }

    public void OnStrokeStarted(long time)
    {
        MarkEdit();
        _inkGeneration++; // eine laufende Erkennung kennt diesen Strich noch nicht
        if (_inkRecognize != null) _inkHandler?.RemoveCallbacks(_inkRecognize);

        // Nach einer Pause beginnt ein neues Wort: das erkannte übernehmen, die alten Striche verschwinden
        if (_inkText != null && time - _lastStrokeEnd > NewWordPauseMs)
        {
            CommitInk(IsChinese ? string.Empty : " ", keepCurrentStroke: true);
        }
    }

    public void OnStrokeFinished()
    {
        _lastStrokeEnd = SystemClock.UptimeMillis();
        ScheduleRecognition(RecognizeDelayMs);
    }

    void ScheduleRecognition(int delayMs)
    {
        _inkHandler ??= new Handler(Looper.MainLooper!);
        _inkRecognize ??= new Java.Lang.Runnable(() => _ = RecognizeInk());
        _inkHandler.RemoveCallbacks(_inkRecognize);
        _inkHandler.PostDelayed(_inkRecognize, delayMs);
    }

    async Task RecognizeInk()
    {
        var panel = _handwritingPanel;
        var plugin = _inkPlugin;
        if (panel == null || plugin == null || !HandwritingOpen || !panel.HasInk) return;

        int generation = ++_inkGeneration;
        var strokes = panel.Strokes;
        InkResult result;
        try
        {
            _pluginClient ??= new PluginClient(this);
            result = await _pluginClient.RecognizeInkAsync(plugin, strokes, panel.WriteWidth, panel.WriteHeight, _language, InkPreContext());
        }
        catch (Exception e) when (e is System.OperationCanceledException or Java.Lang.Exception)
        {
            result = new InkResult([], null, false);
        }

        if (generation != _inkGeneration || !HandwritingOpen) return; // inzwischen weitergeschrieben oder geschlossen

        if (result.Candidates.Count == 0)
        {
            string message = result.Error ?? L.T($"{plugin.Label} hat nicht geantwortet.", $"{plugin.Label} did not respond.");
            _suggestionBar?.SetStatus(message, Resource.Drawable.ms_stylus_note);
            if (result.Loading) ScheduleRecognition(RetryWhileLoadingMs); // Modell wird geladen: von selbst erneut
            return;
        }

        SetInkText(result.Candidates[0]);
        _inkCandidates = result.Candidates;
        _suggestionBar?.SetCandidates(_inkCandidates);
    }

    /// <summary>Text vor dem Cursor ohne das gerade erkannte Wort (hilft dem Plugin, z. B. bei Großschreibung).</summary>
    string InkPreContext()
    {
        int length = 20 + (_inkText?.Length ?? 0);
        string before = TextBeforeCursor(length);
        if (_inkText != null && before.EndsWith(_inkText, StringComparison.Ordinal)) before = before[..^_inkText.Length];
        return before.Length > 20 ? before[^20..] : before;
    }

    void SetInkText(string text)
    {
        var ic = Ic;
        if (ic == null) return;
        MarkEdit();
        _inkText = text;
        ic.SetComposingText(J(text), 1);
    }

    /// <summary>Erkanntes Wort übernehmen und <paramref name="separator"/> anhängen; die Striche verschwinden.</summary>
    void CommitInk(string separator, bool keepCurrentStroke = false)
    {
        var ic = Ic;
        if (ic == null) return;
        MarkEdit();
        if (_inkText != null) ic.CommitText(J(_inkText + separator), 1);
        else if (separator.Length > 0) ic.CommitText(J(separator), 1);

        _inkText = null;
        _inkCandidates = [];
        _handwritingPanel?.ClearInk(keepCurrentStroke);
        _lastCorrection = null;
        _lastSpaceCommit = null;
        _autoSpaceInserted = false;
        ShowToolbar();
    }

    /// <summary>Anderen Vorschlag aus der Leiste gewählt: ersetzt das erkannte Wort und übernimmt es.</summary>
    void OnInkCandidatePicked(int index)
    {
        if (index < 0 || index >= _inkCandidates.Count) return;
        _inkGeneration++;
        SetInkText(_inkCandidates[index]);
        CommitInk(IsChinese ? string.Empty : " ");
    }

    public void OnHandwritingKey(HandwritingKey key)
    {
        var ic = Ic;
        if (ic == null) return;
        _inkGeneration++;
        if (_inkRecognize != null) _inkHandler?.RemoveCallbacks(_inkRecognize);

        switch (key)
        {
            case HandwritingKey.Space:
                CommitInk(" ");
                break;
            case HandwritingKey.Comma:
                CommitInk(IsChinese ? "，" : ",");
                break;
            case HandwritingKey.Period:
                CommitInk(IsChinese ? "。" : ".");
                break;
            case HandwritingKey.Enter:
                CommitInk(string.Empty);
                HandleEnter();
                break;
            case HandwritingKey.Delete:
                if (_inkText != null || _handwritingPanel?.HasInk == true)
                {
                    // Wie bei Gboard: Löschen verwirft das gerade geschriebene Wort samt Strichen
                    MarkEdit();
                    if (_inkText != null)
                    {
                        ic.SetComposingText(J(string.Empty), 1);
                        ic.FinishComposingText();
                    }

                    ResetInk();
                    ShowToolbar();
                }
                else
                {
                    HandleDelete();
                }

                break;
            case HandwritingKey.Close:
                CommitInk(string.Empty);
                ShowKeyboardPanel();
                UpdateAutoShift();
                UpdateSuggestions();
                break;
        }
    }

    /// <summary>Leiste während der Handschrift: erkannte Wörter, sonst die Werkzeuge.</summary>
    void ShowHandwritingBar()
    {
        if (_inkCandidates.Count > 0) _suggestionBar?.SetCandidates(_inkCandidates);
        else ShowToolbar();
    }

    /// <summary>Eintrag der durchgehenden Vorschlagszeile: bei Handschrift deren Wörter, sonst Pinyin.</summary>
    void OnCandidatePicked(int index)
    {
        if (HandwritingOpen) OnInkCandidatePicked(index);
        else OnPinyinCandidatePicked(index);
    }
}
