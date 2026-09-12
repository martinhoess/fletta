using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Windows;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using Windows.Graphics.Printing;

namespace Fletta;

/// <summary>
/// Die Quelle, die der Dialog befragt: Vorschau über IPrintPreviewDxgiPackageTarget, Ausgabe über
/// Direct2D in das Paketziel. Lebt genau einen Auftrag lang.
/// </summary>
internal sealed class PrintSource : IDisposable
{
    // ID_PREVIEWPACKAGETARGET_DXGI ist derselbe GUID wie IID_IPrintPreviewDxgiPackageTarget.
    static readonly Guid DxgiPreview = new("1a6dd0ad-1e2a-4e99-a5ba-91f17818290e");

    static readonly StrategyBasedComWrappers Rcw = new(); // Hüllen für die Ziele des Drucksystems

    // Vorschau und Ausgabe zeichnen auf fremden Threads; Dispose kommt aus PrintTask.Completed und darf das
    // Direct3D-Gerät nicht wegziehen, während noch jemand darauf malt — sonst stürzt Fletta ohne Meldung ab.
    readonly Lock gate = new();

    readonly PrintJob job;
    PrintGpu? gpu;
    bool disposed;

    public PrintSource(PrintJob job) => this.job = job;

    /// <summary>Ruft draw mit dem GPU-Gerät auf und hält es solange am Leben. Nach Dispose gibt es kein Gerät mehr.</summary>
    public void Use(Action<PrintGpu> draw)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            draw(gpu ??= new PrintGpu());
        }
    }

    /// <summary>Als WinRT-IPrintDocumentSource für SetSource; die Vtables baut PrintCcw.</summary>
    public IPrintDocumentSource AsDocumentSource()
    {
        var ptr = PrintCcw.Instance.GetOrCreateComInterfaceForObject(this, CreateComInterfaceFlags.None);
        try { return WinRT.MarshalInspectable<IPrintDocumentSource>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public PreviewPages GetPreviewPageCollection(nint docPackageTarget)
    {
        var target = (IPrintDocumentPackageTarget)Rcw.GetOrCreateObjectForComInstance(docPackageTarget, CreateObjectFlags.UniqueInstance);
        target.GetPackageTarget(DxgiPreview, DxgiPreview, out var dxgi);
        try
        {
            var preview = (IPrintPreviewDxgiPackageTarget)Rcw.GetOrCreateObjectForComInstance(dxgi, CreateObjectFlags.UniqueInstance);
            return new PreviewPages(job, this, preview);
        }
        finally { Marshal.Release(dxgi); }
    }

    public void MakeDocument(nint printTaskOptions, nint docPackageTarget)
    {
        var options = WinRT.MarshalInspectable<PrintTaskOptions>.FromAbi(printTaskOptions);
        var pages = job.SelectedPages(options);
        try
        {
            Use(gpu => gpu.PrintPages(docPackageTarget, job, pages, options));
            job.Notify(pages.Length == 1 ? "1 Seite an den Drucker gesendet" : $"{pages.Length} Seiten an den Drucker gesendet");
        }
        catch (Exception e)
        {
            // Auch ein geschlossenes Fenster (TaskCanceledException vom Renderer) landet hier.
            var target = (IPrintDocumentPackageTarget)Rcw.GetOrCreateObjectForComInstance(docPackageTarget, CreateObjectFlags.UniqueInstance);
            target.Cancel();
            job.Notify($"Drucken fehlgeschlagen: {e.Message}");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            gpu?.Dispose();
            gpu = null;
        }
    }
}

/// <summary>
/// COM-Hüllen für die zwei Objekte, die das Drucksystem bei uns aufruft. Von Hand, weil der generierte
/// Wrapper keine fremden IIDs annimmt: IPrintDocumentSource ist eine methodenlose WinRT-Schnittstelle
/// (zeigt auf die IInspectable-Vtable), IAgileObject erlaubt den Aufruf aus beliebigen Apartments.
/// Vtable-Reihenfolge wie in DocumentSource.h (SDK 10.0.26100).
/// </summary>
internal sealed unsafe class PrintCcw : ComWrappers
{
    public static readonly PrintCcw Instance = new();

    static readonly Guid PageSourceIid = new("a96bb1db-172e-4667-82b5-ad97a252318f");
    static readonly Guid PageCollectionIid = new("0b31cc62-d7ec-4747-9d6e-f2537d870f2b");
    static readonly Guid InspectableIid = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");
    static readonly Guid AgileIid = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    const int EFail = unchecked((int)0x80004005);

    static readonly ComInterfaceEntry* sourceEntries = CreateSourceEntries();
    static readonly ComInterfaceEntry* pagesEntries = CreatePagesEntries();

    static nint* Vtable(int slots)
    {
        var table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(PrintCcw), slots * sizeof(nint));
        GetIUnknownImpl(out table[0], out table[1], out table[2]);
        return table;
    }

    static ComInterfaceEntry* CreateSourceEntries()
    {
        var inspectable = Vtable(6);
        inspectable[3] = (nint)(delegate* unmanaged<nint, uint*, nint*, int>)&GetIids;
        inspectable[4] = (nint)(delegate* unmanaged<nint, nint*, int>)&GetRuntimeClassName;
        inspectable[5] = (nint)(delegate* unmanaged<nint, int*, int>)&GetTrustLevel;
        var pageSource = Vtable(5);
        pageSource[3] = (nint)(delegate* unmanaged<nint, nint, nint*, int>)&GetPreviewPageCollection;
        pageSource[4] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&MakeDocument;
        var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(PrintCcw), 4 * sizeof(ComInterfaceEntry));
        entries[0] = new() { IID = PageSourceIid, Vtable = (nint)pageSource };
        entries[1] = new() { IID = typeof(IPrintDocumentSource).GUID, Vtable = (nint)inspectable };
        entries[2] = new() { IID = InspectableIid, Vtable = (nint)inspectable };
        entries[3] = new() { IID = AgileIid, Vtable = (nint)inspectable };
        return entries;
    }

    static ComInterfaceEntry* CreatePagesEntries()
    {
        var collection = Vtable(5);
        collection[3] = (nint)(delegate* unmanaged<nint, uint, nint, int>)&Paginate;
        collection[4] = (nint)(delegate* unmanaged<nint, uint, float, float, int>)&MakePage;
        var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(PrintCcw), 2 * sizeof(ComInterfaceEntry));
        entries[0] = new() { IID = PageCollectionIid, Vtable = (nint)collection };
        entries[1] = new() { IID = AgileIid, Vtable = (nint)collection };
        return entries;
    }

    protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
    {
        switch (obj)
        {
            case PrintSource: count = 4; return sourceEntries;
            case PreviewPages: count = 2; return pagesEntries;
            default: count = 0; return null;
        }
    }

    protected override object CreateObject(nint externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();
    protected override void ReleaseObjects(System.Collections.IEnumerable objects) => throw new NotSupportedException();

    static T Self<T>(nint thisPtr) where T : class => ComInterfaceDispatch.GetInstance<T>((ComInterfaceDispatch*)thisPtr);

    /// <summary>Jede Ausnahme wird HRESULT: über die COM-Grenze darf keine .NET-Ausnahme fliegen.</summary>
    static int Guard(Action action)
    {
        try { action(); return 0; }
        catch (Exception e) { return e.HResult < 0 ? e.HResult : EFail; }
    }

    [UnmanagedCallersOnly]
    static int GetIids(nint thisPtr, uint* count, nint* iids) { *count = 0; *iids = 0; return 0; }

    [UnmanagedCallersOnly]
    static int GetRuntimeClassName(nint thisPtr, nint* name) => WindowsCreateString("Fletta.PrintSource", 18, name);

    [UnmanagedCallersOnly]
    static int GetTrustLevel(nint thisPtr, int* level) { *level = 0; return 0; }

    [UnmanagedCallersOnly]
    static int GetPreviewPageCollection(nint thisPtr, nint target, nint* collection)
    {
        *collection = 0;
        var result = 0;
        var hr = Guard(() =>
        {
            var pages = Self<PrintSource>(thisPtr).GetPreviewPageCollection(target);
            var unknown = Instance.GetOrCreateComInterfaceForObject(pages, CreateComInterfaceFlags.None);
            result = Marshal.QueryInterface(unknown, PageCollectionIid, out var pointer);
            Marshal.Release(unknown);
            *collection = pointer;
        });
        return hr != 0 ? hr : result;
    }

    [UnmanagedCallersOnly]
    static int MakeDocument(nint thisPtr, nint options, nint target) =>
        Guard(() => Self<PrintSource>(thisPtr).MakeDocument(options, target));

    [UnmanagedCallersOnly]
    static int Paginate(nint thisPtr, uint currentJobPage, nint options) =>
        Guard(() => Self<PreviewPages>(thisPtr).Paginate(currentJobPage, options));

    [UnmanagedCallersOnly]
    static int MakePage(nint thisPtr, uint desiredJobPage, float width, float height) =>
        Guard(() => Self<PreviewPages>(thisPtr).MakePage(desiredJobPage, width, height));

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, nint* hstring);
}

