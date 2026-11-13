using System.Numerics;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;

namespace ZBoard.Keyboard;

public enum ShiftState
{
    Off,
    OneShot,
    Locked,
}

/// <summary>Rückmeldungen der Tastaturansicht an den IME-Service.</summary>
public interface IKeyboardActionListener
{
    /// <summary>Finger berührt eine Taste (für Haptik und Ton).</summary>
    void OnKeyDown(KeyDef key, long uptimeMs);

    /// <summary>Finger verlässt die Tastatur. <paramref name="output"/> ist der erzeugte Text oder null.</summary>
    void OnKeyUp(KeyDef key, string? output, long uptimeMs, bool gesture);

    void OnText(string text);

    void OnAction(KeyAction action);

    /// <summary>Cursor um <paramref name="delta"/> Zeichen bewegen (Wischen über die Leertaste).</summary>
    void OnCursorMove(int delta);

    /// <summary>Wischspur über die Buchstaben, Koordinaten in Tastenbreiten.</summary>
    void OnGlide(IReadOnlyList<Vector2> path, IReadOnlyDictionary<char, Vector2> keyCenters);

    /// <summary>Während des Wischens von ⌫ nach links: so viele Wörter sind zum Löschen markiert.</summary>
    void OnDeleteSwipe(int words);

    /// <summary>Wischen von ⌫ beendet; <paramref name="commit"/> = markierte Wörter löschen.</summary>
    void OnDeleteSwipeEnd(bool commit);

    /// <summary>Sprache aus der Auswahl (langer Druck auf die Leertaste) gewählt.</summary>
    void OnLanguageSelected(string language);
}

/// <summary>
/// Zeichnet die Tasten und wertet Berührungen aus: Tippen, langer Druck, Wischeingabe,
/// Löschen mit Wiederholung oder per Wischen, Leertasten-Cursor, Tastenvorschau.
/// </summary>
public sealed class KeyboardView : View
{
    const int LongPressMs = 350;
    const int RepeatStartMs = 400;
    const int RepeatIntervalMs = 50;
    const float GlideStartKeys = 0.6f;       // so weit (in Tastenbreiten) muss der Finger wandern, damit Wischen beginnt
    const float DeleteSwipeStartKeys = 0.6f;
    const float DeleteSwipeKeysPerWord = 0.9f;

    sealed class PlacedKey(KeyDef key, RectF bounds, bool functional)
    {
        public KeyDef Key { get; } = key;
        public RectF Bounds { get; } = bounds;

        /// <summary>Zeichentaste in der unteren Reihe (Komma, Punkt): wie Gboard in der Farbe der Sondertasten.</summary>
        public bool Functional { get; } = functional;
    }

    sealed class Press(PlacedKey key, int pointerId, float startX, float startY)
    {
        public PlacedKey Key { get; set; } = key;
        public int PointerId { get; } = pointerId;
        public float StartX { get; } = startX;
        public float StartY { get; } = startY;

        /// <summary>Wo der Finger die Taste getroffen hat, die am Ende gilt (beim Rutschen auf eine andere: dort).</summary>
        public float TapX { get; set; } = startX;
        public float TapY { get; set; } = startY;
        public bool CursorMode { get; set; }
        public float CursorAnchorX { get; set; }
        public bool Gliding { get; set; }
        public List<PointF> Trail { get; } = [];
        public bool DeleteSwipe { get; set; }
        public int DeleteWords { get; set; }
        public bool RepeatFired { get; set; }
    }

    sealed class Popup(List<string> items, RectF bounds, float cellWidth, List<string>? values = null)
    {
        /// <summary>
        /// Taste mit nur einem Zusatzzeichen (meist die Ziffer der oberen Reihe): wie bei Gboard ein runder
        /// Kreis über der Taste statt einer Leiste; die Taste selbst wird währenddessen ausgegraut.
        /// </summary>
        public bool Round { get; init; }

        /// <summary>Gedrückte Taste: Unter Gboard-Popups verschwindet ihre Fläche, die Beschriftung bleibt blass.</summary>
        public PlacedKey? Source { get; init; }

        /// <summary>Raster wie bei Gboard (Zusatzzeichen); null bei der Sprachauswahl (eine Zeile).</summary>
        public IReadOnlyList<PopupCell>? Grid { get; init; }

        /// <summary>Mitte der Spalte 0 (über der Taste) und Unterkante der untersten Zeile.</summary>
        public float AnchorX { get; init; }
        public float GridBottom { get; init; }
        public float CellHeight { get; init; }

        /// <summary>Bei der Sprachauswahl: Sprachcodes zu den angezeigten Namen.</summary>
        public List<string>? Values { get; } = values;
        public List<string> Items { get; } = items;
        public RectF Bounds { get; } = bounds;
        public float CellWidth { get; } = cellWidth;
        public int Selected { get; set; }
    }

    readonly float _dp;
    readonly Icons _icons;
    readonly Paint _fill = new(PaintFlags.AntiAlias);
    readonly Paint _text = new(PaintFlags.AntiAlias);
    readonly Paint _hint = new(PaintFlags.AntiAlias);
    readonly Paint _shadow = new(PaintFlags.AntiAlias);
    readonly Paint _trail = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path _trailPath = new();
    readonly Handler _handler = new(Looper.MainLooper!);
    readonly Java.Lang.Runnable _longPressRunnable;
    readonly Java.Lang.Runnable _repeatRunnable;
    readonly List<PlacedKey> _keys = [];
    readonly Dictionary<char, Vector2> _keyCenters = [];

