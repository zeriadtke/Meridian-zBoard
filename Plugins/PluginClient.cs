using Android.Content;
using Android.OS;

namespace ZBoard.Plugins;

/// <summary>Aktion eines Plugins, wie sie in der Leiste zur Auswahl steht.</summary>
public sealed record PluginAction(PluginInfo Plugin, string Id, string Label);

/// <summary>Antwort eines Plugins: neuer Text oder Fehlermeldung.</summary>
public sealed record PluginResult(string? Text, string? Error);

/// <summary>Antwort eines Handschrift-Plugins: erkannte Texte, oder Fehlermeldung (Loading: Modell wird noch geladen).</summary>
public sealed record InkResult(IReadOnlyList<string> Candidates, string? Error, bool Loading);

/// <summary>Strich auf der Schreibfläche: Punkte (x, y in Pixeln) mit Zeitpunkt in Millisekunden.</summary>
public sealed record InkStroke(IReadOnlyList<(float X, float Y, long Time)> Points);

/// <summary>Zustand eines Synchronisations-Plugins: eingerichtet?, Anzeige des Speichers, Fehlermeldung.</summary>
public sealed record SyncStatus(bool Ready, string? Target, string? Error);

/// <summary>Ergebnis von Herunter-/Hochladen. <paramref name="Responded"/> false: Plugin hat nicht (rechtzeitig) geantwortet.</summary>
public sealed record SyncTransfer(bool Responded, bool Found, string? Error);

