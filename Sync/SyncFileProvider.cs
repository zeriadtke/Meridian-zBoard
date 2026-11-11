using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;

namespace ZBoard.Sync;

/// <summary>
/// Reicht die beiden Austauschdateien der Synchronisation an das Plugin weiter – ohne gemeinsamen Speicher und ohne
/// Größengrenze von Binder-Nachrichten. Nicht exportiert: Zugriff nur über eine befristete URI-Freigabe, die zBoard
/// für die Dauer einer Anfrage an genau das Synchronisations-Plugin vergibt.
/// <code>
/// content://de.World_of_Bytes.zBoard.sync/download   Plugin schreibt die Datei aus der Cloud hinein
/// content://de.World_of_Bytes.zBoard.sync/upload     Plugin liest die Datei, die in die Cloud soll
/// </code>
/// </summary>
[ContentProvider(new[] { SyncFileProvider.Authority }, Name = "de.World_of_Bytes.zBoard.SyncFileProvider", Exported = false, GrantUriPermissions = true)]
public sealed class SyncFileProvider : ContentProvider
{
    public const string Authority = "de.World_of_Bytes.zBoard.sync";
    public const string Download = "download";
    public const string Upload = "upload";

    public static Android.Net.Uri UriFor(string name) => Android.Net.Uri.Parse($"content://{Authority}/{name}")!;

    public static string FileFor(Context context, string name) =>
        Path.Combine(context.CacheDir!.AbsolutePath, "sync", name + ".zip");

    public override bool OnCreate() => true;

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        string? name = uri.LastPathSegment;
        // Download nur schreiben, Upload nur lesen
        bool allowed = (name == Download && mode is "w" or "wt") || (name == Upload && mode == "r");
        if (!allowed || uri.PathSegments?.Count != 1) throw new Java.IO.FileNotFoundException(uri.ToString());

        var path = FileFor(Context!, name!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var parsed = ParcelFileDescriptor.ParseMode(mode == "w" ? "wt" : mode); // immer von vorn schreiben
        return ParcelFileDescriptor.Open(new Java.IO.File(path), parsed);
    }

    public override string? GetType(Android.Net.Uri uri) => "application/zip";

    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder) => null;

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;

    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;

    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
}
