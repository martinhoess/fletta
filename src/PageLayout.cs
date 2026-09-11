using System.Windows;

namespace Fletta;

public enum ZoomMode { FitWidth, FitPage, Fixed }

/// <summary>
/// Anordnung aller Seiten in DIP, zeilenweise. Eine Zeile hält eine Seite, bei Doppelseite zwei
/// (Deckblatt allein, danach Paare wie im gedruckten Buch). Seitenweise (slotHeight &gt; 0) belegt
/// jede Zeile mindestens die Sichthöhe, die Seiten stehen mittig darin. Reine Rechnung ohne
/// WPF-Elemente, im Selbsttest geprüft.
/// </summary>
public sealed class PageLayout
{
    public const double Gap = 14, SpreadGap = 4, MarginX = 32, MarginTop = 24, MarginBottom = 48;
    public const double DipPerPoint = 96.0 / 72;
    public static readonly int[] ZoomSteps = [25, 33, 50, 67, 75, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400];
    const double MinScale = 0.05;

    readonly Size[] sizes;      // je Seite
    readonly double[] tops;     // je Seite, Oberkante der Seite selbst
    readonly double[] offsetX;  // je Seite, Abstand vom linken Rand ihrer Zeile
    readonly int[] rowOf;       // je Seite
    readonly int[] rowFirst;    // je Zeile, erste Seite
    readonly double[] rowTops;  // je Zeile, Oberkante der Zeile (seitenweise: des Platzes)
    readonly double[] rowWidths;
    readonly bool paged;

    public PageLayout(IReadOnlyList<Size> pagesPt, double scale, bool spreads = false, double slotHeight = 0)
    {
        Scale = scale;
        paged = slotHeight > 0;
        var count = pagesPt.Count;
        sizes = new Size[count];
        tops = new double[count];
        offsetX = new double[count];
        rowOf = new int[count];
        rowFirst = RowStarts(count, spreads);
        rowTops = new double[rowFirst.Length];
        rowWidths = new double[rowFirst.Length];

        var y = paged ? 0 : MarginTop;
        for (var row = 0; row < rowFirst.Length; row++)
        {
            var (first, end) = (rowFirst[row], RowEnd(row));
            double x = 0, rowHeight = 0;
            for (var page = first; page < end; page++)
            {
                sizes[page] = new Size(pagesPt[page].Width * scale, pagesPt[page].Height * scale);
                rowOf[page] = row;
                offsetX[page] = x;
                x += sizes[page].Width + SpreadGap;
                rowHeight = Math.Max(rowHeight, sizes[page].Height);
            }
            rowWidths[row] = x - SpreadGap;
            var slot = paged ? Math.Max(slotHeight, rowHeight + 2 * Gap) : rowHeight;
            rowTops[row] = y;
            for (var page = first; page < end; page++) tops[page] = y + (slot - sizes[page].Height) / 2;
            y += slot + (paged ? 0 : Gap);
        }
        Width = (rowWidths.Length == 0 ? 0 : rowWidths.Max()) + 2 * MarginX;
        Height = paged ? y : y - Gap + MarginBottom;
    }

    /// <summary>Erste Seite je Zeile: einzeln jede Seite, als Doppelseite 0 | 1 2 | 3 4 | …</summary>
    static int[] RowStarts(int count, bool spreads) =>
        spreads ? [.. Enumerable.Range(0, count).Where(page => page == 0 || page % 2 == 1)] : [.. Enumerable.Range(0, count)];

    int RowEnd(int row) => row + 1 < rowFirst.Length ? rowFirst[row + 1] : sizes.Length;

    /// <summary>DIP je PDF-Punkt.</summary>
    public double Scale { get; }
    public double Percent => Scale / DipPerPoint * 100;
    public double Width { get; }
    public double Height { get; }
    public int Count => sizes.Length;
    public double Top(int page) => tops[page];
    public Size SizeOf(int page) => sizes[page];

    /// <summary>Linke Kante der Seite, wenn die Leinwand so breit ist; die Zeile steht mittig.</summary>
    public double Left(int page, double canvasWidth) => (canvasWidth - rowWidths[rowOf[page]]) / 2 + offsetX[page];

    /// <summary>Erste Seite der Zeile, deren Oberkante bei oder über y liegt. Braucht mindestens eine Seite.</summary>
    public int PageAt(double y) => rowFirst[RowAt(y)];

