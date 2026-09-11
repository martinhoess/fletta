using System.IO;
using System.Drawing.Printing;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fletta.Pdfium;

namespace Fletta;

/// <summary>Aufruf: Fletta.exe --selftest. Rückgabe = Zahl der Fehler, 0 = grün.</summary>
static class SelfTest
{
    public static int Run()
    {
        var failures = 0;
        var dir = Directory.CreateTempSubdirectory("fletta-selftest-");
        try
        {
            // Leerzeichen und Umlaute prüfen, dass der Pfad als UTF-8 bei PDFium ankommt.
            var good = Path.Combine(dir.FullName, "eine Seite äöü.pdf");
            File.WriteAllBytes(good, MinimalPdf());
            using (var doc = PdfDocument.Open(good))
            {
                failures += Check("zwei Seiten", doc.PageCount == 2);
                var size = doc.PageSize(0);
                failures += Check("A4 in Punkten", size.Width == 595 && size.Height == 842);
                var turned = doc.PageSize(1);
                failures += Check("/Rotate 90 im PDF tauscht die Seitenkanten", turned.Width == 842 && turned.Height == 595);

                // Ein Fünftel der Punktgröße: das blaue Rechteck beginnt 20 px vom Rand,
                // das rote Quadrat liegt oben links bei 4–18 px.
                var bitmap = doc.Render(0, 119, 168, 96, 96, 0);
                failures += Check("Rand bleibt weiß", RgbAt(bitmap, 2, 2) == 0xFFFFFF);
                failures += Check("Mitte ist blau", RgbAt(bitmap, 60, 84) == 0x0000FF);
                failures += Check("rot oben links", RgbAt(bitmap, 11, 11) == 0xFF0000);

                // Rechts gedreht wandert oben links nach oben rechts: (x, y) → (Höhe − 1 − y, x).
                var right = doc.Render(0, 168, 119, 96, 96, 1);
                failures += Check("rechts gedreht: rot oben rechts", RgbAt(right, 156, 11) == 0xFF0000);
                failures += Check("rechts gedreht: oben links weiß", RgbAt(right, 11, 11) == 0xFFFFFF);
                failures += Check("Seitentext", doc.PageText(0).Contains("Fletta Test"));
                failures += Check("Gliederung: ein Lesezeichen auf Seite 2", doc.Outline().SequenceEqual([new OutlineEntry("Kapitel Zwei", 1, 0)]));
            }

            failures += Check("fehlende Datei", OpenError(Path.Combine(dir.FullName, "fehlt.pdf")) == PdfException.FileError);
            var junk = Path.Combine(dir.FullName, "kaputt.pdf");
            File.WriteAllText(junk, "kein PDF");
            failures += Check("kaputte Datei", OpenError(junk) == PdfException.FormatError);

            failures += LayoutChecks();
            failures += RendererChecks(good, junk, dir.FullName);
        }
        finally
        {
            // Hält der Spooler eine Datei noch offen, bleibt der Ordner eben liegen — kein Grund, den
            // Selbsttest ohne Ergebniszeile abstürzen zu lassen.
            try { dir.Delete(recursive: true); }
            catch (IOException e) { Console.Error.WriteLine($"warn   Temp-Ordner blieb liegen: {e.Message}"); }
        }

        Console.Error.WriteLine(failures == 0 ? "selftest: ok" : $"selftest: {failures} Fehler");
        return failures;
    }

