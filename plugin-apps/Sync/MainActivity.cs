using Android.App;
using Android.Content;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using ZBoard.Sync.Stores;
using Orientation = Android.Widget.Orientation;

namespace ZBoard.Sync;

/// <summary>Einrichtung: Speicher wählen, anmelden bzw. WebDAV-Zugang eintragen. Den Abgleich steuert zBoard.</summary>
[Activity(
    Name = "de.World_of_Bytes.zBoard.Sync.MainActivity",
    Label = "zBoard Sync",
    MainLauncher = true,
    Exported = true,
    LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
    Theme = "@android:style/Theme.DeviceDefault.Light")]
public sealed class MainActivity : Activity
{
    public const string ExtraCode = "code";
    public const string ExtraState = "state";
    public const string ExtraError = "error";

    SyncSettings _settings = null!;
    LinearLayout _content = null!;
    bool _busy;

    int Dp(float value) => (int)(value * Resources!.DisplayMetrics!.Density);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _settings = new SyncSettings(this);

        _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _content.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        var scroll = new ScrollView(this);
        scroll.SetFitsSystemWindows(true);
        scroll.AddView(_content);
        SetContentView(scroll);

        Build();
        HandleRedirect(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleRedirect(intent);
    }

    // ---------- Oberfläche ----------

    void Build()
    {
        _content.RemoveAllViews();
        _content.AddView(Text("zBoard Sync", 28));
        _content.AddView(Text(L.T(
            "Gleicht Einstellungen, gelernte Wörter und E-Mail-Adressen von zBoard zwischen deinen Geräten ab. zBoard selbst "
            + "hat keinen Internetzugriff – nur diese App, und sie spricht nur mit dem Speicher, den du hier wählst.",
            "Syncs zBoard's settings, learned words and email addresses between your devices. zBoard itself has no internet "
            + "access – only this app does, and it only talks to the storage you choose here."), 15));

        _content.AddView(Heading(L.T("1. Speicher", "1. Storage")));
        if (_settings.IsReady)
        {
            _content.AddView(Text(L.T($"Verbunden: {Describe()}", $"Connected: {Describe()}"), 16));
            _content.AddView(Button(L.T("Abmelden", "Sign out"), () =>
            {
                _settings.SignOut();
                Build();
            }));
            _content.AddView(Text(L.T("Abmelden löscht nur die Zugangsdaten auf diesem Gerät; die Datei im Speicher bleibt.",
                "Signing out only deletes the credentials on this device; the file in the storage stays."), 12));
        }
        else
        {
            OAuthButton(StoreKind.GoogleDrive, SyncConfig.HasGoogle);
            OAuthButton(StoreKind.OneDrive, SyncConfig.HasMicrosoft);
            OAuthButton(StoreKind.Dropbox, SyncConfig.HasDropbox);
            BuildWebDav();
        }

        _content.AddView(Heading(L.T("2. In zBoard einschalten", "2. Turn on in zBoard")));
        _content.AddView(Text(L.T(
            "zBoard-Einstellungen → Sichern & wiederherstellen → „Automatisch synchronisieren“. Auf jedem Gerät mit "
            + "demselben Speicher anmelden. zBoard gleicht dann beim Öffnen und Schließen der Tastatur ab, höchstens alle "
            + "15 Minuten.",
            "zBoard settings → Backup & restore → \"Sync automatically\". Sign in to the same storage on every device. zBoard "
            + "then syncs when the keyboard opens and closes, at most every 15 minutes."), 14));
        _content.AddView(Text(L.T(
            "Zugangsdaten bleiben verschlüsselt im privaten Speicher dieser App. Die Datei im Cloud-Speicher ist nicht "
            + "zusätzlich verschlüsselt; es gelten die Bedingungen des Anbieters. Google Drive, OneDrive und Dropbox legen "
            + "sie in einem eigenen App-Ordner ab – zBoard Sync sieht deine übrigen Dateien nicht.",
            "Credentials stay encrypted in this app's private storage. The file in the cloud storage is not additionally "
            + "encrypted; the provider's terms apply. Google Drive, OneDrive and Dropbox keep it in a separate app folder – "
            + "zBoard Sync cannot see your other files."), 12));
    }

