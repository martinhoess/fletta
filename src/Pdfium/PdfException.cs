namespace Fletta.Pdfium;

/// <summary>Fehler aus PDFium, Code nach FPDF_ERR_* in fpdfview.h.</summary>
public sealed class PdfException(uint code) : Exception(Describe(code))
{
    public const uint FileError = 2;     // FPDF_ERR_FILE
    public const uint FormatError = 3;   // FPDF_ERR_FORMAT
    public const uint PasswordError = 4; // FPDF_ERR_PASSWORD
    public const uint SecurityError = 5; // FPDF_ERR_SECURITY
    public const uint PageError = 6;     // FPDF_ERR_PAGE
    public const uint EditError = 1000;  // eigene: Bearbeiten oder Speichern abgelehnt, PDFium nennt keinen Grund

    public uint Code { get; } = code;

    static string Describe(uint code) => code switch
    {
        FileError => "Die Datei wurde nicht gefunden oder lässt sich nicht lesen.",
        FormatError => "Das ist keine PDF-Datei, oder sie ist beschädigt.",
        PasswordError => "Die Datei ist passwortgeschützt, oder das Passwort stimmt nicht.",
        SecurityError => "Die Datei nutzt eine Verschlüsselung, die PDFium nicht kennt.",
        PageError => "Die Seite fehlt oder ihr Inhalt ist fehlerhaft.",
        EditError => "PDFium konnte die Änderung nicht ausführen oder die Datei nicht schreiben.",
        _ => $"PDFium meldet Fehler {code}.",
    };
}