/// <summary>
/// Spricht mit einem Plugin-Dienst über Messenger (Bindung nur für die Dauer einer Anfrage). Muss auf dem
/// Hauptthread benutzt werden; Antworten kommen ebenfalls dort an.
/// </summary>
public sealed class PluginClient(Context context)
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);

    readonly Context _context = context.ApplicationContext!;
    readonly Dictionary<int, TaskCompletionSource<Bundle>> _pending = [];
    int _nextRequest = 1;
    ReplyHandler? _handler;
    Messenger? _replyTo;

    /// <summary>Alle Aktionen eines Plugins; leer, wenn es nicht antwortet.</summary>
    public async Task<IReadOnlyList<PluginAction>> ListActionsAsync(PluginInfo plugin, CancellationToken cancel = default)
    {
        var reply = await RequestAsync(plugin, PluginContract.MsgListActions, new Bundle(), ListTimeout, cancel);
        var ids = reply?.GetStringArray(PluginContract.KeyActionIds);
        var labels = reply?.GetStringArray(PluginContract.KeyActionLabels);
        if (ids == null || labels == null) return [];
        return [.. ids.Zip(labels).Where(x => !string.IsNullOrEmpty(x.First)).Take(8)
            .Select(x => new PluginAction(plugin, x.First, string.IsNullOrWhiteSpace(x.Second) ? x.First : x.Second))];
    }

    public async Task<PluginResult> RunAsync(PluginAction action, string text, string language, CancellationToken cancel = default)
    {
        var data = new Bundle();
        data.PutString(PluginContract.KeyAction, action.Id);
        data.PutString(PluginContract.KeyText, text);
        data.PutString(PluginContract.KeyLanguage, language);
        var reply = await RequestAsync(action.Plugin, PluginContract.MsgRun, data, RunTimeout, cancel);
        if (reply == null) return new PluginResult(null, null);
        return new PluginResult(reply.GetString(PluginContract.KeyText), reply.GetString(PluginContract.KeyError));
    }

    // ---------- Handschrift ----------

    public static readonly TimeSpan InkTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Striche an ein Handschrift-Plugin; keine Texte und kein Fehler, wenn es nicht antwortet.</summary>
    public async Task<InkResult> RecognizeInkAsync(PluginInfo plugin, IReadOnlyList<InkStroke> strokes, float width, float height,
        string language, string preContext, CancellationToken cancel = default)
    {
        var counts = new List<int>();
        var points = new List<float>();
        var times = new List<long>();
        foreach (var stroke in strokes)
        {
            if (times.Count + stroke.Points.Count > PluginContract.MaxInkPoints) break;
            counts.Add(stroke.Points.Count);
            foreach (var (x, y, t) in stroke.Points)
            {
                points.Add(x);
                points.Add(y);
                times.Add(t);
            }
        }

        var data = new Bundle();
        data.PutIntArray(PluginContract.KeyInkStrokes, [.. counts]);
        data.PutFloatArray(PluginContract.KeyInkPoints, [.. points]);
        data.PutLongArray(PluginContract.KeyInkTimes, [.. times]);
        data.PutFloat(PluginContract.KeyInkWidth, width);
        data.PutFloat(PluginContract.KeyInkHeight, height);
        data.PutString(PluginContract.KeyLanguage, language);
        data.PutString(PluginContract.KeyPreContext, preContext);
        var reply = await RequestAsync(plugin, PluginContract.MsgInkRecognize, data, InkTimeout, cancel);
        if (reply == null) return new InkResult([], null, false);
        return new InkResult(reply.GetStringArray(PluginContract.KeyCandidates) ?? [], reply.GetString(PluginContract.KeyError),
            reply.GetBoolean(PluginContract.KeyInkLoading, false));
    }

    // ---------- Synchronisation ----------

    public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Speicher des Synchronisations-Plugins; <c>null</c>, wenn es nicht antwortet.</summary>
    public async Task<SyncStatus?> SyncStatusAsync(PluginInfo plugin, CancellationToken cancel = default)
    {
        var reply = await RequestAsync(plugin, PluginContract.MsgSyncStatus, new Bundle(), ListTimeout, cancel);
        if (reply == null) return null;
        return new SyncStatus(reply.GetBoolean(PluginContract.KeySyncReady, false), reply.GetString(PluginContract.KeySyncTarget),
            reply.GetString(PluginContract.KeyError));
    }

    /// <summary>
    /// Herunter- oder Hochladen (<see cref="PluginContract.MsgSyncDownload"/>, <see cref="PluginContract.MsgSyncUpload"/>).
    /// Die URI muss dem Plugin vorher freigegeben sein.
    /// </summary>
    public async Task<SyncTransfer> SyncTransferAsync(PluginInfo plugin, int what, Android.Net.Uri uri, CancellationToken cancel = default)
    {
        var data = new Bundle();
        data.PutString(PluginContract.KeyUri, uri.ToString());
        var reply = await RequestAsync(plugin, what, data, SyncTimeout, cancel);
        if (reply == null) return new SyncTransfer(false, false, null);
        return new SyncTransfer(true, reply.GetBoolean(PluginContract.KeySyncFound, false), reply.GetString(PluginContract.KeyError));
    }

    /// <summary>Bindet den Dienst, schickt die Nachricht, wartet auf die Antwort und löst die Bindung wieder.</summary>
    async Task<Bundle?> RequestAsync(PluginInfo plugin, int what, Bundle data, TimeSpan timeout, CancellationToken cancel)
    {
        _handler ??= new ReplyHandler(this);
        _replyTo ??= new Messenger(_handler);

        var connection = new Connection();
        var intent = new Intent(PluginContract.ServiceAction).SetComponent(new ComponentName(plugin.Package, plugin.Service));
        bool bound;
        try
        {
            bound = _context.BindService(intent, connection, Bind.AutoCreate);
        }
        catch (Java.Lang.SecurityException)
        {
            return null;
        }

        if (!bound)
        {
            Unbind(connection);
            return null;
        }

        int id = _nextRequest++;
        var reply = new TaskCompletionSource<Bundle>();
        _pending[id] = reply;
        try
        {
            var service = await WithTimeout(connection.Messenger.Task, ConnectTimeout, cancel);
            if (service == null) return null;

            data.PutInt(PluginContract.KeyRequestId, id);
            data.PutInt(PluginContract.KeyApiVersion, PluginContract.ApiVersion);
            var message = Message.Obtain(null, what)!;
            message.Data = data;
            message.ReplyTo = _replyTo;
            service.Send(message);

            return await WithTimeout(reply.Task, timeout, cancel);
        }
        catch (Android.OS.RemoteException)
        {
            return null;
        }
        finally
        {
            _pending.Remove(id);
            Unbind(connection);
        }
    }

    void Unbind(Connection connection)
    {
        try
        {
            _context.UnbindService(connection);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // nicht (mehr) gebunden
        }
    }

    static async Task<T?> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken cancel) where T : class
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout, cancel)).ConfigureAwait(true);
        return finished == task && task.IsCompletedSuccessfully ? task.Result : null;
    }

    void OnReply(Message message)
    {
        var data = message.Data;
        if (data == null) return;
        int id = data.GetInt(PluginContract.KeyRequestId, 0);
        // Kopie: die Nachricht wird nach HandleMessage wiederverwendet
        if (_pending.Remove(id, out var reply)) reply.TrySetResult(new Bundle(data));
    }

    sealed class ReplyHandler(PluginClient owner) : Handler(Looper.MainLooper!)
    {
        public override void HandleMessage(Message msg) => owner.OnReply(msg);
    }

    sealed class Connection : Java.Lang.Object, IServiceConnection
    {
        public TaskCompletionSource<Messenger> Messenger { get; } = new();

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service != null) Messenger.TrySetResult(new Messenger(service));
            else Messenger.TrySetCanceled();
        }

        public void OnServiceDisconnected(ComponentName? name) => Messenger.TrySetCanceled();

        public void OnNullBinding(ComponentName? name) => Messenger.TrySetCanceled();
    }
}