    /// <summary>Mitten der Buchstabentasten in Tastenbreiten (dieselben Einheiten wie <see cref="TapPoint"/>).</summary>
    public IReadOnlyDictionary<char, Vector2> KeyCenters => _keyCenters;

    /// <summary>
    /// Nur während <see cref="IKeyboardActionListener.OnText"/> für eine angetippte Zeichentaste gesetzt: wo der Finger
    /// die Taste getroffen hat, in Tastenbreiten. Für Zeichen aus dem Popup und alles andere null.
    /// </summary>
    public Vector2? TapPoint { get; private set; }

    KeyboardLayout? _layout;
    Press? _press;
    Popup? _popup;
    float _unit = 1;

    public KeyboardView(Context context) : base(context)
    {
        _dp = context.Resources!.DisplayMetrics!.Density;
        _icons = new Icons(context);
        _text.TextAlign = Paint.Align.Center;
        _hint.TextAlign = Paint.Align.Center;
        _trail.SetStyle(Paint.Style.Stroke);
        _trail.StrokeWidth = 5 * _dp;
        _trail.StrokeCap = Paint.Cap.Round;
        _trail.StrokeJoin = Paint.Join.Round;
        _longPressRunnable = new Java.Lang.Runnable(OnLongPress);
        _repeatRunnable = new Java.Lang.Runnable(OnRepeat);
    }

    public IKeyboardActionListener? Listener { get; set; }

    public KeyboardLayout? CurrentLayout => _layout;

    public ShiftState Shift
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Invalidate();
        }
    }

    public string SpaceLabel
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = string.Empty;

    public string EnterLabel
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = "⏎";

    public KeyboardTheme Theme
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = KeyboardTheme.Light;

    /// <summary>Aktivierte Sprachen (Code, Name) für die Auswahl per langem Druck auf die Leertaste.</summary>
    public IReadOnlyList<(string Code, string Name)> Languages { get; set; } = [];

    /// <summary>Sprache des angezeigten Layouts; in der Auswahl über der Leertaste vorausgewählt.</summary>
    public string CurrentLanguage { get; set; } = string.Empty;

    /// <summary>Wischeingabe über die Buchstaben.</summary>
    public bool GlideEnabled { get; set; } = true;

    /// <summary>Vergrößerte Taste über dem Finger anzeigen.</summary>
    public bool ShowKeyPreview { get; set; } = true;

    /// <summary>Ziffern klein oben rechts auf der obersten Buchstabenreihe (aus, wenn eine Zahlenreihe da ist).</summary>
    public bool ShowDigitHints { get; set; } = true;

    /// <summary>Tastenhöhe als Faktor (0,8 bis 1,3).</summary>
    public float HeightScale
    {
        get;
        set
        {
            float clamped = Math.Clamp(value, 0.7f, 1.4f);
            if (clamped == field) return; // kein unnötiges Neu-Layout beim Öffnen der Tastatur
            field = clamped;
            RequestLayout();
        }
    } = 1f;

    /// <summary>Gewünschte Reihenhöhe.</summary>
    float KeyHeight => 58 * _dp * HeightScale;

    /// <summary>
    /// Tatsächliche Reihenhöhe. Gibt das Fenster weniger Platz als gewünscht (manche Geräte vergrößern das
    /// Tastaturfenster nicht sofort), werden die Reihen etwas flacher, statt dass die obere Reihe verschwindet.
    /// </summary>
    float _rowHeight;
    bool _relayoutPosted;
    // Abstände wie Gboard (aus Screenshots gemessen): Tasten ~46 dp hoch bei 58 dp Reihenabstand, 2 dp Rand
    // links und rechts, oben kein Abstand zur Leiste, unten 8 dp.
    float TopPadding => 0;
    float BottomPadding => 8 * _dp;
    float VerticalPadding => (TopPadding + BottomPadding) / 2;
    float HorizontalPadding => 2 * _dp;
    float GapX => 3 * _dp;
    float GapY => 6.25f * _dp;

    public void SetLayout(KeyboardLayout layout)
    {
        bool heightChanged = _layout == null || _layout.Rows.Count != layout.Rows.Count;
        CancelPress();
        _layout = layout;
        LayoutKeys();
        if (heightChanged) RequestLayout();
        Invalidate();
    }

    public void PerformKeyHaptic() => PerformHapticFeedback(FeedbackConstants.KeyboardTap);

    // ---------- Größe und Anordnung ----------

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec);
        int rows = _layout?.Rows.Count ?? 4;
        int desired = (int)(rows * KeyHeight + 2 * VerticalPadding);
        int height = MeasureSpec.GetMode(heightMeasureSpec) switch
        {
            MeasureSpecMode.Exactly => MeasureSpec.GetSize(heightMeasureSpec),
            MeasureSpecMode.AtMost => Math.Min(desired, MeasureSpec.GetSize(heightMeasureSpec)),
            _ => desired,
        };

        if (height < desired && !_relayoutPosted)
        {
            // Fenster war zu klein: einmal neu anfordern, damit es auf die volle Höhe wachsen kann
            _relayoutPosted = true;
            Post(() =>
            {
                _relayoutPosted = false;
                RootView?.RequestLayout();
            });
        }

        SetMeasuredDimension(width, height);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        LayoutKeys();
    }

    void LayoutKeys()
    {
#if DEBUG || ZBOARD_TESTHOOKS
        _loggedOrigin = null;
#endif
        _keys.Clear();
        _keyCenters.Clear();
        if (_layout == null || Width == 0) return;

        _unit = (Width - 2 * HorizontalPadding) / _layout.Units;
        _rowHeight = Math.Min(KeyHeight, (Height - TopPadding - BottomPadding) / _layout.Rows.Count);
        if (_rowHeight <= 0) _rowHeight = KeyHeight;
        float y = TopPadding;
        var lastRow = _layout.Rows[^1];
        foreach (var row in _layout.Rows)
        {
            float fixedUnits = row.Keys.Where(k => k.Width > 0).Sum(k => k.Width);
            int fillCount = row.Keys.Count(k => k.Width <= 0);
            float fillUnits = fillCount > 0
                ? Math.Max(1f, (_layout.Units - fixedUnits - 2 * row.Indent) / fillCount)
                : 0f;

            float x = HorizontalPadding + row.Indent * _unit;
            foreach (var key in row.Keys)
            {
                float w = (key.Width > 0 ? key.Width : fillUnits) * _unit;
                var bounds = new RectF(x + GapX, y + GapY, x + w - GapX, y + _rowHeight - GapY);
                _keys.Add(new PlacedKey(key, bounds, key.IsCharacter && row == lastRow));

                if (key.IsCharacter && key.Label.Length == 1 && char.IsLetter(key.Label[0]))
                {
                    _keyCenters[key.Label[0]] = ToUnits(bounds.CenterX(), bounds.CenterY());
                }

                x += w;
            }

            y += _rowHeight;
        }
    }

    Vector2 ToUnits(float x, float y) => new(x / _unit, y / _unit);

    // ---------- Zeichnen ----------

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // Nur die eigene Fläche füllen: Seit der runde Zahlen-Popup über den Rand zeichnen darf (ClipChildren = false
        // bei den Eltern), reicht der Zeichenbereich bis über die Vorschlagsleiste – DrawColor würde sie übermalen.
        _fill.Color = Theme.Background;
        canvas.DrawRect(0, 0, Width, Height, _fill);
