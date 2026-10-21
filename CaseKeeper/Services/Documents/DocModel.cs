using System.Text;
using System.Text.RegularExpressions;

namespace CaseKeeper.Services.Documents;

public enum BlockKind { Heading1, Heading2, Heading3, Paragraph, Bullet, Numbered, Quote, Table, Rule, PageBreak, Spacer }

public enum BlockAlign { Left, Centre, Right, Justify }

/// <summary>A run of text with one style. A "\n" inside Text is a line break.</summary>
public sealed class Inline
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    public Inline() { }
    public Inline(string text, bool bold = false, bool italic = false, bool underline = false)
    {
        Text = text;
        Bold = bold;
        Italic = italic;
        Underline = underline;
    }
}

public sealed class TableData
{
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    /// <summary>Relative column widths. Worked out from the content when left empty.</summary>
    public double[] Widths { get; set; }

    public double[] ResolveWidths()
    {
        int n = Math.Max(Headers.Count, Rows.Count == 0 ? 0 : Rows.Max(r => r.Count));
        if (n == 0) return Array.Empty<double>();
        if (Widths is { Length: > 0 } w && w.Length == n) return Normalise(w);
        var lengths = new double[n];
        for (int i = 0; i < n; i++)
        {
            double head = i < Headers.Count ? Headers[i].Length : 0;
            double body = Rows.Count == 0 ? 0 : Rows.Average(r => i < r.Count ? Math.Min(r[i]?.Length ?? 0, 400) : 0);
            lengths[i] = Math.Clamp(Math.Max(head * 1.1, Math.Sqrt(Math.Max(body, 1)) * 6 + body * 0.15), 6, 120);
        }
        return Normalise(lengths);
    }

    private static double[] Normalise(double[] v)
    {
        var sum = v.Sum();
        return sum <= 0 ? v.Select(_ => 1d / v.Length).ToArray() : v.Select(x => x / sum).ToArray();
    }
}

public sealed class Block
{
    public BlockKind Kind { get; set; }
    public List<Inline> Inlines { get; set; } = new();
    public BlockAlign Align { get; set; } = BlockAlign.Left;
    /// <summary>The number shown for a numbered paragraph.</summary>
    public int Number { get; set; }
    /// <summary>0 for a top-level list item, 1 for an indented one.</summary>
    public int Level { get; set; }
    public TableData Table { get; set; }
    public bool KeepWithNext { get; set; }

    public string PlainText => string.Concat(Inlines.Select(i => i.Text));

    public static Block Para(string text, BlockAlign align = BlockAlign.Left, bool bold = false) =>
        new() { Kind = BlockKind.Paragraph, Align = align, Inlines = { new Inline(text ?? "", bold) } };

    public static Block Heading(string text, int level = 1) => new()
    {
        Kind = level switch { 1 => BlockKind.Heading1, 2 => BlockKind.Heading2, _ => BlockKind.Heading3 },
        Inlines = { new Inline(text ?? "") }, KeepWithNext = true
    };

    public static Block TableOf(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows, double[] widths = null) => new()
    {
        Kind = BlockKind.Table,
        Table = new TableData
        {
            Headers = headers.ToList(),
            Rows = rows.Select(r => r.Select(c => c ?? "").ToList()).ToList(),
            Widths = widths
        }
    };
}

