using System.Text;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using ZBoard.Clipboard;
using ZBoard.Emoji;
using ZBoard.Keyboard;
using ZBoard.Settings;
using ZBoard.Suggestions;
using KeyboardView = ZBoard.Keyboard.KeyboardView;
using Orientation = Android.Widget.Orientation;
using ZBoard.Localization;

namespace ZBoard.Ime;

/// <summary>Werkzeugleiste, Emoji-Tastatur mit Suche, Zwischenablage-Verlauf und Einhandmodus.</summary>
public sealed partial class ZBoardImeService : IEmojiPanelListener, IClipboardPanelListener
{
    const long ClipChipMaxAgeMs = 90_000;
    const float OneHandedWidth = 0.82f;

    /// <summary>Wie bei Gboard: Werkzeuge links, Mikrofon rechts im Kreis.</summary>
    static readonly IReadOnlyList<ToolbarItem> Toolbar =
    [
        new("clipboard", Resource.Drawable.ms_content_paste),
        new("onehanded", Resource.Drawable.ms_back_hand),
        new("settings", Resource.Drawable.ms_settings),
        new("voice", Resource.Drawable.ms_mic, Trailing: true),
    ];

    LinearLayout? _inputRoot;
    FrameLayout? _panelHost;
    EmojiPanelView? _emojiPanel;
    ClipboardPanelView? _clipboardPanel;
    KeyboardTheme _theme = KeyboardTheme.Light;

    EmojiCatalog? _emojiCatalog;
    string? _emojiCatalogLanguage;
    RecentEmoji _recentEmoji = new();
    bool _emojiSearchActive;
    readonly StringBuilder _emojiQuery = new();

    ClipboardHistory _clipboard = new();
    ClipboardManager? _clipboardManager;
    string? _dismissedClip;

    // ---------- Aufbau ----------

    View BuildInputView()
    {
        float dp = Resources!.DisplayMetrics!.Density;
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _inputRoot = root;

        // Ab Android 15 reicht das Tastaturfenster bis unter die Navigationsleiste: Platz dafür freihalten.
        root.SetOnApplyWindowInsetsListener(new SystemBarInsets());
        // Ein neu angehängter View bekommt die Insets nicht immer von selbst (z. B. wenn Android die Tastatur
        // neu aufbaut): beim Anhängen ausdrücklich anfordern.
        root.AddOnAttachStateChangeListener(new RequestInsetsOnAttach());
        root.SetBackgroundColor(_theme.Background);

        _suggestionBar = new SuggestionBarView(this) { ToolbarItems = Toolbar };
        _suggestionBar.SuggestionPicked += OnSuggestionPicked;
        _suggestionBar.ToolbarPicked += OnToolbarPicked;
        _suggestionBar.EmojiPicked += InsertEmoji;

        _keyboardView = new KeyboardView(this) { Listener = this };
        _emojiPanel = new EmojiPanelView(this) { Listener = this, Visibility = ViewStates.Gone };
        _clipboardPanel = new ClipboardPanelView(this) { Listener = this, Visibility = ViewStates.Gone };

        _emojiCatalogLanguage = null; // neues Panel braucht den Katalog erneut

        _panelHost = new FrameLayout(this);
        _panelHost.AddView(_keyboardView);
        _panelHost.AddView(_emojiPanel);
        _panelHost.AddView(_clipboardPanel);
        ApplyOneHanded();

        root.AddView(_suggestionBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(48 * dp)));
        root.AddView(_panelHost, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return root;
    }

    /// <summary>Beim Öffnen der Tastatur: Einstellungen übernehmen, Panels schließen.</summary>
    void StartPanels()
    {
        _recentEmoji = RecentEmoji.Deserialize(_settings.RecentEmoji);
        _emojiSearchActive = false;
        _emojiQuery.Clear();
        _keyboardView?.HeightScale = _settings.HeightPercent / 100f;
        ApplyOneHanded();
        ShowKeyboardPanel();
    }

    void FinishPanels()
    {
        _settings.RecentEmoji = _recentEmoji.Serialize();
        _emojiSearchActive = false;
        ShowKeyboardPanel();
    }

