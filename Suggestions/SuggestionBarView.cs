using Android.Content;
using Android.Graphics;
using Android.Views;
using ZBoard.Keyboard;

namespace ZBoard.Suggestions;

/// <param name="Display">Angezeigter Text (z. B. mit Anführungszeichen).</param>
/// <param name="Word">Wort, das beim Antippen eingefügt wird.</param>
/// <param name="Emphasized">Fett darstellen (wird bei Leertaste eingefügt).</param>
/// <param name="IsLiteral">Der Nutzer wählt sein getipptes Wort statt der Korrektur.</param>
/// <param name="IsEmail">E-Mail-Adresse: ersetzt das angefangene Adress-Stück, ohne Leerzeichen dahinter.</param>
/// <param name="IsPhrase">Mehrere Wörter aus einer gelernten Wortfolge (Wort plus die nächsten).</param>
/// <param name="IsReplacement">Ersetzt ein angetipptes Wort im Text statt das Wort in Bearbeitung.</param>
public sealed record SuggestionSlot(
    string Display, string Word, bool Emphasized, bool IsLiteral, bool IsEmail = false, bool IsPhrase = false,
    bool IsReplacement = false);

/// <summary>Eintrag der Werkzeugleiste (Material-Symbol). <paramref name="Trailing"/>: rechts im Kreis (Mikrofon).</summary>
public sealed record ToolbarItem(string Id, int Icon, bool Trailing = false);

/// <summary>
/// Leiste über der Tastatur. Zeigt je nach Lage drei Vorschläge, die Werkzeugleiste (mit dem zuletzt kopierten Text),
/// die Emoji-Suche oder eine Statuszeile (Spracheingabe).
/// </summary>
public sealed class SuggestionBarView : View
{
    public const string ClipChipId = "clip-chip";
    public const string SearchPillId = "emoji-search";
    public const string StatusId = "status";
    public const string RemoveId = "remove";
    public const string RemoveCancelId = "remove-cancel";

    /// <summary>Präfix der Auswahl-Einträge ("choice:0", "choice:1" …) und das Schließen-Kreuz der Auswahl.</summary>
    public const string ChoicePrefix = "choice:";
    public const string ChoiceCloseId = "choice-close";

    enum Mode
    {
        Suggestions,
        Toolbar,
        EmojiSearch,
        Status,

        /// <summary>Nach langem Druck auf eine gemerkte E-Mail-Adresse: Entfernen bestätigen.</summary>
        Remove,

        /// <summary>Einträge zur Auswahl (Plugin-Aktionen, Rückgängig) und ein Kreuz zum Schließen.</summary>
        Choices,
    }

    readonly float _dp;
    readonly Icons _icons;
    readonly Paint _text = new(PaintFlags.AntiAlias);
    readonly Paint _line = new(PaintFlags.AntiAlias);
    readonly Paint _fill = new(PaintFlags.AntiAlias);
    readonly SuggestionSlot?[] _slots = new SuggestionSlot?[3];
    readonly List<(RectF Bounds, string Id, string Label, int Icon)> _targets = [];

    Mode _mode = Mode.Toolbar;
    string? _clipChip;
    string _searchQuery = string.Empty;
    IReadOnlyList<string> _searchResults = [];
    string _status = string.Empty;
    int _pressed = -1;
    long _downTime;
    int _pressGeneration;
    bool _longPressFired;
    string _removeLabel = string.Empty;
    IReadOnlyList<string> _choices = [];
    int _statusIcon = Resource.Drawable.ms_mic;

    public SuggestionBarView(Context context) : base(context)
    {
        _dp = context.Resources!.DisplayMetrics!.Density;
        _icons = new Icons(context);
        _text.TextAlign = Paint.Align.Center;
        _line.StrokeWidth = 1 * _dp;
    }

    /// <summary>Vorschlag angetippt (Slot, Touch-Down-Zeit, Touch-Up-Zeit in Uptime-ms).</summary>
    public event Action<SuggestionSlot, long, long>? SuggestionPicked;

    /// <summary>Langer Druck auf einen Vorschlag (nur E-Mail-Adressen, die sich entfernen lassen).</summary>
    public event Action<SuggestionSlot>? SuggestionLongPressed;

