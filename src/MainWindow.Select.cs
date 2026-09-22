using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Fletta.Pdfium;

namespace Fletta;

/// <summary>
/// Text mit der Maus markieren und kopieren. Die linke Taste entscheidet nach der Stelle: liegt sie auf einem Zeichen,
/// markiert der Zug, sonst schiebt er die Ansicht wie bisher (Martins Wahl, 2026-09-22). Der Zeiger zeigt es vorher als
/// I-Balken an, und über der Seite gelesene Zeichenkästen (dieselben wie beim Textmarker) machen den nächsten Druck
/// treffsicher. Markiert wird innerhalb einer Seite; die Marken liegen in der 1-×-1-Leinwand der Suchtreffer und wachsen
/// darum beim Zoomen mit, ohne neu gezeichnet zu werden.
/// </summary>
public partial class MainWindow
{
    static readonly Brush SelectionBrush = Frozen(Color.FromArgb(0x55, 0x3C, 0x8C, 0xE0));

    /// <summary>Markierte Zeichen der Zeichenliste einer Seite, beide eingeschlossen; From ist der Anfang des Zugs.</summary>
    (int Page, int From, int To)? selection;
    bool selecting;       // linke Taste hält gerade eine Auswahl auf
    bool selectHintShown; // der Hinweis auf Strg+C kommt einmal je Fenster

    /// <summary>
    /// Druck auf der Seitenfläche: liegt er auf einem Zeichen, beginnt eine Auswahl (true), sonst schiebt der Zug wie
    /// bisher. Sind die Zeichenkästen der Seite noch nicht gelesen, schiebt er ebenfalls — der Zeiger zeigt dann auch
    /// keinen I-Balken, also überrascht es nicht.
    /// </summary>
    bool BeginSelect(Point canvasPoint)
    {
        ClearSelection();
        if (CharUnderPoint(canvasPoint) is not var (page, hit) || hit < 0) return false;
        selecting = true;
        selection = (page, hit, hit); // ein einzelnes Zeichen wird nicht gezeichnet: ein Klick auf Text soll nicht aufblitzen
        Scroller.CaptureMouse();
        return true;
    }

    /// <summary>Ziehen: das Ende wandert mit, notfalls an den Seitenrand geklemmt (wie beim Textmarker).</summary>
    void ContinueSelect(Point canvasPoint)
    {
        if (selection is not { } current || !slots.TryGetValue(current.Page, out var slot)
            || !charBoxes.TryGetValue(current.Page, out var loading) || !loading.IsCompletedSuccessfully) return;
        var frame = new Rect(Canvas.GetLeft(slot.Frame), Canvas.GetTop(slot.Frame), slot.Frame.Width, slot.Frame.Height);
        var clamped = new Point(Math.Clamp(canvasPoint.X, frame.Left, frame.Right), Math.Clamp(canvasPoint.Y, frame.Top, frame.Bottom));
        var at = new Point((clamped.X - frame.X) / frame.Width, (clamped.Y - frame.Y) / frame.Height);
        var to = CharAt(loading.Result, PageLayout.Turn(at, -quarterTurns[current.Page]), pagesPt[current.Page]);
        if (to < 0 || to == current.To) return;
        selection = (current.Page, current.From, to);
        RefreshMarks();
    }

    /// <summary>Loslassen: ein Klick ohne Zug hebt die Auswahl wieder auf, sonst bleibt sie stehen — Strg+C kopiert sie.</summary>
    void EndSelect()
    {
        selecting = false;
        Scroller.ReleaseMouseCapture();
        if (selection is not { } current || current.From == current.To)
        {
            ClearSelection();
            return;
        }
        if (selectHintShown) return;
        selectHintShown = true;
        ShowNotice("Markiert – Strg+C kopiert den Text");
    }

    /// <summary>true, wenn es etwas aufzuheben gab: Esc nimmt erst die Auswahl, bevor es das Fenster schließt.</summary>
    bool ClearSelection()
    {
        if (selection is null) return false;
        selection = null;
        RefreshMarks();
        return true;
    }

