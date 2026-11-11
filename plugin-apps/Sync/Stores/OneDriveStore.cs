using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ZBoard.Plugins;

namespace ZBoard.Sync.Stores;

/// <summary>
/// OneDrive über Microsoft Graph, App-Ordner (Bereich Files.ReadWrite.AppFolder): Ordner „Apps/zBoard Sync“, die App
/// kommt an keine anderen Dateien heran.
/// </summary>
public sealed class OneDriveStore(Func<Task<string>> token) : ICloudStore
{
    const string Name = "OneDrive";
    static readonly string Item = $"https://graph.microsoft.com/v1.0/me/drive/special/approot:/{PluginContract.SyncFileName}";

    async Task<HttpRequestMessage> Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = OAuth.Bearer(await token());
        return request;
    }

    public async Task<bool> DownloadAsync(Stream target, CancellationToken cancel)
    {
        // Erst die Metadaten: sie enthalten eine vorab signierte Download-Adresse, die kein Token braucht
        // (das Token darf nicht an den Speicher-Server weitergeleitet werden).
        string? downloadUrl;
        using (var request = await Request(HttpMethod.Get, Item))
        using (var response = await OAuth.Http.SendAsync(request, cancel))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            downloadUrl = json.RootElement.TryGetProperty("@microsoft.graph.downloadUrl", out var url) ? url.GetString() : null;
        }

        if (downloadUrl == null) throw new SyncException(L.T("OneDrive: keine Download-Adresse.", "OneDrive: no download address."));
        using var download = await OAuth.Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!download.IsSuccessStatusCode) throw CloudStores.Failed(Name, download);
        await CloudStores.CopyLimitedAsync(download, target, cancel);
        return true;
    }

    public async Task UploadAsync(byte[] content, CancellationToken cancel)
    {
        using var request = await Request(HttpMethod.Put, $"{Item}:/content");
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await OAuth.Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
    }
}
