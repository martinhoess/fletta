using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fletta.Pdfium;

namespace Fletta;

/// <summary>
/// Pixelgröße ist die angezeigte, also schon gedrehte Größe; QuarterTurns 0–3 im Uhrzeigersinn.
/// Thumbnail trennt die Aufträge der Seitenleiste von denen der Hauptansicht.
/// </summary>
public readonly record struct RenderRequest(int Page, int PixelWidth, int PixelHeight, double DpiX, double DpiY,
                                           int QuarterTurns, bool Thumbnail = false);

public sealed record RenderResult(RenderRequest Request, BitmapSource? Bitmap, string? Error);

/// <summary>
/// Besitzt das Dokument und rendert auf einem eigenen Thread; PDFium sieht nur diesen Thread.
/// Die Warteschlange ersetzt der UI-Thread bei jedem Scrollen und Zoomen komplett, damit
/// veraltete Aufträge gar nicht erst gerendert werden.
/// </summary>
public sealed class PageRenderer : IDisposable
{
    static readonly Size FallbackPageSize = new(595, 842); // A4, falls eine Seite keine lesbare Größe hat

    readonly object gate = new();
    readonly Dispatcher ui;
    readonly Action<RenderResult> deliver;
    readonly TaskCompletionSource<IReadOnlyList<Size>> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Queue<Job> jobs = new();
    // Aufträge, die gerade gerendert oder schon fertig, aber noch nicht zugestellt sind: die filtert
    // Request heraus, sonst stellte das Fenster sie in dieser Lücke erneut an (Seite doppelt gerendert).
    readonly HashSet<RenderRequest> inFlight = [];
    RenderRequest[] queue = [];
    bool stopped;  // nur UI-Thread schreibt, siehe Dispose
    bool finished; // Thread ist durch; unter gate

    /// <summary>Eine Aufgabe auf dem Render-Thread (Drucken, Text); Cancel, wenn sie nie drankommt.</summary>
    sealed record Job(Action<PdfDocument> Run, Action Cancel);

    public PageRenderer(string path, Dispatcher ui, Action<RenderResult> deliver)
    {
        this.ui = ui;
        this.deliver = deliver;
        var thread = new Thread(() => Run(path)) { IsBackground = true, Name = "PDFium" };
        thread.SetApartmentState(ApartmentState.STA); // die WriteableBitmaps entstehen auf diesem Thread
        thread.Start();
    }

    /// <summary>Seitengrößen in Punkten, sobald das Dokument offen ist. Schlägt mit PdfException fehl.</summary>
    public Task<IReadOnlyList<Size>> Opened => opened.Task;

    /// <summary>Erfüllt, sobald der Thread PDFium nicht mehr anfasst und die Datei geschlossen ist.</summary>
    public Task Closed => closed.Task;

    /// <summary>
    /// Führt work auf dem Render-Thread aus, vor den wartenden Bildaufträgen — für alles, was PDFium
    /// braucht (Drucken, Seitentext). Ausnahmen landen im Task; endet der Thread vorher, wird er abgebrochen.
    /// </summary>
    public Task<T> Invoke<T>(Func<PdfDocument, T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new Job(document =>
        {
            try { done.SetResult(work(document)); }
            catch (Exception e) { done.SetException(e); } // nichts darf den Render-Thread beenden
        }, () => done.TrySetCanceled());
        lock (gate)
        {
            if (stopped || finished) job.Cancel();
            else
            {
                jobs.Enqueue(job);
                Monitor.Pulse(gate);
            }
        }
        return done.Task;
    }

    public Task Invoke(Action<PdfDocument> work) => Invoke(document =>
    {
        work(document);
        return true;
    });

    /// <summary>Ersetzt alle offenen Aufträge; der erste wird als Nächstes gerendert.</summary>
    public void Request(RenderRequest[] next)
    {
        lock (gate)
        {
            queue = inFlight.Count == 0 ? next : [.. next.Where(request => !inFlight.Contains(request))];
            Monitor.Pulse(gate);
        }
    }

    /// <summary>
    /// Beendet den Thread nach dem laufenden Auftrag, dessen Ergebnis wird verworfen. Nur aus dem
    /// UI-Thread aufrufen: dort liest auch die Zustellung das Flag.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            stopped = true;
            Monitor.Pulse(gate);
        }
    }

    void Run(string path)
    {
        try { RenderUntilStopped(path); }
        finally
        {
            lock (gate)
            {
                finished = true;
                while (jobs.TryDequeue(out var job)) job.Cancel();
            }
            closed.SetResult();
        }
    }

    void RenderUntilStopped(string path)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path);
            opened.SetResult(SizesOf(document));
        }
        catch (PdfException e)
        {
            opened.SetException(e);
            return;
        }

        using (document)
        {
            while (TryTake(out var job, out var task))
            {
                if (task is not null)
                {
                    App.Mark("aufgabe");
                    task.Run(document);
                    continue;
                }
                App.Mark(job.Thumbnail ? "auftrag miniatur" : "auftrag seite");
                RenderResult result;
                try
                {
                    result = new(job, document.Render(job.Page, job.PixelWidth, job.PixelHeight, job.DpiX, job.DpiY, job.QuarterTurns), null);
                }
                catch (Exception e) when (e is PdfException or OutOfMemoryException)
                {
                    // Eine kaputte oder zu große Seite darf den Thread nicht beenden, sonst stirbt der Prozess.
                    result = new(job, null, e.Message);
                }
                App.Mark(job.Thumbnail ? "gerendert miniatur" : "gerendert seite");
                ui.InvokeAsync(() =>
                {
                    lock (gate) inFlight.Remove(result.Request);
                    if (!stopped) deliver(result);
                });
            }
        }
    }

    /// <summary>Nächste Aufgabe (zuerst) oder nächster Bildauftrag; false, sobald gestoppt.</summary>
    bool TryTake(out RenderRequest job, out Job? task)
    {
        lock (gate)
        {
            while (!stopped && queue.Length == 0 && jobs.Count == 0) Monitor.Wait(gate);
            (job, task) = (default, null);
            if (stopped) return false;
            if (jobs.TryDequeue(out task)) return true;
            job = queue[0];
            queue = queue[1..];
            inFlight.Add(job);
            return true;
        }
    }

    /// <summary>Seitengrößen in Punkten; nach einer Änderung auf dem Render-Thread neu zu lesen.</summary>
    public static Size[] SizesOf(PdfDocument document) => [.. Enumerable.Range(0, document.PageCount).Select(page => SizeOrFallback(document, page))];

    static Size SizeOrFallback(PdfDocument document, int page)
    {
        try { return document.PageSize(page); }
        catch (PdfException) { return FallbackPageSize; }
    }
}
