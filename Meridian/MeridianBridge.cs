// zHealth Meridian – Tipp-Dynamik. Nicht Teil von zBoard (upstream), siehe Meridian/README.md.
using Android.Content;
using Android.Util;
using ZBoard.Meridian.Typing;
using Uri = Android.Net.Uri;

namespace ZBoard.Meridian;

/// <summary>
/// Übergibt Sitzungszusammenfassungen an die Meridian-App (ContentProvider, geschützt durch eine Berechtigung mit
/// protectionLevel=signature). Die Tastatur speichert selbst NICHTS dauerhaft: Ist Meridian nicht installiert, nicht
/// freigegeben oder anders signiert, wird die Zusammenfassung verworfen.
/// </summary>
public sealed class MeridianBridge(Context context)
{
    public const string Authority = "de.World_of_Bytes.zHealth.Meridian.typing";
    static readonly Uri Sessions = Uri.Parse($"content://{Authority}/sessions")!;
    static readonly Uri Status = Uri.Parse($"content://{Authority}/status")!;
    const string Tag = "MeridianBridge";

    readonly ContentResolver _resolver = context.ApplicationContext!.ContentResolver!;

    // Nacheinander abarbeiten (wie ein Single-Thread-Executor), damit Sitzungen in Reihenfolge ankommen.
    Task _queue = Task.CompletedTask;
    readonly Lock _lock = new();

    public void Submit(TypingSessionSummary summary)
    {
        lock (_lock)
        {
            _queue = _queue.ContinueWith(_ => SubmitBlocking(summary), TaskScheduler.Default);
        }
    }

    void SubmitBlocking(TypingSessionSummary summary)
    {
        try
        {
            if (!IsEnabledBlocking()) return;
            var values = new ContentValues();
            foreach (var (key, value) in summary.ToValues())
            {
                switch (value)
                {
                    case long l: values.Put(key, l); break;
                    case double d: values.Put(key, d); break;
                }
            }

            _resolver.Insert(Sessions, values);
        }
        catch (Java.Lang.SecurityException e)
        {
            // Meridian mit anderem Schlüssel signiert → nichts tun.
            Log.Debug(Tag, $"Meridian nicht erreichbar: {e.GetType().Name}");
        }
        catch (Java.Lang.IllegalArgumentException e)
        {
            // Meridian nicht installiert (unbekannte Authority) → nichts tun.
            Log.Debug(Tag, $"Meridian nicht erreichbar: {e.GetType().Name}");
        }
        catch (Exception e)
        {
            Log.Warn(Tag, $"Übergabe an Meridian fehlgeschlagen: {e.GetType().Name}");
        }
    }

    /// <summary>Meridian meldet, ob die Quelle "Tipp-Dynamik" freigegeben ist.</summary>
    bool IsEnabledBlocking()
    {
        using var cursor = _resolver.Query(Status, null, null, null, null);
        return cursor != null && cursor.MoveToFirst() && cursor.GetInt(0) == 1;
    }
}