/// <summary>Vorschauseiten. Paginate legt die Seitenauswahl fest, MakePage liefert je ein Blattbild.</summary>
internal sealed class PreviewPages(PrintJob job, PrintSource source, IPrintPreviewDxgiPackageTarget target)
{
    const uint FinalPageCount = 0, ApplicationDefined = 0xFFFFFFFF;
    const float MaxPreviewPixels = 4_000_000f; // deckelt den Speicher bei großem Vorschaufenster

    int[] pages = [];

    public void Paginate(uint currentJobPage, nint printTaskOptions)
    {
        var options = printTaskOptions == 0 ? null : WinRT.MarshalInspectable<PrintTaskOptions>.FromAbi(printTaskOptions);
        pages = job.SelectedPages(options);
        target.SetJobPageCount(FinalPageCount, (uint)Math.Max(1, pages.Length));
    }

    public void MakePage(uint desiredJobPage, float width, float height)
    {
        if (pages.Length == 0) return;
        var number = desiredJobPage == ApplicationDefined ? 1 : desiredJobPage;
        if (number < 1 || number > pages.Length) return;
        if (width < 1 || height < 1) (width, height) = (612, 792); // Letter, falls der Dialog nichts vorgibt
        // Der Dialog nennt die Blattgröße in DIPs; wir verkleinern nur, wenn es zu viele Pixel würden.
        var scale = MathF.Min(1f, MathF.Sqrt(MaxPreviewPixels / (width * height)));
        int sheetWidth = Math.Max(1, (int)(width * scale)), sheetHeight = Math.Max(1, (int)(height * scale));
        source.Use(gpu =>
        {
            var pixels = job.RenderSheet(pages[number - 1], sheetWidth, sheetHeight);
            using var surface = gpu.CreateSurface(sheetWidth, sheetHeight, pixels);
            target.DrawPage(number, surface.NativePointer, 96f * scale, 96f * scale); // nie den Platzhalter 0xFFFFFFFF
        });
    }
}

