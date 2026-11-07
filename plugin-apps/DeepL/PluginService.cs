using Android.App;
using Android.Content;
using Android.OS;
using ZBoard.Plugins;

namespace ZBoard.DeepL;

/// <summary>
/// Plugin-Dienst für zBoard (siehe docs/PLUGINS.md im zBoard-Repository). Antwortet nur der Tastatur, die gerade als
/// Systemtastatur ausgewählt ist – andere Apps können den Dienst nicht für eigene Übersetzungen nutzen.
/// </summary>
[Service(Name = "de.World_of_Bytes.zBoard.DeepL.PluginService", Exported = true, Label = "DeepL")]
[IntentFilter(new[] { PluginContract.ServiceAction })]
[MetaData(PluginContract.MetaApiVersion, Value = "1")]
public sealed class PluginService : Service
{
    const string ActionPrefix = "translate:";

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
            reply.PutString(PluginContract.KeyError, T("Nur die aktive Tastatur darf DeepL nutzen.", "Only the active keyboard may use DeepL."));
            Send(replyTo, what, reply);
            return;
        }

        var settings = new DeepLSettings(this);
        switch (what)
        {
            case PluginContract.MsgListActions:
                var targets = settings.Targets;
                reply.PutStringArray(PluginContract.KeyActionIds, [.. targets.Select(t => ActionPrefix + t.Code)]);
                reply.PutStringArray(PluginContract.KeyActionLabels, [.. targets.Select(t => "DeepL → " + t.Name)]);
                Send(replyTo, what, reply);
                break;

            case PluginContract.MsgRun:
                string action = data.GetString(PluginContract.KeyAction) ?? string.Empty;
                string text = data.GetString(PluginContract.KeyText) ?? string.Empty;
                string apiKey = settings.ApiKey;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!action.StartsWith(ActionPrefix, StringComparison.Ordinal) || text.Length > PluginContract.MaxTextLength)
                        {
                            throw new DeepLException(T("Unbekannte Aktion.", "Unknown action."));
                        }

                        reply.PutString(PluginContract.KeyText, await DeepLClient.TranslateAsync(apiKey, text, action[ActionPrefix.Length..]));
                    }
                    catch (DeepLException e)
                    {
                        reply.PutString(PluginContract.KeyError, e.Message);
                    }
                    catch (Exception)
                    {
                        reply.PutString(PluginContract.KeyError, T("Übersetzen ist fehlgeschlagen.", "Translation failed."));
                    }

                    Send(replyTo, what, reply);
                });
                break;

            default:
                reply.PutString(PluginContract.KeyError, T("Unbekannte Anfrage.", "Unknown request."));
                Send(replyTo, what, reply);
                break;
        }
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
