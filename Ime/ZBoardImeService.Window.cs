using Android.Views;
using Android.Widget;

namespace ZBoard.Ime;

/// <summary>
/// Platz für Popups über der Tastatur, wie bei Gboard: Das Tastaturfenster ist so hoch wie der Bildschirm. Darin
/// liegt ein durchsichtiger Rahmen (<see cref="PopupSpaceFrame"/>), der immer die volle Höhe beansprucht; die
/// Tastatur sitzt an seiner Unterkante. So darf der Zusatzzeichen-Popup mit zwei Zeilen über die App reichen, statt
/// nach unten auf die Tasten zu rutschen. In <see cref="OnComputeInsets"/> erfährt Android, dass nur die Tastatur
/// selbst sichtbar und berührbar ist: Die App darüber bleibt bedienbar und wird nicht verkleinert.
/// </summary>
public partial class ZBoardImeService
{
    /// <summary>Volle Höhe nur ohne Vollbildmodus (Querformat mit eigenem Textfeld der Tastatur).</summary>
    bool UsePopupSpace => !IsFullscreenMode;

    public override void SetInputView(View? view)
    {
        base.SetInputView(view);
        UpdatePopupSpace();
    }

    public override void UpdateFullscreenMode()
    {
        base.UpdateFullscreenMode();
        UpdatePopupSpace();
    }

    void UpdatePopupSpace()
    {
        var window = Window?.Window;
        if (window == null) return;

        var attributes = window.Attributes;
        int height = UsePopupSpace ? ViewGroup.LayoutParams.MatchParent : ViewGroup.LayoutParams.WrapContent;
        if (attributes != null && attributes.Height != height)
        {
            attributes.Height = height;
            window.Attributes = attributes;
        }

        _inputRoot?.Parent?.RequestLayout();
        AllowDrawingAboveKeyboard();
        ApplyNavigationBarStyle();
    }

    /// <summary>Sichtbar und berührbar ist nur die Tastatur, nicht der durchsichtige Platz darüber.</summary>
    public override void OnComputeInsets(Insets? outInsets)
    {
        base.OnComputeInsets(outInsets);
        if (outInsets == null || !UsePopupSpace || _inputRoot is not { IsShown: true } root || root.Height == 0) return;

        var location = new int[2];
        root.GetLocationInWindow(location);
        int top = location[1];
        outInsets.ContentTopInsets = top;
        outInsets.VisibleTopInsets = top;

        // Berührbar nur die Tastatur selbst (wie AOSP LatinIME: TOUCHABLE_INSETS_REGION). Den Wert 3 direkt ins
        // Java-Feld schreiben: Je nach .NET-Version ist das Feld als int oder als Aufzählung gebunden.
        var region = outInsets.TouchableRegion;
        if (region != null)
        {
            region.Set(location[0], top, location[0] + root.Width, top + root.Height);
            var cls = Android.Runtime.JNIEnv.GetObjectClass(outInsets.Handle);
            var field = Android.Runtime.JNIEnv.GetFieldID(cls, "touchableInsets", "I");
            Android.Runtime.JNIEnv.SetField(outInsets.Handle, field, 3);
            Android.Runtime.JNIEnv.DeleteLocalRef(cls);
        }

#if DEBUG || ZBOARD_TESTHOOKS
        if (top != _loggedInsetsTop)
        {
            _loggedInsetsTop = top;
            Android.Util.Log.Info("zBoardTest", $"INSETS top={top} height={root.Height} region={region != null}");
        }
#endif
    }

#if DEBUG || ZBOARD_TESTHOOKS
    int _loggedInsetsTop = -1;
#endif

    /// <summary>
    /// Alle Eltern bis zum Fenster schneiden nicht ab: Die Tastatur darf darüber zeichnen
    /// (KeyboardView.OverflowTop rechnet den Platz bis zum obersten solchen Elternteil).
    /// </summary>
    void AllowDrawingAboveKeyboard()
    {
        var parent = _inputRoot?.Parent;
        while (parent is ViewGroup group)
        {
            group.SetClipChildren(false);
            group.SetClipToPadding(false);
            parent = group.Parent;
        }
    }

    /// <summary>Durchsichtige Ebene in voller Höhe für die Popups der Tastatur; nimmt keine Berührungen an.</summary>
    sealed class PopupLayerView(ZBoardImeService service, ZBoard.Keyboard.KeyboardView keyboard) : View(service)
    {
        protected override void OnDraw(Android.Graphics.Canvas canvas) => keyboard.DrawPopupLayer(canvas);
    }

    /// <summary>Durchsichtiger Rahmen in voller Höhe, die Tastatur unten darin.</summary>
    sealed class PopupSpaceFrame(ZBoardImeService service) : FrameLayout(service)
    {
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            if (service.UsePopupSpace && MeasureSpec.GetMode(heightMeasureSpec) != MeasureSpecMode.Unspecified)
            {
                heightMeasureSpec = MeasureSpec.MakeMeasureSpec(MeasureSpec.GetSize(heightMeasureSpec), MeasureSpecMode.Exactly);
            }

            base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
        }
    }

    /// <summary>
    /// Navigationsleiste unter der Tastatur wie bei Gboard in der Hintergrundfarbe der Tastatur, Symbole passend
    /// dunkel oder hell. Sonst malt Android die Leiste in eigener Farbe (oft weiß mit hellen Symbolen).
    /// </summary>
    void ApplyNavigationBarStyle()
    {
        var window = Window?.Window;
        if (window == null) return;
        var bg = _theme.Background;
        bool light = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255 >= 0.5;

        window.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) window.NavigationBarContrastEnforced = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            int mask = (int)WindowInsetsControllerAppearance.LightNavigationBars;
            window.InsetsController?.SetSystemBarsAppearance(light ? mask : 0, mask);
        }
        else if (window.DecorView is { } decor)
        {
#pragma warning disable CA1422, CS0618 // vor Android 11 nur über die alten Flags
            var flags = (int)decor.SystemUiVisibility;
            int lightNav = (int)SystemUiFlags.LightNavigationBar;
            decor.SystemUiVisibility = (StatusBarVisibility)(light ? flags | lightNav : flags & ~lightNav);
#pragma warning restore CA1422, CS0618
        }

#pragma warning disable CA1422, CS0618 // bis Android 14 wirksam, danach liegt die Tastatur selbst unter der Leiste
        window.SetNavigationBarColor(bg);
#pragma warning restore CA1422, CS0618
    }
}