    void ApplyPanelTheme(KeyboardTheme theme)
    {
        _theme = theme;
        _inputRoot?.SetBackgroundColor(theme.Background); // auch der Streifen hinter der Navigationsleiste
        _panelHost?.SetBackgroundColor(theme.Background);
        _emojiPanel?.Theme = theme;
        _clipboardPanel?.ApplyTheme(theme);
    }

    void ShowKeyboardPanel()
    {
        _keyboardView?.Visibility = ViewStates.Visible;
        _emojiPanel?.Visibility = ViewStates.Gone;
        _clipboardPanel?.Visibility = ViewStates.Gone;
    }

    /// <summary>Panels so hoch wie die Tastatur, damit nichts springt.</summary>
    int PanelHeight() => _keyboardView is { Height: > 0 } kv ? kv.Height : 0;

    // ---------- Einhandmodus ----------

    void ApplyOneHanded()
    {
        if (_panelHost == null) return;
        var mode = _settings.OneHanded;
        int width = mode == OneHandedMode.Off
            ? ViewGroup.LayoutParams.MatchParent
            : (int)(Resources!.DisplayMetrics!.WidthPixels * OneHandedWidth);
        var gravity = mode == OneHandedMode.Left ? GravityFlags.Left : GravityFlags.Right;

        for (int i = 0; i < _panelHost.ChildCount; i++)
        {
            _panelHost.GetChildAt(i)!.LayoutParameters =
                new FrameLayout.LayoutParams(width, ViewGroup.LayoutParams.WrapContent, gravity | GravityFlags.Bottom);
        }

        _panelHost.SetBackgroundColor(_theme.Background);
        _panelHost.RequestLayout();
    }

    // ---------- Werkzeugleiste ----------

    /// <summary>Leere Vorschlagsleiste: Werkzeuge, dazu der eben kopierte Text als Vorschlag.</summary>
    void ShowToolbar()
    {
        if (_suggestionBar == null) return;
        string? chip = null;
        if (_settings.ClipboardHistory && !_profile.IsPassword)
        {
            var latest = _clipboard.Latest(NowMs(), ClipChipMaxAgeMs);
            if (latest != null && latest.Text != _dismissedClip) chip = latest.Text;
        }

        _suggestionBar.Clear(chip);
    }

    void OnToolbarPicked(string id)
    {
        switch (id)
        {
            case SuggestionBarView.ClipChipId:
                var latest = _clipboard.Latest(NowMs(), ClipChipMaxAgeMs);
                if (latest != null)
                {
                    Paste(latest.Text);
                    _dismissedClip = latest.Text;
                }

                break;
            case "clipboard":
                ShowClipboardPanel();
                break;
            case "voice":
                StartVoice();
                break;
            case "onehanded":
                _settings.OneHanded = _settings.OneHanded switch
                {
                    OneHandedMode.Off => OneHandedMode.Right,
                    OneHandedMode.Right => OneHandedMode.Left,
                    _ => OneHandedMode.Off,
                };
                ApplyOneHanded();
                break;
            case "settings":
                var intent = new Intent(this, typeof(SettingsActivity));
                intent.AddFlags(ActivityFlags.NewTask);
                StartActivity(intent);
                break;
            case SuggestionBarView.SearchPillId:
                ExitEmojiSearch(backToPanel: true);
                break;
            case SuggestionBarView.StatusId:
                StopVoice(cancel: false);
                break;
        }
    }

    void Paste(string text)
    {
        var ic = Ic;
        if (ic == null) return;
        if (_composing.Length > 0) CommitComposing(autocorrect: false, separator: string.Empty);
        ic.CommitText(J(text), 1);
        _lastCorrection = null;
        UpdateAutoShift();
        UpdateSuggestions();
    }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- Emoji ----------

    EmojiCatalog Catalog()
    {
        if (_emojiCatalog != null && _emojiCatalogLanguage == _language) return _emojiCatalog;

        using var emoji = Assets!.Open("emoji/emoji.txt");
        Stream? keywords = null;
        try
        {
            keywords = Assets!.Open($"emoji/keywords.{_language}.txt");
        }
        catch (Java.IO.FileNotFoundException)
        {
            keywords = Assets!.Open("emoji/keywords.en.txt");
        }

        using (keywords)
        {
            _emojiCatalog = EmojiCatalog.Load(emoji, keywords);
        }

        _emojiCatalogLanguage = _language;
        _emojiPanel?.SetCatalog(_emojiCatalog, () => _recentEmoji.Items);
        return _emojiCatalog;
    }

