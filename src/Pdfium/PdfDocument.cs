using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fletta.Pdfium;

/// <summary>Ein geöffnetes PDF. PDFium ist nicht threadsicher: alle Aufrufe aus demselben Thread.</summary>
public sealed class PdfDocument : IDisposable
{
    const int BitmapFormatBgrx = 3;     // FPDFBitmap_BGRx, gleiche Byte-Folge wie PixelFormats.Bgr32
    const int RenderAnnotations = 0x01; // FPDF_ANNOT
    const int RenderForPrinting = 0x800; // FPDF_PRINTING
    const uint White = 0xFFFFFFFF;
    const uint ActionGoTo = 1;          // PDFACTION_GOTO
    const int MaxOutlineEntries = 10_000, MaxOutlineDepth = 32;
    const int MaxLinks = 2_000; // je Seite; ein kaputtes oder absichtlich aufgeblähtes PDF soll die Maus nicht lähmen
    const int FractionGrid = 1_000_000; // Raster für FPDF_PageToDevice/DeviceToPage: Anteile der Seite auf sechs Stellen
    const string StampFont = "Helvetica"; // Standardschrift: nichts einzubetten, WinAnsi deckt Umlaute und ß
    // WinAnsiEncoding der Standardschrift: druckbares ASCII, Latin-1 ab 0xA0 und diese Zeichen auf 0x80–0x9F.
    const string WinAnsiExtras = "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ";
    // PDFium zerlegt den Seiteninhalt erst beim ersten Rendern und behält ihn am offenen FPDF_PAGE: am
    // Suzuki-Handbuch 158 ms fürs erste, 32 ms fürs zweite Rendern derselben geladenen Seite, das Laden selbst
    // 1,5 ms (windows-pc, 2026-09-13). Jede offene Seite belegt dort aber rund 5 MB (Test-VM: 16 offene +74 MB,
    // 6 offene +32 MB). Zwei reichen: MainWindow stellt die Miniatur direkt hinter ihre Hauptseite.
    internal const int KeepPagesOpen = 2;

    // Einmal je Prozess; freigegeben wird beim Beenden vom Betriebssystem.
    static PdfDocument() => Native.FPDF_InitLibraryWithConfig(new Native.LibraryConfig { Version = 2 });

    nint handle;
    readonly List<(int Index, nint Page)> openPages = []; // zuletzt benutzte hinten

    PdfDocument(nint handle) => this.handle = handle;

    /// <summary>Für den Selbsttest: so viele Seiten hält das Dokument gerade offen.</summary>
    internal int OpenPageCount => openPages.Count;

