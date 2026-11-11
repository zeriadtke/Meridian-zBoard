using Android.Content;
using Android.Content.PM;
using ZBoard.Settings;

namespace ZBoard.Plugins;

/// <summary>Installiertes Plugin: App und Dienst, Name, ob es Internet nutzen darf, ob es synchronisieren kann.</summary>
public sealed record PluginInfo(string Package, string Service, string Label, bool HasInternet, bool SameSigner, bool CanSync = false);

/// <summary>
/// Findet installierte Plugins (Dienste mit <see cref="PluginContract.ServiceAction"/>). Freigeschaltet ist ein Plugin
/// erst, wenn der Nutzer es in den Einstellungen einschaltet – vorher bekommt es nie Text zu sehen.
/// </summary>
public static class PluginRegistry
{
    public static IReadOnlyList<PluginInfo> Installed(Context context)
    {
        var pm = context.PackageManager!;
        var intent = new Intent(PluginContract.ServiceAction);
#pragma warning disable CA1422 // Variante mit Flags-Objekt erst ab Android 13
        var services = pm.QueryIntentServices(intent, PackageInfoFlags.MetaData);
#pragma warning restore CA1422
        var result = new List<PluginInfo>();
        foreach (var info in services)
        {
            var service = info.ServiceInfo;
            if (service?.PackageName == null || service.Name == null || !service.Exported) continue;
            int api = service.MetaData?.GetInt(PluginContract.MetaApiVersion, 0) ?? 0;
            if (api < 1) continue;

            string label = info.LoadLabel(pm)?.ToString() ?? service.PackageName;
            bool canSync = service.MetaData?.GetBoolean(PluginContract.MetaSync, false) == true;
            result.Add(new PluginInfo(service.PackageName, service.Name, label,
                HasInternet(pm, service.PackageName), SameSigner(pm, context.PackageName!, service.PackageName), canSync));
        }

        return [.. result.OrderBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Installierte und vom Nutzer freigeschaltete Plugins.</summary>
    public static IReadOnlyList<PluginInfo> Enabled(Context context, ZBoardSettings settings)
    {
        var enabled = settings.EnabledPlugins;
        if (enabled.Count == 0) return [];
        return [.. Installed(context).Where(p => enabled.Contains(p.Package))];
    }

    static bool HasInternet(PackageManager pm, string package)
    {
        try
        {
#pragma warning disable CA1422 // GetPackageInfo(string, flags) bleibt bis Android 13 der Weg
            var info = pm.GetPackageInfo(package, PackageInfoFlags.Permissions);
#pragma warning restore CA1422
            return info?.RequestedPermissions?.Contains("android.permission.INTERNET") == true;
        }
        catch (PackageManager.NameNotFoundException)
        {
            return false;
        }
    }

    static bool SameSigner(PackageManager pm, string self, string other) =>
        pm.CheckSignatures(self, other) == SignatureResult.Match;
}
