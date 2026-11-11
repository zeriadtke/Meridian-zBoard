using Android.Content;
using ZBoard.Backup;
using ZBoard.Ime;
using ZBoard.Localization;
using ZBoard.Plugins;
using ZBoard.Settings;

namespace ZBoard.Sync;

/// <summary>Ergebnis eines Abgleichs; <paramref name="Message"/> ist eine Meldung für den Nutzer.</summary>
public sealed record SyncOutcome(bool Success, string Message);

/// <summary>
/// Abgleich über ein Synchronisations-Plugin (z. B. „zBoard Sync“): Datei aus der Cloud holen, mit dem eigenen Stand
/// zusammenführen (<see cref="BackupMerge"/>), Ergebnis wieder hochladen. zBoard selbst bleibt ohne Internet; das Plugin
/// ist nur der Transport. Läuft auf dem Hauptthread (Plugin-Messenger); Dateiarbeit im Hintergrund.
/// </summary>
public static class SyncManager
{
    /// <summary>Automatisch höchstens so oft (beim Öffnen und Schließen der Tastatur).</summary>
    static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(15);

    static bool s_running;

    public static bool IsRunning => s_running;

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Installiertes, freigeschaltetes Synchronisations-Plugin laut Einstellungen.</summary>
    public static PluginInfo? ChosenPlugin(Context context, ZBoardSettings settings) =>
        PluginRegistry.Enabled(context, settings).FirstOrDefault(p => p.CanSync && p.Package == settings.SyncPlugin);

    /// <summary>Beim Öffnen/Schließen der Tastatur: abgleichen, wenn eingeschaltet und der letzte Versuch lange her ist.</summary>
    public static void SyncIfDue(Context context)
    {
        var settings = new ZBoardSettings(context);
        if (!settings.SyncEnabled || s_running) return;
        if (NowMs() - settings.SyncLastAttemptMs < AutoInterval.TotalMilliseconds) return;
        _ = SyncAsync(context.ApplicationContext!);
    }

    public static async Task<SyncOutcome> SyncAsync(Context context, CancellationToken cancel = default)
    {
        if (s_running) return new SyncOutcome(false, L.T("Der Abgleich läuft bereits.", "Sync is already running."));
        s_running = true;
        var settings = new ZBoardSettings(context);
        settings.SyncLastAttemptMs = NowMs();
        try
        {
            var outcome = await RunAsync(context, settings, cancel);
            if (outcome.Success)
            {
                settings.SyncLastSuccessMs = NowMs();
                settings.SyncLastError = string.Empty;
            }
            else
            {
                settings.SyncLastError = outcome.Message;
            }

            return outcome;
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("zBoard", $"Synchronisation fehlgeschlagen: {e}");
            var outcome = new SyncOutcome(false, L.T("Der Abgleich ist fehlgeschlagen.", "Sync failed."));
            settings.SyncLastError = outcome.Message;
            return outcome;
        }
        finally
        {
            s_running = false;
        }
    }

