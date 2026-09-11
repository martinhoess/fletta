using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fletta;

/// <summary>
/// Seitenleiste mit Miniaturen. Gerendert wird über denselben PageRenderer wie die Hauptansicht:
/// das Fenster hängt <see cref="Wanted"/> hinter die eigenen Aufträge und reicht die Ergebnisse
/// mit Thumbnail-Flag an <see cref="Deliver"/> weiter.
/// </summary>
public partial class ThumbnailPanel : UserControl
{
    // Waagrecht um die Miniatur: Randlinie 1, Innenabstand der Liste 2 × 6, Bildlaufleiste 12,
    // Karte 2 × (8 Innenabstand + 2 Rahmen). Senkrecht: Beschriftung 22 plus Karte 20.
    const double HorizontalChrome = 1 + 12 + 12 + 20, LabelHeight = 22, CardChrome = 20;
    const double MinBox = 40;
    const int Prefetch = 6;      // so viele Einträge über und unter dem Sichtbaren werden vorab gerendert
    const int KeepRendered = 24; // darüber hinaus werden Bilder verworfen

    // Ein Feld, auf einmal übergeben: 990 einzelne Add-Aufrufe an einer ObservableCollection kosteten
    // die ListBox vor dem ersten Bild rund eine Sekunde (gemessen 2026-09-10). Einzeln hinzu kommt nie etwas.
    ThumbItem[] items = [];
    readonly DispatcherTimer resizeSettled = new() { Interval = TimeSpan.FromMilliseconds(150) };
    IReadOnlyList<Size> pagesPt = [];
    int[] quarterTurns = [];
    DpiScale dpi = new(1, 1);
    double boxSize;
    (int First, int Last) visible = (0, -1);
    int current = -1;

    public ThumbnailPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Resize();
        // Ausgeblendet (F4) oder Reiter „Gliederung“ vorn: nichts bestellen; beim Einblenden neu.
        IsVisibleChanged += (_, _) => WantedChanged?.Invoke();
        // Beim Ziehen am Rand werden die alten Bilder gestreckt; neu gerendert wird erst, wenn es ruht.
        resizeSettled.Tick += (_, _) =>
        {
            resizeSettled.Stop();
            WantedChanged?.Invoke();
        };
    }

    /// <summary>Klick auf eine Miniatur. Die Auswahl (Strg/Umschalt) regelt die ListBox selbst.</summary>
    public event Action<int>? PageClicked;

    /// <summary>Die gewünschten Bilder haben sich geändert: Aufträge neu zusammenstellen.</summary>
    public event Action? WantedChanged;

    public DpiScale Dpi
    {
        set
        {
            dpi = value;
            WantedChanged?.Invoke();
        }
    }

    /// <summary>
    /// Neues Dokument. quarterTurns ist dasselbe Feld wie im Fenster, Drehungen meldet Refresh.
    /// expectedWidth ist die Breite, die die Leiste gleich bekommt: beim Befüllen hat sie oft noch
    /// keine (gerade erst eingeblendet).
    /// </summary>
    public void Show(IReadOnlyList<Size> pagesPt, int[] quarterTurns, double expectedWidth)
    {
        this.pagesPt = pagesPt;
        this.quarterTurns = quarterTurns;
        items = [.. Enumerable.Range(0, pagesPt.Count).Select(page => new ThumbItem(page))];
        List.ItemsSource = items;
        boxSize = 0; // erzwingt die Größen für alle Einträge
        Resize(expectedWidth);
    }

    public void Clear()
    {
        items = [];
        List.ItemsSource = items;
        pagesPt = [];
        quarterTurns = [];
        visible = (0, -1);
        current = -1;
    }

    /// <summary>Seitenfolge der markierten Miniaturen, aufsteigend.</summary>
    public int[] SelectedPages => [.. List.SelectedItems.Cast<ThumbItem>().Select(item => item.Page).Order()];

    public void SelectAll() => List.SelectAll();

    /// <summary>Hebt eine Mehrfachauswahl auf. Gibt false zurück, wenn es keine gab.</summary>
    public bool ClearSelection()
    {
        if (List.SelectedItems.Count <= 1) return false;
        List.UnselectAll();
        return true;
    }

    public void SetCurrent(int page)
    {
        if (current >= 0 && current < items.Length) items[current].IsCurrent = false;
        current = page;
        if (page < 0 || page >= items.Length) return;
        items[page].IsCurrent = true;
        if (page < visible.First || page > visible.Last) List.ScrollIntoView(items[page]);
    }

    /// <summary>Größe und Drehung einer Seite neu setzen, etwa nach R / L.</summary>
    public void Refresh(int page)
    {
        var shown = quarterTurns[page] % 2 == 1 ? new Size(pagesPt[page].Height, pagesPt[page].Width) : pagesPt[page];
        var scale = boxSize / Math.Max(shown.Width, shown.Height);
        items[page].SetGeometry(boxSize, shown.Width * scale, shown.Height * scale, ItemHeight, quarterTurns[page]);
    }

    double ItemHeight => boxSize + LabelHeight + CardChrome;

    /// <summary>
    /// Größe aller Einträge aus der Breite der Leiste. Kein Eintrag darf je 0 hoch sein: dann füllt
    /// sich der Sichtbereich nie, und die virtualisierende ListBox baut beim ersten Layout alle 990
    /// Einträge — gemessen eine Sekunde bis zum ersten Bild (2026-09-10). Daher die Ersatzbreite.
    /// </summary>
    void Resize(double fallbackWidth = 0)
    {
        var width = ActualWidth >= 1 ? ActualWidth : fallbackWidth;
        if (width < 1 || items.Length == 0) return; // ausgeblendet oder leer
        var box = Math.Max(MinBox, width - HorizontalChrome);
        if (Math.Abs(box - boxSize) < 0.5) return;
        boxSize = box;
        for (var page = 0; page < items.Length; page++) Refresh(page);
        resizeSettled.Stop();
        resizeSettled.Start();
    }

    /// <summary>Sichtbare Einträge bei fester Eintragshöhe; leer bei count = 0. Im Selbsttest geprüft.</summary>
    public static (int First, int Last) VisibleRange(double offset, double viewportHeight, double itemHeight, int count) =>
        count == 0 || itemHeight <= 0
            ? (0, -1)
            : (Math.Clamp((int)(offset / itemHeight), 0, count - 1),
               Math.Clamp((int)((offset + viewportHeight) / itemHeight), 0, count - 1));

    void OnListScrolled(object sender, ScrollChangedEventArgs e)
    {
        visible = VisibleRange(e.VerticalOffset, e.ViewportHeight, ItemHeight, items.Length);
        foreach (var item in items)
            if (item.Bitmap is not null && (item.Page < visible.First - KeepRendered || item.Page > visible.Last + KeepRendered))
                item.Drop();
        // Beim Ziehen am Rand löst jede Zwischenbreite ein ScrollChanged aus; bestellt wird erst nach der Ruhe.
        if (!resizeSettled.IsEnabled) WantedChanged?.Invoke();
    }

    void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ThumbItem item) PageClicked?.Invoke(item.Page);
    }

    /// <summary>Sichtbare Miniaturen zuerst, dann die Nachbarn; was schon passend da ist, fehlt.</summary>
    public IEnumerable<RenderRequest> Wanted()
    {
        if (items.Length == 0 || boxSize <= 0 || !IsVisible) return [];
        var (first, last) = visible;
        return Enumerable.Range(first - Prefetch, last - first + 1 + 2 * Prefetch)
            .Where(page => page >= 0 && page < items.Length && !items[page].Failed)
            .OrderBy(page => page < first || page > last)
            .Select(RequestFor)
            .Where(request => items[request.Page].Request != request);
    }

    RenderRequest RequestFor(int page)
    {
        var item = items[page];
        return new(page, PageLayout.ToPixels(item.ShownWidth * dpi.DpiScaleX), PageLayout.ToPixels(item.ShownHeight * dpi.DpiScaleY),
                   dpi.PixelsPerInchX, dpi.PixelsPerInchY, quarterTurns[page], Thumbnail: true);
    }

    public void Deliver(RenderResult result)
    {
        var page = result.Request.Page;
        if (page >= items.Length) return; // gehört zu einem inzwischen geschlossenen Dokument
        var wanted = RequestFor(page);
        if (result.Request != wanted && items[page].Request == wanted) return; // veraltet, passendes schon da
        if (result.Bitmap is null) items[page].Fail();
        else items[page].Show(result);
    }
}