    int RowAt(double y)
    {
        var i = Array.BinarySearch(rowTops, y);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    /// <summary>Erste und letzte Seite der Zeilen, die zwischen top und bottom liegen.</summary>
    public (int First, int Last) VisiblePages(double top, double bottom) => (PageAt(top), RowEnd(RowAt(bottom)) - 1);

    /// <summary>Erste Seite der Zeile davor oder dahinter; an den Enden bleibt es bei der Randzeile.</summary>
    public int NextRowPage(int page, int direction) => rowFirst[Math.Clamp(rowOf[page] + direction, 0, rowFirst.Length - 1)];

    /// <summary>Ober- und Unterkante des Platzes, den die Zeile der Seite belegt.</summary>
    public (double Top, double Bottom) RowSpan(int page)
    {
        var row = rowOf[page];
        var bottom = row + 1 < rowTops.Length ? rowTops[row + 1] - (paged ? 0 : Gap) : Height - (paged ? 0 : MarginBottom);
        return (rowTops[row], bottom);
    }

    /// <summary>
    /// Seite, die beim Bildlauf als aktuell gilt. Der Messpunkt wandert mit dem Bildlauf von der
    /// Oberkante (ganz oben) zur Unterkante des Sichtbereichs (ganz unten). Ein fester Punkt,
    /// etwa bei 35 % der Höhe, erreicht bei kleinem Zoom die letzten Seiten nie: die können nicht
    /// so weit nach oben rollen.
    /// </summary>
    public int CurrentPage(double offset, double viewportHeight)
    {
        var scrollable = Math.Max(0, Height - viewportHeight);
        var progress = scrollable > 0 ? Math.Clamp(offset / scrollable, 0, 1) : 0;
        return PageAt(offset + viewportHeight * progress);
    }

    /// <summary>
    /// Ausgangsseite für ← →. Ist die aktuelle Seite festgehalten (Sprung, Zoom, Drehen) oder steht
    /// der Bildlauf am Dokumentende, gilt sie; sonst die Seite an der Oberkante. Der Messpunkt von
    /// CurrentPage liegt beim freien Scrollen tiefer im Bild, → übersprünge von dort sichtbare Seiten.
    /// </summary>
    public int StepOrigin(double offset, double viewportHeight, int currentPage, bool pinned) =>
        pinned || offset >= Height - viewportHeight - 1 ? currentPage : PageAt(offset + (paged ? 1 : Gap + 1));

    /// <summary>
    /// Bildlauf-Offset, der die Zeile der Seite oben zeigt (seitenweise: ihren ganzen Platz); am
    /// Dokumentende so weit, wie es geht.
    /// </summary>
    public double ScrollTargetFor(int page, double viewportHeight) =>
        Math.Clamp(rowTops[rowOf[page]] - (paged ? 0 : Gap), 0, Math.Max(0, Height - viewportHeight));

    /// <summary>
    /// Offset in diesem Layout, der dieselbe Lesestelle zeigt wie oldOffset in old: als Anteil am
    /// Platz der Zeile von page (nicht an der Seite — in einem Paar oder seitenweise steht die Seite
    /// nicht oben in ihrer Zeile). Nach oben höchstens bis zum Sprungziel der Zeile, sonst stünde ein
    /// Stück der Vorzeile im Bild; seitenweise so, dass eine Zeile, die ins Fenster passt, ganz drin steht.
    /// </summary>
    public double KeepPosition(PageLayout old, double oldOffset, int page, double viewportHeight)
    {
        var (oldTop, oldBottom) = old.RowSpan(page);
        var (top, bottom) = RowSpan(page);
        var target = top + (oldOffset - oldTop) / Math.Max(1, oldBottom - oldTop) * (bottom - top);
        target = paged ? Math.Clamp(target, top, Math.Max(top, bottom - viewportHeight)) : Math.Max(target, top - Gap);
        return Math.Clamp(target, 0, Math.Max(0, Height - viewportHeight));
    }

    /// <summary>
    /// Maßstab für den Zoommodus. „Seitenbreite“ passt die breiteste Zeile ein, „Ganze Seite“ die
    /// Zeile der aktuellen Seite. Einen DIP Luft lassen: sonst kippt die Rundung die Breite knapp
    /// über den Sichtbereich, und die waagrechte Leiste erscheint.
    /// </summary>
    public static double ScaleFor(ZoomMode mode, int percent, IReadOnlyList<Size> pagesPt, int currentPage, Size viewport,
                                  bool spreads = false)
    {
        var width = viewport.Width - 2 * MarginX - 1;
        var starts = RowStarts(pagesPt.Count, spreads);
        IEnumerable<Size> Row(int row) =>
            pagesPt.Skip(starts[row]).Take((row + 1 < starts.Length ? starts[row + 1] : pagesPt.Count) - starts[row]);
        double FitWidth(int row) =>
            (width - SpreadGap * (Row(row).Count() - 1)) / Row(row).Sum(p => p.Width);

        switch (mode)
        {
            case ZoomMode.FitWidth:
                return Math.Max(MinScale, Enumerable.Range(0, starts.Length).Min(FitWidth));
            case ZoomMode.FitPage:
                var current = Array.FindLastIndex(starts, start => start <= currentPage);
                return Math.Max(MinScale, Math.Min(FitWidth(current), (viewport.Height - 2 * Gap) / Row(current).Max(p => p.Height)));
            default:
                return percent / 100.0 * DipPerPoint;
        }
    }

    /// <summary>Gerundete Pixelzahl, mindestens 1 — für Hauptansicht und Miniaturen gleich.</summary>
    public static int ToPixels(double value) => Math.Max(1, (int)Math.Round(value));

    /// <summary>Nächste Stufe über bzw. unter dem aktuellen Prozentwert; an den Enden bleibt es dabei.</summary>
    public static int NextZoom(double currentPercent, int direction) =>
        direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > currentPercent + 0.5, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < currentPercent - 0.5, ZoomSteps[0]);
}
