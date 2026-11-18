using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using ZBoard.Localization;
using ZBoard.Plugins;

namespace ZBoard.Keyboard;

public enum HandwritingKey
{
    Close,
    Comma,
    Space,
    Period,
    Delete,
    Enter,
}

public interface IHandwritingListener
{
    /// <summary>Ein neuer Strich beginnt (Zeitpunkt in Uptime-ms).</summary>
    void OnStrokeStarted(long time);

    /// <summary>Ein Strich ist fertig; die Striche stehen in <see cref="HandwritingPanelView.Strokes"/>.</summary>
    void OnStrokeFinished();

    void OnHandwritingKey(HandwritingKey key);
}

/// <summary>
/// Schreibfläche für Handschrift wie bei Gboard: oben schreibt man mit dem Finger oder Stift, unten eine Reihe mit
/// ABC, Komma, Leertaste, Punkt, Löschen und Enter. Erkannt wird nicht hier, sondern in einem Handschrift-Plugin
/// (zBoard Pen); diese Ansicht sammelt nur die Striche.
/// </summary>
public sealed class HandwritingPanelView : View
{
    const int RepeatStartMs = 400;
    const int RepeatIntervalMs = 60;

    sealed record Button(HandwritingKey Key, RectF Bounds);

    readonly float _dp;
    readonly Icons _icons;
    readonly Paint _fill = new(PaintFlags.AntiAlias);
    readonly Paint _text = new(PaintFlags.AntiAlias);
    readonly Paint _ink = new(PaintFlags.AntiAlias);
    readonly Paint _guide = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path _path = new();
    readonly Handler _handler = new(Looper.MainLooper!);
    readonly Java.Lang.Runnable _repeatRunnable;
    readonly List<Button> _buttons = [];
    readonly List<List<(float X, float Y, long Time)>> _strokes = [];
    List<(float X, float Y, long Time)>? _current;
    int _pointerId = -1;
    Button? _pressed;
    bool _repeated;
    RectF _writeArea = new();

    public HandwritingPanelView(Context context) : base(context)
    {
        _dp = context.Resources!.DisplayMetrics!.Density;
        _icons = new Icons(context);
        _text.TextAlign = Paint.Align.Center;
        _ink.SetStyle(Paint.Style.Stroke);
        _ink.StrokeWidth = 4 * _dp;
        _ink.StrokeCap = Paint.Cap.Round;
        _ink.StrokeJoin = Paint.Join.Round;
        _guide.StrokeWidth = 1 * _dp;
        _repeatRunnable = new Java.Lang.Runnable(OnRepeat);
    }

    public IHandwritingListener? Listener { get; set; }

    public int PanelHeight { get; set; }

    public bool Haptics { get; set; } = true;

    /// <summary>Beschriftung der Enter-Taste (wie auf der Tastatur, z. B. „🔍“ für Suchen).</summary>
    public string EnterLabel { get; set; } = "⏎";

    /// <summary>Hinweis in der leeren Schreibfläche (z. B. Sprache oder ein fehlendes Modell).</summary>
    public string Hint
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = string.Empty;

    public KeyboardTheme Theme
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = KeyboardTheme.Light;

    /// <summary>Breite und Höhe der Schreibfläche in Pixeln.</summary>
    public float WriteWidth => _writeArea.Width();

    public float WriteHeight => _writeArea.Height();

    /// <summary>Alle Striche (auch der gerade entstehende), Koordinaten relativ zur Schreibfläche.</summary>
    public IReadOnlyList<InkStroke> Strokes => [.. _strokes.Where(s => s.Count > 0).Select(s => new InkStroke([.. s]))];

    public bool HasInk => _strokes.Count > 0;

    /// <summary>Striche löschen; mit <paramref name="keepCurrent"/> bleibt der Strich, den der Finger gerade zieht.</summary>
    public void ClearInk(bool keepCurrent = false)
    {
        _strokes.Clear();
        if (keepCurrent && _current != null) _strokes.Add(_current);
        else _current = null;
        Invalidate();
    }

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
        if (Width == 0 || Height == 0) return;
        float side = 2 * _dp, gapX = 3 * _dp, gapY = 6 * _dp, bottomPad = 8 * _dp;
        float rowH = Math.Min(58 * _dp, Height / 4f);
        float top = Height - bottomPad - rowH;
        _writeArea = new RectF(side + gapX, 4 * _dp, Width - side - gapX, top);

