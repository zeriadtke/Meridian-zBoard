using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using ZBoard.Localization;

namespace ZBoard.Keyboard;

public enum TextEditAction
{
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    Select,
    SelectAll,
    Copy,
    Cut,
    Paste,
    Delete,
    Close,
}

public interface ITextEditPanelListener
{
    void OnTextEditAction(TextEditAction action);
}

/// <summary>
/// Textbearbeitung wie bei Gboard: Pfeiltasten zum Bewegen des Cursors, „Auswählen“ zum Markieren mit den Pfeilen,
/// dazu Alles auswählen, Kopieren, Ausschneiden und Einfügen. Ersetzt die Tasten, solange es offen ist; „ABC“ führt
/// zurück. Pfeile und Löschen wiederholen sich beim Halten.
/// </summary>
public sealed class TextEditPanelView : View
{
    const int RepeatStartMs = 400;
    const int RepeatIntervalMs = 60;

    sealed record Button(TextEditAction Action, RectF Bounds, int Icon, string Label);

    readonly float _dp;
    readonly Icons _icons;
    readonly Paint _fill = new(PaintFlags.AntiAlias);
    readonly Paint _text = new(PaintFlags.AntiAlias);
    readonly Handler _handler = new(Looper.MainLooper!);
    readonly Java.Lang.Runnable _repeatRunnable;
    readonly List<Button> _buttons = [];
    Button? _pressed;
    bool _repeated;

    public TextEditPanelView(Context context) : base(context)
    {
        _dp = context.Resources!.DisplayMetrics!.Density;
        _icons = new Icons(context);
        _text.TextAlign = Paint.Align.Left;
        _repeatRunnable = new Java.Lang.Runnable(OnRepeat);
    }

    public ITextEditPanelListener? Listener { get; set; }

    /// <summary>Höhe des Panels; die Tastatur soll beim Umschalten nicht springen.</summary>
    public int PanelHeight { get; set; }

    /// <summary>Vibration bei Tastendruck wie auf der Tastatur.</summary>
    public bool Haptics { get; set; } = true;

    /// <summary>„Auswählen“ ist aktiv: Die Pfeile erweitern die Markierung.</summary>
    public bool Selecting
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Invalidate();
        }
    }

    public KeyboardTheme Theme
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = KeyboardTheme.Light;

    static bool Repeats(TextEditAction action) =>
        action is TextEditAction.Left or TextEditAction.Right or TextEditAction.Up or TextEditAction.Down or TextEditAction.Delete;

    // ---------- Größe und Anordnung ----------

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int height = PanelHeight > 0 ? PanelHeight : (int)(4 * 58 * _dp + 8 * _dp);
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), height);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        LayoutButtons();
    }

    void LayoutButtons()
    {
        _buttons.Clear();
#if DEBUG || ZBOARD_TESTHOOKS
        _loggedOrigin = null;
#endif
        if (Width == 0 || Height == 0) return;

        float side = 2 * _dp, gapX = 3 * _dp, gapY = 5 * _dp;
        float top = 2 * _dp, bottom = Height - 8 * _dp;
        float split = side + (Width - 2 * side) * 0.6f; // links die Pfeile, rechts die Aktionen

        // Links: drei Spalten, drei Reihen
        float cellW = (split - side) / 3, cellH = (bottom - top) / 3;
        void Arrow(int column, int row, TextEditAction action, int icon, string label = "")
        {
            var bounds = new RectF(side + column * cellW + gapX, top + row * cellH + gapY,
                side + (column + 1) * cellW - gapX, top + (row + 1) * cellH - gapY);
            _buttons.Add(new Button(action, bounds, icon, label));
        }

        Arrow(0, 0, TextEditAction.Home, Resource.Drawable.ms_first_page);
        Arrow(1, 0, TextEditAction.Up, Resource.Drawable.ms_keyboard_arrow_up);
        Arrow(2, 0, TextEditAction.End, Resource.Drawable.ms_last_page);
        Arrow(0, 1, TextEditAction.Left, Resource.Drawable.ms_keyboard_arrow_left);
        Arrow(1, 1, TextEditAction.Select, 0, L.T("Auswählen", "Select"));
        Arrow(2, 1, TextEditAction.Right, Resource.Drawable.ms_keyboard_arrow_right);
        Arrow(0, 2, TextEditAction.Close, 0, "ABC");
        Arrow(1, 2, TextEditAction.Down, Resource.Drawable.ms_keyboard_arrow_down);
        Arrow(2, 2, TextEditAction.Delete, Resource.Drawable.ms_backspace);

        // Rechts: vier Aktionen untereinander
        (TextEditAction Action, int Icon, string Label)[] actions =
        [
            (TextEditAction.SelectAll, Resource.Drawable.ms_select_all, L.T("Alles auswählen", "Select all")),
            (TextEditAction.Copy, Resource.Drawable.ms_content_copy, L.T("Kopieren", "Copy")),
            (TextEditAction.Cut, Resource.Drawable.ms_content_cut, L.T("Ausschneiden", "Cut")),
            (TextEditAction.Paste, Resource.Drawable.ms_content_paste, L.T("Einfügen", "Paste")),
        ];
        float rowH = (bottom - top) / actions.Length;
        for (int i = 0; i < actions.Length; i++)
        {
            var bounds = new RectF(split + gapX, top + i * rowH + gapY, Width - side - gapX, top + (i + 1) * rowH - gapY);
            _buttons.Add(new Button(actions[i].Action, bounds, actions[i].Icon, actions[i].Label));
        }
    }

    // ---------- Zeichnen ----------

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        _fill.Color = Theme.Background;
        canvas.DrawRect(0, 0, Width, Height, _fill);
