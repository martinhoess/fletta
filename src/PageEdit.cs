using Fletta.Pdfium;

namespace Fletta;

/// <summary>
/// Eine gesammelte Seitenänderung. Apply wirkt auf das offene Dokument (dort liegt der Zwischenstand bis
/// Strg+S), Remap führt eine Liste je Seite — die Drehung der Ansicht — genauso nach. Rückgängig heißt:
/// Datei neu laden und alle Änderungen bis auf die letzte noch einmal anwenden.
/// </summary>
public abstract record PageEdit
{
    public abstract void Apply(PdfDocument document);

    /// <summary>perPage nach der Änderung; neue Seiten bekommen added. newCount ist die Seitenzahl danach.</summary>
    public abstract T[] Remap<T>(IReadOnlyList<T> perPage, int newCount, T added);
}

public sealed record DeletePages(int[] Pages) : PageEdit
{
    public override void Apply(PdfDocument document) => document.DeletePages(Pages);

    public override T[] Remap<T>(IReadOnlyList<T> perPage, int newCount, T added) =>
        [.. perPage.Where((_, page) => !Pages.Contains(page))];
}

/// <summary>Pages aufsteigend; Destination zählt wie bei FPDF_MovePages im Ergebnis.</summary>
public sealed record MovePages(int[] Pages, int Destination) : PageEdit
{
    /// <summary>Aus einer Lücke in der alten Liste (vor Eintrag gap, count = hinter dem letzten) das Ziel im Ergebnis.</summary>
    public static MovePages ToGap(int[] pages, int gap) => new([.. pages.Order()], gap - pages.Count(page => page < gap));

    /// <summary>Ändert die Reihenfolge überhaupt? Sonst lohnt weder PDFium noch ein Rückgängig-Schritt.</summary>
    public bool ChangesOrder(int count)
    {
        var order = Remap(Enumerable.Range(0, count).ToArray(), count, -1);
        return !order.SequenceEqual(Enumerable.Range(0, count));
    }

    public override void Apply(PdfDocument document) => document.MovePages(Pages, Destination);

    public override T[] Remap<T>(IReadOnlyList<T> perPage, int newCount, T added)
    {
        var rest = perPage.Where((_, page) => !Pages.Contains(page)).ToList();
        rest.InsertRange(Destination, Pages.Select(page => perPage[page]));
        return [.. rest];
    }
}

/// <summary>Alle Seiten der PDF unter Path vor Seite At.</summary>
public sealed record InsertPages(string Path, int At) : PageEdit
{
    public override void Apply(PdfDocument document) => document.InsertFrom(Path, At);

    public override T[] Remap<T>(IReadOnlyList<T> perPage, int newCount, T added)
    {
        var result = perPage.ToList();
        result.InsertRange(At, Enumerable.Repeat(added, newCount - perPage.Count));
        return [.. result];
    }
}
