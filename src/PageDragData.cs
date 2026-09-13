using System.IO;
using System.Windows;
using Fletta.Pdfium;

namespace Fletta;

/// <summary>
/// Ziehdaten für Miniaturen. Die PDF mit den Seiten entsteht erst, wenn ein Ziel die Datei wirklich
/// anfordert (Explorer, anderes Fenster) — ein Zug innerhalb der eigenen Leiste liest nur die Seitennummern.
/// </summary>
sealed class PageDragData(int[] pages, Func<string> export) : IDataObject
{
    // Der Explorer verschiebt auf demselben Laufwerk sonst standardmäßig — Seiten sollen kopiert werden,
    // verschoben nur mit Umschalt. Der Wert ist DROPEFFECT_COPY.
    const string PreferredDropEffect = "Preferred DropEffect";
    // Ein Fenster mit ungespeichertem Einfügen braucht die Datei fürs Rückgängig-Machen noch.
    // ponytail: feste Frist statt Nachfrage bei den Fenstern; ein Fenster, das länger offen bleibt, verliert Strg+Z dafür.
    static readonly TimeSpan KeepExports = TimeSpan.FromDays(7);

    string? file;

    /// <summary>Warum die Datei nicht entstand; das Fenster meldet es nach dem Ziehen.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Ordner für gezogene Seiten. Je Zug ein eigener Unterordner: so behält die Datei einen lesbaren Namen,
    /// und ein anderes Fenster, das sie eingefügt hat, findet sie beim Rückgängig-Machen noch vor.
    /// </summary>
    public static string ExportFolder() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Fletta", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Beim Start: Züge älter als eine Woche wegräumen. Was noch in Benutzung ist, bleibt eben liegen.</summary>
    public static void CleanUp()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Fletta"));
        if (!root.Exists) return;
        foreach (var folder in root.EnumerateDirectories().Where(folder => DateTime.UtcNow - folder.CreationTimeUtc > KeepExports))
        {
            try { folder.Delete(recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public object? GetData(string format, bool autoConvert)
    {
        if (format == ThumbnailPanel.PagesFormat) return $"{Environment.ProcessId}:{string.Join(",", pages)}";
        if (format == PreferredDropEffect) return new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy));
        if (format != DataFormats.FileDrop) return null;
        if (file is null && Error is null)
        {
            try { file = export(); }
            catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Error = e.Message;
            }
        }
        return file is null ? null : new[] { file };
    }

    public object? GetData(string format) => GetData(format, true);
    public object? GetData(Type format) => GetData(format.FullName!, true);
    public bool GetDataPresent(string format, bool autoConvert) => GetFormats().Contains(format);
    public bool GetDataPresent(string format) => GetDataPresent(format, true);
    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, true);
    public string[] GetFormats(bool autoConvert) => [ThumbnailPanel.PagesFormat, DataFormats.FileDrop, PreferredDropEffect];
    public string[] GetFormats() => GetFormats(true);

    // Ziele wie der Explorer melden ihr Ergebnis per SetData zurück; gebraucht wird es hier nicht.
    public void SetData(object data) { }
    public void SetData(string format, object data) { }
    public void SetData(string format, object data, bool autoConvert) { }
    public void SetData(Type format, object data) { }
}
