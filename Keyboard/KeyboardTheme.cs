using Android.Content;
using Android.Graphics;
using R = Android.Resource.Color;

namespace ZBoard.Keyboard;

/// <summary>Farben der Tastatur. Standard ist Material You (Systemfarben ab Android 12), sonst eine helle/dunkle Palette.</summary>
public sealed class KeyboardTheme
{
    public Color Background { get; init; }
    public Color Key { get; init; }
    public Color SpecialKey { get; init; }
    public Color KeyPressed { get; init; }
    public Color Accent { get; init; }
    public Color AccentText { get; init; }
    public Color Text { get; init; }
    public Color HintText { get; init; }
    public Color Popup { get; init; }

    /// <summary>Enter-Taste (Pille); bei Material You die zarte Akzentfläche, sonst die Akzentfarbe.</summary>
    public Color EnterKey { get; init; }
    public Color EnterText { get; init; }

    /// <summary>
    /// Deckende Farbe, die für die Fläche steht, wenn <see cref="Background"/> durchsichtig ist (Design mit eigenem Bild):
    /// für die Navigationsleiste und um zu entscheiden, ob deren Symbole hell oder dunkel sein müssen.
    /// </summary>
    public Color? Solid { get; init; }

    public Color BarColor => Solid ?? Background;

    /// <summary>Schatten unter der Schrift, damit sie auf einem Bild lesbar bleibt.</summary>
    public bool TextShadow { get; init; }

    /// <summary>
    /// Design mit eigenem Hintergrundbild: Hintergrund durchsichtig (das Bild liegt darunter), Tasten halb
    /// durchsichtig. Schrift hell oder dunkel je nachdem, wie hell das Bild nach dem Abdunkeln ist.
    /// </summary>
    /// <param name="average">Mittlere Farbe des Bilds nach dem Abdunkeln.</param>
    public static KeyboardTheme FromImage(Color average, Color accent)
    {
        bool dark = ThemeImage.IsDark(average);
        return dark
            ? new KeyboardTheme
            {
                Background = Color.Transparent,
                Solid = average,
                Key = Color.Argb(64, 255, 255, 255),
                SpecialKey = Color.Argb(36, 255, 255, 255),
                KeyPressed = Color.Argb(120, 255, 255, 255),
                Accent = accent,
                AccentText = Color.ParseColor("#0E1218"),
                Text = Color.White,
                HintText = Color.Argb(210, 255, 255, 255),
                Popup = Color.ParseColor("#3A3F46"),
                EnterKey = accent,
                EnterText = Color.ParseColor("#0E1218"),
                TextShadow = true,
            }
            : new KeyboardTheme
            {
                Background = Color.Transparent,
                Solid = average,
                Key = Color.Argb(170, 255, 255, 255),
                SpecialKey = Color.Argb(100, 255, 255, 255),
                KeyPressed = Color.Argb(230, 255, 255, 255),
                Accent = accent,
                AccentText = Color.White,
                Text = Color.ParseColor("#1F2328"),
                HintText = Color.ParseColor("#3F4650"),
                Popup = Color.White,
                EnterKey = accent,
                EnterText = Color.White,
            };
    }

    public static readonly KeyboardTheme Light = new()
    {
        Background = Color.ParseColor("#E9ECEF"),
        Key = Color.ParseColor("#FFFFFF"),
        SpecialKey = Color.ParseColor("#CED4DA"),
        KeyPressed = Color.ParseColor("#B8C0C9"),
        Accent = Color.ParseColor("#2F6FDE"),
        AccentText = Color.ParseColor("#FFFFFF"),
        Text = Color.ParseColor("#1F2328"),
        HintText = Color.ParseColor("#6B7280"),
        Popup = Color.ParseColor("#FFFFFF"),
        EnterKey = Color.ParseColor("#2F6FDE"),
        EnterText = Color.ParseColor("#FFFFFF"),
    };

    public static readonly KeyboardTheme Dark = new()
    {
        Background = Color.ParseColor("#1D2024"),
        Key = Color.ParseColor("#3A3F46"),
        SpecialKey = Color.ParseColor("#2A2E33"),
        KeyPressed = Color.ParseColor("#535A63"),
        Accent = Color.ParseColor("#5B8DEF"),
        AccentText = Color.ParseColor("#0E1218"),
        Text = Color.ParseColor("#E8EAED"),
        HintText = Color.ParseColor("#9AA0A6"),
        Popup = Color.ParseColor("#454B53"),
        EnterKey = Color.ParseColor("#5B8DEF"),
        EnterText = Color.ParseColor("#0E1218"),
    };

    /// <summary>Material You: Farben aus der Systempalette (ab Android 12, abgeleitet vom Hintergrundbild).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    public static KeyboardTheme FromSystem(Context context, bool dark)
    {
        Color C(int id) => ToColor(context.Resources!.GetColor(id, context.Theme));

        // Angelehnt an Gboard mit Material You: getönter Hintergrund, weiße/dunkle Buchstabentasten,
        // Sondertasten und Enter in der zweiten Akzentfarbe.
        return dark
            ? new KeyboardTheme
            {
                Background = C(R.SystemNeutral1900),
                Key = C(R.SystemNeutral1800),
                SpecialKey = C(R.SystemAccent2800),
                KeyPressed = C(R.SystemNeutral1700),
                Accent = C(R.SystemAccent1200),
                AccentText = C(R.SystemAccent1900),
                Text = C(R.SystemNeutral150),
                HintText = C(R.SystemNeutral2300),
                Popup = C(R.SystemNeutral1700),
                EnterKey = C(R.SystemAccent1700),
                EnterText = C(R.SystemAccent1100),
            }
            : new KeyboardTheme
            {
                Background = C(R.SystemNeutral250),
                Key = C(R.SystemNeutral110),
                SpecialKey = C(R.SystemAccent2100),
                KeyPressed = C(R.SystemAccent2200),
                Accent = C(R.SystemAccent1600),
                AccentText = C(R.SystemNeutral110),
                Text = C(R.SystemNeutral1900),
                HintText = C(R.SystemNeutral2600),
                Popup = C(R.SystemNeutral110),
                EnterKey = C(R.SystemAccent2100),
                EnterText = C(R.SystemNeutral1900),
            };
    }

    // Je nach .NET-Binding liefert GetColor eine Zahl oder schon eine Farbe.
    static Color ToColor(int argb) => new(argb);

    static Color ToColor(Color color) => color;
}
