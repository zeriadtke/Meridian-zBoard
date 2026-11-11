using ZBoard.Plugins;

namespace ZBoard.Sync.Stores;

/// <summary>Ein Cloud-Speicher mit genau einer Datei (<see cref="PluginContract.SyncFileName"/>).</summary>
public interface ICloudStore
{
    /// <summary>Lädt die Datei nach <paramref name="target"/>. false: es gibt noch keine.</summary>
    Task<bool> DownloadAsync(Stream target, CancellationToken cancel);

    /// <summary>Ersetzt die Datei durch <paramref name="content"/>.</summary>
    Task UploadAsync(byte[] content, CancellationToken cancel);
}

public static class CloudStores
{
    /// <summary>Speicher laut Einstellungen; null, wenn keiner eingerichtet ist.</summary>
    public static ICloudStore? Create(SyncSettings settings)
    {
        if (!settings.IsReady) return null;
        if (settings.Kind == StoreKind.WebDav) return new WebDavStore(settings.WebDavUrl, settings.WebDavUser, settings.WebDavPassword);

        var provider = OAuth.For(settings.Kind);
        if (provider == null) return null;
        Func<Task<string>> token = () => OAuth.AccessTokenAsync(provider, settings);
        return settings.Kind switch
        {
            StoreKind.GoogleDrive => new GoogleDriveStore(token),
            StoreKind.OneDrive => new OneDriveStore(token),
            StoreKind.Dropbox => new DropboxStore(token),
            _ => null,
        };
    }

    /// <summary>Antwort lesen mit Obergrenze (gegen beschädigte oder untergeschobene Dateien).</summary>
    public static async Task CopyLimitedAsync(HttpResponseMessage response, Stream target, CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength > PluginContract.MaxSyncBytes) throw TooLarge();
        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancel)) > 0)
        {
            total += read;
            if (total > PluginContract.MaxSyncBytes) throw TooLarge();
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
        }
    }

    static SyncException TooLarge() => new(L.T("Die Datei in der Cloud ist zu groß.", "The file in the cloud is too large."));

    /// <summary>Fehlermeldung für eine nicht erfolgreiche Antwort.</summary>
    public static SyncException Failed(string store, HttpResponseMessage response) => (int)response.StatusCode switch
    {
        401 or 403 => new(L.T($"{store}: Zugriff verweigert. Bitte in zBoard Sync neu anmelden.", $"{store}: access denied. Please sign in again in zBoard Sync.")),
        507 => new(L.T($"{store}: Der Speicher ist voll.", $"{store}: the storage is full.")),
        _ => new(L.T($"{store}: Fehler {(int)response.StatusCode}.", $"{store}: error {(int)response.StatusCode}.")),
    };
}
