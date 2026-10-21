using System.IO;
using CaseKeeper.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CaseKeeper.Services.Documents;

/// <summary>
/// Writes a <see cref="DocModel"/> as an A4 PDF, with an optional letterhead on the first page and
/// a footer with page numbers on every page. Uses PDFsharp with the fonts installed in Windows.
/// Must run on an STA thread (see <see cref="DocumentExporter"/>).
/// </summary>
public sealed class PdfDocWriter
{
    private const double MarginX = 62, MarginTop = 60, MarginBottom = 64;
    private double _pageW, _pageH;

    private readonly DocModel _doc;
    private readonly Letterhead _lh;
    private readonly string _family;
    private readonly XColor _accent;
    private static readonly XPdfFontOptions FontOpts = new(PdfFontEncoding.Unicode);
    private static readonly XColor Ink = XColor.FromArgb(17, 41, 43);
    private static readonly XColor Muted = XColor.FromArgb(88, 110, 112);
    private static readonly XColor Line = XColor.FromArgb(205, 218, 218);

    private PdfDocument _pdf;
    private PdfPage _page;
    private XGraphics _g;
    private double _y;

    public PdfDocWriter(DocModel doc, Letterhead letterhead)
    {
        _doc = doc;
        _lh = letterhead;
        _family = string.IsNullOrWhiteSpace(letterhead?.FontFamily) ? "Segoe UI" : letterhead.FontFamily;
        _accent = ParseColour(letterhead?.AccentColor, XColor.FromArgb(11, 122, 117));
    }

