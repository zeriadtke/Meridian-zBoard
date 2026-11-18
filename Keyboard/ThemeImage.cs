using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;

namespace ZBoard.Keyboard;

/// <summary>Geladenes Hintergrundbild samt mittlerer Farbe (für Schriftfarbe und Navigationsleiste).</summary>
public sealed record ThemeImageData(Bitmap Bitmap, Color Average, DateTime Modified);

/// <summary>
/// Eigenes Hintergrundbild der Tastatur (Design „Eigenes Bild“) wie bei Gboard. zBoard kopiert das gewählte Bild
/// verkleinert in den app-privaten Speicher; es bleibt auf dem Gerät und gehört nicht zu Sicherung und Synchronisation.
/// </summary>
public static class ThemeImage
{
    /// <summary>Längste Seite des gespeicherten Bilds: genug für jedes Telefon, ohne viel Speicher zu belegen.</summary>
    const int MaxSide = 1600;

    static readonly Lock Gate = new();
    static ThemeImageData? s_cached;

    public static string FilePath(Context context) =>
        System.IO.Path.Combine(context.FilesDir!.AbsolutePath, "theme", "background.jpg");

    public static bool Exists(Context context) => File.Exists(FilePath(context));

    /// <summary>Das Bild, oder null, wenn keins gespeichert ist. Bleibt geladen, bis sich die Datei ändert.</summary>
    public static ThemeImageData? Load(Context context)
    {
        string path = FilePath(context);
        if (!File.Exists(path)) return null;
        var modified = File.GetLastWriteTimeUtc(path);
        lock (Gate)
        {
            if (s_cached != null && s_cached.Modified == modified) return s_cached;
            var bitmap = BitmapFactory.DecodeFile(path);
            if (bitmap == null) return null;
            s_cached = new ThemeImageData(bitmap, AverageColor(bitmap), modified);
            return s_cached;
        }
    }

    /// <summary>Liest ein Bild (z. B. aus der Bildauswahl), dreht es nach den EXIF-Angaben und speichert es verkleinert.</summary>
    public static void Save(Context context, Android.Net.Uri uri)
    {
        var resolver = context.ContentResolver!;

        // Erst nur die Größe lesen, dann passend verkleinert dekodieren (große Kamerafotos sprengen sonst den Speicher)
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using (var probe = resolver.OpenInputStream(uri) ?? throw new IOException("Bild nicht lesbar"))
        {
            BitmapFactory.DecodeStream(probe, null, bounds);
        }

        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) throw new IOException("Kein Bild");
        int sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= MaxSide) sample *= 2;

        Bitmap bitmap;
        using (var stream = resolver.OpenInputStream(uri) ?? throw new IOException("Bild nicht lesbar"))
        {
            bitmap = BitmapFactory.DecodeStream(stream, null, new BitmapFactory.Options { InSampleSize = sample })
                     ?? throw new IOException("Kein Bild");
        }

        int rotation = 0;
        try
        {
            using var stream = resolver.OpenInputStream(uri);
            if (stream != null)
            {
                var exif = new Android.Media.ExifInterface(stream);
                rotation = exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1) switch
                {
                    6 => 90,
                    3 => 180,
                    8 => 270,
                    _ => 0,
                };
            }
        }
        catch (Exception e) when (e is IOException or Java.Lang.Exception)
        {
            // ohne EXIF-Angaben bleibt das Bild, wie es ist
        }

        float scale = Math.Min(1f, MaxSide / (float)Math.Max(bitmap.Width, bitmap.Height));
        if (rotation != 0 || scale < 1f)
        {
            var matrix = new Matrix();
            matrix.PostScale(scale, scale);
            matrix.PostRotate(rotation);
            var transformed = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true)!;
            if (transformed != bitmap) bitmap.Recycle();
            bitmap = transformed;
        }

        SaveBitmap(context, bitmap);
        bitmap.Recycle();
    }

    /// <summary>Speichert ein Bild als Hintergrund (auch für den Emulator-Test).</summary>
    public static void SaveBitmap(Context context, Bitmap bitmap)
    {
        string path = FilePath(context);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 90, stream)) throw new IOException("Bild nicht gespeichert");
        }

        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(Context context)
    {
        string path = FilePath(context);
        if (File.Exists(path)) File.Delete(path);
        lock (Gate) s_cached = null;
    }

    static Color AverageColor(Bitmap bitmap)
    {
        using var small = Bitmap.CreateScaledBitmap(bitmap, 16, 16, true)!;
        long r = 0, g = 0, b = 0;
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int p = small.GetPixel(x, y);
                r += (p >> 16) & 0xff;
                g += (p >> 8) & 0xff;
                b += p & 0xff;
            }
        }

        return Color.Rgb((int)(r / 256), (int)(g / 256), (int)(b / 256));
    }

    /// <summary>Mittlere Farbe nach dem Abdunkeln (Anteil 0–1 Schwarz darüber).</summary>
    public static Color Dimmed(Color color, float dim) =>
        Color.Rgb((int)(color.R * (1 - dim)), (int)(color.G * (1 - dim)), (int)(color.B * (1 - dim)));

    public static bool IsDark(Color color) => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255 < 0.55;
}

/// <summary>Zeichnet das Hintergrundbild füllend (mittig zugeschnitten) und darüber eine Abdunkelung.</summary>
public sealed class ImageBackgroundDrawable(Bitmap bitmap, float dim) : Drawable
{
    readonly Paint _paint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    readonly Paint _shade = new() { Color = Color.Argb((int)(Math.Clamp(dim, 0f, 1f) * 255), 0, 0, 0) };

    public Bitmap Bitmap { get; } = bitmap;

    public float Dim { get; } = dim;

    public override void Draw(Canvas canvas)
    {
        var b = Bounds;
        if (b.Width() <= 0 || b.Height() <= 0) return;

        // Wie centerCrop: so skalieren, dass das Bild die Fläche füllt, unten ausrichten (die Tastatur sitzt unten)
        float scale = Math.Max(b.Width() / (float)Bitmap.Width, b.Height() / (float)Bitmap.Height);
        float w = Bitmap.Width * scale, h = Bitmap.Height * scale;
        float left = b.Left + (b.Width() - w) / 2, top = b.Bottom - h;
        canvas.Save();
        canvas.ClipRect(b);
        canvas.DrawBitmap(Bitmap, null, new RectF(left, top, left + w, top + h), _paint);
        canvas.DrawRect(b, _shade);
        canvas.Restore();
    }

    public override void SetAlpha(int alpha) => _paint.Alpha = alpha;

    public override void SetColorFilter(ColorFilter? colorFilter) => _paint.SetColorFilter(colorFilter);

#pragma warning disable CS0672, CS0618 // GetOpacity ist veraltet, muss aber überschrieben werden
    public override int Opacity => (int)Format.Opaque;
#pragma warning restore CS0672, CS0618
}
