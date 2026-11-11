namespace ZBoard.Sync;

/// <summary>
/// Öffentliche Client-IDs der Cloud-Anbieter (OAuth 2.0 mit PKCE, ohne Client-Secret – die IDs sind nicht geheim).
/// Einrichtung bei den Anbietern: plugin-apps/Sync/README.md. Ist eine ID leer, bietet die App den Anbieter
/// nicht zur Anmeldung an; WebDAV braucht keine.
/// </summary>
public static class SyncConfig
{
    /// <summary>
    /// Google Cloud: OAuth-Client vom Typ „iOS“ (Bundle-ID = Paketname) oder „Android“ mit aktiviertem benutzerdefiniertem
    /// URI-Schema. Hier nur der Teil vor ".apps.googleusercontent.com", z. B. "123456789012-abc123def456".
    /// </summary>
    public const string GoogleClientIdPrefix = "";

    /// <summary>Microsoft Entra: Anwendungs-ID (Client-ID) der App-Registrierung, z. B. "0b1c2d3e-…".</summary>
    public const string MicrosoftClientId = "";

    /// <summary>Dropbox App Console: „App key“ einer App mit Zugriff „App folder“.</summary>
    public const string DropboxAppKey = "";

    // ---------- abgeleitet ----------

    public const string GoogleClientId = GoogleClientIdPrefix + ".apps.googleusercontent.com";

    /// <summary>Google verlangt als Rücksprung das umgedrehte Client-ID-Schema.</summary>
    public const string GoogleRedirectScheme = "com.googleusercontent.apps." + GoogleClientIdPrefix;

    public const string GoogleRedirectUri = GoogleRedirectScheme + ":/oauth2redirect";

    /// <summary>Rücksprung für Microsoft und Dropbox (in beiden Konsolen genau so eintragen).</summary>
    public const string RedirectScheme = "de.worldofbytes.zboardsync";

    public const string MicrosoftRedirectUri = RedirectScheme + "://oauth/onedrive";

    public const string DropboxRedirectUri = RedirectScheme + "://oauth/dropbox";

    public static bool HasGoogle => GoogleClientIdPrefix.Length > 0;

    public static bool HasMicrosoft => MicrosoftClientId.Length > 0;

    public static bool HasDropbox => DropboxAppKey.Length > 0;
}
