using System.IO;
using System.Text.RegularExpressions;
using CaseKeeper.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace CaseKeeper.Services.Documents;

/// <summary>
/// Writes Word (.docx) files: either a fresh document built from a <see cref="DocModel"/> with an
/// optional letterhead, or an imported Word template with its {{fields}} filled in and the body
/// dropped in at {{body}}. Every file is stripped of author, company and editing history before
/// it is saved.
/// </summary>
public static class DocxDocWriter
{
    private const int A4W = 11906, A4H = 16838, Margin = 1247; // twips (2.2 cm margins)

    // ================================================================== new document
    public static byte[] Render(DocModel doc, Letterhead lh)
    {
        using var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var font = string.IsNullOrWhiteSpace(lh?.FontFamily) ? "Calibri" : lh.FontFamily;
            AddStyles(main, font);
            var accent = Hex(lh?.AccentColor) ?? "0B7A75";

            var body = new Body();
            if (doc.ShowTitle && !string.IsNullOrWhiteSpace(doc.Title))
            {
                body.Append(Para(new[] { new Inline(doc.Title, bold: true) }, 36, accent, JustificationValues.Left, spacingAfter: 60));
                if (!string.IsNullOrWhiteSpace(doc.Subtitle))
                    body.Append(Para(new[] { new Inline(doc.Subtitle) }, 20, "586E70", JustificationValues.Left, spacingAfter: 240));
            }
            foreach (var el in BlocksToElements(doc.Blocks, accent)) body.Append(el);

            // Letterhead on the first page only, as on printed stationery; the footer on every page.
            // Word requires header/footer references first, then page size and margins, then titlePg.
            var sect = new SectionProperties();
            if (lh != null)
            {
                var first = main.AddNewPart<HeaderPart>();
                first.Header = BuildLetterheadHeader(first, lh, accent);
                var other = main.AddNewPart<HeaderPart>();
                other.Header = new Header(new Paragraph());
                sect.Append(new HeaderReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(first) });
                sect.Append(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(other) });
            }
            var footerText = string.Join("   ·   ", new[] { lh?.FooterText, doc.Footer }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            var footerPart = main.AddNewPart<FooterPart>();
            footerPart.Footer = BuildFooter(footerText);
            var fid = main.GetIdOfPart(footerPart);
            if (lh != null) sect.Append(new FooterReference { Type = HeaderFooterValues.First, Id = fid });
            sect.Append(new FooterReference { Type = HeaderFooterValues.Default, Id = fid });
            sect.Append(new PageSize
            {
                Width = (UInt32Value)(uint)(doc.Landscape ? A4H : A4W), Height = (UInt32Value)(uint)(doc.Landscape ? A4W : A4H),
                Orient = doc.Landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait
            });
            sect.Append(new PageMargin
            {
                Top = Margin, Bottom = Margin, Left = (UInt32Value)(uint)Margin, Right = (UInt32Value)(uint)Margin,
                Header = 600, Footer = 500, Gutter = 0
            });
            if (lh != null) sect.Append(new TitlePage());

            body.Append(sect);
            main.Document = new Document(body);
            SetCoreProperties(word, doc.Title);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static void AddStyles(MainDocumentPart main, string font)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font, EastAsia = font },
                    new FontSize { Val = "21" }, new FontSizeComplexScript { Val = "21" },
                    new Languages { Val = "en-GB" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "140", Line = "276", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            HeadingStyle("Heading1", "heading 1", 30, 0),
            HeadingStyle("Heading2", "heading 2", 25, 1),
            HeadingStyle("Heading3", "heading 3", 22, 2));
        styles.Styles.Save();
    }

    private static Style HeadingStyle(string id, string name, int halfPoints, int level) =>
        new(new StyleName { Val = name }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "100" },
                new OutlineLevel { Val = level }),
            new StyleRunProperties(new Bold(), new FontSize { Val = halfPoints.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = id };

    private static string Hex(string colour)
    {
        var h = (colour ?? "").Trim().TrimStart('#');
        return Regex.IsMatch(h, "^[0-9A-Fa-f]{6}$") ? h.ToUpperInvariant() : null;
    }

    // ------------------------------------------------------------------ blocks → Word
    /// <summary>
    /// Turns blocks into Word paragraphs and tables using direct formatting only, so the same
    /// output drops cleanly into any imported template whatever styles it happens to define.
    /// </summary>
    public static IEnumerable<OpenXmlElement> BlocksToElements(IEnumerable<Block> blocks, string accent = "0B7A75")
    {
        foreach (var b in blocks)
        {
            var just = b.Align switch
            {
                BlockAlign.Right => JustificationValues.Right,
                BlockAlign.Centre => JustificationValues.Center,
                BlockAlign.Justify => JustificationValues.Both,
                _ => JustificationValues.Left
            };
            switch (b.Kind)
            {
                case BlockKind.Heading1:
                    yield return Para(b.Inlines, 30, accent, just, bold: true, keepNext: true, spacingBefore: 240, styleId: "Heading1");
                    break;
                case BlockKind.Heading2:
                    yield return Para(b.Inlines, 25, null, just, bold: true, keepNext: true, spacingBefore: 200, styleId: "Heading2");
                    break;
                case BlockKind.Heading3:
                    yield return Para(b.Inlines, 22, null, just, bold: true, keepNext: true, spacingBefore: 160, styleId: "Heading3");
                    break;
                case BlockKind.Paragraph:
                    yield return Para(b.Inlines, null, null, just);
                    break;
                case BlockKind.Bullet:
                    yield return Para(new[] { new Inline(b.Level == 0 ? "•\t" : "–\t") }.Concat(b.Inlines), null, null, JustificationValues.Left,
                        indentLeft: 360 + b.Level * 360, hanging: 360, spacingAfter: 60);
                    break;
                case BlockKind.Numbered:
                    yield return Para(new[] { new Inline($"{b.Number}.\t") }.Concat(b.Inlines), null, null, JustificationValues.Left,
                        indentLeft: 567 + b.Level * 360, hanging: 567, spacingAfter: 120);
                    break;
                case BlockKind.Quote:
                    yield return Para(b.Inlines.Select(i => new Inline(i.Text, i.Bold, true, i.Underline)), null, "586E70", JustificationValues.Left,
                        indentLeft: 567, borderLeft: accent);
                    break;
                case BlockKind.Rule:
                    yield return new Paragraph(new ParagraphProperties(new ParagraphBorders(
                        new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "CDDADA", Space = 1 })));
                    break;
                case BlockKind.PageBreak:
                    yield return new Paragraph(new Run(new Break { Type = BreakValues.Page }));
                    break;
                case BlockKind.Spacer:
                    yield return new Paragraph();
                    break;
                case BlockKind.Table:
                    yield return BuildTable(b.Table, accent);
                    yield return new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "120" }));
                    break;
            }
        }
    }

    private static Paragraph Para(IEnumerable<Inline> inlines, int? halfPoints, string colour, JustificationValues just,
        bool bold = false, bool keepNext = false, int spacingBefore = 0, int spacingAfter = -1, int indentLeft = 0, int hanging = 0,
        string borderLeft = null, string styleId = null)
    {
        var pp = new ParagraphProperties();
        if (styleId != null) pp.Append(new ParagraphStyleId { Val = styleId });
        if (keepNext) pp.Append(new KeepNext());
        if (borderLeft != null)
            pp.Append(new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 18, Color = borderLeft, Space = 8 }));
        if (spacingBefore > 0 || spacingAfter >= 0)
        {
            var sp = new SpacingBetweenLines();
            if (spacingBefore > 0) sp.Before = spacingBefore.ToString();
            if (spacingAfter >= 0) sp.After = spacingAfter.ToString();
            pp.Append(sp);
        }
        if (indentLeft > 0)
        {
            var ind = new Indentation { Left = indentLeft.ToString() };
            if (hanging > 0) ind.Hanging = hanging.ToString();
            pp.Append(ind);
        }
        pp.Append(new Justification { Val = just });

        var p = new Paragraph(pp);
        foreach (var inline in inlines)
        {
            var parts = (inline.Text ?? "").Replace("\r", "").Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                // Word insists on this element order: b, i, color, sz, u
                var rp = new RunProperties();
                if (inline.Bold || bold) rp.Append(new Bold());
                if (inline.Italic) rp.Append(new Italic());
                if (colour != null) rp.Append(new Color { Val = colour });
                if (halfPoints.HasValue) rp.Append(new FontSize { Val = halfPoints.Value.ToString() });
                if (inline.Underline) rp.Append(new Underline { Val = UnderlineValues.Single });
                var run = new Run(rp);
                if (i > 0) run.Append(new Break());
                AppendText(run, parts[i]);
                p.Append(run);
            }
        }
        return p;
    }

    /// <summary>Adds text, turning tab characters into real Word tabs.</summary>
    private static void AppendText(Run run, string text)
    {
        var bits = text.Split('\t');
        for (int i = 0; i < bits.Length; i++)
        {
            if (i > 0) run.Append(new TabChar());
            if (bits[i].Length > 0) run.Append(new Text(bits[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
    }

    private static Table BuildTable(TableData t, string accent)
    {
        var widths = t.ResolveWidths();
        int total = A4W - 2 * Margin;
        var table = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            // Word requires the order top, left, bottom, right, insideH, insideV
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "CDDADA" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "CDDADA" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "CDDADA" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "CDDADA" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "CDDADA" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "CDDADA" }),
            new TableCellMarginDefault(new TopMargin { Width = "50", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "50", Type = TableWidthUnitValues.Dxa })));
        var grid = new TableGrid();
        foreach (var w in widths) grid.Append(new GridColumn { Width = ((int)(w * total)).ToString() });
        table.Append(grid);

        TableRow Row(IReadOnlyList<string> cells, bool header)
        {
            var tr = new TableRow();
            if (header) tr.Append(new TableRowProperties(new TableHeader()));
            for (int i = 0; i < widths.Length; i++)
            {
                var tcp = new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = ((int)(widths[i] * total)).ToString() });
                if (header) tcp.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "EAF3F2", Color = "auto" });
                var text = i < cells.Count ? cells[i] ?? "" : "";
                tr.Append(new TableCell(tcp, Para(new[] { new Inline(text, bold: header) }, 18, header ? accent : null, JustificationValues.Left, spacingAfter: 0)));
            }
            return tr;
        }

        if (t.Headers.Count > 0) table.Append(Row(t.Headers, true));
        foreach (var r in t.Rows) table.Append(Row(r, false));
        return table;
    }

    // ------------------------------------------------------------------ letterhead & footer
    private static Header BuildLetterheadHeader(HeaderPart part, Letterhead lh, string accent)
    {
        var header = new Header();
        var lines = (lh.AddressLines ?? "").Replace("\r", "").Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var contact = string.Join("   ·   ", new[] { lh.Phone, lh.Email, lh.Website }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var align = lh.LogoAlign switch
        {
            "Centre" => JustificationValues.Center,
            "Left" => JustificationValues.Right,
            _ => JustificationValues.Left
        };

        Drawing logo = null;
        if (lh.LogoPng is { Length: > 0 })
        {
            try { logo = AddImage(part, lh.LogoPng, 1600000); }
            catch { logo = null; }
        }

        if (lh.Layout == "Band")
        {
            var band = new Paragraph(new ParagraphProperties(
                    new Shading { Val = ShadingPatternValues.Clear, Fill = accent, Color = "auto" },
                    new SpacingBetweenLines { After = "60" }),
                new Run(new RunProperties(new Bold(), new Color { Val = "FFFFFF" }, new FontSize { Val = "36" }),
                    new Text(" " + (lh.SenderName ?? "")) { Space = SpaceProcessingModeValues.Preserve }));
            if (logo != null) band.InsertAfter(new Run(new Text("   ") { Space = SpaceProcessingModeValues.Preserve }, logo), band.ParagraphProperties);
            header.Append(band);
            var info = string.Join("   ·   ", lines.Append(contact).Where(s => !string.IsNullOrWhiteSpace(s)));
            if (info.Length > 0) header.Append(Para(new[] { new Inline(info) }, 17, "586E70", JustificationValues.Left));
        }
        else if (lh.Layout == "Minimal")
        {
            header.Append(Para(new[] { new Inline((lh.SenderName ?? "").ToUpperInvariant(), bold: true) }, 22, accent, JustificationValues.Left, spacingAfter: 0));
            var info = string.Join("  ·  ", lines.Append(contact).Where(s => !string.IsNullOrWhiteSpace(s)));
            if (info.Length > 0) header.Append(Para(new[] { new Inline(info) }, 17, "586E70", JustificationValues.Left));
        }
        else
        {
            if (logo != null)
                header.Append(new Paragraph(new ParagraphProperties(new Justification
                    { Val = lh.LogoAlign == "Left" ? JustificationValues.Left : lh.LogoAlign == "Centre" ? JustificationValues.Center : JustificationValues.Right }),
                    new Run(logo)));
            header.Append(Para(new[] { new Inline(lh.SenderName ?? "", bold: true) }, 32, accent, align, spacingAfter: 0));
            foreach (var l in lines) header.Append(Para(new[] { new Inline(l) }, 18, "586E70", align, spacingAfter: 0));
            if (contact.Length > 0) header.Append(Para(new[] { new Inline(contact) }, 18, "586E70", align, spacingAfter: 0));
            if (!string.IsNullOrWhiteSpace(lh.ReferenceLine))
                header.Append(Para(new[] { new Inline(lh.ReferenceLine, italic: true) }, 17, "586E70", align, spacingAfter: 0));
        }
        header.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(
            new BottomBorder { Val = BorderValues.Single, Size = 8, Color = accent, Space = 1 }))));
        return header;
    }

    private static Footer BuildFooter(string text)
    {
        var p = new Paragraph(new ParagraphProperties(
            new ParagraphBorders(new TopBorder { Val = BorderValues.Single, Size = 4, Color = "CDDADA", Space = 4 }),
            new Tabs(new TabStop { Val = TabStopValues.Right, Position = A4W - 2 * Margin })));
        RunProperties Small() => new(new Color { Val = "586E70" }, new FontSize { Val = "16" });
        if (!string.IsNullOrWhiteSpace(text)) p.Append(new Run(Small(), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        p.Append(new Run(Small(), new TabChar(), new Text("Page ") { Space = SpaceProcessingModeValues.Preserve }));
        p.Append(new SimpleField(new Run(Small(), new Text("1"))) { Instruction = " PAGE " });
        p.Append(new Run(Small(), new Text(" of ") { Space = SpaceProcessingModeValues.Preserve }));
        p.Append(new SimpleField(new Run(Small(), new Text("1"))) { Instruction = " NUMPAGES " });
        return new Footer(p);
    }

    /// <summary>Embeds a PNG in a header and returns the inline drawing for it, at most <paramref name="maxWidthEmu"/> wide.</summary>
    private static Drawing AddImage(OpenXmlPart part, byte[] png, long maxWidthEmu)
    {
        int pw = 200, ph = 80;
        try
        {
            var bmp = System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(png));
            pw = bmp.PixelWidth;
            ph = bmp.PixelHeight;
        }
        catch { /* use defaults */ }
        long heightEmu = 640000;   // about 1.8 cm tall
        long widthEmu = heightEmu * pw / Math.Max(1, ph);
        if (widthEmu > maxWidthEmu) { widthEmu = maxWidthEmu; heightEmu = widthEmu * ph / Math.Max(1, pw); }

        string relId;
        ImagePart img = part switch
        {
            HeaderPart h => h.AddImagePart(ImagePartType.Png),
            MainDocumentPart m => m.AddImagePart(ImagePartType.Png),
            _ => throw new NotSupportedException()
        };
        using (var s = new MemoryStream(png)) img.FeedData(s);
        relId = part.GetIdOfPart(img);
        uint id = (uint)Random.Shared.Next(1000, 100000);

        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = widthEmu, Cy = heightEmu },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = "Logo" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0, Name = "logo.png" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
    }

    // ================================================================== filling a Word template
    private static readonly Regex FieldPattern = new(@"\{\{\s*([a-zA-Z0-9_.\-]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Fills an imported Word template. Every {{field}} in the body, headers and footers is replaced
    /// (even where Word has split it across several runs), a paragraph containing only {{body}} is
    /// replaced with <paramref name="body"/>, and if there is no {{body}} the content is added at the end.
    /// Formatting from the template is kept. A .dotx template comes out as an ordinary .docx.
    /// </summary>
    public static byte[] FillTemplate(byte[] template, IReadOnlyDictionary<string, string> fields, IList<Block> body,
        ICollection<string> missing = null, string title = null)
    {
        WordSafety.Check(template);
        using var ms = new MemoryStream();
        ms.Write(template, 0, template.Length);
        ms.Position = 0;
        using (var word = WordprocessingDocument.Open(ms, true))
        {
            if (word.DocumentType == WordprocessingDocumentType.Template)
                word.ChangeDocumentType(WordprocessingDocumentType.Document);

            var main = word.MainDocumentPart ?? throw new InvalidDataException("This Word file has no document body.");
            var roots = new List<OpenXmlPartRootElement> { main.Document };
            roots.AddRange(main.HeaderParts.Select(h => (OpenXmlPartRootElement)h.Header));
            roots.AddRange(main.FooterParts.Select(f => (OpenXmlPartRootElement)f.Footer));

            bool bodyPlaced = false;
            foreach (var root in roots.Where(r => r != null))
            {
                foreach (var p in root.Descendants<Paragraph>().ToList())
                {
                    var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
                    if (!text.Contains("{{")) continue;
                    if (body != null && root == main.Document && Regex.IsMatch(text.Trim(), @"^\{\{\s*body\s*\}\}$", RegexOptions.IgnoreCase))
                    {
                        foreach (var el in BlocksToElements(body)) p.InsertBeforeSelf(el);
                        p.Remove();
                        bodyPlaced = true;
                        continue;
                    }
                    ReplaceFieldsInParagraph(p, fields, missing);
                }
                root.Save();
            }

            if (body != null && !bodyPlaced)
            {
                var b = main.Document.Body!;
                var sect = b.Elements<SectionProperties>().LastOrDefault();
                foreach (var el in BlocksToElements(body))
                {
                    if (sect != null) sect.InsertBeforeSelf(el);
                    else b.Append(el);
                }
                main.Document.Save();
            }

            Scrub(word, title);
        }
        return ms.ToArray();
    }

    private static void ReplaceFieldsInParagraph(Paragraph p, IReadOnlyDictionary<string, string> fields, ICollection<string> missing)
    {
        var texts = p.Descendants<Text>().ToList();
        var full = string.Concat(texts.Select(t => t.Text));
        var matches = FieldPattern.Matches(full).Cast<Match>().Reverse().ToList();
        if (matches.Count == 0) return;

        // Map each character of the joined string back to the Text element and offset it came from
        var owner = new List<(Text T, int Offset)>();
        foreach (var t in texts)
            for (int i = 0; i < t.Text.Length; i++) owner.Add((t, i));

        foreach (var m in matches)
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            if (!fields.TryGetValue(key, out var value) || value == null)
            {
                missing?.Add(key);
                continue;
            }
            var (startT, startOff) = owner[m.Index];
            var (endT, endOff) = owner[m.Index + m.Length - 1];
            if (startT == endT)
            {
                startT.Text = startT.Text[..startOff] + value + startT.Text[(endOff + 1)..];
            }
            else
            {
                bool inside = false;
                foreach (var t in texts)
                {
                    if (t == startT) { inside = true; t.Text = t.Text[..startOff] + value; continue; }
                    if (!inside) continue;
                    if (t == endT) { t.Text = t.Text[(endOff + 1)..]; break; }
                    t.Text = "";
                }
            }
            startT.Space = SpaceProcessingModeValues.Preserve;
            // Rebuild the map, since lengths have changed
            owner.Clear();
            foreach (var t in texts)
                for (int i = 0; i < t.Text.Length; i++) owner.Add((t, i));
        }

        // Multi-line values (addresses) become proper line breaks
        foreach (var t in texts.Where(t => t.Text.Contains('\n')).ToList())
        {
            var parts = t.Text.Replace("\r", "").Split('\n');
            t.Text = parts[0];
            OpenXmlElement after = t;
            for (int i = 1; i < parts.Length; i++)
            {
                var br = new Break();
                after.InsertAfterSelf(br);
                var nt = new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve };
                br.InsertAfterSelf(nt);
                after = nt;
            }
        }
    }

    /// <summary>Lists the {{fields}} a Word template uses, for the template editor.</summary>
    public static List<string> FieldsInTemplate(byte[] template)
    {
        WordSafety.Check(template);
        using var ms = new MemoryStream(template, false);
        using var word = WordprocessingDocument.Open(ms, false);
        var main = word.MainDocumentPart;
        if (main == null) return new List<string>();
        var texts = new List<string> { main.Document.InnerText };
        texts.AddRange(main.HeaderParts.Select(h => h.Header?.InnerText ?? ""));
        texts.AddRange(main.FooterParts.Select(f => f.Footer?.InnerText ?? ""));
        return texts.SelectMany(t => FieldPattern.Matches(t).Select(m => m.Groups[1].Value.ToLowerInvariant())).Distinct().ToList();
    }

    /// <summary>
    /// Converts a Word document into the editable template language: headings, lists, tables and
    /// paragraphs, with {{fields}} kept as they are.
    /// </summary>
    public static string ToMarkup(byte[] docx)
    {
        WordSafety.Check(docx);
        using var ms = new MemoryStream(docx, false);
        using var word = WordprocessingDocument.Open(ms, false);
        var body = word.MainDocumentPart?.Document?.Body;
        if (body == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var el in body.ChildElements)
        {
            if (el is Paragraph p)
            {
                var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                var text = ParagraphMarkup(p);
                if (string.IsNullOrWhiteSpace(text)) { sb.AppendLine(); continue; }
                var heading = Regex.Match(style, @"Heading(\d)", RegexOptions.IgnoreCase);
                if (heading.Success) sb.Append(new string('#', Math.Min(3, int.Parse(heading.Groups[1].Value)))).Append(' ');
                else if (p.ParagraphProperties?.NumberingProperties != null || style.Contains("List", StringComparison.OrdinalIgnoreCase)) sb.Append("- ");
                else if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) sb.Append("# ");
                sb.AppendLine(text).AppendLine();
            }
            else if (el is Table t)
            {
                bool first = true;
                foreach (var row in t.Elements<TableRow>())
                {
                    var cells = row.Elements<TableCell>().Select(c => string.Join(" ", c.Elements<Paragraph>().Select(ParagraphMarkup)).Replace("|", "\\|"));
                    sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                    if (first) { sb.AppendLine("|" + string.Concat(row.Elements<TableCell>().Select(_ => "---|"))); first = false; }
                }
                sb.AppendLine();
            }
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    private static string ParagraphMarkup(Paragraph p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in p.Descendants<Run>())
        {
            var text = string.Concat(run.ChildElements.Select(c => c switch
            {
                Text t => t.Text,
                TabChar => "\t",
                Break => "\n",
                _ => ""
            }));
            if (text.Length == 0) continue;
            bool bold = run.RunProperties?.Bold != null && run.RunProperties.Bold.Val?.Value != false;
            bool italic = run.RunProperties?.Italic != null && run.RunProperties.Italic.Val?.Value != false;
            var escaped = Markup.Escape(text).Replace(@"\{", "{");
            if (bold && text.Trim().Length > 0) escaped = $"**{escaped}**";
            if (italic && text.Trim().Length > 0) escaped = $"*{escaped}*";
            sb.Append(escaped);
        }
        // Formatting that happened to fall inside a {{field}} in Word mustn't break the field
        return Regex.Replace(sb.ToString().Replace("****", ""), @"\{\{(.*?)\}\}", m => "{{" + m.Groups[1].Value.Replace("*", "").Replace("\\", "") + "}}");
    }

    // ================================================================== privacy
    private static void SetCoreProperties(WordprocessingDocument word, string title)
    {
        var props = word.PackageProperties;
        props.Creator = "";
        props.LastModifiedBy = "";
        props.Title = title ?? "";
        props.Created = DateTime.UtcNow;
        props.Modified = DateTime.UtcNow;
    }

    /// <summary>
    /// Removes anything that could identify the people who wrote or edited a template: author and
    /// "last modified by", company and manager, template file path, custom properties, comments
    /// and the list of reviewers.
    /// </summary>
    public static void Scrub(WordprocessingDocument word, string title = null)
    {
        var props = word.PackageProperties;
        props.Creator = "";
        props.LastModifiedBy = "";
        props.Category = "";
        props.Keywords = "";
        props.Description = "";
        props.Subject = "";
        props.ContentStatus = "";
        props.Revision = "1";
        if (title != null) props.Title = title;
        props.Created = DateTime.UtcNow;
        props.Modified = DateTime.UtcNow;
        props.LastPrinted = null;

        if (word.ExtendedFilePropertiesPart?.Properties is { } ext)
        {
            ext.Company?.Remove();
            ext.Manager?.Remove();
            ext.Template?.Remove();
            ext.HyperlinkBase?.Remove();
            ext.Save();
        }
        if (word.CustomFilePropertiesPart != null) word.DeletePart(word.CustomFilePropertiesPart);

        var main = word.MainDocumentPart;
        if (main == null) return;
        if (main.WordprocessingCommentsPart != null)
        {
            foreach (var el in main.Document.Descendants<CommentRangeStart>().ToList()) el.Remove();
            foreach (var el in main.Document.Descendants<CommentRangeEnd>().ToList()) el.Remove();
            foreach (var el in main.Document.Descendants<CommentReference>().ToList()) el.Remove();
            main.DeletePart(main.WordprocessingCommentsPart);
        }
        if (main.WordprocessingCommentsExPart != null) main.DeletePart(main.WordprocessingCommentsExPart);
        if (main.WordprocessingPeoplePart != null) main.DeletePart(main.WordprocessingPeoplePart);
        // Accept tracked changes' authorship by stripping the author attribute from revision marks
        foreach (var ins in main.Document.Descendants<InsertedRun>()) ins.Author = "";
        foreach (var del in main.Document.Descendants<DeletedRun>()) del.Author = "";
        main.Document.Save();
    }
}

/// <summary>Basic checks before an untrusted Word file is opened: size, and protection against zip bombs.</summary>
public static class WordSafety
{
    public const long MaxBytes = 25 * 1024 * 1024;
    private const long MaxExpanded = 300L * 1024 * 1024;

    public static void Check(byte[] data)
    {
        if (data == null || data.Length == 0) throw new InvalidDataException("The Word file is empty.");
        if (data.Length > MaxBytes) throw new InvalidDataException("That Word file is too large to use as a template (25 MB limit).");
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(data, false), System.IO.Compression.ZipArchiveMode.Read);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                total += e.Length;
                if (total > MaxExpanded || (e.CompressedLength > 0 && e.Length / Math.Max(1, e.CompressedLength) > 200))
                    throw new InvalidDataException("That Word file looks malformed and was not opened.");
            }
            if (zip.GetEntry("word/document.xml") == null) throw new InvalidDataException("That isn't a Word document (.docx / .dotx).");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException("That file couldn't be read as a Word document: " + ex.Message); }
    }
}