/// <summary>Ein Eintrag der Seitenleiste; die Vorlage bindet an diese Eigenschaften.</summary>
public sealed class ThumbItem(int page) : INotifyPropertyChanged
{
    int quarterTurns;
    bool isCurrent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Page { get; } = page;
    public string Label { get; private set; } = $"Seite {page + 1}";
    public double BoxSize { get; private set; }
    public double ShownWidth { get; private set; }
    public double ShownHeight { get; private set; }
    public double ItemHeight { get; private set; }
    public BitmapSource? Bitmap { get; private set; }
    public Transform Turn { get; private set; } = Transform.Identity;
    public bool Failed { get; private set; }
    internal RenderRequest? Request { get; private set; }

    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            if (isCurrent == value) return;
            isCurrent = value;
            Notify(nameof(IsCurrent));
        }
    }

    internal void SetGeometry(double box, double width, double height, double itemHeight, int turns)
    {
        (BoxSize, ShownWidth, ShownHeight, ItemHeight, quarterTurns) = (box, width, height, itemHeight, turns);
        Label = Failed ? $"Seite {Page + 1} · Fehler" : turns == 0 ? $"Seite {Page + 1}" : $"Seite {Page + 1} · {turns * 90}°";
        UpdateTurn();
        Notify();
    }

    internal void Show(RenderResult result)
    {
        (Bitmap, Request, Failed) = (result.Bitmap, result.Request, false);
        UpdateTurn();
        Notify();
    }

    internal void Drop()
    {
        (Bitmap, Request) = (null, null);
        UpdateTurn();
        Notify();
    }

    internal void Fail()
    {
        Failed = true;
        Label = $"Seite {Page + 1} · Fehler";
        Notify();
    }

    // Wie in der Hauptansicht: ein Bild in alter Drehung dreht WPF, bis das neue da ist.
    void UpdateTurn()
    {
        var turns = Request is { } request ? (quarterTurns - request.QuarterTurns + 4) % 4 : 0;
        Turn = turns == 0 ? Transform.Identity : new RotateTransform(turns * 90);
    }

    /// <summary>Ohne Namen: alle Bindungen neu lesen.</summary>
    void Notify(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