    static int LayoutChecks()
    {
        Size a4 = new(595, 842), landscape = new(842, 595);
        var layout = new PageLayout([a4, landscape], 1);
        var viewport = new Size(842 + 2 * PageLayout.MarginX + 1, 600);
        var failures = 0;
        failures += Check("Seite 2 beginnt nach Seite 1 und Abstand", layout.Top(1) == PageLayout.MarginTop + 842 + PageLayout.Gap);
        failures += Check("Höhe endet mit unterem Rand", layout.Height == layout.Top(1) + 595 + PageLayout.MarginBottom);
        failures += Check("Breite nach der breitesten Seite", layout.Width == 842 + 2 * PageLayout.MarginX);
        failures += Check("PageAt vor der ersten Seite", layout.PageAt(0) == 0);
        failures += Check("PageAt knapp vor Seite 2", layout.PageAt(layout.Top(1) - 0.1) == 0);
        failures += Check("PageAt genau auf Seite 2", layout.PageAt(layout.Top(1)) == 1);
        failures += Check("PageAt hinter dem Ende", layout.PageAt(1e9) == 1);
        failures += Check("Seitenbreite passt die breiteste Seite ein",
            Math.Abs(PageLayout.ScaleFor(ZoomMode.FitWidth, 100, [a4, landscape], 0, viewport) - 1) < 1e-9);
        failures += Check("100 % sind 96/72 DIP je Punkt",
            PageLayout.ScaleFor(ZoomMode.Fixed, 100, [a4], 0, viewport) == PageLayout.DipPerPoint);
        failures += Check("ganze Seite richtet sich hier nach der Höhe",
            Math.Abs(PageLayout.ScaleFor(ZoomMode.FitPage, 100, [a4], 0, viewport) - (600 - 2 * PageLayout.Gap) / 842) < 1e-9);
        // Stark verkleinert: zehn A4-Seiten bei 10 %, Sichtbereich 600 hoch, Dokument 1040 hoch.
        var tiny = new PageLayout(Enumerable.Repeat(a4, 10).ToArray(), 0.1);
        failures += Check("ganz oben ist Seite 1 aktuell", tiny.CurrentPage(0, 600) == 0);
        failures += Check("ganz unten ist die letzte Seite aktuell", tiny.CurrentPage(tiny.Height - 600, 600) == 9);
        failures += Check("Sprung zur letzten Seite endet am Dokumentende",
            tiny.ScrollTargetFor(9, 600) == tiny.Height - 600);
        // Frei zur Mitte gescrollt (Offset 200): oben steht Seite 2, der Messpunkt liegt auf Seite 5.
        failures += Check("← → nach freiem Scrollen von der Seite an der Oberkante",
            tiny.StepOrigin(200, 600, tiny.CurrentPage(200, 600), pinned: false) == 1 && tiny.CurrentPage(200, 600) == 4);
        failures += Check("← → nach Sprung, Zoom oder Drehen von der festgehaltenen Seite",
            tiny.StepOrigin(200, 600, 4, pinned: true) == 4);
        failures += Check("← → am Dokumentende von der aktuellen Seite",
            tiny.StepOrigin(tiny.Height - 600, 600, 9, pinned: false) == 9);
        failures += Check("Sprungziel zeigt die Seite oben mit Abstand",
            layout.ScrollTargetFor(1, 600) == layout.Top(1) - PageLayout.Gap);

        // Doppelseite, fünf A4-Seiten bei 100 % Punktmaß: Zeilen 1 | 2 3 | 4 5.
        var book = new PageLayout(Enumerable.Repeat(a4, 5).ToArray(), 1, spreads: true);
        failures += Check("Doppelseite: 2 und 3 in einer Zeile", book.Top(1) == book.Top(2) && book.Top(3) > book.Top(2));
        failures += Check("Doppelseite: 3 steht rechts neben 2", book.Left(2, 2000) - book.Left(1, 2000) == 595 + PageLayout.SpreadGap);
        failures += Check("Doppelseite: Deckblatt mittig", book.Left(0, 2000) == (2000 - 595) / 2.0);
        failures += Check("Doppelseite: Breite nach dem Paar", book.Width == 2 * 595 + PageLayout.SpreadGap + 2 * PageLayout.MarginX);
        failures += Check("Doppelseite: → vom Deckblatt auf 2, von 2 auf 4",
            book.NextRowPage(0, +1) == 1 && book.NextRowPage(1, +1) == 3 && book.NextRowPage(3, +1) == 3);
        failures += Check("Doppelseite: ← von 5 auf 2", book.NextRowPage(4, -1) == 1);
        failures += Check("Doppelseite: sichtbar ganz ergibt 1–5", book.VisiblePages(0, 1e9) == (0, 4));
        failures += Check("Doppelseite: aktuell ist die linke Seite", book.PageAt(book.Top(2)) == 1);
        failures += Check("Doppelseite: Seitenbreite passt das Paar ein",
            Math.Abs(PageLayout.ScaleFor(ZoomMode.FitWidth, 100, Enumerable.Repeat(a4, 5).ToArray(), 0,
                new Size(2 * 595 + PageLayout.SpreadGap + 2 * PageLayout.MarginX + 1, 600), spreads: true) - 1) < 1e-9);

        // Seitenweise: drei A4-Seiten bei 50 %, Sichthöhe 600 — jede Seite hat einen eigenen Platz.
        var paged = new PageLayout(Enumerable.Repeat(a4, 3).ToArray(), 0.5, slotHeight: 600);
        failures += Check("Seitenweise: je Seite eine Sichthöhe", paged.Height == 1800);
        failures += Check("Seitenweise: Seite mittig im Platz", paged.Top(1) == 600 + (600 - 421) / 2.0);
        failures += Check("Seitenweise: Sprung zeigt den ganzen Platz", paged.ScrollTargetFor(1, 600) == 600);
        failures += Check("Seitenweise: Platz von Seite 2", paged.RowSpan(1) == (600, 1200));

        // Lesestelle beim Zoomen, gerechnet am Platz der Zeile.
        // Sichthöhe 300: bei 600 läge Top(1) + 100 hinter dem Dokumentende (1523 − 600) und würde zu Recht begrenzt.
        failures += Check("Lesestelle bleibt bei gleichem Layout", layout.KeepPosition(layout, layout.Top(1) + 100, 1, 300) == layout.Top(1) + 100);
        var doubled = new PageLayout([a4, landscape], 2);
        failures += Check("Lesestelle wächst mit dem Zoom mit", Math.Abs(doubled.KeepPosition(layout, layout.Top(1) + 100, 1, 300) - (doubled.Top(1) + 200)) < 1e-9);
        var pagedBig = new PageLayout(Enumerable.Repeat(a4, 3).ToArray(), 1.5, slotHeight: 900);
        var pagedSmall = new PageLayout(Enumerable.Repeat(a4, 3).ToArray(), 0.5, slotHeight: 900);
        failures += Check("Seitenweise: nach dem Verkleinern steht die Zeile wieder ganz im Bild",
            pagedSmall.KeepPosition(pagedBig, pagedBig.RowSpan(1).Top + 163, 1, 900) == pagedSmall.RowSpan(1).Top);

        // Seitenleiste: feste Eintragshöhe 200, Sichthöhe 600, 990 Seiten.
        failures += Check("Miniaturen oben: Einträge 1–4", ThumbnailPanel.VisibleRange(0, 600, 200, 990) == (0, 3));
        failures += Check("Miniaturen Mitte", ThumbnailPanel.VisibleRange(1000, 600, 200, 990) == (5, 8));
        failures += Check("Miniaturen am Ende begrenzt", ThumbnailPanel.VisibleRange(197_900, 600, 200, 990) == (989, 989));
        failures += Check("Miniaturen ohne Seiten leer", ThumbnailPanel.VisibleRange(0, 600, 200, 0) == (0, -1));

        // Einstellungen: Hin und zurück durch JSON, kaputte Datei, Fenster außerhalb des Bildschirms.
        var saved = new AppSettings { Left = 100, Top = 50, Zoom = ZoomMode.FitPage, ZoomPercent = 150, Spreads = true, SidebarHidden = true };
        failures += Check("Einstellungen überstehen JSON", AppSettings.Parse(saved.ToJson()) == saved);
        failures += Check("Einstellungen: Zoommodus als Text", saved.ToJson().Contains("\"FitPage\""));
        failures += Check("kaputte Einstellungen ergeben Standardwerte", AppSettings.Parse("{ kaputt") == new AppSettings());
        var screen = new Rect(0, 0, 1920, 1080);
        failures += Check("Fenster auf dem Bildschirm bleibt", saved.FitsOn(screen));
        failures += Check("Fenster neben dem Bildschirm wird verworfen", !(saved with { Left = 3000 }).FitsOn(screen));
        failures += Check("nie gespeichertes Fenster wird mittig", !new AppSettings().FitsOn(screen));

        // Anmeldung als PDF-Programm: Pfad mit Leerzeichen muss in Anführungszeichen stehen.
        var entries = FileAssociation.Entries(@"C:\Program Files\Fletta\Fletta.exe");
        failures += Check("Öffnen-Befehl mit Anführungszeichen",
            entries.Any(e => e.Key.EndsWith(@"shell\open\command") && e.Value == "\"C:\\Program Files\\Fletta\\Fletta.exe\" \"%1\""));
        failures += Check(".pdf zeigt auf die ProgID", entries.Contains((@"Software\Classes\.pdf\OpenWithProgids", FileAssociation.ProgId, "")));

        failures += Check("Druckbereich 3–5 von 10", PagePrinter.PagesFor(3, 5, 10).SequenceEqual([2, 3, 4]));
        failures += Check("Druckbereich über das Ende begrenzt", PagePrinter.PagesFor(0, 99, 10).SequenceEqual(Enumerable.Range(0, 10)));
        failures += Check("Druckbereich rückwärts ergibt die Startseite", PagePrinter.PagesFor(7, 2, 10).SequenceEqual([6]));

        failures += Check("Zoom hoch von 100", PageLayout.NextZoom(100, +1) == 110);
        failures += Check("Zoom runter von 100", PageLayout.NextZoom(100, -1) == 90);
        failures += Check("Zoom zwischen zwei Stufen", PageLayout.NextZoom(118.3, +1) == 125 && PageLayout.NextZoom(118.3, -1) == 110);
        failures += Check("Zoom bleibt an den Enden", PageLayout.NextZoom(400, +1) == 400 && PageLayout.NextZoom(25, -1) == 25);
        return failures;
    }