    void ShowEmojiPanel()
    {
        if (_emojiPanel == null || _keyboardView == null) return;
        if (_composing.Length > 0) CommitComposing(autocorrect: true, separator: string.Empty);

        Catalog();
        _emojiPanel.PanelHeight = PanelHeight();
        _emojiPanel.ShowStart();
        _keyboardView.Visibility = ViewStates.Gone;
        _clipboardPanel?.Visibility = ViewStates.Gone;
        _emojiPanel.Visibility = ViewStates.Visible;
        ShowToolbar();
    }

    public void OnEmojiChosen(string emoji) => InsertEmoji(emoji);

    void InsertEmoji(string emoji)
    {
        var ic = Ic;
        if (ic == null) return;
        if (_composing.Length > 0) CommitComposing(autocorrect: true, separator: string.Empty);
        ic.CommitText(J(emoji), 1);
        _recentEmoji.Use(emoji);
        if (_settings.Haptics) _keyboardView?.PerformKeyHaptic();
    }

    public void OnEmojiPanelAction(EmojiPanelAction action)
    {
        switch (action)
        {
            case EmojiPanelAction.Close:
                ShowKeyboardPanel();
                UpdateSuggestions();
                break;
            case EmojiPanelAction.Delete:
                HandleDelete();
                break;
            case EmojiPanelAction.Search:
                StartEmojiSearch();
                break;
        }
    }

    // Emoji-Suche: Die Tastatur schreibt in das Suchfeld der Leiste statt in die App.

    void StartEmojiSearch()
    {
        _emojiSearchActive = true;
        _emojiQuery.Clear();
        _symbolsPage = 0;
        SetShift(ShiftState.Off, manual: false);
        ApplyLayout();
        ShowKeyboardPanel();
        _suggestionBar?.SetEmojiSearch(string.Empty, _recentEmoji.Items);
    }

    void EmojiSearchType(string text)
    {
        _emojiQuery.Append(text);
        if (_shift == ShiftState.OneShot) SetShift(ShiftState.Off, manual: false);
        RefreshEmojiSearch();
    }

    /// <summary>Tasten während der Emoji-Suche; true = erledigt.</summary>
    bool EmojiSearchAction(KeyAction action)
    {
        switch (action)
        {
            case KeyAction.Delete:
                if (_emojiQuery.Length > 0) _emojiQuery.Length--;
                RefreshEmojiSearch();
                return true;
            case KeyAction.Space:
                if (_emojiQuery.Length > 0 && _emojiQuery[^1] != ' ') _emojiQuery.Append(' ');
                RefreshEmojiSearch();
                return true;
            case KeyAction.Enter:
            case KeyAction.Emoji:
                ExitEmojiSearch(backToPanel: true);
                return true;
            default:
                return false; // Umschalt, Symbole usw. wie gewohnt
        }
    }

    void RefreshEmojiSearch()
    {
        var query = _emojiQuery.ToString();
        var results = query.Trim().Length == 0 ? _recentEmoji.Items : Catalog().Search(query);
        var probe = new Android.Graphics.Paint();
        _suggestionBar?.SetEmojiSearch(query, [.. results.Where(e => probe.HasGlyph(e))]);
    }

    void ExitEmojiSearch(bool backToPanel)
    {
        _emojiSearchActive = false;
        _emojiQuery.Clear();
        if (backToPanel) ShowEmojiPanel();
        else
        {
            ShowKeyboardPanel();
            UpdateSuggestions();
        }
    }

    // ---------- Zwischenablage ----------

    void InitClipboard()
    {
        _clipboard = ClipboardHistory.DeserializePinned(_settings.PinnedClips);
        _clipboardManager = GetSystemService(ClipboardService) as ClipboardManager;
        if (_clipboardManager != null) _clipboardManager.PrimaryClipChanged += OnPrimaryClipChanged;
    }

    public override void OnDestroy()
    {
        if (_clipboardManager != null) _clipboardManager.PrimaryClipChanged -= OnPrimaryClipChanged;
        StopVoice(cancel: true);
        base.OnDestroy();
    }

