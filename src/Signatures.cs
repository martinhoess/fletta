using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fletta;

/// <summary>
/// Gezeichnete Unterschrift: Striche in Anteilen ihres Rahmens (je Strich x, y, x, y, …), dazu das Seitenverhältnis
/// Breite / Höhe. So lässt sie sich in jede Größe setzen.
/// </summary>
public sealed record DrawnSignature(double Aspect, double[][] Strokes)
{
    public Point[][] ToPoints() => [.. Strokes.Select(stroke => Enumerable.Range(0, stroke.Length / 2).Select(i => new Point(stroke[2 * i], stroke[2 * i + 1])).ToArray())];
}

/// <summary>
/// Die gemerkte Unterschrift, gezeichnet und als Bild, unter %APPDATA%\Fletta — beides bleibt bis zum nächsten Zeichnen
/// oder Laden. Fehlt eine Datei oder ist sie kaputt, gibt es diese Unterschrift eben nicht.
/// </summary>
static class Signatures
{
    const byte WhiteLevel = 225; // heller als das wird durchsichtig: Scanhintergrund und Papier
    const int MaxImageSide = 1600; // größer bringt auf einer Seite nichts und bläht nur die PDF auf

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fletta");
    static string DrawnPath => Path.Combine(Folder, "unterschrift.json");
    static string ImagePath => Path.Combine(Folder, "unterschrift.png");

    public static bool HasImage => File.Exists(ImagePath);

    public static DrawnSignature? LoadDrawn()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(DrawnPath), SettingsJson.Default.DrawnSignature); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <returns>false, wenn nichts gezeichnet war oder sich nicht speichern ließ.</returns>
    public static bool SaveDrawn(IReadOnlyList<Point[]> strokes)
    {
        if (Normalize(strokes) is not { } signature) return false;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(DrawnPath, JsonSerializer.Serialize(signature, SettingsJson.Default.DrawnSignature));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Striche auf ihren gemeinsamen Rahmen bezogen (0–1); null ohne Ausdehnung. Im Selbsttest geprüft.</summary>
    internal static DrawnSignature? Normalize(IReadOnlyList<Point[]> strokes)
    {
        var all = strokes.SelectMany(stroke => stroke).ToList();
        if (all.Count < 2) return null;
        var bounds = new Rect(new Point(all.Min(p => p.X), all.Min(p => p.Y)), new Point(all.Max(p => p.X), all.Max(p => p.Y)));
        if (bounds.Width < 1 || bounds.Height < 1) return null;
        return new DrawnSignature(bounds.Width / bounds.Height, [.. strokes.Where(stroke => stroke.Length > 0).Select(stroke =>
            stroke.SelectMany(p => new[] { (p.X - bounds.X) / bounds.Width, (p.Y - bounds.Y) / bounds.Height }).ToArray())]);
    }

    /// <summary>Bild der Unterschrift als BGRA-Pixel (mit Alpha); null, wenn keins gemerkt ist.</summary>
    public static (byte[] Bgra, int Width, int Height)? LoadImage()
    {
        try
        {
            using var file = File.OpenRead(ImagePath);
            return Pixels(BitmapFrame.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { return null; }
    }

    /// <summary>
    /// Bild übernehmen: heller Hintergrund wird durchsichtig, der Rand bis zur Schrift wird abgeschnitten, zu große
    /// Bilder werden verkleinert. Gespeichert als PNG; wirft IOException/NotSupportedException bei unlesbarer Datei.
    /// </summary>
    public static void ImportImage(string path)
    {
        BitmapFrame frame;
        using (var file = File.OpenRead(path)) frame = BitmapFrame.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        // Handyfotos liegen oft quer im Speicher und tragen die Drehung nur als EXIF-Angabe (Review 2026-09-18).
        BitmapSource source = OrientationOf(frame) switch
        {
            3 => new TransformedBitmap(frame, new RotateTransform(180)),
            6 => new TransformedBitmap(frame, new RotateTransform(90)),
            8 => new TransformedBitmap(frame, new RotateTransform(270)),
            _ => frame,
        };
        var scale = Math.Min(1, (double)MaxImageSide / Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale < 1) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var (bgra, width, height) = Pixels(source);
        var (cleaned, cleanWidth, cleanHeight) = Clean(bgra, width, height);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(cleanWidth, cleanHeight, 96, 96, PixelFormats.Bgra32, null, cleaned, cleanWidth * 4)));
        Directory.CreateDirectory(Folder);
        using var target = File.Create(ImagePath);
        encoder.Save(target);
    }

    /// <summary>EXIF-Ausrichtung (1 = aufrecht, 3/6/8 = gedreht); gespiegelte kommen bei Unterschriften nicht vor.</summary>
    static int OrientationOf(BitmapFrame frame)
    {
        try { return frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value ? value : 1; }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { return 1; } // Format ohne EXIF
    }

    static (byte[] Bgra, int Width, int Height) Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bgra = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bgra, converted.PixelWidth * 4, 0);
        return (bgra, converted.PixelWidth, converted.PixelHeight);
    }

    /// <summary>
    /// Heller Hintergrund durchsichtig, dann auf die übrigen Pixel zugeschnitten; bleibt nichts, das ganze Bild. Farbe der
    /// Schrift bleibt, wie sie ist. Im Selbsttest geprüft.
    /// </summary>
    internal static (byte[] Bgra, int Width, int Height) Clean(byte[] bgra, int width, int height)
    {
        var pixels = (byte[])bgra.Clone();
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                if (pixels[at] >= WhiteLevel && pixels[at + 1] >= WhiteLevel && pixels[at + 2] >= WhiteLevel) pixels[at + 3] = 0;
                if (pixels[at + 3] == 0) continue;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        if (right < 0) return (pixels, width, height);
        var (cropWidth, cropHeight) = (right - left + 1, bottom - top + 1);
        var cropped = new byte[cropWidth * cropHeight * 4];
        for (var row = 0; row < cropHeight; row++)
            Array.Copy(pixels, ((top + row) * width + left) * 4, cropped, row * cropWidth * 4, cropWidth * 4);
        return (cropped, cropWidth, cropHeight);
    }
}