        // Wie die untere Reihe der Tastatur: ABC 1,5 · Komma 1 · Leertaste 4 · Punkt 1 · Löschen 1,5 · Enter 1,5
        (HandwritingKey Key, float Units)[] row =
        [
            (HandwritingKey.Close, 1.5f), (HandwritingKey.Comma, 1f), (HandwritingKey.Space, 3.5f),
            (HandwritingKey.Period, 1f), (HandwritingKey.Delete, 1.5f), (HandwritingKey.Enter, 1.5f),
        ];
        float unit = (Width - 2 * side) / row.Sum(r => r.Units);
        float x = side;
        foreach (var (key, units) in row)
        {
            float w = units * unit;
            _buttons.Add(new Button(key, new RectF(x + gapX, top + gapY, x + w - gapX, top + rowH - gapY)));
            x += w;
        }
#if DEBUG || ZBOARD_TESTHOOKS
        _logged = string.Empty;
#endif
    }

    // ---------- Zeichnen ----------

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        _fill.Color = Theme.Background;
        canvas.DrawRect(0, 0, Width, Height, _fill);

        // Schreibfläche: leicht abgesetzt, mit einer Grundlinie als Orientierung
        float radius = 12 * _dp;
        _fill.Color = Theme.Key;
        _fill.Alpha = Math.Min((int)Theme.Key.A, 110);
        canvas.DrawRoundRect(_writeArea, radius, radius, _fill);
        _fill.Alpha = 255;
        _guide.Color = Theme.HintText;
        _guide.Alpha = 80;
        float baseline = _writeArea.Top + _writeArea.Height() * 0.68f;
        canvas.DrawLine(_writeArea.Left + 24 * _dp, baseline, _writeArea.Right - 24 * _dp, baseline, _guide);

        if (_strokes.Count == 0)
        {
            _text.TextSize = 18 * _dp;
            _text.Color = Theme.HintText;
            string hint = Hint.Length > 0 ? Hint : L.T("Hier schreiben", "Write here");
            canvas.DrawText(hint, _writeArea.CenterX(), _writeArea.CenterY() - (_text.Descent() + _text.Ascent()) / 2, _text);
        }

        _ink.Color = Theme.Text;
        foreach (var stroke in _strokes)
        {
            if (stroke.Count == 0) continue;
            _path.Reset();
            _path.MoveTo(_writeArea.Left + stroke[0].X, _writeArea.Top + stroke[0].Y);
            if (stroke.Count == 1) _path.LineTo(_writeArea.Left + stroke[0].X + 0.1f, _writeArea.Top + stroke[0].Y);
            for (int i = 1; i < stroke.Count; i++) _path.LineTo(_writeArea.Left + stroke[i].X, _writeArea.Top + stroke[i].Y);
            canvas.DrawPath(_path, _ink);
        }

        foreach (var button in _buttons) DrawButton(canvas, button);
#if DEBUG || ZBOARD_TESTHOOKS
        LogPositions();
#endif
    }

    void DrawButton(Canvas canvas, Button button)
    {
        var b = button.Bounds;
        _fill.Color = button == _pressed ? Theme.KeyPressed
            : button.Key switch
            {
                HandwritingKey.Enter => Theme.EnterKey,
                HandwritingKey.Space or HandwritingKey.Comma or HandwritingKey.Period => Theme.Key,
                _ => Theme.SpecialKey,
            };
        float r = button.Key is HandwritingKey.Close or HandwritingKey.Enter ? b.Height() / 2 : 7 * _dp;
        canvas.DrawRoundRect(b, r, r, _fill);

        var color = button.Key == HandwritingKey.Enter ? Theme.EnterText : Theme.Text;
        int icon = button.Key switch
        {
            HandwritingKey.Delete => Resource.Drawable.ms_backspace,
            HandwritingKey.Enter => Icons.ForEnter(EnterLabel),
            _ => 0,
        };
        if (icon != 0)
        {
            _icons.Draw(canvas, icon, b.CenterX(), b.CenterY(), 24 * _dp, color);
            return;
        }

        string label = button.Key switch
        {
            HandwritingKey.Close => "ABC",
            HandwritingKey.Comma => ",",
            HandwritingKey.Period => ".",
            _ => string.Empty,
        };
        _text.TextSize = (button.Key == HandwritingKey.Close ? 15 : 22) * _dp;
        _text.Color = color;
        canvas.DrawText(label, b.CenterX(), b.CenterY() - (_text.Descent() + _text.Ascent()) / 2, _text);
    }