/// <summary>
/// A document independent of file format. Letters, statements, reports, the audit trail and AI
/// reports are all built as one of these, then written out as Word, PDF or HTML, or shown on screen.
/// </summary>
public sealed class DocModel
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Shown as a large title block at the top. Off for letters, which open with the letterhead.</summary>
    public bool ShowTitle { get; set; } = true;
    public List<Block> Blocks { get; set; } = new();
    /// <summary>Small print at the foot of every page, next to the page number.</summary>
    public string Footer { get; set; } = "";
    public bool Landscape { get; set; }

    public DocModel Add(Block b)
    {
        Blocks.Add(b);
        return this;
    }

    public DocModel AddRange(IEnumerable<Block> blocks)
    {
        Blocks.AddRange(blocks);
        return this;
    }

    /// <summary>Plain text version, used for the AI, copying to the clipboard and search.</summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        if (ShowTitle && Title.Length > 0) sb.AppendLine(Title).AppendLine();
        foreach (var b in Blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Table:
                    sb.AppendLine(string.Join(" | ", b.Table.Headers));
                    foreach (var r in b.Table.Rows) sb.AppendLine(string.Join(" | ", r));
                    break;
                case BlockKind.Bullet: sb.Append(new string(' ', b.Level * 2)).Append("• ").AppendLine(b.PlainText); break;
                case BlockKind.Numbered: sb.Append(b.Number).Append(". ").AppendLine(b.PlainText); break;
                case BlockKind.Rule: sb.AppendLine("———"); break;
                case BlockKind.PageBreak: sb.AppendLine(); break;
                default: sb.AppendLine(b.PlainText); break;
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The simple formatting language used in templates and letters:
///   # Heading, ## Subheading, ### Minor heading
///   **bold**, *italic*, __underline__
///   - bullet (indent two spaces for a sub-bullet), 1. numbered list
///   #. numbered paragraph – numbering carries on through the whole document, as in a witness statement
///   > quotation, --- line across the page, [page break]
///   [right] / [centre] at the start of a line to align it
///   | table | rows | with | a | header row | followed by |---|---|
/// A single line break is kept (addresses); a blank line starts a new paragraph.
/// </summary>
public static class Markup
{
    private static readonly Regex Field = new(@"\{\{\s*([a-zA-Z0-9_.\-]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Fills {{fields}}. Fields with no value are left in place and reported back.</summary>
    public static string Merge(string text, IReadOnlyDictionary<string, string> fields, ICollection<string> missing = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Field.Replace(text, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            if (fields != null && fields.TryGetValue(key, out var v) && v != null) return v;
            missing?.Add(key);
            return m.Value;
        });
    }

    public static IEnumerable<string> FieldsIn(string text) =>
        string.IsNullOrEmpty(text) ? Enumerable.Empty<string>()
            : Field.Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct();

    public static List<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var para = new List<string>();
        int listNumber = 0, statementNumber = 0;
        BlockAlign paraAlign = BlockAlign.Left;

        void FlushPara()
        {
            if (para.Count == 0) return;
            var b = new Block { Kind = BlockKind.Paragraph, Align = paraAlign, Inlines = ParseInline(string.Join("\n", para)) };
            blocks.Add(b);
            para.Clear();
            paraAlign = BlockAlign.Left;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            if (trimmed.Length == 0) { FlushPara(); listNumber = 0; continue; }

            // tables: a run of lines starting with |
            if (trimmed.StartsWith('|'))
            {
                FlushPara();
                var rows = new List<List<string>>();
                bool hasHeader = false;
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    var cells = SplitRow(lines[i].Trim());
                    if (cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))) hasHeader = rows.Count == 1;
                    else rows.Add(cells);
                    i++;
                }
                i--;
                var table = new TableData();
                if (hasHeader && rows.Count > 0) { table.Headers = rows[0]; rows.RemoveAt(0); }
                table.Rows = rows;
                blocks.Add(new Block { Kind = BlockKind.Table, Table = table });
                continue;
            }

            if (trimmed.Equals("[page break]", StringComparison.OrdinalIgnoreCase)) { FlushPara(); blocks.Add(new Block { Kind = BlockKind.PageBreak }); continue; }
            if (Regex.IsMatch(trimmed, @"^(-{3,}|_{3,}|\*{3,})$")) { FlushPara(); blocks.Add(new Block { Kind = BlockKind.Rule }); continue; }

            var h = Regex.Match(trimmed, @"^(#{1,3})\s+(.*)$");
            if (h.Success)
            {
                FlushPara();
                listNumber = 0;
                var kind = h.Groups[1].Length switch { 1 => BlockKind.Heading1, 2 => BlockKind.Heading2, _ => BlockKind.Heading3 };
                blocks.Add(new Block { Kind = kind, Inlines = ParseInline(h.Groups[2].Value), KeepWithNext = true });
                continue;
            }

            var stmt = Regex.Match(trimmed, @"^#\.\s+(.*)$");
            if (stmt.Success)
            {
                FlushPara();
                blocks.Add(new Block { Kind = BlockKind.Numbered, Number = ++statementNumber, Inlines = ParseInline(stmt.Groups[1].Value) });
                continue;
            }

            var bullet = Regex.Match(trimmed, @"^[-*•]\s+(.*)$");
            if (bullet.Success)
            {
                FlushPara();
                blocks.Add(new Block { Kind = BlockKind.Bullet, Level = indent >= 2 ? 1 : 0, Inlines = ParseInline(bullet.Groups[1].Value) });
                continue;
            }

            var num = Regex.Match(trimmed, @"^(\d{1,3})[.)]\s+(.*)$");
            if (num.Success)
            {
                FlushPara();
                listNumber = listNumber == 0 ? int.Parse(num.Groups[1].Value) : listNumber + 1;
                blocks.Add(new Block { Kind = BlockKind.Numbered, Number = listNumber, Level = indent >= 2 ? 1 : 0, Inlines = ParseInline(num.Groups[2].Value) });
                continue;
            }

            if (trimmed.StartsWith("> ") || trimmed == ">")
            {
                FlushPara();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    quote.Add(lines[i].TrimStart().TrimStart('>').TrimStart());
                    i++;
                }
                i--;
                blocks.Add(new Block { Kind = BlockKind.Quote, Inlines = ParseInline(string.Join("\n", quote)) });
                continue;
            }

            var align = Regex.Match(trimmed, @"^\[(right|centre|center|justify)\]\s*(.*)$", RegexOptions.IgnoreCase);
            if (align.Success)
            {
                if (para.Count > 0 && paraAlign == BlockAlign.Left) FlushPara();
                paraAlign = align.Groups[1].Value.ToLowerInvariant() switch
                {
                    "right" => BlockAlign.Right,
                    "justify" => BlockAlign.Justify,
                    _ => BlockAlign.Centre
                };
                para.Add(align.Groups[2].Value);
                continue;
            }

            para.Add(trimmed);
        }
        FlushPara();
        return blocks;
    }

    private static List<string> SplitRow(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|')) inner = inner[..^1];
        return inner.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>Splits **bold**, *italic* and __underline__ into styled runs.</summary>
    public static List<Inline> ParseInline(string text)
    {
        var result = new List<Inline>();
        var sb = new StringBuilder();
        bool bold = false, italic = false, underline = false;

        void Flush()
        {
            if (sb.Length == 0) return;
            result.Add(new Inline(sb.ToString(), bold, italic, underline));
            sb.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && "*_\\[]|#".Contains(text[i + 1])) { sb.Append(text[++i]); continue; }
            // Three or more underscores are a line to write on (Signed: ______), not formatting
            if (ch == '_' && i + 2 < text.Length && text[i + 1] == '_' && text[i + 2] == '_')
            {
                while (i < text.Length && text[i] == '_') sb.Append(text[i++]);
                i--;
                continue;
            }
            if (ch == '*' && i + 1 < text.Length && text[i + 1] == '*') { Flush(); bold = !bold; i++; continue; }
            if (ch == '_' && i + 1 < text.Length && text[i + 1] == '_') { Flush(); underline = !underline; i++; continue; }
            if (ch == '*' && (italic || (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))))
            {
                Flush();
                italic = !italic;
                continue;
            }
            sb.Append(ch);
        }
        Flush();
        if (result.Count == 0) result.Add(new Inline(""));
        return result;
    }

    /// <summary>Light conversion of an AI model's Markdown into the same blocks.</summary>
    public static List<Block> FromMarkdown(string md)
    {
        // Our language is a near-subset of Markdown; just normalise the bits that differ
        var text = Regex.Replace(md ?? "", @"^#{4,6}\s", "### ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*```.*$", "", RegexOptions.Multiline);
        return Parse(text);
    }

    /// <summary>Escapes text so it is shown literally rather than read as formatting.</summary>
    public static string Escape(string s) =>
        string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"([*_\\|])", @"\$1");
}
