using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace ZBoard.Sync;

/// <summary>Cloud-Speicher, die das Plugin kennt.</summary>
public enum StoreKind
{
    None,
    GoogleDrive,
    OneDrive,
    Dropbox,
    WebDav,
}

/// <summary>
/// Gewählter Speicher und Zugangsdaten. Alles im privaten Speicher der App; Tokens und Passwort zusätzlich mit einem
/// Schlüssel aus dem Android Keystore verschlüsselt (der Schlüssel verlässt das Gerät nicht).
/// </summary>
public sealed class SyncSettings(Context context)
{
    readonly ISharedPreferences _prefs = context.GetSharedPreferences("sync", FileCreationMode.Private)!;

    public StoreKind Kind
    {
        get => Enum.TryParse<StoreKind>(_prefs.GetString("kind", null), out var kind) ? kind : StoreKind.None;
        set => _prefs.Edit()!.PutString("kind", value.ToString())!.Apply();
    }

    // ---------- OAuth (Google Drive, OneDrive, Dropbox) ----------

    public string RefreshToken
    {
        get => Secret.Decrypt(_prefs.GetString("refreshToken", null));
        set => _prefs.Edit()!.PutString("refreshToken", Secret.Encrypt(value))!.Apply();
    }

    public string AccessToken
    {
        get => Secret.Decrypt(_prefs.GetString("accessToken", null));
        set => _prefs.Edit()!.PutString("accessToken", Secret.Encrypt(value))!.Apply();
    }

    public long AccessTokenExpiresMs
    {
        get => _prefs.GetLong("accessExpires", 0);
        set => _prefs.Edit()!.PutLong("accessExpires", value)!.Apply();
    }

    /// <summary>Laufende Anmeldung im Browser: Anbieter, state und PKCE-Verifier (überlebt das Neuerstellen der Activity).</summary>
    public (StoreKind Kind, string State, string Verifier) PendingLogin
    {
        get => (Enum.TryParse<StoreKind>(_prefs.GetString("pendingKind", null), out var k) ? k : StoreKind.None,
            _prefs.GetString("pendingState", string.Empty) ?? string.Empty,
            Secret.Decrypt(_prefs.GetString("pendingVerifier", null)));
        set => _prefs.Edit()!
            .PutString("pendingKind", value.Kind.ToString())!
            .PutString("pendingState", value.State)!
            .PutString("pendingVerifier", Secret.Encrypt(value.Verifier))!
            .Apply();
    }

    // ---------- WebDAV (HiDrive, Nextcloud …) ----------

    public string WebDavUrl
    {
        get => _prefs.GetString("webdavUrl", string.Empty) ?? string.Empty;
        set => _prefs.Edit()!.PutString("webdavUrl", value.Trim())!.Apply();
    }

    public string WebDavUser
    {
        get => _prefs.GetString("webdavUser", string.Empty) ?? string.Empty;
        set => _prefs.Edit()!.PutString("webdavUser", value.Trim())!.Apply();
    }

    public string WebDavPassword
    {
        get => Secret.Decrypt(_prefs.GetString("webdavPassword", null));
        set => _prefs.Edit()!.PutString("webdavPassword", Secret.Encrypt(value))!.Apply();
    }

    /// <summary>Eingerichtet (angemeldet bzw. Zugangsdaten vorhanden)?</summary>
    public bool IsReady => Kind switch
    {
        StoreKind.GoogleDrive or StoreKind.OneDrive or StoreKind.Dropbox => RefreshToken.Length > 0,
        StoreKind.WebDav => WebDavUrl.Length > 0 && WebDavUser.Length > 0 && WebDavPassword.Length > 0,
        _ => false,
    };

    /// <summary>Abmelden: Tokens und Passwort löschen (die Daten im Cloud-Speicher bleiben).</summary>
    public void SignOut()
    {
        _prefs.Edit()!
            .Remove("kind")!.Remove("refreshToken")!.Remove("accessToken")!.Remove("accessExpires")!
            .Remove("webdavPassword")!
            .Apply();
    }

    public static string Name(StoreKind kind) => kind switch
    {
        StoreKind.GoogleDrive => "Google Drive",
        StoreKind.OneDrive => "OneDrive",
        StoreKind.Dropbox => "Dropbox",
        StoreKind.WebDav => "WebDAV",
        _ => "–",
    };

    /// <summary>AES-GCM mit einem Schlüssel im Android Keystore. Format: Base64(IV) + ":" + Base64(Daten).</summary>
    static class Secret
    {
        const string KeyStoreName = "AndroidKeyStore";
        const string Alias = "zboard-sync";
        static readonly Lock KeyLock = new();

        static IKey Key()
        {
            lock (KeyLock)
            {
                var store = KeyStore.GetInstance(KeyStoreName)!;
                store.Load(null, null);
                if (store.GetKey(Alias, null) is IKey existing) return existing;

                var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)!;
                generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                    .SetBlockModes(KeyProperties.BlockModeGcm)!
                    .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                    .SetKeySize(256)!
                    .Build());
                return generator.GenerateKey()!;
            }
        }

        public static string? Encrypt(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.EncryptMode, Key());
            var data = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(value))!;
            return Convert.ToBase64String(cipher.GetIV()!) + ":" + Convert.ToBase64String(data);
        }

        public static string Decrypt(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return string.Empty;
            try
            {
                var parts = stored.Split(':');
                if (parts.Length != 2) return string.Empty;
                var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, Convert.FromBase64String(parts[0])));
                return System.Text.Encoding.UTF8.GetString(cipher.DoFinal(Convert.FromBase64String(parts[1]))!);
            }
            catch (Exception)
            {
                return string.Empty; // Schlüssel verloren (z. B. nach Gerätewiederherstellung): neu anmelden
            }
        }
    }
}
