using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZBoard.Plugins;

namespace ZBoard.Sync.Stores;

/// <summary>
/// Google Drive, versteckter App-Ordner (appDataFolder, Bereich drive.appdata): Nur diese App sieht die Datei, sie
/// taucht nicht in der Dateiliste auf und die App kommt an keine anderen Dateien heran.
/// </summary>
public sealed class GoogleDriveStore(Func<Task<string>> token) : ICloudStore
{
    const string Name = "Google Drive";
    const string Api = "https://www.googleapis.com/drive/v3/files";
    const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";

    async Task<HttpRequestMessage> Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = OAuth.Bearer(await token());
        return request;
    }

    async Task<string?> FindAsync(CancellationToken cancel)
    {
        string query = Uri.EscapeDataString($"name = '{PluginContract.SyncFileName}' and trashed = false");
        using var request = await Request(HttpMethod.Get, $"{Api}?spaces=appDataFolder&q={query}&fields=files(id)&pageSize=10");
        using var response = await OAuth.Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var files = json.RootElement.GetProperty("files");
        return files.GetArrayLength() > 0 ? files[0].GetProperty("id").GetString() : null;
    }

    public async Task<bool> DownloadAsync(Stream target, CancellationToken cancel)
    {
        var id = await FindAsync(cancel);
        if (id == null) return false;
        using var request = await Request(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(id)}?alt=media");
        using var response = await OAuth.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
        await CloudStores.CopyLimitedAsync(response, target, cancel);
        return true;
    }

    public async Task UploadAsync(byte[] content, CancellationToken cancel)
    {
        var id = await FindAsync(cancel);
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        HttpRequestMessage request;
        if (id != null)
        {
            request = await Request(HttpMethod.Patch, $"{UploadApi}/{Uri.EscapeDataString(id)}?uploadType=media");
            request.Content = file;
        }
        else
        {
            var metadata = new StringContent($$"""{"name":"{{PluginContract.SyncFileName}}","parents":["appDataFolder"]}""",
                Encoding.UTF8, "application/json");
            var multipart = new MultipartContent("related") { metadata, file };
            request = await Request(HttpMethod.Post, $"{UploadApi}?uploadType=multipart");
            request.Content = multipart;
        }

        using (request)
        using (var response = await OAuth.Http.SendAsync(request, cancel))
        {
            if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
        }
    }
}
