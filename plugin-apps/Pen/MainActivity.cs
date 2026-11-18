using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using Orientation = Android.Widget.Orientation;

namespace ZBoard.Pen;

/// <summary>Einrichtung: Erklärung und Schriftmodelle je Sprache (laden, löschen). Geschrieben wird in zBoard.</summary>
[Activity(
    Name = "de.World_of_Bytes.zBoard.Pen.MainActivity",
    Label = "zBoard Pen",
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.DeviceDefault.Light")]
public sealed class MainActivity : Activity
{
    LinearLayout _models = null!;

    static string T(string german, string english) => Java.Util.Locale.Default.Language == "de" ? german : english;

    int Dp(float value) => (int)(value * Resources!.DisplayMetrics!.Density);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        var scroll = new ScrollView(this);
        scroll.SetFitsSystemWindows(true);
        scroll.AddView(content);
        SetContentView(scroll);

        content.AddView(Text("zBoard Pen", 28));
        content.AddView(Text(T(
            "Handschrift für zBoard: Schreib mit Finger oder Stift, zBoard setzt den erkannten Text ein. Erkannt wird mit "
            + "Google ML Kit auf dem Gerät. Internet braucht zBoard Pen nur, um einmalig das Schriftmodell einer Sprache "
            + "zu laden (je etwa 20 MB). zBoard selbst bleibt ohne Internet.",
            "Handwriting for zBoard: write with your finger or a stylus, zBoard inserts the recognized text. Recognition "
            + "runs on the device with Google ML Kit. zBoard Pen only needs the internet to download a language's "
            + "handwriting model once (about 20 MB each). zBoard itself stays offline."), 15));

        content.AddView(Heading(T("So geht's", "How to")));
        content.AddView(Text(T(
            "1. zBoard-Einstellungen → Plugins → „zBoard Pen“ einschalten.\n"
            + "2. In der Werkzeugleiste über der Tastatur den Stift antippen und schreiben.\n"
            + "3. Das erkannte Wort steht unterstrichen im Feld, Alternativen in der Leiste. Schreib nach einer kurzen "
            + "Pause weiter, um das nächste Wort zu beginnen; Löschen verwirft das gerade geschriebene Wort, „ABC“ führt "
            + "zurück zur Tastatur.",
            "1. zBoard settings → Plugins → turn on “zBoard Pen”.\n"
            + "2. Tap the pen in the toolbar above the keyboard and write.\n"
            + "3. The recognized word appears underlined in the field, alternatives in the bar. Pause briefly, then keep "
            + "writing to start the next word; delete discards the word you just wrote, “ABC” returns to the keyboard."), 14));

        content.AddView(Heading(T("Schriftmodelle", "Handwriting models")));
        content.AddView(Text(T("Fehlende Modelle lädt zBoard Pen auch automatisch, sobald du in der Sprache schreibst.",
            "zBoard Pen also downloads missing models automatically as soon as you write in that language."), 14));
        _models = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.AddView(_models);

        content.AddView(Text(T(
            "Die Striche bleiben auf dem Gerät. Google ML Kit kann anonyme Nutzungsdaten (z. B. Laufzeiten, Fehler) an "
            + "Google senden, nach Angaben von Google ohne das Geschriebene. zBoard Pen ist kein Produkt von Google.",
            "Your strokes stay on the device. Google ML Kit may send anonymous usage data (such as timings and errors) "
            + "to Google, according to Google without what you write. zBoard Pen is not a Google product."), 12));
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = ShowModels();
    }

    async Task ShowModels()
    {
        _models.RemoveAllViews();
        foreach (var (code, name, _) in PenLanguages.All)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            var label = Text(name, 16);
            row.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var status = Text("…", 13);
            row.AddView(status);
            var button = new Button(this) { Enabled = false, Text = "…" };
            button.SetAllCaps(false);
            row.AddView(button);
            _models.AddView(row);
            _ = Fill(code, status, button);
        }

        await Task.CompletedTask;
    }

    async Task Fill(string code, TextView status, Button button)
    {
        ModelState state;
        try
        {
            state = await InkRecognizer.StateAsync(code);
        }
        catch (Exception)
        {
            state = ModelState.Missing;
        }

        status.Text = state switch
        {
            ModelState.Ready => T("geladen", "downloaded"),
            ModelState.Downloading => T("wird geladen …", "downloading …"),
            ModelState.Unsupported => T("nicht verfügbar", "not available"),
            _ => T("nicht geladen", "not downloaded"),
        };
        button.Enabled = state is ModelState.Ready or ModelState.Missing;
        button.Text = state == ModelState.Ready ? T("Löschen", "Delete") : T("Laden", "Download");
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            status.Text = state == ModelState.Ready ? T("wird gelöscht …", "deleting …") : T("wird geladen …", "downloading …");
            try
            {
                if (state == ModelState.Ready) await InkRecognizer.DeleteAsync(code);
                else await InkRecognizer.DownloadAsync(code);
            }
            catch (Exception e)
            {
                Toast.MakeText(this, T("Fehlgeschlagen: ", "Failed: ") + e.Message, ToastLength.Long)!.Show();
            }

            if (!IsFinishing && !IsDestroyed) _ = ShowModels();
        };
    }

    TextView Text(string text, float size)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetPadding(0, Dp(4), Dp(8), Dp(8));
        return view;
    }

    TextView Heading(string text)
    {
        var view = Text(text, 18);
        view.SetPadding(0, Dp(20), 0, Dp(4));
        return view;
    }
}
