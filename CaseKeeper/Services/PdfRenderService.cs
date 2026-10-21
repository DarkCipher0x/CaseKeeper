using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace CaseKeeper.Services;

/// <summary>
/// Wraps the built-in Windows PDF renderer (Windows.Data.Pdf), so no third-party PDF viewer is
/// needed. Works from bytes rather than a file path, because a protected library's documents
/// are only ever decrypted in memory.
/// Page sizes are in device-independent pixels (1/96 inch).
/// </summary>
/// <remarks>
/// Pages are drawn in software (Direct3D's WARP rasteriser) on a device this class owns and releases
/// itself, never with PdfPage.RenderToStreamAsync. That method draws on a graphics-card device that
/// Windows keeps in a process-wide cache and only lets go of while the process is exiting, after the
/// graphics driver has already unloaded. Some drivers (seen with AMD Radeon) then fault, and the app
/// ends with 0xC0000409 instead of closing cleanly. Software drawing is also identical on every PC.
/// </remarks>
public sealed class PdfRenderer : IDisposable
{
    private readonly PdfDocument _pdf;
    private readonly InMemoryRandomAccessStream _stream;   // must outlive the document
    private readonly object _gate = new();                // one D3D device context: one page at a time
    private SoftwarePageRenderer _renderer;               // created on first render; page counts don't need it
    private bool _disposed;

    private PdfRenderer(PdfDocument pdf, InMemoryRandomAccessStream stream)
    {
        _pdf = pdf;
        _stream = stream;
    }

    public int PageCount => (int)_pdf.PageCount;

    public static async Task<PdfRenderer> OpenAsync(byte[] content)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(content.AsBuffer());
        stream.Seek(0);
        try
        {
            var pdf = await PdfDocument.LoadFromStreamAsync(stream);
            return new PdfRenderer(pdf, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public (double Width, double Height) GetPageSize(int index)
    {
        using var page = _pdf.GetPage((uint)index);
        return (page.Size.Width, page.Size.Height);
    }

    /// <summary>Renders a page to PNG bytes. <paramref name="scale"/> 1.0 = 96 dpi.</summary>
    public Task<byte[]> RenderPngAsync(int index, double scale, int maxDimension = 0) => Task.Run(() =>
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _pdf.GetPage((uint)index);
            double w = page.Size.Width * scale, h = page.Size.Height * scale;
            int cap = maxDimension > 0 ? Math.Min(maxDimension, SoftwarePageRenderer.MaxDimension) : SoftwarePageRenderer.MaxDimension;
            if (Math.Max(w, h) > cap)
            {
                var f = cap / Math.Max(w, h);
                w *= f; h *= f;
            }
            _renderer ??= new SoftwarePageRenderer();
            int width = (int)Math.Max(1, w), height = (int)Math.Max(1, h);
            var pixels = _renderer.Render(page, width, height);
            return EncodePng(pixels, width, height);
        }
    });

    private static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static BitmapImage ToBitmap(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static async Task<int> CountPagesAsync(byte[] content)
    {
        using var r = await OpenAsync(content);
        return r.PageCount;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _renderer?.Dispose();
            _stream?.Dispose();
        }
    }
}

/// <summary>
/// Draws PDF pages through IPdfRendererNative onto a WARP (software) Direct3D 11 device and reads the
/// pixels back. Plain COM calls through the interfaces' function tables; every reference is released
/// in Dispose, so nothing is left for Windows to tidy up at process exit.
/// </summary>
internal sealed class SoftwarePageRenderer : IDisposable
{
    /// <summary>Largest texture side Direct3D 11 guarantees.</summary>
    public const int MaxDimension = 16384;

    private IntPtr _device, _context, _pdfRenderer;

