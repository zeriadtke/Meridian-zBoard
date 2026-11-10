using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using ZBoard.Keyboard;
using ZBoard.Localization;

namespace ZBoard.Emoji;

public enum EmojiPanelAction
{
    Close,
    Search,
    Delete,
}

public interface IEmojiPanelListener
{
    void OnEmojiChosen(string emoji);

    void OnEmojiPanelAction(EmojiPanelAction action);
}

/// <summary>
/// Emoji-Tastatur: Raster der aktuellen Kategorie (scrollbar mit Schwung), unten die Kategorien-Leiste
/// mit ABC, Suche, „Zuletzt verwendet“ und Löschen. Langer Druck zeigt Hauttöne.
/// </summary>
public sealed class EmojiPanelView : View
{
    const int RecentGroup = -1;
    const int LongPressMs = 350;

    readonly float _dp;
    readonly Paint _text = new(PaintFlags.AntiAlias);
    readonly Paint _fill = new(PaintFlags.AntiAlias);
    readonly OverScroller _scroller;
    readonly Handler _handler = new(Looper.MainLooper!);
    readonly Java.Lang.Runnable _longPressRunnable;
    readonly List<(RectF Bounds, string Id, string Label)> _barTargets = [];

    EmojiCatalog? _catalog;
    readonly Dictionary<int, List<EmojiEntry>> _visibleByGroup = [];
    Func<IReadOnlyList<string>> _recent = () => [];
    int _group;
    float _scrollY;
    VelocityTracker? _velocity;
    float _downX;
    float _downY;
    float _lastY;
    bool _dragging;
    int _pressedIndex = -1;
    int _pressedBar = -1;
    List<string>? _variants;
    RectF? _variantBounds;
    int _variantSelected;

    public EmojiPanelView(Context context) : base(context)
    {
        _dp = context.Resources!.DisplayMetrics!.Density;
        _text.TextAlign = Paint.Align.Center;
        _scroller = new OverScroller(context);
        _longPressRunnable = new Java.Lang.Runnable(OnLongPress);
    }

    public IEmojiPanelListener? Listener { get; set; }

    public KeyboardTheme Theme
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = KeyboardTheme.Light;

    /// <summary>Höhe des Panels; die Tastatur soll beim Umschalten nicht springen.</summary>
    public int PanelHeight { get; set; }

    float BarHeight => 44 * _dp;
    float CellSize => Width / (float)Columns;
    int Columns => Math.Max(6, (int)(Width / (46 * _dp)));
    float GridHeight => Height - BarHeight;

    /// <summary>Katalog setzen; Emoji, die die Schrift des Geräts nicht darstellen kann, werden ausgeblendet.</summary>
    public void SetCatalog(EmojiCatalog catalog, Func<IReadOnlyList<string>> recent)
    {
        _catalog = catalog;
        _recent = recent;
        _visibleByGroup.Clear();
        var probe = new Paint();
        foreach (var entry in catalog.Entries)
        {
            if (!probe.HasGlyph(entry.Emoji)) continue;
            if (!_visibleByGroup.TryGetValue(entry.Group, out var list))
            {
                list = [];
                _visibleByGroup[entry.Group] = list;
            }

            list.Add(entry);
        }

        _group = _recent().Count > 0 ? RecentGroup : 0;
        _scrollY = 0;
        Invalidate();
    }

    /// <summary>Beim Öffnen: mit „Zuletzt verwendet“ beginnen, wenn vorhanden.</summary>
    public void ShowStart()
    {
        _group = _recent().Count > 0 ? RecentGroup : 0;
        _scrollY = 0;
        _scroller.ForceFinished(true);
        Invalidate();
    }

    IReadOnlyList<string> CurrentItems() =>
        _group == RecentGroup
            ? _recent()
            : _visibleByGroup.TryGetValue(_group, out var list) ? list.Select(e => e.Emoji).ToList() : [];