    string Describe() => _settings.Kind == StoreKind.WebDav
        ? $"WebDAV ({_settings.WebDavUser} · {(Uri.TryCreate(_settings.WebDavUrl, UriKind.Absolute, out var u) ? u.Host : _settings.WebDavUrl)})"
        : SyncSettings.Name(_settings.Kind);

    void OAuthButton(StoreKind kind, bool available)
    {
        string name = SyncSettings.Name(kind);
        var button = Button(L.T($"Mit {name} anmelden", $"Sign in with {name}"), () => StartLogin(kind));
        if (!available)
        {
            button.Enabled = false;
            button.Text = L.T($"{name} (in dieser Version nicht eingerichtet)", $"{name} (not set up in this build)");
        }

        _content.AddView(button);
    }

    void BuildWebDav()
    {
        _content.AddView(Heading(L.T("WebDAV (HiDrive, Nextcloud …)", "WebDAV (HiDrive, Nextcloud …)")));
        var url = Field(L.T("Adresse, z. B. https://cloud.example.de/remote.php/dav/files/name/", "Address, e.g. https://cloud.example.com/remote.php/dav/files/name/"),
            _settings.WebDavUrl, InputTypes.ClassText | InputTypes.TextVariationUri);
        var user = Field(L.T("Benutzername", "User name"), _settings.WebDavUser, InputTypes.ClassText);
        var password = Field(L.T("Passwort bzw. App-Passwort", "Password or app password"), string.Empty,
            InputTypes.ClassText | InputTypes.TextVariationPassword);
        _content.AddView(url);
        _content.AddView(user);
        _content.AddView(password);
        _content.AddView(Button(L.T("HiDrive-Adresse einsetzen", "Use HiDrive address"), () =>
        {
            if (string.IsNullOrWhiteSpace(user.Text))
            {
                Toast.MakeText(this, L.T("Zuerst den HiDrive-Benutzernamen eintragen.", "Enter your HiDrive user name first."), ToastLength.Short)!.Show();
                return;
            }

            url.Text = WebDavStore.HiDriveUrl(user.Text!);
        }));
        _content.AddView(Button(L.T("Verbinden und testen", "Connect and test"), () =>
            ConnectWebDav(url.Text ?? string.Empty, user.Text ?? string.Empty, password.Text ?? string.Empty)));
        _content.AddView(Text(L.T(
            "HiDrive: WebDAV muss in deinem Tarif enthalten sein. Nextcloud: am besten ein App-Passwort unter "
            + "Einstellungen → Sicherheit anlegen. zBoard Sync legt dort einen Ordner „zBoard“ an.",
            "HiDrive: WebDAV must be included in your plan. Nextcloud: preferably create an app password under "
            + "Settings → Security. zBoard Sync creates a folder \"zBoard\" there."), 12));
    }

