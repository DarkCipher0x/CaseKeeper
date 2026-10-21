using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using Microsoft.Win32;

namespace CaseKeeper.Views.Forms;

/// <summary>Saving lists and documents: one Save dialog offering PDF, Word, web page or spreadsheet.</summary>
public static class ExportHelper
{
    private const string AllFormats = "PDF document|*.pdf|Word document|*.docx|Web page (HTML)|*.html|Spreadsheet (CSV)|*.csv";
    private const string DocFormats = "PDF document|*.pdf|Word document|*.docx|Web page (HTML)|*.html";

    /// <summary>Asks where to save, defaulting to the library's Exports folder. Null if cancelled.</summary>
    public static string AskPath(Window owner, string suggestedName, bool allowCsv, out ExportFormat? format, int defaultIndex = 1)
    {
        format = null;
        var dlg = new SaveFileDialog
        {
            FileName = LibraryService.SafeFileName(suggestedName),
            Filter = allowCsv ? AllFormats : DocFormats,
            FilterIndex = defaultIndex,
            InitialDirectory = App.Library.ExportsFolder,
            AddExtension = true
        };
        if (dlg.ShowDialog(owner) != true) return null;
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        format = ext switch
        {
            ".docx" => ExportFormat.Word,
            ".html" or ".htm" => ExportFormat.Html,
            ".csv" => null,
            _ => ExportFormat.Pdf
        };
        return dlg.FileName;
    }

    /// <summary>Exports a table of records, as a formatted document or a CSV spreadsheet.</summary>
    public static async Task ExportTableAsync(Window owner, string title, string subtitle, IList<string> headers,
        IList<IList<string>> rows, bool landscape = false, IEnumerable<Block> extraBlocks = null)
    {
        if (!MainWindow.ConfirmConfidentiality(owner)) return;
        var path = AskPath(owner, $"{title} {DateTime.Today:yyyy-MM-dd}", true, out var format);
        if (path == null) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            if (format == null)
            {
                App.Library.WriteExport(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ToCsv(headers, rows))).ToArray());
                App.Library.Db.Audit("export.csv", "export", null, $"Exported “{title}” as a spreadsheet", DocumentExporter.DescribeLocation(path));
            }
            else
            {
                var doc = new DocModel { Title = title, Subtitle = subtitle, Landscape = landscape, Footer = "Confidential – family proceedings" };
                if (extraBlocks != null) doc.AddRange(extraBlocks);
                doc.Add(Block.TableOf(headers, rows));
                await DocumentExporter.ExportAsync(doc, null, format.Value, path);
            }
            Mouse.OverrideCursor = null;
            Offer(owner, path);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            var id = AppLog.Error("Export", "Export failed", ex);
            MessageBox.Show(owner, $"Couldn't save the file:\n\n{ex.Message}\n\nReference: {id}", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Exports a whole document (letter, report) in the format the person picks.</summary>
    public static async Task<string> ExportDocumentAsync(Window owner, DocModel doc, Letterhead lh, byte[] wordLayout = null,
        IReadOnlyDictionary<string, string> fields = null, int defaultIndex = 1)
    {
        if (!MainWindow.ConfirmConfidentiality(owner)) return null;
        var path = AskPath(owner, doc.Title, false, out var format, defaultIndex);
        if (path == null || format == null) return null;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var result = await DocumentExporter.ExportAsync(doc, lh, format.Value, path, wordLayout, fields);
            Mouse.OverrideCursor = null;
            if (result.MissingFields.Count > 0 || result.Note.Length > 0)
                MessageBox.Show(owner,
                    (result.Note.Length > 0 ? result.Note + "\n\n" : "") +
                    (result.MissingFields.Count > 0
                        ? "These fields had no value, so they were left as they are:\n" + string.Join(", ", result.MissingFields.Distinct().Select(f => "{{" + f + "}}"))
                        : ""),
                    "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            Offer(owner, path);
            return path;
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            var id = AppLog.Error("Export", "Document export failed", ex);
            MessageBox.Show(owner, $"Couldn't save the file:\n\n{ex.Message}\n\nIf it's open in another program, close it and try again.\nReference: {id}",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>"Saved. Open it now?" – opening a decrypted temporary copy if the file was encrypted.</summary>
    public static void Offer(Window owner, string path)
    {
        if (MessageBox.Show(owner, $"Saved:\n{Path.GetFileName(path)}\n\nOpen it now?", "Saved",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            OpenFile(path);
    }

    public static void OpenFile(string path)
    {
        try
        {
            var readable = App.Library.ReadableExportPath(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(readable) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("Couldn't open the file: " + ex.Message, "CaseKeeper"); }
    }

    /// <summary>
    /// RFC 4180 CSV. Cells beginning with = + - @ are prefixed with an apostrophe so a spreadsheet
    /// can't be tricked into running them as formulas.
    /// </summary>
    public static string ToCsv(IList<string> headers, IEnumerable<IList<string>> rows)
    {
        var sb = new StringBuilder();
        static string Cell(string s)
        {
            s ??= "";
            if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        sb.AppendLine(string.Join(",", headers.Select(Cell)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Cell)));
        return sb.ToString();
    }
}
