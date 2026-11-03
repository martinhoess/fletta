using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using Fletta.Pdfium;

namespace Fletta;

/// <summary>
/// Druckt Seiten über die Windows-Druckausgabe: PDFium zeichnet direkt auf das Drucker-HDC, also
/// scharf und ohne riesige Bilder im Spooler. Muss auf dem Render-Thread laufen (PageRenderer.Invoke),
/// weil es PDFium aufruft; der StandardPrintController braucht dafür kein Fenster.
/// </summary>
static partial class PagePrinter
{
    public const string PdfPrinter = "Microsoft Print to PDF";
    const int HorizontalResolution = 8, VerticalResolution = 10; // HORZRES / VERTRES für GetDeviceCaps

    /// <summary>
    /// Jede Seite eingepasst in den druckbaren Bereich, Hoch- oder Querformat nach der angezeigten
    /// Drehung. quarterTurns ist je Seite indiziert. Mit toFile druckt der Drucker in diese Datei
    /// statt auf Papier (für „Microsoft Print to PDF“ und den Selbsttest).
    /// </summary>
    public static void Print(PdfDocument document, IReadOnlyList<int> pages, IReadOnlyList<int> quarterTurns,
                             string printer, short copies, string title, string? toFile = null)
    {
        // Während des Drucks hält der Render-Thread den Prozess am Leben: wird das Fenster mitten im
        // Auftrag geschlossen, bräche ein Hintergrund-Thread sonst ohne EndDoc ab, der Druck wäre weg.
        var thread = Thread.CurrentThread;
        var wasBackground = thread.IsBackground;
        thread.IsBackground = false;
        try
        {
            PrintJob(document, pages, quarterTurns, printer, copies, title, toFile);
        }
        finally
        {
            thread.IsBackground = wasBackground;
        }
    }

    static void PrintJob(PdfDocument document, IReadOnlyList<int> pages, IReadOnlyList<int> quarterTurns,
                         string printer, short copies, string title, string? toFile)
    {
        using var job = new PrintDocument { DocumentName = title, PrintController = new StandardPrintController() };
        job.PrinterSettings.PrinterName = printer;
        job.PrinterSettings.Copies = copies;
        if (toFile is not null)
        {
            job.PrinterSettings.PrintToFile = true;
            job.PrinterSettings.PrintFileName = toFile;
        }
        if (!job.PrinterSettings.IsValid) throw new InvalidPrinterException(job.PrinterSettings);

        var next = 0;
        Size Shown(int page)
        {
            var size = document.PageSize(page);
            return quarterTurns[page] % 2 == 1 ? new Size(size.Height, size.Width) : size;
        }
        job.QueryPageSettings += (_, e) =>
        {
            var shown = Shown(pages[next]);
            e.PageSettings.Landscape = shown.Width > shown.Height;
        };
        job.PrintPage += (_, e) =>
        {
            var page = pages[next];
            var shown = Shown(page);
            var hdc = e.Graphics!.GetHdc();
            try
            {
                // Das HDC zählt in Gerätepixeln ab der linken oberen Ecke des druckbaren Bereichs.
                int width = GetDeviceCaps(hdc, HorizontalResolution), height = GetDeviceCaps(hdc, VerticalResolution);
                var scale = Math.Min(width / shown.Width, height / shown.Height);
                int pageWidth = (int)(shown.Width * scale), pageHeight = (int)(shown.Height * scale);
                document.Print(page, hdc, (width - pageWidth) / 2, (height - pageHeight) / 2, pageWidth, pageHeight, quarterTurns[page]);
            }
            finally
            {
                e.Graphics.ReleaseHdc(hdc); // vor dem Ende des Ereignisses, sonst schlägt EndPage fehl
            }
            e.HasMorePages = ++next < pages.Count;
        };
        job.Print();
    }

    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(nint hdc, int index);
}
