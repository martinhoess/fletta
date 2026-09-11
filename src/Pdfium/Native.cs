using System.Runtime.InteropServices;

namespace Fletta.Pdfium;

/// <summary>
/// Nur die PDFium-Funktionen, die Fletta braucht. Signaturen aus fpdfview.h von chromium/8044.
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
}