    /// <summary>
    /// Der Render-Thread end-to-end. Vor dem nächsten Renderer und vor dem Löschen des Ordners
    /// wird auf Closed gewartet: sonst riefen zwei Threads PDFium auf, oder die Datei wäre noch offen.
    /// </summary>
    static int RendererChecks(string good, string junk, string dir)
    {
        var failures = 0;
        var broken = new PageRenderer(junk, Dispatcher.CurrentDispatcher, _ => { });
        using (broken)
            failures += Check("Render-Thread meldet kaputte Datei", FailureCode(broken.Opened) == PdfException.FormatError);
        failures += Check("Render-Thread endet nach Fehlschlag", broken.Closed.Wait(TimeSpan.FromSeconds(5)));

        RenderResult? result = null;
        var frame = new DispatcherFrame();
        var renderer = new PageRenderer(good, Dispatcher.CurrentDispatcher, r => { result = r; frame.Continue = false; });
        using (renderer)
        {
            failures += Check("Render-Thread liefert Seitengrößen",
                renderer.Opened.Wait(TimeSpan.FromSeconds(5)) && renderer.Opened.Result.Count == 2);
            renderer.Request([new RenderRequest(0, 119, 168, 96, 96, 0)]);
            // Die Zustellung läuft über den Dispatcher, der muss dafür pumpen.
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal,
                                              (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            timeout.Stop();

            var text = renderer.Invoke(document => document.PageText(0));
            failures += Check("Aufgabe auf dem Render-Thread (Seitentext)",
                text.Wait(TimeSpan.FromSeconds(5)) && text.Result.Contains("Fletta Test"));
            failures += PrintCheck(renderer, dir);
        }
        failures += Check("Render-Thread stellt das Bild zu", result?.Bitmap is { } bitmap && RgbAt(bitmap, 60, 84) == 0x0000FF);
        failures += Check("Render-Thread schließt das Dokument", renderer.Closed.Wait(TimeSpan.FromSeconds(5)));
        failures += Check("Aufgabe nach dem Ende wird abgebrochen", renderer.Invoke(_ => 1).IsCanceled);
        return failures;
    }

    /// <summary>
    /// Echter Druck über den Windows-Spooler in eine PDF-Datei: beide Seiten, die zweite gedreht.
    /// Ohne „Microsoft Print to PDF“ sichtbar übersprungen. Der Spooler schreibt die Datei erst nach
    /// Print(), deshalb wird bis zu 30 s gewartet, bis sie vollständig und freigegeben ist.
    /// </summary>
    static int PrintCheck(PageRenderer renderer, string dir)
    {
        if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(PagePrinter.PdfPrinter))
        {
            Console.Error.WriteLine($"skip   Drucken: „{PagePrinter.PdfPrinter}“ ist nicht installiert");
            return 0;
        }
        var target = Path.Combine(dir, "druck.pdf");
        var job = renderer.Invoke(document => PagePrinter.Print(document, [0, 1], [0, 1], PagePrinter.PdfPrinter, 1, "Fletta-Selbsttest", target));
        var printed = false;
        try
        {
            printed = job.Wait(TimeSpan.FromSeconds(30));
            for (var waited = 0; printed && !IsCompletePdf(target) && waited < 30_000; waited += 250) Thread.Sleep(250);
        }
        catch (AggregateException e)
        {
            Console.Error.WriteLine($"       Drucken warf: {e.InnerException?.Message}");
        }
        return Check("Drucken in eine PDF-Datei (zwei Seiten, zweite gedreht)", printed && IsCompletePdf(target));
    }