    bool HasSkinTones(string emoji) =>
        _catalog?.Entries.FirstOrDefault(e => e.Emoji == emoji)?.SkinTones == true;

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int height = PanelHeight > 0 ? PanelHeight : (int)(4 * 54 * _dp + 12 * _dp);
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), height);
    }

    // ---------- Zeichnen ----------

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // Nur die eigene Fläche füllen: Die Eltern schneiden nicht ab (Zahlen-Popup), DrawColor würde die Leiste übermalen.
        _fill.Color = Theme.Background;
        canvas.DrawRect(0, 0, Width, Height, _fill);
        DrawGrid(canvas);
        DrawBar(canvas);
        if (_variants != null) DrawVariants(canvas);
    }

    void DrawGrid(Canvas canvas)
    {
        var items = CurrentItems();
        float cell = CellSize;
        int columns = Columns;

        canvas.Save();
        canvas.ClipRect(0, 0, Width, GridHeight);

        if (items.Count == 0)
        {
            _text.TextSize = 14 * _dp;
            _text.Color = Theme.HintText;
            canvas.DrawText(L.T("Noch keine Emoji verwendet", "No recently used emoji"), Width / 2f, GridHeight / 2f, _text);
        }

        _text.TextSize = cell * 0.6f;
        _text.Color = Theme.Text;
        int firstRow = Math.Max(0, (int)(_scrollY / cell));
        int lastRow = (int)((_scrollY + GridHeight) / cell);
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int i = row * columns + col;
                if (i >= items.Count) break;

                float left = col * cell;
                float top = row * cell - _scrollY;
                if (i == _pressedIndex && _variants == null)
                {
                    _fill.Color = Theme.KeyPressed;
                    canvas.DrawRoundRect(new RectF(left + 2, top + 2, left + cell - 2, top + cell - 2), 8 * _dp, 8 * _dp, _fill);
                }

                float baseline = top + cell / 2 - (_text.Descent() + _text.Ascent()) / 2;
                canvas.DrawText(items[i], left + cell / 2, baseline, _text);
            }
        }

        canvas.Restore();
    }

    void DrawBar(Canvas canvas)
    {
        _barTargets.Clear();
        float top = GridHeight;
        _fill.Color = Theme.SpecialKey;
        canvas.DrawRect(0, top, Width, Height, _fill);

        List<(string Id, string Label)> items = [("abc", "ABC"), ("search", "🔍"), ("recent", "🕘")];
        for (int g = 0; g < EmojiCatalog.GroupIcons.Length; g++) items.Add(($"g{g}", EmojiCatalog.GroupIcons[g]));
        items.Add(("delete", "⌫"));

        float w = Width / (float)items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            var bounds = new RectF(i * w, top, (i + 1) * w, Height);
            _barTargets.Add((bounds, items[i].Id, items[i].Label));

            bool active = items[i].Id == (_group == RecentGroup ? "recent" : $"g{_group}");
            if (active || i == _pressedBar)
            {
                _fill.Color = active ? Theme.Key : Theme.KeyPressed;
                canvas.DrawRoundRect(new RectF(bounds.Left + 2 * _dp, top + 4 * _dp, bounds.Right - 2 * _dp, Height - 4 * _dp), 8 * _dp, 8 * _dp, _fill);
            }

            _text.TextSize = (items[i].Id == "abc" ? 13 : 18) * _dp;
            _text.Color = Theme.Text;
            float baseline = bounds.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
            canvas.DrawText(items[i].Label, bounds.CenterX(), baseline, _text);
        }
    }

    void DrawVariants(Canvas canvas)
    {
        var bounds = _variantBounds!;
        _fill.Color = Theme.Popup;
        canvas.DrawRoundRect(bounds, 8 * _dp, 8 * _dp, _fill);
        float cell = bounds.Width() / _variants!.Count;
        _text.TextSize = cell * 0.6f;
        for (int i = 0; i < _variants.Count; i++)
        {
            var r = new RectF(bounds.Left + i * cell, bounds.Top, bounds.Left + (i + 1) * cell, bounds.Bottom);
            if (i == _variantSelected)
            {
                _fill.Color = Theme.Accent;
                canvas.DrawRoundRect(r, 8 * _dp, 8 * _dp, _fill);
            }

            float baseline = r.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
            canvas.DrawText(_variants[i], r.CenterX(), baseline, _text);
        }
    }

    // ---------- Berührung ----------

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        float x = e.GetX();
        float y = e.GetY();

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _scroller.ForceFinished(true);
                _velocity?.Recycle();
                _velocity = VelocityTracker.Obtain();
                _velocity!.AddMovement(e);
                _downX = x;
                _downY = _lastY = y;
                _dragging = false;
                if (y >= GridHeight)
                {
                    _pressedBar = _barTargets.FindIndex(t => t.Bounds.Contains(x, y));
                }
                else
                {
                    _pressedIndex = IndexAt(x, y);
                    if (_pressedIndex >= 0) _handler.PostDelayed(_longPressRunnable, LongPressMs);
                }

                Invalidate();
                break;

            case MotionEventActions.Move:
                _velocity?.AddMovement(e);
                if (_variants != null)
                {
                    float cell = _variantBounds!.Width() / _variants.Count;
                    _variantSelected = Math.Clamp((int)((x - _variantBounds.Left) / cell), 0, _variants.Count - 1);
                    Invalidate();
                    break;
                }

                if (_pressedBar < 0 && !_dragging && Math.Abs(y - _downY) > 8 * _dp)
                {
                    _dragging = true;
                    _pressedIndex = -1;
                    _handler.RemoveCallbacks(_longPressRunnable);
                }

                if (_dragging)
                {
                    ScrollTo(_scrollY - (y - _lastY));
                }

                _lastY = y;
                break;

            case MotionEventActions.Up:
                _handler.RemoveCallbacks(_longPressRunnable);
                if (_variants != null)
                {
                    Listener?.OnEmojiChosen(_variants[_variantSelected]);
                    _variants = null;
                }
                else if (_dragging)
                {
                    _velocity?.AddMovement(e);
                    _velocity?.ComputeCurrentVelocity(1000);
                    int vy = (int)(_velocity?.YVelocity ?? 0);
                    _scroller.Fling(0, (int)_scrollY, 0, -vy, 0, 0, 0, (int)MaxScroll());
                    PostInvalidateOnAnimation();
                }
                else if (_pressedBar >= 0 && _barTargets.FindIndex(t => t.Bounds.Contains(x, y)) == _pressedBar)
                {
                    OnBar(_barTargets[_pressedBar].Id);
                }
                else if (_pressedIndex >= 0 && _pressedIndex == IndexAt(x, y))
                {
                    Listener?.OnEmojiChosen(CurrentItems()[_pressedIndex]);
                }

                _pressedIndex = -1;
                _pressedBar = -1;
                _velocity?.Recycle();
                _velocity = null;
                Invalidate();
                break;

            case MotionEventActions.Cancel:
                _handler.RemoveCallbacks(_longPressRunnable);
                _variants = null;
                _pressedIndex = -1;
                _pressedBar = -1;
                Invalidate();
                break;
        }

        return true;
    }

    public override void ComputeScroll()
    {
        base.ComputeScroll();
        if (!_scroller.ComputeScrollOffset()) return;
        ScrollTo(_scroller.CurrY);
        PostInvalidateOnAnimation();
    }

    void ScrollTo(float y)
    {
        _scrollY = Math.Clamp(y, 0, MaxScroll());
        Invalidate();
    }

    float MaxScroll()
    {
        int rows = (CurrentItems().Count + Columns - 1) / Columns;
        return Math.Max(0, rows * CellSize - GridHeight);
    }

    int IndexAt(float x, float y)
    {
        if (y < 0 || y >= GridHeight) return -1;
        int col = (int)(x / CellSize);
        int row = (int)((y + _scrollY) / CellSize);
        int i = row * Columns + col;
        return col < Columns && i < CurrentItems().Count ? i : -1;
    }

    void OnBar(string id)
    {
        switch (id)
        {
            case "abc":
                Listener?.OnEmojiPanelAction(EmojiPanelAction.Close);
                break;
            case "search":
                Listener?.OnEmojiPanelAction(EmojiPanelAction.Search);
                break;
            case "delete":
                Listener?.OnEmojiPanelAction(EmojiPanelAction.Delete);
                break;
            case "recent":
                SelectGroup(RecentGroup);
                break;
            default:
                if (id.StartsWith('g') && int.TryParse(id[1..], out var g)) SelectGroup(g);
                break;
        }
    }

    void SelectGroup(int group)
    {
        _group = group;
        _scroller.ForceFinished(true);
        _scrollY = 0;
        Invalidate();
    }

    void OnLongPress()
    {
        if (_pressedIndex < 0) return;
        var emoji = CurrentItems()[_pressedIndex];
        if (!HasSkinTones(emoji)) return;

        var probe = new Paint();
        _variants = [.. EmojiCatalog.SkinToneVariants(emoji).Where(v => probe.HasGlyph(v))];
        if (_variants.Count <= 1)
        {
            _variants = null;
            return;
        }

        float cell = CellSize;
        int col = _pressedIndex % Columns;
        int row = _pressedIndex / Columns;
        float width = cell * _variants.Count;
        float left = Math.Clamp(col * cell + cell / 2 - width / 2, 0, Width - width);
        float top = Math.Max(0, row * cell - _scrollY - cell);
        _variantBounds = new RectF(left, top, left + width, top + cell);
        _variantSelected = 0;
        PerformHapticFeedback(FeedbackConstants.LongPress);
        Invalidate();
    }
}
