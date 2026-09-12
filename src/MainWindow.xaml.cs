using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Fletta.Pdfium;
using Microsoft.Win32;

namespace Fletta;

public partial class MainWindow : Window
{
    const int KeepNeighbours = 2;             // so viele Seiten über und unter dem Sichtbaren bleiben gerendert
    const long MaxPixelsPerPage = 24_000_000; // ~96 MB je Seite; darüber skaliert WPF hoch
    const int WheelNotch = 120;               // Mausrad-Rastung; Touchpads liefern Bruchteile davon
    const double CopyDpi = 200;               // Bild in der Zwischenablage: scharf genug für Mail und Word
    static readonly int[] ZoomMenuSteps = [.. PageLayout.ZoomSteps.Where(z => z >= 50 && z % 25 == 0)];

    /// <summary>Eine Zeile der Gliederung; Indent rückt nach Tiefe ein.</summary>
    sealed record OutlineRow(string Title, string PageLabel, Thickness Indent, int Page);

    sealed record Shortcut(string Keys, string Action);

    static readonly Shortcut[] Shortcuts =
    [
        new("R / L", "rechts / links drehen – markierte Miniaturen, sonst die aktuelle Seite (nur Ansicht)"),
        new("+ / −, Strg+Mausrad", "Zoom in Stufen"),
        new("Strg+0 / 1 / 2", "ganze Seite / 100 % / Seitenbreite"),
        new("← / →", "vorige / nächste Seite (bei Doppelseite ein Paar)"),
        new("Pos1 / Ende", "erste / letzte Seite"),
        new("Bild↑ / Bild↓, Leertaste", "scrollen, seitenweise: blättern"),
        new("B", "Doppelseite ein/aus"),
        new("S", "seitenweise blättern ein/aus"),
        new("F4", "Seitenleiste ein/aus"),
        new("Strg+A", "alle Seiten markieren"),
        new("Strg+C", "Seite als Bild und Text kopieren"),
        new("Strg+Umschalt+C", "nur den Text kopieren"),
        new("Strg+P", "drucken"),
        new("Strg+O", "öffnen (neues Fenster, wenn schon eines offen ist)"),
        new("F1 oder ?", "diese Übersicht"),
        new("Esc", "Übersicht oder Auswahl schließen, sonst Fenster schließen"),
    ];

    /// <summary>Ein Seitenelement auf der Leinwand, wird beim Scrollen wiederverwendet.</summary>
    sealed class PageSlot
    {
        public readonly Image Image = new() { Stretch = Stretch.Fill };
        public readonly TextBlock Status = new()
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
        };
        public readonly Border Frame;

