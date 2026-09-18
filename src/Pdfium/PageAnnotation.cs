using System.Windows;

namespace Fletta.Pdfium;

/// <summary>
/// Eine Anmerkung auf der Seite, wie Fletta sie zeigt und löscht: Nummer in /Annots, Art (FPDF_ANNOT_*), Lage in Anteilen
/// der angezeigten Seite (wie PageLink) und ihr Text (/Contents, bei Notizen der Notiztext).
/// </summary>
public sealed record PageAnnotation(int Index, int Subtype, Rect Area, string Contents);
