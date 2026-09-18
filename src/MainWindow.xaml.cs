using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    const int KeepNeighbours = 2;             // so viele Seiten über und unter dem Sichtbaren bleiben gerendert,
    const long LargePagePixels = 8_000_000;   // bei Bildern darüber (~30 MB, starker Zoom) nur eine
    const long MaxPixelsPerPage = 24_000_000; // ~91 MB je Seite; darüber skaliert WPF hoch
    const int WheelNotch = 120;               // Mausrad-Rastung; Touchpads liefern Bruchteile davon
    const double CopyDpi = 200;               // Bild in der Zwischenablage: scharf genug für Mail und Word
    static readonly int[] ZoomMenuSteps = [.. PageLayout.ZoomSteps.Where(z => z >= 50 && z % 25 == 0)];

    /// <summary>Eine Zeile der Gliederung; Indent rückt nach Tiefe ein.</summary>
    sealed record OutlineRow(string Title, string PageLabel, Thickness Indent, int Page);

    sealed record Shortcut(string Keys, string Action);

    static readonly Shortcut[] Shortcuts =
    [
        new("R / L", "rechts / links drehen – markierte Miniaturen, sonst die aktuelle Seite (Ansicht; Strg+S speichert)"),
        new("Rechtsklick auf Miniaturen", "drehen, löschen, in neue PDF kopieren oder verschieben, PDF einfügen"),
        new("Miniaturen ziehen", "umsortieren; hinaus in Explorer oder anderes Fenster kopiert (Umschalt: verschiebt)"),
        new("PDF auf die Miniaturen ziehen", "ihre Seiten an dieser Stelle einfügen"),
        new("Entf", "markierte Miniaturen löschen, sonst die aktuelle Seite"),
        new("Strg+Z", "letzte Änderung zurücknehmen"),
        new("Strg+S", "Änderungen und Drehungen in die Datei speichern"),
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
        new("Strg+F", "suchen; Enter oder F3: nächster Treffer, mit Umschalt: voriger"),
        new("Klick auf einen Link", "springt zur Seite oder öffnet die Adresse im Browser"),
        new("Anmerkungen", "Werkzeuge oben: Textmarker, Notiz, Freihand, Text, Unterschrift; Esc beendet das Werkzeug"),
        new("Rechtsklick auf Anmerkung", "Notiz bearbeiten oder Anmerkung löschen"),
        new("Klick in ein Formularfeld", "ausfüllen: Enter übernimmt, Tab springt weiter, Esc verwirft; Kästchen und Listen per Klick"),
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
        /// <summary>Treffermarken in Anteilen der Seite: 1 × 1 groß, gestreckt auf die Seite — Zoomen zeichnet nichts neu.</summary>
        public readonly Canvas Marks = new() { Width = 1, Height = 1 };
        public (int Page, int Version, int Turns) MarkedFor = (-1, -1, -1);
        public readonly Border Frame;

        public PageSlot()
        {
            // Ohne Layout-Rundung: die rundete die Marken im 1-×-1-Raum auf ganze Einheiten, also weg.
            var marks = new Viewbox { Stretch = Stretch.Fill, Child = Marks, IsHitTestVisible = false, UseLayoutRounding = false };
            Frame = new Border { Background = Brushes.White, Child = new Grid { Children = { Image, marks, Status } } };
        }
    }

    readonly Dictionary<int, PageSlot> slots = [];
    readonly Stack<PageSlot> spareSlots = [];
    readonly Dictionary<int, RenderResult> rendered = []; // nur erfolgreiche: Bild samt Auftrag, zu dem es gehört
    readonly Dictionary<int, string> pageErrors = [];
    /// <summary>Links (Stufe 2) und Anmerkungen (Stufe 3) einer Seite, gelesen im Hintergrund nach ihrem ersten Bild.</summary>
    /// <summary>Revision: pageRevisions der Seite beim Lesen; weicht sie ab, liest das nächste Bild neu (die alten gelten bis dahin).</summary>
    sealed record PageItems(IReadOnlyList<PageLink> Links, IReadOnlyList<PageAnnotation> Annotations, IReadOnlyList<FormField> Fields, int Revision);
    readonly Dictionary<int, PageItems> pageItems = [];
    readonly HashSet<int> itemsLoading = [];
    int itemsGeneration; // Seitenfolge neu (ShowPages, Schließen): laufende Antworten verfallen
    int[] pageRevisions = []; // je Seite: Anmerkungen seit dem Laden, siehe RenderRequest.Revision
    long droppedPixels; // verworfene Seitenbilder seit dem letzten Anstoß des GC, siehe Discard
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
    Release? update;       // neuere Version auf GitHub, sobald CheckForUpdate eine gefunden hat
    bool updating;         // „Aktualisieren“ lädt oder installiert: keine neue Prüfung, die den Hinweis überschriebe
    // Seitenänderungen seit dem Öffnen oder Speichern, je mit Drehung und gelesenem Text davor — Strg+Z nimmt die letzte zurück.
    readonly List<(PageEdit Edit, int[] TurnsBefore, string?[] TextsBefore)> edits = [];
    string documentPath = "";
    string? password;      // der geöffneten Datei, für jedes Neuladen (Rückgängig, Speichern)
    bool busy;             // Änderung, Rückgängig oder Speichern läuft: keine Aufträge, kein zweiter Eingriff
    bool closeConfirmed;   // Rückfrage zu ungespeicherten Änderungen ist beantwortet
    bool droppedInside;    // der eigene Zug endete in der eigenen Leiste: dort schon verschoben
    EventWaitHandle? unsavedSignal;
    Button? undoItem, saveItem;
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
        Thumbs.MenuRequested += OpenPageMenu;
        Thumbs.DragRequested += DragPagesOut;
        Thumbs.PagesDropped += (pages, gap) =>
        {
            droppedInside = true;
            MovePagesTo(pages, gap);
        };
        Thumbs.FilesDropped += InsertFiles;
        Thumbs.CanAcceptDrop = () => renderer is not null && !busy && printing == 0;
        BuildPageMenu();
        InitSearch();
        SetDocumentControls(false);
        SourceInitialized += (_, _) =>
        {
            ApplyDarkCaption(this);
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
            App.RegisterRestart(null);
            FinishMeasure();
        }
        if (!measure) CheckForUpdate();
    }

    /// <summary>
    /// Neuere Version auf GitHub? Dann bleibt oben rechts ein Hinweis mit Knopf stehen; sonst passiert nichts. force
    /// („Jetzt prüfen“) fragt an der gemerkten Antwort vorbei und meldet auch „aktuell“ und Fehler.
    /// </summary>
    async void CheckForUpdate(bool force = false)
    {
        if (updating)
        {
            if (force) ShowNotice("Das Update läuft gerade");
            return;
        }
        if (force)
        {
            ShowNotice("Suche nach Updates …");
            noticeTimer.Stop(); // bleibt stehen, bis das Ergebnis ihn ersetzt (bis zu 15 s ohne Antwort)
        }
        Release? found;
        try
        {
            found = await Updater.FindNewerAsync(AppSettings.Load().PreReleases, force);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            ShowNotice($"Update-Prüfung fehlgeschlagen: {e.Message}");
            return;
        }
        if (force) NoticePill.Visibility = Visibility.Collapsed; // „Suche nach Updates …“ ist beantwortet
        if (updating) return; // „Aktualisieren“ kam dazwischen: dessen Hinweis gilt
        update = found;
        if (update is null)
        {
            UpdatePill.Visibility = Visibility.Collapsed; // ein älterer Hinweis (etwa auf eine Vorabversion) gilt nicht mehr
            if (force) ShowNotice($"Fletta {Updater.Current} ist aktuell");
            return;
        }
        UpdateText.Text = UpdateTitle(update);
        UpdateButton.Content = "Aktualisieren";
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = UpdateCloseButton.IsEnabled = true;
        UpdatePill.Visibility = Visibility.Visible;
    }

    static string UpdateTitle(Release release) => release.PreRelease ? $"Vorabversion {release.Version} ist da" : $"Fletta {release.Version} ist da";

    /// <summary>
    /// Updates: installierte Version, Vorabversionen ein/aus, jetzt prüfen. Die Wahl gilt sofort und für alle Fenster
    /// (settings.json); einschalten prüft gleich, damit eine eben erschienene Vorabversion nicht bis zur nächsten Stunde wartet.
    /// </summary>
    void OnUpdatesClick(object sender, RoutedEventArgs e)
    {
        var preReleases = new CheckBox
        {
            Content = "Auch Vorabversionen anbieten – zum Testen vor der Freigabe",
            IsChecked = AppSettings.Load().PreReleases,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        var answer = AskDialog.Show(this,
            $"Installiert: Fletta {Updater.Current}. Fletta sieht beim Start nach, ob es eine neuere Version gibt — höchstens einmal am Tag, mit Vorabversionen einmal in der Stunde.",
            preReleases, "Jetzt prüfen", "Schließen");
        // Frisch laden: während der Dialog offen war, kann ein anderes Fenster beim Schließen seine Lage geschrieben haben.
        var settings = AppSettings.Load();
        var wanted = preReleases.IsChecked == true;
        if (wanted != settings.PreReleases) (settings with { PreReleases = wanted }).Save();
        // Jede Änderung prüft neu: einschalten findet eine eben erschienene Vorabversion, ausschalten nimmt ihren Hinweis weg
        // und zeigt stattdessen eine vorhandene Freigabe.
        if (answer == 0 || wanted != settings.PreReleases) CheckForUpdate(force: true);
    }

    async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        await FlushFieldEditor(); // getippter Text zählt als Änderung; das Setup schlösse das Fenster hart (Review 2026-09-18)
        // Eigene Kopie: eine Prüfung aus dem Updates-Dialog könnte das Feld sonst mitten im Laden ändern (Review 2026-09-18).
        if (updating || update is not { } release || PrintRunningAnywhere() || UnsavedAnywhere()) return;
        updating = true;
        UpdateButton.IsEnabled = UpdateCloseButton.IsEnabled = false;
        UpdateText.Text = $"Fletta {release.Version} wird geladen …";
        try
        {
            var setup = await Updater.DownloadAsync(release);
            if (PrintRunningAnywhere() || UnsavedAnywhere()) // während des Ladens begonnen
            {
                UpdateButton.IsEnabled = UpdateCloseButton.IsEnabled = true;
                UpdateText.Text = UpdateTitle(release);
                return;
            }
            UpdateText.Text = "Fletta wird aktualisiert …";
            // Das Setup schließt alle Fletta-Fenster aus seinem Ordner und öffnet sie danach wieder. Bleibt
            // dieses hier offen (anderer Ordner, Setup gescheitert), sagt der Hinweis, was passiert ist.
            var exitCode = await Updater.InstallAsync(setup);
            UpdateText.Text = exitCode == 0 ? $"Fletta {release.Version} installiert – bitte neu starten" : $"Setup mit Code {exitCode} beendet";
            UpdateButton.Visibility = Visibility.Collapsed;
            UpdateCloseButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
        {
            UpdateText.Text = $"Update fehlgeschlagen: {ex.Message}";
            UpdateButton.Content = "Erneut";
            UpdateButton.IsEnabled = UpdateCloseButton.IsEnabled = true;
        }
        finally
        {
            updating = false;
        }
    }

    void OnUpdateCloseClick(object sender, RoutedEventArgs e) => UpdatePill.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Druckt irgendein Fletta-Fenster? Jede PDF ist ein eigener Prozess, und das Setup schließt alle —
    /// ein laufender Auftrag bräche ab. Solange gedruckt wird, hält Print ein benanntes Ereignis offen.
    /// </summary>
    bool PrintRunningAnywhere() => SignalBlocks(PrintingSignal, "Erst nach dem Druck – das Setup schließt alle Fletta-Fenster");

    /// <summary>Hat irgendein Fletta-Fenster ungespeicherte Änderungen? Dann nicht aktualisieren, das Setup schlösse es hart.</summary>
    bool UnsavedAnywhere() => SignalBlocks(UnsavedSignal, "Erst speichern (Strg+S) oder Änderungen zurücknehmen – das Setup schließt alle Fletta-Fenster");

    bool SignalBlocks(string name, string notice)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return false;
        signal.Dispose();
        ShowNotice(notice);
        return true;
    }

    const string PrintingSignal = @"Local\Fletta-printing";
    const string UnsavedSignal = @"Local\Fletta-unsaved"; // offen, solange ein Fenster ungespeicherte Änderungen hat

    /// <summary>Für App.OnSessionEnding: Abmelden oder Herunterfahren soll die Änderungen nicht stillschweigend verwerfen.</summary>
    public bool HasUnsavedEdits => edits.Count > 0 || FieldEditorChanged;

    /// <summary>Nach jeder Änderung an edits: Titel, Speichern-Knopf und prozessübergreifendes Signal nachführen.</summary>
    void EditsChanged()
    {
        // Offene Menüs zeigen auf Seiten- und Anmerkungsnummern von vorher (Review 2026-09-18).
        PageMenu.IsOpen = AnnotMenu.IsOpen = false;
        SaveButton.Visibility = edits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (edits.Count > 0) unsavedSignal ??= new EventWaitHandle(false, EventResetMode.ManualReset, UnsavedSignal);
        else
        {
            unsavedSignal?.Dispose();
            unsavedSignal = null;
        }
        titlePage = -1;
        if (layout is not null) SetCurrentPage(currentPage);
    }

    async void ShowDocument(string path)
    {
        var opening = renderer!;
        documentPath = Path.GetFullPath(path);
        fileName = Path.GetFileName(path);
        Title = $"{fileName} – Fletta";
        password = null;
        while (true)
        {
            ShowLoadingAfterDelay(opening);
            try
            {
                pagesPt = await opening.Opened;
                break;
            }
            catch (PdfException e) when (e.Code == PdfException.PasswordError && renderer == opening && !measurePending)
            {
                var entered = await AskPassword(wrongBefore: password is not null);
                if (renderer != opening) return; // Fenster inzwischen geschlossen
                if (entered is null)
                {
                    CloseDocument();
                    ShowMessage($"„{fileName}“ ist passwortgeschützt", "Ohne Passwort lässt sie sich nicht öffnen. Zum neuen Versuch mit Strg+O wieder auswählen.");
                    return;
                }
                password = entered;
                opening = renderer = new PageRenderer(path, Dispatcher, Deliver, password);
            }
            catch (PdfException e)
            {
                if (renderer != opening) return;
                CloseDocument();
                ShowMessage($"„{fileName}“ lässt sich nicht öffnen", e.Message);
                FinishMeasure();
                return;
            }
        }
        if (renderer != opening) return; // Fenster inzwischen geschlossen
        App.Mark("dokument");
        App.RegisterRestart(path);
        NoticeXfa(opening);
        if (pagesPt.Count == 0)
        {
            CloseDocument();
            ShowMessage($"„{fileName}“ hat keine Seiten", "Die Datei ist ein gültiges PDF, enthält aber nichts zum Anzeigen.");
            FinishMeasure();
            return;
        }
        quarterTurns = new int[pagesPt.Count];
        pageTexts = new string?[pagesPt.Count];
        pageRevisions = new int[pagesPt.Count];
        MessagePanel.Visibility = Visibility.Collapsed;
        SetDocumentControls(true);
        ShowOutlineMessage("Gliederung wird gelesen …");
        Thumbs.Show(pagesPt, quarterTurns, pageRevisions, sidebarWidth.Value);
        App.Mark("leiste");
        ApplyZoom();
    }

    /// <summary>XFA-Formulare kann das PDFium ohne XFA-Modul nur anzeigen (oft nur eine Ersatzseite): das sagen.</summary>
    async void NoticeXfa(PageRenderer opening)
    {
        try
        {
            if (await opening.Invoke(document => document.FormType) == 2 && renderer == opening)
                ShowNotice("XFA-Formular: Fletta kann es nur anzeigen, nicht ausfüllen");
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>Dunkle Rückfrage mit Passwortfeld; null bei Abbrechen.</summary>
    async Task<string?> AskPassword(bool wrongBefore)
    {
        // Beim Start kommt die Antwort von PDFium womöglich noch im Konstruktor an: erst warten, bis das Fenster steht,
        // sonst taugt es nicht als Besitzer des Dialogs.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var box = new PasswordBox
        {
            Width = 280,
            Height = 30,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = (Brush)FindResource("RaiseBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            CaretBrush = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var text = wrongBefore ? "Das Passwort stimmt nicht. Noch einmal:" : $"„{fileName}“ ist passwortgeschützt. Passwort:";
        return AskDialog.Show(this, text, box, "Öffnen", "Abbrechen") == 0 ? box.Password : null;
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
        edits.Clear();
        EditsChanged();
        layout = null;
        pagesPt = [];
        quarterTurns = [];
        pageTexts = [];
        pageRevisions = [];
        pageItems.Clear();
        itemsLoading.Clear();
        itemsGeneration++;
        choosing = null;
        charBoxes.Clear();
        sketches.Clear(); // hängen an PageCanvas, das gleich geleert wird
        CloseFieldEditor();
        SetTool(Tool.None);
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
        CloseSearch(); // erst nach slots.Clear: die Marken rechnen mit quarterTurns je Seite
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
            // Nicht aus dem Fenster, sondern aus der Datei: ein anderes Fenster kann es seit dem Start umgestellt haben.
            PreReleases = AppSettings.Load().PreReleases,
        };
    }

    /// <summary>Knöpfe und Bodenleisten, die nur mit offenem Dokument etwas tun.</summary>
    void SetDocumentControls(bool enabled)
    {
        foreach (var button in new ButtonBase[] { SidebarButton, PrintButton, CopyButton, SearchButton, RotateLeftButton, RotateRightButton,
                                                  FitWidthButton, FitPageButton, SpreadsButton, PagedButton, HighlightToolButton,
                                                  NoteToolButton, InkToolButton, TextToolButton, SignToolButton })
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
        var neighbours = NeighboursFor(RequestFor(first), spreads);
        foreach (var page in rendered.Keys.Where(p => p < first - neighbours || p > last + neighbours).ToList())
        {
            Discard(rendered[page]);
            rendered.Remove(page);
        }

        mainWanted = Wanted(first, last, neighbours);
        RequestRenders();
        PlaceFieldEditor();
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
        DrawMarks(slot, page);
    }

    /// <summary>
    /// Nachbarn je Richtung, die gerendert bereitliegen. Große Bilder nur eine Seite weit: ein 24-MP-Bild
    /// belegt 91 MB. Ganz ohne Nachbarn stünde die nächste Seite zu lange weiß da — eine Handbuchseite
    /// braucht bei 24 MP knapp 0,9 s (Ryzen 7 5800X, gemessen 2026-09-13). Bei Doppelseite bleibt es
    /// bei zwei: eine Seite wäre dort nur die halbe Nachbarzeile.
    /// </summary>
    internal static int NeighboursFor(RenderRequest page, bool spreads) =>
        !spreads && (long)page.PixelWidth * page.PixelHeight > LargePagePixels ? 1 : KeepNeighbours;

    /// <summary>Sichtbare Seiten zuerst, von der Mitte aus; dann die Nachbarn. Was schon scharf da ist, fehlt.</summary>
    RenderRequest[] Wanted(int first, int last, int neighbours)
    {
        var centre = (first + last) / 2.0;
        return Enumerable.Range(first - neighbours, last - first + 1 + 2 * neighbours)
            .Where(p => p >= 0 && p < layout!.Count && !pageErrors.ContainsKey(p))
            .OrderBy(p => p < first || p > last)
            .ThenBy(p => Math.Abs(p - centre))
            .Select(RequestFor)
            .Where(r => !IsSharp(r))
            .ToArray();
    }

    /// <summary>
    /// Hauptansicht zuerst, die Miniaturen dahinter bzw. direkt hinter ihrer Hauptseite (Interleave) — beide über denselben Render-Thread. Was seit dem
    /// letzten UpdateView scharf angekommen oder fehlgeschlagen ist, fällt heraus; sonst rendert der
    /// Thread beim Scrollen der Seitenleiste die Hauptseiten immer wieder neu. Miniaturen erst nach
    /// dem ersten Ergebnis der Hauptansicht: ihre Wünsche kommen beim ersten Layout früher an und
    /// hielten die erste Seite sonst um eine Miniatur auf.
    /// </summary>
    void RequestRenders()
    {
        if (busy) return; // Seitennummern der Leiste und der Ansicht passen erst nach ShowPages wieder zum Dokument
        renderer?.Request(Interleave([.. mainWanted.Where(r => !IsSharp(r) && !pageErrors.ContainsKey(r.Page))],
                                     outlineRequested ? [.. Thumbs.Wanted()] : []));
    }

    /// <summary>
    /// Hauptseiten in ihrer Reihenfolge, die Miniatur derselben Seite jeweils direkt dahinter — die Seite ist dann
    /// noch offen (PdfDocument.KeepPagesOpen), ~30 statt ~160 ms. Die übrigen Miniaturen danach. Im Selbsttest geprüft.
    /// </summary>
    internal static RenderRequest[] Interleave(RenderRequest[] main, RenderRequest[] thumbs)
    {
        var byPage = thumbs.ToLookup(thumb => thumb.Page);
        var paired = new HashSet<int>();
        var queue = new List<RenderRequest>(main.Length + thumbs.Length);
        foreach (var request in main)
        {
            queue.Add(request);
            if (paired.Add(request.Page)) queue.AddRange(byPage[request.Page]);
        }
        queue.AddRange(thumbs.Where(thumb => !paired.Contains(thumb.Page)));
        return [.. queue];
    }

    /// <summary>Liegt für die Seite schon ein Bild zu genau diesem Auftrag vor (Größe, DPI, Drehung)?</summary>
    bool IsSharp(RenderRequest wanted) => rendered.TryGetValue(wanted.Page, out var r) && r.Request == wanted;

    // ponytail: ganze Seiten statt Kacheln. Über MaxPixelsPerPage wird unscharf; Kacheln erst, wenn das stört.
    RenderRequest RequestFor(int page)
    {
        var size = layout!.SizeOf(page);
        double width = size.Width * dpi.DpiScaleX, height = size.Height * dpi.DpiScaleY;
        var shrink = Math.Min(1, Math.Sqrt(MaxPixelsPerPage / (width * height)));
        return new(page, PageLayout.ToPixels(width * shrink), PageLayout.ToPixels(height * shrink),
                   dpi.PixelsPerInchX * shrink, dpi.PixelsPerInchY * shrink, quarterTurns[page], Revision: pageRevisions[page]);
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
        if (result.Bitmap is not null)
        {
            LoadPageItems(page);
            if (result.Request.Revision == pageRevisions[page]) RemoveSketches(page); // die Anmerkung steht jetzt im Bild
        }
        var wanted = RequestFor(page);
        // Aus einer älteren Zoomstufe, während die passende Größe schon da ist: sonst überschreibt
        // ein 110-%-Bild das scharfe 100-%-Bild, nachdem man schnell wieder zurückgezoomt hat.
        if (result.Request != wanted && IsSharp(wanted))
        {
            if (result.Bitmap is not null) Discard(result);
            return;
        }

        if (result.Bitmap is not null)
        {
            if (rendered.TryGetValue(page, out var old)) Discard(old);
            rendered[page] = result;
        }
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

    /// <summary>
    /// Ein Seitenbild fällt weg. Seinen Speicher gibt WPF erst frei, wenn der GC es einsammelt, und der
    /// kommt bei kleinem verwaltetem Heap lange nicht: auf der Test-VM blieben nach Blättern bei 400 %
    /// und Zurückzoomen auf 25 % 505 MB belegt (2026-09-13). Nach etwa einem großen Bild daher selbst
    /// anstoßen — im Hintergrund und erst nach dem Zeichnen, bis dahin hängt das alte Bild noch am Element.
    /// Eingeplant wird nur beim Überschreiten der Schwelle, also höchstens ein Anstoß auf einmal.
    /// </summary>
    void Discard(RenderResult old)
    {
        var before = droppedPixels;
        droppedPixels += (long)old.Request.PixelWidth * old.Request.PixelHeight;
        if (before >= MaxPixelsPerPage || droppedPixels < MaxPixelsPerPage) return;
        Dispatcher.InvokeAsync(() =>
        {
            droppedPixels = 0;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        }, DispatcherPriority.ContextIdle);
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
        Title = $"{(edits.Count > 0 ? "● " : "")}{fileName} · {page + 1}/{layout!.Count} – Fletta";
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
    void Rotate(int quarters) => Rotate(quarters, KeyPages);

    void Rotate(int quarters, int[] pages)
    {
        if (layout is null || busy) return;
        foreach (var page in pages)
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
        await FlushFieldEditor();
        if (layout is null || renderer is null || busy) return;
        // Eine einzelne markierte Miniatur ist nur der Klickfokus, erst ab zwei ist es eine Auswahl.
        var selected = Thumbs.SelectedPages;
        var job = new PrintJob(renderer, pagesPt, (int[])quarterTurns.Clone(), fileName,
                               selected.Length > 1 ? PrintJob.RunsOf(selected) : [], ShowNotice); // Stand beim Klick
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        printing++;
        using var printingSignal = new EventWaitHandle(false, EventResetMode.ManualReset, PrintingSignal); // für PrintRunningAnywhere
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
        await FlushFieldEditor(); // sonst zeigte das Bild den alten Feldwert
        if (layout is null || renderer is null || busy) return;
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
    void OnSaveClick(object sender, RoutedEventArgs e) => _ = Save();
    void OnCopyClick(object sender, RoutedEventArgs e) => CopyPage(textOnly: false);
    void OnSearchClick(object sender, RoutedEventArgs e) => OpenSearch();
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

    /// <summary>
    /// Auf der Seitenfläche: Dateien öffnen, eigene Miniaturen nicht annehmen. Ohne ausdrückliche Wirkung
    /// meldete WPF „verschieben“ an die Quelle zurück, und die Seiten verschwänden aus dem Dokument.
    /// </summary>
    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = ThumbnailPanel.OwnPages(e.Data) is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (ThumbnailPanel.OwnPages(e.Data) is not null) return;
        e.Effects = DragDropEffects.Copy;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var file in files) Open(file);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Im Seitenfeld und im Eingabefeld eines Formulars gehören die Tasten dem Feld (Enter, Esc, Tab behandeln die selbst).
        if (PageBox.IsKeyboardFocused || fieldEditor?.IsKeyboardFocused == true)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var plain = Keyboard.Modifiers == ModifierKeys.None;
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        var ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        // Während eines Werkzeugzugs (Maustaste gedrückt) nur Esc: Entf, Strg+Z oder R änderten die Seiten unter dem Zug weg,
        // und der Strich landete auf der falschen Seite oder gar keiner (Review 2026-09-18).
        if (gesture is not null)
        {
            if (e.Key == Key.Escape) CancelGesture();
            e.Handled = true;
            return;
        }
        // Im Suchfeld tippt man: nur Enter, F3, Esc und Strg+F gehören der Suche, alles andere dem Feld.
        if (SearchBox.IsKeyboardFocused && !(e.Key is Key.Enter or Key.F3 or Key.Escape || (e.Key == Key.F && ctrl)))
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when SearchBox.IsKeyboardFocused && (plain || shift): SearchEnter(shift ? -1 : +1); break;
            case Key.F3 when plain || shift: StepHit(shift ? -1 : +1); break;
            case Key.F when ctrl: OpenSearch(); break;
            case Key.Escape when dragging: EndDrag(); break;
            case Key.Escape when ZoomMenu.IsOpen: ZoomMenu.IsOpen = false; break;
            case Key.Escape when PageMenu.IsOpen: PageMenu.IsOpen = false; break;
            case Key.Escape when AnnotMenu.IsOpen: AnnotMenu.IsOpen = false; break;
            case Key.Escape when SignMenu.IsOpen: SignMenu.IsOpen = false; break;
            case Key.Escape when gesture is not null: CancelGesture(); break;
            case Key.Escape when tool != Tool.None: SetTool(Tool.None); break;
            case Key.Escape when HelpOverlay.IsVisible: ToggleHelp(); break;
            case Key.Escape when searchOpen: CloseSearch(); break;
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
            case Key.S when ctrl: _ = Save(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Delete when plain: DeletePagesCommand(KeyPages); break;
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
        if (e.Text == "?" && !PageBox.IsKeyboardFocused && !SearchBox.IsKeyboardFocused && fieldEditor?.IsKeyboardFocused != true)
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
        if (fieldEditor?.IsMouseOver == true) return; // Klick ins offene Eingabefeld: Schreibmarke setzen, nicht neu öffnen
        choosing = null; // ein früher gedrücktes Auswahlfeld, das anderswo losgelassen wurde, gilt nicht mehr
        if (tool != Tool.None)
        {
            BeginGesture(e); // mit einem Werkzeug zeichnet oder markiert die Maus, statt zu scrollen
            return;
        }
        if (ClickFieldAt(e.GetPosition(PageCanvas)))
        {
            e.Handled = true; // kein Ziehen, und der ScrollViewer nimmt dem Eingabefeld den Fokus nicht
            return;
        }
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
        if (gesture is not null)
        {
            ContinueGesture(e);
            return;
        }
        if (!dragging)
        {
            ShowTipUnder(e);
            return;
        }
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

    /// <summary>Losgelassen, ohne gezogen zu haben, und über einem Link: dem Link folgen.</summary>
    void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (gesture is not null)
        {
            FinishGesture(e);
            return;
        }
        if (OpenPendingChoices()) return;
        var moved = e.GetPosition(Scroller) - dragFrom;
        var clicked = dragging && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                               && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance;
        EndDrag();
        if (clicked && LinkAt(e.GetPosition(PageCanvas)) is { } link) FollowLink(link);
    }

    /// <summary>
    /// Links und Anmerkungen einer Seite einmal lesen, im Hintergrund: nicht auf dem Weg zum ersten Bild und nicht bei
    /// jedem Zoomschritt neu (Review 2026-09-18). Bis dahin steht ein leerer Platzhalter; ist er inzwischen weg (Seiten
    /// geändert, neue Anmerkung), verfällt die Antwort und das nächste Bild fragt neu. Ein Fehlschlag lässt ihn stehen.
    /// </summary>
    async void LoadPageItems(int page)
    {
        if (renderer is not { } reader || itemsLoading.Contains(page) || page >= pageRevisions.Length) return;
        var revision = pageRevisions[page];
        if (pageItems.TryGetValue(page, out var known) && known.Revision == revision) return;
        var generation = itemsGeneration;
        itemsLoading.Add(page);
        PageItems items;
        try
        {
            items = await reader.Invoke(document => new PageItems(document.Links(page), document.Annotations(page), document.FormFields(page), revision), whenIdle: true);
        }
        catch (PdfException)
        {
            items = new PageItems([], [], [], revision); // kaputte Seite: ohne Links und Felder, nicht bei jedem Bild neu versuchen
        }
        catch (TaskCanceledException)
        {
            return; // Renderer beendet; ShowPages oder CloseDocument räumen itemsLoading
        }
        if (reader != renderer || generation != itemsGeneration) return;
        itemsLoading.Remove(page);
        if (revision == pageRevisions[page]) pageItems[page] = items;
        else LoadPageItems(page); // inzwischen geändert: gleich noch einmal, die alten gelten bis dahin
    }

    /// <summary>Seite unter einer Stelle der Leinwand und die Stelle in Anteilen der Ansicht (mit Flettas Drehung).</summary>
    (int Page, Point At)? PageUnder(Point point)
    {
        foreach (var (page, slot) in slots)
        {
            var at = new Point((point.X - Canvas.GetLeft(slot.Frame)) / slot.Frame.Width, (point.Y - Canvas.GetTop(slot.Frame)) / slot.Frame.Height);
            if (at.X >= 0 && at.X <= 1 && at.Y >= 0 && at.Y <= 1) return (page, at);
        }
        return null;
    }

    /// <summary>Link an dieser Stelle der Leinwand; die Drehung der Ansicht wird herausgerechnet.</summary>
    PageLink? LinkAt(Point point) =>
        PageUnder(point) is var (page, at) && pageItems.TryGetValue(page, out var items)
            ? items.Links.FirstOrDefault(link => link.Area.Contains(PageLayout.Turn(at, -quarterTurns[page])))
            : null;

    /// <summary>Oberste Anmerkung an dieser Stelle (die zuletzt gezeichnete liegt oben).</summary>
    (int Page, PageAnnotation Annotation)? AnnotationAt(Point point) =>
        PageUnder(point) is var (page, at) && pageItems.TryGetValue(page, out var items)
            && items.Annotations.LastOrDefault(annotation => annotation.Area.Contains(PageLayout.Turn(at, -quarterTurns[page]))) is { } found
            ? (page, found)
            : null;

    /// <summary>Über einem Link: Hand und Ziel als Tooltip; über einer Anmerkung ihr Text. Mit Werkzeug dessen Zeiger.</summary>
    void ShowTipUnder(MouseEventArgs e)
    {
        var point = e.GetPosition(PageCanvas);
        var link = layout is null || tool != Tool.None ? null : LinkAt(point);
        var field = layout is null || tool != Tool.None || link is not null ? null : FieldAt(point);
        var annotation = layout is null || link is not null || field is not null ? null : AnnotationAt(point);
        PageCanvas.Cursor = link is not null ? Cursors.Hand : field is { } hit ? FieldCursor(hit.Field) : ToolCursor();
        var tip = link is not null ? (link.Page >= 0 ? $"Seite {link.Page + 1}" : link.Uri)
                : field is { } under ? FieldTip(under.Field)
                : annotation is { } found ? AnnotationTip(found.Annotation) : null;
        if (!Equals(PageCanvas.ToolTip, tip)) PageCanvas.ToolTip = tip;
    }

    /// <summary>
    /// Ziel im Dokument: hinspringen. Adresse: nur Web und Mail an das Standardprogramm — ein PDF soll über einen
    /// Klick keine Programme oder Dateien starten.
    /// </summary>
    void FollowLink(PageLink link)
    {
        if (link.Page >= 0)
        {
            GoTo(link.Page);
            return;
        }
        // „www.example.com“ ohne Schema ist eine Web-Adresse (Review 2026-09-18); geprüft wird erst danach.
        if ((!Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) && !Uri.TryCreate($"http://{link.Uri}", UriKind.Absolute, out uri))
            || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            ShowNotice($"Nicht geöffnet – Fletta öffnet nur Web- und Mail-Adressen: {link.Uri}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            ShowNotice(uri.Scheme == "mailto" ? $"Neue Mail an {uri.UserInfo}@{uri.Host}" : $"Öffne {uri.Host} im Browser");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            ShowNotice($"Link lässt sich nicht öffnen: {e.Message}");
        }
    }

    /// <summary>Fang verloren — Fensterwechsel, fremder Fang; das Ziehen endet dann ebenso, ein Werkzeugzug verfällt.</summary>
    void OnDragLost(object sender, MouseEventArgs e)
    {
        if (gesture is not null) CancelGesture();
        else EndDrag();
    }

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
            // Über den Miniaturen bewegt das Rad das Dokument, die Leiste folgt der aktuellen Seite (Martin,
            // 2026-09-13). Die Gliederung scrollt weiter selbst.
            var overThumbs = Thumbs.IsMouseOver && layout is not null;
            if (paged && layout is not null && (Scroller.IsMouseOver || overThumbs)) FlipWithWheel(e);
            else if (overThumbs)
            {
                e.Handled = true;
                Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
            }
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

    /// <summary>Miniaturen-Menü und Maus: markierte Seiten, sonst die aktuelle.</summary>
    int[] MenuPages => Thumbs.SelectedPages is { Length: > 0 } selected ? selected : [currentPage];

    /// <summary>Tasten (R, L, Entf): eine einzelne markierte Miniatur ist nur der Klickfokus, erst ab zwei zählt die Auswahl.</summary>
    int[] KeyPages => Thumbs.SelectedPages is { Length: > 1 } selected ? selected : [currentPage];

    void BuildPageMenu()
    {
        AddPageItem("Rechts drehen", "R", () => Rotate(+1, MenuPages));
        AddPageItem("Links drehen", "L", () => Rotate(-1, MenuPages));
        AddMenuLine(PageMenuItems);
        AddPageItem("Löschen", "Entf", () => DeletePagesCommand(MenuPages));
        AddPageItem("In neue PDF kopieren …", "", () => ExportPages(MenuPages, move: false));
        AddPageItem("In neue PDF verschieben …", "", () => ExportPages(MenuPages, move: true));
        AddMenuLine(PageMenuItems);
        AddPageItem("PDF davor einfügen …", "", () => PickAndInsert(MenuPages.Min()));
        AddPageItem("PDF dahinter einfügen …", "", () => PickAndInsert(MenuPages.Max() + 1));
        AddMenuLine(PageMenuItems);
        undoItem = AddPageItem("Rückgängig", "Strg+Z", Undo);
        saveItem = AddPageItem("Speichern", "Strg+S", () => _ = Save());
    }

    Button AddPageItem(string text, string keys, Action action) => AddMenuItem(PageMenuItems, PageMenu, text, keys, action);

    Button AddMenuItem(StackPanel items, Popup menu, string text, string keys, Action action)
    {
        var shortcut = new TextBlock { Text = keys, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(24, 0, 0, 0) };
        DockPanel.SetDock(shortcut, Dock.Right);
        var item = new Button
        {
            Style = (Style)FindResource("MenuButton"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel { Children = { shortcut, new TextBlock { Text = text } } },
        };
        item.Click += (_, _) =>
        {
            menu.IsOpen = false;
            action();
        };
        items.Children.Add(item);
        return item;
    }

    void AddMenuLine(StackPanel items) =>
        items.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4), Background = (Brush)FindResource("LineBrush") });

    void OpenPageMenu()
    {
        if (layout is null) return;
        undoItem!.IsEnabled = edits.Count > 0;
        saveItem!.IsEnabled = edits.Count > 0 || quarterTurns.Any(turns => turns != 0);
        PageMenu.IsOpen = true;
    }

    /// <summary>Löschen bleibt ein gesammelter Schritt; eine PDF ohne Seiten gibt es nicht.</summary>
    async void DeletePagesCommand(int[] pages)
    {
        if (layout is null) return;
        if (pages.Length >= pagesPt.Count)
        {
            ShowNotice("Mindestens eine Seite muss bleiben");
            return;
        }
        if (await Edit(new DeletePages(pages), pages.Min()) >= 0)
            ShowNotice($"{PagesText(pages)} gelöscht – Strg+Z nimmt es zurück, Strg+S speichert");
    }

    async void MovePagesTo(int[] pages, int gap)
    {
        var move = MovePages.ToGap(pages, gap);
        if (layout is null || !move.ChangesOrder(pagesPt.Count)) return;
        if (await Edit(move, move.Destination) >= 0) Thumbs.Select(Enumerable.Range(move.Destination, pages.Length));
    }

    void PickAndInsert(int at)
    {
        var dialog = new OpenFileDialog { Filter = "PDF-Dateien|*.pdf", Multiselect = true };
        if (dialog.ShowDialog(this) == true) InsertFiles(dialog.FileNames, at);
    }

    /// <summary>Jede Datei ein eigener Schritt; rückwärts vor dieselbe Stelle, so bleibt ihre Reihenfolge.</summary>
    async void InsertFiles(string[] files, int at)
    {
        if (layout is null) return;
        var before = pagesPt.Count;
        foreach (var file in files.Reverse())
        {
            // Rückgängig wendet das Einfügen erneut an: dafür eine eigene Kopie, die niemand verschiebt oder löscht.
            string copy;
            try
            {
                copy = Path.Combine(PageDragData.ExportFolder(), Path.GetFileName(file));
                await Task.Run(() => File.Copy(file, copy)); // große PDF von der NAS: Oberfläche und Ablegen nicht einfrieren
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShowNotice($"„{Path.GetFileName(file)}“ lässt sich nicht lesen: {e.Message}");
                break;
            }
            if (await Edit(new InsertPages(copy, at), at) < 0) break;
        }
        var added = pagesPt.Count - before;
        if (added <= 0) return;
        Thumbs.Select(Enumerable.Range(at, added));
        ShowNotice($"{added} {(added == 1 ? "Seite" : "Seiten")} eingefügt – Strg+S speichert");
    }

    /// <summary>Kopieren oder verschieben in eine neue PDF; gedreht wie in der Ansicht.</summary>
    async void ExportPages(int[] pages, bool move)
    {
        if (renderer is null) return;
        if (busy)
        {
            ShowNotice("Einen Moment – die letzte Änderung läuft noch");
            return;
        }
        if (move && pages.Length >= pagesPt.Count)
        {
            ShowNotice("Mindestens eine Seite muss bleiben – zum Kopieren „In neue PDF kopieren“");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "PDF-Dateien|*.pdf",
            DefaultExt = ".pdf",
            FileName = ExportName(pages),
            InitialDirectory = Path.GetDirectoryName(documentPath),
        };
        if (dialog.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), documentPath, StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice("Das ist die offene Datei – Änderungen daran speichert Strg+S");
            return;
        }
        var turns = pages.Select(page => quarterTurns[page]).ToArray();
        try
        {
            await renderer.Invoke(document => document.ExportPages(pages, turns, dialog.FileName));
        }
        catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            ShowNotice($"Neue PDF fehlgeschlagen: {e.Message}");
            return;
        }
        if (move && await Edit(new DeletePages(pages), pages.Min()) < 0) return;
        ShowNotice($"{PagesText(pages)} {(move ? "verschoben" : "kopiert")} nach „{Path.GetFileName(dialog.FileName)}“");
    }

    string ExportName(int[] pages) => $"{Path.GetFileNameWithoutExtension(fileName)} – {PagesText(pages)}.pdf";

    static string PagesText(int[] pages) =>
        (pages.Length == 1 ? "Seite " : "Seiten ") +
        string.Join(", ", PrintJob.RunsOf([.. pages.Order()]).Select(run => run.From == run.To ? $"{run.From}" : $"{run.From}–{run.To}"));

    /// <summary>
    /// Miniaturen hinausziehen. Ziel ist die eigene Leiste (verschieben, OnPagesDropped), ein anderes
    /// Fenster oder der Explorer: dort landet eine neue PDF, erst beim Ablegen angelegt. Meldet das Ziel
    /// „verschieben“ zurück (Umschalt), fallen die Seiten hier weg.
    /// </summary>
    void DragPagesOut(int[] pages)
    {
        if (renderer is null || busy) return;
        var source = renderer;
        var turns = pages.Select(page => quarterTurns[page]).ToArray();
        var name = ExportName(pages);
        // Läuft im Ziehen auf dem UI-Thread; der Render-Thread braucht ihn dafür nicht, also kein Stillstand.
        var data = new PageDragData(pages, () =>
        {
            var path = Path.Combine(PageDragData.ExportFolder(), name);
            source.Invoke(document => document.ExportPages(pages, turns, path)).GetAwaiter().GetResult();
            return path;
        });
        droppedInside = false;
        var effect = DragDrop.DoDragDrop(Thumbs, data, DragDropEffects.Copy | DragDropEffects.Move);
        if (data.Error is { } error)
        {
            ShowNotice($"Seiten ließen sich nicht übergeben: {error}");
            return;
        }
        if (effect != DragDropEffects.Move || droppedInside || renderer != source) return;
        if (pages.Length >= pagesPt.Count) ShowNotice("Kopiert – mindestens eine Seite muss hier bleiben");
        else DeletePagesCommand(pages);
    }

    /// <returns>Seitenzahl danach, −1 wenn nichts geschah.</returns>
    async Task<int> Edit(PageEdit edit, int focusPage)
    {
        // Ein offenes Formular-Eingabefeld zuerst übernehmen: sonst zeigte es nach einer Anmerkung auf eine verschobene
        // Nummer, oder sein Text ginge verloren (Review 2026-09-18).
        await FlushFieldEditor();
        if (renderer is null || BlockedByWork()) return -1;
        var opening = renderer;
        busy = true;
        mainWanted = [];
        opening.Request([]);
        try
        {
            var sizes = await opening.Invoke(document =>
            {
                edit.Apply(document);
                return PageRenderer.SizesOf(document);
            });
            if (renderer != opening) return -1;
            if (edit.KeepsPages)
            {
                // Anmerkung oder Formular: nur neu rendern, die Ansicht bleibt stehen; die alten Bilder bis dahin auch.
                edits.Add((edit, (int[])quarterTurns.Clone(), pageTexts));
                if (edit.OnlyPage is { } changed)
                {
                    pageRevisions[changed]++;
                    pageItems.Remove(changed);
                }
                else
                {
                    // Formular: Lage und Nummern der Felder bleiben, die Listen gelten weiter und werden im Hintergrund
                    // erneuert (Revision). Leer dazwischen fand Tab kein nächstes Feld (Review 2026-09-18).
                    for (var page = 0; page < pageRevisions.Length; page++) pageRevisions[page]++;
                }
                EditsChanged();
                busy = false;
                Thumbs.Invalidate();
                UpdateView();
                return sizes.Length;
            }
            edits.Add((edit, quarterTurns, pageTexts));
            quarterTurns = edit.Remap(quarterTurns, sizes.Length, 0);
            pageTexts = edit.Remap<string?>(pageTexts, sizes.Length, null);
            ShowPages(sizes, focusPage);
            EditsChanged();
            return sizes.Length;
        }
        catch (Exception e) when (e is PdfException or TaskCanceledException)
        {
            if (renderer != opening) return -1;
            ShowNotice($"Änderung nicht möglich: {e.Message}");
            // PDFium kann ein Dokument halb geändert zurücklassen (FPDF_MovePages): neu laden, gesammelte Schritte wieder anwenden.
            await Reload(quarterTurns, pageTexts, currentPage);
            return -1;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>
    /// Läuft eine Änderung oder ein Druck? Der Druckauftrag holt seine Seiten über ihre Nummern vom selben
    /// Dokument — eine Änderung mittendrin verschöbe den Ausdruck, Neuladen bräche ihn ab.
    /// </summary>
    bool BlockedByWork()
    {
        if (busy) ShowNotice("Einen Moment – die letzte Änderung läuft noch");
        else if (printing > 0) ShowNotice("Erst nach dem Druck");
        else return false;
        return true;
    }

    /// <summary>Ansicht und Leiste nach einer Änderung am offenen Dokument neu aufbauen.</summary>
    void ShowPages(IReadOnlyList<Size> sizes, int focusPage)
    {
        CancelGesture(); // die Seitennummer des Zugs gilt nicht mehr
        CloseFieldEditor(); // ebenso die des Formularfelds
        pagesPt = sizes;
        foreach (var result in rendered.Values) Discard(result);
        slots.Clear();
        spareSlots.Clear();
        rendered.Clear();
        pageErrors.Clear();
        pageItems.Clear();
        itemsLoading.Clear();
        itemsGeneration++;
        choosing = null;
        charBoxes.Clear();
        sketches.Clear(); // hängen an PageCanvas und gehen mit dessen Kindern
        pageRevisions = new int[sizes.Count];
        mainWanted = [];
        PageCanvas.Children.Clear();
        layout = null;
        titlePage = -1;
        pinnedOffset = null;
        Thumbs.Show(sizes, quarterTurns, pageRevisions, sidebarWidth.Value);
        currentPage = Math.Clamp(focusPage, 0, sizes.Count - 1);
        ApplyZoom();
        GoTo(currentPage);
        LoadOutline(renderer!);
        FindAgain(navigate: false); // Seiten haben sich verschoben: Treffer aus dem gemerkten Text neu
    }

    /// <summary>
    /// Datei neu öffnen und die gesammelten Änderungen erneut anwenden — für Strg+Z, nach dem Speichern und
    /// wenn eine Änderung scheiterte. turns und texts gehören zum Stand danach; null heißt ungedreht bzw. ungelesen.
    /// </summary>
    async Task<bool> Reload(int[]? turns, string?[]? texts, int focusPage)
    {
        busy = true;
        try
        {
            if (renderer is { } old)
            {
                old.Dispose();
                await old.Closed;
            }
            var opening = renderer = new PageRenderer(documentPath, Dispatcher, Deliver, password);
            var replay = edits.Select(entry => entry.Edit).ToArray();
            // Sofort anstellen, vor dem Warten aufs Öffnen: Aufgaben laufen vor Hintergrundarbeit, sonst läse die Suche in
            // dieser Lücke Text aus der unveränderten Datei (Review 2026-09-18).
            var replayed = opening.Invoke(document =>
            {
                foreach (var edit in replay) edit.Apply(document);
                return PageRenderer.SizesOf(document);
            });
            await opening.Opened;
            var sizes = await replayed;
            if (renderer != opening) return false;
            quarterTurns = turns is { } kept && kept.Length == sizes.Length ? kept : new int[sizes.Length];
            pageTexts = texts is { } read && read.Length == sizes.Length ? read : new string?[sizes.Length];
            ShowPages(sizes, focusPage);
            return true;
        }
        catch (Exception e) when (e is PdfException or TaskCanceledException)
        {
            CloseDocument();
            NoticePill.Visibility = Visibility.Collapsed;
            ShowMessage($"„{fileName}“ lässt sich nicht neu laden", e.Message);
            return false;
        }
        finally
        {
            busy = false;
        }
    }

    async void Undo()
    {
        if (layout is null || BlockedByWork()) return;
        if (edits.Count == 0)
        {
            ShowNotice("Nichts zurückzunehmen");
            return;
        }
        // ponytail: Drehungen nach der zurückgenommenen Änderung gehen mit verloren; ein eigener Verlauf je Drehung erst, wenn das stört.
        var (_, turnsBefore, textsBefore) = edits[^1];
        edits.RemoveAt(edits.Count - 1);
        EditsChanged();
        if (await Reload(turnsBefore, textsBefore, currentPage))
            ShowNotice(edits.Count == 0 ? "Alle Änderungen zurückgenommen" : "Letzte Änderung zurückgenommen");
    }

    /// <summary>
    /// Strg+S: Änderungen und Drehungen der Ansicht in die Datei. PDFium schreibt eine vollständige Kopie
    /// daneben; erst danach wird das Dokument geschlossen und das Original ersetzt (es ist geöffnet und
    /// damit gesperrt). Scheitert das Ersetzen, bleibt der Zwischenstand erhalten.
    /// </summary>
    async Task<bool> Save()
    {
        await FlushFieldEditor(); // der Speichern-Knopf nimmt dem Eingabefeld den Fokus nicht (Review 2026-09-18)
        if (renderer is null || layout is null || BlockedByWork()) return false;
        var turns = (int[])quarterTurns.Clone();
        if (edits.Count == 0 && turns.All(turn => turn == 0))
        {
            ShowNotice("Nichts zu speichern");
            return true;
        }
        var opening = renderer;
        var temp = Path.Combine(Path.GetDirectoryName(documentPath)!, $".{Path.GetFileNameWithoutExtension(documentPath)}.fletta-{Guid.NewGuid():N}.tmp");
        busy = true;
        mainWanted = [];
        opening.Request([]);
        try
        {
            if (await opening.Invoke(document => document.SignatureCount) > 0 &&
                AskDialog.Show(this, $"„{fileName}“ ist digital signiert. Speichern macht die Signatur ungültig.",
                               "Trotzdem speichern", "Abbrechen") != 0)
                return false;
            ShowNotice("Speichere …");
            noticeTimer.Stop(); // bleibt stehen, bis „gespeichert“ oder ein Fehler ihn ersetzt
            await opening.Invoke(document =>
            {
                var rotated = 0;
                try
                {
                    for (; rotated < turns.Length; rotated++)
                        if (turns[rotated] != 0) document.Rotate(rotated, turns[rotated]);
                    document.SaveAs(temp);
                }
                finally
                {
                    // Der Zwischenstand bleibt ungedreht wie vorher — die Drehung steckt weiter in der Ansicht.
                    // Nur zurück, was schon gedreht war: scheitert Seite k, blieben sonst alle davor doppelt gedreht.
                    for (var page = 0; page < rotated; page++)
                        if (turns[page] != 0) document.Rotate(page, -turns[page]);
                }
            });
        }
        catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            TryDelete(temp);
            ShowNotice($"Speichern fehlgeschlagen: {e.Message}");
            return false;
        }
        finally
        {
            busy = false;
        }

        busy = true;
        opening.Dispose();
        await opening.Closed;
        try
        {
            File.Move(temp, documentPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Original unverändert: Zwischenstand wiederherstellen. Die geschriebene Kopie erst weg, wenn das klappt.
            if (await Reload(turns, pageTexts, currentPage))
            {
                TryDelete(temp);
                ShowNotice($"Speichern fehlgeschlagen: {e.Message}");
            }
            else ShowMessage($"„{fileName}“ ließ sich nicht ersetzen", $"{e.Message}\nDer gespeicherte Stand liegt unter {temp}.");
            return false;
        }
        edits.Clear();
        EditsChanged();
        var saved = await Reload(null, pageTexts, currentPage); // gleiche Seiten, nur die Drehung steckt jetzt in der Datei
        if (saved) ShowNotice($"„{fileName}“ gespeichert");
        return saved;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Schließen mit ungespeicherten Änderungen: speichern, verwerfen oder abbrechen.</summary>
    async void ConfirmClose()
    {
        const int SaveChoice = 0, CancelChoice = 2;
        var answer = AskDialog.Show(this, $"Änderungen an „{fileName}“ speichern?", "Speichern", "Nicht speichern", "Abbrechen");
        if (answer == CancelChoice || (answer == SaveChoice && !await Save())) return;
        closeConfirmed = true;
        // Bei „Nein“ liefe Close sonst noch innerhalb von OnClosing — WPF wirft dann, und der Prozess stürzt ab.
        _ = Dispatcher.InvokeAsync(Close);
    }

    /// <summary>Beim Schließen und beim Abmelden (App.OnSessionEnding) — nie im Messlauf.</summary>
    public void SaveSettings()
    {
        if (!App.IsMeasuring) CurrentSettings().Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            ShowNotice("Einen Moment – die Änderung wird noch geschrieben");
            return;
        }
        if (FieldEditorChanged && !closeConfirmed)
        {
            // Getippt, noch nicht übernommen: erst übernehmen, dann wie jede ungespeicherte Änderung nachfragen.
            e.Cancel = true;
            FlushThenClose();
            return;
        }
        if (edits.Count > 0 && !closeConfirmed)
        {
            e.Cancel = true;
            ConfirmClose();
            return;
        }
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

    internal static void ApplyDarkCaption(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, 1, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, CaptionColorRef, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);
}
