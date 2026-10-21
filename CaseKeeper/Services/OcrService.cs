using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Xml.Linq;
using CaseKeeper.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CaseKeeper.Services;

/// <summary>
/// Extracts searchable text from documents. Uses the OCR engine built into Windows
/// (Windows.Media.Ocr) for PDFs and images, and reads Word (.docx) and text files directly.
/// Everything works from bytes, so a protected library never writes a decrypted file to disk.
/// </summary>
public static class OcrService
{
    private static OcrEngine _engine;
    private static bool _engineChecked;

    public static OcrEngine Engine
    {
        get
        {
            if (!_engineChecked)
            {
                _engineChecked = true;
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                foreach (var tag in new[] { "en-GB", "en-US", "en" })
                {
                    if (_engine != null) break;
                    try
                    {
                        var lang = new Language(tag);
                        if (OcrEngine.IsLanguageSupported(lang)) _engine = OcrEngine.TryCreateFromLanguage(lang);
                    }
                    catch { /* ignore */ }
                }
            }
            return _engine;
        }
    }

    public static string EngineDescription =>
        Engine == null
            ? "Not available – install an English language pack with 'Optical character recognition' in Windows Settings > Time & language > Language & region."
            : $"Windows OCR ({Engine.RecognizerLanguage.DisplayName})";

    /// <summary>
    /// A separate engine for one background worker, so several documents can be scanned at once
    /// without sharing an engine between threads.
    /// </summary>
    public static OcrEngine CreateEngine()
    {
        if (Engine == null) return null;
        try { return OcrEngine.TryCreateFromLanguage(Engine.RecognizerLanguage) ?? Engine; }
        catch { return Engine; }
    }

    /// <summary>Returns the text of each page. Throws if text could not be extracted.</summary>
    public static async Task<List<string>> ExtractAsync(byte[] content, string extension, IProgress<string> progress = null,
        OcrEngine engine = null)
    {
        var ext = extension.ToLowerInvariant();
        if (ext == ".pdf") return await ExtractPdfAsync(content, progress, engine);
        if (FileKinds.IsImage(ext)) return new List<string> { await RecogniseImageAsync(content, engine) };
        if (ext == ".docx") return new List<string> { ExtractDocx(content) };
        if (FileKinds.IsText(ext)) return new List<string> { DecodeText(content) };
        throw new NotSupportedException("This file type can't be scanned for text.");
    }

    public static bool CanExtract(string extension)
    {
        var ext = extension.ToLowerInvariant();
        return ext == ".pdf" || ext == ".docx" || FileKinds.IsImage(ext) || FileKinds.IsText(ext);
    }

    /// <summary>Reads a text file, honouring a byte order mark when there is one.</summary>
    public static string DecodeText(byte[] content)
    {
        using var ms = new MemoryStream(content);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static OcrEngine RequireEngine() =>
        Engine ?? throw new InvalidOperationException("Windows OCR is not available on this PC.");

    private static async Task<List<string>> ExtractPdfAsync(byte[] content, IProgress<string> progress, OcrEngine engine)
    {
        engine ??= RequireEngine();
        using var pdf = await PdfRenderer.OpenAsync(content);
        var pages = new List<string>();
        int max = (int)OcrEngine.MaxImageDimension;
        // ~190 dpi gives good accuracy for typed court documents; the performance profile adjusts it
        double scale = Performance.OcrRenderScale;
        for (int i = 0; i < pdf.PageCount; i++)
        {
            progress?.Report($"page {i + 1} of {pdf.PageCount}");
            var png = await pdf.RenderPngAsync(i, scale, max);
            pages.Add(await RecogniseImageAsync(png, engine));
        }
        return pages;
    }

    private static async Task<string> RecogniseImageAsync(byte[] image, OcrEngine engine = null)
    {
        engine ??= RequireEngine();
        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(image.AsBuffer());
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var transform = new BitmapTransform();
        uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        uint max = OcrEngine.MaxImageDimension;
        if (w > max || h > max)
        {
            double f = (double)max / Math.Max(w, h);
            // Scaled sizes apply before orientation, so scale the raw pixel dimensions
            transform.ScaledWidth = (uint)(decoder.PixelWidth * f);
            transform.ScaledHeight = (uint)(decoder.PixelHeight * f);
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }

    /// <summary>Reads the body text from a Word .docx file (no Word installation needed).</summary>
    public static string ExtractDocx(byte[] content)
    {
        using var ms = new MemoryStream(content);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a valid Word document.");
        using var s = entry.Open();
        var xml = XDocument.Load(s);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        foreach (var p in xml.Descendants(w + "p"))
        {
            foreach (var node in p.Descendants())
            {
                if (node.Name == w + "t") sb.Append(node.Value);
                else if (node.Name == w + "tab") sb.Append('\t');
                else if (node.Name == w + "br") sb.AppendLine();
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
