using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fletta.Pdfium;

namespace Fletta;

/// <summary>
/// Formulare (Stufe 4): ein Klick ohne Werkzeug in ein Feld füllt es aus. Textfelder bekommen ein Eingabefeld von Fletta
/// genau darüber (Enter übernimmt, Tab springt zum nächsten Feld der Seite, Esc verwirft); Kästchen und Optionsfelder
/// schalten um, Auswahl- und Listenfelder zeigen ihre Möglichkeiten als Menü. Jeder übernommene Wert ist ein
/// gesammelter Schritt (Strg+Z, Strg+S), PDFium schreibt ihn samt Erscheinung ins Feld (PdfDocument.SetFieldText).
/// </summary>
public partial class MainWindow
{
    const double AutoFontShare = 0.65; // Schriftgröße „automatisch“: so viel der Feldhöhe, höchstens MaxAutoFont
    const double MaxAutoFont = 12;

    TextBox? fieldEditor;
    (int Page, FormField Field)? editing;
    (int Page, FormField Field)? choosing; // Auswahlfeld gedrückt: das Menü kommt beim Loslassen, sonst schlösse es das
    bool keepEdit; // false: Esc hat verworfen, der Fokusverlust danach übernimmt nichts

    /// <summary>Oberstes Formularfeld an dieser Stelle der Leinwand.</summary>
    (int Page, FormField Field)? FieldAt(Point point) =>
        PageUnder(point) is var (page, at) && pageItems.TryGetValue(page, out var items)
            && items.Fields.LastOrDefault(field => field.Area.Contains(PageLayout.Turn(at, -quarterTurns[page]))) is { } found
            ? (page, found)
            : null;

    /// <summary>Klick ohne Werkzeug: ein ausfüllbares Feld übernimmt ihn. true, wenn es eins war.</summary>
    bool ClickFieldAt(Point point)
    {
        if (FieldAt(point) is not var (page, field) || !field.Fillable) return false;
        switch (field.Type)
        {
            case Native.FormFieldText:
                EditField(page, field);
                break;
            case Native.FormFieldCheckBox or Native.FormFieldRadioButton:
                _ = Edit(new ClickField(page, field.Index), page); // übernimmt ein offenes Eingabefeld selbst zuerst
                break;
            default:
                CommitField();
                choosing = (page, field); // OnDragEnd öffnet das Menü
                break;
        }
        return true;
    }

    static string FieldTip(FormField field) =>
        (field.Name.Length > 0 ? field.Name : "Formularfeld") + (field.ReadOnly ? " (schreibgeschützt)" : "") +
        (field.Type is Native.FormFieldPushButton or Native.FormFieldSignature ? "\nSchaltflächen und Signaturfelder füllt Fletta nicht aus" : "");

    static Cursor? FieldCursor(FormField field) =>
        !field.Fillable ? null : field.Type == Native.FormFieldText ? Cursors.IBeam : Cursors.Hand;

    void EditField(int page, FormField field)
    {
        CommitField();
        if (!slots.ContainsKey(page)) return;
        var box = new TextBox
        {
            Text = field.Value,
            AcceptsReturn = field.Multiline,
            TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = field.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            FontFamily = new FontFamily("Arial"), // Helvetica-Ersatz: so ähnlich wie das, was PDFium danach ins Feld schreibt
        };
        box.PreviewKeyDown += (_, e) => OnFieldKey(e, page, field);
        // Nur das eigene: wird ein fokussiertes Feld entfernt (Tab), meldet WPF den Verlust erst später — dann steht schon
        // das nächste, und ohne den Vergleich würde das übernommen und geschlossen (VM, 2026-09-18).
        box.LostKeyboardFocus += (_, _) =>
        {
            if (fieldEditor == box) CommitField();
        };
        box.RequestBringIntoView += (_, e) => e.Handled = true; // sonst scrollte der ScrollViewer selbst dorthin
        // Erst fokussieren, wenn es im Layout steht: direkt nach dem Einfügen scheiterte Focus() nach Tab (VM, 2026-09-18).
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        PageCanvas.ToolTip = null; // der Name des Felds stand sonst über dem nächsten
        Panel.SetZIndex(box, 20);
        PageCanvas.Children.Add(box);
        (fieldEditor, editing, keepEdit) = (box, (page, field), true);
        PlaceFieldEditor();
    }

    /// <summary>Nach jedem Bildlauf und Zoom: Eingabefeld über sein Formularfeld; ist die Seite aus dem Bild, übernehmen.</summary>
    void PlaceFieldEditor()
    {
        if (fieldEditor is not { } box || editing is not var (page, field) || layout is null) return;
        if (!slots.TryGetValue(page, out var slot))
        {
            CommitField();
            return;
        }
        var area = PageLayout.Turn(field.Area, quarterTurns[page]);
        var (left, top) = (Canvas.GetLeft(slot.Frame), Canvas.GetTop(slot.Frame));
        Canvas.SetLeft(box, left + area.X * slot.Frame.Width);
        Canvas.SetTop(box, top + area.Y * slot.Frame.Height);
        box.Width = Math.Max(8, area.Width * slot.Frame.Width);
        box.Height = Math.Max(8, area.Height * slot.Frame.Height);
        var heightPt = area.Height * ShownSize(page).Height;
        var sizePt = field.FontSize > 0 ? field.FontSize : Math.Min(MaxAutoFont, heightPt * AutoFontShare);
        box.FontSize = Math.Max(1, sizePt * layout.Scale);
    }