#if DEBUG || ZBOARD_TESTHOOKS
        LogButtonPositions();
#endif

        float radius = 7 * _dp;
        foreach (var button in _buttons)
        {
            bool active = button.Action == TextEditAction.Select && Selecting;
            bool special = button.Action is TextEditAction.Close or TextEditAction.Delete or TextEditAction.Home or TextEditAction.End;
            _fill.Color = button == _pressed ? Theme.KeyPressed : active ? Theme.Accent : special ? Theme.SpecialKey : Theme.Key;
            float r = button.Action == TextEditAction.Close ? button.Bounds.Height() / 2 : radius;
            canvas.DrawRoundRect(button.Bounds, r, r, _fill);
            DrawLabel(canvas, button, active ? Theme.AccentText : Theme.Text);
        }
    }

    void DrawLabel(Canvas canvas, Button button, Color color)
    {
        var b = button.Bounds;
        if (button.Label.Length == 0)
        {
            _icons.Draw(canvas, button.Icon, b.CenterX(), b.CenterY(), 26 * _dp, color);
            return;
        }

        float iconSize = button.Icon != 0 ? 20 * _dp : 0;
        float iconGap = button.Icon != 0 ? 8 * _dp : 0;
        _text.TextSize = 15 * _dp;
        _text.Color = color;
        float available = b.Width() - 16 * _dp - iconSize - iconGap;
        float measured = _text.MeasureText(button.Label);
        if (measured > available && measured > 0)
        {
            _text.TextSize *= available / measured;
            measured = _text.MeasureText(button.Label);
        }

        float left = b.CenterX() - (iconSize + iconGap + measured) / 2;
        if (button.Icon != 0) _icons.Draw(canvas, button.Icon, left + iconSize / 2, b.CenterY(), iconSize, color);
        float baseline = b.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
        canvas.DrawText(button.Label, left + iconSize + iconGap, baseline, _text);
    }

#if DEBUG || ZBOARD_TESTHOOKS
    (int X, int Y)? _loggedOrigin;

    /// <summary>Nur Testversionen: Bildschirmposition jeder Schaltfläche ins Log, Format "EDIT Left@100,1700 Copy@900,1650 …".</summary>
    void LogButtonPositions()
    {
        if (_buttons.Count == 0 || Visibility != ViewStates.Visible) return;
        var origin = new int[2];
        GetLocationOnScreen(origin);
        if (_loggedOrigin == (origin[0], origin[1])) return;
        _loggedOrigin = (origin[0], origin[1]);
        var line = new System.Text.StringBuilder("EDIT ");
        foreach (var button in _buttons)
        {
            line.Append(button.Action).Append('@')
                .Append((int)(origin[0] + button.Bounds.CenterX())).Append(',')
                .Append((int)(origin[1] + button.Bounds.CenterY())).Append(' ');
        }

        Android.Util.Log.Info("zBoardTest", line.ToString());
    }
#endif

    // ---------- Berührung ----------

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _pressed = ButtonAt(e.GetX(), e.GetY());
                _repeated = false;
                if (_pressed != null)
                {
                    if (Haptics) PerformHapticFeedback(FeedbackConstants.KeyboardTap);
                    if (Repeats(_pressed.Action))
                    {
                        // Pfeile und Löschen wirken sofort und wiederholen sich beim Halten
                        _repeated = true;
                        Listener?.OnTextEditAction(_pressed.Action);
                        _handler.PostDelayed(_repeatRunnable, RepeatStartMs);
                    }
                }

                Invalidate();
                break;

            case MotionEventActions.Move:
                if (_pressed != null && ButtonAt(e.GetX(), e.GetY()) != _pressed) Cancel();
                break;

            case MotionEventActions.Up:
                var released = _pressed;
                bool fired = _repeated;
                Cancel();
                if (released != null && !fired) Listener?.OnTextEditAction(released.Action);
                break;

            case MotionEventActions.Cancel:
                Cancel();
                break;
        }

        return true;
    }

    void OnRepeat()
    {
        if (_pressed == null || !Repeats(_pressed.Action)) return;
        Listener?.OnTextEditAction(_pressed.Action);
        _handler.PostDelayed(_repeatRunnable, RepeatIntervalMs);
    }

    void Cancel()
    {
        _handler.RemoveCallbacks(_repeatRunnable);
        _pressed = null;
        Invalidate();
    }

    protected override void OnDetachedFromWindow()
    {
        _handler.RemoveCallbacks(_repeatRunnable);
        base.OnDetachedFromWindow();
    }

    protected override void OnVisibilityChanged(View changedView, ViewStates visibility)
    {
        base.OnVisibilityChanged(changedView, visibility);
        if (visibility != ViewStates.Visible) Cancel();
#if DEBUG || ZBOARD_TESTHOOKS
        _loggedOrigin = null;
#endif
    }

    Button? ButtonAt(float x, float y) => _buttons.FirstOrDefault(b => b.Bounds.Contains(x, y));
}
