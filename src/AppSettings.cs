using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Fletta;

/// <summary>
/// Was sich Fletta zwischen zwei Starts merkt, als JSON unter %APPDATA%\Fletta. Mit mehreren Fenstern
/// gewinnt das zuletzt geschlossene. Eine kaputte oder fehlende Datei heißt: Standardwerte.
/// </summary>
public sealed record AppSettings
{
    public double Left { get; init; } = double.NaN; // NaN: noch nie gespeichert, Fenster mittig
    public double Top { get; init; } = double.NaN;
    public double Width { get; init; } = 1000;
    public double Height { get; init; } = 900;
    public bool Maximized { get; init; }
    public double SidebarWidth { get; init; } = 220;
    public bool SidebarHidden { get; init; }
    public ZoomMode Zoom { get; init; } = ZoomMode.FitWidth;
    public int ZoomPercent { get; init; } = 100;
    public bool Spreads { get; init; }
    public bool Paged { get; init; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fletta", "settings.json");

    public static AppSettings Load()
    {
        try { return Parse(File.ReadAllText(FilePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    public static AppSettings Parse(string json)
    {
        try { return JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings);

    /// <summary>Speichern ist Komfort: schlägt es fehl (Profil voll, gesperrt), geht nur die Einstellung verloren.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, ToJson());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Liegt das gemerkte Fenster noch zu einem brauchbaren Teil (100 × 50 DIP) auf dem virtuellen
    /// Bildschirm? Sonst stünde es nach dem Abstecken eines Monitors unerreichbar daneben.
    /// </summary>
    public bool FitsOn(Rect virtualScreen)
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return false;
        var visible = Rect.Intersect(new Rect(Left, Top, Width, Height), virtualScreen);
        return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 50;
    }
}

/// <summary>Vom Compiler erzeugte JSON-Verarbeitung: kein Reflection-Aufwand beim Start.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
partial class SettingsJson : JsonSerializerContext;
