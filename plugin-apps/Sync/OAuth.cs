using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZBoard.Sync;

/// <summary>OAuth-Einstellungen eines Anbieters.</summary>
public sealed record OAuthProvider(
    StoreKind Kind,
    string AuthorizeUrl,
    string TokenUrl,
    string ClientId,
    string Scope,
    string RedirectUri,
    IReadOnlyDictionary<string, string> ExtraAuthorizeParameters);

public sealed class SyncException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// OAuth 2.0 für native Apps (RFC 8252): Anmeldung im Browser mit PKCE (S256), Rücksprung über ein eigenes URI-Schema,
/// danach nur noch Refresh-Token. Kein Client-Secret – das könnte eine Open-Source-App ohnehin nicht geheim halten.
/// </summary>
public static class OAuth
{
    /// <summary>
    /// Verwalteter Handler statt des Android-Standards: HttpURLConnection kennt weder PATCH (Google Drive) noch MKCOL
    /// (WebDAV). Bei Weiterleitungen entfernt er das Authorization-Kopffeld.
    /// </summary>
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public static OAuthProvider? For(StoreKind kind) => kind switch
    {
        StoreKind.GoogleDrive when SyncConfig.HasGoogle => new(kind,
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            SyncConfig.GoogleClientId,
            "https://www.googleapis.com/auth/drive.appdata",
            SyncConfig.GoogleRedirectUri,
            new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" }),

        StoreKind.OneDrive when SyncConfig.HasMicrosoft => new(kind,
            "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            SyncConfig.MicrosoftClientId,
            "Files.ReadWrite.AppFolder offline_access",
            SyncConfig.MicrosoftRedirectUri,
            new Dictionary<string, string> { ["prompt"] = "select_account" }),

        StoreKind.Dropbox when SyncConfig.HasDropbox => new(kind,
            "https://www.dropbox.com/oauth2/authorize",
            "https://api.dropboxapi.com/oauth2/token",
            SyncConfig.DropboxAppKey,
            string.Empty, // Berechtigungen legt die App Console fest (files.content.read/write)
            SyncConfig.DropboxRedirectUri,
            new Dictionary<string, string> { ["token_access_type"] = "offline" }),

        _ => null,
    };

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewRandom() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Adresse der Anmeldeseite.</summary>
    public static string AuthorizeUrl(OAuthProvider provider, string state, string verifier)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = provider.RedirectUri,
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };
        if (provider.Scope.Length > 0) parameters["scope"] = provider.Scope;
        foreach (var (key, value) in provider.ExtraAuthorizeParameters) parameters[key] = value;

        return provider.AuthorizeUrl + "?" + string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    /// <summary>Code aus dem Rücksprung gegen Tokens tauschen und speichern.</summary>
    public static async Task ExchangeCodeAsync(OAuthProvider provider, string code, string verifier, SyncSettings settings)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = provider.RedirectUri,
            ["client_id"] = provider.ClientId,
            ["code_verifier"] = verifier,
        };
        var tokens = await TokenRequestAsync(provider, form);
        if (tokens.Refresh.Length == 0)
        {
            throw new SyncException(L.T("Der Anbieter hat keine dauerhafte Anmeldung erlaubt.", "The provider did not allow a lasting sign-in."));
        }

        settings.Kind = provider.Kind;
        settings.RefreshToken = tokens.Refresh;
        Store(tokens, settings);
    }

    /// <summary>Gültiges Access-Token, bei Bedarf mit dem Refresh-Token erneuert.</summary>
    public static async Task<string> AccessTokenAsync(OAuthProvider provider, SyncSettings settings)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string current = settings.AccessToken;
        if (current.Length > 0 && settings.AccessTokenExpiresMs - now > 60_000) return current;

        string refresh = settings.RefreshToken;
        if (refresh.Length == 0) throw new SyncException(L.T("Bitte in zBoard Sync neu anmelden.", "Please sign in again in zBoard Sync."));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = provider.ClientId,
        };
        if (provider.Kind == StoreKind.OneDrive) form["scope"] = provider.Scope;

        var tokens = await TokenRequestAsync(provider, form);
        if (tokens.Refresh.Length > 0) settings.RefreshToken = tokens.Refresh; // Microsoft tauscht es bei jeder Erneuerung
        Store(tokens, settings);
        return tokens.Access;
    }

    static void Store((string Access, string Refresh, long ExpiresIn) tokens, SyncSettings settings)
    {
        settings.AccessToken = tokens.Access;
        settings.AccessTokenExpiresMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + tokens.ExpiresIn * 1000;
    }

    static async Task<(string Access, string Refresh, long ExpiresIn)> TokenRequestAsync(OAuthProvider provider, Dictionary<string, string> form)
    {
        using var response = await Http.PostAsync(provider.TokenUrl, new FormUrlEncodedContent(form));
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            throw new SyncException(L.T($"Anmeldung fehlgeschlagen (HTTP {(int)response.StatusCode}).", $"Sign-in failed (HTTP {(int)response.StatusCode})."), e);
        }

        using (json)
        {
            var root = json.RootElement;
            if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access))
            {
                string error = root.TryGetProperty("error", out var e) ? e.ToString() : $"HTTP {(int)response.StatusCode}";
                if (error is "invalid_grant") throw new SyncException(L.T("Die Anmeldung ist abgelaufen. Bitte in zBoard Sync neu anmelden.", "The sign-in has expired. Please sign in again in zBoard Sync."));
                throw new SyncException(L.T($"Anmeldung fehlgeschlagen ({error}).", $"Sign-in failed ({error})."));
            }

            string refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            long expires = root.TryGetProperty("expires_in", out var x) && x.TryGetInt64(out long seconds) ? seconds : 3600;
            return (access.GetString() ?? string.Empty, refresh, expires);
        }
    }

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
}

/// <summary>Texte auf Deutsch oder Englisch je nach Systemsprache.</summary>
public static class L
{
    public static string T(string german, string english) => Java.Util.Locale.Default.Language == "de" ? german : english;
}