#if DEBUG || ZBOARD_TESTHOOKS
        LogKeyPositions();
#endif

        float radius = 7 * _dp;
        foreach (var pk in _keys)
        {
            if (_popup != null && _popup.Source == pk)
            {
                // Wie Gboard: Unter dem Popup verschwindet die Tastenfläche, die Beschriftung bleibt blass stehen.
                DrawKeyLabel(canvas, pk, faded: true);
                continue;
            }

            bool pressed = _press is { Gliding: false } current && current.Key == pk && _popup == null;
            _fill.Color = pressed ? Theme.KeyPressed : KeyColor(pk);
            // Wie Gboard: Ebenen- und Enter-Taste als Pille
            float r = pk.Key.Action is KeyAction.Enter or KeyAction.ToSymbols or KeyAction.ToAlpha
                ? pk.Bounds.Height() / 2
                : radius;
            canvas.DrawRoundRect(pk.Bounds, r, r, _fill);
            DrawKeyLabel(canvas, pk);
        }

        bool showsPopup = _popup != null;
        if (PopupLayer != null && (showsPopup || _layerShowsPopup)) PopupLayer.Invalidate();
        _layerShowsPopup = showsPopup;

        if (_press is { Gliding: true }) DrawTrail(canvas, _press.Trail);
        else if (_popup != null)
        {
            if (PopupLayer == null) DrawPopup(canvas, radius);
        }
        else if (ShowKeyPreview && _press is { CursorMode: false, DeleteSwipe: false } p && p.Key.Key.IsCharacter)
        {
            DrawPreview(canvas, p.Key, radius);
        }
    }

    Color KeyColor(PlacedKey pk) => pk.Key.Action switch
    {
        KeyAction.Character when pk.Functional => Theme.SpecialKey,
        KeyAction.Character or KeyAction.Space or KeyAction.Emoji => Theme.Key,
        KeyAction.Enter => Theme.EnterKey,
        _ => Theme.SpecialKey,
    };

    /// <summary>Material-Symbol statt Text, oder 0.</summary>
    int IconFor(KeyDef key) => key.Action switch
    {
        KeyAction.Shift => Shift switch
        {
            ShiftState.Locked => Resource.Drawable.ms_shift_lock_fill1,
            ShiftState.OneShot => Resource.Drawable.ms_shift_fill1,
            _ => Resource.Drawable.ms_shift,
        },
        KeyAction.Delete when _press is not { DeleteSwipe: true, DeleteWords: > 0 } => Resource.Drawable.ms_backspace,
        KeyAction.Enter => Icons.ForEnter(EnterLabel),
        KeyAction.Emoji => Resource.Drawable.ms_sentiment_satisfied,
        KeyAction.Language => Resource.Drawable.ms_language,
        _ => 0,
    };

    void DrawKeyLabel(Canvas canvas, PlacedKey pk, bool faded = false)
    {
        const int FadedAlpha = 90;
        var key = pk.Key;
        var b = pk.Bounds;

        if (IconFor(key) is var icon and not 0)
        {
            var color = key.Action switch
            {
                KeyAction.Enter => Theme.EnterText,
                KeyAction.Shift when Shift != ShiftState.Off => Theme.Accent,
                _ => Theme.Text,
            };
            _icons.Draw(canvas, icon, b.CenterX(), b.CenterY(), 24 * _dp, color);
            return;
        }

        string label = DisplayLabel(key);

        float size = key.Action switch
        {
            KeyAction.Character => 24,
            KeyAction.Space => 13,
            KeyAction.ToSymbols or KeyAction.ToSymbols2 or KeyAction.ToAlpha => 15,
            KeyAction.Delete when _press is { DeleteSwipe: true } => 15,
            _ => 20,
        };
        _text.TextSize = size * _dp;
        _text.Color = key.Action switch
        {
            KeyAction.Space => Theme.HintText,
            KeyAction.Delete when _press is { DeleteSwipe: true, DeleteWords: > 0 } => Theme.Accent,
            _ => Theme.Text,
        };

        if (faded) _text.Alpha = FadedAlpha;

        float baseline = b.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
        canvas.DrawText(label, b.CenterX(), baseline, _text);

        // Zahlenhinweis oben rechts (wie die Gboard-Option "Zahlenhinweise")
        if (ShowDigitHints && _layout?.IsAlphabetic == true && key.IsCharacter
            && key.Alternates.FirstOrDefault(a => a is [var c] && char.IsDigit(c)) is { } digit)
        {
            _hint.TextSize = 10 * _dp;
            _hint.Color = Theme.HintText;
            if (faded) _hint.Alpha = FadedAlpha;
            canvas.DrawText(digit, b.Right - 7 * _dp, b.Top + 12 * _dp, _hint);
        }
    }

    string DisplayLabel(KeyDef key) => key.Action switch
    {
        KeyAction.Character => Shifted(key.Label),
        KeyAction.Shift => Shift == ShiftState.Locked ? "⇪" : "⇧",
        KeyAction.Delete when _press is { DeleteSwipe: true, DeleteWords: > 0 } d => $"⌫ {d.DeleteWords}",
        KeyAction.Delete => "⌫",
        KeyAction.Enter => EnterLabel,
        KeyAction.Space => Languages.Count > 1 ? SpaceLabel : string.Empty, // wie Gboard: leer bei nur einer Sprache
        KeyAction.ToSymbols => "?123",
        KeyAction.ToSymbols2 => "=\\<",
        KeyAction.ToAlpha => "ABC",
        KeyAction.Language => "🌐",
        KeyAction.Emoji => "☺",
        _ => key.Label,
    };

    string Shifted(string text) =>
        Shift != ShiftState.Off && _layout?.IsAlphabetic == true ? text.ToUpperInvariant() : text;

    void DrawPreview(Canvas canvas, PlacedKey pk, float radius)
    {
        var b = pk.Bounds;
        float width = b.Width() * 1.3f;
        float height = b.Height() * 1.2f;
        float left = Math.Clamp(b.CenterX() - width / 2, 2 * _dp, Width - width - 2 * _dp);
        float top = Math.Max(0, b.Top - height - 6 * _dp);
        var bubble = new RectF(left, top, left + width, top + height);

        _fill.Color = Theme.Popup;
        canvas.DrawRoundRect(bubble, radius, radius, _fill);
        _text.TextSize = 30 * _dp;
        _text.Color = Theme.Text;
        float baseline = bubble.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
        canvas.DrawText(Shifted(pk.Key.Label), bubble.CenterX(), baseline, _text);
    }

    void DrawTrail(Canvas canvas, List<PointF> trail)
    {
        if (trail.Count < 2) return;
        _trailPath.Reset();
        _trailPath.MoveTo(trail[0].X, trail[0].Y);
        for (int i = 1; i < trail.Count; i++) _trailPath.LineTo(trail[i].X, trail[i].Y);
        _trail.Color = Theme.Accent;
        _trail.Alpha = 190;
        canvas.DrawPath(_trailPath, _trail);
    }

    void DrawPopup(Canvas canvas, float radius)
    {
        var popup = _popup!;
        if (popup.Round)
        {
            DrawRoundPopup(canvas, popup);
            return;
        }

        if (popup.Grid != null)
        {
            DrawGridPopup(canvas, popup);
            return;
        }

        _fill.Color = Theme.Popup;
        canvas.DrawRoundRect(popup.Bounds, radius, radius, _fill);

        _text.TextSize = 20 * _dp;
        for (int i = 0; i < popup.Items.Count; i++)
        {
            var cell = new RectF(
                popup.Bounds.Left + i * popup.CellWidth,
                popup.Bounds.Top,
                popup.Bounds.Left + (i + 1) * popup.CellWidth,
                popup.Bounds.Bottom);

            bool selected = i == popup.Selected;
            if (selected)
            {
                _fill.Color = Theme.Accent;
                canvas.DrawRoundRect(cell, radius, radius, _fill);
            }

            _text.Color = selected ? Theme.AccentText : Theme.Text;
            float baseline = cell.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
            canvas.DrawText(popup.Items[i], cell.CenterX(), baseline, _text);
        }
    }

    /// <summary>
    /// Zusatzzeichen wie bei Gboard: helle Karte mit stark gerundeten Ecken und leichtem Schatten, die Zeichen im
    /// Raster, das ausgewählte als Kreis in der Akzentfarbe.
    /// </summary>
    void DrawGridPopup(Canvas canvas, Popup popup)
    {
        var b = popup.Bounds;
        float corner = Math.Min(popup.CellHeight / 2 + PopupPad, b.Height() / 2);
        _shadow.Color = Theme.Popup;
        _shadow.SetShadowLayer(4 * _dp, 0, 1.5f * _dp, Color.Argb(70, 0, 0, 0));
        canvas.DrawRoundRect(b, corner, corner, _shadow);

        float circle = Math.Min(popup.CellWidth, popup.CellHeight) / 2 - 1 * _dp;
        _text.TextSize = popup.CellHeight * 0.46f;
        for (int i = 0; i < popup.Items.Count; i++)
        {
            var cell = popup.Grid![i];
            float cx = popup.AnchorX + cell.Column * popup.CellWidth;
            float cy = popup.GridBottom - (cell.Row + 0.5f) * popup.CellHeight;
            bool selected = i == popup.Selected;
            if (selected)
            {
                _fill.Color = Theme.Accent;
                canvas.DrawCircle(cx, cy, circle, _fill);
            }

            _text.Color = selected ? Theme.AccentText : Theme.Text;
            canvas.DrawText(popup.Items[i], cx, cy - (_text.Descent() + _text.Ascent()) / 2, _text);
        }
    }

    float PopupPad => 6 * _dp;

    /// <summary>
    /// Gboard-Kreis für ein einzelnes Zusatzzeichen: heller Kreis mit leichtem Schatten,
    /// darin ein Kreis in der Akzentfarbe mit dem Zeichen.
    /// </summary>
    void DrawRoundPopup(Canvas canvas, Popup popup)
    {
        var b = popup.Bounds;
        float outer = b.Width() / 2;
        float inner = outer * 0.64f;

        // Schatten auf Hardware-Canvas erst ab Android 9; davor bleibt er einfach weg.
        _shadow.Color = Theme.Popup;
        _shadow.SetShadowLayer(4 * _dp, 0, 1.5f * _dp, Color.Argb(70, 0, 0, 0));
        canvas.DrawCircle(b.CenterX(), b.CenterY(), outer, _shadow);

        _fill.Color = Theme.Accent;
        canvas.DrawCircle(b.CenterX(), b.CenterY(), inner, _fill);

        _text.TextSize = inner * 1.2f;
        _text.Color = Theme.AccentText;
        float baseline = b.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
        canvas.DrawText(popup.Items[0], b.CenterX(), baseline, _text);
    }

    /// <summary>
    /// Eigene durchsichtige Zeichenebene in voller Höhe über der Tastatur (wie AOSP DrawingPreviewPlacerView).
    /// Ist sie gesetzt, zeichnet die Tastatur ihre Popups dort statt über den eigenen Rand hinaus – Android
    /// aktualisiert Bereiche außerhalb eines Views nicht zuverlässig (der obere Teil des Popups fehlte).
    /// </summary>
    public View? PopupLayer { get; set; }

    bool _layerShowsPopup;

    /// <summary>Lage dieser Tastatur in der Zeichenebene.</summary>
    PointF OffsetInPopupLayer()
    {
        var mine = new int[2];
        var layer = new int[2];
        GetLocationInWindow(mine);
        PopupLayer!.GetLocationInWindow(layer);
        return new PointF(mine[0] - layer[0], mine[1] - layer[1]);
    }

    /// <summary>Von der Zeichenebene aufgerufen: aktuellen Popup in deren Koordinaten zeichnen.</summary>
    public void DrawPopupLayer(Canvas canvas)
    {
        if (_popup == null || PopupLayer == null) return;
        var offset = OffsetInPopupLayer();
        canvas.Save();
        canvas.Translate(offset.X, offset.Y);
        DrawPopup(canvas, 7 * _dp);
        canvas.Restore();
    }

    /// <summary>
    /// Obergrenze (in eigenen Koordinaten), bis zu der Popups über den View hinaus zeichnen dürfen:
    /// Die Eltern schneiden ihre Kinder nicht ab (ClipChildren = false), also reicht der Platz bis zum
    /// oberen Rand des äußersten solchen Elternteils – bei zBoard über die Vorschlagsleiste.
    /// </summary>
    float OverflowTop()
    {
        if (PopupLayer != null) return -OffsetInPopupLayer().Y;

        View v = this;
        float offset = 0;
        while (v.Parent is ViewGroup { ClipChildren: false } parent)
        {
            offset += v.Top;
            v = parent;
        }

        return -offset;
    }

    /// <summary>Neu zeichnen, auch den Bereich außerhalb des Views (Popup über der Vorschlagsleiste).</summary>
    void InvalidateWithOverflow()
    {
        Invalidate();
        PopupLayer?.Invalidate();
        View v = this;
        while (v.Parent is ViewGroup { ClipChildren: false } parent) v = parent;
        if (v != this) v.Invalidate();
    }