    /// <summary>Werkzeug, Kopier-Vorschlag, Suchfeld oder Statuszeile angetippt (siehe Konstanten).</summary>
    public event Action<string>? ToolbarPicked;

    /// <summary>Emoji aus der Suchzeile angetippt.</summary>
    public event Action<string>? EmojiPicked;

    public KeyboardTheme Theme
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = KeyboardTheme.Light;

    /// <summary>Werkzeuge, die bei leerer Leiste erscheinen.</summary>
    public IReadOnlyList<ToolbarItem> ToolbarItems { get; set; } = [];

    /// <summary>Setzt die Vorschläge links, Mitte, rechts. Sind alle leer, erscheint die Werkzeugleiste.</summary>
    public void SetSlots(SuggestionSlot? left, SuggestionSlot? middle, SuggestionSlot? right)
    {
        _slots[0] = left;
        _slots[1] = middle;
        _slots[2] = right;
        _mode = left == null && middle == null && right == null ? Mode.Toolbar : Mode.Suggestions;
        Reset();
#if DEBUG || ZBOARD_TESTHOOKS
        Android.Util.Log.Info("zBoardTest", "SUGGESTIONS " + string.Join("|", _slots.Select(s => s?.Word ?? "")));
#endif
    }

    /// <summary>Werkzeugleiste, optional mit dem zuletzt kopierten Text als Vorschlag.</summary>
    public void Clear(string? clipChip = null)
    {
        Array.Clear(_slots);
        _clipChip = clipChip;
        _mode = Mode.Toolbar;
        Reset();
    }

    public void SetEmojiSearch(string query, IReadOnlyList<string> results)
    {
        _searchQuery = query;
        _searchResults = results;
        _mode = Mode.EmojiSearch;
        Reset();
    }

    /// <summary>Fragt, ob <paramref name="label"/> entfernt werden soll: links Entfernen, rechts Abbrechen.</summary>
    public void SetRemoveConfirm(string label)
    {
        _removeLabel = label;
        _mode = Mode.Remove;
        Reset();
    }

    /// <summary>Einträge zur Auswahl; Antippen meldet <see cref="ChoicePrefix"/> + Index über <see cref="ToolbarPicked"/>.</summary>
    public void SetChoices(IReadOnlyList<string> labels)
    {
        _choices = labels;
        _mode = Mode.Choices;
        Reset();
    }

    public void SetStatus(string status, int icon = 0)
    {
        _status = status;
        _statusIcon = icon != 0 ? icon : Resource.Drawable.ms_mic;
        _mode = Mode.Status;
        Reset();
    }

    void Reset()
    {
        _pressed = -1;
        _targets.Clear();
        Invalidate();
    }

    // ---------- Zeichnen ----------

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.DrawColor(Theme.Background);
        _targets.Clear();

        switch (_mode)
        {
            case Mode.Suggestions:
                LayoutSuggestions();
                break;
            case Mode.Toolbar:
                LayoutToolbar();
                break;
            case Mode.EmojiSearch:
                LayoutEmojiSearch();
                break;
            case Mode.Status:
                _targets.Add((new RectF(0, 0, Width, Height), StatusId, _status, _statusIcon));
                break;
            case Mode.Choices:
                LayoutChoices();
                break;
            case Mode.Remove:
                _targets.Add((new RectF(0, 0, Width * 0.7f, Height), RemoveId, "🗑 " + _removeLabel, 0));
                _targets.Add((new RectF(Width * 0.7f, 0, Width, Height), RemoveCancelId, Localization.L.T("Abbrechen", "Cancel"), 0));
                break;
        }

