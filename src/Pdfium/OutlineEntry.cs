namespace Fletta.Pdfium;

/// <summary>Ein Lesezeichen: Titel, Zielseite (0-basiert, −1 ohne Ziel) und Tiefe im Baum.</summary>
public sealed record OutlineEntry(string Title, int Page, int Depth);
