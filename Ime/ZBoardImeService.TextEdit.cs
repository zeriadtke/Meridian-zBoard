using Android.OS;
using Android.Views;
using ZBoard.Keyboard;
using Keycode = Android.Views.Keycode;

namespace ZBoard.Ime;

/// <summary>
/// Textbearbeitung (Werkzeugleiste → Cursor-Symbol): Pfeile bewegen den Cursor, mit „Auswählen“ markieren sie Text;
/// dazu Alles auswählen, Kopieren, Ausschneiden, Einfügen. Die Pfeile gehen als Tastendrücke an die App (mit
/// gehaltener Umschalttaste beim Markieren), die übrigen Aktionen als Kontextmenü-Befehle des Eingabefelds.
/// </summary>
public sealed partial class ZBoardImeService : ITextEditPanelListener
{
    TextEditPanelView? _textEditPanel;
    bool _textEditSelecting;

    bool TextEditOpen => _textEditPanel?.Visibility == ViewStates.Visible;

    void ShowTextEditPanel()
    {
        if (_textEditPanel == null || _keyboardView == null) return;
        if (_composing.Length > 0) CommitComposing(autocorrect: false, separator: string.Empty);
        if (_emojiSearchActive) ExitEmojiSearch(backToPanel: false);

        _textEditSelecting = _selStart != _selEnd; // schon Markiertes lässt sich gleich erweitern
        _textEditPanel.Selecting = _textEditSelecting;
        _textEditPanel.Haptics = _settings.Haptics;
        _textEditPanel.PanelHeight = PanelHeight();
        _textEditPanel.RequestLayout();
        _keyboardView.Visibility = ViewStates.Gone;
        _emojiPanel?.Visibility = ViewStates.Gone;
        _clipboardPanel?.Visibility = ViewStates.Gone;
        _handwritingPanel?.Visibility = ViewStates.Gone;
        _textEditPanel.Visibility = ViewStates.Visible;
        _toolbarOpen = false;
        ShowToolbar();
    }

    public void OnTextEditAction(TextEditAction action)
    {
        var ic = Ic;
        if (ic == null) return;
        MarkEdit();
        _lastCorrection = null;
        _lastSpaceCommit = null;
        _autoSpaceInserted = false;

        switch (action)
        {
            case TextEditAction.Left:
                SendEditKey(Keycode.DpadLeft);
                break;
            case TextEditAction.Right:
                SendEditKey(Keycode.DpadRight);
                break;
            case TextEditAction.Up:
                SendEditKey(Keycode.DpadUp);
                break;
            case TextEditAction.Down:
                SendEditKey(Keycode.DpadDown);
                break;
            case TextEditAction.Home:
                SendEditKey(Keycode.MoveHome);
                break;
            case TextEditAction.End:
                SendEditKey(Keycode.MoveEnd);
                break;
            case TextEditAction.Select:
                _textEditSelecting = !_textEditSelecting;
                // Auswählen beenden hebt die Markierung auf; der Cursor bleibt, wo die Pfeile ihn hingebracht haben
                if (!_textEditSelecting && _selStart != _selEnd) ic.SetSelection(_selEnd, _selEnd);
                break;
            case TextEditAction.SelectAll:
                ic.PerformContextMenuAction(Android.Resource.Id.SelectAll);
                _textEditSelecting = true;
                break;
            case TextEditAction.Copy:
                ic.PerformContextMenuAction(Android.Resource.Id.Copy);
                _textEditSelecting = false;
                break;
            case TextEditAction.Cut:
                ic.PerformContextMenuAction(Android.Resource.Id.Cut);
                _textEditSelecting = false;
                break;
            case TextEditAction.Paste:
                ic.PerformContextMenuAction(Android.Resource.Id.Paste);
                _textEditSelecting = false;
                break;
            case TextEditAction.Delete:
                HandleDelete();
                _textEditSelecting = false;
                break;
            case TextEditAction.Close:
                _textEditSelecting = false;
                ShowKeyboardPanel();
                UpdateAutoShift();
                UpdateSuggestions();
                break;
        }

        _textEditPanel?.Selecting = _textEditSelecting;
    }

    /// <summary>Taste an die App schicken; beim Auswählen mit gehaltener Umschalttaste (erweitert die Markierung).</summary>
    void SendEditKey(Keycode code)
    {
        var ic = Ic;
        if (ic == null) return;
        long now = SystemClock.UptimeMillis();
        var meta = _textEditSelecting ? MetaKeyStates.ShiftOn | MetaKeyStates.ShiftLeftOn : default(MetaKeyStates);
        const KeyEventFlags Flags = KeyEventFlags.SoftKeyboard | KeyEventFlags.KeepTouchMode;
        const int VirtualKeyboard = -1; // KeyCharacterMap.VIRTUAL_KEYBOARD
        ic.SendKeyEvent(new KeyEvent(now, now, KeyEventActions.Down, code, 0, meta, VirtualKeyboard, 0, Flags));
        ic.SendKeyEvent(new KeyEvent(now, now, KeyEventActions.Up, code, 0, meta, VirtualKeyboard, 0, Flags));
    }
}
