using Android.Views.InputMethods;
using Android.Widget;
using ZBoard.Localization;
using ZBoard.Plugins;
using ZBoard.Suggestions;

namespace ZBoard.Ime;

/// <summary>
/// Plugins: eigene Apps (z. B. „zBoard DeepL“), die zBoard um Funktionen mit Internet erweitern, ohne dass zBoard
/// selbst Internet braucht. Das Puzzle-Symbol in der Werkzeugleiste fragt die freigeschalteten Plugins nach ihren
/// Aktionen; die gewählte Aktion bekommt den markierten Text (sonst den Inhalt des Felds) und ersetzt ihn.
/// Text verlässt zBoard nur auf diesen ausdrücklichen Fingertipp hin.
/// </summary>
public sealed partial class ZBoardImeService
{
    /// <summary>Werkzeugleiste mit Puzzle-Symbol, sobald mindestens ein Plugin freigeschaltet ist.</summary>
    static readonly IReadOnlyList<ToolbarItem> ToolbarWithPlugins =
    [
        new("clipboard", Resource.Drawable.ms_content_paste),
        new("onehanded", Resource.Drawable.ms_back_hand),
        new("plugins", Resource.Drawable.ms_extension),
        new("settings", Resource.Drawable.ms_settings),
        new("voice", Resource.Drawable.ms_mic, Trailing: true),
    ];

    sealed record PluginUndo(int Start, string Result, string Original);

    PluginClient? _pluginClient;
    IReadOnlyList<PluginInfo> _enabledPlugins = [];
    List<PluginAction> _pluginChoices = [];
    bool _choicesAreUndo;
    PluginUndo? _pluginUndo;
    CancellationTokenSource? _pluginCancel;
    int _pluginSession;           // ändert sich bei jedem Öffnen/Schließen: späte Antworten verwerfen

    bool PluginBusy => _pluginCancel != null;

    void StartPlugins()
    {
        _pluginSession++;
        CancelPlugin();
        _pluginUndo = null;
        _enabledPlugins = _profile.IsPassword ? [] : [.. PluginRegistry.Enabled(this, _settings).Where(p => !p.CanSync)];
        _suggestionBar?.ToolbarItems = _enabledPlugins.Count > 0 ? ToolbarWithPlugins : Toolbar;
    }

    void FinishPlugins()
    {
        _pluginSession++;
        CancelPlugin();
        _pluginUndo = null;
    }

    void CancelPlugin()
    {
        _pluginCancel?.Cancel();
        _pluginCancel = null;
    }

