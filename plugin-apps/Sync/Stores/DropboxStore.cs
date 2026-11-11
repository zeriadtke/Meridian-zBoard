using System.Net;
using System.Net.Http.Headers;
using ZBoard.Plugins;

namespace ZBoard.Sync.Stores;

/// <summary>
/// Dropbox, App mit Zugriff „App folder“: Ordner „Apps/&lt;App-Name&gt;“, die App kommt an keine anderen Dateien heran.
/// </summary>
public sealed class DropboxStore(Func<Task<string>> token) : ICloudStore
{
    const string Name = "Dropbox";
    const string Path = "/" + PluginContract.SyncFileName;

    async Task<HttpRequestMessage> Request(string url, string apiArg)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = OAuth.Bearer(await token());
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", apiArg);
        return request;
    }

    public async Task<bool> DownloadAsync(Stream target, CancellationToken cancel)
    {
        using var request = await Request("https://content.dropboxapi.com/2/files/download", $$"""{"path":"{{Path}}"}""");
        using var response = await OAuth.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // 409 mit "path/not_found": es gibt noch keine Datei
            string error = await response.Content.ReadAsStringAsync(cancel);
            if (error.Contains("not_found", StringComparison.Ordinal)) return false;
        }

        if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
        await CloudStores.CopyLimitedAsync(response, target, cancel);
        return true;
    }

    public async Task UploadAsync(byte[] content, CancellationToken cancel)
    {
        using var request = await Request("https://content.dropboxapi.com/2/files/upload",
            $$"""{"path":"{{Path}}","mode":"overwrite","mute":true}""");
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await OAuth.Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw CloudStores.Failed(Name, response);
    }
}
