using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fletta.Pdfium;

namespace Fletta;

/// <summary>
/// Suche (Strg+F). PDFium braucht für den Text einer Seite den zerlegten Seiteninhalt. Fletta liest ihn je Seite einmal im
/// Hintergrund (ab der aktuellen Seite, nur wenn sonst nichts zu rendern ist), merkt ihn sich und sucht darin selbst; jede
/// weitere Suche ist dann sofort da. Gemessen: das Suzuki-Handbuch (990 Seiten) ist auf der Test-VM in unter 10 s gelesen
/// (2026-09-18). Wo ein Treffer auf der Seite steht, fragt Fletta erst, wenn die Seite zu sehen ist.
/// </summary>
public partial class MainWindow
{
    static readonly Brush HitBrush = Frozen(Color.FromArgb(0x60, 0xFF, 0xC8, 0x00));
    static readonly Brush CurrentHitBrush = Frozen(Color.FromArgb(0x90, 0xFF, 0x70, 0x00));

    /// <summary>Ein Treffer: die Zeichen [Start, Start + Length) im Text der Seite, wie PdfDocument.PageText ihn liefert.</summary>
    internal readonly record struct SearchHit(int Page, int Start, int Length);

    string?[] pageTexts = []; // Text je Seite, sobald gelesen; folgt den Änderungen wie die Drehungen
    string searchTerm = "";
    bool searchOpen;
    readonly List<SearchHit> hits = []; // in Dokumentreihenfolge
    int currentHit = -1;
    readonly Dictionary<int, Rect[][]> hitRects = []; // je Seite, in der Reihenfolge ihrer Treffer
    readonly HashSet<int> hitRectsPending = [];        // angefragt oder gescheitert: nicht noch einmal
    int hitGeneration;  // neue Trefferliste: ältere Antworten zu Rechtecken verfallen
    int searchVersion;  // ändert sich mit allem, was die Marken betrifft (Treffer wie Auswahl); PageSlot.MarkedFor vergleicht dagegen
    int textReading;    // Lesekette: ein Wechsel beendet die laufende, sie startet dann neu
    bool readingTexts;
    bool revealPending; // ShowHit wartet auf die Lage des Treffers, um ihn ins Bild zu holen
    readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    void InitSearch() => searchDelay.Tick += (_, _) =>
    {
        searchDelay.Stop();
        Find(SearchBox.Text.Trim());
    };

    void OpenSearch()
    {
        if (layout is null) return;
        searchOpen = true;
        SearchPill.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
        ShowSearchStatus();
        ReadTexts(); // schon beim Öffnen: bis der Begriff getippt ist, sind die ersten Seiten gelesen
        Find(SearchBox.Text.Trim()); // wieder geöffnet: der alte Begriff steht noch da und gilt gleich
    }

    /// <summary>Esc oder ×: Marken weg, der gelesene Text bleibt für die nächste Suche.</summary>
    void CloseSearch()
    {
        var wasOpen = searchOpen;
        searchOpen = false;
        searchDelay.Stop();
        SearchPill.Visibility = Visibility.Collapsed;
        searchTerm = "";
        FindAgain(navigate: false);
        if (wasOpen && layout is not null) Scroller.Focus();
    }

