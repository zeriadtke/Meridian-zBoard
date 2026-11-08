using Android.App;
using Android.Content;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Orientation = Android.Widget.Orientation;

namespace ZBoard.DeepL;

/// <summary>Einrichtung: API-Schlüssel, Zielsprachen, Test. Alles Weitere passiert in zBoard.</summary>
[Activity(
    Name = "de.World_of_Bytes.zBoard.DeepL.MainActivity",
    Label = "zBoard DeepL",
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.DeviceDefault.Light")]
public sealed class MainActivity : Activity
{
    DeepLSettings _settings = null!;
    EditText _key = null!;
    readonly List<(TargetLanguage Language, CheckBox Box)> _boxes = [];

    static string T(string german, string english) => Java.Util.Locale.Default.Language == "de" ? german : english;

    int Dp(float value) => (int)(value * Resources!.DisplayMetrics!.Density);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _settings = new DeepLSettings(this);

        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        var scroll = new ScrollView(this);
        scroll.SetFitsSystemWindows(true);
        scroll.AddView(content);
        SetContentView(scroll);

        content.AddView(Text(T("zBoard DeepL", "zBoard DeepL"), 28));
        content.AddView(Text(T(
            "Übersetzt Text in zBoard mit DeepL. zBoard selbst hat keinen Internetzugriff – nur diese App. Text geht nur an "
            + "DeepL, wenn du in zBoard über das Puzzle-Symbol eine Übersetzung antippst.",
            "Translates text in zBoard with DeepL. zBoard itself has no internet access – only this app does. Text is only "
            + "sent to DeepL when you tap a translation via the puzzle icon in zBoard."), 15));

        content.AddView(Heading(T("1. API-Schlüssel", "1. API key")));
        content.AddView(Text(T(
            "Den Schlüssel gibt es nur mit einem eigenen API-Konto (kostenlos: „DeepL API Free“) – ein normales "
            + "DeepL-Konto hat keinen. Bei DeepL abmelden, unter „API-Tarif wählen“ registrieren, dann im Konto unter "
            + "„API-Schlüssel“ kopieren. Free-Schlüssel enden auf „:fx“.",
            "The key requires a separate API account (free: “DeepL API Free”) – a regular DeepL account has none. Log "
            + "out of DeepL, sign up via “Choose an API plan”, then copy it from your account under “API keys”. Free "
            + "keys end in “:fx”."), 14));
        _key = new EditText(this)
        {
            Text = _settings.ApiKey,
            Hint = T("DeepL-API-Schlüssel", "DeepL API key"),
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword,
        };
        content.AddView(_key);
        content.AddView(Button(T("API-Tarif wählen", "Choose an API plan"), () =>
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://www.deepl.com/pro-api")))));
        content.AddView(Button(T("Meine API-Schlüssel", "My API keys"), () =>
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://www.deepl.com/your-account/keys")))));

        content.AddView(Heading(T("2. Zielsprachen", "2. Target languages")));
        var selected = _settings.Targets.Select(t => t.Code).ToHashSet();
        foreach (var language in DeepLSettings.Languages)
        {
            var box = new CheckBox(this) { Text = language.Name, Checked = selected.Contains(language.Code) };
            _boxes.Add((language, box));
            content.AddView(box);
        }

        content.AddView(Button(T("Speichern", "Save"), Save));
        content.AddView(Button(T("Testen", "Test"), Test));

        content.AddView(Heading(T("3. In zBoard einschalten", "3. Turn on in zBoard")));
        content.AddView(Text(T(
            "zBoard-Einstellungen → Plugins → „zBoard DeepL“ einschalten. Dann in der Leiste über der Tastatur das "
            + "Puzzle-Symbol antippen und eine Sprache wählen: Übersetzt wird der markierte Text, ohne Markierung der "
            + "ganze Inhalt des Felds. „↶ Rückgängig“ stellt das Original wieder her.",
            "zBoard settings → Plugins → turn on “zBoard DeepL”. Then tap the puzzle icon in the bar above the keyboard "
            + "and pick a language: the selected text is translated, or the whole field without a selection. "
            + "“↶ Undo” restores the original."), 14));
        content.AddView(Text(T(
            "Der Schlüssel bleibt im privaten Speicher dieser App. DeepL verarbeitet den Text nach seinen eigenen "
            + "Datenschutzbestimmungen. zBoard DeepL ist kein Produkt der DeepL SE.",
            "The key stays in this app's private storage. DeepL processes the text under its own privacy policy. "
            + "zBoard DeepL is not a product of DeepL SE."), 12));
    }

    void Save()
    {
        _settings.ApiKey = _key.Text ?? string.Empty;
        _settings.Targets = [.. _boxes.Where(b => b.Box.Checked).Select(b => b.Language)];
        Toast.MakeText(this, T("Gespeichert.", "Saved."), ToastLength.Short)!.Show();
    }

    async void Test()
    {
        Save();
        var target = _settings.Targets.FirstOrDefault() ?? DeepLSettings.Languages[1];
        try
        {
            string result = await DeepLClient.TranslateAsync(_settings.ApiKey, T("Hallo Welt, wie geht es dir?", "Hello world, how are you?"), target.Code);
            Toast.MakeText(this, $"{target.Name}: {result}", ToastLength.Long)!.Show();
        }
        catch (DeepLException e)
        {
            Toast.MakeText(this, e.Message, ToastLength.Long)!.Show();
        }
    }

    TextView Text(string text, float size)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetPadding(0, Dp(4), 0, Dp(8));
        return view;
    }

    TextView Heading(string text)
    {
        var view = Text(text, 18);
        view.SetPadding(0, Dp(20), 0, Dp(4));
        return view;
    }

    Android.Widget.Button Button(string label, Action onClick)
    {
        var button = new Android.Widget.Button(this) { Text = label };
        button.Click += (_, _) => onClick();
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) };
        button.LayoutParameters = lp;
        return button;
    }
}
