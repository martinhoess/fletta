using System.Windows;
using System.Windows.Media;
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

    /// <summary>
    /// Bleiben Seitenfolge und -größen (Anmerkungen, Formulare)? Dann rendert das Fenster nur neu und bleibt, wo es ist —
    /// nur OnlyPage, wenn gesetzt, sonst alle Seiten (ein Feld kann auf mehreren Seiten stehen).
    /// </summary>
    public virtual bool KeepsPages => false;

    public virtual int? OnlyPage => null;
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

/// <summary>
/// Anmerkungen (Stufe 3): ändern keine Seitenfolge. Lagen stehen in Anteilen der Ansicht (0–1 von oben links, mit
/// Flettas Drehung Turns zum Zeitpunkt der Änderung) — so trifft ein Wiederanwenden nach Strg+Z dieselbe Stelle, auch
/// wenn die Ansicht inzwischen anders gedreht ist.
/// </summary>
public abstract record AnnotationEdit(int Page) : PageEdit
{
    public static readonly Color HighlightColor = Color.FromRgb(255, 226, 0);
    public static readonly Color InkColor = Color.FromRgb(214, 40, 40);
    public static readonly Color SignatureColor = Color.FromRgb(20, 40, 120);
    public static readonly Color TextColor = Colors.Black;

    public override bool KeepsPages => true;

    public override int? OnlyPage => Page;

    public override T[] Remap<T>(IReadOnlyList<T> perPage, int newCount, T added) => [.. perPage];

    /// <summary>Anteile der Ansicht in Seitenkoordinaten; Right und Down sind die Richtungen der Ansicht je Punkt.</summary>
    public readonly record struct ViewMap(Point Origin, Vector Right, Vector Down, Size Size)
    {
        public Point At(Point fraction) => Origin + Right * (fraction.X * Size.Width) + Down * (fraction.Y * Size.Height);
    }

    /// <summary>
    /// Ansicht → angezeigte Seite (Flettas Drehung zurück, PageLayout.Turn) → Seitenkoordinaten (PdfDocument.DisplayToPage,
    /// mit /Rotate und Seitenrahmen). Alles affin, also genügen drei Punkte. Im Selbsttest geprüft.
    /// </summary>
    public static ViewMap MapOf(PdfDocument document, int page, int turns)
    {
        var (origin, right, down) = document.DisplayToPage(page);
        Point PageOf(Point view)
        {
            var shown = PageLayout.Turn(view, -turns);
            return origin + right * shown.X + down * shown.Y;
        }
        var size = document.PageSize(page);
        var viewSize = turns % 2 == 0 ? size : new Size(size.Height, size.Width);
        var start = PageOf(new Point(0, 0));
        return new ViewMap(start, (PageOf(new Point(1, 0)) - start) / viewSize.Width, (PageOf(new Point(0, 1)) - start) / viewSize.Height, viewSize);
    }
}

/// <summary>Textmarker über Zeichen der Zeichenliste (PdfDocument.CharBoxes).</summary>
public sealed record AddHighlight(int Page, int Start, int Count) : AnnotationEdit(Page)
{
    public override void Apply(PdfDocument document) => document.AddHighlight(Page, Start, Count, HighlightColor);
}

/// <summary>Freihand und gezeichnete Unterschrift: Striche in Anteilen der Ansicht, Breite in Punkt.</summary>
public sealed record AddInk(int Page, int Turns, Point[][] Strokes, Color Color, float Width) : AnnotationEdit(Page)
{
    public override void Apply(PdfDocument document)
    {
        var map = MapOf(document, Page, Turns);
        document.AddInk(Page, [.. Strokes.Select(stroke => stroke.Select(map.At).ToArray())], Color, Width);
    }
}

/// <summary>Notiz mit Symbol oben links an At.</summary>
public sealed record AddNote(int Page, int Turns, Point At, string Text) : AnnotationEdit(Page)
{
    public override void Apply(PdfDocument document) => document.AddNote(Page, MapOf(document, Page, Turns).At(At), Text);
}

/// <summary>Text auf die Seite, linke obere Ecke an At, Größe in Punkt; steht in der Ansicht aufrecht.</summary>
public sealed record AddText(int Page, int Turns, Point At, string Text, float Size) : AnnotationEdit(Page)
{
    public override void Apply(PdfDocument document)
    {
        var map = MapOf(document, Page, Turns);
        document.AddText(Page, map.At(At), map.Right, map.Down, Text, Size, TextColor);
    }
}

/// <summary>Bild (Unterschrift aus Datei) in den Kasten Box der Ansicht; Pixel BGRA mit Alpha.</summary>
public sealed record AddImage(int Page, int Turns, Rect Box, byte[] Bgra, int PixelWidth, int PixelHeight) : AnnotationEdit(Page)
{
    public override void Apply(PdfDocument document)
    {
        var map = MapOf(document, Page, Turns);
        var corner = map.At(Box.BottomLeft);
        document.AddImage(Page, corner, map.At(Box.BottomRight) - corner, map.At(Box.TopLeft) - corner, Bgra, PixelWidth, PixelHeight);
    }
}

/// <summary>Anmerkung Nummer Index der Seite löschen.</summary>
public sealed record RemoveAnnotation(int Page, int Index) : AnnotationEdit(Page)
{
    public override void Apply(PdfDocument document) => document.RemoveAnnotation(Page, Index);
}

/// <summary>Text einer Notiz ändern.</summary>
public sealed record SetNoteText(int Page, int Index, string Text) : AnnotationEdit(Page)
{
    public override void Apply(PdfDocument document) => document.SetAnnotationText(Page, Index, Text);
}

/// <summary>
/// Formular (Stufe 4): ein Feld (Widget Nummer Index auf Page) ausfüllen. Felder mit gleichem Namen teilen den Wert, auch
/// über Seiten hinweg — deshalb rendert das Fenster danach alle Seiten neu (OnlyPage bleibt null).
/// </summary>
public abstract record FormEdit(int Page, int Index) : PageEdit
{
    public override bool KeepsPages => true;

    public override T[] Remap<T>(IReadOnlyList<T> perPage, int newCount, T added) => [.. perPage];
}

public sealed record SetFieldText(int Page, int Index, string Text) : FormEdit(Page, Index)
{
    public override void Apply(PdfDocument document) => document.SetFieldText(Page, Index, Text);
}

/// <summary>Kästchen oder Optionsfeld umschalten.</summary>
public sealed record ClickField(int Page, int Index) : FormEdit(Page, Index)
{
    public override void Apply(PdfDocument document) => document.ClickField(Page, Index);
}

public sealed record SetFieldChoice(int Page, int Index, int Option) : FormEdit(Page, Index)
{
    public override void Apply(PdfDocument document) => document.SetFieldChoice(Page, Index, Option);
}
