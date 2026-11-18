using Android.App;
using Android.Content;
using Android.OS;
using ZBoard.Plugins;

namespace ZBoard.Pen;

/// <summary>
/// Handschrift-Plugin für zBoard (siehe docs/PLUGINS.md im zBoard-Repository): bekommt die Striche von der
/// Schreibfläche und antwortet mit erkannten Texten. Antwortet nur der Tastatur, die gerade als Systemtastatur
/// ausgewählt ist. Fehlt das Schriftmodell der Sprache, lädt es das Plugin und meldet <c>inkLoading</c>; zBoard fragt
/// dann von selbst wieder.
/// </summary>
[Service(Name = "de.World_of_Bytes.zBoard.Pen.PluginService", Exported = true, Label = "zBoard Pen")]
[IntentFilter(new[] { PluginContract.ServiceAction })]
[MetaData(PluginContract.MetaApiVersion, Value = "1")]
[MetaData(PluginContract.MetaInk, Value = "true")]
public sealed class PluginService : Service
{
    Messenger? _messenger;

    public override IBinder? OnBind(Intent? intent)
    {
        _messenger ??= new Messenger(new IncomingHandler(this));
        return _messenger.Binder;
    }

    static string T(string german, string english) => Java.Util.Locale.Default.Language == "de" ? german : english;

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
            reply.PutString(PluginContract.KeyError, T("Nur die aktive Tastatur darf zBoard Pen nutzen.", "Only the active keyboard may use zBoard Pen."));
            Send(replyTo, what, reply);
            return;
        }

        switch (what)
        {
            case PluginContract.MsgListActions:
                // Keine Aktionen für das Puzzle-Menü: zBoard Pen arbeitet über die Schreibfläche
                reply.PutStringArray(PluginContract.KeyActionIds, []);
                reply.PutStringArray(PluginContract.KeyActionLabels, []);
                Send(replyTo, what, reply);
                break;

            case PluginContract.MsgInkRecognize:
                _ = Recognize(data, reply, replyTo, what);
                break;

            default:
                reply.PutString(PluginContract.KeyError, T("Unbekannte Anfrage.", "Unknown request."));
                Send(replyTo, what, reply);
                break;
        }
    }

    async Task Recognize(Bundle data, Bundle reply, Messenger replyTo, int what)
    {
        string language = data.GetString(PluginContract.KeyLanguage) ?? "en";
        string name = PenLanguages.NameOf(language);
        try
        {
            switch (await InkRecognizer.StateAsync(language))
            {
                case ModelState.Unsupported:
                    reply.PutString(PluginContract.KeyError, T($"Handschrift gibt es für {name} nicht.", $"Handwriting is not available for {name}."));
                    break;
                case ModelState.Missing:
                    InkRecognizer.StartDownload(language);
                    goto case ModelState.Downloading;
                case ModelState.Downloading:
                    reply.PutBoolean(PluginContract.KeyInkLoading, true);
                    reply.PutString(PluginContract.KeyError, T($"Schriftmodell {name} wird geladen (einmalig, ca. 20 MB) …",
                        $"Downloading the {name} handwriting model (once, about 20 MB) …"));
                    break;
                default:
                    var strokes = Strokes(data);
                    var candidates = await InkRecognizer.RecognizeAsync(language, strokes,
                        data.GetFloat(PluginContract.KeyInkWidth, 0), data.GetFloat(PluginContract.KeyInkHeight, 0),
                        data.GetString(PluginContract.KeyPreContext) ?? string.Empty);
                    reply.PutStringArray(PluginContract.KeyCandidates, [.. candidates]);
                    if (candidates.Count == 0) reply.PutString(PluginContract.KeyError, T("Nichts erkannt.", "Nothing recognized."));
                    break;
            }
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("zBoardPen", $"Erkennung fehlgeschlagen: {e}");
            reply.PutString(PluginContract.KeyError, T("Erkennen ist fehlgeschlagen.", "Recognition failed."));
        }

        Send(replyTo, what, reply);
    }

    /// <summary>Striche aus der Anfrage: Anzahl der Punkte je Strich, Punkte als x/y-Paare, Zeitpunkte.</summary>
    static List<IReadOnlyList<(float X, float Y, long T)>> Strokes(Bundle data)
    {
        var counts = data.GetIntArray(PluginContract.KeyInkStrokes) ?? [];
        var points = data.GetFloatArray(PluginContract.KeyInkPoints) ?? [];
        var times = data.GetLongArray(PluginContract.KeyInkTimes) ?? [];
        var strokes = new List<IReadOnlyList<(float X, float Y, long T)>>();
        int index = 0;
        foreach (int count in counts)
        {
            var stroke = new List<(float, float, long)>(count);
            for (int i = 0; i < count && index < times.Length && 2 * index + 1 < points.Length; i++, index++)
            {
                stroke.Add((points[2 * index], points[2 * index + 1], times[index]));
            }

            if (stroke.Count > 0) strokes.Add(stroke);
        }

        return strokes;
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