    public SoftwarePageRenderer()
    {
        try
        {
            Check(D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0,
                SdkVersion, out _device, out _, out _context), "create the software graphics device");
            var dxgiDevice = QueryInterface(_device, IidDxgiDevice);
            try { Check(PdfCreateRenderer(dxgiDevice, out _pdfRenderer), "create the PDF renderer"); }
            finally { Marshal.Release(dxgiDevice); }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Returns the page as 32-bit BGRA pixels, <paramref name="width"/> × 4 bytes per row.</summary>
    public byte[] Render(PdfPage page, int width, int height)
    {
        IntPtr target = IntPtr.Zero, surface = IntPtr.Zero, staging = IntPtr.Zero, pageUnknown = IntPtr.Zero;
        try
        {
            var desc = new Texture2DDesc
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = FormatB8G8R8A8,
                SampleCount = 1, Usage = UsageDefault, BindFlags = BindRenderTarget | BindShaderResource
            };
            target = CreateTexture2D(ref desc);
            surface = QueryInterface(target, IidDxgiSurface);

            pageUnknown = WinRT.MarshalInspectable<object>.FromManaged(page);
            var render = new PdfRenderParams
            {
                DestinationWidth = (uint)width, DestinationHeight = (uint)height,
                BackgroundR = 1, BackgroundG = 1, BackgroundB = 1, BackgroundA = 1, IgnoreHighContrast = 1
            };
            Check(Call<RenderPageToSurfaceFn>(_pdfRenderer, 3)(_pdfRenderer, pageUnknown, surface, default, ref render), "draw the page");

            desc.Usage = UsageStaging;
            desc.BindFlags = 0;
            desc.CpuAccessFlags = CpuAccessRead;
            staging = CreateTexture2D(ref desc);
            Call<CopyResourceFn>(_context, 47)(_context, staging, target);

            Check(Call<MapFn>(_context, 14)(_context, staging, 0, MapRead, 0, out var mapped), "read the page back");
            try
            {
                int stride = width * 4;
                var pixels = new byte[stride * height];
                for (int y = 0; y < height; y++)
                    Marshal.Copy(mapped.Data + y * (int)mapped.RowPitch, pixels, y * stride, stride);
                return pixels;
            }
            finally { Call<UnmapFn>(_context, 15)(_context, staging, 0); }
        }
        finally
        {
            Release(ref pageUnknown);
            Release(ref staging);
            Release(ref surface);
            Release(ref target);
        }
    }

    public void Dispose()
    {
        Release(ref _pdfRenderer);
        if (_context != IntPtr.Zero)
        {
            Call<VoidFn>(_context, 110)(_context);     // ClearState
            Call<VoidFn>(_context, 111)(_context);     // Flush
        }
        Release(ref _context);
        Release(ref _device);
    }

    private IntPtr CreateTexture2D(ref Texture2DDesc desc)
    {
        Check(Call<CreateTexture2DFn>(_device, 5)(_device, ref desc, IntPtr.Zero, out var texture), "create a page buffer");
        return texture;
    }

    private static IntPtr QueryInterface(IntPtr unknown, Guid iid)
    {
        Check(Marshal.QueryInterface(unknown, in iid, out var result), "reach a graphics interface");
        return result;
    }

    private static void Release(ref IntPtr unknown)
    {
        if (unknown != IntPtr.Zero) Marshal.Release(unknown);
        unknown = IntPtr.Zero;
    }

    private static T Call<T>(IntPtr comObject, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"Couldn't {what} (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
    }

    // ------------------------------------------------------------------ Direct3D 11 / Windows.Data.Pdf interop
    private const int DriverTypeWarp = 5, SdkVersion = 7;
    private const uint CreateDeviceBgraSupport = 0x20, FormatB8G8R8A8 = 87, UsageDefault = 0, UsageStaging = 3,
        BindShaderResource = 0x8, BindRenderTarget = 0x20, CpuAccessRead = 0x20000, MapRead = 1;
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidDxgiSurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    /// <summary>PDF_RENDER_PARAMS. An empty source rectangle means the whole page.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdfRenderParams
    {
        public float SourceLeft, SourceTop, SourceRight, SourceBottom;
        public uint DestinationWidth, DestinationHeight;
        public float BackgroundR, BackgroundG, BackgroundB, BackgroundA;
        public byte IgnoreHighContrast;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DFn(IntPtr device, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RenderPageToSurfaceFn(IntPtr renderer, IntPtr page, IntPtr surface, Point offset, ref PdfRenderParams args);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceFn(IntPtr context, IntPtr destination, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapFn(IntPtr context, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out MappedSubresource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapFn(IntPtr context, IntPtr resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void VoidFn(IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("Windows.Data.Pdf.dll", ExactSpelling = true)]
    private static extern int PdfCreateRenderer(IntPtr dxgiDevice, out IntPtr renderer);
}
