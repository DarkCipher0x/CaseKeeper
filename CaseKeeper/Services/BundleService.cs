using System.IO;
using CaseKeeper.Data;
using CaseKeeper.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CaseKeeper.Services;

public class BundleOptions
{
    /// <summary>false = paginated court bundle with full documents; true = summary report (details + notes, no full documents).</summary>
    public bool SummaryMode { get; set; }
    public string Title { get; set; } = "Court Bundle";
    public string CaseNumber { get; set; } = "";
    public string Court { get; set; } = "";
    public string Parties { get; set; } = "";
    public string PreparedBy { get; set; } = "";
    public string HearingDate { get; set; } = "";
    public string IntroText { get; set; } = "";
    public bool IncludeCover { get; set; } = true;
    public bool IncludeIndex { get; set; } = true;
    public bool IncludeChronology { get; set; } = true;
    public bool IncludeNotes { get; set; }
    public bool StampPages { get; set; } = true;
    public bool ConfidentialFooter { get; set; } = true;
    public bool SectionDividers { get; set; }
    public List<BundleItem> Items { get; set; } = new();
    public List<TimelineEvent> Events { get; set; } = new();
}

public class BundleResult
{
    public string OutputPath { get; set; }
    public int TotalPages { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Builds PDF bundles and reports. Bundles follow the general shape of Practice Direction 27A
/// (sections A–F, an index, and pages numbered within each section e.g. "B12").
/// </summary>
public class BundleService
{
    private const double A4W = 595, A4H = 842, Margin = 56, BottomLimit = A4H - 72;

    private static readonly XPdfFontOptions FontOpts = new(PdfFontEncoding.Unicode);
    private static XFont F(double size, XFontStyleEx style = XFontStyleEx.Regular) => new("Segoe UI", size, style, FontOpts);

    private readonly XFont _title = F(24, XFontStyleEx.Bold), _h1 = F(16, XFontStyleEx.Bold), _h2 = F(11.5, XFontStyleEx.Bold),
        _body = F(10), _bodyBold = F(10, XFontStyleEx.Bold), _small = F(8.5), _smallItalic = F(8.5, XFontStyleEx.Italic),
        _stamp = F(11, XFontStyleEx.Bold);

    private static readonly XBrush Ink = new XSolidBrush(XColor.FromArgb(30, 41, 59));
    private static readonly XBrush Muted = new XSolidBrush(XColor.FromArgb(100, 116, 139));
    private static readonly XBrush Accent = new XSolidBrush(XColor.FromArgb(30, 64, 120));
    private static readonly XBrush Shade = new XSolidBrush(XColor.FromArgb(232, 238, 247));

    private readonly LibraryService _lib;

    public BundleService(LibraryService lib) => _lib = lib;

    public Task<BundleResult> BuildAsync(BundleOptions o, string outputPath, IProgress<string> progress)
    {
        // PDFsharp-WPF uses WPF font/imaging objects, so run on a dedicated STA thread to keep the UI responsive.
        var tcs = new TaskCompletionSource<BundleResult>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Build(o, outputPath, progress)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    // ------------------------------------------------------------------ sources
    private class Source
    {
        public BundleItem Item;
        public CaseDocument Doc => Item.Document;
        public PdfDocument Pdf;
        public List<(byte[] Png, double W, double H)> Rendered;
        public byte[] ImageBytes;
        public List<FlowPage> TextPages;
        public int PageCount;
        public string Error;
        public int Number;
        public string FirstLabel = "", LastLabel = "";
        public PdfPage FirstPage;
        public string RangeText => PageCount == 0 ? "—" : FirstLabel == LastLabel ? FirstLabel : $"{FirstLabel} – {LastLabel}";
        public Dictionary<int, string> PageLabels = new();
    }

    private Source Prepare(BundleItem item, XGraphics measure, IProgress<string> progress)
    {
        var s = new Source { Item = item };
        var d = item.Document;
        progress?.Report($"Preparing: {d.Title}");
        try
        {
            if (!File.Exists(_lib.GetFilePath(d))) { s.Error = "file missing from library"; return s; }
            // Decrypted in memory; a protected library never writes a plain copy to disk
            var content = _lib.ReadDocumentBytes(d);

            if (d.IsPdf)
            {
                try
                {
                    s.Pdf = PdfReader.Open(new MemoryStream(content), PdfDocumentOpenMode.Import);
                    s.PageCount = s.Pdf.PageCount;
                }
                catch
                {
                    // Fall back to the Windows renderer (handles some protected/unusual PDFs) and embed as images
                    using var r = PdfRenderer.OpenAsync(content).GetAwaiter().GetResult();
                    s.Rendered = new();
                    for (int i = 0; i < r.PageCount; i++)
                    {
                        var (w, h) = r.GetPageSize(i);
                        s.Rendered.Add((r.RenderPngAsync(i, 2.0).GetAwaiter().GetResult(), w * 72 / 96, h * 72 / 96));
                    }
                    s.PageCount = s.Rendered.Count;
                }
            }
            else if (d.IsImage)
            {
                s.ImageBytes = content;
                s.PageCount = 1;
            }
            else if (d.IsText || d.IsWord)
            {
                var text = d.IsWord ? OcrService.ExtractDocx(content) : OcrService.DecodeText(content);
                var flow = NewFlow(measure);
                flow.Para(d.Title, _h2, Accent, anchor: null);
                flow.Space(6);
                flow.Para(text, _body, Ink);
                s.TextPages = flow.Pages;
                s.PageCount = flow.Pages.Count;
            }
            else s.Error = $"{d.FileTypeLabel} files can't be added to a PDF bundle – save it as PDF and re-import";
        }
        catch (Exception ex) { s.Error = "could not be read: " + ex.Message; }
        return s;
    }

    // ------------------------------------------------------------------ build
    private BundleResult Build(BundleOptions o, string outputPath, IProgress<string> progress)
    {
        var result = new BundleResult { OutputPath = outputPath };
        var output = new PdfDocument();
        output.Info.Title = o.Title;
        output.Info.Creator = "CaseKeeper";
        var labels = new List<string>(); // per output page: stamp label or null
        var measure = XGraphics.CreateMeasureContext(new XSize(A4W, A4H), XGraphicsUnit.Point, XPageDirection.Downwards);

        var chronology = o.IncludeChronology ? BuildChronology(o, measure) : null;

        if (o.SummaryMode)
            BuildSummary(o, output, labels, measure, chronology, progress);
        else
            BuildBundle(o, output, labels, measure, chronology, result, progress);

        if (output.PageCount == 0)
        {
            using var g = XGraphics.FromPdfPage(output.AddPage());
            g.DrawString("Nothing was selected to include.", _body, Muted, new XRect(Margin, Margin, 400, 20), XStringFormats.TopLeft);
            labels.Add(null);
        }

        progress?.Report("Adding page numbers…");
        int total = output.PageCount;
        for (int i = 0; i < total; i++)
        {
            var label = o.SummaryMode ? $"Page {i + 1} of {total}" : labels[i];
            StampPage(output.Pages[i], label, o, o.SummaryMode);
        }

        progress?.Report("Saving…");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        output.Save(outputPath);
        result.TotalPages = total;
        return result;
    }

    private void BuildBundle(BundleOptions o, PdfDocument output, List<string> labels, XGraphics measure,
        Flow chronology, BundleResult result, IProgress<string> progress)
    {
        var sectionKeys = DefaultCategories.BundleSections.Keys.ToList();
        var sources = o.Items.Select(i => Prepare(i, measure, progress)).ToList();
        foreach (var s in sources.Where(s => s.Error != null))
            result.Warnings.Add($"“{s.Doc.Title}” not included: {s.Error}");

        var sections = sources.GroupBy(s => s.Item.Section)
            .OrderBy(g => sectionKeys.IndexOf(g.Key) < 0 ? 99 : sectionKeys.IndexOf(g.Key)).ThenBy(g => g.Key)
            .Select(g => (Key: g.Key, Sources: g.ToList())).ToList();
        if (chronology != null && !sections.Any(x => x.Key == "A"))
            sections.Insert(0, ("A", new List<Source>()));

        // Assign section page labels (A1, A2 … B1 …)
        int number = 1;
        string chronoRange = "";
        foreach (var (key, list) in sections)
        {
            int n = 1;
            if (key == "A" && chronology != null)
            {
                chronoRange = chronology.Pages.Count == 1 ? $"A1" : $"A1 – A{chronology.Pages.Count}";
                n += chronology.Pages.Count;
            }
            foreach (var s in list)
            {
                s.Number = number++;
                if (s.PageCount == 0) continue;
                s.FirstLabel = $"{key}{n}";
                for (int p = 1; p <= s.PageCount; p++) s.PageLabels[p] = $"{key}{n + p - 1}";
                n += s.PageCount;
                s.LastLabel = $"{key}{n - 1}";
            }
        }

        Flow notes = o.IncludeNotes ? BuildNotes(sources.Where(s => s.Error == null).ToList(), measure, true) : null;
        if (notes != null && !notes.HasContent) notes = null;

        // ---- cover
        if (o.IncludeCover) { DrawCover(output.AddPage(), o); labels.Add(null); }

        // ---- index
        if (o.IncludeIndex)
        {
            var rows = new List<IndexRow>();
            foreach (var (key, list) in sections)
            {
                rows.Add(new IndexRow { IsSection = true, Text = $"Section {key} – {SectionName(key)}" });
                if (key == "A" && chronology != null)
                    rows.Add(new IndexRow { Text = "Chronology", Pages = chronoRange, Date = DateTime.Today.ToString("dd/MM/yyyy") });
                foreach (var s in list)
                    rows.Add(new IndexRow
                    {
                        Number = s.Number.ToString(), Text = s.Doc.Title + (s.Error != null ? "  (not included)" : ""),
                        Date = s.Doc.DocumentDate?.ToString("dd/MM/yyyy") ?? "", Pages = s.RangeText
                    });
            }
            if (notes != null)
                rows.Add(new IndexRow { IsSection = true, Text = "Annex – Notes of the party preparing this bundle", Pages = notes.Pages.Count == 1 ? "N1" : $"N1 – N{notes.Pages.Count}" });
            DrawIndex(output, rows, o, labels);
        }

        // ---- sections
        var outline = output.Outlines;
        foreach (var (key, list) in sections)
        {
            progress?.Report($"Building section {key}…");
            PdfOutline sectionOutline = null;
            if (o.SectionDividers)
            {
                var div = output.AddPage();
                labels.Add(null);
                DrawDivider(div, key);
                sectionOutline = outline.Add($"Section {key} – {SectionName(key)}", div, true);
            }

            if (key == "A" && chronology != null)
            {
                for (int i = 0; i < chronology.Pages.Count; i++)
                {
                    var page = output.AddPage();
                    DrawFlowPage(page, chronology.Pages[i]);
                    labels.Add($"A{i + 1}");
                    if (i == 0)
                    {
                        sectionOutline ??= outline.Add($"Section A – {SectionName("A")}", page, true);
                        sectionOutline.Outlines.Add("Chronology", page);
                    }
                }
            }

            foreach (var s in list.Where(s => s.Error == null && s.PageCount > 0))
            {
                progress?.Report($"Adding: {s.Doc.Title}");
                AddSourcePages(output, s, labels, result);
                if (s.FirstPage != null)
                {
                    sectionOutline ??= outline.Add($"Section {key} – {SectionName(key)}", s.FirstPage, true);
                    sectionOutline.Outlines.Add($"{s.RangeText}  {s.Doc.Title}", s.FirstPage);
                }
            }
        }

        // ---- notes annex
        if (notes != null)
        {
            PdfOutline notesOutline = null;
            for (int i = 0; i < notes.Pages.Count; i++)
            {
                var page = output.AddPage();
                DrawFlowPage(page, notes.Pages[i]);
                labels.Add($"N{i + 1}");
                notesOutline ??= outline.Add("Annex – Notes", page);
            }
        }
    }

    private void AddSourcePages(PdfDocument output, Source s, List<string> labels, BundleResult result)
    {
        for (int p = 0; p < s.PageCount; p++)
        {
            PdfPage page;
            try
            {
                if (s.Pdf != null) page = output.AddPage(s.Pdf.Pages[p]);
                else if (s.Rendered != null)
                {
                    var (png, w, h) = s.Rendered[p];
                    page = output.AddPage();
                    page.Width = XUnit.FromPoint(w);
                    page.Height = XUnit.FromPoint(h);
                    using var g = XGraphics.FromPdfPage(page);
                    using var img = XImage.FromStream(new MemoryStream(png));
                    g.DrawImage(img, 0, 0, w, h);
                }
                else if (s.ImageBytes != null)
                {
                    page = output.AddPage();
                    using var img = XImage.FromStream(new MemoryStream(s.ImageBytes));
                    bool landscape = img.PixelWidth > img.PixelHeight;
                    double pw = landscape ? A4H : A4W, ph = landscape ? A4W : A4H;
                    page.Width = XUnit.FromPoint(pw);
                    page.Height = XUnit.FromPoint(ph);
                    double boxW = pw - 60, boxH = ph - 100;
                    double scale = Math.Min(boxW / img.PixelWidth, boxH / img.PixelHeight);
                    double w = img.PixelWidth * scale, h = img.PixelHeight * scale;
                    using var g = XGraphics.FromPdfPage(page);
                    g.DrawImage(img, (pw - w) / 2, 30 + (boxH - h) / 2, w, h);
                }
                else
                {
                    page = output.AddPage();
                    DrawFlowPage(page, s.TextPages[p]);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"“{s.Doc.Title}” page {p + 1}: {ex.Message}");
                page = output.AddPage();
                using var g = XGraphics.FromPdfPage(page);
                g.DrawString($"[Page {p + 1} of “{s.Doc.Title}” could not be included]", _body, Muted,
                    new XRect(Margin, Margin, A4W - 2 * Margin, 40), XStringFormats.TopLeft);
            }
            s.FirstPage ??= page;
            labels.Add(s.PageLabels.TryGetValue(p + 1, out var l) ? l : null);
        }
    }

    private void BuildSummary(BundleOptions o, PdfDocument output, List<string> labels, XGraphics measure,
        Flow chronology, IProgress<string> progress)
    {
        if (o.IncludeCover) { DrawCover(output.AddPage(), o); labels.Add(null); }

        var flow = NewFlow(measure);
        if (!string.IsNullOrWhiteSpace(o.IntroText))
        {
            flow.Para("Summary", _h1, Accent, anchor: "Summary");
            flow.Space(6);
            flow.Para(o.IntroText, _body, Ink);
            flow.Space(14);
        }
        if (chronology != null)
        {
            if (flow.HasContent) flow.NewPage();
            flow.Append(chronology);
        }

        progress?.Report("Writing document schedule…");
        var sources = o.Items.Select(i => new Source { Item = i, PageCount = i.Document.PageCount }).ToList();
        int n = 1;
        foreach (var s in sources) s.Number = n++;
        var schedule = BuildNotes(sources, measure, false);
        if (schedule.HasContent)
        {
            if (flow.HasContent) flow.NewPage();
            flow.Append(schedule);
        }

        var outline = output.Outlines;
        foreach (var fp in flow.Pages.Where(p => p.Lines.Count > 0))
        {
            var page = output.AddPage();
            DrawFlowPage(page, fp);
            labels.Add(null);
            foreach (var a in fp.Anchors) outline.Add(a, page);
        }
    }

    // ------------------------------------------------------------------ content flows
    private Flow NewFlow(XGraphics measure) => new(measure, Margin, Margin, A4W - 2 * Margin, BottomLimit);

    private Flow BuildChronology(BundleOptions o, XGraphics measure)
    {
        var events = o.Events.Where(e => e.IncludeInChronology).OrderBy(e => e.EventDate).ThenBy(e => e.Time).ToList();
        var flow = NewFlow(measure);
        flow.Para("Chronology", _h1, Accent, anchor: o.SummaryMode ? "Chronology" : null);
        if (!string.IsNullOrWhiteSpace(o.CaseNumber)) flow.Para($"Case number: {o.CaseNumber}", _small, Muted);
        flow.Space(10);
        if (events.Count == 0) flow.Para("No events have been recorded.", _body, Muted);
        foreach (var e in events)
        {
            var date = e.EventDate.ToString("dd/MM/yyyy") + (string.IsNullOrWhiteSpace(e.Time) ? "" : "\n" + e.Time);
            flow.Para($"{e.Title}  [{e.EventType}]", _bodyBold, Ink, indent: 88, spaceBefore: 8, label: date, labelFont: _body, keepSpace: 14);
            if (!string.IsNullOrWhiteSpace(e.Description)) flow.Para(e.Description, _body, Ink, indent: 88);
            if (!string.IsNullOrWhiteSpace(e.LinkedDocumentTitle)) flow.Para("See: " + e.LinkedDocumentTitle, _smallItalic, Muted, indent: 88);
        }
        return flow;
    }

    /// <summary>Document details + your document notes + per-page notes. Used for the bundle annex and the summary report.</summary>
    private Flow BuildNotes(List<Source> sources, XGraphics measure, bool annex)
    {
        var flow = NewFlow(measure);
        bool any = false;
        foreach (var s in sources)
        {
            var d = s.Doc;
            var pageNotes = _lib.Db.GetPageNotes(d.Id).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).OrderBy(kv => kv.Key).ToList();
            if (annex && string.IsNullOrWhiteSpace(d.Notes) && pageNotes.Count == 0) continue;

            if (!any)
            {
                flow.Para(annex ? "Annex – Notes" : "Document Schedule", _h1, Accent, anchor: annex ? null : "Document schedule");
                flow.Para(annex
                    ? "Notes made by the party preparing this bundle, cross-referenced to bundle pages. These are comments, not evidence."
                    : "Each document with its details and the notes recorded against it.", _smallItalic, Muted);
                flow.Space(8);
                any = true;
            }

            var heading = annex ? $"{d.Title}  ({s.RangeText})" : $"{s.Number}. {d.Title}";
            flow.Para(heading, _h2, Ink, spaceBefore: 12, keepSpace: 30, anchor: annex ? null : $"{s.Number}. {d.Title}");
            var meta = new List<string> { s.Item.Document.CategoryName };
            if (d.DocumentDate.HasValue) meta.Add(d.DocumentDate.Value.ToString("dd MMM yyyy"));
            if (!string.IsNullOrWhiteSpace(d.PartyName)) meta.Add("Party: " + d.PartyName);
            if (!string.IsNullOrWhiteSpace(d.Author)) meta.Add("From: " + d.Author);
            if (d.PageCount > 0) meta.Add(d.PageCount == 1 ? "1 page" : $"{d.PageCount} pages");
            flow.Para(string.Join("  ·  ", meta.Where(m => !string.IsNullOrWhiteSpace(m))), _small, Muted);
            if (!annex && !string.IsNullOrWhiteSpace(d.Description)) flow.Para(d.Description, _body, Ink, spaceBefore: 4);
            if (!string.IsNullOrWhiteSpace(d.Notes))
            {
                flow.Para("Notes", _bodyBold, Accent, spaceBefore: 6, keepSpace: 14);
                flow.Para(d.Notes, _body, Ink);
            }
            if (pageNotes.Count > 0)
            {
                flow.Para("Page notes", _bodyBold, Accent, spaceBefore: 6, keepSpace: 14);
                foreach (var (page, note) in pageNotes)
                {
                    var label = annex && s.PageLabels.TryGetValue(page, out var l) ? $"p.{page} ({l})" : $"Page {page}";
                    flow.Para(note, _body, Ink, indent: 70, spaceBefore: 3, label: label, labelFont: _bodyBold);
                }
            }
        }
        return flow;
    }

    // ------------------------------------------------------------------ drawing
    private string SectionName(string key) =>
        DefaultCategories.BundleSections.TryGetValue(key, out var n) ? n : "Other";

    private void DrawCover(PdfPage page, BundleOptions o)
    {
        using var g = XGraphics.FromPdfPage(page);
        g.DrawRectangle(Accent, 0, 0, A4W, 10);
        double y = 190;
        if (!string.IsNullOrWhiteSpace(o.Court))
        {
            g.DrawString(o.Court.ToUpperInvariant(), _h2, Muted, new XRect(Margin, y, A4W - 2 * Margin, 20), XStringFormats.TopCenter);
            y += 34;
        }
        if (!string.IsNullOrWhiteSpace(o.CaseNumber))
        {
            g.DrawString($"Case No: {o.CaseNumber}", _h2, Ink, new XRect(Margin, y, A4W - 2 * Margin, 20), XStringFormats.TopCenter);
            y += 40;
        }
        if (!string.IsNullOrWhiteSpace(o.Parties))
        {
            foreach (var line in o.Parties.Replace("\r", "").Split('\n'))
            {
                g.DrawString(line, _body, Ink, new XRect(Margin, y, A4W - 2 * Margin, 16), XStringFormats.TopCenter);
                y += 16;
            }
            y += 30;
        }
        g.DrawLine(new XPen(XColor.FromArgb(30, 64, 120), 1.2), 150, y, A4W - 150, y);
        y += 24;
        var titleFlow = new Flow(g, Margin, y, A4W - 2 * Margin, A4H);
        foreach (var line in titleFlow.Wrap(o.Title, _title, A4W - 2 * Margin))
        {
            g.DrawString(line, _title, Accent, new XRect(Margin, y, A4W - 2 * Margin, 34), XStringFormats.TopCenter);
            y += 34;
        }
        y += 12;
        g.DrawLine(new XPen(XColor.FromArgb(30, 64, 120), 1.2), 150, y, A4W - 150, y);
        y += 30;
        if (!string.IsNullOrWhiteSpace(o.HearingDate))
        {
            g.DrawString($"For hearing: {o.HearingDate}", _body, Ink, new XRect(Margin, y, A4W - 2 * Margin, 16), XStringFormats.TopCenter);
            y += 20;
        }
        if (!string.IsNullOrWhiteSpace(o.PreparedBy))
        {
            g.DrawString($"Prepared by: {o.PreparedBy}", _body, Ink, new XRect(Margin, y, A4W - 2 * Margin, 16), XStringFormats.TopCenter);
            y += 20;
        }
        g.DrawString($"Date prepared: {DateTime.Today:dd MMMM yyyy}", _body, Muted, new XRect(Margin, y, A4W - 2 * Margin, 16), XStringFormats.TopCenter);
    }

    private class IndexRow
    {
        public bool IsSection;
        public string Number = "", Text = "", Date = "", Pages = "";
    }

    private void DrawIndex(PdfDocument output, List<IndexRow> rows, BundleOptions o, List<string> labels)
    {
        const double rowH = 19, colNo = Margin, colDoc = Margin + 30, colDate = A4W - Margin - 150, colPages = A4W - Margin - 72;
        double docWidth = colDate - colDoc - 8;
        int i = 0;
        bool first = true;
        while (i < rows.Count || first)
        {
            var page = output.AddPage();
            labels.Add(null);
            using var g = XGraphics.FromPdfPage(page);
            double y = Margin;
            if (first)
            {
                g.DrawString("INDEX", _h1, Accent, new XRect(Margin, y, 300, 24), XStringFormats.TopLeft);
                y += 26;
                var sub = o.Title + (string.IsNullOrWhiteSpace(o.CaseNumber) ? "" : $"   ·   Case No: {o.CaseNumber}");
                g.DrawString(sub, _small, Muted, new XRect(Margin, y, A4W - 2 * Margin, 14), XStringFormats.TopLeft);
                y += 28;
                output.Outlines.Add("Index", page);
            }
            // header row
            g.DrawLine(new XPen(XColor.FromArgb(30, 64, 120), 1), Margin, y + rowH, A4W - Margin, y + rowH);
            g.DrawString("No.", _bodyBold, Ink, new XRect(colNo, y + 3, 30, rowH), XStringFormats.TopLeft);
            g.DrawString("Document", _bodyBold, Ink, new XRect(colDoc, y + 3, docWidth, rowH), XStringFormats.TopLeft);
            g.DrawString("Date", _bodyBold, Ink, new XRect(colDate, y + 3, 70, rowH), XStringFormats.TopLeft);
            g.DrawString("Pages", _bodyBold, Ink, new XRect(colPages, y + 3, 72, rowH), XStringFormats.TopLeft);
            y += rowH + 4;
            first = false;

            while (i < rows.Count && y + rowH <= BottomLimit)
            {
                var r = rows[i++];
                if (r.IsSection)
                {
                    y += 3;
                    g.DrawRectangle(Shade, Margin, y, A4W - 2 * Margin, rowH);
                    g.DrawString(Fit(g, r.Text, _bodyBold, colPages - colNo - 10), _bodyBold, Accent, new XRect(colNo + 4, y + 3, colPages - colNo, rowH), XStringFormats.TopLeft);
                    if (!string.IsNullOrEmpty(r.Pages))
                        g.DrawString(r.Pages, _bodyBold, Accent, new XRect(colPages, y + 3, 72, rowH), XStringFormats.TopLeft);
                }
                else
                {
                    g.DrawString(r.Number, _body, Muted, new XRect(colNo, y + 3, 30, rowH), XStringFormats.TopLeft);
                    g.DrawString(Fit(g, r.Text, _body, docWidth), _body, Ink, new XRect(colDoc, y + 3, docWidth, rowH), XStringFormats.TopLeft);
                    g.DrawString(r.Date, _body, Ink, new XRect(colDate, y + 3, 70, rowH), XStringFormats.TopLeft);
                    g.DrawString(r.Pages, _body, Ink, new XRect(colPages, y + 3, 72, rowH), XStringFormats.TopLeft);
                }
                y += rowH;
            }
        }
    }

    private static string Fit(XGraphics g, string text, XFont font, double width)
    {
        if (g.MeasureString(text, font).Width <= width) return text;
        while (text.Length > 1 && g.MeasureString(text + "…", font).Width > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    private void DrawDivider(PdfPage page, string key)
    {
        using var g = XGraphics.FromPdfPage(page);
        g.DrawRectangle(Shade, 0, A4H / 2 - 70, A4W, 140);
        g.DrawString($"SECTION {key}", _title, Accent, new XRect(0, A4H / 2 - 40, A4W, 34), XStringFormats.TopCenter);
        g.DrawString(SectionName(key), _h2, Ink, new XRect(0, A4H / 2 + 6, A4W, 20), XStringFormats.TopCenter);
    }

    private static void DrawFlowPage(PdfPage page, FlowPage fp)
    {
        using var g = XGraphics.FromPdfPage(page);
        foreach (var l in fp.Lines)
            g.DrawString(l.Text, l.Font, l.Brush, new XRect(l.X, l.Y, 2000, l.Font.Size * 1.6), XStringFormats.TopLeft);
    }

    private void StampPage(PdfPage page, string label, BundleOptions o, bool centred)
    {
        if (!o.StampPages) label = null;
        if (label == null && !o.ConfidentialFooter) return;
        using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        double w = g.PageSize.Width, h = g.PageSize.Height;
        if (label != null)
        {
            if (centred)
                g.DrawString(label, _small, Muted, new XRect(0, h - 34, w, 14), XStringFormats.TopCenter);
            else
            {
                var size = g.MeasureString(label, _stamp);
                double bw = size.Width + 14, bh = 20, x = w - bw - 24, y = h - bh - 18;
                g.DrawRoundedRectangle(new XPen(XColor.FromArgb(30, 64, 120), 0.8), XBrushes.White, x, y, bw, bh, 6, 6);
                g.DrawString(label, _stamp, Accent, new XRect(x, y + 2, bw, bh), XStringFormats.TopCenter);
            }
        }
        if (o.ConfidentialFooter)
            g.DrawString("CONFIDENTIAL – Family proceedings. Not to be disclosed without the permission of the court.",
                _small, Muted, new XRect(24, h - 16, w - 48, 12), centred ? XStringFormats.TopCenter : XStringFormats.TopLeft);
    }
}

// ---------------------------------------------------------------------- simple text flow engine
public class FlowLine
{
    public string Text;
    public XFont Font;
    public XBrush Brush;
    public double X, Y;
}

public class FlowPage
{
    public List<FlowLine> Lines { get; } = new();
    public List<string> Anchors { get; } = new();
}

/// <summary>Lays out wrapped paragraphs onto A4 pages (measure first, draw later), so page counts are known in advance.</summary>
public class Flow
{
    private readonly XGraphics _m;
    private readonly double _left, _top, _width, _bottom;
    private FlowPage _page;
    private double _y;

    public List<FlowPage> Pages { get; } = new();
    public bool HasContent => Pages.Any(p => p.Lines.Count > 0);

    public Flow(XGraphics measure, double left, double top, double width, double bottom)
    {
        _m = measure; _left = left; _top = top; _width = width; _bottom = bottom;
        NewPage();
    }

    public void NewPage()
    {
        if (_page != null && _page.Lines.Count == 0) return;
        _page = new FlowPage();
        Pages.Add(_page);
        _y = _top;
    }

    public void Space(double h) => _y += h;

    /// <summary>Appends another flow's pages after this one's.</summary>
    public void Append(Flow other)
    {
        Pages.RemoveAll(p => p.Lines.Count == 0);
        Pages.AddRange(other.Pages.Where(p => p.Lines.Count > 0));
        _page = new FlowPage();
        Pages.Add(_page);
        _y = _top;
    }

    public void Para(string text, XFont font, XBrush brush, double indent = 0, double spaceBefore = 0,
        string label = null, XFont labelFont = null, double keepSpace = 0, string anchor = null)
    {
        double lh = font.Size * 1.4;
        var lines = Wrap(text ?? "", font, _width - indent);
        if (_y > _top) _y += spaceBefore;
        double needed = Math.Min(lines.Count, 2) * lh + keepSpace;
        if (_y + needed > _bottom && _y > _top) NewPage();

        var labelLines = label?.Split('\n') ?? Array.Empty<string>();
        int count = Math.Max(lines.Count, labelLines.Length);
        for (int i = 0; i < count; i++)
        {
            if (_y + lh > _bottom) NewPage();
            if (i == 0 && anchor != null) _page.Anchors.Add(anchor);
            if (i < lines.Count && lines[i].Length > 0)
                _page.Lines.Add(new FlowLine { Text = lines[i], Font = font, Brush = brush, X = _left + indent, Y = _y });
            if (i < labelLines.Length)
                _page.Lines.Add(new FlowLine { Text = labelLines[i], Font = labelFont ?? font, Brush = brush, X = _left, Y = _y });
            _y += lh;
        }
    }

    public List<string> Wrap(string text, XFont font, double width)
    {
        var result = new List<string>();
        foreach (var rawPara in text.Replace("\r", "").Replace("\t", "    ").Split('\n'))
        {
            var words = rawPara.Split(' ');
            var line = "";
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (_m.MeasureString(candidate, font).Width <= width) { line = candidate; continue; }
                if (line.Length > 0) result.Add(line);
                line = word;
                // hard-break very long words (e.g. URLs)
                while (line.Length > 1 && _m.MeasureString(line, font).Width > width)
                {
                    int cut = line.Length - 1;
                    while (cut > 1 && _m.MeasureString(line[..cut], font).Width > width) cut--;
                    result.Add(line[..cut]);
                    line = line[cut..];
                }
            }
            result.Add(line);
        }
        return result;
    }
}
