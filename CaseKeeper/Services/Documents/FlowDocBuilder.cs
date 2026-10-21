using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CaseKeeper.Models;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace CaseKeeper.Services.Documents;

/// <summary>
/// Shows a <see cref="DocModel"/> on screen as an A4-shaped page, letterhead included, and prints it.
/// Always black-on-white, like the exported file, whichever theme the app is using.
/// </summary>
public static class FlowDocBuilder
{
    public static FlowDocument Build(DocModel doc, Letterhead lh)
    {
        var accent = ParseBrush(lh?.AccentColor, Color.FromRgb(11, 122, 117));
        var ink = new SolidColorBrush(Color.FromRgb(17, 41, 43));
        var muted = new SolidColorBrush(Color.FromRgb(88, 110, 112));
        var fd = new FlowDocument
        {
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(lh?.FontFamily) ? "Segoe UI" : lh.FontFamily),
            FontSize = 14,
            Foreground = ink,
            Background = Brushes.White,
            PagePadding = new Thickness(64, 56, 64, 56),
            PageWidth = doc.Landscape ? 1123 : 794,
            ColumnWidth = 2000,
            TextAlignment = TextAlignment.Left
        };

        if (lh != null) fd.Blocks.Add(Letterhead(lh, accent, muted));
        if (doc.ShowTitle && !string.IsNullOrWhiteSpace(doc.Title))
        {
            fd.Blocks.Add(new Paragraph(new Run(doc.Title)) { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(0, 0, 0, 2) });
            if (!string.IsNullOrWhiteSpace(doc.Subtitle))
                fd.Blocks.Add(new Paragraph(new Run(doc.Subtitle)) { Foreground = muted, Margin = new Thickness(0, 0, 0, 12) });
        }