    public static XColor ParseColour(string hex, XColor fallback)
    {
        try
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 6)
                return XColor.FromArgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16));
        }
        catch { /* fall through */ }
        return fallback;
    }

    private XFont Font(double size, bool bold = false, bool italic = false, bool underline = false)
    {
        var style = XFontStyleEx.Regular;
        if (bold) style |= XFontStyleEx.Bold;
        if (italic) style |= XFontStyleEx.Italic;
        if (underline) style |= XFontStyleEx.Underline;
        try { return new XFont(_family, size, style, FontOpts); }
        catch { return new XFont("Segoe UI", size, style, FontOpts); }
    }

    private double ContentWidth => _pageW - 2 * MarginX;
    private double Bottom => _pageH - MarginBottom;

    public byte[] Render()
    {
        _pdf = new PdfDocument();
        _pdf.Info.Title = _doc.Title ?? "";
        _pdf.Info.Creator = "CaseKeeper";
        _pdf.Info.Author = "";       // never the Windows account name
        _pdf.Info.Subject = "";
        _pdf.Info.Keywords = "";

        NewPage(first: true);
        if (_doc.ShowTitle && !string.IsNullOrWhiteSpace(_doc.Title))
        {
            DrawRuns(new List<Inline> { new(_doc.Title, bold: true) }, 20, null, BlockAlign.Left, 0, colour: _accent);
            if (!string.IsNullOrWhiteSpace(_doc.Subtitle))
                DrawRuns(new List<Inline> { new(_doc.Subtitle) }, 10.5, null, BlockAlign.Left, 0, colour: Muted);
            _y += 4;
            _g.DrawLine(new XPen(_accent, 1.2), MarginX, _y, MarginX + ContentWidth, _y);
            _y += 14;
        }

        foreach (var b in _doc.Blocks) DrawBlock(b);

        _g.Dispose();
        DrawFooters();

        using var ms = new MemoryStream();
        _pdf.Save(ms, false);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ pages
    private void NewPage(bool first = false)
    {
        _g?.Dispose();
        _page = _pdf.AddPage();
        _page.Size = PdfSharp.PageSize.A4;
        if (_doc.Landscape) _page.Orientation = PdfSharp.PageOrientation.Landscape;
        _pageW = _page.Width.Point;
        _pageH = _page.Height.Point;
        _g = XGraphics.FromPdfPage(_page);
        _y = MarginTop;
        if (first && _lh != null) DrawLetterhead();
    }

    private void EnsureSpace(double height)
    {
        if (_y + height > Bottom) NewPage();
    }

    private void DrawLetterhead()
    {
        var name = _lh.SenderName ?? "";
        var contact = new List<string>();
        contact.AddRange((_lh.AddressLines ?? "").Replace("\r", "").Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)));
        var line2 = string.Join("   ·   ", new[] { _lh.Phone, _lh.Email, _lh.Website }.Where(s => !string.IsNullOrWhiteSpace(s)));
        XImage logo = null;
        if (_lh.LogoPng is { Length: > 0 })
        {
            try { logo = XImage.FromStream(new MemoryStream(_lh.LogoPng)); }
            catch { logo = null; }
        }

        switch (_lh.Layout)
        {
            case "Band":
            {
                double bandH = 74;
                _g.DrawRectangle(new XSolidBrush(_accent), 0, 0, _pageW, bandH);
                double textX = MarginX;
                if (logo != null)
                {
                    double lh = 46, lw = lh * logo.PixelWidth / Math.Max(1, logo.PixelHeight);
                    double lx = _lh.LogoAlign == "Left" ? MarginX : _pageW - MarginX - lw;
                    _g.DrawImage(logo, lx, (bandH - lh) / 2, lw, lh);
                    if (_lh.LogoAlign == "Left") textX = MarginX + lw + 14;
                }
                _g.DrawString(name, Font(18, bold: true), XBrushes.White, new XRect(textX, 18, ContentWidth, 24), XStringFormats.TopLeft);
                if (!string.IsNullOrWhiteSpace(_lh.ReferenceLine))
                    _g.DrawString(_lh.ReferenceLine, Font(9), XBrushes.White, new XRect(textX, 44, ContentWidth, 14), XStringFormats.TopLeft);
                _y = bandH + 10;
                var info = string.Join("   ·   ", contact.Concat(new[] { line2 }).Where(s => !string.IsNullOrWhiteSpace(s)));
                if (info.Length > 0)
                {
                    DrawRuns(new List<Inline> { new(info) }, 8.5, null, BlockAlign.Left, 0, colour: Muted);
                }
                _y += 12;
                break;
            }
            case "Minimal":
            {
                DrawRuns(new List<Inline> { new(name.ToUpperInvariant(), bold: true) }, 11, null, BlockAlign.Left, 0, colour: _accent);
                var info = string.Join("  ·  ", contact.Concat(new[] { line2 }).Where(s => !string.IsNullOrWhiteSpace(s)));
                if (info.Length > 0) DrawRuns(new List<Inline> { new(info) }, 8.5, null, BlockAlign.Left, 0, colour: Muted);
                _y += 4;
                _g.DrawLine(new XPen(Line, 0.6), MarginX, _y, MarginX + ContentWidth, _y);
                _y += 18;
                break;
            }
            default: // Classic
            {
                double top = _y, logoBottom = top;
                bool logoRight = _lh.LogoAlign != "Left";
                bool centred = _lh.LogoAlign == "Centre";
                if (logo != null)
                {
                    double lh = 58, lw = Math.Min(170, lh * logo.PixelWidth / Math.Max(1, logo.PixelHeight));
                    lh = lw * logo.PixelHeight / Math.Max(1, logo.PixelWidth);
                    double lx = centred ? (_pageW - lw) / 2 : logoRight ? _pageW - MarginX - lw : MarginX;
                    _g.DrawImage(logo, lx, top, lw, lh);
                    logoBottom = top + lh;
                    if (centred) _y = logoBottom + 8;
                }
                var align = centred ? BlockAlign.Centre : logoRight || logo == null ? BlockAlign.Left : BlockAlign.Right;
                DrawRuns(new List<Inline> { new(name, bold: true) }, 16, null, align, 0, colour: _accent);
                foreach (var l in contact) DrawRuns(new List<Inline> { new(l) }, 9, null, align, 0, colour: Muted, spaceAfter: 0);
                if (line2.Length > 0) DrawRuns(new List<Inline> { new(line2) }, 9, null, align, 0, colour: Muted, spaceAfter: 0);
                if (!string.IsNullOrWhiteSpace(_lh.ReferenceLine))
                    DrawRuns(new List<Inline> { new(_lh.ReferenceLine, italic: true) }, 8.5, null, align, 0, colour: Muted, spaceAfter: 0);
                _y = Math.Max(_y, logoBottom) + 8;
                _g.DrawLine(new XPen(_accent, 1.4), MarginX, _y, MarginX + ContentWidth, _y);
                _y += 20;
                break;
            }
        }
    }

    private void DrawFooters()
    {
        int total = _pdf.PageCount;
        var footerText = string.Join("   ·   ", new[] { _lh?.FooterText, _doc.Footer }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        for (int i = 0; i < total; i++)
        {
            using var g = XGraphics.FromPdfPage(_pdf.Pages[i], XGraphicsPdfPageOptions.Append);
            double w = _pdf.Pages[i].Width.Point, h = _pdf.Pages[i].Height.Point;
            double y = h - 40;
            g.DrawLine(new XPen(Line, 0.5), MarginX, y - 6, w - MarginX, y - 6);
            var small = Font(8);
            if (footerText.Length > 0)
                g.DrawString(Clip(g, footerText, small, w - 2 * MarginX - 90), small, new XSolidBrush(Muted),
                    new XRect(MarginX, y, w - 2 * MarginX - 90, 12), XStringFormats.TopLeft);
            g.DrawString($"Page {i + 1} of {total}", small, new XSolidBrush(Muted), new XRect(MarginX, y, w - 2 * MarginX, 12), XStringFormats.TopRight);
        }
    }

    private static string Clip(XGraphics g, string s, XFont f, double width)
    {
        if (g.MeasureString(s, f).Width <= width) return s;
        while (s.Length > 4 && g.MeasureString(s + "…", f).Width > width) s = s[..^1];
        return s + "…";
    }

    // ------------------------------------------------------------------ blocks
    private void DrawBlock(Block b)
    {
        switch (b.Kind)
        {
            case BlockKind.Heading1:
                _y += 6;
                EnsureSpace(60);
                DrawRuns(b.Inlines, 15, null, b.Align, 0, forceBold: true, colour: _accent, spaceAfter: 6);
                break;
            case BlockKind.Heading2:
                _y += 4;
                EnsureSpace(50);
                DrawRuns(b.Inlines, 12.5, null, b.Align, 0, forceBold: true, colour: Ink, spaceAfter: 4);
                break;
            case BlockKind.Heading3:
                EnsureSpace(44);
                DrawRuns(b.Inlines, 11, null, b.Align, 0, forceBold: true, colour: Ink, spaceAfter: 3);
                break;
            case BlockKind.Paragraph:
                DrawRuns(b.Inlines, 10.5, null, b.Align, 0);
                break;
            case BlockKind.Bullet:
                DrawRuns(b.Inlines, 10.5, null, BlockAlign.Left, 18 + b.Level * 18, marker: b.Level == 0 ? "•" : "–", spaceAfter: 3);
                break;
            case BlockKind.Numbered:
                DrawRuns(b.Inlines, 10.5, null, BlockAlign.Left, 24 + b.Level * 18, marker: $"{b.Number}.", spaceAfter: 5);
                break;
            case BlockKind.Quote:
            {
                double start = _y;
                var startPage = _page;
                DrawRuns(b.Inlines, 10.5, null, BlockAlign.Left, 16, forceItalic: true, colour: Muted);
                if (_page == startPage)
                    _g.DrawLine(new XPen(_accent, 2), MarginX + 4, start, MarginX + 4, _y - 6);
                break;
            }
            case BlockKind.Rule:
                EnsureSpace(16);
                _g.DrawLine(new XPen(Line, 0.8), MarginX, _y + 4, MarginX + ContentWidth, _y + 4);
                _y += 14;
                break;
            case BlockKind.PageBreak:
                NewPage();
                break;
            case BlockKind.Spacer:
                _y += 10;
                break;
            case BlockKind.Table:
                DrawTable(b.Table);
                break;
        }
    }

    private sealed class Word
    {
        public string Text;
        public XFont Font;
        public double Width;
        public bool LineBreak;
    }

    /// <summary>Lays out styled runs with word wrapping, page breaks and alignment.</summary>
    private void DrawRuns(List<Inline> runs, double size, XBrush brushOverride, BlockAlign align, double indent,
        bool forceBold = false, bool forceItalic = false, XColor? colour = null, string marker = null, double spaceAfter = 7)
    {
        var brush = brushOverride ?? new XSolidBrush(colour ?? Ink);
        double left = MarginX + indent, width = ContentWidth - indent;
        double lineH = size * 1.42;

        // Split into words, keeping each word's style
        var words = new List<Word>();
        foreach (var run in runs)
        {
            var font = Font(size, run.Bold || forceBold, run.Italic || forceItalic, run.Underline);
            var parts = (run.Text ?? "").Replace("\r", "").Split('\n');
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0) words.Add(new Word { LineBreak = true });
                foreach (var token in SplitKeepingSpaces(parts[p]))
                    words.Add(new Word { Text = token, Font = font, Width = _g.MeasureString(token, font).Width });
            }
        }

        // Break into lines
        var lines = new List<List<Word>>();
        var current = new List<Word>();
        double lineWidth = 0;
        foreach (var w in words)
        {
            if (w.LineBreak) { lines.Add(current); current = new List<Word>(); lineWidth = 0; continue; }
            bool isSpace = string.IsNullOrWhiteSpace(w.Text);
            if (lineWidth + w.Width > width && current.Count > 0 && !isSpace)
            {
                lines.Add(current);
                current = new List<Word>();
                lineWidth = 0;
            }
            if (isSpace && current.Count == 0) continue;
            if (w.Width > width)
            {
                // A single unbreakable token wider than the line: break it by characters
                foreach (var piece in BreakLong(w, width)) { lines.Add(new List<Word> { piece }); }
                continue;
            }
            current.Add(w);
            lineWidth += w.Width;
        }
        if (current.Count > 0 || lines.Count == 0) lines.Add(current);

        bool firstLine = true;
        foreach (var line in lines)
        {
            while (line.Count > 0 && string.IsNullOrWhiteSpace(line[^1].Text)) line.RemoveAt(line.Count - 1);
            if (_y + lineH > Bottom) NewPage();
            double total = line.Sum(w => w.Width);
            double x = align switch
            {
                BlockAlign.Right => left + width - total,
                BlockAlign.Centre => left + (width - total) / 2,
                _ => left
            };
            if (firstLine && marker != null)
                _g.DrawString(marker, Font(size, forceBold), brush, new XRect(left - (indent > 20 ? 22 : 14), _y, 20, lineH), XStringFormats.TopLeft);
            foreach (var w in line)
            {
                _g.DrawString(w.Text, w.Font, brush, new XRect(x, _y, w.Width + 2, lineH), XStringFormats.TopLeft);
                x += w.Width;
            }
            _y += lineH;
            firstLine = false;
        }
        _y += spaceAfter;
    }

    private IEnumerable<Word> BreakLong(Word w, double width)
    {
        var s = w.Text;
        while (s.Length > 0)
        {
            int n = s.Length;
            while (n > 1 && _g.MeasureString(s[..n], w.Font).Width > width) n--;
            var piece = s[..n];
            yield return new Word { Text = piece, Font = w.Font, Width = _g.MeasureString(piece, w.Font).Width };
            s = s[n..];
        }
    }

    private static IEnumerable<string> SplitKeepingSpaces(string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != ' ') continue;
            if (i > start) yield return s[start..i];
            yield return " ";
            start = i + 1;
        }
        if (start < s.Length) yield return s[start..];
    }

    // ------------------------------------------------------------------ tables
    private void DrawTable(TableData t)
    {
        var widths = t.ResolveWidths();
        if (widths.Length == 0) return;
        var colW = widths.Select(w => w * ContentWidth).ToArray();
        var font = Font(9);
        var headFont = Font(9, bold: true);
        const double pad = 4;

        List<string> Wrap(string text, XFont f, double w)
        {
            var result = new List<string>();
            foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var line = "";
                foreach (var word in para.Split(' '))
                {
                    var trial = line.Length == 0 ? word : line + " " + word;
                    if (_g.MeasureString(trial, f).Width <= w - 2 * pad || line.Length == 0)
                    {
                        if (_g.MeasureString(trial, f).Width > w - 2 * pad && line.Length == 0)
                        {
                            // very long word
                            var s = trial;
                            while (s.Length > 0)
                            {
                                int n = s.Length;
                                while (n > 1 && _g.MeasureString(s[..n], f).Width > w - 2 * pad) n--;
                                result.Add(s[..n]);
                                s = s[n..];
                            }
                            line = "";
                        }
                        else line = trial;
                    }
                    else
                    {
                        result.Add(line);
                        line = word;
                    }
                }
                result.Add(line);
            }
            return result;
        }

        double lineH = 9 * 1.35;

        void DrawRow(List<string> cells, bool header)
        {
            var f = header ? headFont : font;
            var wrapped = colW.Select((w, i) => Wrap(i < cells.Count ? cells[i] : "", f, w)).ToList();
            double h = wrapped.Max(l => l.Count) * lineH + 2 * pad;
            if (_y + h > Bottom)
            {
                NewPage();
                if (!header && t.Headers.Count > 0) DrawRow(t.Headers, true);
            }
            double x = MarginX;
            if (header) _g.DrawRectangle(new XSolidBrush(XColor.FromArgb(234, 243, 242)), x, _y, ContentWidth, h);
            for (int i = 0; i < colW.Length; i++)
            {
                double ty = _y + pad;
                foreach (var l in wrapped[i])
                {
                    _g.DrawString(l, f, new XSolidBrush(header ? _accent : Ink), new XRect(x + pad, ty, colW[i] - 2 * pad, lineH), XStringFormats.TopLeft);
                    ty += lineH;
                }
                _g.DrawRectangle(new XPen(Line, 0.5), x, _y, colW[i], h);
                x += colW[i];
            }
            _y += h;
        }

        if (t.Headers.Count > 0) DrawRow(t.Headers, true);
        foreach (var r in t.Rows) DrawRow(r, false);
        _y += 10;
    }
}