        for (int i = 0; i < _targets.Count; i++) DrawTarget(canvas, i);
    }

    void LayoutSuggestions()
    {
        // E-Mail-Adressen sind lang: die vorhandenen Vorschläge teilen sich die ganze Breite (eine Adresse = volle Breite)
        if (_slots.Any(s => s?.IsEmail == true))
        {
            var present = Enumerable.Range(0, 3).Where(i => _slots[i] != null).ToList();
            float each = Width / (float)present.Count;
            for (int n = 0; n < present.Count; n++)
            {
                int i = present[n];
                _targets.Add((new RectF(n * each, 0, (n + 1) * each, Height), $"slot{i}", _slots[i]!.Display, 0));
            }

            return;
        }

        // Eine Wortfolge rechts bekommt mehr Platz, damit sie lesbar bleibt
        float[] share = _slots[2]?.IsPhrase == true ? [0.27f, 0.31f, 0.42f] : [1 / 3f, 1 / 3f, 1 / 3f];
        float x = 0;
        for (int i = 0; i < 3; i++)
        {
            float w = Width * share[i];
            if (_slots[i] is { } slot) _targets.Add((new RectF(x, 0, x + w, Height), $"slot{i}", slot.Display, 0));
            x += w;
        }
    }

    void LayoutToolbar()
    {
        // Gboard: gleich breite Felder, das Mikrofon ganz rechts
        float slot = Width / 5f;
        float right = Width;
        foreach (var item in ToolbarItems.Where(t => t.Trailing))
        {
            _targets.Add((new RectF(right - slot, 0, right, Height), item.Id, string.Empty, item.Icon));
            right -= slot;
        }

        float x = 0;
        if (_clipChip != null)
        {
            float chipWidth = Width * 0.45f;
            _targets.Add((new RectF(0, 0, chipWidth, Height), ClipChipId, Shorten(_clipChip), Resource.Drawable.ms_content_paste));
            x = chipWidth;
        }

        var leading = ToolbarItems.Where(t => !t.Trailing).ToList();
        if (leading.Count == 0) return;
        float w = Math.Min(slot, (right - x) / leading.Count);
        foreach (var item in leading)
        {
            _targets.Add((new RectF(x, 0, x + w, Height), item.Id, string.Empty, item.Icon));
            x += w;
        }
    }

    void LayoutChoices()
    {
        float close = 48 * _dp;
        if (_choices.Count > 0)
        {
            float w = (Width - close) / Math.Min(_choices.Count, 4);
            for (int i = 0; i < Math.Min(_choices.Count, 4); i++)
            {
                _targets.Add((new RectF(i * w, 0, (i + 1) * w, Height), ChoicePrefix + i, _choices[i], 0));
            }
        }

        _targets.Add((new RectF(Width - close, 0, Width, Height), ChoiceCloseId, "✕", 0));
    }

    void LayoutEmojiSearch()
    {
        float pillWidth = Width * 0.35f;
        _targets.Add((new RectF(0, 0, pillWidth, Height), SearchPillId, _searchQuery + "▏", Resource.Drawable.ms_search));
        float cell = 44 * _dp;
        float x = pillWidth;
        foreach (var emoji in _searchResults)
        {
            if (x + cell > Width) break;
            _targets.Add((new RectF(x, 0, x + cell, Height), "emoji:" + emoji, emoji, 0));
            x += cell;
        }
    }

    void DrawTarget(Canvas canvas, int index)
    {
        var (bounds, id, label, icon) = _targets[index];
        bool pressed = index == _pressed;

        bool pill = id is ClipChipId or SearchPillId or RemoveId || id.StartsWith(ChoicePrefix, StringComparison.Ordinal);
        if (pill)
        {
            _fill.Color = pressed ? Theme.KeyPressed : Theme.Key;
            var r = new RectF(bounds.Left + 6 * _dp, bounds.Top + 6 * _dp, bounds.Right - 6 * _dp, bounds.Bottom - 6 * _dp);
            canvas.DrawRoundRect(r, r.Height() / 2, r.Height() / 2, _fill);
        }
        else if (_mode == Mode.Toolbar && label.Length == 0)
        {
            // Symbol: runde Fläche beim Drücken, das Mikrofon dauerhaft im Kreis
            bool trailing = ToolbarItems.Any(t => t.Id == id && t.Trailing);
            if (pressed || trailing)
            {
                _fill.Color = pressed ? Theme.KeyPressed : Theme.Key;
                canvas.DrawCircle(bounds.CenterX(), bounds.CenterY(), Math.Min(bounds.Height(), bounds.Width()) * 0.42f, _fill);
            }

            _icons.Draw(canvas, icon, bounds.CenterX(), bounds.CenterY(), 24 * _dp, Theme.Text);
            return;
        }
        else if (pressed)
        {
            _fill.Color = Theme.KeyPressed;
            canvas.DrawRect(bounds, _fill);
        }

        if (_mode == Mode.Suggestions && index > 0)
        {
            _line.Color = Theme.HintText;
            canvas.DrawLine(bounds.Left, Height * 0.3f, bounds.Left, Height * 0.7f, _line);
        }

        bool emphasized = id.StartsWith("slot") && _slots[id[^1] - '0']?.Emphasized == true;
        _text.SetTypeface(emphasized ? Typeface.DefaultBold : Typeface.Default);
        _text.Color = _mode == Mode.Status ? Theme.Accent : Theme.Text;
        _text.TextSize = (id.StartsWith("emoji:") || _mode == Mode.Toolbar && !pill ? 20 : 17) * _dp;

        float iconSize = icon != 0 ? 20 * _dp : 0;
        float iconGap = icon != 0 ? 6 * _dp : 0;
        float available = bounds.Width() - 24 * _dp - iconSize - iconGap;
        float measured = _text.MeasureText(label);
        if (measured > available) _text.TextSize *= available / measured;
        measured = _text.MeasureText(label);

        float total = iconSize + iconGap + measured;
        float textCenter = bounds.CenterX() + (iconSize + iconGap) / 2;
        if (icon != 0)
        {
            var iconColor = _mode == Mode.Status ? Theme.Accent : Theme.Text;
            _icons.Draw(canvas, icon, bounds.CenterX() - total / 2 + iconSize / 2, bounds.CenterY(), iconSize, iconColor);
        }

        float baseline = bounds.CenterY() - (_text.Descent() + _text.Ascent()) / 2;
        canvas.DrawText(label, textCenter, baseline, _text);
    }

    static string Shorten(string text)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 24 ? oneLine : oneLine[..23] + "…";
    }

    // ---------- Berührung ----------

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        int index = _targets.FindIndex(t => t.Bounds.Contains(e.GetX(), e.GetY()));

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _pressed = index;
                _downTime = e.EventTime;
                _longPressFired = false;
                int generation = ++_pressGeneration;
                if (index >= 0 && _mode == Mode.Suggestions) PostDelayed(() => OnLongPress(generation, index), ViewConfiguration.LongPressTimeout);
                Invalidate();
                break;

            case MotionEventActions.Move:
                if (_pressed >= 0 && index != _pressed)
                {
                    _pressed = -1;
                    Invalidate();
                }

                break;

            case MotionEventActions.Up:
                int picked = _pressed;
                _pressed = -1;
                _pressGeneration++;
                Invalidate();
                if (picked >= 0 && picked == index && !_longPressFired) Dispatch(_targets[picked].Id, e.EventTime);
                break;

            case MotionEventActions.Cancel:
                _pressGeneration++;
                _pressed = -1;
                Invalidate();
                break;
        }

        return true;
    }

    void OnLongPress(int generation, int index)
    {
        if (generation != _pressGeneration || _pressed != index || index >= _targets.Count) return;
        string id = _targets[index].Id;
        if (!id.StartsWith("slot") || _slots[id[^1] - '0'] is not { IsEmail: true } slot) return;
        if (slot.Display != slot.Word) return; // Anbieter-Ergänzung ("@gmail.com"), nichts Gemerktes
        _longPressFired = true;
        _pressed = -1;
        PerformHapticFeedback(FeedbackConstants.LongPress);
        SuggestionLongPressed?.Invoke(slot);
    }

    void Dispatch(string id, long upTime)
    {
        if (id.StartsWith("slot") && _slots[id[^1] - '0'] is { } slot)
        {
            SuggestionPicked?.Invoke(slot, _downTime, upTime);
        }
        else if (id.StartsWith("emoji:"))
        {
            EmojiPicked?.Invoke(id["emoji:".Length..]);
        }
        else
        {
            ToolbarPicked?.Invoke(id);
        }
    }
}
