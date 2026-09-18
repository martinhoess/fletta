using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Windows;
using System.Windows.Threading;
using Fletta.Pdfium;
using Windows.Graphics.Printing;

namespace Fletta;

// --- Schnittstellen des Drucksystems; Reihenfolge = Vtable (Windows SDK 10.0.26100) ---

[GeneratedComInterface, Guid("1b8efec4-3019-4c27-964e-367202156906")]
internal partial interface IPrintDocumentPackageTarget
{
    void GetPackageTargetTypes(out uint targetCount, out nint targetTypes);
    void GetPackageTarget(in Guid guidTargetType, in Guid riid, out nint target);
    void Cancel();
}

[GeneratedComInterface, Guid("1a6dd0ad-1e2a-4e99-a5ba-91f17818290e")]
internal partial interface IPrintPreviewDxgiPackageTarget
{
    void SetJobPageCount(uint countType, uint count);
    void DrawPage(uint jobPageNumber, nint pageImage, float dpiX, float dpiY);
    void InvalidatePreview();
}

/// <summary>
/// Ein Druckauftrag aus Sicht des Dokuments. Alle Größen in PDF-Punkten, QuarterTurns wie angezeigt.
/// Notify meldet zurück ins Fenster (schon auf dem UI-Thread).
/// </summary>
public sealed record PrintJob(PageRenderer Renderer, IReadOnlyList<Size> PageSizes, int[] QuarterTurns,
                              string Title, IReadOnlyList<(int From, int To)> Preselect, Action<string> Notify)
{
    /// <summary>Aufsteigende 0-basierte Seiten als zusammenhängende 1-basierte Läufe — der Dialog kennt nur Bereiche.</summary>
    public static (int From, int To)[] RunsOf(IReadOnlyList<int> pages)
    {
        var runs = new List<(int, int)>();
        for (var i = 0; i < pages.Count; i++)
        {
            var start = i;
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            runs.Add((pages[start] + 1, pages[i] + 1));
        }
        return [.. runs];
    }

    /// <summary>Seiten aus den Bereichen des Dialogs (1-basiert); leer heißt alle.</summary>
    public int[] SelectedPages(PrintTaskOptions? options)
    {
        var all = () => Enumerable.Range(0, PageSizes.Count).ToArray();
        var ranges = options?.CustomPageRanges;
        if (ranges is null || ranges.Count == 0) return all();
        var pages = new SortedSet<int>();
        foreach (var range in ranges)
        {
            var from = Math.Clamp(range.FirstPageNumber, 1, PageSizes.Count);
            // Offene Bereiche („5-“) melden 0 oder -1 als Ende.
            var last = range.LastPageNumber < from ? PageSizes.Count : range.LastPageNumber;
            for (var page = from; page <= Math.Min(last, PageSizes.Count); page++) pages.Add(page - 1);
        }
        return pages.Count == 0 ? all() : [.. pages];
    }

    /// <summary>
    /// Drehung wie angezeigt; passt die Seite nicht zur Blattausrichtung, eine Vierteldrehung zurück —
    /// wie beim Querformat des Treibers: jede so gedruckte Seite liest man, indem man das Blatt im
    /// Uhrzeigersinn dreht. (Vorwärts drehte eine mit R gedrehte Seite auf den Kopf.)
    /// </summary>
    public int TurnsFor(int page, bool sheetIsLandscape)
    {
        var turns = QuarterTurns[page];
        var shown = ShownSize(page, turns);
        return shown.Width > shown.Height != sheetIsLandscape ? (turns + 3) % 4 : turns;
    }

    public Size ShownSize(int page, int turns)
    {
        var size = PageSizes[page];
        return turns % 2 == 1 ? new Size(size.Height, size.Width) : size;
    }

    /// <summary>
    /// Ein Blatt als BGRA-Puffer: weiß, Seite mittig eingepasst, gedreht wie angezeigt. Blockiert auf
    /// dem Render-Thread — PDFium sieht nur diesen. Bricht der Renderer ab, fliegt TaskCanceledException.
    /// </summary>
    public byte[] RenderSheet(int page, int sheetWidth, int sheetHeight)
    {
        var stride = sheetWidth * 4;
        var sheet = new byte[stride * sheetHeight];
        Array.Fill(sheet, byte.MaxValue); // weißer Grund, Alpha inklusive
        var turns = TurnsFor(page, sheetWidth > sheetHeight);
        var shown = ShownSize(page, turns);
        var scale = Math.Min(sheetWidth / shown.Width, sheetHeight / shown.Height);
        int width = Math.Max(1, (int)(shown.Width * scale)), height = Math.Max(1, (int)(shown.Height * scale));
        var bitmap = Renderer.Invoke(document => document.Render(page, width, height, 96, 96, turns, RenderPurpose.Print))
                             .GetAwaiter().GetResult();
        var offset = (sheetHeight - height) / 2 * stride + (sheetWidth - width) / 2 * 4;
        bitmap.CopyPixels(new Int32Rect(0, 0, width, height), sheet, stride, offset);
        // PDFium liefert BGRx: das vierte Byte ist nicht verlässlich gesetzt, DXGI/D2D brauchen es opak.
        var words = MemoryMarshal.Cast<byte, uint>(sheet.AsSpan());
        for (var i = 0; i < words.Length; i++) words[i] |= 0xFF000000;
        return sheet;
    }
}

/// <summary>
/// Moderner Windows-Druckdialog mit echter Vorschau. Alles hier — WinRT, Direct3D, Direct2D — wird
/// erst beim ersten Strg+P geladen und beim Ende des Auftrags wieder freigegeben.
/// </summary>
public static class SystemPrint
{
    /// <summary>
    /// Zeigt den Dialog für hwnd und kehrt erst zurück, wenn ein daraus entstandener Auftrag durch ist —
    /// solange braucht der Renderer die Seiten. Ohne Auftrag (Dialog abgebrochen) sofort.
    /// </summary>
    public static async Task ShowAsync(nint hwnd, PrintJob job)
    {
        var ui = Dispatcher.CurrentDispatcher;
        var notify = job.Notify;
        var source = new PrintSource(job with { Notify = text => ui.InvokeAsync(() => notify(text)) });
        var started = false;
        var done = new TaskCompletionSource();
        var manager = PrintManagerInterop.GetForWindow(hwnd);
        void Requested(PrintManager _, PrintTaskRequestedEventArgs e)
        {
            var task = e.Request.CreatePrintTask(job.Title, args => args.SetSource(source.AsDocumentSource()));
            // Ohne diese Zeile zeigt der Dialog gar keine Seitenauswahl, egal was in PageRangeOptions steht.
            task.Options.DisplayedOptions.Add(StandardPrintTaskOptions.CustomPageRanges);
            var ranges = task.Options.PageRangeOptions;
            ranges.AllowAllPages = ranges.AllowCustomSetOfPages = true;
            ranges.AllowCurrentPage = false; // der Dialog weiß nicht, welche Seite Fletta gerade zeigt
            // Markierte Miniaturen kommen vorbelegt in den Dialog, je zusammenhängendem Lauf ein Bereich.
            foreach (var (from, to) in job.Preselect) task.Options.CustomPageRanges.Add(new PrintPageRange(from, to));
            task.Completed += (_, _) => { source.Dispose(); done.TrySetResult(); };
            started = true;
        }
        manager.PrintTaskRequested += Requested;
        try
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        finally
        {
            manager.PrintTaskRequested -= Requested;
            if (!started) { source.Dispose(); done.TrySetResult(); } // abgebrochen, es gab nie einen Auftrag
        }
        await done.Task;
    }
}
