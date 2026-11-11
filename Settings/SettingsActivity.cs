using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using ZBoard.Backup;
using ZBoard.Ime;
using ZBoard.Keyboard;
using ZBoard.Localization;
using ZBoard.Plugins;
using ZBoard.Sync;
using Color = Android.Graphics.Color;
using Orientation = Android.Widget.Orientation;

namespace ZBoard.Settings;

/// <summary>
/// Einstellungen im Aufbau von Gboard: Startseite mit großem Titel und Einträgen mit Symbol,
/// jeder Eintrag öffnet eine Unterseite. Farben wie die Tastatur (Material You ab Android 12).
/// </summary>
[Activity(
    Name = "de.World_of_Bytes.zBoard.SettingsActivity",
    Label = "Meridian Tastatur", // Meridian: Name im App-Menü
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar")]
public sealed class SettingsActivity : Activity
{
    const int MicRequest = 42;
    const int CreateBackupRequest = 43;
    const int OpenBackupRequest = 44;

    ZBoardSettings _settings = null!;
    KeyboardTheme _colors = KeyboardTheme.Light;
    ScrollView _scroll = null!;
    LinearLayout _content = null!;
    float _dp;
    string _page = Main;
    Java.Lang.Object? _backCallback;
    BackupParts _backupParts = BackupParts.All;

    const string Main = "main";

    record Page(string Id, int Icon, string Title, Func<string?> Subtitle, Action<SettingsActivity> Build);

    static readonly Page[] Pages =
    [
        new("languages", Resource.Drawable.ms_language, L.T("Sprachen", "Languages"), () => null, a => a.BuildLanguages()),
        new("preferences", Resource.Drawable.ms_tune, L.T("Einstellungen", "Preferences"), () => null, a => a.BuildPreferences()),
        new("theme", Resource.Drawable.ms_palette, L.T("Design", "Theme"), () => null, a => a.BuildTheme()),
        new("corrections", Resource.Drawable.ms_spellcheck, L.T("Korrekturen & Vorschläge", "Text correction"), () => null, a => a.BuildCorrections()),
        new("glide", Resource.Drawable.ms_gesture, L.T("Glide Typing", "Glide typing"), () => null, a => a.BuildGlide()),
        new("voice", Resource.Drawable.ms_mic, L.T("Spracheingabe", "Voice typing"), () => null, a => a.BuildVoice()),
        new("clipboard", Resource.Drawable.ms_content_paste, L.T("Zwischenablage", "Clipboard"), () => null, a => a.BuildClipboard()),
        new("dictionary", Resource.Drawable.ms_menu_book, L.T("Wörterbuch", "Dictionary"), () => null, a => a.BuildDictionary()),
        new("backup", Resource.Drawable.ms_settings_backup_restore, L.T("Sichern & wiederherstellen", "Backup & restore"), () => null, a => a.BuildBackup()),
        new("plugins", Resource.Drawable.ms_extension, L.T("Plugins", "Plugins"), () => null, a => a.BuildPlugins()),
        new("test", Resource.Drawable.ms_keyboard, L.T("Tastatur ausprobieren", "Try the keyboard"), () => null, a => a.BuildTest()),
        new("about", Resource.Drawable.ms_info, L.T("Über zBoard", "About zBoard"), () => null, a => a.BuildAbout()),
    ];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _settings = new ZBoardSettings(this);
        _colors = CurrentColors();
        // Schalter, Dialoge und Eingabefelder passend hell oder dunkel
        SetTheme(IsDark ? Android.Resource.Style.ThemeDeviceDefaultNoActionBar : Android.Resource.Style.ThemeDeviceDefaultLightNoActionBar);
        base.OnCreate(savedInstanceState);
        _dp = Resources!.DisplayMetrics!.Density;

        _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _scroll = new ScrollView(this) { FillViewport = true };
        _scroll.SetBackgroundColor(_colors.Background);
        _scroll.AddView(_content);
        _scroll.SetOnApplyWindowInsetsListener(new SystemBarPadding());
        SetContentView(_scroll);
        ApplySystemBars();

        if (savedInstanceState != null) _backupParts = (BackupParts)savedInstanceState.GetInt("backupParts", (int)BackupParts.All);
        ShowPage(savedInstanceState?.GetString("page") ?? Main);
        if (Intent?.GetBooleanExtra(ZBoardImeService.RequestMicExtra, false) == true) RequestMicrophone();
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutString("page", _page);
        outState.PutInt("backupParts", (int)_backupParts);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_page == Main) ShowPage(Main); // Einrichtungsstatus aktualisieren
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus && _page == Main) ShowPage(Main); // nach dem Auswahldialog
    }

    // ---------- Navigation ----------

    void ShowPage(string id)
    {
        _page = id;
        _content.RemoveAllViews();
        if (id == Main)
        {
            BuildMain();
        }
        else
        {
            var page = Pages.First(p => p.Id == id);
            PageHeader(page.Title);
            page.Build(this);
        }

        _scroll.ScrollTo(0, 0);
        UpdateBackHandling();
    }

    void GoBack()
    {
        if (_page == Main) Finish();
        else ShowPage(Main);
    }

    // Ab Android 13 (bei targetSdk 36 Pflicht): Zurück-Geste über OnBackInvokedDispatcher.
    void UpdateBackHandling()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        var dispatcher = OnBackInvokedDispatcher;
        if (_page != Main && _backCallback == null)
        {
            var callback = new BackCallback(this);
            dispatcher.RegisterOnBackInvokedCallback(0, callback);
            _backCallback = callback;
        }
        else if (_page == Main && _backCallback is Android.Window.IOnBackInvokedCallback registered)
        {
            dispatcher.UnregisterOnBackInvokedCallback(registered);
            _backCallback = null;
        }
    }

#pragma warning disable CS0618, CS0672, CA1422 // bis Android 12 der klassische Weg
    public override void OnBackPressed()
    {
        if (_page != Main) ShowPage(Main);
        else base.OnBackPressed();
    }