    static async Task<SyncOutcome> RunAsync(Context context, ZBoardSettings settings, CancellationToken cancel)
    {
        var plugin = ChosenPlugin(context, settings);
        if (plugin == null)
        {
            return new SyncOutcome(false, L.T("Kein Synchronisations-Plugin eingeschaltet.", "No sync plugin turned on."));
        }

        var client = new PluginClient(context);
        var status = await client.SyncStatusAsync(plugin, cancel);
        if (status == null) return new SyncOutcome(false, L.T($"{plugin.Label} antwortet nicht.", $"{plugin.Label} does not respond."));
        if (status.Error != null) return new SyncOutcome(false, status.Error);
        if (!status.Ready)
        {
            return new SyncOutcome(false, L.T($"In {plugin.Label} ist noch kein Speicher eingerichtet.", $"No storage is set up in {plugin.Label} yet."));
        }

        // 1. Stand aus der Cloud holen
        string downloadPath = SyncFileProvider.FileFor(context, SyncFileProvider.Download);
        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
        File.Delete(downloadPath);
        var down = await TransferAsync(context, client, plugin, PluginContract.MsgSyncDownload, SyncFileProvider.Download,
            ActivityFlags.GrantWriteUriPermission, cancel);
        if (!down.Responded) return new SyncOutcome(false, L.T($"{plugin.Label} antwortet nicht.", $"{plugin.Label} does not respond."));
        if (down.Error != null) return new SyncOutcome(false, down.Error);

        BackupData? remote = null;
        if (down.Found)
        {
            try
            {
                remote = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(downloadPath);
                    return BackupArchive.Read(stream);
                }, cancel);
            }
            catch (BackupException e)
            {
                // Nicht hochladen: sonst überschriebe dieses Gerät Daten, die es nicht versteht.
                return new SyncOutcome(false, e.Error == BackupError.NewerVersion
                    ? L.T("Die Daten in der Cloud stammen aus einer neueren zBoard-Version. Bitte aktualisiere zBoard.",
                        "The data in the cloud comes from a newer zBoard version. Please update zBoard.")
                    : L.T("Die Datei in der Cloud ist beschädigt.", "The file in the cloud is damaged."));
            }
            finally
            {
                File.Delete(downloadPath);
            }
        }

        // 2. Zusammenführen (Hauptthread: dieselben Daten benutzt die Tastatur)
        if (remote != null) ZBoardImeService.ApplySync(context, remote, firstSync: settings.SyncLastSuccessMs == 0);

        // 3. Zusammengeführten Stand hochladen
        string version = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.VersionName ?? string.Empty;
        var data = ZBoardImeService.CreateSyncData(context, version);
        string uploadPath = SyncFileProvider.FileFor(context, SyncFileProvider.Upload);
        await Task.Run(() =>
        {
            using var stream = File.Create(uploadPath);
            BackupArchive.Write(stream, data);
        }, cancel);

        try
        {
            var up = await TransferAsync(context, client, plugin, PluginContract.MsgSyncUpload, SyncFileProvider.Upload,
                ActivityFlags.GrantReadUriPermission, cancel);
            if (!up.Responded) return new SyncOutcome(false, L.T($"{plugin.Label} antwortet nicht.", $"{plugin.Label} does not respond."));
            if (up.Error != null) return new SyncOutcome(false, up.Error);
        }
        finally
        {
            File.Delete(uploadPath);
        }

        return new SyncOutcome(true, L.T($"Abgeglichen mit {status.Target ?? plugin.Label}.", $"Synced with {status.Target ?? plugin.Label}."));
    }

    /// <summary>Gibt die Datei nur für diese eine Anfrage und nur diesem Plugin frei.</summary>
    static async Task<SyncTransfer> TransferAsync(Context context, PluginClient client, PluginInfo plugin, int what, string file,
        ActivityFlags permission, CancellationToken cancel)
    {
        var uri = SyncFileProvider.UriFor(file);
        context.GrantUriPermission(plugin.Package, uri, permission);
        try
        {
            return await client.SyncTransferAsync(plugin, what, uri, cancel);
        }
        finally
        {
            context.RevokeUriPermission(plugin.Package, uri, permission);
        }
    }

    /// <summary>Kurze Zustandszeile für die Einstellungen.</summary>
    public static string StatusLine(ZBoardSettings settings)
    {
        if (s_running) return L.T("Gleicht gerade ab …", "Syncing …");
        string error = settings.SyncLastError;
        long last = settings.SyncLastSuccessMs;
        string when = last == 0
            ? L.T("Noch nie abgeglichen", "Never synced")
            : L.T("Zuletzt abgeglichen: ", "Last synced: ") + DateTimeOffset.FromUnixTimeMilliseconds(last).ToLocalTime().ToString("g");
        return error.Length > 0 ? $"{when} · {error}" : when;
    }
}