    /// <summary>Werkzeugleiste: Puzzle-Symbol angetippt.</summary>
    async void ShowPluginActions()
    {
        if (PluginBusy || _suggestionBar == null || _enabledPlugins.Count == 0) return;
        CommitComposing(autocorrect: false, separator: string.Empty);
        int session = _pluginSession;
        _pluginCancel = new CancellationTokenSource();
        var cancel = _pluginCancel.Token;
        _suggestionBar.SetStatus(L.T("Plugins werden gefragt …", "Asking plugins …"), Resource.Drawable.ms_extension);

        var actions = new List<PluginAction>();
        try
        {
            _pluginClient ??= new PluginClient(this);
            foreach (var plugin in _enabledPlugins)
            {
                actions.AddRange(await _pluginClient.ListActionsAsync(plugin, cancel));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or Java.Lang.Exception)
        {
            actions.Clear();
        }

        if (session != _pluginSession || cancel.IsCancellationRequested) return;
        _pluginCancel = null;

        if (actions.Count == 0)
        {
            Toast.MakeText(this, L.T("Kein Plugin hat geantwortet.", "No plugin responded."), ToastLength.Short)!.Show();
            UpdateSuggestions();
            return;
        }

        _pluginChoices = actions;
        _choicesAreUndo = false;
        _suggestionBar.SetChoices([.. actions.Select(a => a.Label)]);
    }

    /// <summary>Eintrag der Auswahlleiste angetippt (Plugin-Aktion oder Rückgängig).</summary>
    void OnChoicePicked(int index)
    {
        if (_choicesAreUndo)
        {
            UndoPlugin();
        }
        else if (index >= 0 && index < _pluginChoices.Count)
        {
            RunPlugin(_pluginChoices[index]);
        }
    }

    void CloseChoices()
    {
        _pluginChoices = [];
        _choicesAreUndo = false;
        _pluginUndo = null;
        UpdateSuggestions();
    }

    /// <summary>Markierter Text samt Anfang, sonst der ganze Feldinhalt (ab Offset 0).</summary>
    (int Start, string Text)? PluginInput()
    {
        var ic = Ic;
        if (ic == null) return null;
        string? selected = ic.GetSelectedTextFormatted(0)?.ToString();
        if (!string.IsNullOrEmpty(selected)) return (Math.Min(_selStart, _selEnd), selected);

        var extracted = ic.GetExtractedText(new ExtractedTextRequest { HintMaxChars = PluginContract.MaxTextLength + 1 }, 0);
        string? text = extracted?.Text?.ToString();
        if (extracted == null || text == null) return null;
        return (extracted.StartOffset, text);
    }

    /// <summary>Steht <paramref name="expected"/> noch an <paramref name="start"/> im Feld?</summary>
    bool FieldHas(int start, string expected)
    {
        var extracted = Ic?.GetExtractedText(new ExtractedTextRequest { HintMaxChars = PluginContract.MaxTextLength * 2 }, 0);
        string? text = extracted?.Text?.ToString();
        if (extracted == null || text == null) return false;
        int offset = start - extracted.StartOffset;
        return offset >= 0 && offset + expected.Length <= text.Length
            && string.CompareOrdinal(text, offset, expected, 0, expected.Length) == 0;
    }

    void ReplaceRange(int start, int length, string replacement)
    {
        var ic = Ic;
        if (ic == null) return;
        MarkEdit();
        ic.BeginBatchEdit();
        ic.SetComposingRegion(start, start + length);
        ic.CommitText(J(replacement), 1);
        ic.EndBatchEdit();
        _lastCorrection = null;
        _autoSpaceInserted = false;
    }

    async void RunPlugin(PluginAction action)
    {
        if (PluginBusy || _suggestionBar == null || _profile.IsPassword) return;
        if (PluginInput() is not { } input || string.IsNullOrWhiteSpace(input.Text))
        {
            Toast.MakeText(this, L.T("Kein Text im Feld.", "No text in this field."), ToastLength.Short)!.Show();
            return;
        }

        if (input.Text.Length > PluginContract.MaxTextLength)
        {
            Toast.MakeText(this, L.T("Der Text ist zu lang – markiere einen Teil.", "The text is too long – select a part."), ToastLength.Short)!.Show();
            return;
        }

        int session = _pluginSession;
        _pluginCancel = new CancellationTokenSource();
        var cancel = _pluginCancel.Token;
        _suggestionBar.SetStatus(action.Label + " … " + L.T("(antippen zum Abbrechen)", "(tap to cancel)"), Resource.Drawable.ms_extension);

        PluginResult result;
        try
        {
            _pluginClient ??= new PluginClient(this);
            result = await _pluginClient.RunAsync(action, input.Text, _language, cancel);
        }
        catch (Exception e) when (e is OperationCanceledException or Java.Lang.Exception)
        {
            result = new PluginResult(null, null);
        }

        if (session != _pluginSession || cancel.IsCancellationRequested) return;
        _pluginCancel = null;

        string? message = result.Error;
        if (message == null && result.Text == null) message = L.T($"{action.Plugin.Label} hat nicht geantwortet.", $"{action.Plugin.Label} did not respond.");
        else if (message == null && !FieldHas(input.Start, input.Text)) message = L.T("Der Text wurde inzwischen geändert und bleibt, wie er ist.", "The text changed in the meantime and was left as is.");

        if (message != null || result.Text == null)
        {
            Toast.MakeText(this, message ?? string.Empty, ToastLength.Long)!.Show();
            UpdateSuggestions();
            return;
        }

        ReplaceRange(input.Start, input.Text.Length, result.Text);
        _pluginUndo = new PluginUndo(input.Start, result.Text, input.Text);
        _pluginChoices = [];
        _choicesAreUndo = true;
        _suggestionBar.SetChoices([L.T("↶ Rückgängig", "↶ Undo")]);
    }

    void UndoPlugin()
    {
        var undo = _pluginUndo;
        _pluginUndo = null;
        _choicesAreUndo = false;
        if (undo != null && FieldHas(undo.Start, undo.Result)) ReplaceRange(undo.Start, undo.Result.Length, undo.Original);
        UpdateSuggestions();
    }
}