    void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        searchDelay.Stop();
        searchDelay.Start();
    }

    void OnSearchPrevClick(object sender, RoutedEventArgs e) => StepHit(-1);
    void OnSearchNextClick(object sender, RoutedEventArgs e) => StepHit(+1);
    void OnSearchCloseClick(object sender, RoutedEventArgs e) => CloseSearch();

    /// <summary>Enter im Suchfeld: ist der Begriff noch nicht gesucht (schnell getippt), erst suchen — das zeigt den ersten Treffer.</summary>
    void SearchEnter(int direction)
    {
        if (searchDelay.IsEnabled || SearchBox.Text.Trim() != searchTerm)
        {
            searchDelay.Stop();
            Find(SearchBox.Text.Trim());
        }
        else StepHit(direction);
    }

    void Find(string term)
    {
        if (term == searchTerm) return;
        searchTerm = term;
        FindAgain(navigate: true);
    }

    /// <summary>Trefferliste aus dem gemerkten Text neu — nach neuem Begriff und nachdem sich die Seiten geändert haben.</summary>
    void FindAgain(bool navigate)
    {
        hitGeneration++;
        hits.Clear();
        hitRects.Clear();
        hitRectsPending.Clear();
        currentHit = -1;
        revealPending = false;
        for (var page = 0; page < pageTexts.Length; page++) AddHits(page);
        if (hits.Count > 0)
        {
            if (navigate) ShowHit(FirstHitFrom(currentPage));
            else currentHit = FirstHitFrom(currentPage);
        }
        searchVersion++;
        RedrawMarks();
        ShowSearchStatus();
        textReading++; // eine laufende Lesekette beginnt neu, ab der aktuellen Seite und für die neue Seitenfolge
        ReadTexts();
    }

    /// <summary>Treffer im gelesenen Text einer Seite einsortieren; ein aktueller Treffer dahinter rückt mit.</summary>
    void AddHits(int page)
    {
        if (searchTerm.Length == 0 || pageTexts[page] is not { } text) return;
        var found = FindAll(text, searchTerm);
        if (found.Length == 0) return;
        // Meist aufsteigend (FindAgain, Lesekette ab der aktuellen Seite): dann anhängen statt die Liste abzusuchen —
        // sonst O(Seiten × Treffer) bei „e“ im 990-Seiten-Handbuch (Review 2026-09-18).
        var at = hits.Count == 0 || hits[^1].Page < page ? hits.Count : hits.FindIndex(hit => hit.Page > page);
        if (at < 0) at = hits.Count;
        hits.InsertRange(at, found.Select(range => new SearchHit(page, range.Start, range.Length)));
        if (currentHit >= at) currentHit += found.Length;
    }

    /// <summary>
    /// Fundstellen ohne Rücksicht auf Groß- und Kleinschreibung, nicht überlappend. Ordinal statt nach Sprache: die Länge
    /// eines Treffers muss die des Begriffs sein, sonst stimmen die Zeichennummern für PDFium nicht. Im Selbsttest geprüft.
    /// </summary>
    internal static (int Start, int Length)[] FindAll(string text, string term)
    {
        var found = new List<(int, int)>();
        for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0 && term.Length > 0;
             at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
            found.Add((at, term.Length));
        return [.. found];
    }

    int FirstHitFrom(int page) => Math.Max(0, hits.FindIndex(hit => hit.Page >= page));

    /// <summary>F3, Enter, Pfeile: nächster oder voriger Treffer, am Ende geht es vorn weiter.</summary>
    void StepHit(int direction)
    {
        if (!searchOpen)
        {
            OpenSearch();
            return;
        }
        if (hits.Count == 0) return;
        ShowHit(currentHit < 0 ? FirstHitFrom(currentPage) : (currentHit + direction + hits.Count) % hits.Count);
    }

    void ShowHit(int index)
    {
        currentHit = index;
        var page = hits[index].Page;
        if (!slots.ContainsKey(page)) GoTo(page);
        searchVersion++;
        RedrawMarks();
        revealPending = true;
        RevealCurrentHit();
        ShowSearchStatus();
    }

    /// <summary>
    /// Den aktuellen Treffer ins Bild holen, sobald seine Lage bekannt ist; bis dahin steht die Seite oben. Nur nach
    /// ShowHit — sonst zöge das Nachladen der Lage (etwa nach einer Änderung) die Ansicht vom Leser weg.
    /// </summary>
    void RevealCurrentHit()
    {
        if (!revealPending || currentHit < 0 || layout is null) return;
        var page = hits[currentHit].Page;
        if (!hitRects.TryGetValue(page, out var rects))
        {
            LoadHitRects(page);
            return;
        }
        revealPending = false;
        var index = currentHit - hits.FindIndex(hit => hit.Page == page);
        if (index >= rects.Length || rects[index].Length == 0) return;
        var area = rects[index].Select(rect => PageLayout.Turn(rect, quarterTurns[page])).Aggregate(Rect.Union);
        var size = layout.SizeOf(page);
        double top = layout.Top(page) + area.Top * size.Height, bottom = layout.Top(page) + area.Bottom * size.Height;
        double left = layout.Left(page, PageCanvas.Width) + area.Left * size.Width, right = left + area.Width * size.Width;
        // Nach einem Sprung (GoTo) steht der neue Offset erst nach dem Layout im ScrollViewer, festgehalten ist er schon.
        var (viewTop, viewHeight) = (pinnedOffset ?? Scroller.VerticalOffset, Scroller.ViewportHeight);
        currentPage = page;
        if (top < viewTop || bottom > viewTop + viewHeight)
        {
            var target = top - viewHeight / 3;
            if (paged)
            {
                var (rowTop, rowBottom) = layout.RowSpan(page);
                target = Math.Clamp(target, rowTop, Math.Max(rowTop, rowBottom - viewHeight));
            }
            pinnedOffset = Math.Clamp(target, 0, Math.Max(0, layout.Height - viewHeight));
            Scroller.ScrollToVerticalOffset(pinnedOffset.Value);
        }
        if (left < Scroller.HorizontalOffset || right > Scroller.HorizontalOffset + Scroller.ViewportWidth)
            Scroller.ScrollToHorizontalOffset((left + right - Scroller.ViewportWidth) / 2);
        SetCurrentPage(page);
    }

    /// <summary>Wo die Treffer einer Seite stehen, auf dem Render-Thread; die Seite ist meist gerade gerendert und noch offen.</summary>
    async void LoadHitRects(int page)
    {
        if (renderer is not { } reader || busy || !hitRectsPending.Add(page)) return;
        var generation = hitGeneration;
        var ranges = hits.Where(hit => hit.Page == page).Select(hit => (hit.Start, hit.Length)).ToArray();
        try
        {
            var rects = await reader.Invoke(document => document.TextRects(page, ranges));
            if (reader != renderer || generation != hitGeneration) return;
            hitRects[page] = rects;
            searchVersion++;
            RedrawMarks();
            if (currentHit >= 0 && hits[currentHit].Page == page) RevealCurrentHit(); // wartet ShowHit darauf?
        }
        catch (Exception e) when (e is PdfException or TaskCanceledException)
        {
            // Die Seite bleibt ohne Marken und als angefragt stehen; gezählt und angesprungen wird der Treffer trotzdem.
        }
    }

    void RedrawMarks()
    {
        foreach (var (page, slot) in slots) DrawMarks(slot, page);
    }

    /// <summary>
    /// Marken einer sichtbaren Seite; nur neu, wenn sich Treffer, Seite oder Drehung geändert haben. Fehlt die Lage noch,
    /// bleibt der Schlüssel offen: LoadHitRects lehnt während einer Änderung ab, und die Seite bliebe sonst ohne Marken.
    /// </summary>
    void DrawMarks(PageSlot slot, int page)
    {
        var key = (page, searchVersion, quarterTurns[page]);
        if (slot.MarkedFor == key) return;
        slot.Marks.Children.Clear();
        DrawSelection(slot, page); // markierter Text liegt in derselben Leinwand, unter den Treffermarken
        var first = hits.FindIndex(hit => hit.Page == page);
        if (first >= 0 && !hitRects.ContainsKey(page))
        {
            slot.MarkedFor = (-1, -1, -1);
            LoadHitRects(page); // hitRectsPending verhindert doppelte Aufträge
            return;
        }
        slot.MarkedFor = key;
        if (first < 0) return;
        var rects = hitRects[page];
        for (var i = 0; i < rects.Length; i++)
            foreach (var rect in rects[i])
            {
                var area = PageLayout.Turn(rect, quarterTurns[page]);
                var mark = new Rectangle { Width = area.Width, Height = area.Height, Fill = first + i == currentHit ? CurrentHitBrush : HitBrush };
                Canvas.SetLeft(mark, area.X);
                Canvas.SetTop(mark, area.Y);
                slot.Marks.Children.Add(mark);
            }
    }

    /// <summary>
    /// Liest den Text aller noch ungelesenen Seiten, ab der aktuellen und nur, wenn der Render-Thread sonst nichts zu tun
    /// hat. Eine Seite je Aufgabe: dazwischen kommen Bildaufträge zum Zug. Ändert sich das Dokument oder wird die Suche
    /// neu gestartet, beginnt die Kette von vorn.
    /// </summary>
    async void ReadTexts()
    {
        if (readingTexts || !searchOpen || renderer is not { } reader || pageTexts.Length == 0) return;
        readingTexts = true;
        var generation = textReading;
        try
        {
            var start = currentPage;
            for (var step = 0; step < pageTexts.Length; step++)
            {
                var page = (start + step) % pageTexts.Length;
                if (pageTexts[page] is not null) continue;
                string text;
                try { text = await reader.Invoke(document => document.PageText(page), whenIdle: true); }
                catch (PdfException) { text = ""; } // kaputte Seite: zählt als ohne Text
                if (reader != renderer || generation != textReading) return;
                pageTexts[page] = text;
                if (searchTerm.Length > 0)
                {
                    var before = hits.Count;
                    AddHits(page);
                    if (currentHit < 0 && hits.Count > 0) ShowHit(FirstHitFrom(currentPage));
                    else if (hits.Count != before)
                    {
                        searchVersion++;
                        RedrawMarks();
                    }
                }
                ShowSearchStatus();
            }
        }
        catch (TaskCanceledException)
        {
            // Renderer beendet (Neuladen, Fenster zu); ein neuer startet die Kette über FindAgain.
        }
        finally
        {
            readingTexts = false;
            if (generation != textReading) ReadTexts();
        }
    }

    void ShowSearchStatus()
    {
        if (!searchOpen) return;
        var read = pageTexts.Count(text => text is not null);
        var reading = read < pageTexts.Length;
        var percent = read * 100 / Math.Max(1, pageTexts.Length);
        SearchStatus.Text =
            searchTerm.Length == 0 ? "" :
            hits.Count > 0 ? $"{currentHit + 1} / {hits.Count}{(reading ? " …" : "")}" :
            reading ? $"sucht … {percent} %" :
            pageTexts.All(text => text!.Length == 0) ? "kein Text (Scan?)" : "keine Treffer";
    }
}