    void OnPrimaryClipChanged(object? sender, EventArgs e)
    {
        if (!_settings.ClipboardHistory || _profile.IsPassword) return;
        var clip = _clipboardManager?.PrimaryClip;
        if (clip == null || clip.ItemCount == 0) return;

        // Als vertraulich markierte Inhalte (z. B. aus Passwort-Managern) nie übernehmen
        if (clip.Description?.Extras?.GetBoolean("android.content.extra.IS_SENSITIVE") == true) return;

        var text = clip.GetItemAt(0)?.CoerceToText(this)?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        _clipboard.Add(text, NowMs());
        _dismissedClip = null;
        if (_composing.Length == 0 && !_emojiSearchActive && !_voiceActive) ShowToolbar();
        if (_clipboardPanel?.Visibility == ViewStates.Visible) RefreshClipboardPanel();
    }

    void ShowClipboardPanel()
    {
        if (_clipboardPanel == null || _keyboardView == null) return;
        if (!_settings.ClipboardHistory)
        {
            Toast.MakeText(this, L.T("Der Zwischenablage-Verlauf ist in den Einstellungen ausgeschaltet.", "Clipboard history is turned off in the settings."), ToastLength.Short)!.Show();
            return;
        }

        _clipboardPanel.PanelHeight = PanelHeight();
        RefreshClipboardPanel();
        _keyboardView.Visibility = ViewStates.Gone;
        _emojiPanel?.Visibility = ViewStates.Gone;
        _clipboardPanel.Visibility = ViewStates.Visible;
    }

    void RefreshClipboardPanel() => _clipboardPanel?.ShowItems(_clipboard.Items(NowMs()));

    public void OnClipPaste(string text) => Paste(text);

    public void OnClipPin(string text, bool pinned)
    {
        _clipboard.SetPinned(text, pinned);
        _settings.PinnedClips = _clipboard.SerializePinned();
        RefreshClipboardPanel();
    }

    public void OnClipRemove(string text)
    {
        _clipboard.Remove(text);
        _settings.PinnedClips = _clipboard.SerializePinned();
        RefreshClipboardPanel();
    }

    public void OnClipClearUnpinned()
    {
        _clipboard.ClearUnpinned();
        RefreshClipboardPanel();
    }

    public void OnClipPanelClose()
    {
        ShowKeyboardPanel();
        UpdateSuggestions();
    }

    /// <summary>Innenabstand für Navigationsleiste und Display-Aussparung (links, rechts, unten).</summary>
    /// <summary>
    /// Bei jedem Öffnen der Tastatur den Abstand zur Navigationsleiste prüfen, auch wenn Android keine neuen
    /// Insets schickt (sonst kann die untere Reihe hinter der Leiste landen).
    /// </summary>
    void RefreshSystemBarPadding()
    {
        var root = _inputRoot;
        if (root == null) return;
        root.RequestApplyInsets();
        root.Post(() =>
        {
            if (root.RootWindowInsets is { } insets) SystemBarInsets.Apply(root, insets);
        });
    }

    sealed class RequestInsetsOnAttach : Java.Lang.Object, View.IOnAttachStateChangeListener
    {
        public void OnViewAttachedToWindow(View v) => v.RequestApplyInsets();

        public void OnViewDetachedFromWindow(View v)
        {
        }
    }

    sealed class SystemBarInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            Apply(v, insets);
            return insets;
        }

        /// <summary>Innenabstand links, rechts und unten so groß wie Navigationsleiste und Display-Aussparung.</summary>
        public static void Apply(View v, WindowInsets insets)
        {
            int left, right, bottom;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.NavigationBars() | WindowInsets.Type.DisplayCutout());
                (left, right, bottom) = (bars.Left, bars.Right, bars.Bottom);
            }
            else
            {
#pragma warning disable CA1422, CS0618 // vor Android 11 gibt es nur die alte Schnittstelle
                (left, right, bottom) = (insets.SystemWindowInsetLeft, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CA1422, CS0618
            }

            if (v.PaddingLeft != left || v.PaddingRight != right || v.PaddingBottom != bottom)
            {
                v.SetPadding(left, 0, right, bottom);
            }
        }
    }
}