#if DEBUG || ZBOARD_TESTHOOKS
    string _logged = string.Empty;

    /// <summary>Nur Testversionen: Lage der Schreibfläche und der Tasten ins Log ("PEN area@x1,y1,x2,y2 Space@…").</summary>
    void LogPositions()
    {
        if (_buttons.Count == 0) return;
        var origin = new int[2];
        GetLocationOnScreen(origin);
        var line = new System.Text.StringBuilder("PEN ");
        line.Append($"area@{origin[0] + (int)_writeArea.Left},{origin[1] + (int)_writeArea.Top},{origin[0] + (int)_writeArea.Right},{origin[1] + (int)_writeArea.Bottom} ");
        foreach (var button in _buttons)
        {
            line.Append(button.Key).Append('@')
                .Append(origin[0] + (int)button.Bounds.CenterX()).Append(',')
                .Append(origin[1] + (int)button.Bounds.CenterY()).Append(' ');
        }

        string text = line.ToString();
        if (text == _logged) return;
        _logged = text;
        Android.Util.Log.Info("zBoardTest", text);
    }
#endif

    // ---------- Berührung ----------

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        int index = e.ActionIndex;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            {
                float x = e.GetX(index), y = e.GetY(index);
                var button = _buttons.FirstOrDefault(b => b.Bounds.Contains(x, y));
                if (button != null)
                {
                    PressButton(button);
                    break;
                }

                // Schreiben: auch knapp außerhalb der Fläche beginnen lassen, die Punkte bleiben relativ zu ihr
                _pointerId = e.GetPointerId(index);
                _current = [(x - _writeArea.Left, y - _writeArea.Top, e.EventTime)];
                _strokes.Add(_current);
                Listener?.OnStrokeStarted(e.EventTime);
                Invalidate();
                break;
            }

            case MotionEventActions.Move:
                if (_current != null)
                {
                    int i = e.FindPointerIndex(_pointerId);
                    if (i < 0) break;
                    for (int h = 0; h < e.HistorySize; h++)
                    {
                        _current.Add((e.GetHistoricalX(i, h) - _writeArea.Left, e.GetHistoricalY(i, h) - _writeArea.Top, e.GetHistoricalEventTime(h)));
                    }

                    _current.Add((e.GetX(i) - _writeArea.Left, e.GetY(i) - _writeArea.Top, e.EventTime));
                    Invalidate();
                }
                else if (_pressed != null && !_pressed.Bounds.Contains(e.GetX(), e.GetY()))
                {
                    CancelButton();
                }

                break;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                if (_current != null && e.GetPointerId(index) == _pointerId)
                {
                    _current.Add((e.GetX(index) - _writeArea.Left, e.GetY(index) - _writeArea.Top, e.EventTime));
                    _current = null;
                    _pointerId = -1;
                    Invalidate();
                    Listener?.OnStrokeFinished();
                }
                else if (_pressed != null && e.ActionMasked == MotionEventActions.Up)
                {
                    var released = _pressed;
                    bool fired = _repeated;
                    CancelButton();
                    if (!fired) Listener?.OnHandwritingKey(released.Key);
                }

                break;

            case MotionEventActions.Cancel:
                if (_current != null)
                {
                    _strokes.Remove(_current);
                    _current = null;
                    _pointerId = -1;
                    Invalidate();
                }

                CancelButton();
                break;
        }

        return true;
    }

    void PressButton(Button button)
    {
        _pressed = button;
        _repeated = false;
        if (Haptics) PerformHapticFeedback(FeedbackConstants.KeyboardTap);
        if (button.Key == HandwritingKey.Delete)
        {
            _repeated = true;
            Listener?.OnHandwritingKey(HandwritingKey.Delete);
            _handler.PostDelayed(_repeatRunnable, RepeatStartMs);
        }

        Invalidate();
    }

    void OnRepeat()
    {
        if (_pressed?.Key != HandwritingKey.Delete) return;
        Listener?.OnHandwritingKey(HandwritingKey.Delete);
        _handler.PostDelayed(_repeatRunnable, RepeatIntervalMs);
    }

    void CancelButton()
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
}