/// <summary>Direct3D für die Vorschaubilder, Direct2D für die Ausgabe. Erst beim Drucken erzeugt.</summary>
internal sealed class PrintGpu : IDisposable
{
    const float PrintDpi = 300f;
    const float MaxPrintPixels = 40_000_000f; // A3 bei 300 dpi passt darunter

    readonly ID3D11Device device = Create();

    static ID3D11Device Create()
    {
        foreach (var type in (ReadOnlySpan<DriverType>)[DriverType.Hardware, DriverType.Warp])
            if (D3D11.D3D11CreateDevice(null, type, DeviceCreationFlags.BgraSupport, null!,
                                        out var created, out _, out _).Success && created is not null)
                return created;
        throw new InvalidOperationException("Kein Direct3D-Gerät für die Druckvorschau");
    }

    /// <summary>BGRA-Puffer als DXGI-Oberfläche; der Aufrufer gibt sie nach DrawPage frei.</summary>
    public unsafe IDXGISurface CreateSurface(int width, int height, byte[] bgra)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, // wie im D2D-Druckbeispiel von Microsoft
        };
        fixed (byte* data = bgra)
        {
            using var texture = device.CreateTexture2D(description, [new SubresourceData((nint)data, (uint)(width * 4))]);
            return texture.QueryInterface<IDXGISurface>();
        }
    }

    /// <summary>
    /// Jede Seite als ein Rasterbild in 300 dpi in eine Command-List, die geht als Seite ins Paketziel.
    /// Vektoriell später: statt RenderSheet ein EMF-HDC von FPDF_RenderPage füllen, daraus
    /// ID2D1Factory1.CreateGdiMetafile, und hier DrawGdiMetafile(metafile, imageable) statt DrawBitmap —
    /// der Rest dieser Schleife bleibt, wie er ist.
    /// </summary>
    public unsafe void PrintPages(nint packageTarget, PrintJob job, int[] pages, PrintTaskOptions options)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice, null);
        using var wic = new IWICImagingFactory();
        // Das Paketziel gehört dem Drucksystem: eigene Referenz, die die Hülle beim Dispose wieder abgibt.
        Marshal.AddRef(packageTarget);
        using var target = new SharpGen.Runtime.ComObject(packageTarget);
        using var control = d2dDevice.CreatePrintControl(wic, target,
            new PrintControlProperties { RasterDPI = PrintDpi, ColorSpace = ColorSpace.Srgb, FontSubset = PrintFontSubsetMode.Default });
        using var context = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        for (var i = 0; i < pages.Length; i++)
        {
            // PageSize und ImageableRect kommen in DIPs (1/96 Zoll) für genau diese Auftragsseite.
            var description = options.GetPageDescription((uint)(i + 1));
            var imageable = description.ImageableRect;
            var scale = MathF.Min(PrintDpi / 96f,
                MathF.Sqrt(MaxPrintPixels / MathF.Max(1, (float)(imageable.Width * imageable.Height))));
            int width = Math.Max(1, (int)(imageable.Width * scale)), height = Math.Max(1, (int)(imageable.Height * scale));
            var pixels = job.RenderSheet(pages[i], width, height);
            using var list = context.CreateCommandList();
            context.Target = list;
            context.BeginDraw();
            context.Clear(new Color4(1f, 1f, 1f, 1f));
            fixed (byte* data = pixels)
            {
                var properties = new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Ignore), 96f * scale, 96f * scale);
                using var bitmap = context.CreateBitmap(new SizeI(width, height), (nint)data, (uint)(width * 4), properties);
                context.DrawBitmap(bitmap, new Vortice.Mathematics.Rect((float)imageable.X, (float)imageable.Y,
                    (float)imageable.Width, (float)imageable.Height), 1f, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
            }
            context.EndDraw();
            context.Target = null;
            list.Close();
            control.AddPage(list, new Vortice.Mathematics.Size((float)description.PageSize.Width, (float)description.PageSize.Height), null, out _, out _);
        }
        control.Close();
    }

    public void Dispose() => device.Dispose();
}