        List list = null;
        foreach (var b in doc.Blocks)
        {
            if (b.Kind != BlockKind.Bullet) list = null;
            var align = b.Align switch
            {
                BlockAlign.Right => TextAlignment.Right,
                BlockAlign.Centre => TextAlignment.Center,
                BlockAlign.Justify => TextAlignment.Justify,
                _ => TextAlignment.Left
            };
            switch (b.Kind)
            {
                case BlockKind.Heading1: fd.Blocks.Add(P(b, 20, FontWeights.Bold, accent, align, new Thickness(0, 14, 0, 6))); break;
                case BlockKind.Heading2: fd.Blocks.Add(P(b, 16.5, FontWeights.Bold, ink, align, new Thickness(0, 12, 0, 4))); break;
                case BlockKind.Heading3: fd.Blocks.Add(P(b, 14.5, FontWeights.Bold, ink, align, new Thickness(0, 10, 0, 3))); break;
                case BlockKind.Paragraph: fd.Blocks.Add(P(b, 14, FontWeights.Normal, ink, align, new Thickness(0, 0, 0, 9))); break;
                case BlockKind.Quote:
                {
                    var p = P(b, 14, FontWeights.Normal, muted, align, new Thickness(0, 0, 0, 10));
                    p.FontStyle = FontStyles.Italic;
                    p.BorderBrush = accent;
                    p.BorderThickness = new Thickness(3, 0, 0, 0);
                    p.Padding = new Thickness(12, 2, 0, 2);
                    fd.Blocks.Add(p);
                    break;
                }
                case BlockKind.Bullet:
                    list ??= new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(22, 0, 0, 0) };
                    if (!fd.Blocks.Contains(list)) fd.Blocks.Add(list);
                    var li = new ListItem(P(b, 14, FontWeights.Normal, ink, TextAlignment.Left, new Thickness(0, 0, 0, 3)));
                    if (b.Level > 0) li.Margin = new Thickness(20, 0, 0, 0);
                    list.ListItems.Add(li);
                    break;
                case BlockKind.Numbered:
                {
                    var p = new Paragraph { Margin = new Thickness(34, 0, 0, 8), TextIndent = -34, TextAlignment = TextAlignment.Left };
                    p.Inlines.Add(new Run($"{b.Number}.\t") { FontWeight = FontWeights.SemiBold });
                    foreach (var i in Inlines(b)) p.Inlines.Add(i);
                    fd.Blocks.Add(p);
                    break;
                }
                case BlockKind.Rule:
                    fd.Blocks.Add(new Paragraph { BorderBrush = new SolidColorBrush(Color.FromRgb(205, 218, 218)), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 12), FontSize = 2 });
                    break;
                case BlockKind.PageBreak:
                    fd.Blocks.Add(new Paragraph(new Run("— page break —")) { Foreground = muted, FontSize = 11, TextAlignment = TextAlignment.Center, BreakPageBefore = false });
                    break;
                case BlockKind.Spacer:
                    fd.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 10) });
                    break;
                case BlockKind.Table:
                    fd.Blocks.Add(Table(b.Table, accent, ink));
                    break;
            }
        }

        var footer = string.Join("  ·  ", new[] { lh?.FooterText, doc.Footer }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        if (footer.Length > 0)
            fd.Blocks.Add(new Paragraph(new Run(footer))
            {
                Foreground = muted, FontSize = 11, Margin = new Thickness(0, 22, 0, 0), BorderBrush = new SolidColorBrush(Color.FromRgb(205, 218, 218)),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 5, 0, 0)
            });
        return fd;
    }

    private static WpfBlock Letterhead(Letterhead lh, Brush accent, Brush muted)
    {
        var section = new Section { Margin = new Thickness(0, 0, 0, 18) };
        var lines = (lh.AddressLines ?? "").Replace("\r", "").Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var contact = string.Join("   ·   ", new[] { lh.Phone, lh.Email, lh.Website }.Where(s => !string.IsNullOrWhiteSpace(s)));
        BitmapImage logo = null;
        if (lh.LogoPng is { Length: > 0 })
        {
            try
            {
                logo = new BitmapImage();
                logo.BeginInit();
                logo.CacheOption = BitmapCacheOption.OnLoad;
                logo.StreamSource = new MemoryStream(lh.LogoPng);
                logo.EndInit();
                logo.Freeze();
            }
            catch { logo = null; }
        }
        var align = lh.LogoAlign == "Centre" ? TextAlignment.Center : TextAlignment.Left;

        if (lh.Layout == "Band")
        {
            var band = new Paragraph { Background = accent, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 6) };
            band.Inlines.Add(new Run(lh.SenderName ?? "") { Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold });
            if (logo != null)
            {
                band.Inlines.Add(new Run("    "));
                band.Inlines.Add(new InlineUIContainer(new System.Windows.Controls.Image { Source = logo, Height = 40 }) { BaselineAlignment = BaselineAlignment.Center });
            }
            section.Blocks.Add(band);
            var info = string.Join("   ·   ", lines.Append(contact).Where(s => !string.IsNullOrWhiteSpace(s)));
            if (info.Length > 0) section.Blocks.Add(new Paragraph(new Run(info)) { Foreground = muted, FontSize = 11.5, Margin = new Thickness(0) });
            return section;
        }

        if (logo != null && lh.Layout != "Minimal")
            section.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Image
            {
                Source = logo, Height = 58, Stretch = Stretch.Uniform,
                HorizontalAlignment = lh.LogoAlign switch { "Left" => HorizontalAlignment.Left, "Centre" => HorizontalAlignment.Center, _ => HorizontalAlignment.Right }
            }));

        var name = new Paragraph(new Run(lh.Layout == "Minimal" ? (lh.SenderName ?? "").ToUpperInvariant() : lh.SenderName ?? ""))
        {
            FontSize = lh.Layout == "Minimal" ? 14 : 21, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(0), TextAlignment = align
        };
        section.Blocks.Add(name);
        var infoText = lh.Layout == "Minimal"
            ? string.Join("  ·  ", lines.Append(contact).Where(s => !string.IsNullOrWhiteSpace(s)))
            : string.Join("\n", lines.Append(contact).Where(s => !string.IsNullOrWhiteSpace(s)));
        if (infoText.Length > 0)
            section.Blocks.Add(new Paragraph(new Run(infoText)) { Foreground = muted, FontSize = 12, Margin = new Thickness(0), TextAlignment = align });
        if (!string.IsNullOrWhiteSpace(lh.ReferenceLine))
            section.Blocks.Add(new Paragraph(new Run(lh.ReferenceLine)) { Foreground = muted, FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(0), TextAlignment = align });
        section.BorderBrush = accent;
        section.BorderThickness = new Thickness(0, 0, 0, 1.5);
        section.Padding = new Thickness(0, 0, 0, 8);
        return section;
    }

    private static Paragraph P(Block b, double size, FontWeight weight, Brush brush, TextAlignment align, Thickness margin)
    {
        var p = new Paragraph { FontSize = size, FontWeight = weight, Foreground = brush, TextAlignment = align, Margin = margin, KeepWithNext = b.KeepWithNext };
        foreach (var i in Inlines(b)) p.Inlines.Add(i);
        return p;
    }

    private static IEnumerable<WpfInline> Inlines(Block b)
    {
        foreach (var i in b.Inlines)
        {
            var parts = (i.Text ?? "").Replace("\r", "").Split('\n');
            for (int n = 0; n < parts.Length; n++)
            {
                if (n > 0) yield return new LineBreak();
                var run = new Run(parts[n]);
                if (i.Bold) run.FontWeight = FontWeights.Bold;
                if (i.Italic) run.FontStyle = FontStyles.Italic;
                if (i.Underline) run.TextDecorations = TextDecorations.Underline;
                yield return run;
            }
        }
    }

    private static WpfTable Table(TableData t, Brush accent, Brush ink)
    {
        var line = new SolidColorBrush(Color.FromRgb(205, 218, 218));
        var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 14), FontSize = 12.5, BorderBrush = line, BorderThickness = new Thickness(1, 1, 0, 0) };
        foreach (var w in t.ResolveWidths()) table.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
        var group = new TableRowGroup();
        void Row(IReadOnlyList<string> cells, bool header)
        {
            var row = new TableRow();
            if (header) row.Background = new SolidColorBrush(Color.FromRgb(234, 243, 242));
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var p = new Paragraph(new Run(i < cells.Count ? cells[i] ?? "" : "")) { Margin = new Thickness(0) };
                if (header) { p.FontWeight = FontWeights.Bold; p.Foreground = accent; }
                row.Cells.Add(new TableCell(p) { BorderBrush = line, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 4, 6, 4) });
            }
            group.Rows.Add(row);
        }
        if (t.Headers.Count > 0) Row(t.Headers, true);
        foreach (var r in t.Rows) Row(r, false);
        table.RowGroups.Add(group);
        return table;
    }

    private static Brush ParseBrush(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex)) return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex));
        }
        catch { /* fall back */ }
        return new SolidColorBrush(fallback);
    }

    /// <summary>Prints through the standard Windows print dialog.</summary>
    public static void Print(DocModel doc, Letterhead lh, Window owner)
    {
        var dlg = new System.Windows.Controls.PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var fd = Build(doc, lh);
        fd.PageHeight = dlg.PrintableAreaHeight;
        fd.PageWidth = dlg.PrintableAreaWidth;
        fd.PagePadding = new Thickness(56);
        fd.ColumnWidth = dlg.PrintableAreaWidth;
        dlg.PrintDocument(((IDocumentPaginatorSource)fd).DocumentPaginator, doc.Title ?? "CaseKeeper");
    }
}
