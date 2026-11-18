using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using ZBoard.Keyboard;
using ZBoard.Settings;

namespace ZBoard.Ime;

/// <summary>
/// Schwebende Tastatur und geteiltes Layout wie bei Gboard.
/// <list type="bullet">
/// <item>Schwebend: Die Tastatur wird schmaler, bekommt runde Ecken und unten einen Griff, mit dem sie sich frei
/// verschieben lässt. Die App darunter wird nicht verkleinert (sie sieht keinen Platzbedarf der Tastatur), bleibt
/// außerhalb der Tastatur bedienbar. Ein- und ausschalten über die Werkzeugleiste.</item>
/// <item>Geteilt: Auf breiten Bildschirmen (ab 600 dp: Tablets, aufgeklappte Foldables) rücken die Tastenhälften
/// an die Ränder, damit sie mit den Daumen erreichbar sind.</item>
/// </list>
/// </summary>
public sealed partial class ZBoardImeService
{
    const float FloatingWidth = 0.78f;
    const float FloatingMaxWidthDp = 460;
    const float SplitGap = 0.3f;
    const int WideScreenDp = 600;

    FloatingHandle? _floatingHandle;

    /// <summary>Schwebend nur mit dem durchsichtigen Fenster in voller Höhe (nicht im Vollbildmodus).</summary>
    bool Floating => _settings.Floating && UsePopupSpace;

    /// <summary>Lage und Größe der Tastatur im Fenster: unten über die ganze Breite oder schwebend.</summary>
    void ApplyFloating()
    {
        var root = _inputRoot;
        if (root?.Parent is not FrameLayout frame) return;
        float dp = Resources!.DisplayMetrics!.Density;
        bool floating = Floating;

        if (floating)
        {
            int screen = Resources.DisplayMetrics.WidthPixels;
            int width = (int)Math.Min(screen * FloatingWidth, FloatingMaxWidthDp * dp);
            var lp = new FrameLayout.LayoutParams(width, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Left);
            root.LayoutParameters = lp;
            PlaceFloating();
        }
        else
        {
            root.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom);
            root.TranslationX = 0;
            root.TranslationY = 0;
        }

        _floatingHandle?.Visibility = floating ? ViewStates.Visible : ViewStates.Gone;
        root.ClipToOutline = floating;
        root.OutlineProvider = floating ? new RoundedOutline(16 * dp) : ViewOutlineProvider.Background;
        root.Elevation = floating ? 8 * dp : 0;
        RefreshSystemBarPadding();
        ApplyOneHanded();
        ApplySplit();
        ApplyNavigationBarStyle();
    }

    /// <summary>Gespeicherte Lage (Anteile des freien Platzes) auf den aktuellen Bildschirm übertragen.</summary>
    void PlaceFloating()
    {
        var root = _inputRoot;
        if (root?.Parent is not View frame) return;
        if (frame.Width == 0 || root.Width == 0 || root.Height == 0)
        {
            root.Post(PlaceFloating); // nach dem ersten Layout
            return;
        }

        float freeX = Math.Max(0, frame.Width - root.Width);
        float freeY = Math.Max(0, frame.Height - root.Height);
        root.TranslationX = freeX * _settings.FloatingX;
        root.TranslationY = freeY * _settings.FloatingY;
        Window?.Window?.DecorView?.RequestLayout(); // Berührungsbereich (OnComputeInsets) neu melden
    }

    /// <summary>Griff gezogen: Tastatur folgt dem Finger, bleibt aber ganz auf dem Bildschirm.</summary>
    void MoveFloating(float dx, float dy, bool finished)
    {
        var root = _inputRoot;
        if (root?.Parent is not View frame) return;
        float freeX = Math.Max(0, frame.Width - root.Width);
        float freeY = Math.Max(0, frame.Height - root.Height);
        root.TranslationX = Math.Clamp(root.TranslationX + dx, 0, freeX);
        root.TranslationY = Math.Clamp(root.TranslationY + dy, 0, freeY);
        if (!finished) return;

        if (freeX > 0) _settings.FloatingX = root.TranslationX / freeX;
        if (freeY > 0) _settings.FloatingY = root.TranslationY / freeY;
        Window?.Window?.DecorView?.RequestLayout();
#if DEBUG || ZBOARD_TESTHOOKS
        Android.Util.Log.Info("zBoardTest", $"FLOATING moved x={root.TranslationX:F0} y={root.TranslationY:F0}");
#endif
    }

    void ToggleFloating()
    {
        _settings.Floating = !_settings.Floating;
        if (_settings.Floating && _settings.OneHanded != OneHandedMode.Off) _settings.OneHanded = OneHandedMode.Off;
        ApplyFloating();
        ApplyLayout();
    }

    /// <summary>Geteiltes Layout je nach Einstellung, Bildschirmbreite und Ausrichtung.</summary>
    void ApplySplit()
    {
        if (_keyboardView == null) return;
        var config = Resources!.Configuration!;
        bool wide = config.ScreenWidthDp >= WideScreenDp;
        bool landscape = config.Orientation == Android.Content.Res.Orientation.Landscape;
        bool split = !Floating && _settings.OneHanded == OneHandedMode.Off && _settings.Split switch
        {
            SplitMode.On => wide,
            SplitMode.Auto => wide && landscape,
            _ => false,
        };
        _keyboardView.SplitGap = split ? SplitGap : 0;
    }

    /// <summary>Griff unter der schwebenden Tastatur: ziehen verschiebt sie.</summary>
    sealed class FloatingHandle : View
    {
        readonly ZBoardImeService _service;
        readonly Paint _paint = new(PaintFlags.AntiAlias);
        readonly float _dp;
        float _lastX, _lastY;

        public FloatingHandle(ZBoardImeService service) : base(service)
        {
            _service = service;
            _dp = service.Resources!.DisplayMetrics!.Density;
            ContentDescription = Localization.L.T("Tastatur verschieben", "Move keyboard");
        }

        public Color Color
        {
            get => _paint.Color;
            set
            {
                _paint.Color = value;
                Invalidate();
            }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float w = 40 * _dp, h = 5 * _dp;
            canvas.DrawRoundRect(Width / 2f - w / 2, Height / 2f - h / 2, Width / 2f + w / 2, Height / 2f + h / 2, h / 2, h / 2, _paint);
#if DEBUG || ZBOARD_TESTHOOKS
            var origin = new int[2];
            GetLocationOnScreen(origin);
            string line = $"FLOATING handle@{origin[0] + Width / 2},{origin[1] + Height / 2}";
            if (line != _logged)
            {
                _logged = line;
                Android.Util.Log.Info("zBoardTest", line);
            }
#endif
        }

#if DEBUG || ZBOARD_TESTHOOKS
        string _logged = string.Empty;
#endif

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null) return false;
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    (_lastX, _lastY) = (e.RawX, e.RawY);
                    return true;
                case MotionEventActions.Move:
                    _service.MoveFloating(e.RawX - _lastX, e.RawY - _lastY, finished: false);
                    (_lastX, _lastY) = (e.RawX, e.RawY);
                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    _service.MoveFloating(e.RawX - _lastX, e.RawY - _lastY, finished: true);
                    Invalidate(); // neue Lage ins Testprotokoll
                    return true;
            }

            return true;
        }
    }

    sealed class RoundedOutline(float radius) : ViewOutlineProvider
    {
        public override void GetOutline(View? view, Outline? outline)
        {
            if (view == null || outline == null) return;
            outline.SetRoundRect(0, 0, view.Width, view.Height, radius);
        }
    }
}
