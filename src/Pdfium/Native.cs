using System.Runtime.InteropServices;

namespace Fletta.Pdfium;

/// <summary>
/// Nur die PDFium-Funktionen, die Fletta braucht. Signaturen aus den Headern von chromium/8044 (fpdfview.h und die
/// jeweils genannten fpdf_*.h).
/// FPDF_BOOL ist ein int, daher MarshalAs(Bool) mit 4 Byte.
/// </summary>
internal static partial class Native
{
    const string Dll = "pdfium";

    /// <summary>FPDF_LIBRARY_CONFIG bis einschließlich Version 2.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LibraryConfig
    {
        public int Version;
        public nint UserFontPaths;
        public nint Isolate;
        public uint V8EmbedderSlot;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SizeF
    {
        public float Width;
        public float Height;
    }

    [LibraryImport(Dll)]
    internal static partial void FPDF_InitLibraryWithConfig(in LibraryConfig config);

    /// <summary>Pfad als UTF-8, laut fpdfview.h auch unter Windows.</summary>
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FPDF_LoadDocument(string filePath, string? password);

    /// <summary>unsigned long ist unter Windows 32 Bit breit.</summary>
    [LibraryImport(Dll)]
    internal static partial uint FPDF_GetLastError();

    [LibraryImport(Dll)]
    internal static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_GetPageSizeByIndexF(nint document, int pageIndex, out SizeF size);

    [LibraryImport(Dll)]
    internal static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Dll)]
    internal static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Dll)]
    internal static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Dll)]
    internal static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY,
                                                       int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Dll)]
    internal static partial void FPDFBitmap_Destroy(nint bitmap);

    /// <summary>Nur unter Windows: rendert auf ein Geräte-HDC (Bildschirm, Bitmap oder Drucker).</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_RenderPage(nint dc, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // fpdf_doc.h — Lesezeichen
    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    /// <summary>Titel in UTF-16LE mit Terminator; Rückgabe ist die nötige Länge in Bytes, buffer darf null sein.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetAction(nint bookmark);

    [LibraryImport(Dll)]
    internal static partial uint FPDFAction_GetType(nint action);

    [LibraryImport(Dll)]
    internal static partial nint FPDFAction_GetDest(nint document, nint action);

    [LibraryImport(Dll)]
    internal static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);

    // fpdf_edit.h, fpdf_ppo.h, fpdf_save.h, fpdf_signature.h — Seiten bearbeiten und speichern

    /// <summary>
    /// FPDF_FILEWRITE (Version 1). PDFium kennt nur die ersten beiden Felder; State dahinter trägt den
    /// GCHandle des Ziel-Streams, den WriteBlock über den self-Zeiger wiederfindet.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct FileWrite
    {
        public int Version;
        public delegate* unmanaged<FileWrite*, byte*, uint, int> WriteBlock;
        public nint State;
    }

    internal const uint SaveNoIncremental = 2; // FPDF_NO_INCREMENTAL

    /// <summary>0–3 im Uhrzeigersinn, −1 bei Fehler.</summary>
    [LibraryImport(Dll)]
    internal static partial int FPDFPage_GetRotation(nint page);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_SetRotation(nint page, int rotate);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_Delete(nint document, int pageIndex);

    /// <summary>Experimentell. destPageIndex zählt im Ergebnis, also nach dem Herausnehmen der Seiten.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool FPDF_MovePages(nint document, int* pageIndices, uint length, int destPageIndex);

    [LibraryImport(Dll)]
    internal static partial nint FPDF_CreateNewDocument();

    /// <summary>pageIndices null importiert alle Seiten.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool FPDF_ImportPagesByIndex(nint destDocument, nint sourceDocument, int* pageIndices, uint length, int index);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool FPDF_SaveAsCopy(nint document, FileWrite* fileWrite, uint flags);

    /// <summary>Experimentell; −1 bei Fehler.</summary>
    [LibraryImport(Dll)]
    internal static partial int FPDF_GetSignatureCount(nint document);

    // fpdf_text.h
    [LibraryImport(Dll)]
    internal static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Dll)]
    internal static partial void FPDFText_ClosePage(nint textPage);

    /// <summary>Zeichenzahl der Seite, -1 bei Fehler.</summary>
    [LibraryImport(Dll)]
    internal static partial int FPDFText_CountChars(nint textPage);

    /// <summary>Schreibt count UCS-2-Werte plus Terminator; gibt die geschriebene Zahl samt Terminator zurück.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial int FPDFText_GetText(nint textPage, int startIndex, int count, ushort* result);

    /// <summary>
    /// Nummer in der Zeichenliste der Seite zu einer Stelle im Text von FPDFText_GetText; die beiden laufen auseinander,
    /// sobald eine Seite Zeichen ohne UCS-2-Entsprechung hat (die lässt GetText aus). −1 bei Fehler. fpdf_searchex.h.
    /// </summary>
    [LibraryImport(Dll)]
    internal static partial int FPDFText_GetCharIndexFromTextIndex(nint textPage, int textIndex);

    /// <summary>Rechtecke, die count Zeichen ab startIndex belegen (Zeichenliste, nicht Text); −1 bei falschem Start.</summary>
    [LibraryImport(Dll)]
    internal static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);

    /// <summary>Rechteck aus dem letzten FPDFText_CountRects, in Seitenkoordinaten (y nach oben).</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFText_GetRect(nint textPage, int rectIndex, out double left, out double top, out double right, out double bottom);

    // Links: Annotationen (fpdf_doc.h) und Adressen im Text (fpdf_text.h)

    /// <summary>FS_RECTF.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    internal const uint ActionUri = 3; // PDFACTION_URI

    /// <summary>Nächste Link-Annotation ab startPos; false am Ende.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFLink_Enumerate(nint page, ref int startPos, out nint link);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFLink_GetAnnotRect(nint link, out RectF rect);

    [LibraryImport(Dll)]
    internal static partial nint FPDFLink_GetDest(nint document, nint link);

    [LibraryImport(Dll)]
    internal static partial nint FPDFLink_GetAction(nint link);

    /// <summary>Bytes samt NUL; buffer bleibt unberührt, wenn er zu klein ist. Meist ASCII, mitunter UTF-8.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial uint FPDFAction_GetURIPath(nint document, nint action, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    internal static partial nint FPDFLink_LoadWebLinks(nint textPage);

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_CountWebLinks(nint linkPage);

    /// <summary>UTF-16-Einheiten samt Terminator; mit buffer null die nötige Länge.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial int FPDFLink_GetURL(nint linkPage, int linkIndex, ushort* buffer, int bufferLength);

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_CountRects(nint linkPage, int linkIndex);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFLink_GetRect(nint linkPage, int linkIndex, int rectIndex, out double left, out double top, out double right, out double bottom);

    [LibraryImport(Dll)]
    internal static partial void FPDFLink_CloseWebLinks(nint linkPage);

    /// <summary>Seitenkoordinaten auf ein Pixelraster der angezeigten Seite; rechnet Seitenrahmen und /Rotate ein.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
                                                   double pageX, double pageY, out int deviceX, out int deviceY);
}