    /// <summary>Ändert sich mit DeletePages und InsertFrom.</summary>
    public int PageCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            return Native.FPDF_GetPageCount(handle);
        }
    }

    /// <summary>Digitale Signaturen im Dokument; Speichern macht sie ungültig.</summary>
    public int SignatureCount => Math.Max(0, Native.FPDF_GetSignatureCount(handle));

    /// <summary>
    /// Liest nur Querverweistabelle und Seitenbaum, keine Seiteninhalte. Ohne oder mit falschem Passwort bei
    /// einer geschützten Datei PdfException mit PasswordError.
    /// </summary>
    public static PdfDocument Open(string path, string? password = null)
    {
        var handle = Native.FPDF_LoadDocument(path, password);
        return handle == 0 ? throw new PdfException(Native.FPDF_GetLastError()) : new PdfDocument(handle);
    }

    /// <summary>Seitengröße in PDF-Punkten (1/72 Zoll), ohne die Seite zu laden.</summary>
    public Size PageSize(int index)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        return Native.FPDF_GetPageSizeByIndexF(handle, index, out var size)
            ? new Size(size.Width, size.Height)
            : throw new PdfException(PdfException.PageError);
    }

    /// <summary>
    /// Rendert eine Seite auf weißem Grund in genau diese Pixelgröße. quarterTurns dreht zusätzlich
    /// zum /Rotate des PDFs im Uhrzeigersinn (0–3, der rotate-Wert von FPDF_RenderPageBitmap);
    /// die Pixelgröße ist dann die gedrehte.
    /// </summary>
    public BitmapSource Render(int index, int pixelWidth, int pixelHeight, double dpiX, double dpiY, int quarterTurns)
    {
        var page = LoadPage(index);
        var bitmap = new WriteableBitmap(pixelWidth, pixelHeight, dpiX, dpiY, PixelFormats.Bgr32, null);
        bitmap.Lock();
        try
        {
            // PDFium schreibt direkt in den Puffer von WPF, ohne Kopie.
            var target = Native.FPDFBitmap_CreateEx(pixelWidth, pixelHeight, BitmapFormatBgrx,
                                                    bitmap.BackBuffer, bitmap.BackBufferStride);
            if (target == 0) throw new PdfException(PdfException.PageError);
            Native.FPDFBitmap_FillRect(target, 0, 0, pixelWidth, pixelHeight, White);
            Native.FPDF_RenderPageBitmap(target, page, 0, 0, pixelWidth, pixelHeight, quarterTurns, RenderAnnotations);
            Native.FPDFBitmap_Destroy(target); // gibt nur die Hülle frei, der Puffer gehört WPF
            bitmap.AddDirtyRect(new Int32Rect(0, 0, pixelWidth, pixelHeight));
        }
        finally
        {
            bitmap.Unlock();
        }
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Text der Seite laut PDFium; leer bei Scans ohne Textschicht.</summary>
    public unsafe string PageText(int index)
    {
        var page = LoadPage(index);
        var text = Native.FPDFText_LoadPage(page);
        try
        {
            var count = text == 0 ? 0 : Native.FPDFText_CountChars(text);
            if (count <= 0) return "";
            var buffer = new ushort[count + 1];
            int written;
            fixed (ushort* result = buffer) written = Native.FPDFText_GetText(text, 0, count, result);
            return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, Math.Max(0, written - 1))));
        }
        finally
        {
            if (text != 0) Native.FPDFText_ClosePage(text);
        }
    }

    /// <summary>
    /// Wo die Zeichen [Start, Start + Length) auf der Seite stehen, je Bereich als Rechtecke in Anteilen der Seite
    /// (siehe ToFraction). Die Nummern sind Stellen im Text von PageText; PDFium zählt für die Lage in seiner Zeichenliste,
    /// daher umgerechnet (Review 2026-09-18).
    /// </summary>
    public Rect[][] TextRects(int index, IReadOnlyList<(int Start, int Length)> ranges)
    {
        var page = LoadPage(index);
        var text = Native.FPDFText_LoadPage(page);
        if (text == 0) return [.. ranges.Select(_ => Array.Empty<Rect>())];
        try
        {
            return [.. ranges.Select(range =>
            {
                var first = Native.FPDFText_GetCharIndexFromTextIndex(text, range.Start);
                var last = Native.FPDFText_GetCharIndexFromTextIndex(text, range.Start + range.Length - 1);
                if (first < 0 || last < first) return [];
                var rects = new Rect[Math.Max(0, Native.FPDFText_CountRects(text, first, last - first + 1))];
                for (var i = 0; i < rects.Length; i++)
                {
                    Native.FPDFText_GetRect(text, i, out var left, out var top, out var right, out var bottom);
                    rects[i] = ToFraction(page, left, top, right, bottom);
                }
                return rects;
            })];
        }
        finally
        {
            Native.FPDFText_ClosePage(text);
        }
    }

    /// <summary>
    /// Links der Seite: Link-Annotationen mit Ziel im Dokument oder Adresse, dazu Adressen, die nur als Text dastehen
    /// (PDFium erkennt „www.…“, „https://…“ und Mail-Adressen). Ohne erkennbares Ziel fällt ein Link weg.
    /// </summary>
    public unsafe IReadOnlyList<PageLink> Links(int index)
    {
        var page = LoadPage(index);
        var links = new List<PageLink>();
        for (var position = 0; links.Count < MaxLinks && Native.FPDFLink_Enumerate(page, ref position, out var link);)
        {
            if (!Native.FPDFLink_GetAnnotRect(link, out var rect)) continue;
            var area = ToFraction(page, rect.Left, rect.Top, rect.Right, rect.Bottom);
            var dest = Native.FPDFLink_GetDest(handle, link);
            var action = Native.FPDFLink_GetAction(link);
            var type = action == 0 ? 0 : Native.FPDFAction_GetType(action);
            if (dest == 0 && type == ActionGoTo) dest = Native.FPDFAction_GetDest(handle, action);
            if (dest != 0)
            {
                // Ziel −1: die Seite gibt es nicht mehr (in Fletta gelöscht) — dann ist es kein Link mehr.
                var target = Native.FPDFDest_GetDestPageIndex(handle, dest);
                if (target >= 0) links.Add(new PageLink(area, target, null));
            }
            else if (type == Native.ActionUri && ActionUri(action) is { Length: > 0 } uri) links.Add(new PageLink(area, -1, uri));
        }

        var text = Native.FPDFText_LoadPage(page);
        if (text == 0) return links;
        var web = Native.FPDFLink_LoadWebLinks(text);
        try
        {
            for (var i = 0; web != 0 && i < Native.FPDFLink_CountWebLinks(web) && links.Count < MaxLinks; i++)
            {
                var length = Native.FPDFLink_GetURL(web, i, null, 0);
                if (length <= 1) continue;
                var buffer = new ushort[length];
                fixed (ushort* target = buffer) Native.FPDFLink_GetURL(web, i, target, length);
                var url = new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, length - 1)));
                for (var r = 0; r < Native.FPDFLink_CountRects(web, i); r++)
                    if (Native.FPDFLink_GetRect(web, i, r, out var left, out var top, out var right, out var bottom))
                        links.Add(new PageLink(ToFraction(page, left, top, right, bottom), -1, url));
            }
        }
        finally
        {
            if (web != 0) Native.FPDFLink_CloseWebLinks(web);
            Native.FPDFText_ClosePage(text);
        }
        return links;
    }

    unsafe string ActionUri(nint action)
    {
        var length = Native.FPDFAction_GetURIPath(handle, action, null, 0);
        if (length <= 1) return "";
        var buffer = new byte[length];
        fixed (byte* target = buffer) Native.FPDFAction_GetURIPath(handle, action, target, length);
        return Encoding.UTF8.GetString(buffer, 0, (int)length - 1); // ASCII ist gültiges UTF-8
    }

    /// <summary>
    /// Seitenkoordinaten als Anteil der angezeigten Seite, 0–1 von oben links: mit Seitenrahmen und /Rotate des PDFs,
    /// ohne Flettas eigene Drehung (die rechnet PageGeometry.Turn obendrauf). PDFium rechnet dafür auf ein Raster.
    /// </summary>
    static Rect ToFraction(nint page, double left, double top, double right, double bottom)
    {
        const int Grid = FractionGrid;
        Native.FPDF_PageToDevice(page, 0, 0, Grid, Grid, 0, left, top, out var x1, out var y1);
        Native.FPDF_PageToDevice(page, 0, 0, Grid, Grid, 0, right, bottom, out var x2, out var y2);
        return new Rect(new Point((double)x1 / Grid, (double)y1 / Grid), new Point((double)x2 / Grid, (double)y2 / Grid));
    }

    /// <summary>
    /// Umkehrung von ToFraction als affine Abbildung: Seitenpunkt = Origin + Right · u + Down · v für Anteile (u, v) der
    /// angezeigten Seite. Rechnet Seitenrahmen und /Rotate ein; Flettas Drehung rechnet der Aufrufer (AnnotationEdit).
    /// </summary>
    public (Point Origin, Vector Right, Vector Down) DisplayToPage(int index)
    {
        var page = LoadPage(index);
        Point At(int x, int y)
        {
            Native.FPDF_DeviceToPage(page, 0, 0, FractionGrid, FractionGrid, 0, x, y, out var pageX, out var pageY);
            return new Point(pageX, pageY);
        }
        var origin = At(0, 0);
        return (origin, At(FractionGrid, 0) - origin, At(0, FractionGrid) - origin);
    }

    /// <summary>
    /// Kasten jedes Zeichens in voller Zeilenhöhe, in Anteilen der Seite, je Nummer der Zeichenliste (die zählt auch
    /// AddHighlight); Rect.Empty für Zeichen ohne Ausdehnung (von PDFium eingefügte Leerzeichen und Umbrüche).
    /// </summary>
    public Rect[] CharBoxes(int index)
    {
        var page = LoadPage(index);
        var text = Native.FPDFText_LoadPage(page);
        if (text == 0) return [];
        try
        {
            var boxes = new Rect[Math.Max(0, Native.FPDFText_CountChars(text))];
            for (var i = 0; i < boxes.Length; i++)
                boxes[i] = Native.FPDFText_GetLooseCharBox(text, i, out var box) && box.Right > box.Left
                    ? ToFraction(page, box.Left, box.Top, box.Right, box.Bottom)
                    : Rect.Empty;
            return boxes;
        }
        finally
        {
            Native.FPDFText_ClosePage(text);
        }
    }

    /// <summary>Anmerkungen der Seite außer Links, Popups und Formularfeldern — die hat Fletta anderswo oder gar nicht.</summary>
    public unsafe IReadOnlyList<PageAnnotation> Annotations(int index)
    {
        var page = LoadPage(index);
        var found = new List<PageAnnotation>();
        var count = Native.FPDFPage_GetAnnotCount(page);
        for (var i = 0; i < count && found.Count < MaxLinks; i++)
        {
            var annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == 0) continue;
            try
            {
                var subtype = Native.FPDFAnnot_GetSubtype(annot);
                if (subtype is Native.AnnotLink or Native.AnnotPopup or Native.AnnotWidget || !Native.FPDFAnnot_GetRect(annot, out var rect)) continue;
                found.Add(new PageAnnotation(i, subtype, ToFraction(page, rect.Left, rect.Top, rect.Right, rect.Bottom), AnnotationText(annot)));
            }
            finally
            {
                Native.FPDFPage_CloseAnnot(annot);
            }
        }
        return found;
    }

    static unsafe string AnnotationText(nint annot)
    {
        var length = Native.FPDFAnnot_GetStringValue(annot, "Contents", null, 0);
        if (length <= 2) return "";
        var buffer = new byte[length];
        fixed (byte* target = buffer) Native.FPDFAnnot_GetStringValue(annot, "Contents", target, length);
        return Encoding.Unicode.GetString(buffer, 0, (int)length - 2);
    }

    /// <summary>Textmarker über count Zeichen ab start (Nummern der Zeichenliste, wie CharBoxes), je Zeile ein Viereck.</summary>
    public void AddHighlight(int index, int start, int count, Color color)
    {
        var page = LoadPage(index);
        var text = Native.FPDFText_LoadPage(page);
        if (text == 0) throw new PdfException(PdfException.EditError);
        var quads = new List<Native.QuadPointsF>();
        try
        {
            for (var i = 0; i < Native.FPDFText_CountRects(text, start, count); i++)
                if (Native.FPDFText_GetRect(text, i, out var left, out var top, out var right, out var bottom))
                    quads.Add(new Native.QuadPointsF
                    {
                        X1 = (float)left, Y1 = (float)top, X2 = (float)right, Y2 = (float)top,
                        X3 = (float)left, Y3 = (float)bottom, X4 = (float)right, Y4 = (float)bottom,
                    });
        }
        finally
        {
            Native.FPDFText_ClosePage(text);
        }
        if (quads.Count == 0) throw new PdfException(PdfException.EditError);
        WithNewAnnotation(page, Native.AnnotHighlight, annot =>
        {
            Check(Native.FPDFAnnot_SetColor(annot, Native.ColorStroke, color.R, color.G, color.B, 255));
            foreach (var quad in quads) Check(Native.FPDFAnnot_AppendAttachmentPoints(annot, quad));
            SetRect(annot, quads.SelectMany(q => new[] { new Point(q.X1, q.Y1), new Point(q.X4, q.Y4) }), 0);
        });
    }

    /// <summary>Freihand: jeder Strich eine Punktfolge in Seitenkoordinaten; width in Punkt.</summary>
    public unsafe void AddInk(int index, IReadOnlyList<Point[]> strokes, Color color, float width)
    {
        if (strokes.Count == 0 || strokes.Any(stroke => stroke.Length == 0)) throw new PdfException(PdfException.EditError);
        WithNewAnnotation(LoadPage(index), Native.AnnotInk, annot =>
        {
            Check(Native.FPDFAnnot_SetColor(annot, Native.ColorStroke, color.R, color.G, color.B, 255));
            Check(Native.FPDFAnnot_SetBorder(annot, 0, 0, width));
            foreach (var stroke in strokes)
            {
                var points = stroke.Select(point => new Native.PointF { X = (float)point.X, Y = (float)point.Y }).ToArray();
                fixed (Native.PointF* first = points) Check(Native.FPDFAnnot_AddInkStroke(annot, first, (nuint)points.Length) >= 0);
            }
            SetRect(annot, strokes.SelectMany(stroke => stroke), width);
        });
    }

    /// <summary>Notiz (Zettel-Symbol) mit Text; at ist die linke obere Ecke des Symbols in Seitenkoordinaten.</summary>
    public unsafe void AddNote(int index, Point at, string text)
    {
        const float Icon = 20;
        WithNewAnnotation(LoadPage(index), Native.AnnotText, annot =>
        {
            Check(Native.FPDFAnnot_SetColor(annot, Native.ColorStroke, 255, 214, 0, 255));
            Check(Native.FPDFAnnot_SetRect(annot, new Native.RectF { Left = (float)at.X, Top = (float)at.Y, Right = (float)at.X + Icon, Bottom = (float)at.Y - Icon }));
            fixed (char* value = text) Check(Native.FPDFAnnot_SetStringValue(annot, "Contents", value));
            Check(Native.FPDFAnnot_SetFlags(annot, Native.AnnotFlagPrint | Native.AnnotFlagNoZoom | Native.AnnotFlagNoRotate));
        });
    }

    /// <summary>Text der Anmerkung mit Nummer annotIndex ändern (Notiz bearbeiten).</summary>
    public unsafe void SetAnnotationText(int index, int annotIndex, string text)
    {
        var annot = Native.FPDFPage_GetAnnot(LoadPage(index), annotIndex);
        if (annot == 0) throw new PdfException(PdfException.EditError);
        try
        {
            fixed (char* value = text) Check(Native.FPDFAnnot_SetStringValue(annot, "Contents", value));
        }
        finally
        {
            Native.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>
    /// Text als Stempel: je Zeile ein Textobjekt in Helvetica. origin ist die linke obere Ecke des Textes, right und
    /// down die Richtungen der Ansicht je Punkt in Seitenkoordinaten — so steht der Text in der Ansicht aufrecht, auch
    /// auf gedrehten Seiten.
    /// </summary>
    public unsafe void AddText(int index, Point origin, Vector right, Vector down, string text, float size, Color color)
    {
        var up = -down;
        var objects = new List<nint>();
        try
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var line = 0; line < lines.Length; line++)
            {
                if (lines[line].Length == 0) continue; // Leerzeile: nur Abstand; FPDFText_SetText lehnt leeren Text ab
                var textObject = Native.FPDFPageObj_NewTextObj(handle, StampFont, size);
                if (textObject == 0) throw new PdfException(PdfException.EditError);
                objects.Add(textObject);
                fixed (char* value = lines[line]) Check(Native.FPDFText_SetText(textObject, value));
                Check(Native.FPDFPageObj_SetFillColor(textObject, color.R, color.G, color.B, 255));
                var baseline = origin + down * (size * (0.8 + 1.25 * line)); // Oberlänge ≈ 0,8 der Größe, Zeilenabstand 1,25
                Native.FPDFPageObj_Transform(textObject, right.X, right.Y, up.X, up.Y, baseline.X, baseline.Y);
            }
        }
        catch
        {
            foreach (var textObject in objects) Native.FPDFPageObj_Destroy(textObject); // gehören noch niemandem
            throw;
        }
        AddStamp(index, objects, text); // der Text auch als /Contents: für Tooltip, Kommentarliste anderer Programme, Vorlesen
    }

    /// <summary>
    /// Bild als Stempel (Unterschrift aus Datei). bgra: Pixel mit Alpha, Zeile für Zeile; corner ist die linke untere
    /// Ecke des Bildes, width und height die Kanten als Vektoren in Seitenkoordinaten (height zeigt nach oben).
    /// </summary>
    public unsafe void AddImage(int index, Point corner, Vector width, Vector height, byte[] bgra, int pixelWidth, int pixelHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bgra.Length, pixelWidth * pixelHeight * 4);
        var image = Native.FPDFPageObj_NewImageObj(handle);
        if (image == 0) throw new PdfException(PdfException.EditError);
        try
        {
            var bitmap = Native.FPDFBitmap_Create(pixelWidth, pixelHeight, 1);
            if (bitmap == 0) throw new PdfException(PdfException.EditError);
            try
            {
                // In PDFiums eigenen Puffer kopiert: ob das Bildobjekt die Bitmap später noch liest, sagt die API nicht.
                var target = (byte*)Native.FPDFBitmap_GetBuffer(bitmap);
                var stride = Native.FPDFBitmap_GetStride(bitmap);
                for (var row = 0; row < pixelHeight; row++)
                    bgra.AsSpan(row * pixelWidth * 4, pixelWidth * 4).CopyTo(new Span<byte>(target + row * stride, pixelWidth * 4));
                Check(Native.FPDFImageObj_SetBitmap(0, 0, image, bitmap));
            }
            finally
            {
                Native.FPDFBitmap_Destroy(bitmap);
            }
            Check(Native.FPDFImageObj_SetMatrix(image, width.X, width.Y, height.X, height.Y, corner.X, corner.Y));
        }
        catch
        {
            Native.FPDFPageObj_Destroy(image);
            throw;
        }
        AddStamp(index, [image], "Unterschrift");
    }

    /// <summary>
    /// Stempel-Anmerkung um die Objekte herum. Erst eine leere Erscheinung (BBox = /Rect), dann die Objekte hinein —
    /// PDFium schreibt daraus den AP-Stream. Übernimmt die Objekte in jedem Fall: angehängte gehören der Anmerkung (und
    /// gehen mit ihr, falls sie wieder entfernt wird), die übrigen gibt es hier frei.
    /// </summary>
    unsafe void AddStamp(int index, IReadOnlyList<nint> objects, string contents)
    {
        var appended = 0;
        try
        {
            var corners = new List<Point>();
            foreach (var pageObject in objects)
            {
                Check(Native.FPDFPageObj_GetBounds(pageObject, out var left, out var bottom, out var right, out var top));
                corners.Add(new Point(left, bottom));
                corners.Add(new Point(right, top));
            }
            WithNewAnnotation(LoadPage(index), Native.AnnotStamp, annot =>
            {
                SetRect(annot, corners, 1);
                fixed (char* value = contents) Check(Native.FPDFAnnot_SetStringValue(annot, "Contents", value));
                // Keine Erscheinung vorab (FPDFAnnot_SetAP): die hätte keine eigenen /Resources, Schrift und Bild landeten in
                // denen der Seite, und andere Programme fänden sie nicht. AppendObject legt selbst eine leere samt
                // Ressourcen an (Review 2026-09-18).
                foreach (var pageObject in objects)
                {
                    Check(Native.FPDFAnnot_AppendObject(annot, pageObject));
                    appended++;
                }
            });
        }
        finally
        {
            foreach (var pageObject in objects.Skip(appended)) Native.FPDFPageObj_Destroy(pageObject);
        }
    }

    /// <summary>
    /// Zeichen, die die Standardschrift der Text-Stempel nicht hat — PDFium setzte dafür „ÿ“ (Review 2026-09-18).
    /// Zeilenumbrüche zählen nicht. Im Selbsttest geprüft.
    /// </summary>
    public static string MissingInStampFont(string text) =>
        new([.. text.Where(c => c is not ('\r' or '\n') && !(c is >= ' ' and <= '~' || c is >= '\u00A0' and <= '\u00FF' || WinAnsiExtras.Contains(c))).Distinct()]);

    /// <summary>Anmerkung löschen, samt ihrem Popup (sonst bliebe ein verwaister Notizzettel übrig).</summary>
    public void RemoveAnnotation(int index, int annotIndex)
    {
        var page = LoadPage(index);
        var annot = Native.FPDFPage_GetAnnot(page, annotIndex);
        if (annot == 0) throw new PdfException(PdfException.EditError);
        var popupIndex = -1;
        try
        {
            var popup = Native.FPDFAnnot_GetLinkedAnnot(annot, "Popup");
            if (popup != 0)
            {
                popupIndex = Native.FPDFPage_GetAnnotIndex(page, popup);
                Native.FPDFPage_CloseAnnot(popup);
            }
        }
        finally
        {
            Native.FPDFPage_CloseAnnot(annot);
        }
        foreach (var remove in new[] { annotIndex, popupIndex }.Where(i => i >= 0).OrderDescending())
            Check(Native.FPDFPage_RemoveAnnot(page, remove));
    }

    /// <summary>
    /// Neue Anmerkung, druckbar; scheitert fill, wird sie wieder entfernt — halb angelegt bleibt nichts stehen. Danach
    /// rendert PDFium die Seite einmal in 1 × 1 Pixel: erst dabei schreibt es die Erscheinung (/AP) von Textmarker,
    /// Freihand und Notiz. Ohne sie fehlte sie in der gespeicherten Datei, wenn die Seite vorher nie zu sehen war (etwa nach
    /// Strg+Z, das alle Schritte neu anwendet) — und manche Programme zeigen eine Anmerkung ohne /AP gar nicht.
    /// </summary>
    static void WithNewAnnotation(nint page, int subtype, Action<nint> fill)
    {
        var annot = Native.FPDFPage_CreateAnnot(page, subtype);
        if (annot == 0) throw new PdfException(PdfException.EditError);
        try
        {
            Check(Native.FPDFAnnot_SetFlags(annot, Native.AnnotFlagPrint));
            fill(annot);
        }
        catch
        {
            var at = Native.FPDFPage_GetAnnotIndex(page, annot);
            if (at >= 0) Native.FPDFPage_RemoveAnnot(page, at);
            throw;
        }
        finally
        {
            Native.FPDFPage_CloseAnnot(annot);
        }
        var bitmap = Native.FPDFBitmap_Create(1, 1, 0);
        if (bitmap == 0) return; // ohne /AP geht es auch, PDFium erzeugt sie beim nächsten Rendern
        Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, 1, 1, 0, RenderAnnotations);
        Native.FPDFBitmap_Destroy(bitmap);
    }

    static void SetRect(nint annot, IEnumerable<Point> points, float margin)
    {
        var list = points.ToList();
        Check(Native.FPDFAnnot_SetRect(annot, new Native.RectF
        {
            Left = (float)list.Min(point => point.X) - margin,
            Right = (float)list.Max(point => point.X) + margin,
            Bottom = (float)list.Min(point => point.Y) - margin,
            Top = (float)list.Max(point => point.Y) + margin,
        }));
    }

    static void Check(bool ok)
    {
        if (!ok) throw new PdfException(PdfException.EditError);
    }

    /// <summary>Druckt eine Seite auf ein Drucker-HDC; Koordinaten in Gerätepixeln, Drehung wie bei Render.</summary>
    public void Print(int index, nint hdc, int x, int y, int width, int height, int quarterTurns)
    {
        if (!Native.FPDF_RenderPage(hdc, LoadPage(index), x, y, width, height, quarterTurns, RenderAnnotations | RenderForPrinting))
            throw new PdfException(PdfException.PageError);
    }

    /// <summary>
    /// Lesezeichen in Lesereihenfolge samt Tiefe; ohne erkennbares Ziel ist Page −1. Kaputte PDFs
    /// können im Lesezeichenbaum Schleifen haben: jeder Eintrag wird nur einmal besucht, dazu Deckel
    /// für Anzahl und Tiefe.
    /// </summary>
    public IReadOnlyList<OutlineEntry> Outline()
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        var entries = new List<OutlineEntry>();
        var seen = new HashSet<nint>();
        void Walk(nint parent, int depth)
        {
            for (var item = Native.FPDFBookmark_GetFirstChild(handle, parent);
                 item != 0 && entries.Count < MaxOutlineEntries && seen.Add(item);
                 item = Native.FPDFBookmark_GetNextSibling(handle, item))
            {
                entries.Add(new OutlineEntry(BookmarkTitle(item), BookmarkPage(item), depth));
                if (depth < MaxOutlineDepth) Walk(item, depth + 1);
            }
        }
        Walk(0, 0);
        return entries;
    }

    static unsafe string BookmarkTitle(nint bookmark)
    {
        var length = Native.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (length <= 2) return "";
        var buffer = new byte[length];
        fixed (byte* target = buffer) Native.FPDFBookmark_GetTitle(bookmark, target, length);
        return Encoding.Unicode.GetString(buffer, 0, (int)length - 2); // ohne Terminator
    }

    /// <summary>Ziel direkt am Lesezeichen oder über eine GoTo-Aktion; sonst −1.</summary>
    int BookmarkPage(nint bookmark)
    {
        var dest = Native.FPDFBookmark_GetDest(handle, bookmark);
        if (dest == 0)
        {
            var action = Native.FPDFBookmark_GetAction(bookmark);
            if (action != 0 && Native.FPDFAction_GetType(action) == ActionGoTo) dest = Native.FPDFAction_GetDest(handle, action);
        }
        return dest == 0 ? -1 : Native.FPDFDest_GetDestPageIndex(handle, dest);
    }

    /// <summary>Dreht die Seite in der Datei (/Rotate) um quarterTurns Viertel im Uhrzeigersinn weiter.</summary>
    public void Rotate(int index, int quarterTurns)
    {
        ClosePages();
        RotatePage(handle, index, quarterTurns);
    }

    /// <summary>Löscht die Seiten; Reihenfolge der Angabe egal.</summary>
    public void DeletePages(IEnumerable<int> pages)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        ClosePages();
        foreach (var page in pages.Distinct().OrderDescending()) Native.FPDFPage_Delete(handle, page);
    }

    /// <summary>Setzt die Seiten in dieser Reihenfolge an destination — gezählt im Ergebnis, nach dem Herausnehmen.</summary>
    public unsafe void MovePages(int[] pages, int destination)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        ClosePages();
        fixed (int* indices = pages)
            if (!Native.FPDF_MovePages(handle, indices, (uint)pages.Length, destination))
                throw new PdfException(PdfException.EditError);
    }

    /// <summary>Fügt alle Seiten einer anderen PDF vor Seite at ein; gibt ihre Zahl zurück.</summary>
    public unsafe int InsertFrom(string path, int at)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        ClosePages();
        using var source = Open(path);
        var count = source.PageCount;
        if (!Native.FPDF_ImportPagesByIndex(handle, source.handle, null, 0, at)) throw new PdfException(PdfException.EditError);
        return count;
    }

    /// <summary>
    /// Schreibt das Dokument samt allen Änderungen vollständig neu nach path. Nicht inkrementell: sonst
    /// stünden gelöschte Seiten weiter lesbar in der Datei.
    /// </summary>
    public void SaveAs(string path)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        Save(handle, path);
    }

    /// <summary>Legt die Seiten als neue PDF an, jede um turns[i] Viertel gedreht wie in der Ansicht.</summary>
    public unsafe void ExportPages(int[] pages, int[] turns, string path)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        // fixed auf ein leeres Feld ergibt null, und null heißt für PDFium „alle Seiten“.
        ArgumentOutOfRangeException.ThrowIfZero(pages.Length);
        var target = Native.FPDF_CreateNewDocument();
        if (target == 0) throw new PdfException(PdfException.EditError);
        try
        {
            fixed (int* indices = pages)
                if (!Native.FPDF_ImportPagesByIndex(target, handle, indices, (uint)pages.Length, 0))
                    throw new PdfException(PdfException.EditError);
            for (var i = 0; i < pages.Length; i++)
                if (turns[i] % 4 != 0) RotatePage(target, i, turns[i]);
            Save(target, path);
        }
        finally
        {
            Native.FPDF_CloseDocument(target);
        }
    }

    static void RotatePage(nint document, int index, int quarterTurns)
    {
        var page = Native.FPDF_LoadPage(document, index);
        if (page == 0) throw new PdfException(PdfException.PageError);
        try { Native.FPDFPage_SetRotation(page, (Math.Max(0, Native.FPDFPage_GetRotation(page)) + quarterTurns % 4 + 4) % 4); }
        finally { Native.FPDF_ClosePage(page); }
    }

    /// <summary>
    /// Erst vollständig in eine Temp-Datei daneben, dann an den Platz: bricht das Schreiben unterwegs ab
    /// (Platte voll, NAS weg), bleibt eine vorhandene Datei unter path heil.
    /// </summary>
    static unsafe void Save(nint document, string path)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(temp))
            {
                var target = GCHandle.Alloc(stream);
                try
                {
                    var writer = new Native.FileWrite { Version = 1, WriteBlock = &WriteBlock, State = GCHandle.ToIntPtr(target) };
                    if (!Native.FPDF_SaveAsCopy(document, &writer, Native.SaveNoIncremental)) throw new PdfException(PdfException.EditError);
                }
                finally
                {
                    target.Free();
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Rückruf aus PDFium: keine Ausnahme darf hier hinaus, ein Fehler heißt 0.</summary>
    [UnmanagedCallersOnly]
    static unsafe int WriteBlock(Native.FileWrite* self, byte* data, uint size)
    {
        try
        {
            ((Stream)GCHandle.FromIntPtr(self->State).Target!).Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Offene Seite aus dem Vorrat, sonst frisch geladen. Nicht selbst schließen: das übernimmt der Vorrat,
    /// spätestens bei einer Änderung am Dokument (Seitenindizes verschieben sich) und in Dispose.
    /// </summary>
    nint LoadPage(int index)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        var at = openPages.FindIndex(open => open.Index == index);
        if (at >= 0)
        {
            var hit = openPages[at];
            openPages.RemoveAt(at);
            openPages.Add(hit);
            return hit.Page;
        }
        var page = Native.FPDF_LoadPage(handle, index);
        if (page == 0) throw new PdfException(PdfException.PageError);
        openPages.Add((index, page));
        if (openPages.Count > KeepPagesOpen)
        {
            Native.FPDF_ClosePage(openPages[0].Page);
            openPages.RemoveAt(0);
        }
        return page;
    }

    /// <summary>
    /// Offene Seiten freigeben — der Render-Thread im Leerlauf: eine Scan-Seite hält ihre entpackten Bilder, das soll
    /// nicht liegen bleiben, wenn nichts mehr zu rendern ist (Review 2026-09-13).
    /// </summary>
    public void ReleasePages() => ClosePages();

    /// <summary>Vor jeder Änderung an Seiten und vor dem Schließen: offene Seiten dürfen das Dokument nicht überleben.</summary>
    void ClosePages()
    {
        foreach (var (_, page) in openPages) Native.FPDF_ClosePage(page);
        openPages.Clear();
    }

    public void Dispose()
    {
        if (handle == 0) return;
        ClosePages();
        Native.FPDF_CloseDocument(handle);
        handle = 0;
    }
}