    /// <summary>Fertig erst, wenn der Spooler die Datei freigegeben hat und sie mit %%EOF endet.</summary>
    static bool IsCompletePdf(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            return bytes.AsSpan().StartsWith("%PDF"u8) && bytes.AsSpan().TrimEnd("\r\n "u8).EndsWith("%%EOF"u8);
        }
        catch (IOException) { return false; } // fehlt noch oder der Spooler schreibt noch
    }

    static uint FailureCode(Task task)
    {
        try { task.Wait(TimeSpan.FromSeconds(5)); return 0; }
        catch (AggregateException e) when (e.InnerException is PdfException pdf) { return pdf.Code; }
    }

    static int Check(string name, bool ok)
    {
        Console.Error.WriteLine($"{(ok ? "ok    " : "FEHLER")} {name}");
        return ok ? 0 : 1;
    }

    static uint OpenError(string path)
    {
        try { PdfDocument.Open(path).Dispose(); return 0; }
        catch (PdfException e) { return e.Code; }
    }

    static int RgbAt(BitmapSource bitmap, int x, int y)
    {
        var bgrx = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bgrx, 4, 0);
        return bgrx[2] << 16 | bgrx[1] << 8 | bgrx[0];
    }

    /// <summary>
    /// Kleinstes gültiges PDF mit zwei A4-Seiten, Querverweise korrekt gezählt. Inhalt: blaues
    /// Rechteck mittig, rotes Quadrat oben links (daran lässt sich die Drehrichtung ablesen), darüber
    /// die Textzeile „Fletta Test“ in Helvetica für den Seitentext; ein Lesezeichen „Kapitel Zwei“ auf Seite 2.
    /// Seite 2 hat denselben Inhalt, trägt aber /Rotate 90 im PDF.
    /// </summary>
    static byte[] MinimalPdf()
    {
        const string content = "0 0 1 rg 100 100 395 642 re f 1 0 0 rg 20 752 70 70 re f BT 0 g /F1 18 Tf 110 780 Td (Fletta Test) Tj ET";
        const string resources = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] {resources} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Rotate 90 {resources} /Contents 4 0 R >>",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            "<< /Title (Kapitel Zwei) /Parent 6 0 R /Dest [5 0 R /Fit] >>",
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length); // reines ASCII: Zeichen = Bytes
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
