using System.IO;
using System.IO.Compression;
using System.Drawing.Printing;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
                failures += SearchAndLinkChecks(doc);
            }

            failures += Check("fehlende Datei", OpenError(Path.Combine(dir.FullName, "fehlt.pdf")) == PdfException.FileError);
            var junk = Path.Combine(dir.FullName, "kaputt.pdf");
            File.WriteAllText(junk, "kein PDF");
            failures += Check("kaputte Datei", OpenError(junk) == PdfException.FormatError);

            failures += LayoutChecks();
            failures += PasswordChecks(dir.FullName);
            failures += EditChecks(good, dir.FullName);
            failures += AnnotationChecks(good, dir.FullName);
            failures += FormChecks(dir.FullName);
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
        // Vorab gerendert: sichtbare, dann in Scrollrichtung, dann ein paar dagegen — je nach Abstand.
        var down = ThumbnailPanel.PrefetchOrder(100, 102, 990, up: false).ToArray();
        failures += Check("Miniaturen vorab nach unten", down.Take(5).SequenceEqual([100, 101, 102, 103, 104])
                          && down.Length == 3 + ThumbnailPanel.PrefetchAhead + ThumbnailPanel.PrefetchBehind && down[^1] == 100 - ThumbnailPanel.PrefetchBehind);
        var up = ThumbnailPanel.PrefetchOrder(100, 102, 990, up: true).ToArray();
        failures += Check("Miniaturen vorab nach oben", up.Take(5).SequenceEqual([100, 101, 102, 99, 98]) && up[^1] == 102 + ThumbnailPanel.PrefetchBehind);
        RenderRequest Main(int page) => new(page, 100, 100, 96, 96, 0);
        RenderRequest Thumb(int page) => new(page, 10, 10, 96, 96, 0, Thumbnail: true);
        failures += Check("Miniatur direkt hinter ihrer Hauptseite, übrige danach",
            MainWindow.Interleave([Main(5), Main(6), Main(4)], [Thumb(4), Thumb(5), Thumb(9)])
                .SequenceEqual([Main(5), Thumb(5), Main(6), Main(4), Thumb(4), Thumb(9)]));
        failures += Check("Miniaturen vorab an den Enden begrenzt", ThumbnailPanel.PrefetchOrder(0, 1, 3, up: false).SequenceEqual([0, 1, 2])
                          && !ThumbnailPanel.PrefetchOrder(0, -1, 0, up: false).Any());

        // Nachbarseiten: A4 in Seitenbreite (1500 px) zwei je Richtung, bei 24 MP nur eine — außer bei
        // Doppelseite, dort ist die kleinste Einheit eine Zeile aus zwei Seiten.
        var small = new RenderRequest(0, 1500, 2121, 180, 180, 0);
        var large = new RenderRequest(0, 4306, 5573, 520, 520, 0);
        failures += Check("kleine Seitenbilder: zwei Nachbarn", MainWindow.NeighboursFor(small, spreads: false) == 2);
        failures += Check("große Seitenbilder: ein Nachbar", MainWindow.NeighboursFor(large, spreads: false) == 1);
        failures += Check("große Seitenbilder, Doppelseite: ganze Nachbarzeile", MainWindow.NeighboursFor(large, spreads: true) == 2);

        // Update: Antwort der GitHub-API, auf das Nötige gekürzt.
        const string latest = """{"tag_name":"v0.11.0","assets":[{"name":"Fletta-0.11.0.zip","browser_download_url":"https://x/zip","digest":"sha256:00"},{"name":"Fletta-0.11.0-setup.exe","browser_download_url":"https://x/setup","digest":"sha256:ab12"}]}""";
        failures += Check("Update: Tag und Setup gelesen", Updater.Parse(latest) == new Release(new Version(0, 11, 0), "Fletta-0.11.0-setup.exe", "https://x/setup", "ab12"));
        failures += Check("Update: ohne Prüfsumme kein Update", Updater.Parse(latest.Replace("sha256:ab12", "")) is null);
        failures += Check("Update: kaputte Antwort", Updater.Parse("<html>") is null && Updater.Parse("""{"message":"rate limit"}""") is null);
        failures += Check("Update: laufende Version dreistellig", Updater.Current.Revision == -1 && Updater.Current.Major >= 0);
        // Liste von /releases, neueste zuerst: Vorabversion 0.14.0, Entwurf 0.15.0, Freigabe 0.13.0.
        const string list = """
            [{"tag_name":"v0.14.0-pre","prerelease":true,"draft":false,"assets":[{"name":"Fletta-0.14.0-setup.exe","browser_download_url":"https://x/pre","digest":"sha256:cc"}]},
             {"tag_name":"v0.15.0","prerelease":false,"draft":true,"assets":[{"name":"Fletta-0.15.0-setup.exe","browser_download_url":"https://x/draft","digest":"sha256:dd"}]},
             {"tag_name":"v0.13.0","prerelease":false,"draft":false,"assets":[{"name":"Fletta-0.13.0-setup.exe","browser_download_url":"https://x/rel","digest":"sha256:ee"}]}]
            """;
        failures += Check("Update: ohne Vorabversionen die Freigabe, Entwurf nie",
            Updater.Newest(list, preReleases: false) == new Release(new Version(0, 13, 0), "Fletta-0.13.0-setup.exe", "https://x/rel", "ee"));
        failures += Check("Update: mit Vorabversionen die neuere Vorabversion, „-pre“ abgetrennt",
            Updater.Newest(list, preReleases: true) == new Release(new Version(0, 14, 0), "Fletta-0.14.0-setup.exe", "https://x/pre", "cc", PreRelease: true));
        const string twins = """
            [{"tag_name":"v0.13.1-pre","prerelease":true,"assets":[{"name":"Fletta-0.13.1-setup.exe","browser_download_url":"https://x/pre","digest":"sha256:aa"}]},
             {"tag_name":"v0.13.1","prerelease":false,"assets":[{"name":"Fletta-0.13.1-setup.exe","browser_download_url":"https://x/rel","digest":"sha256:bb"}]}]
            """;
        failures += Check("Update: gleiche Nummer, Vorabversion zuerst gelistet: die Freigabe gewinnt",
            Updater.Newest(twins, preReleases: true) is { PreRelease: false, SetupUrl: "https://x/rel" });
        failures += Check("Update: kaputte Liste ergibt nichts", Updater.Newest("{}", true) is null && Updater.Newest("""[1, "x", {"tag_name": 5}]""", true) is null);

        // Einstellungen: Hin und zurück durch JSON, kaputte Datei, Fenster außerhalb des Bildschirms.
        var saved = new AppSettings { Left = 100, Top = 50, Zoom = ZoomMode.FitPage, ZoomPercent = 150, Spreads = true, SidebarHidden = true, PreReleases = true };
        failures += Check("Einstellungen überstehen JSON", AppSettings.Parse(saved.ToJson()) == saved);
        failures += Check("Einstellungen: Zoommodus als Text", saved.ToJson().Contains("\"FitPage\""));
        failures += Check("kaputte Einstellungen ergeben Standardwerte", AppSettings.Parse("{ kaputt") == new AppSettings());
        failures += Check("Standardwerte (Lage NaN) lassen sich speichern und zurücklesen", AppSettings.Parse(new AppSettings { PreReleases = true }.ToJson()) == new AppSettings { PreReleases = true });
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
        // Windows-Druckdialog: Seite 0 hoch und mit R gedreht, Seite 1 quer. Aufs Hochblatt: eine Vierteldrehung
        // zurück, also Seite 0 wieder aufrecht (nicht auf dem Kopf), Seite 1 gegen den Uhrzeigersinn.
        var printJob = new PrintJob(null!, [new Size(595, 842), new Size(842, 595)], [1, 0], "Test", [], _ => { });
        failures += Check("Druck: Drehung aufs Blatt", printJob.TurnsFor(0, sheetIsLandscape: false) == 0
            && printJob.TurnsFor(1, sheetIsLandscape: false) == 3 && printJob.TurnsFor(1, sheetIsLandscape: true) == 0);
        // Markierte Miniaturen 0,1,2 und 5 (0-basiert) werden im Dialog zu „1-3“ und „6“.
        failures += Check("Druck: markierte Seiten als Läufe",
            PrintJob.RunsOf([0, 1, 2, 5]).SequenceEqual([(1, 3), (6, 6)]) && PrintJob.RunsOf([]).Length == 0);

        // Drehung der Ansicht auf Anteile der Seite: oben links landet nach R oben rechts, L dreht zurück.
        failures += Check("Drehen: oben links wird oben rechts", PageLayout.Turn(new Point(0, 0), 1) == new Point(1, 0));
        var inside = new Point(0.2, 0.3);
        failures += Check("Drehen: zurück ergibt die Stelle", Near(PageLayout.Turn(PageLayout.Turn(inside, 1), -1), inside) && Near(PageLayout.Turn(inside, 4), inside));
        var turnedRect = PageLayout.Turn(new Rect(0.1, 0.2, 0.3, 0.1), 1);
        failures += Check("Drehen: Rechteck tauscht die Kanten",
            Math.Abs(turnedRect.X - 0.7) < 1e-9 && Math.Abs(turnedRect.Y - 0.1) < 1e-9 && Math.Abs(turnedRect.Width - 0.1) < 1e-9 && Math.Abs(turnedRect.Height - 0.3) < 1e-9);
        // Suche im gemerkten Text: ohne Groß/klein, nicht überlappend, Umlaute gleich behandelt.
        failures += Check("Suche: Groß/klein egal, auch Umlaute", MainWindow.FindAll("Öl ÖL öl", "öl").SequenceEqual([(0, 2), (3, 2), (6, 2)]));
        failures += Check("Suche: Treffer überlappen nicht", MainWindow.FindAll("aaaa", "aa").SequenceEqual([(0, 2), (2, 2)]));
        failures += Check("Suche: leerer Begriff findet nichts", MainWindow.FindAll("abc", "").Length == 0 && MainWindow.FindAll("", "a").Length == 0);

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

            // Hintergrundarbeit (Text für die Suche) läuft erst nach allem anderen, auch wenn sie früher kam.
            var order = new List<string>();
            using var hold = new ManualResetEventSlim();
            var blocker = renderer.Invoke(_ => hold.Wait(TimeSpan.FromSeconds(5)));
            var idle = renderer.Invoke(_ => { lock (order) order.Add("Hintergrund"); return 0; }, whenIdle: true);
            var urgent = renderer.Invoke(_ => { lock (order) order.Add("Aufgabe"); return 0; });
            hold.Set();
            failures += Check("Hintergrundarbeit nach der Aufgabe",
                Task.WaitAll([blocker, idle, urgent], TimeSpan.FromSeconds(5)) && order.SequenceEqual(["Aufgabe", "Hintergrund"]));
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

    static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    /// <summary>
    /// Anmerkungen (Stufe 3) auf der Vier-Seiten-PDF (Breiten 100–400, Höhe 500, „Seite …“ in 12 pt bei x 10, y 250):
    /// anlegen, im Bild sehen, speichern, wieder finden, ändern, löschen; dazu die Lage auf gedrehten Seiten und in
    /// gedrehter Ansicht.
    /// </summary>
    static int AnnotationChecks(string twoPagesWithOutline, string dir)
    {
        var failures = 0;
        var four = Path.Combine(dir, "vier Seiten.pdf");
        var saved = Path.Combine(dir, "mit Anmerkungen.pdf");
        using (var doc = PdfDocument.Open(four))
        {
            var map = AnnotationEdit.MapOf(doc, 0, 0);
            // PDFium rechnet über ein Ganzzahlraster (1e6) und float: auf ein Tausendstel genau.
            static bool Close(Point a, Point b) => Math.Abs(a.X - b.X) < 1e-3 && Math.Abs(a.Y - b.Y) < 1e-3;
            failures += Check("Ansicht → Seite: oben links ist (0, 500), rechts x+, unten y−",
                Close(map.At(new Point(0, 0)), new Point(0, 500)) && Close((Point)map.Right, new Point(1, 0)) && Close((Point)map.Down, new Point(0, -1)));

            // Textmarker über „Seite Eins“: Zeichen unter der Maus, eine Zeile, gelb im Bild.
            var boxes = doc.CharBoxes(0);
            var first = MainWindow.CharAt(boxes, new Point(12 / 100.0, (500 - 254) / 500.0), new Size(100, 500));
            var last = MainWindow.CharAt(boxes, new Point(0.99, (500 - 254) / 500.0), new Size(100, 500));
            failures += Check("Textmarker: Zeichen unter der Maus", first == 0 && last > 3);
            failures += Check("Textmarker: eine Zeile", MainWindow.LineRects(boxes, first, last).Count == 1);

            // Auswahl mit der Maus: der Druck muss auf einem Zeichenkasten liegen, sonst schiebt der Zug die Ansicht.
            failures += Check("Auswahl: Zeichen unter dem Druck", MainWindow.CharUnder(boxes, new Point(12 / 100.0, (500 - 254) / 500.0)) == 0);
            failures += Check("Auswahl: neben dem Text keines", MainWindow.CharUnder(boxes, new Point(0.99, 0.95)) < 0);
            failures += Check("Auswahl: Text der markierten Zeichen", doc.CharText(0, first, last - first + 1) == "Seite Eins");
            failures += Check("Auswahl: Nummern außerhalb der Seite werden begrenzt", doc.CharText(0, -5, 1000) == doc.PageText(0));

            new AddHighlight(0, first, last - first + 1).Apply(doc);
            var marked = doc.Annotations(0).Single();
            failures += Check("Textmarker angelegt, um den Text herum", marked.Subtype == 9 && marked.Area.Left < 0.12 && marked.Area.Top < 0.5 && marked.Area.Bottom > 0.49);
            failures += Check("Textmarker gelb im Bild", CountPixels(doc.Render(0, 100, 500, 72, 72, 0), marked.Area, IsYellow) > 20);

            // Freihand quer über Seite 2 (200 × 500), 4 pt breit, rot.
            new AddInk(1, 0, [[new Point(0.1, 0.5), new Point(0.9, 0.5)]], AnnotationEdit.InkColor, 4).Apply(doc);
            failures += Check("Freihand rot im Bild", IsRed(RgbAt(doc.Render(1, 200, 500, 72, 72, 0), 100, 250)));

            // Notiz auf Seite 3, Text ändern.
            new AddNote(2, 0, new Point(0.1, 0.1), "Hallo Notiz").Apply(doc);
            failures += Check("Notiz mit Text", doc.Annotations(2).SingleOrDefault() is { Subtype: 1, Contents: "Hallo Notiz" });
            new SetNoteText(2, 0, "Geändert").Apply(doc);
            failures += Check("Notiz geändert", doc.Annotations(2).Single().Contents == "Geändert");

            // Text auf Seite 4 (400 × 500), waagrecht in der Ansicht und dunkel im Bild.
            new AddText(3, 0, new Point(0.1, 0.1), "Grüße", 20).Apply(doc);
            var text = doc.Annotations(3).Single();
            failures += Check("Text als Stempel samt /Contents, links oben",
                text is { Subtype: 13, Contents: "Grüße" } && Math.Abs(text.Area.Left - 0.1) < 0.02 && Math.Abs(text.Area.Top - 0.1) < 0.02);
            failures += Check("Text waagrecht", text.Area.Width * 400 > 2 * text.Area.Height * 500);
            failures += Check("Text dunkel im Bild", CountPixels(doc.Render(3, 400, 500, 72, 72, 0), text.Area, IsDark) > 20);

            // Bild (Unterschrift aus Datei): 2 × 1 rote Pixel in einen Kasten unten auf Seite 1.
            new AddImage(0, 0, new Rect(0.2, 0.8, 0.6, 0.1), [0, 0, 255, 255, 0, 0, 255, 255], 2, 1).Apply(doc);
            failures += Check("Bild im Kasten", IsRed(RgbAt(doc.Render(0, 100, 500, 72, 72, 0), 50, 425)));
            doc.SaveAs(saved);
        }
        using (var reopened = PdfDocument.Open(saved))
        {
            failures += Check("Speichern behält die Anmerkungen",
                reopened.Annotations(0).Count == 2 && reopened.Annotations(1).Count == 1 && reopened.Annotations(2).Count == 1 && reopened.Annotations(3).Count == 1);
            new RemoveAnnotation(2, 0).Apply(reopened);
            failures += Check("Anmerkung gelöscht", reopened.Annotations(2).Count == 0);
        }
        // Erscheinung (/AP) steht in der Datei, auch für Seiten, die nie gerendert wurden: die Freihand-Linie auf Seite 3.
        using (var doc = PdfDocument.Open(four))
        {
            new AddInk(2, 0, [[new Point(0.1, 0.5), new Point(0.9, 0.5)]], AnnotationEdit.InkColor, 2).Apply(doc);
            var unrendered = Path.Combine(dir, "ohne Rendern.pdf");
            doc.SaveAs(unrendered);
            failures += Check("Freihand hat /AP ohne vorheriges Rendern", Regex.IsMatch(Encoding.Latin1.GetString(File.ReadAllBytes(unrendered)), @"/AP\b"));
        }
        // Gedreht: auf der mit /Rotate 90 gedrehten Seite 2 der Minimal-PDF steht Text waagrecht in der Anzeige; in einer mit R
        // gedrehten Ansicht (Turns 1) steht er in der Anzeige senkrecht, weil er in der Ansicht waagrecht steht — und oben links
        // der Ansicht ist unten links der Seite.
        using (var doc = PdfDocument.Open(twoPagesWithOutline))
        {
            new AddText(1, 0, new Point(0.1, 0.1), "Waagrecht", 20).Apply(doc);
            var turnedPage = doc.Annotations(1).Single();
            failures += Check("Text auf /Rotate-90-Seite waagrecht", turnedPage.Area.Width * 842 > 2 * turnedPage.Area.Height * 595);
        }
        using (var doc = PdfDocument.Open(four))
        {
            new AddText(3, 1, new Point(0.1, 0.1), "Gedreht", 20).Apply(doc);
            var turnedView = doc.Annotations(3).Single();
            failures += Check("Text in gedrehter Ansicht: senkrecht, unten links",
                turnedView.Area.Height * 500 > 2 * turnedView.Area.Width * 400 && turnedView.Area.Left < 0.2 && turnedView.Area.Bottom > 0.8);
        }

        failures += Check("Text-Stempel: Leerzeile ist nur Abstand", EditError(four, doc => new AddText(0, 0, new Point(0.1, 0.1), "Eins\n\nDrei", 12).Apply(doc)) == 0);
        failures += Check("Text-Stempel: fehlende Zeichen erkannt, Umlaute und € nicht",
            PdfDocument.MissingInStampFont("Grüße ✓ Łukasz € „x“") == "✓Ł" && PdfDocument.MissingInStampFont("Zeile\r\nZwei") == "");

        // Unterschrift: Striche auf ihren Rahmen bezogen; Bild ohne hellen Hintergrund, auf die Schrift zugeschnitten.
        var signature = Signatures.Normalize([[new Point(10, 20), new Point(110, 70)], [new Point(60, 45)]]);
        failures += Check("Unterschrift: Rahmen 0–1, Seitenverhältnis 2", signature is { Aspect: 2 }
            && signature.ToPoints()[0].SequenceEqual([new Point(0, 0), new Point(1, 1)]) && signature.ToPoints()[1].SequenceEqual([new Point(0.5, 0.5)]));
        failures += Check("Unterschrift: ohne Ausdehnung keine", Signatures.Normalize([[new Point(5, 5)]]) is null);
        byte[] white = [255, 255, 255, 255], ink = [120, 40, 20, 255];
        var (cleaned, width, height) = Signatures.Clean([.. white, .. white, .. white, .. ink, .. white, .. white], 3, 2);
        failures += Check("Unterschrift aus Bild: weiß wird durchsichtig, zugeschnitten", width == 1 && height == 1 && cleaned.SequenceEqual(ink));
        return failures;
    }

    /// <summary>
    /// Formular (Stufe 4): Textfeld „Name“, Kästchen „Ja“ (Erscheinung „Yes“ = schwarzes Quadrat), Auswahlfeld „Farbe“
    /// mit Rot/Grün/Blau. Ausfüllen über die FORM_-Funktionen, im Bild sehen, speichern, wieder lesen.
    /// </summary>
    static int FormChecks(string dir)
    {
        var failures = 0;
        var path = Path.Combine(dir, "Formular.pdf");
        File.WriteAllBytes(path, FormPdf());
        var saved = Path.Combine(dir, "Formular ausgefüllt.pdf");
        using (var doc = PdfDocument.Open(path))
        {
            var fields = doc.FormFields(0);
            failures += Check("Formular: AcroForm mit vier Feldern, das versteckte fehlt", doc.FormType == 1 && fields.Count == 4
                && fields[0] is { Type: 6, Name: "Name", Value: "" } && fields[1] is { Type: 2, Name: "Ja", Checked: false }
                && fields[2] is { Type: 4, Name: "Farbe", Selected: 0 } && fields[2].Options.SequenceEqual(["Rot", "Grün", "Blau"])
                && fields[3] is { Type: 1, Name: "Drucken", Fillable: false });
            var empty = doc.Render(0, 400, 400, 72, 72, 0, RenderPurpose.Screen);
            failures += Check("Formular: Felder hellblau hinterlegt nur auf dem Bildschirm",
                IsFieldBlue(RgbAt(empty, 370, 85)) && RgbAt(doc.Render(0, 400, 400, 72, 72, 0), 370, 85) == 0xFFFFFF);
            // Schaltfläche ohne Druck-Flag (unten rechts, schwarz): auf dem Bildschirm und in der Kopie da, im Druck nicht.
            failures += Check("Formular: Knopf ohne Druck-Flag fehlt nur im Druck",
                IsDark(RgbAt(empty, 340, 360)) && IsDark(RgbAt(doc.Render(0, 400, 400, 72, 72, 0), 340, 360))
                && RgbAt(doc.Render(0, 400, 400, 72, 72, 0, RenderPurpose.Print), 340, 360) == 0xFFFFFF
                && IsDark(RgbAt(doc.Render(0, 400, 400, 72, 72, 0), 340, 360))); // Flags danach wieder wie vorher

            new SetFieldText(0, 0, "Erika Musterfrau").Apply(doc);
            new ClickField(0, 1).Apply(doc);
            new SetFieldChoice(0, 2, 2).Apply(doc);
            fields = doc.FormFields(0);
            failures += Check("Formular: Werte übernommen", fields[0].Value == "Erika Musterfrau" && fields[1].Checked && fields[2] is { Selected: 2, Value: "Blau" });
            var filled = doc.Render(0, 400, 400, 72, 72, 0);
            failures += Check("Formular: Text und Haken im Bild", CountPixels(filled, fields[0].Area, IsDark) > 20 && IsDark(RgbAt(filled, 30, 140)));
            new SetFieldText(0, 0, "").Apply(doc);
            failures += Check("Formular: leerer Text leert das Feld", doc.FormFields(0)[0].Value == "");
            new SetFieldText(0, 0, "Grüße aus Köln").Apply(doc);
            // „Name“ steht auch auf Seite 2 (zweites Widget desselben Felds): Wert und Erscheinung ziehen mit.
            failures += Check("Formular: gleiches Feld auf Seite 2 hat den Wert", doc.FormFields(1).SingleOrDefault()?.Value == "Grüße aus Köln");
            failures += Check("Formular: gleiches Feld auf Seite 2 zeigt ihn", CountPixels(doc.Render(1, 400, 400, 72, 72, 0), doc.FormFields(1)[0].Area, IsDark) > 20);
            doc.SaveAs(saved);
        }
        using (var reopened = PdfDocument.Open(saved))
        {
            var fields = reopened.FormFields(0);
            failures += Check("Formular: Speichern behält die Werte",
                fields[0].Value == "Grüße aus Köln" && fields[1].Checked && fields[2].Value == "Blau");
            failures += Check("Formular: gespeichert zeigt auch Seite 2 den Wert",
                CountPixels(reopened.Render(1, 400, 400, 72, 72, 0), reopened.FormFields(1)[0].Area, IsDark) > 20);
        }
        // Wie eben, aber Seite 2 war nie geladen: steht die neue Erscheinung trotzdem in der Datei? Andere Programme zeigen
        // die Erscheinung, nicht den Wert (Review 2026-09-18).
        var untouched = Path.Combine(dir, "Formular Seite 2 nie gesehen.pdf");
        using (var doc = PdfDocument.Open(path))
        {
            new SetFieldText(0, 0, "Nur Seite eins").Apply(doc);
            doc.SaveAs(untouched);
        }
        using (var reopened = PdfDocument.Open(untouched))
        {
            var appearance = reopened.AppearanceOf(1, 0);
            failures += Check("Formular: Erscheinung auf nie geladener Seite 2 erneuert", appearance.Contains("Tj"));
        }
        return failures;
    }

    static bool IsFieldBlue(int rgb) => (rgb & 255) > 230 && (rgb >> 16 & 255) < 250 && (rgb >> 16 & 255) > 150;

    /// <summary>
    /// Zwei Seiten 400 × 400 mit AcroForm. Seite 1: Textfeld „Name“ (20–380 × 300–330), Kästchen (20–40 × 250–270), Auswahl
    /// (20–200 × 190–215), Schaltfläche „Drucken“ ohne Druck-Flag (300–380 × 20–60, schwarz), ein verstecktes Textfeld.
    /// Seite 2: zweites Widget von „Name“ an derselben Stelle.
    /// </summary>
    static byte[] FormPdf()
    {
        const string content = "BT /F1 12 Tf 20 360 Td (Formular) Tj ET";
        const string check = "0 g 3 3 14 14 re f";
        const string button = "0 g 0 0 80 40 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 12 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R /Annots [7 0 R 8 0 R 9 0 R 14 0 R 17 0 R] /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Fields [15 0 R 8 0 R 9 0 R 14 0 R 17 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 15 0 R /Rect [20 300 380 330] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Ja) /Rect [20 250 40 270] /P 3 0 R /V /Off /AS /Off /F 4 /AP << /N << /Yes 10 0 R /Off 11 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (Farbe) /Opt [(Rot) (Gr\u00FCn) (Blau)] /V (Rot) /Rect [20 190 200 215] /P 3 0 R /DA (/Helv 12 Tf 0 g) /F 4 >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length {check.Length} >>\nstream\n{check}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Annots [13 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 15 0 R /Rect [20 300 380 330] /P 12 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (Drucken) /Rect [300 20 380 60] /P 3 0 R /F 0 /AP << /N 16 0 R >> >>",
            "<< /FT /Tx /T (Name) /DA (/Helv 14 Tf 0 g) /Kids [7 0 R 13 0 R] >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 80 40] /Length {button.Length} >>\nstream\n{button}\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Versteckt) /Rect [20 100 380 130] /P 3 0 R /F 6 >>",
        ];
        return Pdf(objects);
    }

    static bool IsYellow(int rgb) => (rgb >> 16 & 255) > 200 && (rgb >> 8 & 255) > 170 && (rgb & 255) < 120;
    static bool IsRed(int rgb) => (rgb >> 16 & 255) > 150 && (rgb >> 8 & 255) < 110 && (rgb & 255) < 110;
    static bool IsDark(int rgb) => (rgb >> 16 & 255) < 90 && (rgb >> 8 & 255) < 90 && (rgb & 255) < 90;

    /// <summary>Pixel im Bereich (Anteile der Seite), auf die test passt.</summary>
    static int CountPixels(BitmapSource bitmap, Rect area, Func<int, bool> test)
    {
        var count = 0;
        for (var y = (int)(area.Top * bitmap.PixelHeight); y < Math.Min(bitmap.PixelHeight, (int)(area.Bottom * bitmap.PixelHeight)); y++)
            for (var x = (int)(area.Left * bitmap.PixelWidth); x < Math.Min(bitmap.PixelWidth, (int)(area.Right * bitmap.PixelWidth)); x++)
                if (test(RgbAt(bitmap, x, y))) count++;
        return count;
    }

    /// <summary>
    /// Lage von Suchtreffern und Links in Anteilen der Seite, auf Seite 2 (/Rotate 90 im PDF) mitgedreht. „Fletta Test“
    /// steht bei x = 110, Grundlinie y = 780 (von unten), 18 pt; die Links auf Seite 1 siehe MinimalPdf.
    /// </summary>
    static int SearchAndLinkChecks(PdfDocument doc)
    {
        var failures = 0;
        var at = MainWindow.FindAll(doc.PageText(0), "fletta test");
        var upright = at.Length == 1 ? doc.TextRects(0, at)[0] : Array.Empty<Rect>();
        failures += Check("Suche: Treffer oben links auf Seite 1", upright.Length > 0
            && upright[0].Left is > 0.17 and < 0.2 && upright[0].Top is > 0.02 and < 0.08 && upright[0].Bottom is > 0.05 and < 0.1);
        var turnedAt = MainWindow.FindAll(doc.PageText(1), "fletta test");
        var turned = turnedAt.Length == 1 ? doc.TextRects(1, turnedAt)[0] : Array.Empty<Rect>();
        failures += Check("Suche: auf der gedrehten Seite 2 oben rechts", turned.Length > 0 && turned[0].Left > 0.88 && turned[0].Top is > 0.17 and < 0.2);

        var links = doc.Links(0);
        failures += Check("Link mit Adresse samt Lage", links.Any(link => link.Uri == "https://example.com/fletta" && link.Page == -1
            && Math.Abs(link.Area.Left - 100 / 595.0) < 0.005 && Math.Abs(link.Area.Top - (842 - 150) / 842.0) < 0.005
            && Math.Abs(link.Area.Width - 100 / 595.0) < 0.005));
        failures += Check("Link auf Seite 2", links.Any(link => link.Page == 1 && Math.Abs(link.Area.Left - 300 / 595.0) < 0.005));
        failures += Check("Adresse im Text als Link", links.Any(link => link.Page == -1 && link.Uri?.Contains("fletta.example/hilfe") == true));
        failures += Check("Seite ohne Link-Annotationen: nur die Adresse aus dem Text", doc.Links(1).All(link => link.Uri?.Contains("fletta.example") == true));
        return failures;
    }

    /// <summary>
    /// Passwortgeschützte PDF (RC4, 40 Bit, Revision 2): Öffnen ohne und mit falschem Passwort scheitert mit PasswordError,
    /// mit richtigem geht es — das Passwort hat ein Umlaut, Fletta übergibt es als UTF-8, verschlüsselt ist mit Latin-1.
    /// Speichern behält den Schutz (sonst läge die Datei nach Strg+S offen da).
    /// </summary>
    static int PasswordChecks(string dir)
    {
        const string secret = "geheim-ä";
        var failures = 0;
        var locked = Path.Combine(dir, "geschützt.pdf");
        File.WriteAllBytes(locked, EncryptedPdf(secret));
        failures += Check("Passwort: ohne abgelehnt", PasswordOpenError(locked, null) == PdfException.PasswordError);
        failures += Check("Passwort: falsches abgelehnt", PasswordOpenError(locked, "falsch") == PdfException.PasswordError);
        using (var doc = PdfDocument.Open(locked, secret))
        {
            failures += Check("Passwort: richtiges öffnet, Text lesbar", doc.PageText(0).Contains("Geheim Test"));
            var saved = Path.Combine(dir, "geschützt gespeichert.pdf");
            doc.Rotate(0, 1);
            doc.SaveAs(saved);
            failures += Check("Passwort: Speichern behält den Schutz", PasswordOpenError(saved, null) == PdfException.PasswordError);
            using var reopened = PdfDocument.Open(saved, secret);
            failures += Check("Passwort: gespeicherte Datei mit Passwort lesbar und gedreht",
                reopened.PageText(0).Contains("Geheim Test") && reopened.PageSize(0) == new Size(842, 595));
        }
        var renderer = new PageRenderer(locked, Dispatcher.CurrentDispatcher, _ => { }, secret);
        using (renderer)
            failures += Check("Passwort: Render-Thread öffnet mit Passwort", renderer.Opened.Wait(TimeSpan.FromSeconds(5)) && renderer.Opened.Result.Count == 1);
        renderer.Closed.Wait(TimeSpan.FromSeconds(5));
        return failures;
    }

    static uint PasswordOpenError(string path, string? password)
    {
        try { PdfDocument.Open(path, password).Dispose(); return 0; }
        catch (PdfException e) { return e.Code; }
    }

    /// <summary>
    /// Eine Seite A4 mit „Geheim Test“, verschlüsselt nach dem Standard-Sicherheitsverfahren Revision 2 (PDF 1.7,
    /// Algorithmen 1–4): 40-Bit-RC4, Schlüssel aus MD5 über Passwort, O-Wert, Rechte und Datei-ID; jeder Stream mit
    /// eigenem Schlüssel aus Objektnummer und Generation. Eigentümer- gleich Benutzerpasswort.
    /// </summary>
    static byte[] EncryptedPdf(string password)
    {
        byte[] padding = [0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
                          0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A];
        byte[] padded = [.. Encoding.Latin1.GetBytes(password).Concat(padding).Take(32)];
        byte[] id = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
        const int permissions = -4; // alles erlaubt
        var owner = Rc4(MD5.HashData(padded)[..5], padded);
        var key = MD5.HashData([.. padded, .. owner, .. BitConverter.GetBytes(permissions), .. id])[..5];
        var user = Rc4(key, padding);
        static string Hex(byte[] bytes) => $"<{Convert.ToHexString(bytes)}>";

        const string content = "BT /F1 18 Tf 110 780 Td (Geheim Test) Tj ET";
        var objectKey = MD5.HashData([.. key, 4, 0, 0, 0, 0])[..10]; // Objekt 4, Generation 0
        var encrypted = Encoding.Latin1.GetString(Rc4(objectKey, Encoding.ASCII.GetBytes(content)));
        const string resources = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] {resources} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{encrypted}\nendstream",
            $"<< /Filter /Standard /V 1 /R 2 /O {Hex(owner)} /U {Hex(user)} /P {permissions} >>",
        ];
        return Pdf(objects, $"/Encrypt 5 0 R /ID [{Hex(id)} {Hex(id)}]");
    }

    static byte[] Rc4(byte[] key, byte[] data)
    {
        var state = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }
        var result = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255;
            j = (j + state[i]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
            result[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 255]);
        }
        return result;
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
    /// <summary>
    /// Seiten bearbeiten und speichern. Vier Seiten mit Breiten 100–400 Punkt machen jede Seite an ihrer
    /// Größe erkennbar; jede trägt ihren Namen als Text, damit sich prüfen lässt, was in der Datei steht.
    /// </summary>
    static int EditChecks(string twoPagesWithOutline, string dir)
    {
        var failures = 0;
        var four = Path.Combine(dir, "vier Seiten.pdf");
        File.WriteAllBytes(four, FourPagePdf());
        double[] Widths(PdfDocument doc) => [.. Enumerable.Range(0, doc.PageCount).Select(page => doc.PageSize(page).Width)];

        using (var doc = PdfDocument.Open(four))
        {
            var turns = new[] { 0, 1, 2, 3 };
            var delete = new DeletePages([1, 3]);
            delete.Apply(doc);
            failures += Check("Löschen: Seiten 2 und 4 weg", Widths(doc).SequenceEqual([100.0, 300.0]));
            failures += Check("Löschen: Drehungen folgen", delete.Remap(turns, doc.PageCount, 0).SequenceEqual([0, 2]));
        }
        using (var doc = PdfDocument.Open(four))
        {
            var move = MovePages.ToGap([2, 3], 0); // 3 und 4 an den Anfang
            move.Apply(doc);
            failures += Check("Verschieben an den Anfang", Widths(doc).SequenceEqual([300.0, 400.0, 100.0, 200.0]));
            failures += Check("Verschieben: Drehungen folgen", move.Remap(new[] { 0, 1, 2, 3 }, 4, -1).SequenceEqual([2, 3, 0, 1]));
            var back = MovePages.ToGap([0], 3); // Seite (jetzt 300) vor den vierten Eintrag
            back.Apply(doc);
            failures += Check("Verschieben in eine Lücke weiter hinten", Widths(doc).SequenceEqual([400.0, 100.0, 300.0, 200.0]));
            failures += Check("Verschieben an dieselbe Stelle ändert nichts", !MovePages.ToGap([1, 2], 1).ChangesOrder(4) && !MovePages.ToGap([1, 2], 3).ChangesOrder(4));
        }
        using (var doc = PdfDocument.Open(four))
        {
            var insert = new InsertPages(twoPagesWithOutline, 1);
            insert.Apply(doc);
            failures += Check("Einfügen: zwei Seiten vor Seite 2", Widths(doc).SequenceEqual([100.0, 595.0, 842.0, 200.0, 300.0, 400.0]));
            failures += Check("Einfügen: Drehungen folgen, neue ungedreht", insert.Remap(new[] { 1, 2, 3, 0 }, doc.PageCount, 0).SequenceEqual([1, 0, 0, 2, 3, 0]));

            doc.Rotate(0, 1);
            failures += Check("Drehen tauscht die Kanten", doc.PageSize(0) == new Size(500, 100));
            doc.DeletePages([5]);
            var saved = Path.Combine(dir, "gespeichert.pdf");
            doc.SaveAs(saved);
            using var reopened = PdfDocument.Open(saved);
            failures += Check("Speichern: Seiten, Reihenfolge und Drehung bleiben",
                Widths(reopened).SequenceEqual([500.0, 595.0, 842.0, 200.0, 300.0]));
            // Nicht inkrementell geschrieben: eine gelöschte Seite darf nicht mehr in der Datei stehen.
            // PDFium packt die Inhalte beim Speichern (FlateDecode), gesucht wird also im Entpackten.
            var content = StreamText(saved);
            failures += Check("Speichern: gelöschte Seite nicht mehr in der Datei", !content.Contains("Seite Vier"));
            failures += Check("Speichern: übrige Seiten stehen noch drin", content.Contains("Seite Drei") && content.Contains("Seite Eins"));

            var export = Path.Combine(dir, "Auszug.pdf");
            doc.ExportPages([3, 4], [1, 0], export);
            using var extract = PdfDocument.Open(export);
            failures += Check("In neue PDF: zwei Seiten, erste wie in der Ansicht gedreht",
                extract.PageCount == 2 && extract.PageSize(0) == new Size(500, 200) && extract.PageSize(1).Width == 300);
            failures += Check("In neue PDF: Quelle unverändert", doc.PageCount == 5);
        }
        using (var doc = PdfDocument.Open(four))
        {
            // Offene Seiten (PdfDocument.KeepPagesOpen): mehrfach gerendert keine Doppelten, nach oben begrenzt, und nach
            // jeder Änderung lebt keine Seite unter ihrem alten Index weiter — dafür vorher genau diese Indizes öffnen.
            for (var round = 0; round < 3; round++) doc.Render(2, 10, 10, 72, 72, 0);
            failures += Check("Seitenvorrat: jede Seite nur einmal offen", doc.OpenPageCount == 1);
            doc.PageText(0); doc.PageText(1);
            doc.MovePages([2], 0); // Drei Eins Zwei Vier
            failures += Check("Seitenvorrat nach Verschieben", doc.PageText(0).Contains("Seite Drei") && doc.PageText(1).Contains("Seite Eins"));
            doc.DeletePages([0]); // Eins Zwei Vier
            failures += Check("Seitenvorrat nach Löschen", doc.PageText(0).Contains("Seite Eins") && doc.PageText(1).Contains("Seite Zwei"));
            doc.InsertFrom(four, 1); // Eins [Eins Zwei Drei Vier] Zwei Vier
            failures += Check("Seitenvorrat nach Einfügen", doc.PageText(1).Contains("Seite Eins") && doc.PageText(0).Contains("Seite Eins") && doc.PageCount == 7);
            doc.Rotate(1, 1);
            failures += Check("Seitenvorrat nach Drehen leer", doc.OpenPageCount == 0);
            doc.ReleasePages();
            for (var copy = 0; copy < 5; copy++) doc.InsertFrom(four, 0);
            for (var page = 0; page < doc.PageCount; page++) doc.Render(page, 10, 10, 72, 72, 0);
            failures += Check("Seitenvorrat: begrenzt", doc.PageCount == 27 && doc.OpenPageCount == PdfDocument.KeepPagesOpen);
            doc.ReleasePages();
            failures += Check("Seitenvorrat: im Leerlauf freigegeben", doc.OpenPageCount == 0);
        }
        using (var doc = PdfDocument.Open(twoPagesWithOutline))
        {
            doc.Rotate(0, 1);
            var saved = Path.Combine(dir, "mit Gliederung.pdf");
            doc.SaveAs(saved);
            using var reopened = PdfDocument.Open(saved);
            failures += Check("Speichern behält Gliederung und /Rotate der zweiten Seite",
                reopened.Outline().Count == 1 && reopened.PageSize(0) == new Size(842, 595) && reopened.PageSize(1) == new Size(842, 595));
            failures += Check("keine Signaturen im Test-PDF", doc.SignatureCount == 0);
        }
        // Lesezeichen „Kapitel Zwei“ zeigt auf Seite 2: rückt nach, wenn Seite 1 fällt, und zeigt ins Leere, wenn Seite 2 fällt.
        foreach (var (deleted, expected, name) in new[] { (0, 0, "rückt nach vorn"), (1, -1, "ohne Ziel") })
        {
            using var doc = PdfDocument.Open(twoPagesWithOutline);
            doc.DeletePages([deleted]);
            var saved = Path.Combine(dir, $"Gliederung ohne {deleted}.pdf");
            doc.SaveAs(saved);
            using var reopened = PdfDocument.Open(saved);
            var outline = reopened.Outline();
            failures += Check($"Lesezeichen nach Löschen: {name}", outline.Count == 1 && outline[0].Page == expected);
            // Der Link auf Seite 2 (MinimalPdf) folgt genauso; ist Seite 2 weg, ist er kein Link mehr (Review 2026-09-18).
            if (deleted == 1) failures += Check("Link auf gelöschte Seite fällt weg", doc.Links(0).All(link => link.Uri is not null) && doc.Links(0).Count > 0);
        }
        failures += Check("Einfügen einer kaputten Datei meldet FormatError", EditError(four, doc => doc.InsertFrom(Path.Combine(dir, "kaputt.pdf"), 0)) == PdfException.FormatError);
        failures += Check("Verschieben außerhalb meldet EditError", EditError(four, doc => doc.MovePages([0], 9)) == PdfException.EditError);
        using (var doc = PdfDocument.Open(four))
        {
            var empty = Path.Combine(dir, "leer.pdf");
            var threw = false;
            try { doc.ExportPages([], [], empty); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            failures += Check("In neue PDF ohne Seiten: abgelehnt, keine Datei", threw && !File.Exists(empty));
            // Speichern auf eine vorhandene Datei ersetzt sie ganz und lässt keine Temp-Datei liegen.
            var target = Path.Combine(dir, "Ziel.pdf");
            File.WriteAllText(target, "alt");
            doc.ExportPages([0], [0], target);
            failures += Check("In neue PDF ersetzt vorhandene Datei, ohne Temp-Rest",
                File.ReadAllBytes(target).AsSpan(0, 4).SequenceEqual("%PDF"u8) && Directory.GetFiles(dir, "*.tmp").Length == 0);
        }
        return failures;
    }

    /// <summary>Alle Streams der Datei entpackt (oder roh, wenn nicht gepackt) hintereinander.</summary>
    static string StreamText(string path)
    {
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var text = new StringBuilder();
        foreach (Match m in Regex.Matches(raw, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var bytes = Encoding.Latin1.GetBytes(m.Groups[1].Value);
            try
            {
                using var inflate = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var reader = new StreamReader(inflate, Encoding.Latin1);
                text.Append(reader.ReadToEnd());
            }
            catch (InvalidDataException) { text.Append(m.Groups[1].Value); } // ungepackt
        }
        return text.ToString();
    }

    static uint EditError(string path, Action<PdfDocument> edit)
    {
        using var doc = PdfDocument.Open(path);
        try { edit(doc); return 0; }
        catch (PdfException e) { return e.Code; }
    }

    static byte[] FourPagePdf()
    {
        string[] names = ["Eins", "Zwei", "Drei", "Vier"];
        const string resources = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(" ", names.Select((_, i) => $"{3 + 2 * i} 0 R"))}] /Count 4 >>" };
        for (var i = 0; i < names.Length; i++)
        {
            var content = $"BT /F1 12 Tf 10 250 Td (Seite {names[i]}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {100 * (i + 1)} 500] {resources} /Contents {4 + 2 * i} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        return Pdf(objects);
    }

    static byte[] MinimalPdf()
    {
        // Unten eine Adresse als reiner Text (PDFium erkennt sie als Link), außerhalb der Pixelproben.
        const string content = "0 0 1 rg 100 100 395 642 re f 1 0 0 rg 20 752 70 70 re f BT 0 g /F1 18 Tf 110 780 Td (Fletta Test) Tj ET "
                             + "BT 0 g /F1 10 Tf 300 60 Td (https://fletta.example/hilfe) Tj ET";
        const string resources = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] {resources} /Contents 4 0 R /Annots [8 0 R 9 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Rotate 90 {resources} /Contents 4 0 R >>",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            "<< /Title (Kapitel Zwei) /Parent 6 0 R /Dest [5 0 R /Fit] >>",
            // Unsichtbare Links (ohne Rahmen): einer auf eine Adresse, einer auf Seite 2.
            "<< /Type /Annot /Subtype /Link /Rect [100 100 200 150] /Border [0 0 0] /A << /S /URI /URI (https://example.com/fletta) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [300 100 400 150] /Border [0 0 0] /Dest [5 0 R /Fit] >>",
        ];
        return Pdf(objects);
    }

    /// <summary>
    /// Objekte 1 … n in dieser Reihenfolge, Objekt 1 ist der Katalog. Latin-1, damit verschlüsselte Streams Byte für
    /// Byte ankommen; trailerExtra steht zusätzlich im Trailer.
    /// </summary>
    static byte[] Pdf(IReadOnlyList<string> objects, string trailerExtra = "")
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length); // Latin-1: Zeichen = Bytes
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