    /// <summary>
    /// Beim Schließen und nach Seitenänderungen: die Nummern gelten nicht mehr, gezeichnet wird ohnehin neu. Der Fang
    /// muss mit weg — ShowPages kommt mitten im Zug (Entf, Strg+Z), und danach riefe ihn niemand mehr zurück (Review).
    /// </summary>
    void ForgetSelection()
    {
        var held = selecting;
        (selection, selecting) = (null, false); // erst die Felder: ReleaseMouseCapture ruft OnDragLost gleich zurück
        if (held) Scroller.ReleaseMouseCapture();
    }

    /// <summary>Strg+C mit Auswahl kopiert nur den markierten Text; ohne Auswahl bleibt es bei der ganzen Seite.</summary>
    async void CopySelection()
    {
        if (selection is not { } current || renderer is not { } reader || busy) return;
        var (from, to) = (Math.Min(current.From, current.To), Math.Max(current.From, current.To));
        try
        {
            var text = await reader.Invoke(document => document.CharText(current.Page, from, to - from + 1));
            if (text.Length == 0)
            {
                ShowNotice("Die Auswahl enthält keinen Text");
                return;
            }
            Clipboard.SetDataObject(text, copy: true);
            ShowNotice($"{text.Length} Zeichen kopiert");
        }
        catch (Exception e) when (e is PdfException or ExternalException or TaskCanceledException)
        {
            // ExternalException: die Zwischenablage ist gerade von einem anderen Programm belegt.
            ShowNotice($"Kopieren fehlgeschlagen: {e.Message}");
        }
    }

    /// <summary>
    /// Seite und Zeichen unter der Stelle der Leinwand; Hit ist −1 neben allem Text. Die Zeichenkästen der Seite werden
    /// dabei angefordert: beim Zeigen gelesen, beim Drücken dann schon da. null ohne Seite oder solange sie fehlen.
    /// </summary>
    (int Page, int Hit)? CharUnderPoint(Point canvasPoint)
    {
        // Während einer Änderung nichts beim Render-Thread bestellen (wie LoadHitRects): der schreibt gerade.
        if (layout is null || busy || PageUnder(canvasPoint) is not var (page, at)) return null;
        var loading = CharBoxesOf(page);
        return loading.IsCompletedSuccessfully ? (page, CharUnder(loading.Result, PageLayout.Turn(at, -quarterTurns[page]))) : null;
    }

    /// <summary>Zeichen, dessen Kasten die Stelle enthält (Anteile der ungedrehten Seite); −1 daneben. Im Selbsttest geprüft.</summary>
    internal static int CharUnder(Rect[] boxes, Point at)
    {
        for (var i = 0; i < boxes.Length; i++)
            if (!boxes[i].IsEmpty && boxes[i].Contains(at)) return i;
        return -1;
    }

    /// <summary>Auswahl in die Markenleinwand ihrer Seite, in Anteilen der Seite und mit Flettas Drehung.</summary>
    void DrawSelection(PageSlot slot, int page)
    {
        if (selection is not { } current || current.Page != page || current.From == current.To
            || !charBoxes.TryGetValue(page, out var loading) || !loading.IsCompletedSuccessfully) return;
        foreach (var line in LineRects(loading.Result, Math.Min(current.From, current.To), Math.Max(current.From, current.To)))
        {
            var area = PageLayout.Turn(line, quarterTurns[page]);
            var mark = new Rectangle { Width = area.Width, Height = area.Height, Fill = SelectionBrush };
            Canvas.SetLeft(mark, area.X);
            Canvas.SetTop(mark, area.Y);
            slot.Marks.Children.Add(mark);
        }
    }

    /// <summary>Marken der sichtbaren Seiten neu: searchVersion zählt alles, was sie betrifft — Treffer wie Auswahl.</summary>
    void RefreshMarks()
    {
        searchVersion++;
        RedrawMarks();
    }
}