    void OnFieldKey(KeyEventArgs e, int page, FormField field)
    {
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        switch (e.Key)
        {
            case Key.Escape:
                keepEdit = false;
                CloseFieldEditor();
                break;
            case Key.Enter when !field.Multiline || Keyboard.Modifiers == ModifierKeys.Control:
                CommitField();
                Scroller.Focus();
                break;
            case Key.Tab when Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                // Das nächste Feld vorher merken: nach dem Übernehmen liest die Seite ihre Felder neu ein.
                var next = NextTextField(page, field, shift ? -1 : +1);
                CommitField();
                if (next is { } target) EditField(page, target);
                else Scroller.Focus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Nächstes ausfüllbares Textfeld der Seite in /Annots-Reihenfolge (so liest sie auch Acrobat mit Tab).</summary>
    FormField? NextTextField(int page, FormField from, int direction)
    {
        if (!pageItems.TryGetValue(page, out var items)) return null;
        var candidates = items.Fields.Where(field => field.Fillable && field.Type == Native.FormFieldText).ToList();
        var at = candidates.FindIndex(field => field.Index == from.Index);
        var next = at + direction;
        return at >= 0 && next >= 0 && next < candidates.Count ? candidates[next] : null;
    }

    /// <summary>Getippt und noch nicht übernommen? Zählt beim Schließen und Abmelden wie eine ungespeicherte Änderung.</summary>
    bool FieldEditorChanged => fieldEditor is { } box && editing is { } open && keepEdit && box.Text != open.Field.Value;

    /// <summary>Für Ereignisse (Fokusverlust, Tasten): übernehmen, ohne zu warten.</summary>
    async void CommitField() => await FlushFieldEditor();

    /// <summary>
    /// Eingabefeld schließen und den Text übernehmen, wenn er sich geändert hat (und nicht mit Esc verworfen wurde); fertig,
    /// wenn der Schritt angewandt ist. Vor Speichern, Drucken, Schließen und jedem anderen Schritt (Edit).
    /// </summary>
    async Task FlushFieldEditor()
    {
        if (fieldEditor is not { } box || editing is not var (page, field)) return;
        var keep = keepEdit;
        CloseFieldEditor();
        if (!keep || box.Text == field.Value) return;
        // Von Feld zu Feld mit Tab: die vorige Übernahme kann noch laufen, Edit lehnte dann ab und der Text wäre weg.
        // ponytail: kurz warten statt einer Warteschlange für Änderungen; reicht für Tippgeschwindigkeit.
        for (var waited = 0; busy && waited < 100; waited++) await Task.Delay(30);
        if (await Edit(new SetFieldText(page, field.Index, box.Text), page) >= 0)
            RememberField(page, field.Index, known => known with { Value = box.Text }); // bis die neue Liste da ist
    }

    /// <summary>Feld in der gemerkten Liste der Seite nachführen, bis LoadPageItems die neue liefert.</summary>
    void RememberField(int page, int index, Func<FormField, FormField> change)
    {
        if (pageItems.TryGetValue(page, out var items))
            pageItems[page] = items with { Fields = [.. items.Fields.Select(field => field.Index == index ? change(field) : field)] };
    }

    /// <summary>Schließen mit offenem, geändertem Eingabefeld: übernehmen, dann wie gewohnt schließen (mit Rückfrage).</summary>
    async void FlushThenClose()
    {
        await FlushFieldEditor();
        _ = Dispatcher.InvokeAsync(Close);
    }

    /// <summary>Eingabefeld weg, ohne etwas zu übernehmen (Esc, Seiten geändert).</summary>
    void CloseFieldEditor()
    {
        if (fieldEditor is not { } box) return;
        (fieldEditor, editing) = (null, null); // vor dem Entfernen: das löst LostKeyboardFocus aus, und CommitField liefe erneut
        PageCanvas.Children.Remove(box);
    }

    async void Choose(int page, FormField field, int option)
    {
        if (await Edit(new SetFieldChoice(page, field.Index, option), page) >= 0)
            RememberField(page, field.Index, known => known with { Selected = option, Value = field.Options[option] });
    }

    /// <summary>Beim Loslassen der Maus: war ein Auswahlfeld gedrückt, jetzt sein Menü.</summary>
    bool OpenPendingChoices()
    {
        if (choosing is not var (page, field)) return false;
        choosing = null;
        OpenChoices(page, field);
        return true;
    }

    /// <summary>Auswahl- oder Listenfeld: Möglichkeiten als Menü an der Maus, die gewählte mit Haken.</summary>
    void OpenChoices(int page, FormField field)
    {
        if (field.Options.Length == 0)
        {
            ShowNotice($"„{field.Name}“ hat keine Möglichkeiten zur Auswahl");
            return;
        }
        AnnotMenuItems.Children.Clear();
        for (var option = 0; option < field.Options.Length; option++)
        {
            var chosen = option;
            AddMenuItem(AnnotMenuItems, AnnotMenu, (option == field.Selected ? "✓  " : "     ") + field.Options[option], "",
                        () => Choose(page, field, chosen));
        }
        AnnotMenu.IsOpen = true;
    }
}
