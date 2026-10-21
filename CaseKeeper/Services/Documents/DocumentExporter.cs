using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CaseKeeper.Models;

namespace CaseKeeper.Services.Documents;

public enum ExportFormat { Word, Pdf, Html }

public sealed class ExportResult
{
    public string Path { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> MissingFields { get; } = new();
}

/// <summary>
/// Writes documents out as Word, PDF or HTML. When a Word layout (an imported letterhead or
/// template) is involved, the Word file is filled in first; for PDF that file is then converted by
/// Microsoft Word or LibreOffice if either is installed, and otherwise by CaseKeeper's own renderer.
/// Files saved inside a password-protected library are encrypted like everything else in it.
/// </summary>
public static class DocumentExporter
{
    public static string Extension(ExportFormat f) => f switch { ExportFormat.Word => ".docx", ExportFormat.Pdf => ".pdf", _ => ".html" };

    public static string Filter(ExportFormat f) => f switch
    {
        ExportFormat.Word => "Word document|*.docx",
        ExportFormat.Pdf => "PDF document|*.pdf",
        _ => "Web page (HTML)|*.html"
    };

    /// <param name="wordLayout">An imported Word letterhead or template to fill, or null.</param>
    /// <param name="fields">Values for {{fields}} in a Word layout.</param>
    public static Task<ExportResult> ExportAsync(DocModel doc, Letterhead lh, ExportFormat format, string path,
        byte[] wordLayout = null, IReadOnlyDictionary<string, string> fields = null)
    {
        return Sta.RunAsync(() =>
        {
            var result = new ExportResult { Path = path };
            var bytes = Render(doc, lh, format, wordLayout, fields, result);
            App.Library.WriteExport(path, bytes);
            App.Library.Db.Audit($"export.{format.ToString().ToLowerInvariant()}", "export", null,
                $"Exported “{doc.Title}” as {format}", DescribeLocation(path) + (result.Note.Length > 0 ? "\n" + result.Note : ""));
            AppLog.Info("Export", $"Exported a {format} document ({bytes.Length:N0} bytes)");
            return result;
        });
    }

    /// <summary>Produces the file's bytes without saving, e.g. for importing a letter back into the library.</summary>
    public static byte[] Render(DocModel doc, Letterhead lh, ExportFormat format, byte[] wordLayout,
        IReadOnlyDictionary<string, string> fields, ExportResult result = null)
    {
        result ??= new ExportResult();
        fields ??= new Dictionary<string, string>();
        switch (format)
        {
            case ExportFormat.Word:
                return wordLayout != null
                    ? DocxDocWriter.FillTemplate(wordLayout, fields, doc.Blocks, result.MissingFields, doc.Title)
                    : DocxDocWriter.Render(doc, lh);
            case ExportFormat.Html:
                return System.Text.Encoding.UTF8.GetBytes(HtmlDocWriter.Render(doc, lh));
            default:
                if (wordLayout != null)
                {
                    var docx = DocxDocWriter.FillTemplate(wordLayout, fields, doc.Blocks, result.MissingFields, doc.Title);
                    if (OfficeConverter.TryConvertToPdf(docx, out var pdf, out var via))
                    {
                        result.Note = $"Converted to PDF with {via}, keeping the Word layout.";
                        return pdf;
                    }
                    result.Note = "Microsoft Word or LibreOffice wasn't found, so the PDF was laid out by CaseKeeper " +
                                  "using your letterhead details instead of the Word layout. Export as Word to keep the exact layout.";
                }
                return new PdfDocWriter(doc, lh).Render();
        }
    }

    /// <summary>Where a file went, without the Windows user folder in it.</summary>
    public static string DescribeLocation(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var name = System.IO.Path.GetFileName(full);
            return App.Library.IsInsideExports(full) ? $"Saved in the library's Exports folder as {name}" : $"Saved outside the library as {name}";
        }
        catch { return ""; }
    }
}

/// <summary>Runs work on a dedicated STA thread, which PDFsharp-WPF and Office automation both need.</summary>
public static class Sta
{
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Priority = Performance.BackgroundPriority;
        t.Start();
        return tcs.Task;
    }
}

/// <summary>
/// Converts .docx to PDF using Microsoft Word (if installed) or LibreOffice (if installed). The
/// file is written to a private temporary folder that is removed straight afterwards. Document
/// properties are not carried into the PDF.
/// </summary>
public static class OfficeConverter
{
    public static bool WordAvailable => Type.GetTypeFromProgID("Word.Application") != null;

    public static string LibreOfficePath
    {
        get
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public static string Describe() =>
        WordAvailable ? "Microsoft Word found: Word layouts keep their exact design in PDFs."
        : LibraryAvailable() ? "LibreOffice found: Word layouts keep their design in PDFs."
        : "Neither Microsoft Word nor LibreOffice is installed: PDFs of Word layouts are laid out by CaseKeeper. Word exports are unaffected.";

    private static bool LibraryAvailable() => LibreOfficePath != null;

    public static bool TryConvertToPdf(byte[] docx, out byte[] pdf, out string via)
    {
        pdf = null;
        via = "";
        var dir = Path.Combine(LibraryService.TempCopiesFolder, "convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "document.docx");
        var output = Path.Combine(dir, "document.pdf");
        try
        {
            File.WriteAllBytes(input, docx);
            if (WordAvailable && TryWord(input, output)) via = "Microsoft Word";
            else if (LibreOfficePath != null && TryLibreOffice(input, dir)) via = "LibreOffice";
            if (via.Length > 0 && File.Exists(output))
            {
                pdf = File.ReadAllBytes(output);
                return pdf.Length > 0;
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Export", "Office conversion to PDF failed", ex);
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* removed with the other temporary copies at exit */ }
        }
    }

    private static bool TryWord(string input, string output)
    {
        object word = null, doc = null;
        try
        {
            var type = Type.GetTypeFromProgID("Word.Application");
            if (type == null) return false;
            word = Activator.CreateInstance(type);
            dynamic w = word;
            w.Visible = false;
            w.DisplayAlerts = 0;
            doc = w.Documents.Open(input, false, true, false);   // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles
            dynamic d = doc;
            // 17 = wdExportFormatPDF. IncludeDocProps:false keeps author details out of the PDF.
            d.ExportAsFixedFormat(output, 17, false, 0, 0, 1, 1, 0, false, true, 1, true, true, false);
            d.Close(0);
            return File.Exists(output);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Export", "Microsoft Word couldn't convert the document", ex);
            return false;
        }
        finally
        {
            try { if (word != null) ((dynamic)word).Quit(0); } catch { /* already gone */ }
            if (doc != null) try { Marshal.FinalReleaseComObject(doc); } catch { /* ignore */ }
            if (word != null) try { Marshal.FinalReleaseComObject(word); } catch { /* ignore */ }
        }
    }

    private static bool TryLibreOffice(string input, string outDir)
    {
        var psi = new ProcessStartInfo(LibreOfficePath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[] { "--headless", "--norestore", "--convert-to", "pdf", "--outdir", outDir, input }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return false;
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(true); } catch { /* ignore */ }
            return false;
        }
        return p.ExitCode == 0;
    }
}