    async void ConnectWebDav(string url, string user, string password)
    {
        if (_busy) return;
        url = url.Trim();
        if (!WebDavStore.IsValidUrl(url))
        {
            Toast.MakeText(this, L.T("Die Adresse muss mit https:// beginnen.", "The address must start with https://."), ToastLength.Long)!.Show();
            return;
        }

        if (user.Trim().Length == 0 || password.Length == 0)
        {
            Toast.MakeText(this, L.T("Benutzername und Passwort fehlen.", "User name and password are missing."), ToastLength.Short)!.Show();
            return;
        }

        _busy = true;
        try
        {
            await new WebDavStore(url, user.Trim(), password).EnsureFolderAsync(CancellationToken.None);
            _settings.SignOut();
            _settings.WebDavUrl = url;
            _settings.WebDavUser = user;
            _settings.WebDavPassword = password;
            _settings.Kind = StoreKind.WebDav;
            Toast.MakeText(this, L.T("Verbunden.", "Connected."), ToastLength.Short)!.Show();
            Build();
        }
        catch (SyncException e)
        {
            Toast.MakeText(this, e.Message, ToastLength.Long)!.Show();
        }
        catch (Exception)
        {
            Toast.MakeText(this, L.T("Keine Verbindung zum Server.", "No connection to the server."), ToastLength.Long)!.Show();
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------- Anmeldung im Browser ----------

    void StartLogin(StoreKind kind)
    {
        var provider = OAuth.For(kind);
        if (provider == null) return;
        string state = OAuth.NewRandom();
        string verifier = OAuth.NewRandom();
        _settings.PendingLogin = (kind, state, verifier);
        try
        {
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(OAuth.AuthorizeUrl(provider, state, verifier))));
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, L.T("Kein Browser gefunden.", "No browser found."), ToastLength.Long)!.Show();
        }
    }

    async void HandleRedirect(Intent? intent)
    {
        if (intent == null || !intent.HasExtra(ExtraState)) return;
        string state = intent.GetStringExtra(ExtraState) ?? string.Empty;
        string? code = intent.GetStringExtra(ExtraCode);
        string? error = intent.GetStringExtra(ExtraError);
        intent.RemoveExtra(ExtraState); // nicht nach einer Drehung erneut einlösen

        var (kind, expectedState, verifier) = _settings.PendingLogin;
        _settings.PendingLogin = (StoreKind.None, string.Empty, string.Empty);
        var provider = OAuth.For(kind);
        if (provider == null || state.Length == 0 || state != expectedState)
        {
            Toast.MakeText(this, L.T("Anmeldung abgebrochen.", "Sign-in cancelled."), ToastLength.Short)!.Show();
            return;
        }

        if (error != null || string.IsNullOrEmpty(code))
        {
            Toast.MakeText(this, L.T("Anmeldung abgelehnt.", "Sign-in declined."), ToastLength.Short)!.Show();
            return;
        }

        try
        {
            _settings.SignOut();
            await OAuth.ExchangeCodeAsync(provider, code, verifier, _settings);
            Toast.MakeText(this, L.T($"Mit {SyncSettings.Name(kind)} verbunden.", $"Connected to {SyncSettings.Name(kind)}."), ToastLength.Short)!.Show();
        }
        catch (SyncException e)
        {
            Toast.MakeText(this, e.Message, ToastLength.Long)!.Show();
        }
        catch (Exception)
        {
            Toast.MakeText(this, L.T("Keine Verbindung zum Anbieter.", "No connection to the provider."), ToastLength.Long)!.Show();
        }

        Build();
    }

    // ---------- Bausteine ----------

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

    EditText Field(string hint, string value, InputTypes type) => new(this) { Hint = hint, Text = value, InputType = type };

    Android.Widget.Button Button(string label, Action onClick)
    {
        var button = new Android.Widget.Button(this) { Text = label };
        button.SetAllCaps(false);
        button.Click += (_, _) => onClick();
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) };
        button.LayoutParameters = lp;
        return button;
    }
}

/// <summary>Rücksprung aus dem Browser (eigenes URI-Schema): Code und state an die Einrichtung weitergeben.</summary>
[Activity(Name = "de.World_of_Bytes.zBoard.Sync.RedirectActivity", Exported = true, NoHistory = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { SyncConfig.RedirectScheme, SyncConfig.GoogleRedirectScheme })]
public sealed class RedirectActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var data = Intent?.Data;
        var forward = new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        forward.PutExtra(MainActivity.ExtraState, data?.GetQueryParameter("state") ?? string.Empty);
        forward.PutExtra(MainActivity.ExtraCode, data?.GetQueryParameter("code"));
        if (data?.GetQueryParameter("error") is { } error) forward.PutExtra(MainActivity.ExtraError, error);
        StartActivity(forward);
        Finish();
    }
}
