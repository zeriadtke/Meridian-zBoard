using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ZBoard.Plugins;

namespace ZBoard.Sync.Stores;

/// <summary>
/// WebDAV (HiDrive, Nextcloud, ownCloud, viele NAS): Datei im Unterordner „zBoard“ der angegebenen Adresse.
/// Nur https – Benutzername und Passwort gehen bei jeder Anfrage mit.
/// </summary>
public sealed class WebDavStore(string baseUrl, string user, string password) : ICloudStore
{
    const string Name = "WebDAV";
    const string Folder = "zBoard";

    /// <summary>HiDrive (STRATO): Adresse des eigenen Bereichs aus dem Benutzernamen.</summary>
    public static string HiDriveUrl(string user) => $"https://webdav.hidrive.strato.com/users/{Uri.EscapeDataString(user.Trim())}/";

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0;

    string FolderUrl => baseUrl.TrimEnd('/') + "/" + Folder + "/";

    string FileUrl => FolderUrl + PluginContract.SyncFileName;

    HttpRequestMessage Request(HttpMethod method, string url)
    {
        if (!IsValidUrl(url)) throw new SyncException(L.T("WebDAV: Die Adresse muss mit https:// beginnen.", "WebDAV: the address must start with https://."));
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return request;
    }

    public async Task<bool> DownloadAsync(Stream target, CancellationToken cancel)
    {
        using var request = Request(HttpMethod.Get, FileUrl);
        using var response = await OAuth.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
        await CloudStores.CopyLimitedAsync(response, target, cancel);
        return true;
    }

    public async Task UploadAsync(byte[] content, CancellationToken cancel)
    {
        await EnsureFolderAsync(cancel);
        using var request = Request(HttpMethod.Put, FileUrl);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await OAuth.Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
    }

    /// <summary>Ordner anlegen (405: gibt es schon). Dient auch als Verbindungstest.</summary>
    public async Task EnsureFolderAsync(CancellationToken cancel)
    {
        using var request = Request(new HttpMethod("MKCOL"), FolderUrl);
        using var response = await OAuth.Http.SendAsync(request, cancel);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed) return;
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new SyncException(L.T("WebDAV: Die Adresse gibt es nicht.", "WebDAV: the address does not exist."));
        }

        throw CloudStores.Failed(Name, response);
    }
}
