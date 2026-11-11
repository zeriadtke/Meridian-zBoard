# zBoard Sync

Plugin-App für zBoard: gleicht **Einstellungen, gelernte Wörter und Wortfolgen sowie E-Mail-Adressen** zwischen
Geräten ab – über **Google Drive**, **OneDrive**, **Dropbox** oder **WebDAV** (HiDrive, Nextcloud, ownCloud, NAS …).
zBoard selbst bleibt ohne Internet-Berechtigung; nur diese App hat sie.

## So funktioniert es

- zBoard stellt eine Sicherungsdatei bereit (Format wie „Sichern & wiederherstellen“) und reicht sie über einen
  eigenen Content-Provider mit befristeter Freigabe an das Plugin weiter. Das Plugin lädt sie als
  `zboard-sync.zip` in den gewählten Speicher bzw. holt sie von dort ab – es ist **nur der Transport**.
- Das **Zusammenführen macht zBoard**: gelernte Wörter, Wortfolgen und E-Mail-Adressen werden vereint (je Eintrag
  die höhere Anzahl), bei den Einstellungen gewinnt die zuletzt geänderte Seite. Ein Gerät, das zum ersten Mal
  abgleicht, übernimmt die Einstellungen aus der Cloud.
- Nicht synchronisiert: aktuelle Tastatursprache, zuletzt verwendete Emoji, angeheftete Zwischenablage,
  Plugin-Freigaben und die Synchronisations-Einstellungen selbst.
- zBoard gleicht beim Öffnen und Schließen der Tastatur ab (höchstens alle 15 Minuten) und auf Knopfdruck
  (Einstellungen → Sichern & wiederherstellen → Jetzt synchronisieren).
- Bekannte Grenze: Gelöschte Wörter kommen zurück, solange ein anderes Gerät sie noch kennt.

Speicherorte: Google Drive im versteckten App-Ordner (`appDataFolder`), OneDrive in „Apps/zBoard Sync“, Dropbox in
„Apps/&lt;App-Name&gt;“, WebDAV im Ordner „zBoard“. Bei den ersten drei sieht das Plugin keine anderen Dateien.
Tokens und WebDAV-Passwort liegen mit einem Schlüssel aus dem Android Keystore verschlüsselt im privaten Speicher.

## Einrichtung der Anbieter (einmalig, durch den Herausgeber)

Alle drei OAuth-Anbieter nutzen **Authorization Code mit PKCE** ohne Client-Secret. Die Client-IDs sind öffentlich
und werden in `SyncConfig.cs` eingetragen. Solange eine ID leer ist, zeigt die App den Anbieter als „nicht
eingerichtet“; WebDAV funktioniert immer.

### Google Drive

1. [Google Cloud Console](https://console.cloud.google.com/): Projekt anlegen, **Google Drive API** aktivieren.
2. **OAuth-Zustimmungsbildschirm**: Typ „Extern“, App-Name „zBoard Sync“, Datenschutz-URL, Bereich
   `https://www.googleapis.com/auth/drive.appdata` hinzufügen (nicht vertraulich – keine Sicherheitsprüfung nötig).
   Zum Veröffentlichen den Status auf „In Produktion“ setzen.
3. **Anmeldedaten → OAuth-Client-ID**, Typ **iOS** mit Bundle-ID `de.World_of_Bytes.zBoard.Sync`
   (dieser Typ erlaubt den Rücksprung `com.googleusercontent.apps.<ID>:/oauth2redirect`). Alternativ Typ „Android“
   mit Paketname, SHA-1 des Upload- und des Play-App-Signaturschlüssels und unter „Erweiterte Einstellungen“
   aktiviertem benutzerdefiniertem URI-Schema.
4. Den Teil der Client-ID **vor** `.apps.googleusercontent.com` in `SyncConfig.GoogleClientIdPrefix` eintragen.

### OneDrive (Microsoft Entra)

1. [Entra Admin Center](https://entra.microsoft.com/) → App-Registrierungen → Neue Registrierung, unterstützte Konten:
   „Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten“.
2. Authentifizierung → Plattform hinzufügen → **Mobil- und Desktopanwendungen**, benutzerdefinierte Umleitungs-URI
   `de.worldofbytes.zboardsync://oauth/onedrive`. „Öffentliche Clientflows zulassen“: Ja.
3. API-Berechtigungen → Microsoft Graph → delegiert: `Files.ReadWrite.AppFolder`, `offline_access`.
4. Anwendungs-ID (Client-ID) in `SyncConfig.MicrosoftClientId` eintragen.

### Dropbox

1. [Dropbox App Console](https://www.dropbox.com/developers/apps) → Create app → Scoped access → **App folder**,
   Name z. B. „zBoard Sync“.
2. Permissions: `files.content.read`, `files.content.write` (danach „Submit“).
3. Settings → OAuth 2 → Redirect URIs: `de.worldofbytes.zboardsync://oauth/dropbox`; „Allow public clients
   (Implicit Grant & PKCE)“: Allow.
4. App key in `SyncConfig.DropboxAppKey` eintragen. Für mehr als 50 Nutzer in der App Console „Apply for
   production“.

### WebDAV / HiDrive

Keine Registrierung nötig. Nutzer tragen Adresse, Benutzername und Passwort ein (nur `https://`).
HiDrive: Button „HiDrive-Adresse einsetzen“ erzeugt `https://webdav.hidrive.strato.com/users/<benutzer>/`;
WebDAV muss im HiDrive-Tarif enthalten sein. Nextcloud: `https://<server>/remote.php/dav/files/<benutzer>/`
mit einem App-Passwort.

## Bauen

```bash
dotnet build plugin-apps/Sync/ZBoard.Sync.csproj -t:Run -f net10.0-android
```

Dateien: `PluginService.cs` (Dienst für zBoard), `MainActivity.cs` (Einrichtung, Rücksprung aus dem Browser),
`OAuth.cs` (PKCE, Tokens), `Stores/` (Google Drive, OneDrive, Dropbox, WebDAV), `SyncSettings.cs`
(Zugangsdaten, verschlüsselt), `SyncConfig.cs` (Client-IDs).

---

## English summary

zBoard Sync syncs zBoard's settings, learned words and email addresses via Google Drive (hidden app data folder),
OneDrive (app folder), Dropbox (app folder) or WebDAV (HiDrive, Nextcloud …). zBoard itself stays offline: it hands a
backup file to this plugin through its own content provider with a temporary URI grant, and merges the downloaded data
itself (union of learned data, newest settings win). OAuth uses authorization code + PKCE without client secrets;
enter the public client IDs in `SyncConfig.cs` (setup steps above). Redirect URIs:
`com.googleusercontent.apps.<ID>:/oauth2redirect` (Google, iOS-type client), `de.worldofbytes.zboardsync://oauth/onedrive`
(Microsoft, mobile & desktop platform), `de.worldofbytes.zboardsync://oauth/dropbox` (Dropbox, public clients allowed).
