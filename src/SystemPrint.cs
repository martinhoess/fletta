using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Windows;
using System.Windows.Threading;
using Fletta.Pdfium;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;

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

    public const string ScalingOption = "FlettaScaling", PercentOption = "FlettaPercent", CustomSize = "custom";

    /// <summary>Prozent aus dem Dialog, wenn „Eigene Größe“ gewählt und die Zahl gültig ist; sonst null = eingepasst.</summary>
    public static double? PercentOf(PrintTaskOptions? options)
    {
        if (options is null) return null;
        var all = PrintTaskOptionDetails.GetFromPrintTaskOptions(options).Options;
        return all.TryGetValue(ScalingOption, out var mode) && mode.Value as string == CustomSize
            && all.TryGetValue(PercentOption, out var percent) ? ParsePercent(percent.Value as string) : null;
    }

    /// <summary>„150“, „87,5 %“, „87.5“ → Zahl; leer, Unsinn oder außerhalb 1–1000 → null.</summary>
    public static double? ParsePercent(string? text) =>
        double.TryParse(text?.Replace('%', ' ').Replace(',', '.').Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent is >= 1 and <= 1000
            ? percent : null;

    /// <summary>Pixel je PDF-Punkt: ohne percent aufs Blatt eingepasst, sonst so groß wie verlangt — auch über das Blatt hinaus.</summary>
    public static double ScaleFor(Size shown, int sheetWidth, int sheetHeight, double pixelsPerPoint, double? percent) =>
        percent is { } p ? pixelsPerPoint * p / 100 : Math.Min(sheetWidth / shown.Width, sheetHeight / shown.Height);

    public Size ShownSize(int page, int turns)
    {
        var size = PageSizes[page];
        return turns % 2 == 1 ? new Size(size.Height, size.Width) : size;
    }

    /// <summary>
    /// Ein Blatt als BGRA-Puffer: weiß, Seite mittig eingepasst oder in percent (pixelsPerPoint = 100 %), was übers
    /// Blatt ragt, fällt weg; gedreht wie angezeigt. Blockiert auf
    /// dem Render-Thread — PDFium sieht nur diesen. Bricht der Renderer ab, fliegt TaskCanceledException.
    /// </summary>
    public byte[] RenderSheet(int page, int sheetWidth, int sheetHeight, double pixelsPerPoint, double? percent)
    {
        var stride = sheetWidth * 4;
        var sheet = new byte[stride * sheetHeight];
        var turns = TurnsFor(page, sheetWidth > sheetHeight);
        var shown = ShownSize(page, turns);
        var scale = ScaleFor(shown, sheetWidth, sheetHeight, pixelsPerPoint, percent);
        int width = Math.Max(1, (int)(shown.Width * scale)), height = Math.Max(1, (int)(shown.Height * scale));
        // Direkt aufs Blatt: bei Übergröße liegt die Ecke im Minus, PDFium schneidet am Rand ab (kein Riesenbild).
        var placement = new Int32Rect((sheetWidth - width) / 2, (sheetHeight - height) / 2, width, height);
        var bitmap = Renderer.Invoke(document => document.Render(page, sheetWidth, sheetHeight, 96, 96, turns, RenderPurpose.Print, placement))
                             .GetAwaiter().GetResult();
        bitmap.CopyPixels(sheet, stride, 0);
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
            var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(task.Options);
            var scaling = details.CreateItemListOption(PrintJob.ScalingOption, "Skalierung");
            scaling.AddItem("fit", "An Blatt anpassen");
            scaling.AddItem(PrintJob.CustomSize, "Eigene Größe");
            scaling.TrySetValue("fit");
            var percent = details.CreateTextOption(PrintJob.PercentOption, "Größe in % (bei eigener Größe)");
            percent.MaxCharacters = 6;
            percent.TrySetValue("100");
            task.Options.DisplayedOptions.Add(PrintJob.ScalingOption);
            task.Options.DisplayedOptions.Add(PrintJob.PercentOption);
            details.OptionChanged += (_, args) =>
            {
                if (args.OptionId as string is not (PrintJob.ScalingOption or PrintJob.PercentOption)) return;
                // Nur bei „Eigene Größe“ ein Fehler: beim Einpassen spielt das Feld keine Rolle und soll nichts sperren.
                percent.ErrorText = scaling.Value as string == PrintJob.CustomSize
                    && PrintJob.ParsePercent(percent.Value as string) is null ? "Zahl von 1 bis 1000" : "";
                source.InvalidatePreview();
            };
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