        public PageSlot() => Frame = new Border { Background = Brushes.White, Child = new Grid { Children = { Image, Status } } };
    }

    readonly Dictionary<int, PageSlot> slots = [];
    readonly Stack<PageSlot> spareSlots = [];
    readonly Dictionary<int, RenderResult> rendered = []; // nur erfolgreiche: Bild samt Auftrag, zu dem es gehört
    readonly Dictionary<int, string> pageErrors = [];
    bool measurePending;
    bool wasMaximized;     // letzter Zustand vor dem Minimieren, den merkt sich Fletta
    bool outlineRequested; // erstes Ergebnis der Hauptansicht ist da: jetzt Gliederung und Miniaturen
    PageRenderer? renderer;
    IReadOnlyList<Size> pagesPt = [];
    int[] quarterTurns = []; // Drehung je Seite in Vierteln im Uhrzeigersinn, nur Ansicht
    PageLayout? layout;
    ZoomMode zoomMode = ZoomMode.FitWidth;
    int zoomPercent = 100;
    int currentPage;
    int titlePage = -1; // Seite, die gerade im Fenstertitel steht
    RenderRequest[] mainWanted = []; // Aufträge der Hauptansicht; die Miniaturen kommen dahinter
    bool sidebarHidden;               // F4
    GridLength sidebarWidth = new(220);
    double? pinnedOffset; // Offset, an dem currentPage festgehalten ist (Sprung, Zoom, Drehen); verfällt beim Scrollen
    bool dragging;        // Ansicht wird gerade mit der Maus geschoben
    Point dragFrom;       // Mausstelle beim Anfassen, im Scroller gemessen
    Vector dragOffset;    // Bildlaufstand beim Anfassen
    int wheelZoomDelta;
    bool spreads;          // B: Doppelseite
    bool paged;            // S: seitenweise blättern
    long flipLockedUntil;  // seitenweise: schluckt den Nachlauf von Touchpads (Environment.TickCount64)
    string fileName = "";
    int printing;          // laufende Druckdialoge/-aufträge; solange bleibt der Renderer stehen
    bool closeAfterPrint;  // Fenster wurde während des Drucks geschlossen und ist nur versteckt
    DpiScale dpi = new(1, 1);
    readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow(string? path, bool measure)
    {
        // Vor dem Fensteraufbau: PDFium laden und das Dokument öffnen laufen so parallel dazu.
        if (path is not null) renderer = new PageRenderer(path, Dispatcher, Deliver);
        InitializeComponent();
        measurePending = measure;
        ApplySettings(AppSettings.Load());
        BuildZoomMenu();
        HelpKeys.ItemsSource = Shortcuts;
        noticeTimer.Tick += (_, _) =>
        {
            noticeTimer.Stop();
            NoticePill.Visibility = Visibility.Collapsed;
        };
        Thumbs.PageClicked += GoTo;
        Thumbs.WantedChanged += RequestRenders;
        SetDocumentControls(false);
        SourceInitialized += (_, _) =>
        {
            ApplyDarkCaption();
            dpi = VisualTreeHelper.GetDpi(this);
            Thumbs.Dpi = dpi;
        };
        DpiChanged += (_, e) =>
        {
            dpi = e.NewDpi; // nicht belegt, dass GetDpi hier schon den neuen Wert liefert
            Thumbs.Dpi = dpi;
            UpdateView();
        };
        ContentRendered += (_, _) => App.Mark("fenster");
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) wasMaximized = WindowState == WindowState.Maximized; };
        Loaded += (_, _) => Scroller.Focus(); // Pfeil- und Bildtasten scrollen dann nativ
        Scroller.ScrollChanged += OnScrollChanged;

        if (path is not null) ShowDocument(path);
        else
        {
            ShowMessage("PDF hierher ziehen oder mit Strg+O öffnen", "Jede Datei öffnet in einem eigenen Fenster.");
            FinishMeasure();
        }
    }

    async void ShowDocument(string path)
    {
        var opening = renderer!;
        fileName = Path.GetFileName(path);
        Title = $"{fileName} – Fletta";
        ShowLoadingAfterDelay(opening);
        try
        {
            pagesPt = await opening.Opened;
        }
        catch (PdfException e)
        {
            CloseDocument();
            ShowMessage($"„{fileName}“ lässt sich nicht öffnen", e.Message);
            FinishMeasure();
            return;
        }
        if (renderer != opening) return; // Fenster inzwischen geschlossen
        App.Mark("dokument");
        if (pagesPt.Count == 0)
        {
            CloseDocument();
            ShowMessage($"„{fileName}“ hat keine Seiten", "Die Datei ist ein gültiges PDF, enthält aber nichts zum Anzeigen.");
            FinishMeasure();
            return;
        }
        quarterTurns = new int[pagesPt.Count];
        MessagePanel.Visibility = Visibility.Collapsed;
        SetDocumentControls(true);
        ShowOutlineMessage("Gliederung wird gelesen …");
        Thumbs.Show(pagesPt, quarterTurns, sidebarWidth.Value);
        App.Mark("leiste");
        ApplyZoom();
    }

    async void ShowLoadingAfterDelay(PageRenderer opening)
    {
        await Task.Delay(250); // schnelles Öffnen soll nicht flackern
        if (renderer == opening && !opening.Opened.IsCompleted) ShowMessage($"Öffne „{fileName}“ …", "");
    }

    void CloseDocument()
    {
        renderer?.Dispose();
        renderer = null;
        layout = null;
        pagesPt = [];
        quarterTurns = [];
        titlePage = -1;
        pinnedOffset = null;
        outlineRequested = false;
        slots.Clear();
        spareSlots.Clear();
        rendered.Clear();
        pageErrors.Clear();
        mainWanted = [];
        PageCanvas.Children.Clear();
        Thumbs.Clear();
        ShowOutline([]);
        SetDocumentControls(false);
    }

    /// <summary>Gemerkte Fensterlage und Ansicht übernehmen; Zoom und Modi greifen beim ersten Layout.</summary>
    void ApplySettings(AppSettings settings)
    {
        Width = settings.Width;
        Height = settings.Height;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (settings.FitsOn(screen))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top) = (settings.Left, settings.Top);
        }
        if (settings.Maximized) WindowState = WindowState.Maximized;
        wasMaximized = settings.Maximized;
        sidebarWidth = new GridLength(Math.Clamp(settings.SidebarWidth, SideColumn.MinWidth, SideColumn.MaxWidth));
        sidebarHidden = settings.SidebarHidden;
        (zoomMode, zoomPercent) = (settings.Zoom, settings.ZoomPercent);
        (spreads, paged) = (settings.Spreads, settings.Paged);
        SpreadsButton.IsChecked = spreads;
        PagedButton.IsChecked = paged;
    }

    AppSettings CurrentSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new AppSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = wasMaximized, // aus dem Minimieren heraus geschlossen: der Zustand davor
            SidebarWidth = SideColumn.Width.Value > 0 ? SideColumn.Width.Value : sidebarWidth.Value,
            SidebarHidden = sidebarHidden,
            Zoom = zoomMode,
            ZoomPercent = zoomPercent,
            Spreads = spreads,
            Paged = paged,
        };
    }

    /// <summary>Knöpfe und Bodenleisten, die nur mit offenem Dokument etwas tun.</summary>
    void SetDocumentControls(bool enabled)
    {
        foreach (var button in new ButtonBase[] { SidebarButton, PrintButton, CopyButton, RotateLeftButton, RotateRightButton,
                                                  FitWidthButton, FitPageButton, SpreadsButton, PagedButton })
            button.IsEnabled = enabled;
        ZoomPill.Visibility = PagePill.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplySidebar(enabled);
    }

    /// <summary>Seitenleiste nur mit Dokument und solange F4 sie nicht ausgeblendet hat.</summary>
    void ApplySidebar(bool hasDocument)
    {
        var show = hasDocument && !sidebarHidden;
        if (!show && SideColumn.Width.Value > 0) sidebarWidth = SideColumn.Width; // gezogene Breite merken
        SideColumn.MinWidth = show ? 140 : 0;
        SideColumn.Width = show ? sidebarWidth : new GridLength(0);
        SidePanel.Visibility = SideSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarButton.IsChecked = show;
    }

    /// <summary>Lesezeichen auf dem Render-Thread lesen; fehlen sie oder scheitert es, zeigt der Reiter den Hinweis.</summary>
    async void LoadOutline(PageRenderer opening)
    {
        IReadOnlyList<OutlineEntry> outline;
        try
        {
            outline = await opening.Invoke(document => document.Outline());
        }
        catch (Exception e) when (e is PdfException or TaskCanceledException)
        {
            outline = [];
        }
        if (renderer == opening) ShowOutline(outline);
    }

    void ShowOutline(IReadOnlyList<OutlineEntry> outline)
    {
        // ponytail: flach mit Einrückung statt Baum zum Auf- und Zuklappen; reicht für typische Handbücher.
        OutlineList.ItemsSource = outline
            .Select(entry => new OutlineRow(entry.Title, entry.Page >= 0 ? (entry.Page + 1).ToString() : "",
                                            new Thickness(entry.Depth * 14, 0, 0, 0), entry.Page))
            .ToList();
        if (outline.Count == 0) ShowOutlineMessage("Diese Datei hat keine Gliederung (keine Lesezeichen im PDF).");
        else OutlineEmpty.Visibility = Visibility.Collapsed;
    }

    /// <summary>Hinweis statt Liste: beim Lesen und wenn das PDF keine Lesezeichen hat.</summary>
    void ShowOutlineMessage(string text)
    {
        OutlineList.ItemsSource = null;
        OutlineEmpty.Text = text;
        OutlineEmpty.Visibility = Visibility.Visible;
    }

    void ShowSideTab(bool outline)
    {
        PagesTab.IsChecked = !outline;
        OutlineTab.IsChecked = outline;
        Thumbs.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
        OutlinePanel.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnSidebarClick(object sender, RoutedEventArgs e) => ToggleSidebar();
    void OnPagesTabClick(object sender, RoutedEventArgs e) => ShowSideTab(outline: false);
    void OnOutlineTabClick(object sender, RoutedEventArgs e) => ShowSideTab(outline: true);

    void OnOutlineClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is OutlineRow { Page: >= 0 } row) GoTo(row.Page);
    }

    void ToggleHelp() => HelpOverlay.Visibility = HelpOverlay.IsVisible ? Visibility.Collapsed : Visibility.Visible;

    void OnHelpClick(object sender, RoutedEventArgs e) => ToggleHelp();

    void OnHelpOverlayClick(object sender, MouseButtonEventArgs e) => HelpOverlay.Visibility = Visibility.Collapsed;

    void ToggleSidebar()
    {
        sidebarHidden = !sidebarHidden;
        ApplySidebar(layout is not null);
    }

    /// <summary>Seitengrößen, wie sie gerade angezeigt werden: gedrehte Seiten mit getauschten Kanten.</summary>
    Size[] ShownSizes() => [.. Enumerable.Range(0, pagesPt.Count).Select(ShownSize)];

    Size ShownSize(int page) => quarterTurns[page] % 2 == 1 ? new Size(pagesPt[page].Height, pagesPt[page].Width) : pagesPt[page];

    /// <summary>Baut die Anordnung neu und hält dabei die Stelle, an der man gerade liest.</summary>
    void ApplyZoom()
    {
        if (renderer is null || pagesPt.Count == 0 || Scroller.ViewportWidth <= 0) return;
        var old = layout;
        var viewport = new Size(Scroller.ViewportWidth, Scroller.ViewportHeight);
        var centerX = old is null ? 0.5 : (Scroller.HorizontalOffset + viewport.Width / 2) / PageCanvas.Width;

        var shown = ShownSizes();
        layout = new PageLayout(shown, PageLayout.ScaleFor(zoomMode, zoomPercent, shown, currentPage, viewport, spreads),
                                spreads, paged ? viewport.Height : 0);
        PageCanvas.Width = Math.Max(layout.Width, viewport.Width);
        PageCanvas.Height = layout.Height;
        ZoomButton.Content = ZoomLabel();
        // UpdateView erst nach dem Layout-Durchgang: vorher gilt noch der alte Offset, dann würfe es
        // die Bilder der sichtbaren Seiten weg und höbe das Festhalten unten gleich wieder auf.
        // Eingeplant statt über ScrollChanged: bei 180° und gleichem Maßstab kommt keines.
        Dispatcher.InvokeAsync(UpdateView, DispatcherPriority.Loaded);
        if (old is null) return;
        // Nach Zoom und Drehen bleibt die aktuelle Seite stehen, bis jemand selbst scrollt: der
        // neue Offset wird festgehalten. Sonst wanderte der Messpunkt von CurrentPage mit der
        // geänderten Höhe weiter, und das nächste R träfe die Nachbarseite.
        var target = layout.KeepPosition(old, Scroller.VerticalOffset, currentPage, viewport.Height);
        pinnedOffset = target;
        Scroller.ScrollToVerticalOffset(target);
        Scroller.ScrollToHorizontalOffset(centerX * PageCanvas.Width - viewport.Width / 2);
    }

    string ZoomLabel()
    {
        var percent = (int)Math.Round(layout!.Percent);
        return zoomMode switch
        {
            ZoomMode.FitWidth => $"Seitenbreite · {percent} %",
            ZoomMode.FitPage => $"Ganze Seite · {percent} %",
            _ => $"{percent} %",
        };
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || (e.ViewportHeightChange != 0 && (zoomMode == ZoomMode.FitPage || paged)))
            ApplyZoom(); // plant UpdateView selbst ein
        else
            UpdateView();
    }

    /// <summary>Setzt Elemente für die sichtbaren Seiten und bestellt die fehlenden Bilder.</summary>
    void UpdateView()
    {
        if (layout is null || renderer is null) return;
        App.Mark("updateview");
        var top = Scroller.VerticalOffset;
        var (first, last) = layout.VisiblePages(top, top + Scroller.ViewportHeight);

        foreach (var page in slots.Keys.Where(p => p < first || p > last).ToList())
        {
            slots[page].Frame.Visibility = Visibility.Collapsed;
            slots[page].Image.Source = null; // sonst hält der Vorrat die Bitmap fest, auch wenn rendered sie vergessen hat
            spareSlots.Push(slots[page]);
            slots.Remove(page);
        }
        for (var page = first; page <= last; page++)
        {
            if (!slots.TryGetValue(page, out var slot)) slots[page] = slot = TakeSlot();
            Place(slot, page);
        }
        foreach (var page in rendered.Keys.Where(p => p < first - KeepNeighbours || p > last + KeepNeighbours).ToList())
            rendered.Remove(page);

        mainWanted = Wanted(first, last);
        RequestRenders();
        // Festgehalten (Sprung, Zoom, Drehen): die Seite bleibt aktuell, bis jemand selbst scrollt —
        // auch wenn sie am Dokumentende nicht nach oben rollen kann.
        // Gegen das tatsächlich erreichbare Ziel: verschwindet beim Zoomen die waagrechte Leiste,
        // klemmt der ScrollViewer mit der neuen Sichthöhe anders als ApplyZoom vorher.
        if (pinnedOffset is { } target && Math.Abs(top - Math.Min(target, Scroller.ScrollableHeight)) < 1) SetCurrentPage(currentPage);
        else
        {
            pinnedOffset = null;
            SetCurrentPage(layout.CurrentPage(top, Scroller.ViewportHeight));
        }
    }

    PageSlot TakeSlot()
    {
        if (spareSlots.TryPop(out var slot)) return slot;
        slot = new PageSlot();
        PageCanvas.Children.Add(slot.Frame);
        return slot;
    }

    void Place(PageSlot slot, int page)
    {
        var size = layout!.SizeOf(page);
        Canvas.SetLeft(slot.Frame, layout.Left(page, PageCanvas.Width));
        Canvas.SetTop(slot.Frame, layout.Top(page));
        slot.Frame.Width = size.Width;
        slot.Frame.Height = size.Height;
        if (rendered.TryGetValue(page, out var shown))
        {
            // Ein Bild in alter Größe wird gestreckt gezeigt, eines in alter Drehung von WPF gedreht,
            // bis das scharfe kommt: so wirken Zoom, R und L sofort.
            slot.Image.Source = shown.Bitmap;
            var turns = (quarterTurns[page] - shown.Request.QuarterTurns + 4) % 4;
            slot.Image.LayoutTransform = turns == 0 ? Transform.Identity : new RotateTransform(turns * 90);
        }
        else
        {
            slot.Image.Source = null;
            slot.Image.LayoutTransform = Transform.Identity;
        }
        slot.Status.Text = pageErrors.TryGetValue(page, out var error) ? $"Seite {page + 1} lässt sich nicht anzeigen.\n{error}" : "";
        slot.Frame.Visibility = Visibility.Visible;
    }

    /// <summary>Sichtbare Seiten zuerst, von der Mitte aus; dann die Nachbarn. Was schon scharf da ist, fehlt.</summary>
    RenderRequest[] Wanted(int first, int last)
    {
        var centre = (first + last) / 2.0;
        return Enumerable.Range(first - KeepNeighbours, last - first + 1 + 2 * KeepNeighbours)
            .Where(p => p >= 0 && p < layout!.Count && !pageErrors.ContainsKey(p))
            .OrderBy(p => p < first || p > last)
            .ThenBy(p => Math.Abs(p - centre))
            .Select(RequestFor)
            .Where(r => !IsSharp(r))
            .ToArray();
    }

    /// <summary>
    /// Hauptansicht zuerst, dahinter die Miniaturen — beide über denselben Render-Thread. Was seit dem
    /// letzten UpdateView scharf angekommen oder fehlgeschlagen ist, fällt heraus; sonst rendert der
    /// Thread beim Scrollen der Seitenleiste die Hauptseiten immer wieder neu. Miniaturen erst nach
    /// dem ersten Ergebnis der Hauptansicht: ihre Wünsche kommen beim ersten Layout früher an und
    /// hielten die erste Seite sonst um eine Miniatur auf.
    /// </summary>
    void RequestRenders() =>
        renderer?.Request([.. mainWanted.Where(r => !IsSharp(r) && !pageErrors.ContainsKey(r.Page)),
                           .. outlineRequested ? Thumbs.Wanted() : []]);

    /// <summary>Liegt für die Seite schon ein Bild zu genau diesem Auftrag vor (Größe, DPI, Drehung)?</summary>
    bool IsSharp(RenderRequest wanted) => rendered.TryGetValue(wanted.Page, out var r) && r.Request == wanted;

    // ponytail: ganze Seiten statt Kacheln. Über MaxPixelsPerPage wird unscharf; Kacheln erst, wenn das stört.
    RenderRequest RequestFor(int page)
    {
        var size = layout!.SizeOf(page);
        double width = size.Width * dpi.DpiScaleX, height = size.Height * dpi.DpiScaleY;
        var shrink = Math.Min(1, Math.Sqrt(MaxPixelsPerPage / (width * height)));
        return new(page, PageLayout.ToPixels(width * shrink), PageLayout.ToPixels(height * shrink),
                   dpi.PixelsPerInchX * shrink, dpi.PixelsPerInchY * shrink, quarterTurns[page]);
    }

    void Deliver(RenderResult result)
    {
        if (layout is null) return;
        if (result.Request.Thumbnail)
        {
            Thumbs.Deliver(result);
            return;
        }
        var page = result.Request.Page;
        var wanted = RequestFor(page);
        // Aus einer älteren Zoomstufe, während die passende Größe schon da ist: sonst überschreibt
        // ein 110-%-Bild das scharfe 100-%-Bild, nachdem man schnell wieder zurückgezoomt hat.
        if (result.Request != wanted && IsSharp(wanted)) return;

        if (result.Bitmap is not null) rendered[page] = result;
        else pageErrors[page] = result.Error ?? "";
        if (slots.TryGetValue(page, out var slot)) Place(slot, page);
        if (result.Bitmap is not null) App.Mark("seite");
        if (!outlineRequested)
        {
            // Erst jetzt: die Gliederung läuft als Aufgabe vor den Bildaufträgen und hielte bei einem
            // Handbuch mit vielen Lesezeichen sonst die erste Seite auf.
            outlineRequested = true;
            LoadOutline(renderer!);
            RequestRenders(); // jetzt auch die Miniaturen
        }
        FinishMeasure();
    }

    /// <summary>--measure: nach dem ersten Ergebnis berichten und schließen, auch im Fehlerfall — sonst hängt measure.ps1.</summary>
    void FinishMeasure()
    {
        if (!measurePending) return;
        measurePending = false;
        Dispatcher.InvokeAsync(ReportAndClose, DispatcherPriority.ApplicationIdle); // nach dem nächsten Bild
    }

    void SetCurrentPage(int page)
    {
        currentPage = page;
        if (page == titlePage) return;
        titlePage = page;
        Thumbs.SetCurrent(page);
        Title = $"{fileName} · {page + 1}/{layout!.Count} – Fletta";
        PageCount.Text = $"/ {layout.Count}";
        if (!PageBox.IsKeyboardFocused) PageBox.Text = (page + 1).ToString();
    }

    void Step(int direction)
    {
        if (layout is null) return;
        var origin = layout.StepOrigin(Scroller.VerticalOffset, Scroller.ViewportHeight, currentPage, pinnedOffset is not null);
        GoTo(layout.NextRowPage(origin, direction)); // Doppelseite: ein Paar weiter
    }

    void GoTo(int page)
    {
        if (layout is null) return;
        currentPage = Math.Clamp(page, 0, layout.Count - 1);
        if (zoomMode == ZoomMode.FitPage) ApplyZoom(); // die Zielseite kann anders groß sein
        pinnedOffset = layout.ScrollTargetFor(currentPage, Scroller.ViewportHeight);
        Scroller.ScrollToVerticalOffset(pinnedOffset.Value);
        SetCurrentPage(currentPage); // sofort: steht das Ziel schon am Offset, kommt kein ScrollChanged
    }

    /// <summary>
    /// R / L: dreht um 90°, nur in der Ansicht — die Datei bleibt unverändert. Sind mehrere
    /// Miniaturen markiert (Strg/Umschalt-Klick, Strg+A), alle markierten, sonst die aktuelle Seite.
    /// </summary>
    void Rotate(int quarters)
    {
        if (layout is null) return;
        var selected = Thumbs.SelectedPages;
        foreach (var page in selected.Length > 1 ? selected : [currentPage])
        {
            quarterTurns[page] = (quarterTurns[page] + quarters + 4) % 4;
            Thumbs.Refresh(page);
        }
        ApplyZoom();
    }

    /// <summary>B und S: Anordnung wechseln, die aktuelle Seite bleibt oben.</summary>
    void SetViewMode(bool spreads, bool paged)
    {
        if (layout is null) return;
        (this.spreads, this.paged) = (spreads, paged);
        SpreadsButton.IsChecked = spreads;
        PagedButton.IsChecked = paged;
        var page = currentPage;
        ApplyZoom();
        GoTo(page);
    }

    void SetZoom(ZoomMode mode, int percent = 100)
    {
        if (layout is null) return;
        zoomMode = mode;
        zoomPercent = percent;
        ApplyZoom();
    }

    void ZoomStep(int direction)
    {
        if (layout is not null) SetZoom(ZoomMode.Fixed, PageLayout.NextZoom(layout.Percent, direction));
    }

    void BuildZoomMenu()
    {
        AddZoomItem("Seitenbreite", ZoomMode.FitWidth, 100);
        AddZoomItem("Ganze Seite", ZoomMode.FitPage, 100);
        foreach (var percent in ZoomMenuSteps) AddZoomItem($"{percent} %", ZoomMode.Fixed, percent);
    }

    void AddZoomItem(string text, ZoomMode mode, int percent)
    {
        var item = new Button { Content = text, Style = (Style)FindResource("MenuButton") };
        item.Click += (_, _) =>
        {
            ZoomMenu.IsOpen = false;
            SetZoom(mode, percent);
        };
        ZoomMenuItems.Children.Add(item);
    }

    /// <summary>Strg+P: moderner Windows-Druckdialog mit echter Vorschau; PDFium rendert auf dem Render-Thread.</summary>
    async void Print()
    {
        if (layout is null || renderer is null) return;
        // Eine einzelne markierte Miniatur ist nur der Klickfokus, erst ab zwei ist es eine Auswahl.
        var selected = Thumbs.SelectedPages;
        var job = new PrintJob(renderer, pagesPt, (int[])quarterTurns.Clone(), fileName,
                               selected.Length > 1 ? PrintJob.RunsOf(selected) : [], ShowNotice); // Stand beim Klick
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        printing++;
        try
        {
            await SystemPrint.ShowAsync(hwnd, job);
        }
        catch (Exception e) // Drucker sind Fremdgeräte: jeder Fehler wird Meldung, nie Absturz
        {
            ShowNotice($"Drucken fehlgeschlagen: {e.Message}");
        }
        finally
        {
            if (--printing == 0 && closeAfterPrint) Close(); // das Schließen war nur aufgeschoben
        }
    }

    /// <summary>
    /// Strg+C: aktuelle Seite als Bild (200 dpi, wie angezeigt gedreht) und Text in die Zwischenablage;
    /// Strg+Umschalt+C nur den Text. Ein Ziel nimmt sich das Format, das es kann.
    /// </summary>
    async void CopyPage(bool textOnly)
    {
        if (layout is null || renderer is null) return;
        var page = currentPage;
        var turns = quarterTurns[page];
        var shown = ShownSize(page);
        double width = shown.Width / 72 * CopyDpi, height = shown.Height / 72 * CopyDpi;
        var shrink = Math.Min(1, Math.Sqrt(MaxPixelsPerPage / (width * height)));
        try
        {
            var (text, image) = await renderer.Invoke(document => (document.PageText(page), textOnly ? null :
                document.Render(page, PageLayout.ToPixels(width * shrink), PageLayout.ToPixels(height * shrink),
                                CopyDpi * shrink, CopyDpi * shrink, turns)));
            if (textOnly && text.Length == 0)
            {
                ShowNotice($"Seite {page + 1} hat keinen Text (Scan?) – nichts kopiert");
                return;
            }
            var data = new DataObject();
            if (text.Length > 0) data.SetText(text, TextDataFormat.UnicodeText);
            if (image is not null) data.SetImage(image);
            Clipboard.SetDataObject(data, copy: true);
            ShowNotice(textOnly ? $"Text von Seite {page + 1} kopiert"
                : text.Length > 0 ? $"Seite {page + 1} kopiert: Bild und Text" : $"Seite {page + 1} kopiert: Bild (kein Text, Scan?)");
        }
        catch (Exception e) when (e is PdfException or ExternalException or TaskCanceledException)
        {
            // ExternalException: die Zwischenablage ist gerade von einem anderen Programm belegt.
            ShowNotice($"Kopieren fehlgeschlagen: {e.Message}");
        }
    }

    void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticePill.Visibility = Visibility.Visible;
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    void ShowMessage(string title, string text)
    {
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Ein Fenster pro PDF: ein leeres Fenster übernimmt die Datei, sonst startet ein neues.</summary>
    void Open(string path)
    {
        if (renderer is null)
        {
            renderer = new PageRenderer(path, Dispatcher, Deliver);
            ShowDocument(path);
        }
        else Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { path } });
    }

    void PickFile()
    {
        var dialog = new OpenFileDialog { Filter = "PDF-Dateien|*.pdf|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) == true) Open(dialog.FileName);
    }

    void OnOpenClick(object sender, RoutedEventArgs e) => PickFile();
    void OnPrintClick(object sender, RoutedEventArgs e) => Print();
    void OnCopyClick(object sender, RoutedEventArgs e) => CopyPage(textOnly: false);
    void OnRotateLeftClick(object sender, RoutedEventArgs e) => Rotate(-1);
    void OnRotateRightClick(object sender, RoutedEventArgs e) => Rotate(+1);
    void OnFitWidthClick(object sender, RoutedEventArgs e) => SetZoom(ZoomMode.FitWidth);
    void OnFitPageClick(object sender, RoutedEventArgs e) => SetZoom(ZoomMode.FitPage);
    void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomStep(-1);
    void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomStep(+1);
    void OnZoomMenuClick(object sender, RoutedEventArgs e) => ZoomMenu.IsOpen = true;
    void OnSpreadsClick(object sender, RoutedEventArgs e) => SetViewMode(!spreads, paged);
    void OnPagedClick(object sender, RoutedEventArgs e) => SetViewMode(spreads, !paged);
    void OnPrevClick(object sender, RoutedEventArgs e) => Step(-1);
    void OnNextClick(object sender, RoutedEventArgs e) => Step(+1);

    void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Enter && int.TryParse(PageBox.Text, out var number)) GoTo(number - 1);
        Scroller.Focus(); // der Fokuswechsel schreibt die aktuelle Seite zurück ins Feld
        e.Handled = true;
    }

    void OnPageBoxFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    void OnPageBoxBlur(object sender, KeyboardFocusChangedEventArgs e) => PageBox.Text = (currentPage + 1).ToString();

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var file in files) Open(file);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Im Seitenfeld gehören die Tasten dem Feld; Enter und Esc behandelt OnPageBoxKeyDown.
        if (PageBox.IsKeyboardFocused)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var plain = Keyboard.Modifiers == ModifierKeys.None;
        var ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape when dragging: EndDrag(); break;
            case Key.Escape when ZoomMenu.IsOpen: ZoomMenu.IsOpen = false; break;
            case Key.Escape when HelpOverlay.IsVisible: ToggleHelp(); break;
            case Key.F1 when plain: ToggleHelp(); break;
            case Key.Escape: if (!Thumbs.ClearSelection()) Close(); break; // erst die Mehrfachauswahl
            case Key.F4 when plain: ToggleSidebar(); break;
            case Key.B when plain: SetViewMode(!spreads, paged); break;
            case Key.S when plain: SetViewMode(spreads, !paged); break;
            // Seitenweise gehen Bild↓/Leertaste/Bild↑ bis zum Zeilenrand und dann eine Zeile weiter.
            case Key.PageDown or Key.Space when plain && paged: PagedKey(+1); break;
            case Key.PageUp when plain && paged: PagedKey(-1); break;
            case Key.A when ctrl: Thumbs.SelectAll(); break;
            case Key.O when ctrl: PickFile(); break;
            case Key.P when ctrl: Print(); break;
            case Key.C when ctrl: CopyPage(textOnly: false); break;
            case Key.C when ctrlShift: CopyPage(textOnly: true); break;
            case Key.R when plain: Rotate(+1); break;
            case Key.L when plain: Rotate(-1); break;
            case Key.OemPlus or Key.Add when plain: ZoomStep(+1); break;
            case Key.OemMinus or Key.Subtract when plain: ZoomStep(-1); break;
            case Key.D0 or Key.NumPad0 when ctrl: SetZoom(ZoomMode.FitPage); break;
            case Key.D1 or Key.NumPad1 when ctrl: SetZoom(ZoomMode.Fixed, 100); break;
            case Key.D2 or Key.NumPad2 when ctrl: SetZoom(ZoomMode.FitWidth); break;
            case Key.Right when plain: Step(+1); break;
            case Key.Left when plain: Step(-1); break;
            // Selbst behandelt statt der ScrollViewer-Vorgabe: die greift nur mit Tastaturfokus,
            // und Pos1/Ende sollen über GoTo laufen, damit am Dokumentende die Seite stimmt.
            case Key.Home when plain: GoTo(0); break;
            case Key.End when plain: GoTo(pagesPt.Count - 1); break;
            case Key.PageDown or Key.Space when plain: Scroller.PageDown(); break;
            case Key.PageUp when plain: Scroller.PageUp(); break;
            case Key.Space when Keyboard.Modifiers == ModifierKeys.Shift: Scroller.PageUp(); break;
            case Key.Down when plain: Scroller.LineDown(); break;
            case Key.Up when plain: Scroller.LineUp(); break;
            default: base.OnPreviewKeyDown(e); return;
        }
        e.Handled = true;
    }

    /// <summary>„?“ liegt je nach Tastatur auf verschiedenen Tasten (deutsch: Umschalt+ß) — daher über den Text.</summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (e.Text == "?" && !PageBox.IsKeyboardFocused)
        {
            ToggleHelp();
            e.Handled = true;
            return;
        }
        base.OnPreviewTextInput(e);
    }

    /// <summary>
    /// Ziehen zum Scrollen: anfassen nur auf der Seitenfläche — die Bildlaufleisten liegen außerhalb
    /// des Sichtfelds und behalten ihr gewohntes Verhalten. Der Druck gilt nicht als behandelt, ein
    /// einfacher Klick holt sich also wie bisher nur den Tastaturfokus.
    /// </summary>
    void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Scroller);
        if (layout is null || point.X >= Scroller.ViewportWidth || point.Y >= Scroller.ViewportHeight) return;
        dragging = true;
        dragFrom = point;
        dragOffset = new Vector(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        Scroller.Cursor = Cursors.Hand;
        Scroller.CaptureMouse();
    }

    /// <summary>
    /// Gegen den Anker gerechnet, damit die Seite am Zeiger klebt, auch nachdem ein Rand geklemmt hat.
    /// Gescrollt wird wie mit Rad und Tasten über den ScrollViewer, also führt OnScrollChanged die
    /// aktuelle Seite nach und pinnedOffset verfällt wie bei jedem anderen Scrollen von Hand.
    /// </summary>
    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        var point = e.GetPosition(Scroller);
        var top = dragOffset.Y - (point.Y - dragFrom.Y);
        // Seitenweise bleibt es in der Zeile der aktuellen Seite: sonst zöge die Maus am Blättern vorbei.
        if (paged)
        {
            var (rowTop, rowBottom) = layout!.RowSpan(currentPage);
            top = Math.Clamp(top, rowTop, Math.Max(rowTop, rowBottom - Scroller.ViewportHeight));
        }
        Scroller.ScrollToVerticalOffset(top);
        Scroller.ScrollToHorizontalOffset(dragOffset.X - (point.X - dragFrom.X));
    }

    void OnDragEnd(object sender, MouseButtonEventArgs e) => EndDrag();

    /// <summary>Fang verloren — Fensterwechsel, fremder Fang; das Ziehen endet dann ebenso.</summary>
    void OnDragLost(object sender, MouseEventArgs e) => EndDrag();

    /// <summary>Gegen Wiedereintritt gesichert: ReleaseMouseCapture löst LostMouseCapture aus.</summary>
    void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        Scroller.Cursor = null; // wieder der Pfeil des Fensters
        Scroller.ReleaseMouseCapture();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            // Seitenweise: nur über dem Seitenbereich, die Seitenleiste scrollt normal.
            if (paged && layout is not null && Scroller.IsMouseOver) FlipWithWheel(e);
            else base.OnPreviewMouseWheel(e);
            return;
        }
        // Touchpad-Zoom kommt als viele kleine Strg+Rad-Schritte: erst eine volle Rastung zählt.
        wheelZoomDelta += e.Delta;
        for (; Math.Abs(wheelZoomDelta) >= WheelNotch; wheelZoomDelta -= Math.Sign(wheelZoomDelta) * WheelNotch)
            ZoomStep(Math.Sign(wheelZoomDelta));
        e.Handled = true;
    }

    /// <summary>
    /// Seitenweise: ist die Zeile höher als das Fenster (stark vergrößert), darin um amount scrollen.
    /// false am Rand der Zeile — dann blättern Rad und Tasten eine Zeile weiter.
    /// </summary>
    bool ScrollWithinRow(int direction, double amount)
    {
        var (rowTop, rowBottom) = layout!.RowSpan(currentPage);
        var (offset, viewport) = (Scroller.VerticalOffset, Scroller.ViewportHeight);
        if (direction > 0 && offset + viewport < rowBottom - 1) Scroller.ScrollToVerticalOffset(Math.Min(offset + amount, rowBottom - viewport));
        else if (direction < 0 && offset > rowTop + 1) Scroller.ScrollToVerticalOffset(Math.Max(offset - amount, rowTop));
        else return false;
        return true;
    }

    /// <summary>Seitenweise Bild↓/Leertaste/Bild↑: erst den Rest einer hohen Zeile, dann eine Zeile weiter.</summary>
    void PagedKey(int direction)
    {
        if (!ScrollWithinRow(direction, Scroller.ViewportHeight * 0.9)) Step(direction);
    }

    /// <summary>
    /// Seitenweise mit dem Rad. Ein Mausrad liefert ganze Rastungen und blättert je Rastung eine
    /// Zeile. Touchpads liefern Bruchteile und schieben nach dem Wischen noch Ereignisse nach; die
    /// schluckt eine kurze Sperre, die jedes weitere Ereignis verlängert — sonst flöge ein Wisch
    /// über mehrere Seiten.
    /// </summary>
    void FlipWithWheel(MouseWheelEventArgs e)
    {
        const int FlipLockMs = 160;
        e.Handled = true;
        var direction = e.Delta < 0 ? +1 : -1;
        if (ScrollWithinRow(direction, Scroller.ViewportHeight * 0.2 * Math.Abs(e.Delta) / WheelNotch)) return;
        // Ganze Rastungen gelten als Mausrad. Ältere Touchpad-Treiber ohne Precision-Treiber senden
        // auch ganze 120er und blättern dann bei einem Wisch mehrere Seiten — in Kauf genommen, sonst
        // bremste die Sperre ein schnell gedrehtes Mausrad (Review 2026-09-10).
        if (Math.Abs(e.Delta) % WheelNotch == 0)
        {
            // Mehrere Rastungen können als ein Ereignis kommen (240, 360 …): je Rastung eine Zeile.
            for (var notch = 0; notch < Math.Abs(e.Delta) / WheelNotch; notch++) Step(direction);
            return;
        }
        var now = Environment.TickCount64;
        var locked = now < flipLockedUntil;
        flipLockedUntil = now + FlipLockMs;
        if (!locked) Step(direction);
    }

    /// <summary>Beim Schließen und beim Abmelden (App.OnSessionEnding) — nie im Messlauf.</summary>
    public void SaveSettings()
    {
        if (!App.IsMeasuring) CurrentSettings().Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveSettings();
        // Ein laufender Druck holt seine Seiten noch vom Renderer; das Fenster verschwindet nur
        // und wird geschlossen, sobald der Auftrag durch ist.
        if (printing > 0) { e.Cancel = closeAfterPrint = true; Hide(); }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseDocument();
        base.OnClosed(e);
    }

    void ReportAndClose()
    {
        App.Mark("erstes bild");
        Console.Error.WriteLine($"measure: {App.Report()} · {pagesPt.Count} Seiten");
        Close();
    }

    // Dunkle Titelleiste in der Farbe der Werkzeugleiste (ChromeBrush). Die Farbe wirkt erst ab Windows 11.
    const int DwmUseImmersiveDarkMode = 20, DwmCaptionColor = 35;
    const int CaptionColorRef = 0x001C1E1B; // #1B1E1C als COLORREF 0x00BBGGRR

    void ApplyDarkCaption()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, 1, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, CaptionColorRef, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);
}
