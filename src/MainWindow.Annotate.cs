using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Fletta.Pdfium;
using Microsoft.Win32;
using Path = System.Windows.Shapes.Path;

namespace Fletta;

/// <summary>
/// Anmerkungen (Stufe 3): Textmarker, Notiz, Freihand, Text auf die Seite und Unterschrift, jeweils als Werkzeug oben in
/// der Leiste. Mit Werkzeug zeichnet oder markiert die linke Maustaste, statt zu scrollen. Jede Anmerkung ist ein
/// gesammelter Schritt wie das Löschen einer Seite (Strg+Z, Strg+S); gerendert wird nur ihre Seite neu. Bis das neue
/// Bild da ist, steht eine Skizze auf der Leinwand, damit nichts flackert.
/// </summary>
public partial class MainWindow
{
    enum Tool { None, Highlight, Note, Ink, Text, SignDrawn, SignImage }

    const float InkWidth = 2;               // Punkt
    const double SignatureWidth = 150;      // Punkt, wenn einfach geklickt statt ein Rahmen gezogen wird
    const double ClickSlop = 4;             // DIP: darunter gilt eine Bewegung als Klick
    static readonly (string Label, float Size)[] TextSizes = [("Klein", 9), ("Normal", 11), ("Groß", 14), ("Sehr groß", 20)];
    static readonly Brush SketchHighlightBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0xE2, 0x00));
    static readonly Brush SketchInkBrush = Frozen(AnnotationEdit.InkColor);
    static readonly Brush SketchSignatureBrush = Frozen(AnnotationEdit.SignatureColor);

    /// <summary>Ein Zug mit dem Werkzeug, vom Drücken bis zum Loslassen. Stellen in Anteilen der Ansicht.</summary>
    sealed class Gesture(int page, Point start, Point startCanvas)
    {
        public readonly int Page = page;
        public readonly Point Start = start;
        public readonly Point StartCanvas = startCanvas;
        public readonly List<Point> Points = [start]; // Freihand
        public Shape? Sketch;                         // Vorschau auf der Leinwand
        public int From = -1, To = -1;               // Textmarker: Zeichen der Zeichenliste, beide eingeschlossen
        public double Aspect;                         // Unterschrift: Breite / Höhe
        public Point End = start;
    }

    Tool tool;
    Gesture? gesture;
    float textSize = 11;
    // Skizzen bleiben stehen, bis ein Bild der Seite mit neuerer Revision kommt (Deliver) oder die Änderung scheitert.
    readonly List<(int Page, int Revision, UIElement Element)> sketches = [];
    // Textmarker: Zeichenkästen je Seite (Anteile der angezeigten Seite), einmal geladen; leer ohne Text oder bei Fehler.
    readonly Dictionary<int, Task<Rect[]>> charBoxes = [];

    void OnHighlightToolClick(object sender, RoutedEventArgs e) => SetTool(tool == Tool.Highlight ? Tool.None : Tool.Highlight);
    void OnNoteToolClick(object sender, RoutedEventArgs e) => SetTool(tool == Tool.Note ? Tool.None : Tool.Note);
    void OnInkToolClick(object sender, RoutedEventArgs e) => SetTool(tool == Tool.Ink ? Tool.None : Tool.Ink);
    void OnTextToolClick(object sender, RoutedEventArgs e) => SetTool(tool == Tool.Text ? Tool.None : Tool.Text);

    void SetTool(Tool next)
    {
        if (gesture is not null) CancelGesture();
        tool = next;
        HighlightToolButton.IsChecked = next == Tool.Highlight;
        NoteToolButton.IsChecked = next == Tool.Note;
        InkToolButton.IsChecked = next == Tool.Ink;
        TextToolButton.IsChecked = next == Tool.Text;
        SignToolButton.IsChecked = next is Tool.SignDrawn or Tool.SignImage;
        PageCanvas.Cursor = ToolCursor();
        PageCanvas.ToolTip = null;
        if (next == Tool.Highlight) foreach (var page in slots.Keys) CharBoxesOf(page); // vorab: ein schneller erster Zug trifft sonst nichts
        var hint = next switch
        {
            Tool.Highlight => "Textmarker: Text mit gedrückter Maustaste überstreichen",
            Tool.Note => "Notiz: auf die Stelle klicken",
            Tool.Ink => "Freihand: mit gedrückter Maustaste zeichnen",
            Tool.Text => "Text: auf die Stelle klicken, an der er beginnen soll",
            Tool.SignDrawn or Tool.SignImage => "Unterschrift: auf die Stelle klicken oder einen Rahmen in der gewünschten Breite ziehen",
            _ => null,
        };
        if (hint is not null) ShowNotice($"{hint} · Esc beendet");
    }

    Cursor? ToolCursor() => tool switch
    {
        Tool.Highlight => Cursors.IBeam,
        Tool.Ink => Cursors.Pen,
        Tool.None => null,
        _ => Cursors.Cross,
    };

    /// <summary>Unterschrift: gemerkte setzen, neu zeichnen oder aus einem Bild laden.</summary>
    void OnSignToolClick(object sender, RoutedEventArgs e)
    {
        var active = tool is Tool.SignDrawn or Tool.SignImage;
        SignToolButton.IsChecked = active; // der Klick allein schaltet nicht um
        if (active)
        {
            SetTool(Tool.None);
            return;
        }
        SignMenuItems.Children.Clear();
        var drawn = Signatures.LoadDrawn() is not null;
        if (drawn) AddMenuItem(SignMenuItems, SignMenu, "Gezeichnete Unterschrift setzen", "", () => SetTool(Tool.SignDrawn));
        AddMenuItem(SignMenuItems, SignMenu, drawn ? "Neu zeichnen …" : "Unterschrift zeichnen …", "", DrawSignature);
        AddMenuLine(SignMenuItems);
        if (Signatures.HasImage) AddMenuItem(SignMenuItems, SignMenu, "Unterschrift als Bild setzen", "", () => SetTool(Tool.SignImage));
        AddMenuItem(SignMenuItems, SignMenu, Signatures.HasImage ? "Anderes Bild laden …" : "Unterschrift aus Bild laden …", "", LoadSignatureImage);
        SignMenu.IsOpen = true;
    }

    void DrawSignature()
    {
        while (true)
        {
            var pad = new InkCanvas { Width = 480, Height = 180, Background = Brushes.White };
            pad.DefaultDrawingAttributes.Color = AnnotationEdit.SignatureColor;
            pad.DefaultDrawingAttributes.Width = pad.DefaultDrawingAttributes.Height = 2.2;
            pad.DefaultDrawingAttributes.FitToCurve = true;
            var answer = AskDialog.Show(this, "Mit Maus oder Stift im weißen Feld unterschreiben:", pad, "Übernehmen", "Neu", "Abbrechen");
            if (answer == 1) continue;
            if (answer != 0) return;
            if (!Signatures.SaveDrawn([.. pad.Strokes.Select(stroke => stroke.StylusPoints.Select(point => new Point(point.X, point.Y)).ToArray())]))
            {
                ShowNotice("Nichts gezeichnet oder nicht speicherbar – keine Unterschrift gemerkt");
                return;
            }
            SetTool(Tool.SignDrawn);
            return;
        }
    }

    void LoadSignatureImage()
    {
        var dialog = new OpenFileDialog { Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Signatures.ImportImage(dialog.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            ShowNotice($"Bild lässt sich nicht laden: {e.Message}");
            return;
        }
        SetTool(Tool.SignImage);
    }

    void BeginGesture(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(PageCanvas);
        if (PageUnder(point) is not var (page, at) || BlockedByWork()) return;
        var started = new Gesture(page, at, point);
        switch (tool)
        {
            case Tool.Ink:
                started.Sketch = new Polyline
                {
                    Stroke = SketchInkBrush, StrokeThickness = InkWidth * layout!.Scale,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Points = { point },
                };
                break;
            case Tool.Highlight:
                started.Sketch = new Path { Fill = SketchHighlightBrush };
                CharBoxesOf(page);
                break;
            case Tool.SignDrawn or Tool.SignImage:
                var aspect = tool == Tool.SignDrawn ? Signatures.LoadDrawn()?.Aspect
                           : Signatures.LoadImage() is var (_, width, height) ? (double)width / height : null;
                if (aspect is not > 0)
                {
                    ShowNotice("Keine Unterschrift gemerkt – über den Knopf zeichnen oder laden");
                    SetTool(Tool.None);
                    return;
                }
                started.Aspect = aspect.Value;
                started.Sketch = new Rectangle { Stroke = SketchSignatureBrush, StrokeThickness = 1, StrokeDashArray = [4, 3] };
                break;
        }
        gesture = started;
        if (started.Sketch is { } sketch) AddSketch(page, sketch);
        Scroller.CaptureMouse();
        ContinueGesture(e);
    }

    void ContinueGesture(MouseEventArgs e)
    {
        var current = gesture!;
        var point = e.GetPosition(PageCanvas);
        if (!slots.TryGetValue(current.Page, out var slot)) return; // Seite aus dem Bild gerollt: dort nichts mehr
        var frame = new Rect(Canvas.GetLeft(slot.Frame), Canvas.GetTop(slot.Frame), slot.Frame.Width, slot.Frame.Height);
        var clamped = new Point(Math.Clamp(point.X, frame.Left, frame.Right), Math.Clamp(point.Y, frame.Top, frame.Bottom));
        current.End = new Point((clamped.X - frame.X) / frame.Width, (clamped.Y - frame.Y) / frame.Height);
        switch (current.Sketch)
        {
            case Polyline line when current.Points[^1] != current.End:
                line.Points.Add(clamped);
                current.Points.Add(current.End);
                break;
            case Path marks when charBoxes.TryGetValue(current.Page, out var loading) && loading.IsCompletedSuccessfully:
                ShowSelection(current, marks, frame, loading.Result);
                break;
            case Rectangle box:
                var area = SignatureBox(current);
                Canvas.SetLeft(box, frame.X + area.X * frame.Width);
                Canvas.SetTop(box, frame.Y + area.Y * frame.Height);
                (box.Width, box.Height) = (area.Width * frame.Width, area.Height * frame.Height);
                break;
        }
    }

    /// <summary>Textmarker: Zeichen unter Anfang und Ende des Zugs, dazwischen alles.</summary>
    void SelectChars(Gesture current, Rect[] boxes)
    {
        var turns = quarterTurns[current.Page];
        var size = pagesPt[current.Page];
        current.From = CharAt(boxes, PageLayout.Turn(current.Start, -turns), size);
        current.To = CharAt(boxes, PageLayout.Turn(current.End, -turns), size);
    }

    /// <summary>Auswahl samt Vorschau zeilenweise.</summary>
    void ShowSelection(Gesture current, Path marks, Rect frame, Rect[] boxes)
    {
        SelectChars(current, boxes);
        var turns = quarterTurns[current.Page];
        var lines = new GeometryGroup();
        if (current.From >= 0)
            foreach (var line in LineRects(boxes, Math.Min(current.From, current.To), Math.Max(current.From, current.To)))
            {
                var shown = PageLayout.Turn(line, turns);
                lines.Children.Add(new RectangleGeometry(new Rect(frame.X + shown.X * frame.Width, frame.Y + shown.Y * frame.Height,
                                                                  shown.Width * frame.Width, shown.Height * frame.Height)));
            }
        marks.Data = lines;
    }

    async void FinishGesture(MouseButtonEventArgs e)
    {
        ContinueGesture(e);
        var done = gesture!;
        if (done.Page >= quarterTurns.Length) // Seiten inzwischen anders (sollte der Tastenriegel verhindern)
        {
            CancelGesture();
            return;
        }
        gesture = null;
        Scroller.ReleaseMouseCapture();
        var clicked = (e.GetPosition(PageCanvas) - done.StartCanvas).Length < ClickSlop;
        var placing = tool;
        if (placing == Tool.Highlight && done.From < 0)
        {
            // Zug schneller als das Laden der Zeichenkästen: darauf warten, dann auswählen.
            var reader = renderer;
            var boxes = await CharBoxesOf(done.Page);
            if (renderer != reader || done.Page >= quarterTurns.Length) return;
            SelectChars(done, boxes);
        }
        var turns = quarterTurns[done.Page];
        PageEdit? edit = placing switch
        {
            Tool.Ink when done.Points.Count > 1 => new AddInk(done.Page, turns, [[.. done.Points]], AnnotationEdit.InkColor, InkWidth),
            Tool.Highlight when done.From >= 0 => new AddHighlight(done.Page, Math.Min(done.From, done.To), Math.Abs(done.To - done.From) + 1),
            Tool.Note when clicked => AskNote("Notiz:", "") is { } text ? new AddNote(done.Page, turns, done.Start, text) : null,
            Tool.Text when clicked => AskText() is { } text ? new AddText(done.Page, turns, done.Start, text, textSize) : null,
            Tool.SignDrawn or Tool.SignImage => SignatureEdit(done, clicked),
            _ => null,
        };
        if (edit is null || await Edit(edit, done.Page) < 0)
        {
            RemoveSketch(done.Sketch);
            return;
        }
        if (placing is Tool.SignDrawn or Tool.SignImage) SetTool(Tool.None); // eine Unterschrift genügt meist
    }

    void CancelGesture()
    {
        var cancelled = gesture;
        gesture = null;
        if (cancelled is null) return;
        RemoveSketch(cancelled.Sketch);
        Scroller.ReleaseMouseCapture();
    }

    /// <summary>
    /// Kasten der Unterschrift in Anteilen der Ansicht: linke obere Ecke am Anfang des Zugs, Breite wie gezogen (sonst
    /// SignatureWidth), Höhe nach dem Seitenverhältnis der Unterschrift.
    /// </summary>
    Rect SignatureBox(Gesture current)
    {
        var view = ShownSize(current.Page);
        var width = Math.Abs(current.End.X - current.Start.X) * view.Width;
        if (width * layout!.Scale < ClickSlop) width = SignatureWidth;
        var left = current.End.X < current.Start.X && width != SignatureWidth ? current.End.X : current.Start.X;
        return new Rect(left, current.Start.Y, width / view.Width, width / current.Aspect / view.Height);
    }

    PageEdit? SignatureEdit(Gesture done, bool clicked)
    {
        if (clicked) done.End = done.Start;
        var box = SignatureBox(done);
        var turns = quarterTurns[done.Page];
        if (tool == Tool.SignDrawn)
        {
            if (Signatures.LoadDrawn() is not { } drawn) return null;
            var strokes = drawn.ToPoints().Select(stroke => stroke.Select(point => new Point(box.X + point.X * box.Width, box.Y + point.Y * box.Height)).ToArray());
            // Strichstärke wächst mit der Größe: 150 pt breit ergibt gut 1,2 pt.
            var width = (float)Math.Clamp(box.Width * ShownSize(done.Page).Width / 120, 0.8, 3);
            return new AddInk(done.Page, turns, [.. strokes], AnnotationEdit.SignatureColor, width);
        }
        return Signatures.LoadImage() is var (bgra, pixelWidth, pixelHeight) ? new AddImage(done.Page, turns, box, bgra, pixelWidth, pixelHeight) : null;
    }

    void AddSketch(int page, UIElement element)
    {
        element.IsHitTestVisible = false;
        Panel.SetZIndex(element, 10);
        PageCanvas.Children.Add(element);
        sketches.Add((page, pageRevisions[page], element));
    }

    void RemoveSketch(UIElement? element)
    {
        if (element is null) return;
        PageCanvas.Children.Remove(element);
        sketches.RemoveAll(sketch => sketch.Element == element);
    }

    /// <summary>Ein neueres Bild der Seite ist da: die Skizzen davor sind jetzt im Bild.</summary>
    void RemoveSketches(int page)
    {
        foreach (var sketch in sketches.Where(sketch => sketch.Page == page && sketch.Revision < pageRevisions[page]).ToList())
            RemoveSketch(sketch.Element);
    }

    /// <summary>Zeichenkästen einer Seite für den Textmarker, einmal je Seite geladen (ShowPages und Schließen leeren).</summary>
    Task<Rect[]> CharBoxesOf(int page)
    {
        if (charBoxes.TryGetValue(page, out var known)) return known;
        var loading = renderer is { } reader ? LoadCharBoxes(reader, page) : Task.FromResult<Rect[]>([]);
        charBoxes[page] = loading;
        return loading;
    }

    async Task<Rect[]> LoadCharBoxes(PageRenderer reader, int page)
    {
        try
        {
            var boxes = await reader.Invoke(document => document.CharBoxes(page));
            if (reader != renderer) return [];
            if (gesture is { Sketch: Path marks } running && running.Page == page && slots.TryGetValue(page, out var slot))
                ShowSelection(running, marks, new Rect(Canvas.GetLeft(slot.Frame), Canvas.GetTop(slot.Frame), slot.Frame.Width, slot.Frame.Height), boxes);
            if (boxes.Length == 0 && tool == Tool.Highlight) ShowNotice($"Seite {page + 1} hat keinen Text (Scan?) – der Textmarker braucht Text");
            return boxes;
        }
        catch (Exception e) when (e is PdfException or TaskCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Zeichen, das der Stelle am nächsten liegt; senkrecht zählt der Abstand doppelt, damit die Zeile gewinnt, auf der
    /// man zieht. −1 ohne Zeichen. at in Anteilen der angezeigten Seite, size ihre Größe in Punkt. Im Selbsttest geprüft.
    /// </summary>
    internal static int CharAt(Rect[] boxes, Point at, Size size)
    {
        var (best, bestDistance) = (-1, double.MaxValue);
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            if (box.IsEmpty) continue;
            var dx = Math.Max(0, Math.Max(box.Left - at.X, at.X - box.Right)) * size.Width;
            var dy = Math.Max(0, Math.Max(box.Top - at.Y, at.Y - box.Bottom)) * size.Height * 2;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance) (best, bestDistance) = (i, distance);
        }
        return best;
    }

    /// <summary>Zeichenkästen from…to zu Zeilen zusammengefasst (überlappen senkrecht zur Hälfte). Im Selbsttest geprüft.</summary>
    internal static List<Rect> LineRects(Rect[] boxes, int from, int to)
    {
        var lines = new List<Rect>();
        for (var i = Math.Max(0, from); i <= to && i < boxes.Length; i++)
        {
            var box = boxes[i];
            if (box.IsEmpty) continue;
            var sameLine = lines.Count > 0 &&
                Math.Min(lines[^1].Bottom, box.Bottom) - Math.Max(lines[^1].Top, box.Top) > 0.5 * Math.Min(lines[^1].Height, box.Height);
            if (sameLine) lines[^1] = Rect.Union(lines[^1], box);
            else lines.Add(box);
        }
        return lines;
    }

    /// <summary>Rechtsklick auf eine Anmerkung: Notiz bearbeiten, löschen.</summary>
    void OnPageRightClick(object sender, MouseButtonEventArgs e)
    {
        if (layout is null || gesture is not null || AnnotationAt(e.GetPosition(PageCanvas)) is not var (page, annotation)) return;
        e.Handled = true;
        AnnotMenuItems.Children.Clear();
        if (annotation.Subtype == Native.AnnotText) AddMenuItem(AnnotMenuItems, AnnotMenu, "Notiz bearbeiten …", "", () => EditNote(page, annotation));
        AddMenuItem(AnnotMenuItems, AnnotMenu, $"{KindOf(annotation.Subtype)} löschen", "", () => RemoveAnnotationCommand(page, annotation));
        AnnotMenu.IsOpen = true;
    }

    async void EditNote(int page, PageAnnotation note)
    {
        if (AskNote("Notiz bearbeiten:", note.Contents) is { } text && text != note.Contents)
            await Edit(new SetNoteText(page, note.Index, text), page);
    }

    async void RemoveAnnotationCommand(int page, PageAnnotation annotation)
    {
        if (await Edit(new RemoveAnnotation(page, annotation.Index), page) >= 0)
            ShowNotice($"{KindOf(annotation.Subtype)} gelöscht – Strg+Z nimmt es zurück, Strg+S speichert");
    }

    static string KindOf(int subtype) => subtype switch
    {
        Native.AnnotText => "Notiz",
        Native.AnnotHighlight => "Textmarker",
        Native.AnnotInk => "Zeichnung",
        Native.AnnotStamp => "Stempel",
        Native.AnnotFreeText => "Textfeld",
        Native.AnnotUnderline => "Unterstreichung",
        Native.AnnotStrikeOut => "Durchstreichung",
        Native.AnnotSquare => "Rechteck",
        _ => "Anmerkung",
    };

    static string AnnotationTip(PageAnnotation annotation) =>
        KindOf(annotation.Subtype) + (annotation.Contents.Length > 0 ? $": {annotation.Contents}" : "")
        + (annotation.Subtype == Native.AnnotText ? "\nRechtsklick: bearbeiten oder löschen" : "\nRechtsklick: löschen");

    /// <summary>Dunkles Eingabefeld für die Dialoge; mehrzeilig nimmt Enter als Zeilenumbruch.</summary>
    TextBox InputBox(string text) => new()
    {
        Text = text,
        Width = 380,
        MinHeight = 90,
        MaxHeight = 240,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(6, 4, 6, 4),
        BorderThickness = new Thickness(0),
        Background = (Brush)FindResource("RaiseBrush"),
        Foreground = (Brush)FindResource("TextBrush"),
        CaretBrush = (Brush)FindResource("TextBrush"),
    };

    string? AskNote(string title, string text)
    {
        var box = InputBox(text);
        box.CaretIndex = text.Length;
        var answer = AskDialog.Show(this, title, box, "Übernehmen", "Abbrechen");
        return answer == 0 && box.Text.Trim().Length > 0 ? box.Text.TrimEnd() : null;
    }

    /// <summary>
    /// Text auf die Seite: Eingabe samt Schriftgröße, die sich Fletta bis zum Schließen merkt. Zeichen, die die
    /// Standardschrift nicht hat, werden gemeldet, und der Dialog kommt mit dem Text wieder.
    /// </summary>
    string? AskText()
    {
        var text = "";
        while (AskText(text) is { } entered)
        {
            var missing = PdfDocument.MissingInStampFont(entered);
            if (missing.Length == 0) return entered;
            ShowNotice($"Diese Zeichen hat die Schrift nicht: {missing}");
            text = entered;
        }
        return null;
    }

    string? AskText(string text)
    {
        var box = InputBox(text);
        box.CaretIndex = text.Length;
        var sizes = new UniformGrid { Columns = TextSizes.Length, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, size) in TextSizes)
        {
            var choice = new ToggleButton { Content = $"{label} {size:0}", Style = (Style)FindResource("TabToggle"), Margin = new Thickness(0, 0, 4, 0), IsChecked = size == textSize };
            choice.Click += (_, _) =>
            {
                textSize = size;
                foreach (var other in sizes.Children.OfType<ToggleButton>()) other.IsChecked = other == choice;
            };
            sizes.Children.Add(choice);
        }
        var answer = AskDialog.Show(this, "Text auf die Seite (Größe in Punkt):", new StackPanel { Children = { box, sizes } }, "Einfügen", "Abbrechen");
        return answer == 0 && box.Text.Trim().Length > 0 ? box.Text.TrimEnd() : null;
    }
}