#pragma warning restore CS0618, CS0672, CA1422

    sealed class BackCallback(SettingsActivity owner) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => owner.GoBack();
    }

    // ---------- Startseite ----------

    void BuildMain()
    {
        var title = Label(L.T("zBoard-Einstellungen", "zBoard settings"), 32, _colors.Text);
        title.SetPadding(Dp(24), Dp(56), Dp(24), Dp(24));
        _content.AddView(title);

        BuildSetupCard();

        foreach (var page in Pages)
        {
            string? subtitle = page.Id == "languages" ? LanguageSummary() : page.Subtitle();
            Row(page.Icon, page.Title, subtitle, () => ShowPage(page.Id));
        }
    }

    string LanguageSummary()
    {
        return string.Join(", ", _settings.Languages.Select(code =>
            $"{Layouts.LanguageNames.GetValueOrDefault(code, code)} ({LayoutName(code)})"));
    }

    static string LayoutName(string code) => code switch { "de" => "QWERTZ", "fr" => "AZERTY", _ => "QWERTY" };

    void BuildSetupCard()
    {
        var imm = InputMethods();
        bool enabled = imm.EnabledInputMethodList?.Any(i => i.PackageName == PackageName) == true;
        string? current = Android.Provider.Settings.Secure.GetString(ContentResolver, Android.Provider.Settings.Secure.DefaultInputMethod);
        bool selected = current?.StartsWith(PackageName + "/", StringComparison.Ordinal) == true;
        if (enabled && selected) return;

        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetPadding(Dp(20), Dp(16), Dp(20), Dp(12));
        card.Background = Rounded(_colors.Key, 24);
        var cardParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        cardParams.SetMargins(Dp(16), 0, Dp(16), Dp(16));

        card.AddView(Label(L.T("zBoard einrichten", "Set up zBoard"), 18, _colors.Text));
        card.AddView(Label(enabled
            ? L.T("zBoard ist aktiviert, aber noch nicht als Tastatur ausgewählt.", "zBoard is enabled but not yet selected as your keyboard.")
            : L.T("Aktiviere zBoard in den Systemeinstellungen und wähle es dann als Tastatur aus.",
                "Enable zBoard in the system settings, then select it as your keyboard."), 14, _colors.HintText));

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.End);
        if (!enabled)
        {
            buttons.AddView(TextButton(L.T("Aktivieren", "Enable"),
                () => StartActivity(new Intent(Android.Provider.Settings.ActionInputMethodSettings))));
        }

        buttons.AddView(TextButton(L.T("Auswählen", "Select"), () => InputMethods().ShowInputMethodPicker()));
        card.AddView(buttons);
        _content.AddView(card, cardParams);
    }

    // ---------- Unterseiten ----------

    void BuildLanguages()
    {
        Section(L.T("Aktive Sprachen", "Active languages"));
        foreach (var (code, name) in Layouts.LanguageNames)
        {
            Toggle(name, LayoutName(code), _settings.Languages.Contains(code), (view, isChecked) =>
            {
                var list = _settings.Languages.ToList();
                if (isChecked && !list.Contains(code)) list.Add(code);
                if (!isChecked) list.Remove(code);

                if (list.Count == 0)
                {
                    Toast.MakeText(this, L.T("Mindestens eine Sprache muss aktiv bleiben.", "At least one language must stay active."), ToastLength.Short)!.Show();
                    view.Checked = true;
                    return;
                }

                _settings.Languages = list;
            });
        }

        Toggle(L.T("Mehrsprachige Eingabe", "Multilingual typing"),
            L.T("Vorschläge und Autokorrektur in allen aktiven Sprachen gleichzeitig (bis zu drei), auf der Leertaste z. B. „DE · EN“",
                "Suggestions and autocorrect in all active languages at once (up to three), e.g. “DE · EN” on the space bar"),
            _settings.MultilingualTyping, (_, v) => _settings.MultilingualTyping = v);

        Note(L.T("Zum Wechseln lange auf die Leertaste drücken und die Sprache wählen. Bei mehrsprachiger Eingabe wechselt das nur "
                 + "das Layout und welche Sprache zuerst kommt; die Wörterbücher aller aktiven Sprachen gelten weiter.",
            "To switch, long-press the space bar and pick a language. With multilingual typing this only changes the layout and "
            + "which language comes first; the dictionaries of all active languages stay in use."));

        Section("Deutsch");
        Toggle(L.T("Eigene Tasten für ü, ö, ä", "Separate keys for ü, ö, ä"),
            L.T("Sonst per langem Druck auf u, o, a (ß auf s)", "Otherwise by long-pressing u, o, a (ß on s)"),
            _settings.UmlautKeys, (_, v) => _settings.UmlautKeys = v);
    }

    void BuildPreferences()
    {
        Section(L.T("Tasten", "Keys"));
        Toggle(L.T("Zahlenreihe", "Number row"), L.T("Ziffern 1–0 über den Buchstaben", "Digits 1–0 above the letters"), _settings.NumberRow, (_, v) => _settings.NumberRow = v);
        Toggle(L.T("Sprachwechseltaste anzeigen", "Show language switch key"), L.T("Taste mit Globus neben der Leertaste", "Globe key next to the space bar"),
            _settings.ShowLanguageKey, (_, v) => _settings.ShowLanguageKey = v);

        Section(L.T("Layout", "Layout"));
        Choice(L.T("Einhandmodus", "One-handed mode"),
        [
            (L.T("Aus", "Off"), _settings.OneHanded == OneHandedMode.Off, () => _settings.OneHanded = OneHandedMode.Off),
            (L.T("Rechts", "Right"), _settings.OneHanded == OneHandedMode.Right, () => _settings.OneHanded = OneHandedMode.Right),
            (L.T("Links", "Left"), _settings.OneHanded == OneHandedMode.Left, () => _settings.OneHanded = OneHandedMode.Left),
        ]);

        var heightLabel = Label($"{L.T("Tastaturhöhe", "Keyboard height")}: {_settings.HeightPercent} %", 17, _colors.Text);
        heightLabel.SetPadding(Dp(24), Dp(12), Dp(24), 0);
        _content.AddView(heightLabel);
        var height = new SeekBar(this) { Max = 50, Progress = _settings.HeightPercent - 80 };
        height.SetPadding(Dp(24), Dp(8), Dp(24), Dp(16));
        height.ProgressChanged += (_, e) =>
        {
            _settings.HeightPercent = 80 + e.Progress;
            heightLabel.Text = $"{L.T("Tastaturhöhe", "Keyboard height")}: {_settings.HeightPercent} %";
        };
        _content.AddView(height);

        Section(L.T("Tastendruck", "Key press"));
        Toggle(L.T("Ton bei Tastendruck", "Sound on keypress"), null, _settings.KeySound, (_, v) => _settings.KeySound = v);
        Toggle(L.T("Haptisches Feedback bei Tastendruck", "Haptic feedback on keypress"), null, _settings.Haptics, (_, v) => _settings.Haptics = v);
        Toggle(L.T("Pop-up bei Tastendruck", "Popup on keypress"), L.T("Vergrößerte Taste über dem Finger", "Enlarged key above your finger"), _settings.KeyPreview, (_, v) => _settings.KeyPreview = v);
    }

    void BuildTheme()
    {
        List<(string, bool, Action)> options =
        [
            (OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? L.T("Wie das System (Farben aus dem Hintergrundbild)", "System default (colors from your wallpaper)")
                    : L.T("Wie das System", "System default"),
                _settings.Theme is ThemeMode.System or ThemeMode.Dynamic, () => _settings.Theme = ThemeMode.System),
            (L.T("Hell", "Light"), _settings.Theme == ThemeMode.Light, () => _settings.Theme = ThemeMode.Light),
            (L.T("Dunkel", "Dark"), _settings.Theme == ThemeMode.Dark, () => _settings.Theme = ThemeMode.Dark),
        ];
        Choice(L.T("Design", "Theme"), options);
        Note(L.T("Die Einstellungen übernehmen das Design beim nächsten Öffnen.", "The settings pick up the theme the next time they open."));
    }

    void BuildCorrections()
    {
        Section(L.T("Vorschläge", "Suggestions"));
        Toggle(L.T("Vorschlagsleiste: nächstes Wort", "Next-word suggestions"), L.T("Vorschläge, bevor du zu tippen beginnst", "Suggestions before you start typing"),
            _settings.NextWordPrediction, (_, v) => _settings.NextWordPrediction = v);

        Section(L.T("Korrekturen", "Corrections"));
        Toggle(L.T("Autokorrektur", "Auto-correction"), L.T("Tippfehler beim Drücken der Leertaste korrigieren", "Fix typos when you press the space bar"), _settings.AutoCorrect, (_, v) => _settings.AutoCorrect = v);
        Toggle(L.T("Automatische Großschreibung", "Auto-capitalization"), L.T("Am Satzanfang und bei Substantiven", "At the start of sentences and for German nouns"), _settings.AutoCaps, (_, v) => _settings.AutoCaps = v);
        Toggle(L.T("Punkt durch doppeltes Leerzeichen", "Double-space period"), null, _settings.DoubleSpacePeriod, (_, v) => _settings.DoubleSpacePeriod = v);
    }

    void BuildGlide()
    {
        Toggle(L.T("Glide Typing aktivieren", "Enable glide typing"), L.T("Wörter eingeben, indem du von Buchstabe zu Buchstabe wischst", "Type words by sliding from letter to letter"),
            _settings.GlideTyping, (_, v) => _settings.GlideTyping = v);
        Note(L.T("Wischen über die Leertaste bewegt den Cursor. Wischen von der Löschtaste nach links markiert Wörter zum Löschen.", "Sliding along the space bar moves the cursor. Sliding left from the delete key selects words to delete."));
    }

    void BuildVoice()
    {
        Toggle(L.T("Nur Offline-Erkennung", "Offline recognition only"), L.T("Sprache verlässt das Gerät nicht (ab Android 12, je nach Hersteller)", "Your voice never leaves the device (Android 12+, depending on the manufacturer)"),
            _settings.VoiceOfflineOnly, (_, v) => _settings.VoiceOfflineOnly = v);
        Row(Resource.Drawable.ms_mic, L.T("Mikrofon freigeben", "Allow microphone"), L.T("Einmalig nötig für die Spracheingabe", "Needed once for voice typing"), RequestMicrophone);
        Note(L.T("Das Mikrofon rechts in der Vorschlagsleiste startet die Spracherkennung des Systems. Antippen während der "
               + "Aufnahme beendet sie.",
            "The microphone on the right of the suggestion bar starts the system's speech recognition. Tap while "
            + "recording to stop."));
    }

    void BuildClipboard()
    {
        Toggle(L.T("Zwischenablage-Verlauf", "Clipboard history"), L.T("Kopierte Texte eine Stunde lang anzeigen", "Show copied text for one hour"),
            _settings.ClipboardHistory, (_, v) => _settings.ClipboardHistory = v);
        Row(Resource.Drawable.ms_content_paste, L.T("Angeheftete Einträge löschen", "Delete pinned items"), null, () =>
        {
            ZBoardImeService.ClearPinnedClips(this);
            Toast.MakeText(this, L.T("Gilt ab dem nächsten Öffnen der Tastatur.", "Takes effect the next time the keyboard opens."), ToastLength.Short)!.Show();
        });
        Note(L.T("Nur Angeheftetes wird gespeichert. Als vertraulich markierte Inhalte, etwa aus Passwort-Managern, "
               + "übernimmt zBoard nie.",
            "Only pinned items are saved. zBoard never takes content marked as sensitive, e.g. from password managers."));
    }

    void BuildDictionary()
    {
        Toggle(L.T("Aus meinen Eingaben lernen", "Learn from my typing"), L.T("Häufig verwendete Wörter und Wortfolgen vorschlagen", "Suggest words and phrases you use often"),
            _settings.LearnWords, (_, v) => _settings.LearnWords = v);
        Row(Resource.Drawable.ms_menu_book, L.T("Gelernte Wörter löschen", "Delete learned words"), null, () =>
        {
            new AlertDialog.Builder(this)
                .SetTitle(L.T("Gelerntes löschen?", "Delete learned words?"))!
                .SetMessage(L.T("Alle gelernten Wörter und Wortfolgen werden entfernt.", "All learned words and phrases will be removed."))!
                .SetPositiveButton(L.T("Löschen", "Delete"), (_, _) =>
                {
                    ZBoardImeService.ClearLearned(this);
                    Toast.MakeText(this, L.T("Gelernte Wörter gelöscht.", "Learned words deleted."), ToastLength.Short)!.Show();
                })!
                .SetNegativeButton(L.T("Abbrechen", "Cancel"), (_, _) => { })!
                .Show();
        });
        Toggle(L.T("E-Mail-Adressen merken", "Remember email addresses"),
            L.T("In E-Mail-Feldern eingegebene Adressen wieder vorschlagen", "Suggest addresses you entered in email fields"),
            _settings.RememberEmails, (_, v) => _settings.RememberEmails = v);
        Row(Resource.Drawable.ms_menu_book, L.T("Gemerkte E-Mail-Adressen löschen", "Delete saved email addresses"), null, () =>
        {
            int count = ZBoardImeService.EmailCount(this);
            new AlertDialog.Builder(this)
                .SetTitle(L.T("E-Mail-Adressen löschen?", "Delete email addresses?"))!
                .SetMessage(L.T($"{count} gemerkte Adresse(n) werden entfernt.", $"{count} saved address(es) will be removed."))!
                .SetPositiveButton(L.T("Löschen", "Delete"), (_, _) =>
                {
                    ZBoardImeService.ClearEmails(this);
                    Toast.MakeText(this, L.T("E-Mail-Adressen gelöscht.", "Email addresses deleted."), ToastLength.Short)!.Show();
                })!
                .SetNegativeButton(L.T("Abbrechen", "Cancel"), (_, _) => { })!
                .Show();
        });
        Note(L.T("Gelerntes bleibt auf dem Gerät und verlässt es nur in einer Sicherung, die du selbst erstellst "
               + "(Sichern & wiederherstellen). In Passwort-, E-Mail- und Adressfeldern lernt "
               + "zBoard keine Wörter. E-Mail-Adressen merkt es sich nur, wenn sie in einem E-Mail-Feld stehen – nicht in "
               + "Inkognito-Fenstern und Feldern, in denen die App das Lernen untersagt.",
            "Learned words stay on the device and only leave it in a backup you create yourself (Backup & restore). "
            + "zBoard learns no words in password, email and "
            + "address fields. It only remembers email addresses entered in email fields – not in incognito windows "
            + "or fields where the app forbids learning."));
    }

    // ---------- Sichern & wiederherstellen ----------

    void BuildBackup()
    {
        Note(L.T("Sichere deine Einstellungen und dein persönliches Wörterbuch in eine Datei, etwa vor einem Gerätewechsel, "
               + "und stelle sie später wieder her – auf diesem oder einem anderen Gerät.",
            "Save your settings and your personal dictionary to a file, e.g. before switching phones, and restore them "
            + "later – on this or another device."));
        Row(Resource.Drawable.ms_save, L.T("Sicherung erstellen", "Create backup"),
            L.T("Einstellungen, gelernte Wörter und mehr in eine Datei speichern", "Save settings, learned words and more to a file"),
            ChooseBackupParts);
        Row(Resource.Drawable.ms_restore_page, L.T("Sicherung wiederherstellen", "Restore backup"),
            L.T("Eine zBoard-Sicherung öffnen und übernehmen", "Open a zBoard backup and apply it"), OpenBackup);
        Note(L.T("Die Sicherung ist eine unverschlüsselte ZIP-Datei und enthält persönliche Daten (gelernte Wörter, "
               + "E-Mail-Adressen, angeheftete Texte). zBoard selbst hat keinen Internetzugriff: Wohin die Datei geht, "
               + "bestimmst du in der Dateiauswahl – etwa in den Download-Ordner oder in einen Cloud-Speicher.",
            "The backup is an unencrypted ZIP file and contains personal data (learned words, email addresses, pinned "
            + "text). zBoard itself has no internet access: you choose where the file goes in the file picker – e.g. "
            + "your Downloads folder or a cloud storage app."));

        BuildSync();
    }

    // ---------- Synchronisation ----------

    void BuildSync()
    {
        Section(L.T("Synchronisation", "Sync"));
        var plugins = PluginRegistry.Installed(this).Where(p => p.CanSync).ToList();
        if (plugins.Count == 0)
        {
            Note(L.T("Mit dem Plugin „zBoard Sync“ gleicht zBoard Einstellungen, gelernte Wörter und E-Mail-Adressen "
                   + "automatisch zwischen deinen Geräten ab – über Google Drive, OneDrive, Dropbox oder WebDAV (z. B. "
                   + "HiDrive, Nextcloud). zBoard selbst bleibt dabei ohne Internet.",
                "With the plugin \"zBoard Sync\" zBoard keeps settings, learned words and email addresses in sync "
                + "between your devices – via Google Drive, OneDrive, Dropbox or WebDAV (e.g. HiDrive, Nextcloud). zBoard "
                + "itself stays offline."));
            Row(Resource.Drawable.ms_search, L.T("zBoard Sync im Play Store", "zBoard Sync on the Play Store"), null,
                () => OpenStore("de.World_of_Bytes.zBoard.Sync"));
            return;
        }

        var chosen = SyncManager.ChosenPlugin(this, _settings);
        Toggle(L.T("Automatisch synchronisieren", "Sync automatically"), SyncManager.StatusLine(_settings),
            _settings.SyncEnabled && chosen != null, (toggle, on) =>
            {
                if (!on)
                {
                    _settings.SyncEnabled = false;
                    return;
                }

                if (_settings.SyncEnabled && SyncManager.ChosenPlugin(this, _settings) != null) return;
                ChooseSyncPlugin(plugins, toggle);
            });

        Row(Resource.Drawable.ms_settings_backup_restore, L.T("Jetzt synchronisieren", "Sync now"), null, () =>
        {
            if (!_settings.SyncEnabled || SyncManager.ChosenPlugin(this, _settings) == null)
            {
                Toast.MakeText(this, L.T("Schalte zuerst „Automatisch synchronisieren“ ein.", "Turn on \"Sync automatically\" first."), ToastLength.Short)!.Show();
                return;
            }

            SyncNow();
        });

        foreach (var plugin in plugins)
        {
            if (PackageManager!.GetLaunchIntentForPackage(plugin.Package) is { } launch)
            {
                Row(Resource.Drawable.ms_settings, L.T($"Speicher wählen ({plugin.Label})", $"Choose storage ({plugin.Label})"),
                    L.T("Google Drive, OneDrive, Dropbox oder WebDAV", "Google Drive, OneDrive, Dropbox or WebDAV"), () => StartActivity(launch));
            }
        }

        Note(L.T("Abgeglichen werden Einstellungen, gelernte Wörter und Wortfolgen sowie E-Mail-Adressen – beim Öffnen und "
               + "Schließen der Tastatur, höchstens alle 15 Minuten. Gerätebezogenes (aktuelle Sprache, zuletzt verwendete "
               + "Emoji, angeheftete Zwischenablage, Plugins) bleibt auf dem Gerät. Wörter, die du löschst, kommen zurück, "
               + "solange ein anderes Gerät sie noch kennt.",
            "Synced are settings, learned words and phrases and email addresses – when the keyboard opens and closes, at "
            + "most every 15 minutes. Device-specific things (current language, recent emoji, pinned clipboard, plugins) "
            + "stay on the device. Words you delete come back as long as another device still knows them."));
    }

    void ChooseSyncPlugin(List<PluginInfo> plugins, Switch toggle)
    {
        if (plugins.Count == 1)
        {
            ConfirmSyncPlugin(plugins[0], toggle);
            return;
        }

        new AlertDialog.Builder(this)
            .SetTitle(L.T("Synchronisieren mit", "Sync with"))!
            .SetItems(plugins.Select(p => p.Label).ToArray(), (_, e) => ConfirmSyncPlugin(plugins[e.Which], toggle))!
            .SetOnCancelListener(new CancelListener(() => toggle.Checked = false))!
            .Show();
    }

    void ConfirmSyncPlugin(PluginInfo plugin, Switch toggle)
    {
        new AlertDialog.Builder(this)
            .SetTitle(L.T($"Mit {plugin.Label} synchronisieren?", $"Sync with {plugin.Label}?"))!
            .SetMessage(L.T($"{plugin.Label} bekommt deine Einstellungen, gelernten Wörter und E-Mail-Adressen und speichert sie "
                            + "in dem Cloud-Speicher, den du dort einrichtest. Schalte nur Plugins ein, denen du vertraust.",
                $"{plugin.Label} receives your settings, learned words and email addresses and stores them in the cloud "
                + "storage you set up there. Only turn on plugins you trust."))!
            .SetPositiveButton(L.T("Einschalten", "Turn on"), (_, _) =>
            {
                SetPluginEnabled(plugin.Package, true);
                _settings.SyncPlugin = plugin.Package;
                _settings.SyncEnabled = true;
                SyncNow();
            })!
            .SetNegativeButton(L.T("Abbrechen", "Cancel"), (_, _) => toggle.Checked = false)!
            .SetOnCancelListener(new CancelListener(() => toggle.Checked = false))!
            .Show();
    }

    async void SyncNow()
    {
        Toast.MakeText(this, L.T("Gleiche ab …", "Syncing …"), ToastLength.Short)!.Show();
        var outcome = await SyncManager.SyncAsync(this);
        if (IsFinishing || IsDestroyed) return;
        Toast.MakeText(this, outcome.Message, ToastLength.Long)!.Show();
        if (_page == "backup") ShowPage("backup"); // Zustandszeile und ggf. übernommene Einstellungen anzeigen
    }

    void OpenStore(string package)
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse($"market://details?id={package}")));
        }
        catch (ActivityNotFoundException)
        {
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse($"https://play.google.com/store/apps/details?id={package}")));
        }
    }

    static readonly (BackupParts Part, string Label)[] BackupChoices =
    [
        (BackupParts.Settings, L.T("Einstellungen", "Settings")),
        (BackupParts.Learned, L.T("Gelernte Wörter und Wortfolgen", "Learned words and phrases")),
        (BackupParts.Emails, L.T("Gemerkte E-Mail-Adressen", "Saved email addresses")),
        (BackupParts.PinnedClips, L.T("Angeheftete Einträge der Zwischenablage", "Pinned clipboard items")),
    ];

    void ChooseBackupParts()
    {
        var selected = BackupChoices.Select(c => _backupParts.HasFlag(c.Part)).ToArray();
        new AlertDialog.Builder(this)
            .SetTitle(L.T("Was soll gesichert werden?", "What should be backed up?"))!
            .SetMultiChoiceItems(BackupChoices.Select(c => c.Label).ToArray(), selected, (_, e) => selected[e.Which] = e.IsChecked)!
            .SetPositiveButton(L.T("Weiter", "Next"), (_, _) =>
            {
                var parts = BackupParts.None;
                for (int i = 0; i < BackupChoices.Length; i++)
                {
                    if (selected[i]) parts |= BackupChoices[i].Part;
                }

                if (parts == BackupParts.None)
                {
                    Toast.MakeText(this, L.T("Nichts ausgewählt.", "Nothing selected."), ToastLength.Short)!.Show();
                    return;
                }

                _backupParts = parts;
                var intent = new Intent(Intent.ActionCreateDocument);
                intent.AddCategory(Intent.CategoryOpenable);
                intent.SetType(BackupArchive.MimeType);
                intent.PutExtra(Intent.ExtraTitle, BackupArchive.SuggestedFileName(DateTime.Now, L.German));
                StartFilePicker(intent, CreateBackupRequest);
            })!
            .SetNegativeButton(L.T("Abbrechen", "Cancel"), (_, _) => { })!
            .Show();
    }

    void OpenBackup()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*"); // manche Speicher melden ZIP-Dateien als octet-stream
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { BackupArchive.MimeType, "application/x-zip-compressed", "application/octet-stream" });
        StartFilePicker(intent, OpenBackupRequest);
    }

    void StartFilePicker(Intent intent, int request)
    {
        try
        {
            StartActivityForResult(intent, request);
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, L.T("Auf diesem Gerät gibt es keine Dateiauswahl.", "This device has no file picker."), ToastLength.Long)!.Show();
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is not { } uri) return;
        if (requestCode == CreateBackupRequest) WriteBackup(uri);
        else if (requestCode == OpenBackupRequest) ReadBackup(uri);
    }

    void WriteBackup(Android.Net.Uri uri)
    {
        var parts = _backupParts;
        string version = PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName ?? string.Empty;
        var resolver = ContentResolver!;
        Task.Run(() =>
        {
            var backup = ZBoardImeService.CreateBackup(this, parts, version);
            using var stream = resolver.OpenOutputStream(uri, "w") ?? throw new IOException("Kein Ausgabestrom");
            BackupArchive.Write(stream, backup);
        }).ContinueWith(task => RunOnUiThread(() =>
        {
            if (task.IsCompletedSuccessfully)
            {
                Toast.MakeText(this, L.T("Sicherung gespeichert.", "Backup saved."), ToastLength.Short)!.Show();
                return;
            }

            try
            {
                Android.Provider.DocumentsContract.DeleteDocument(resolver, uri); // keine leere/halbe Datei zurücklassen
            }
            catch (Exception)
            {
                // nicht jeder Speicher erlaubt das Löschen
            }

            ShowBackupError(L.T("Die Sicherung konnte nicht gespeichert werden.", "The backup could not be saved."));
        }));
    }

    void ReadBackup(Android.Net.Uri uri)
    {
        var resolver = ContentResolver!;
        Task.Run(() =>
        {
            using var stream = resolver.OpenInputStream(uri) ?? throw new IOException("Kein Eingabestrom");
            return BackupArchive.Read(stream);
        }).ContinueWith(task => RunOnUiThread(() =>
        {
            if (task.IsCompletedSuccessfully)
            {
                ConfirmRestore(task.Result);
                return;
            }

            ShowBackupError((task.Exception?.InnerException as BackupException)?.Error switch
            {
                BackupError.NotABackup => L.T("Diese Datei ist keine zBoard-Sicherung.", "This file is not a zBoard backup."),
                BackupError.NewerVersion => L.T("Die Sicherung stammt aus einer neueren zBoard-Version. Bitte aktualisiere zBoard.",
                    "The backup was made with a newer version of zBoard. Please update zBoard."),
                BackupError.Damaged => L.T("Die Sicherung ist beschädigt.", "The backup is damaged."),
                _ => L.T("Die Datei konnte nicht gelesen werden.", "The file could not be read."),
            });
        }));
    }

    void ConfirmRestore(BackupData data)
    {
        if (data.IsEmpty)
        {
            ShowBackupError(L.T("Die Sicherung ist leer.", "The backup is empty."));
            return;
        }

        var lines = new List<string>();
        if (data.Settings != null) lines.Add(L.T("Einstellungen", "Settings"));
        if (data.Learned != null)
        {
            int words = data.Learned.Values.Sum(h => h.WordCount);
            string languages = string.Join(", ", data.Learned.Keys.Order(StringComparer.Ordinal)
                .Select(code => Layouts.LanguageNames.GetValueOrDefault(code, code)));
            lines.Add(L.T($"{words} gelernte Wörter", $"{words} learned words") + (languages.Length > 0 ? $" ({languages})" : string.Empty));
        }

        if (data.Emails != null) lines.Add(L.T($"{data.Emails.Count} E-Mail-Adresse(n)", $"{data.Emails.Count} email address(es)"));
        if (data.PinnedClips != null) lines.Add(L.T("Angeheftete Zwischenablage", "Pinned clipboard items"));

        string origin = data.Created == DateTimeOffset.MinValue
            ? string.Empty
            : L.T($"Erstellt am {data.Created.ToLocalTime():g}", $"Created {data.Created.ToLocalTime():g}")
              + (data.AppVersion.Length > 0 ? L.T($" mit zBoard {data.AppVersion}", $" with zBoard {data.AppVersion}") : string.Empty)
              + ".\n\n";

        new AlertDialog.Builder(this)
            .SetTitle(L.T("Sicherung wiederherstellen?", "Restore backup?"))!
            .SetMessage(origin + L.T("Enthält:", "Contains:") + "\n" + string.Join("\n", lines.Select(l => "• " + l)) + "\n\n"
                        + L.T("Diese Teile ersetzen die aktuellen Daten auf diesem Gerät.", "These parts replace the current data on this device."))!
            .SetPositiveButton(L.T("Wiederherstellen", "Restore"), (_, _) =>
            {
                try
                {
                    ZBoardImeService.Restore(this, data);
                }
                catch (Exception)
                {
                    ShowBackupError(L.T("Die Sicherung konnte nicht übernommen werden.", "The backup could not be applied."));
                    return;
                }

                Toast.MakeText(this, L.T("Sicherung wiederhergestellt.", "Backup restored."), ToastLength.Short)!.Show();
                Recreate(); // Design und Sprachen der Einstellungen neu aufbauen
            })!
            .SetNegativeButton(L.T("Abbrechen", "Cancel"), (_, _) => { })!
            .Show();
    }

    void ShowBackupError(string message)
    {
        new AlertDialog.Builder(this)
            .SetTitle(L.T("Sichern & wiederherstellen", "Backup & restore"))!
            .SetMessage(message)!
            .SetPositiveButton("OK", (_, _) => { })!
            .Show();
    }

    void BuildPlugins()
    {
        Note(L.T("zBoard selbst hat keinen Internetzugriff. Funktionen, die Internet brauchen – etwa Übersetzen mit DeepL –, "
               + "gibt es als eigene Plugin-Apps; nur deren Installation fragt nach Internet. Ein Plugin bekommt erst Text, "
               + "wenn du es hier einschaltest und in der Leiste über das Puzzle-Symbol eine Aktion antippst – dann den "
               + "markierten Text oder, ohne Markierung, den Inhalt des Felds. In Passwortfeldern gibt es keine Plugins.",
            "zBoard itself has no internet access. Features that need the internet – such as translating with DeepL – come "
            + "as separate plugin apps; only installing those asks for internet. A plugin only gets text once you turn it "
            + "on here and tap one of its actions via the puzzle icon in the bar – the selected text or, without a "
            + "selection, the field's content. Plugins are never offered in password fields."));

        var plugins = Plugins.PluginRegistry.Installed(this);
        Section(L.T("Installierte Plugins", "Installed plugins"));
        if (plugins.Count == 0) Note(L.T("Noch kein Plugin installiert.", "No plugins installed yet."));

        foreach (var plugin in plugins)
        {
            var details = new List<string> { plugin.Package };
            details.Add(plugin.HasInternet ? L.T("nutzt Internet", "uses the internet") : L.T("ohne Internet", "no internet"));
            if (plugin.SameSigner) details.Add(L.T("vom selben Entwickler wie zBoard", "same developer as zBoard"));

            Toggle(plugin.Label, string.Join(" · ", details), _settings.EnabledPlugins.Contains(plugin.Package), (toggle, on) =>
            {
                if (!on)
                {
                    SetPluginEnabled(plugin.Package, false);
                    return;
                }

                if (_settings.EnabledPlugins.Contains(plugin.Package)) return;
                new AlertDialog.Builder(this)
                    .SetTitle(L.T($"{plugin.Label} einschalten?", $"Turn on {plugin.Label}?"))!
                    .SetMessage(plugin.CanSync
                        ? L.T("Das Plugin bekommt deine Einstellungen, gelernten Wörter und E-Mail-Adressen, sobald du unter "
                              + "„Sichern & wiederherstellen“ die Synchronisation einschaltest, und speichert sie in deinem Cloud-Speicher.",
                            "The plugin receives your settings, learned words and email addresses once you turn on sync under "
                            + "\"Backup & restore\", and stores them in your cloud storage.")
                        : plugin.HasInternet
                        ? L.T("Text, den du an dieses Plugin schickst, kann es ins Internet senden (z. B. an den Übersetzungsdienst). "
                              + "Schalte nur Plugins ein, denen du vertraust.",
                            "Text you send to this plugin may be sent to the internet (e.g. to the translation service). "
                            + "Only turn on plugins you trust.")
                        : L.T("Das Plugin bekommt nur Text, den du ihm über die Leiste ausdrücklich schickst.",
                            "The plugin only gets text you explicitly send to it from the bar."))!
                    .SetPositiveButton(L.T("Einschalten", "Turn on"), (_, _) => SetPluginEnabled(plugin.Package, true))!
                    .SetNegativeButton(L.T("Abbrechen", "Cancel"), (_, _) => toggle.Checked = false)!
                    .SetOnCancelListener(new CancelListener(() => toggle.Checked = false))!
                    .Show();
            });

            if (PackageManager!.GetLaunchIntentForPackage(plugin.Package) is { } launch)
            {
                Row(Resource.Drawable.ms_settings, L.T($"{plugin.Label} einrichten", $"Set up {plugin.Label}"), null, () => StartActivity(launch));
            }
        }

        Row(Resource.Drawable.ms_search, L.T("Plugins im Play Store suchen", "Find plugins on the Play Store"), null, () =>
        {
            var uri = Android.Net.Uri.Parse("market://search?q=zBoard%20Plugin")!;
            try
            {
                StartActivity(new Intent(Intent.ActionView, uri));
            }
            catch (ActivityNotFoundException)
            {
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://play.google.com/store/search?q=zBoard%20Plugin&c=apps")));
            }
        });
    }

    void SetPluginEnabled(string package, bool enabled)
    {
        var set = _settings.EnabledPlugins.ToHashSet(StringComparer.Ordinal);
        if (enabled) set.Add(package);
        else set.Remove(package);
        _settings.EnabledPlugins = set;
    }

    sealed class CancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => onCancel();
    }

    void BuildTest()
    {
        var input = new EditText(this)
        {
            Hint = L.T("Hier tippen, um zBoard zu testen", "Type here to try zBoard"),
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagCapSentences
                        | Android.Text.InputTypes.TextFlagMultiLine,
        };
        input.SetTextColor(_colors.Text);
        input.SetHintTextColor(_colors.HintText);
        input.SetMinLines(3);
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.SetMargins(Dp(24), Dp(8), Dp(24), Dp(8));
        _content.AddView(input, lp);
        input.RequestFocus();
    }

    void BuildAbout()
    {
        Note($"Version {PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName}\n\n"
             + "© 2026 World of Bytes, Patrick Zeriadtke – world-of-bytes.de\n"
             + L.T("zBoard ist freie Software unter der BSD 2-Clause License.", "zBoard is free software under the BSD 2-Clause License.")
             + "\n" + L.T("Datenschutz", "Privacy") + ": world-of-bytes.de/zboard/datenschutz\n\n"
             + L.T("Wortlisten (CC BY-SA 4.0): FrequencyWords von Hermit Dave auf Basis von OpenSubtitles 2018; "
                   + "Großschreibung deutscher Substantive nach german-nouns von Gregor Weichbrodt auf Basis von Wiktionary. "
                   + "Lizenz: creativecommons.org/licenses/by-sa/4.0\n\n"
                   + "Wortpaare für die Vorhersage (CC BY 3.0): Google Books Ngram Corpus, aufbereitet von "
                   + "orgtre/google-books-ngram-frequency. Lizenz: creativecommons.org/licenses/by/3.0\n\n"
                   + "Emoji-Daten: unicode-emoji-json von Mu-An Chiou (MIT); Namen und Suchbegriffe: Unicode CLDR "
                   + "(Unicode License v3), © Unicode, Inc.\n\n"
                   + "Symbole: Material Symbols von Google (Apache License 2.0).",
                 "Word lists (CC BY-SA 4.0): FrequencyWords by Hermit Dave based on OpenSubtitles 2018; "
                   + "capitalization of German nouns from german-nouns by Gregor Weichbrodt based on Wiktionary. "
                   + "License: creativecommons.org/licenses/by-sa/4.0\n\n"
                   + "Word pairs for prediction (CC BY 3.0): Google Books Ngram Corpus, prepared by "
                   + "orgtre/google-books-ngram-frequency. License: creativecommons.org/licenses/by/3.0\n\n"
                   + "Emoji data: unicode-emoji-json by Mu-An Chiou (MIT); names and keywords: Unicode CLDR "
                   + "(Unicode License v3), © Unicode, Inc.\n\n"
                   + "Icons: Material Symbols by Google (Apache License 2.0)."));
    }

    // ---------- Mikrofon ----------

    void RequestMicrophone()
    {
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Android.Content.PM.Permission.Granted)
        {
            Toast.MakeText(this, L.T("Das Mikrofon ist bereits freigegeben.", "The microphone is already allowed."), ToastLength.Short)!.Show();
            return;
        }

        RequestPermissions([Android.Manifest.Permission.RecordAudio], MicRequest);
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != MicRequest) return;
        bool granted = grantResults.Length > 0 && grantResults[0] == Android.Content.PM.Permission.Granted;
        Toast.MakeText(this,
            granted
                ? L.T("Mikrofon freigegeben. Tippe in der Tastatur erneut auf das Mikrofon.", "Microphone allowed. Tap the microphone in the keyboard again.")
                : L.T("Ohne Mikrofon ist keine Spracheingabe möglich.", "Voice input needs the microphone."),
            ToastLength.Long)!.Show();
        if (granted && Intent?.GetBooleanExtra(ZBoardImeService.RequestMicExtra, false) == true) Finish();
    }

    // ---------- Farben ----------

    KeyboardTheme CurrentColors()
    {
        bool systemDark = (Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;
        return _settings.Theme switch
        {
            ThemeMode.Light => KeyboardTheme.Light,
            ThemeMode.Dark => KeyboardTheme.Dark,
            _ when OperatingSystem.IsAndroidVersionAtLeast(31) => KeyboardTheme.FromSystem(this, systemDark),
            _ => systemDark ? KeyboardTheme.Dark : KeyboardTheme.Light,
        };
    }

    bool IsDark => (0.299 * _colors.Background.R + 0.587 * _colors.Background.G + 0.114 * _colors.Background.B) / 255 < 0.5;

    void ApplySystemBars()
    {
        var window = Window!;
        // Helle Statusleisten-Symbole auf dunklem Hintergrund und umgekehrt
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var mask = (int)WindowInsetsControllerAppearance.LightStatusBars | (int)WindowInsetsControllerAppearance.LightNavigationBars;
            window.InsetsController?.SetSystemBarsAppearance(IsDark ? 0 : mask, mask);
        }

#pragma warning disable CA1422, CS0618 // bis Android 14 wirksam, danach zeichnet die App hinter den Leisten
        window.SetStatusBarColor(_colors.Background);
        window.SetNavigationBarColor(_colors.Background);
#pragma warning restore CA1422, CS0618
    }

    /// <summary>Ab Android 15 liegt der Inhalt unter Status- und Navigationsleiste: Abstand halten.</summary>
    sealed class SystemBarPadding : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                view.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }

            return insets;
        }
    }

    // ---------- Bausteine ----------

    InputMethodManager InputMethods() =>
        (InputMethodManager)GetSystemService(Context.InputMethodService)!;

    int Dp(float value) => (int)(value * _dp);

    TextView Label(string text, float sp, Color color)
    {
        var view = new TextView(this) { Text = text, TextSize = sp };
        view.SetTextColor(color);
        return view;
    }

    Android.Graphics.Drawables.GradientDrawable Rounded(Color color, float radiusDp)
    {
        var shape = new Android.Graphics.Drawables.GradientDrawable();
        shape.SetColor(color);
        shape.SetCornerRadius(radiusDp * _dp);
        return shape;
    }

    /// <summary>Unterseite: Zurück-Pfeil und großer Titel.</summary>
    void PageHeader(string title)
    {
        var back = new ImageView(this) { ContentDescription = L.T("Zurück", "Back") };
        back.SetImageResource(Resource.Drawable.ms_arrow_back);
        back.SetColorFilter(_colors.Text);
        back.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
        back.Click += (_, _) => ShowPage(Main);
        var lp = new LinearLayout.LayoutParams(Dp(48), Dp(48));
        lp.SetMargins(Dp(8), Dp(16), 0, 0);
        _content.AddView(back, lp);

        var heading = Label(title, 32, _colors.Text);
        heading.SetPadding(Dp(24), Dp(40), Dp(24), Dp(16));
        _content.AddView(heading);
    }

    void Section(string title)
    {
        var view = Label(title, 14, _colors.Accent);
        view.SetPadding(Dp(24), Dp(20), Dp(24), Dp(4));
        _content.AddView(view);
    }

    void Note(string text)
    {
        var view = Label(text, 14, _colors.HintText);
        view.SetPadding(Dp(24), Dp(12), Dp(24), Dp(12));
        _content.AddView(view);
    }

    /// <summary>Eintrag wie in den Gboard-Einstellungen: Symbol, Titel, optional Untertitel.</summary>
    void Row(int icon, string title, string? subtitle, Action onClick)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(24), Dp(18), Dp(24), Dp(18));
        row.Foreground = Ripple();
        row.Click += (_, _) => onClick();

        var image = new ImageView(this);
        image.SetImageResource(icon);
        image.SetColorFilter(_colors.Text);
        var iconParams = new LinearLayout.LayoutParams(Dp(26), Dp(26)) { MarginEnd = Dp(28) };
        row.AddView(image, iconParams);

        var texts = new LinearLayout(this) { Orientation = Orientation.Vertical };
        texts.AddView(Label(title, 20, _colors.Text));
        if (!string.IsNullOrEmpty(subtitle)) texts.AddView(Label(subtitle, 14, _colors.HintText));
        row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _content.AddView(row);
    }

    /// <summary>Schalter rechts, Titel und Untertitel links.</summary>
    void Toggle(string title, string? subtitle, bool value, Action<Switch, bool> onChange)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(24), Dp(14), Dp(16), Dp(14));

        var texts = new LinearLayout(this) { Orientation = Orientation.Vertical };
        texts.AddView(Label(title, 18, _colors.Text));
        if (!string.IsNullOrEmpty(subtitle)) texts.AddView(Label(subtitle, 14, _colors.HintText));
        row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var toggle = new Switch(this) { Checked = value };
        toggle.CheckedChange += (_, e) => onChange(toggle, e.IsChecked);
        row.AddView(toggle);

        row.Clickable = true;
        row.Foreground = Ripple();
        row.Click += (_, _) => toggle.Toggle();
        _content.AddView(row);
    }

    /// <summary>Auswahl aus mehreren Möglichkeiten (Optionsfelder).</summary>
    void Choice(string title, List<(string Label, bool Selected, Action Select)> options)
    {
        Section(title);
        var group = new RadioGroup(this) { Orientation = Orientation.Vertical };
        group.SetPadding(Dp(16), 0, Dp(24), 0);
        foreach (var (label, selected, select) in options)
        {
            var radio = new RadioButton(this) { Text = label, Id = View.GenerateViewId(), TextSize = 18 };
            radio.SetTextColor(_colors.Text);
            radio.SetPadding(Dp(12), Dp(12), 0, Dp(12));
            group.AddView(radio);
            if (selected) radio.Checked = true;
            radio.CheckedChange += (_, e) =>
            {
                if (e.IsChecked) select();
            };
        }

        _content.AddView(group);
    }

    Button TextButton(string text, Action onClick)
    {
        var button = new Button(this) { Text = text };
        button.SetAllCaps(false);
        button.SetTextColor(_colors.Accent);
        button.Background = null;
        button.Click += (_, _) => onClick();
        return button;
    }

    Android.Graphics.Drawables.Drawable? Ripple()
    {
        var attrs = ObtainStyledAttributes([Android.Resource.Attribute.SelectableItemBackground]);
        var drawable = attrs.GetDrawable(0);
        attrs.Recycle();
        return drawable;
    }
}
