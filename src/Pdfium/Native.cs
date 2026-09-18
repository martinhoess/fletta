using System.Runtime.InteropServices;

namespace Fletta.Pdfium;

/// <summary>
/// Nur die PDFium-Funktionen, die Fletta braucht. Signaturen aus den Headern von chromium/8044 (fpdfview.h und die
/// jeweils genannten fpdf_*.h, darunter fpdf_annot.h und fpdf_formfill.h).
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

    /// <summary>Puffer gehört PDFium; alpha ≠ 0 ergibt BGRA.</summary>
    [LibraryImport(Dll)]
    internal static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Dll)]
    internal static partial int FPDFBitmap_GetStride(nint bitmap);

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

    /// <summary>Umkehrung von FPDF_PageToDevice: Rasterpunkt der angezeigten Seite in Seitenkoordinaten.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
                                                   int deviceX, int deviceY, out double pageX, out double pageY);

    /// <summary>Zeichenkasten nach Schriftgröße (ganze Zeilenhöhe), in Seitenkoordinaten; Nummer der Zeichenliste.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFText_GetLooseCharBox(nint textPage, int index, out RectF rect);

    // fpdf_annot.h — Anmerkungen (alles „experimental“ in PDFium)

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointF
    {
        public float X;
        public float Y;
    }

    /// <summary>FS_QUADPOINTSF: 1 oben links, 2 oben rechts, 3 unten links, 4 unten rechts.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct QuadPointsF
    {
        public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
    }

    internal const int AnnotText = 1, AnnotLink = 2, AnnotFreeText = 3, AnnotSquare = 5, AnnotHighlight = 9, AnnotUnderline = 10,
                       AnnotStrikeOut = 12, AnnotStamp = 13, AnnotInk = 15, AnnotPopup = 16, AnnotWidget = 20;
    internal const int AnnotFlagHidden = 2, AnnotFlagPrint = 4, AnnotFlagNoZoom = 8, AnnotFlagNoRotate = 16, AnnotFlagNoView = 32; // FPDF_ANNOT_FLAG_*
    internal const int ColorStroke = 0; // FPDFANNOT_COLORTYPE_Color

    [LibraryImport(Dll)]
    internal static partial nint FPDFPage_CreateAnnot(nint page, int subtype);

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport(Dll)]
    internal static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_GetAnnotIndex(nint page, nint annot);

    /// <summary>Gibt nur die Hülle frei, die Anmerkung bleibt im Dokument.</summary>
    [LibraryImport(Dll)]
    internal static partial void FPDFPage_CloseAnnot(nint annot);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFPage_RemoveAnnot(nint page, int index);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetSubtype(nint annot);

    /// <summary>Scheitert, wenn die Anmerkung schon eine Erscheinung (AP) hat.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_SetRect(nint annot, in RectF rect);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_GetRect(nint annot, out RectF rect);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_AppendAttachmentPoints(nint annot, in QuadPointsF quadPoints);

    /// <summary>Index des neuen Strichs, −1 bei Fehler. Nur für Ink.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial int FPDFAnnot_AddInkStroke(nint annot, PointF* points, nuint pointCount);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_SetBorder(nint annot, float horizontalRadius, float verticalRadius, float borderWidth);

    /// <summary>Wert als UTF-16LE mit Terminator; key in UTF-8 (etwa „Contents“).</summary>
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool FPDFAnnot_SetStringValue(nint annot, string key, char* value);

    /// <summary>Bytes samt Terminator (UTF-16LE); buffer darf null sein.</summary>
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial uint FPDFAnnot_GetStringValue(nint annot, string key, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_SetFlags(nint annot, int flags);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetFlags(nint annot);

    /// <summary>Inhalt der Erscheinung als UTF-16LE, Bytes samt Terminator; nur für den Selbsttest.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial uint FPDFAnnot_GetAP(nint annot, int appearanceMode, void* buffer, uint bufferLength);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FPDFAnnot_GetLinkedAnnot(nint annot, string key);

    /// <summary>Nur Ink und Stamp; das Objekt gehört danach der Anmerkung.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_AppendObject(nint annot, nint pageObject);

    // fpdf_edit.h — Seitenobjekte für Stempel (Text, Bild)

    /// <summary>font: Name einer der 14 Standardschriften, etwa „Helvetica“.</summary>
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FPDFPageObj_NewTextObj(nint document, string font, float fontSize);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool FPDFText_SetText(nint textObject, char* text);

    [LibraryImport(Dll)]
    internal static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFPageObj_GetBounds(nint pageObject, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFPageObj_SetFillColor(nint pageObject, uint r, uint g, uint b, uint a);

    [LibraryImport(Dll)]
    internal static partial nint FPDFPageObj_NewImageObj(nint document);

    /// <summary>pages darf null sein (count 0).</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFImageObj_SetBitmap(nint pages, int count, nint imageObject, nint bitmap);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFImageObj_SetMatrix(nint imageObject, double a, double b, double c, double d, double e, double f);

    /// <summary>Nur für Objekte, die keiner Seite und keiner Anmerkung gehören.</summary>
    [LibraryImport(Dll)]
    internal static partial void FPDFPageObj_Destroy(nint pageObject);

    // fpdf_formfill.h — Formulare (Stufe 4). Fletta füllt Felder über die FORM_-Funktionen aus, nicht über
    // weitergereichte Maus- und Tastaturereignisse; die Rückrufe bleiben deshalb bis auf leere Hüllen ungenutzt.

    /// <summary>
    /// FPDF_FORMFILLINFO, Version 1, danach Platz für die Felder von Version 2 (bleiben 0; PDFium liest sie bei
    /// Version 1 nicht). Rückrufe, die PDFium laut Header ohne V8 nicht braucht, bleiben null.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct FormFillInfo
    {
        public int Version;
        public nint Release;
        public delegate* unmanaged<FormFillInfo*, nint, double, double, double, double, void> Invalidate;
        public delegate* unmanaged<FormFillInfo*, nint, double, double, double, double, void> OutputSelectedRect;
        public delegate* unmanaged<FormFillInfo*, int, void> SetCursor;
        public delegate* unmanaged<FormFillInfo*, int, nint, int> SetTimer;
        public delegate* unmanaged<FormFillInfo*, int, void> KillTimer;
        public nint GetLocalTime;   // laut Header ungenutzt
        public delegate* unmanaged<FormFillInfo*, void> OnChange;
        public nint GetPage;        // nur mit V8
        public nint GetCurrentPage; // nur mit V8
        public nint GetRotation;    // laut Header ungenutzt
        public delegate* unmanaged<FormFillInfo*, byte*, void> ExecuteNamedAction;
        public delegate* unmanaged<FormFillInfo*, char*, uint, int, void> SetTextFieldFocus;
        public delegate* unmanaged<FormFillInfo*, byte*, void> DoUriAction;
        public delegate* unmanaged<FormFillInfo*, int, int, float*, int, void> DoGoToAction;
        public nint JsPlatform;
        public int XfaDisabled;
        public fixed long Version2[24]; // ab FFI_DisplayCaret; bei Version 1 ungelesen
    }

    internal const int FormFieldPushButton = 1, FormFieldCheckBox = 2, FormFieldRadioButton = 3, FormFieldComboBox = 4,
                       FormFieldListBox = 5, FormFieldText = 6, FormFieldSignature = 7; // FPDF_FORMFIELD_*
    internal const int FormFlagReadOnly = 1, FormFlagMultiline = 1 << 12; // FPDF_FORMFLAG_*

    /// <summary>0 ohne Formular, 1 AcroForm, 2 und 3 XFA (Fletta füllt nur AcroForm aus).</summary>
    [LibraryImport(Dll)]
    internal static partial int FPDF_GetFormType(nint document);

    [LibraryImport(Dll)]
    internal static unsafe partial nint FPDFDOC_InitFormFillEnvironment(nint document, FormFillInfo* formInfo);

    [LibraryImport(Dll)]
    internal static partial void FPDFDOC_ExitFormFillEnvironment(nint form);

    [LibraryImport(Dll)]
    internal static partial void FORM_OnAfterLoadPage(nint page, nint form);

    /// <summary>Vor jedem FPDF_ClosePage einer Seite, die FORM_OnAfterLoadPage gesehen hat.</summary>
    [LibraryImport(Dll)]
    internal static partial void FORM_OnBeforeClosePage(nint page, nint form);

    /// <summary>Formularfelder auf die schon gerenderte Seite; FPDF_RenderPageBitmap zeichnet sie nicht.</summary>
    [LibraryImport(Dll)]
    internal static partial void FPDF_FFLDraw(nint form, nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>color als 0x00BBGGRR (der Header sagt „0xxxrrggbb“, gemessen ist es umgekehrt); fieldType 0 = alle Felder.</summary>
    [LibraryImport(Dll)]
    internal static partial void FPDF_SetFormFieldHighlightColor(nint form, int fieldType, uint color);

    [LibraryImport(Dll)]
    internal static partial void FPDF_SetFormFieldHighlightAlpha(nint form, byte alpha);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FORM_SetFocusedAnnot(nint form, nint annot);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FORM_SelectAllText(nint form, nint page);

    /// <summary>Ersetzt die Auswahl im fokussierten Textfeld (UTF-16LE); ohne Auswahl wird eingefügt.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial void FORM_ReplaceSelection(nint form, nint page, char* text);

    /// <summary>Fokus weg: erst dann übernimmt PDFium den Wert ins Feld und erneuert seine Erscheinung.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FORM_ForceToKillFocus(nint form);

    /// <summary>Auswahl- und Listenfelder; wirkt auf das fokussierte Feld.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FORM_SetIndexSelected(nint form, nint page, int index, [MarshalAs(UnmanagedType.Bool)] bool selected);

    /// <summary>Zeichen an das fokussierte Feld, wie getippt (Leertaste schaltet Kästchen und Optionsfelder).</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FORM_OnChar(nint form, nint page, int character, int modifier);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetFormFieldType(nint form, nint annot);

    /// <summary>Bytes samt Terminator (UTF-16LE); buffer darf null sein. Gleiches Muster für Wert und Optionen.</summary>
    [LibraryImport(Dll)]
    internal static unsafe partial uint FPDFAnnot_GetFormFieldName(nint form, nint annot, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    internal static unsafe partial uint FPDFAnnot_GetFormFieldValue(nint form, nint annot, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetFormFieldFlags(nint form, nint annot);

    /// <summary>−1 bei Fehler oder anderem Feldtyp.</summary>
    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetOptionCount(nint form, nint annot);

    [LibraryImport(Dll)]
    internal static unsafe partial uint FPDFAnnot_GetOptionLabel(nint form, nint annot, int index, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_IsOptionSelected(nint form, nint annot, int index);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_IsChecked(nint form, nint annot);

    /// <summary>0 heißt „automatisch“ (Größe folgt dem Feld).</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_GetFontSize(nint form, nint annot, out float size);

    /// <summary>Seitenkoordinaten auf ein Pixelraster der angezeigten Seite; rechnet Seitenrahmen und /Rotate ein.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
                                                   double pageX, double pageY, out int deviceX, out int deviceY);
}
