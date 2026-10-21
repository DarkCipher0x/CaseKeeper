using System.Net;
using System.Text;
using CaseKeeper.Models;

namespace CaseKeeper.Services.Documents;

/// <summary>
/// Writes a single self-contained HTML file (styles and logo embedded, no scripts, no external
/// links) that prints cleanly on A4. All text is HTML-encoded, so nothing typed into a document
/// can turn into active content.
/// </summary>
public static class HtmlDocWriter
{
    public static string Render(DocModel doc, Letterhead lh)
    {
        var accent = SafeColour(lh?.AccentColor) ?? "#0B7A75";
        var font = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(lh?.FontFamily) ? "Segoe UI" : lh.FontFamily);
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en-GB\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">");
        sb.AppendLine("<meta name=\"generator\" content=\"CaseKeeper\">");
        sb.Append("<title>").Append(E(doc.Title)).AppendLine("</title>");
        sb.AppendLine("<style>");
        sb.AppendLine($@":root{{--accent:{accent};--ink:#11292b;--muted:#586e70;--line:#cddada}}
*{{box-sizing:border-box}}
body{{margin:0;background:#eef3f3;color:var(--ink);font:11pt/1.5 '{font}','Segoe UI',Arial,sans-serif}}
.page{{max-width:210mm;margin:24px auto;background:#fff;padding:20mm 22mm 24mm;box-shadow:0 6px 24px rgba(0,0,0,.08)}}
h1{{color:var(--accent);font-size:17pt;margin:18px 0 6px}} h2{{font-size:13.5pt;margin:16px 0 6px}} h3{{font-size:11.5pt;margin:14px 0 4px}}
p{{margin:0 0 9px}} .title{{font-size:22pt;color:var(--accent);font-weight:700;margin:0 0 4px}} .subtitle{{color:var(--muted);margin:0 0 14px}}
.titlebar{{border-bottom:1.5px solid var(--accent);margin-bottom:16px;padding-bottom:6px}}
ul,ol{{margin:0 0 9px 0;padding-left:22px}} li{{margin:0 0 4px}} ol.stmt{{list-style:none;padding-left:0}} ol.stmt li{{display:flex;gap:10px}} ol.stmt li b.n{{min-width:26px}}
blockquote{{margin:0 0 10px;padding:4px 0 4px 14px;border-left:3px solid var(--accent);color:var(--muted);font-style:italic}}
hr{{border:0;border-top:1px solid var(--line);margin:12px 0}}
table{{border-collapse:collapse;width:100%;margin:6px 0 14px;font-size:9.5pt}} th,td{{border:1px solid var(--line);padding:5px 7px;vertical-align:top;text-align:left}}
th{{background:#eaf3f2;color:var(--accent)}} .right{{text-align:right}} .centre{{text-align:center}} .justify{{text-align:justify}}
.lh{{margin:-4mm 0 18px;padding-bottom:10px;border-bottom:1.5px solid var(--accent);display:flex;gap:18px;align-items:flex-start}}
.lh .name{{font-size:16pt;font-weight:700;color:var(--accent)}} .lh .info{{color:var(--muted);font-size:9pt;line-height:1.4}}
.lh img{{max-height:62px;max-width:180px}} .lh.right{{flex-direction:row-reverse;justify-content:space-between}} .lh.centre{{flex-direction:column;align-items:center;text-align:center}}
.band{{background:var(--accent);color:#fff;margin:-20mm -22mm 12px;padding:14px 22mm;display:flex;align-items:center;justify-content:space-between}}
.band .name{{color:#fff;font-size:17pt;font-weight:700}} .band img{{max-height:48px}} .bandinfo{{color:var(--muted);font-size:9pt;margin-bottom:16px}}
.minimal .name{{font-size:11pt;letter-spacing:.06em;text-transform:uppercase}}
.foot{{margin-top:28px;padding-top:6px;border-top:1px solid var(--line);color:var(--muted);font-size:8.5pt}}
.pb{{break-after:page;height:0}}
@page{{size:A4;margin:18mm 20mm}}
@media print{{body{{background:#fff}} .page{{box-shadow:none;margin:0;max-width:none;padding:0}} .band{{margin:0 0 12px;-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}");
        sb.AppendLine("</style></head><body><main class=\"page\">");

        if (lh != null) sb.Append(Letterhead(lh));
        if (doc.ShowTitle && !string.IsNullOrWhiteSpace(doc.Title))
        {
            sb.Append("<div class=\"titlebar\"><p class=\"title\">").Append(E(doc.Title)).Append("</p>");
            if (!string.IsNullOrWhiteSpace(doc.Subtitle)) sb.Append("<p class=\"subtitle\">").Append(E(doc.Subtitle)).Append("</p>");
            sb.AppendLine("</div>");
        }

        string openList = null;
        void CloseList()
        {
            if (openList != null) sb.Append("</").Append(openList == "stmt" ? "ol" : openList).AppendLine(">");
            openList = null;
        }

        foreach (var b in doc.Blocks)
        {
            if (b.Kind is not (BlockKind.Bullet or BlockKind.Numbered)) CloseList();
            var cls = b.Align switch { BlockAlign.Right => " class=\"right\"", BlockAlign.Centre => " class=\"centre\"", BlockAlign.Justify => " class=\"justify\"", _ => "" };
            switch (b.Kind)
            {
                case BlockKind.Heading1: sb.Append("<h1").Append(cls).Append('>').Append(Inlines(b)).AppendLine("</h1>"); break;
                case BlockKind.Heading2: sb.Append("<h2").Append(cls).Append('>').Append(Inlines(b)).AppendLine("</h2>"); break;
                case BlockKind.Heading3: sb.Append("<h3").Append(cls).Append('>').Append(Inlines(b)).AppendLine("</h3>"); break;
                case BlockKind.Paragraph: sb.Append("<p").Append(cls).Append('>').Append(Inlines(b)).AppendLine("</p>"); break;
                case BlockKind.Quote: sb.Append("<blockquote>").Append(Inlines(b)).AppendLine("</blockquote>"); break;
                case BlockKind.Rule: sb.AppendLine("<hr>"); break;
                case BlockKind.PageBreak: sb.AppendLine("<div class=\"pb\"></div>"); break;
                case BlockKind.Spacer: sb.AppendLine("<p>&nbsp;</p>"); break;
                case BlockKind.Bullet:
                    if (openList != "ul") { CloseList(); sb.AppendLine("<ul>"); openList = "ul"; }
                    sb.Append("<li").Append(b.Level > 0 ? " style=\"margin-left:20px\"" : "").Append('>').Append(Inlines(b)).AppendLine("</li>");
                    break;
                case BlockKind.Numbered:
                    if (openList != "stmt") { CloseList(); sb.AppendLine("<ol class=\"stmt\">"); openList = "stmt"; }
                    sb.Append("<li><b class=\"n\">").Append(b.Number).Append(".</b><span>").Append(Inlines(b)).AppendLine("</span></li>");
                    break;
                case BlockKind.Table:
                    sb.AppendLine("<table>");
                    if (b.Table.Headers.Count > 0)
                        sb.Append("<thead><tr>").Append(string.Concat(b.Table.Headers.Select(h => $"<th>{E(h)}</th>"))).AppendLine("</tr></thead>");
                    sb.AppendLine("<tbody>");
                    foreach (var r in b.Table.Rows)
                        sb.Append("<tr>").Append(string.Concat(r.Select(c => $"<td>{E(c).Replace("\n", "<br>")}</td>"))).AppendLine("</tr>");
                    sb.AppendLine("</tbody></table>");
                    break;
            }
        }
        CloseList();

        var footer = string.Join("  ·  ", new[] { lh?.FooterText, doc.Footer }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        if (footer.Length > 0) sb.Append("<div class=\"foot\">").Append(E(footer)).AppendLine("</div>");
        sb.AppendLine("</main></body></html>");
        return sb.ToString();
    }

    private static string Letterhead(Letterhead lh)
    {
        var lines = (lh.AddressLines ?? "").Replace("\r", "").Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).Select(E).ToList();
        var contact = string.Join(" &nbsp;·&nbsp; ", new[] { lh.Phone, lh.Email, lh.Website }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(E));
        var img = lh.LogoPng is { Length: > 0 } ? $"<img alt=\"\" src=\"data:image/png;base64,{Convert.ToBase64String(lh.LogoPng)}\">" : "";
        var info = string.Join("<br>", lines.Append(contact).Where(s => s.Length > 0));
        if (!string.IsNullOrWhiteSpace(lh.ReferenceLine)) info += $"<br><i>{E(lh.ReferenceLine)}</i>";

        return lh.Layout switch
        {
            "Band" => $"<div class=\"band\"><div class=\"name\">{E(lh.SenderName)}</div>{img}</div><div class=\"bandinfo\">{info.Replace("<br>", " &nbsp;·&nbsp; ")}</div>\n",
            "Minimal" => $"<div class=\"lh minimal\"><div><div class=\"name\">{E(lh.SenderName)}</div><div class=\"info\">{info.Replace("<br>", " · ")}</div></div></div>\n",
            _ => $"<div class=\"lh {(lh.LogoAlign == "Left" ? "" : lh.LogoAlign == "Centre" ? "centre" : "right")}\">{img}<div><div class=\"name\">{E(lh.SenderName)}</div><div class=\"info\">{info}</div></div></div>\n"
        };
    }

    private static string Inlines(Block b)
    {
        var sb = new StringBuilder();
        foreach (var i in b.Inlines)
        {
            var t = E(i.Text).Replace("\n", "<br>");
            if (i.Underline) t = $"<u>{t}</u>";
            if (i.Italic) t = $"<em>{t}</em>";
            if (i.Bold) t = $"<strong>{t}</strong>";
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

    private static string SafeColour(string c)
    {
        var h = (c ?? "").Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(h, "^#[0-9A-Fa-f]{6}$") ? h : null;
    }
}
