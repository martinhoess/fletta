using System.Windows;

namespace Fletta.Pdfium;

/// <summary>
/// Ein Link auf der Seite. Area in Anteilen der angezeigten Seite (0–1 von oben links, ohne Flettas Drehung);
/// Page ist das Ziel im Dokument (0-basiert) oder −1, dann steht die Adresse in Uri.
/// </summary>
public sealed record PageLink(Rect Area, int Page, string? Uri);