#if DEBUG || ZBOARD_TESTHOOKS
    (int X, int Y)? _loggedOrigin;
    Java.Lang.Runnable? _positionWatch;

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        // Fenster kann sich verschieben, ohne neu zu zeichnen: Position regelmäßig prüfen
        _positionWatch = new Java.Lang.Runnable(() =>
        {
            LogKeyPositions();
            if (_positionWatch != null) _handler.PostDelayed(_positionWatch, 500);
        });
        _handler.PostDelayed(_positionWatch, 500);
    }

    protected override void OnDetachedFromWindow()
    {
        if (_positionWatch != null) _handler.RemoveCallbacks(_positionWatch);
        _positionWatch = null;
        base.OnDetachedFromWindow();
    }

    /// <summary>
    /// Nur Debug: schreibt die Bildschirmposition jeder Taste ins Log (Tag "zBoardTest"),
    /// damit der Emulator-Test gezielt tippen kann. Format: "KEYS de a@540,1700 Space@540,2100 …".
    /// Neu geschrieben, sobald sich Layout oder Position ändern (z. B. während die Tastatur einblendet).
    /// </summary>
    void LogKeyPositions()
    {
        if (_keys.Count == 0 || !IsLaidOut || Visibility != ViewStates.Visible) return;
        var origin = new int[2];
        GetLocationOnScreen(origin);
        if (_loggedOrigin == (origin[0], origin[1])) return;
        _loggedOrigin = (origin[0], origin[1]);
        var line = new System.Text.StringBuilder("KEYS ").Append(_layout?.Id).Append(' ');
        foreach (var pk in _keys)
        {
            string id = pk.Key.IsCharacter ? pk.Key.Label : pk.Key.Action.ToString();
            line.Append(id).Append('@')
                .Append((int)(origin[0] + pk.Bounds.CenterX())).Append(',')
                .Append((int)(origin[1] + pk.Bounds.CenterY())).Append(' ');
        }

        Android.Util.Log.Info("zBoardTest", line.ToString());

        // Unterkante der Tastatur und die höchste erlaubte Unterkante (Fensterende minus Navigationsleiste)
        var rootOrigin = new int[2];
        RootView!.GetLocationOnScreen(rootOrigin);
        int nav = 0;
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && RootWindowInsets is { } insets)
        {
            nav = insets.GetInsets(WindowInsets.Type.NavigationBars()).Bottom;
        }

        Android.Util.Log.Info("zBoardTest",
            $"BOUNDS {_layout?.Id} bottom={origin[1] + Height} limit={rootOrigin[1] + RootView.Height - nav} nav={nav}");
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
            case MotionEventActions.PointerDown:
                // Schnelles Tippen mit zwei Fingern: die vorherige Taste gilt als losgelassen.
                if (_press != null) Release(e.EventTime);
                Begin(e.GetPointerId(index), e.GetX(index), e.GetY(index), e.EventTime);
                break;

            case MotionEventActions.Move:
                if (_press != null)
                {
                    int i = e.FindPointerIndex(_press.PointerId);
                    if (i >= 0)
                    {
                        // Zwischenpunkte für eine glatte Wischspur
                        for (int h = 0; h < e.HistorySize; h++) Move(e.GetHistoricalX(i, h), e.GetHistoricalY(i, h));
                        Move(e.GetX(i), e.GetY(i));
                    }
                }

                break;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                if (_press != null && e.GetPointerId(index) == _press.PointerId) Release(e.EventTime);
                break;

            case MotionEventActions.Cancel:
                if (_press is { DeleteSwipe: true }) Listener?.OnDeleteSwipeEnd(commit: false);
                CancelPress();
                break;
        }

        return true;
    }

    void Begin(int pointerId, float x, float y, long time)
    {
        var pk = KeyAt(x, y);
        if (pk == null) return;

        _press = new Press(pk, pointerId, x, y);
        Listener?.OnKeyDown(pk.Key, time);

        if (pk.Key.Action == KeyAction.Delete)
        {
            // Löschen erst beim Loslassen oder nach kurzem Halten, damit Wischen nach links möglich ist
            _handler.PostDelayed(_repeatRunnable, RepeatStartMs);
        }
        else if (pk.Key.Alternates.Count > 0 || pk.Key.Action == KeyAction.Space && Languages.Count > 1)
        {
            _handler.PostDelayed(_longPressRunnable, LongPressMs);
        }

        Invalidate();
    }

    void Move(float x, float y)
    {
        var p = _press!;

        if (_popup != null)
        {
            int sel = PopupIndexAt(x, y);
            if (sel != _popup.Selected)
            {
                _popup.Selected = sel;
                InvalidateWithOverflow();
            }

            return;
        }

        switch (p.Key.Key.Action)
        {
            case KeyAction.Space:
                MoveCursor(p, x);
                return;
            case KeyAction.Delete:
                MoveDelete(p, x);
                return;
            case KeyAction.Character:
                MoveCharacter(p, x, y);
                return;
        }
    }

    void MoveCursor(Press p, float x)
    {
        float step = 10 * _dp;
        if (!p.CursorMode && Math.Abs(x - p.StartX) > 16 * _dp)
        {
            p.CursorMode = true;
            _handler.RemoveCallbacks(_longPressRunnable);
            p.CursorAnchorX = x;
            Invalidate();
        }

        if (!p.CursorMode) return;
        int steps = (int)((x - p.CursorAnchorX) / step);
        if (steps == 0) return;
        p.CursorAnchorX += steps * step;
        Listener?.OnCursorMove(steps);
    }

    void MoveDelete(Press p, float x)
    {
        float left = p.StartX - x;
        if (!p.DeleteSwipe)
        {
            if (left < DeleteSwipeStartKeys * _unit || p.RepeatFired) return;
            p.DeleteSwipe = true;
            _handler.RemoveCallbacks(_repeatRunnable);
        }

        int words = left < DeleteSwipeStartKeys * _unit
            ? 0
            : 1 + (int)((left - DeleteSwipeStartKeys * _unit) / (DeleteSwipeKeysPerWord * _unit));
        if (words == p.DeleteWords) return;

        p.DeleteWords = words;
        Listener?.OnDeleteSwipe(words);
        Invalidate();
    }

    void MoveCharacter(Press p, float x, float y)
    {
        if (p.Gliding)
        {
            AddTrailPoint(p, x, y);
            Invalidate();
            return;
        }

        bool canGlide = GlideEnabled && _layout?.IsAlphabetic == true;
        float distance = MathF.Sqrt((x - p.StartX) * (x - p.StartX) + (y - p.StartY) * (y - p.StartY));
        if (canGlide && distance > GlideStartKeys * _unit)
        {
            p.Gliding = true;
            _handler.RemoveCallbacks(_longPressRunnable);
            p.Trail.Add(new PointF(p.StartX, p.StartY));
            AddTrailPoint(p, x, y);
            Invalidate();
            return;
        }

        // Ohne Wischeingabe: Finger rutscht auf eine Nachbartaste, diese Taste gilt.
        if (!canGlide)
        {
            var pk = KeyAt(x, y);
            if (pk != null && pk != p.Key && pk.Key.IsCharacter)
            {
                p.Key = pk;
                (p.TapX, p.TapY) = (x, y);
                _handler.RemoveCallbacks(_longPressRunnable);
                if (pk.Key.Alternates.Count > 0) _handler.PostDelayed(_longPressRunnable, LongPressMs);
                Invalidate();
            }
        }
    }

    void AddTrailPoint(Press p, float x, float y)
    {
        var last = p.Trail[^1];
        if (Math.Abs(last.X - x) + Math.Abs(last.Y - y) >= 2 * _dp) p.Trail.Add(new PointF(x, y));
    }

    void Release(long time)
    {
        var p = _press;
        if (p == null) return;

        _handler.RemoveCallbacks(_longPressRunnable);
        _handler.RemoveCallbacks(_repeatRunnable);
        _press = null;

        var key = p.Key.Key;
        string? output = null;
        bool gesture = false;

        if (_popup != null)
        {
            var popup = _popup;
            _popup = null;
            if (popup.Values is { } languages)
            {
                Listener?.OnLanguageSelected(languages[popup.Selected]);
            }
            else
            {
                output = popup.Items[popup.Selected];
                Listener?.OnText(output);
            }
        }
        else if (p.Gliding)
        {
            gesture = true;
            var path = p.Trail.Select(t => ToUnits(t.X, t.Y)).ToList();
            Listener?.OnGlide(path, _keyCenters);
        }
        else if (p.DeleteSwipe)
        {
            gesture = true;
            Listener?.OnDeleteSwipeEnd(commit: p.DeleteWords > 0);
        }
        else
        {
            switch (key.Action)
            {
                case KeyAction.Character:
                    output = Shifted(key.Label);
                    TapPoint = ToUnits(p.TapX, p.TapY);
                    Listener?.OnText(output);
                    TapPoint = null;
                    break;
                case KeyAction.Space:
                    if (!p.CursorMode)
                    {
                        output = " ";
                        Listener?.OnAction(KeyAction.Space);
                    }

                    break;
                case KeyAction.Delete:
                    if (!p.RepeatFired) Listener?.OnAction(KeyAction.Delete);
                    break;
                default:
                    Listener?.OnAction(key.Action);
                    break;
            }
        }

        Listener?.OnKeyUp(key, output, time, gesture);
        InvalidateWithOverflow();
    }

    void CancelPress()
    {
        _handler.RemoveCallbacks(_longPressRunnable);
        _handler.RemoveCallbacks(_repeatRunnable);
        _press = null;
        _popup = null;
        InvalidateWithOverflow();
    }

    void OnLongPress()
    {
        var p = _press;
        if (p == null || p.Gliding) return;
        if (p.Key.Key.Action == KeyAction.Space)
        {
            if (!p.CursorMode) ShowLanguagePopup(p);
            return;
        }

        if (p.Key.Key.Alternates.Count == 0) return;

        var items = p.Key.Key.Alternates.Select(Shifted).ToList();

        if (items.Count == 1)
        {
            ShowRoundPopup(p, items);
            return;
        }

        ShowGridPopup(p, items);
    }

    /// <summary>
    /// Mehrere Zusatzzeichen wie bei Gboard: Raster mit bis zu fünf Spalten (<see cref="PopupGrid"/>), das erste
    /// und häufigste Zeichen direkt über der Taste und vorausgewählt, weitere Zeilen darüber.
    /// </summary>
    void ShowGridPopup(Press p, List<string> items)
    {
        var b = p.Key.Bounds;
        float margin = 2 * _dp, pad = PopupPad;
        float cellW = Math.Max(b.Width() + 2 * GapX, 36 * _dp);
        float cellH = Math.Max(cellW, 42 * _dp);
        int leftRoom = (int)MathF.Floor((b.CenterX() - cellW / 2 - pad - margin) / cellW);
        int rightRoom = (int)MathF.Floor((Width - margin - pad - b.CenterX() - cellW / 2) / cellW);
        var grid = PopupGrid.Place(items.Count, leftRoom, rightRoom);
        int minCol = grid.Min(c => c.Column), maxCol = grid.Max(c => c.Column), rows = grid.Max(c => c.Row) + 1;

        // Waagerecht: an der Taste ausrichten, nur bei zu wenig Platz (z. B. Einhandmodus) verschieben
        float anchorX = b.CenterX();
        float left = anchorX + (minCol - 0.5f) * cellW - pad;
        float right = anchorX + (maxCol + 0.5f) * cellW + pad;
        anchorX += Math.Max(0, margin - left) - Math.Max(0, right - (Width - margin));

        // Senkrecht: knapp über der Taste, notfalls (wenig Platz nach oben) über die Tasten hinein
        float bottom = b.Top - 4 * _dp;
        float top = bottom - rows * cellH - 2 * pad;
        float minTop = OverflowTop() + margin;
        if (top < minTop)
        {
            bottom += minTop - top;
            top = minTop;
        }

        float boundsLeft = anchorX + (minCol - 0.5f) * cellW - pad;
        float boundsRight = anchorX + (maxCol + 0.5f) * cellW + pad;
        _popup = new Popup(items, new RectF(boundsLeft, top, boundsRight, bottom), cellW)
        {
            Grid = grid,
            AnchorX = anchorX,
            GridBottom = bottom - pad,
            CellHeight = cellH,
            Source = p.Key,
        };
        _popup.Selected = 0;
        PerformKeyHaptic();
        InvalidateWithOverflow();
    }

    /// <summary>Runder Popup wie bei Gboard, mittig über der Taste und knapp darüber.</summary>
    void ShowRoundPopup(Press p, List<string> items)
    {
        var b = p.Key.Bounds;
        float size = Math.Clamp(b.Width() * 2f, 60 * _dp, b.Height() * 1.6f);
        float cx = Math.Clamp(b.CenterX(), size / 2 + 2 * _dp, Width - size / 2 - 2 * _dp);
        float top = Math.Max(OverflowTop() + 2 * _dp, b.Top - size - 6 * _dp);

        _popup = new Popup(items, new RectF(cx - size / 2, top, cx + size / 2, top + size), size)
        {
            Round = true,
            Source = p.Key,
        };
        PerformKeyHaptic();
        InvalidateWithOverflow();
    }

    /// <summary>Sprachauswahl über der Leertaste; Finger zur gewünschten Sprache schieben und loslassen.</summary>
    void ShowLanguagePopup(Press p)
    {
        if (Languages.Count < 2) return;
        var b = p.Key.Bounds;
        var names = Languages.Select(l => l.Name).ToList();
        _text.TextSize = 20 * _dp;
        float cell = Math.Max(64 * _dp, names.Max(n => _text.MeasureText(n)) + 24 * _dp);
        float total = cell * names.Count;
        float left = Math.Max(4 * _dp, Math.Min(b.CenterX() - total / 2, Width - 4 * _dp - total));
        float height = (_rowHeight > 0 ? _rowHeight : KeyHeight) - 4 * _dp;
        float top = Math.Max(0, b.Top - height - 4 * _dp);

        _popup = new Popup(names, new RectF(left, top, left + total, top + height), cell,
            [.. Languages.Select(l => l.Code)]);
        _popup.Selected = Math.Max(0, Languages.ToList().FindIndex(l => l.Code == CurrentLanguage));
        PerformKeyHaptic();
        Invalidate();
    }

    void OnRepeat()
    {
        if (_press is not { Key.Key.Action: KeyAction.Delete, DeleteSwipe: false } p) return;
        p.RepeatFired = true;
        Listener?.OnAction(KeyAction.Delete);
        _handler.PostDelayed(_repeatRunnable, RepeatIntervalMs);
    }

    int PopupIndexAt(float x, float y)
    {
        var popup = _popup!;
        if (popup.Grid != null)
        {
            return PopupGrid.IndexAt(popup.Grid, (x - popup.AnchorX) / popup.CellWidth,
                (popup.GridBottom - y) / popup.CellHeight);
        }

        int i = (int)((x - popup.Bounds.Left) / popup.CellWidth);
        return Math.Clamp(i, 0, popup.Items.Count - 1);
    }

    PlacedKey? KeyAt(float x, float y)
    {
        PlacedKey? nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var pk in _keys)
        {
            var b = pk.Bounds;
            if (x >= b.Left - GapX && x <= b.Right + GapX && y >= b.Top - GapY && y <= b.Bottom + GapY)
            {
                return pk;
            }

            float dx = x - b.CenterX();
            float dy = y - b.CenterY();
            float d = dx * dx + dy * dy;
            if (d < nearestDist)
            {
                nearestDist = d;
                nearest = pk;
            }
        }

        return nearest;
    }
}
