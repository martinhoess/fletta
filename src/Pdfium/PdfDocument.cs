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

    /// <summary>Liest nur Querverweistabelle und Seitenbaum, keine Seiteninhalte.</summary>
    public static PdfDocument Open(string path)
    {
        var handle = Native.FPDF_LoadDocument(path, null);
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
