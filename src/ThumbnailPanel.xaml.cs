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
    /// <summary>Ziehdaten „Prozess-ID:Seiten“ — daran erkennt die Leiste Seiten aus dem eigenen Fenster.</summary>
    public const string PagesFormat = "Fletta.Pages";
    const double DropScrollEdge = 40, DropScrollStep = 30;

    // Waagrecht um die Miniatur: Randlinie 1, Innenabstand der Liste 2 × 6, Bildlaufleiste 12,
    // Karte 2 × (8 Innenabstand + 2 Rahmen). Senkrecht: Beschriftung 22 plus Karte 20.
    const double HorizontalChrome = 1 + 12 + 12 + 20, LabelHeight = 22, CardChrome = 20;
    const double MinBox = 40;
    // Vorab gerendert wird vor allem in Scrollrichtung: eine Miniatur kostet am Suzuki-Handbuch ~160 ms, beim
    // Durchscrollen kamen die Bilder mit 6 in beide Richtungen (die schon gesehenen zuerst) zu spät.
    internal const int PrefetchAhead = 12, PrefetchBehind = 4;
    const int KeepRendered = 16; // darüber hinaus werden Bilder verworfen; muss über PrefetchAhead liegen

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
    double scrollOffset;
    bool scrollingUp;
    ThumbItem? pressed;   // gedrückt, noch nicht gezogen
    Point pressedAt;
    bool keepSelection;   // Druck auf eine Mehrfachauswahl: sie bleibt fürs Ziehen stehen

    public ThumbnailPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Resize();
        // Ausgeblendet (F4) oder Reiter „Gliederung“ vorn: nichts bestellen; beim Einblenden neu.
        IsVisibleChanged += (_, _) =>
        {
            // Ausgeblendete Miniaturen sind nur Ballast im Speicher; beim Einblenden kommen sie neu.
            if (!IsVisible) foreach (var item in items) if (item.Bitmap is not null) item.Drop();
            WantedChanged?.Invoke();
        };
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

    /// <summary>Rechtsklick auf eine Miniatur; die Auswahl enthält sie schon.</summary>
    public event Action? MenuRequested;

    /// <summary>Markierte Miniaturen werden gezogen: das Fenster startet das Ziehen mit diesen Seiten.</summary>
    public event Action<int[]>? DragRequested;

    /// <summary>Seiten aus diesem Fenster in eine Lücke gezogen (vor Eintrag gap, count = ans Ende).</summary>
    public event Action<int[], int>? PagesDropped;

    /// <summary>PDF-Dateien — auch Seiten aus einem anderen Fenster — in eine Lücke gezogen.</summary>
    public event Action<string[], int>? FilesDropped;

    /// <summary>Kann das Fenster gerade etwas einfügen? Sonst lehnt die Leiste ab, statt „angenommen“ zu melden.</summary>
    public Func<bool>? CanAcceptDrop { get; set; }

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

    /// <summary>Markiert genau diese Seiten, etwa die eben verschobenen oder eingefügten.</summary>
    public void Select(IEnumerable<int> pages)
    {
        List.SelectedItems.Clear();
        foreach (var page in pages)
            if (page >= 0 && page < items.Length) List.SelectedItems.Add(items[page]);
        if (List.SelectedItems.Count > 0) List.ScrollIntoView(List.SelectedItems[0]);
    }

    /// <summary>Seiten aus dem eigenen Prozess (Ziehdaten von <see cref="PagesFormat"/>), sonst null.</summary>
    public static int[]? OwnPages(IDataObject data)
    {
        if (!data.GetDataPresent(PagesFormat) || data.GetData(PagesFormat) is not string text) return null;
        var parts = text.Split(':');
        return parts.Length == 2 && parts[0] == Environment.ProcessId.ToString()
            ? [.. parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)]
            : null;
    }

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
        // Immer, nicht nur außerhalb von visible: das zählt angeschnittene Einträge mit, die Leiste hing beim Scrollen
        // eine Seite hinterher und kam am Dokumentende nicht ganz mit (Martin, 2026-09-13). Ganz sichtbar: kein Bildlauf.
        List.ScrollIntoView(items[page]);
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
        scrollOffset = e.VerticalOffset;
        if (e.VerticalChange != 0) scrollingUp = e.VerticalChange < 0;
        visible = VisibleRange(e.VerticalOffset, e.ViewportHeight, ItemHeight, items.Length);
        foreach (var item in items)
            if (item.Bitmap is not null && (item.Page < visible.First - KeepRendered || item.Page > visible.Last + KeepRendered))
                item.Drop();
        // Beim Ziehen am Rand löst jede Zwischenbreite ein ScrollChanged aus; bestellt wird erst nach der Ruhe.
        if (!resizeSettled.IsEnabled) WantedChanged?.Invoke();
    }

    void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e);
        if (keepSelection && item is not null) List.SelectedItem = item; // gedrückt, aber nicht gezogen: nur dieser
        (pressed, keepSelection) = (null, false);
        if (item is not null) PageClicked?.Invoke(item.Page);
    }

    static ThumbItem? ItemAt(RoutedEventArgs e) => (e.OriginalSource as FrameworkElement)?.DataContext as ThumbItem;

    /// <summary>
    /// Druck auf eine Miniatur merkt sie fürs Ziehen. Liegt sie in einer Mehrfachauswahl, bleibt die
    /// Auswahl stehen — sonst hebt die ListBox sie beim Drücken auf, und gezogen würde nur eine Seite.
    /// </summary>
    void OnListPress(object sender, MouseButtonEventArgs e)
    {
        pressed = ItemAt(e);
        pressedAt = e.GetPosition(List);
        keepSelection = pressed is not null && List.SelectedItems.Count > 1 && List.SelectedItems.Contains(pressed)
                        && Keyboard.Modifiers == ModifierKeys.None;
        if (keepSelection) e.Handled = true;
    }

    void OnListMove(object sender, MouseEventArgs e)
    {
        if (pressed is null || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(List) - pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = pressed;
        (pressed, keepSelection) = (null, false);
        if (!List.SelectedItems.Contains(item)) List.SelectedItem = item;
        DragRequested?.Invoke(SelectedPages);
    }

    void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e) is not { } item) return;
        if (!List.SelectedItems.Contains(item)) List.SelectedItem = item;
        e.Handled = true;
        MenuRequested?.Invoke();
    }

    /// <summary>
    /// Eigene Seiten werden verschoben. Seiten eines anderen Fensters kopiert, mit Umschalt verschoben (dort per
    /// Strg+Z zurückholbar). Dateien aus dem Explorer immer kopiert: meldete die Leiste „verschieben“, löschte
    /// der Explorer die Datei — auch wenn das Einfügen danach scheitert.
    /// </summary>
    DragDropEffects DropEffect(DragEventArgs e)
    {
        if (items.Length == 0 || CanAcceptDrop?.Invoke() == false) return DragDropEffects.None;
        if (OwnPages(e.Data) is not null) return DragDropEffects.Move;
        // Seiten eines anderen Fensters nicht schon beim Überfahren als Datei anfordern: das legte dort die PDF an.
        var fromFletta = e.Data.GetDataPresent(PagesFormat);
        if (!fromFletta && PdfFiles(e.Data).Length == 0) return DragDropEffects.None;
        if (fromFletta && e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey) && e.AllowedEffects.HasFlag(DragDropEffects.Move)) return DragDropEffects.Move;
        return e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    static string[] PdfFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? [.. files.Where(file => file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))]
            : [];

    /// <summary>Lücke unter dem Zeiger: vor dem Eintrag, dessen obere Hälfte er trifft.</summary>
    int GapAt(DragEventArgs e) =>
        Math.Clamp((int)Math.Round((e.GetPosition(List).Y + scrollOffset) / ItemHeight), 0, items.Length);

    void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropEffect(e);
        e.Handled = true;
        if (e.Effects == DragDropEffects.None)
        {
            DropMarker.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(DropMarker, 6);
        Canvas.SetTop(DropMarker, GapAt(e) * ItemHeight - scrollOffset - DropMarker.Height / 2);
        DropMarker.Width = Math.Max(0, ActualWidth - 12 - 12); // ohne Bildlaufleiste
        DropMarker.Visibility = Visibility.Visible;
        // Während des Ziehens scrollt das Mausrad nicht: am Rand der Leiste rollt sie selbst weiter.
        var y = e.GetPosition(List).Y;
        var step = y < DropScrollEdge ? -DropScrollStep : y > List.ActualHeight - DropScrollEdge ? DropScrollStep : 0;
        if (step != 0 && List.Template.FindName("ListScroller", List) is ScrollViewer scroller)
            scroller.ScrollToVerticalOffset(scrollOffset + step);
    }

    void OnListDragLeave(object sender, DragEventArgs e) => DropMarker.Visibility = Visibility.Collapsed;

    void OnListDrop(object sender, DragEventArgs e)
    {
        DropMarker.Visibility = Visibility.Collapsed;
        e.Handled = true; // sonst öffnete das Fenster die Datei zusätzlich in einem neuen Fenster
        e.Effects = DropEffect(e);
        if (e.Effects == DragDropEffects.None) return;
        var gap = GapAt(e);
        if (OwnPages(e.Data) is { } pages)
        {
            PagesDropped?.Invoke(pages, gap);
            return;
        }
        var files = PdfFiles(e.Data);
        if (files.Length == 0)
        {
            e.Effects = DragDropEffects.None; // die Quelle konnte ihre Seiten nicht liefern: dort nichts löschen
            return;
        }
        FilesDropped?.Invoke(files, gap);
    }

    /// <summary>Sichtbare Miniaturen zuerst, dann vorab in Scrollrichtung; was schon passend da ist, fehlt.</summary>
    public IEnumerable<RenderRequest> Wanted()
    {
        if (items.Length == 0 || boxSize <= 0 || !IsVisible) return [];
        return PrefetchOrder(visible.First, visible.Last, items.Length, scrollingUp)
            .Where(page => !items[page].Failed)
            .Select(RequestFor)
            .Where(request => items[request.Page].Request != request);
    }

    /// <summary>
    /// Sichtbare Einträge, dann PrefetchAhead in Scrollrichtung, dann PrefetchBehind dagegen, je nach Abstand;
    /// innerhalb von 0..count. Im Selbsttest geprüft.
    /// </summary>
    public static IEnumerable<int> PrefetchOrder(int first, int last, int count, bool up)
    {
        if (count == 0 || last < first) return [];
        var (before, after) = up ? (PrefetchAhead, PrefetchBehind) : (PrefetchBehind, PrefetchAhead);
        int Distance(int page) => page < first ? first - page : page > last ? page - last : 0;
        return Enumerable.Range(first - before, last - first + 1 + before + after)
            .Where(page => page >= 0 && page < count)
            .OrderBy(page => Distance(page) == 0 ? 0 : (page < first) == up ? 1 : 2)
            .ThenBy(Distance);
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
