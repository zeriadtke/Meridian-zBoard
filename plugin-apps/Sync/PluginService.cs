using Android.App;
using Android.Content;
using Android.OS;
using ZBoard.Plugins;
using ZBoard.Sync.Stores;

namespace ZBoard.Sync;

/// <summary>
/// Synchronisations-Plugin für zBoard (siehe docs/PLUGINS.md im zBoard-Repository). Reiner Transport: lädt die Datei,
/// die zBoard bereitstellt, in den Cloud-Speicher und holt sie wieder ab – das Zusammenführen macht zBoard selbst.
/// Antwortet nur der Tastatur, die gerade als Systemtastatur ausgewählt ist.
/// </summary>
[Service(Name = "de.World_of_Bytes.zBoard.Sync.PluginService", Exported = true, Label = "zBoard Sync")]
[IntentFilter(new[] { PluginContract.ServiceAction })]
[MetaData(PluginContract.MetaApiVersion, Value = "1")]
[MetaData(PluginContract.MetaSync, Value = "true")]
public sealed class PluginService : Service
{
    /// <summary>Die Dateien kommen nur aus zBoards eigenem Content-Provider.</summary>
    const string ZBoardAuthority = "de.World_of_Bytes.zBoard.sync";

    Messenger? _messenger;

    public override IBinder? OnBind(Intent? intent)
    {
        _messenger ??= new Messenger(new IncomingHandler(this));
        return _messenger.Binder;
    }

    /// <summary>Nur die aktive Systemtastatur darf das Plugin benutzen.</summary>
    bool IsActiveKeyboard(int uid)
    {
        string? current = Android.Provider.Settings.Secure.GetString(ContentResolver, Android.Provider.Settings.Secure.DefaultInputMethod);
        string? keyboard = current?.Split('/')[0];
        var packages = PackageManager?.GetPackagesForUid(uid);
        return keyboard != null && packages != null && packages.Contains(keyboard);
    }

    void Handle(Message message)
    {
        var replyTo = message.ReplyTo;
        if (replyTo == null) return;
        // Alles Nötige jetzt kopieren: die Nachricht wird nach HandleMessage wiederverwendet
        int what = message.What;
        var data = new Bundle(message.Data ?? Bundle.Empty);
        int uid = message.SendingUid;

        var reply = new Bundle();
        reply.PutInt(PluginContract.KeyRequestId, data.GetInt(PluginContract.KeyRequestId, 0));
        reply.PutInt(PluginContract.KeyApiVersion, PluginContract.ApiVersion);

        if (!IsActiveKeyboard(uid))
        {
            reply.PutString(PluginContract.KeyError, L.T("Nur die aktive Tastatur darf zBoard Sync nutzen.", "Only the active keyboard may use zBoard Sync."));
            Send(replyTo, what, reply);
            return;
        }

        var settings = new SyncSettings(this);
        switch (what)
        {
            case PluginContract.MsgListActions:
                // Keine Aktionen für die Leiste
                reply.PutStringArray(PluginContract.KeyActionIds, []);
                reply.PutStringArray(PluginContract.KeyActionLabels, []);
                Send(replyTo, what, reply);
                break;

            case PluginContract.MsgSyncStatus:
                reply.PutBoolean(PluginContract.KeySyncReady, settings.IsReady);
                reply.PutString(PluginContract.KeySyncTarget, Target(settings));
                Send(replyTo, what, reply);
                break;

            case PluginContract.MsgSyncDownload:
            case PluginContract.MsgSyncUpload:
                var uri = ParseUri(data.GetString(PluginContract.KeyUri));
                var store = CloudStores.Create(settings);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (uri == null) throw new SyncException(L.T("Ungültige Anfrage.", "Invalid request."));
                        if (store == null) throw new SyncException(L.T("In zBoard Sync ist kein Speicher eingerichtet.", "No storage is set up in zBoard Sync."));
                        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(80));
                        if (what == PluginContract.MsgSyncDownload) reply.PutBoolean(PluginContract.KeySyncFound, await DownloadAsync(store, uri, cancel.Token));
                        else await UploadAsync(store, uri, cancel.Token);
                    }
                    catch (SyncException e)
                    {
                        reply.PutString(PluginContract.KeyError, e.Message);
                    }
                    catch (System.OperationCanceledException)
                    {
                        reply.PutString(PluginContract.KeyError, L.T("Zeitüberschreitung beim Cloud-Speicher.", "The cloud storage timed out."));
                    }
                    catch (HttpRequestException)
                    {
                        reply.PutString(PluginContract.KeyError, L.T("Keine Verbindung zum Cloud-Speicher.", "No connection to the cloud storage."));
                    }
                    catch (Exception e)
                    {
                        Android.Util.Log.Warn("zBoardSync", e.ToString());
                        reply.PutString(PluginContract.KeyError, L.T("Synchronisieren ist fehlgeschlagen.", "Sync failed."));
                    }

                    Send(replyTo, what, reply);
                });
                break;

            default:
                reply.PutString(PluginContract.KeyError, L.T("Unbekannte Anfrage.", "Unknown request."));
                Send(replyTo, what, reply);
                break;
        }
    }

    static string Target(SyncSettings settings) =>
        settings.Kind == StoreKind.WebDav && Uri.TryCreate(settings.WebDavUrl, UriKind.Absolute, out var url)
            ? $"WebDAV ({url.Host})"
            : SyncSettings.Name(settings.Kind);

    static Android.Net.Uri? ParseUri(string? text)
    {
        var uri = text == null ? null : Android.Net.Uri.Parse(text);
        return uri is { Scheme: "content", Authority: ZBoardAuthority } ? uri : null;
    }

    async Task<bool> DownloadAsync(ICloudStore store, Android.Net.Uri uri, CancellationToken cancel)
    {
        using var buffer = new MemoryStream();
        if (!await store.DownloadAsync(buffer, cancel)) return false;
        await using var target = ContentResolver!.OpenOutputStream(uri, "wt")
            ?? throw new SyncException(L.T("zBoard hat keine Datei bereitgestellt.", "zBoard did not provide a file."));
        buffer.Position = 0;
        await buffer.CopyToAsync(target, cancel);
        return true;
    }

    async Task UploadAsync(ICloudStore store, Android.Net.Uri uri, CancellationToken cancel)
    {
        using var buffer = new MemoryStream();
        await using (var source = ContentResolver!.OpenInputStream(uri)
                     ?? throw new SyncException(L.T("zBoard hat keine Datei bereitgestellt.", "zBoard did not provide a file.")))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancel)) > 0)
            {
                if (buffer.Length + read > PluginContract.MaxSyncBytes) throw new SyncException(L.T("Die Datei ist zu groß.", "The file is too large."));
                buffer.Write(chunk, 0, read);
            }
        }

        await store.UploadAsync(buffer.ToArray(), cancel);
    }

    static void Send(Messenger target, int what, Bundle data)
    {
        try
        {
            var message = Message.Obtain(null, what)!;
            message.Data = data;
            target.Send(message);
        }
        catch (RemoteException)
        {
            // zBoard ist inzwischen weg – nichts zu tun
        }
    }

    sealed class IncomingHandler(PluginService owner) : Handler(Looper.MainLooper!)
    {
        public override void HandleMessage(Message msg) => owner.Handle(msg);
    }
}
