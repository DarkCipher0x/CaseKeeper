using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>
/// "Ask about my case": gathers what the AI needs to answer a question about the person's own case – the case memory,
/// plus the pages of their documents that best match the question (from the search index) and those documents'
/// summaries – and turns the document references in the answer into buttons that open the page. Organising help
/// only: the AI is told to answer from these sources alone and not to give legal advice. Nothing is ever changed.
/// </summary>
public static class CaseQuestion
{
    public sealed record Source(long DocumentId, int Page, string Title);

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "can", "how", "what", "who", "does", "did", "is", "are", "was", "were", "my", "me", "do", "to", "of", "in",
        "on", "an", "it", "if", "or", "be", "with", "about", "when", "will", "should", "there", "this", "that", "at", "by", "from",
        "where", "which", "have", "has", "had", "last", "first", "any", "all", "say", "said", "tell", "show", "case", "please", "ever"
    };

    public static List<string> Keywords(string question) =>
        Regex.Split((question ?? "").ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct().Take(8).ToList();

    /// <summary>
    /// The context block for the AI (case memory, then matching extracts with their document id and page, then summaries),
    /// and the sources it came from. At most <paramref name="maxChars"/> characters.
    /// </summary>
    public static (string Context, List<Source> Sources) Build(Database db, string question, int maxChars = 14000)
    {
        var sb = new StringBuilder();
        var memory = CaseMemory.Text(db, Math.Min(6000, maxChars / 2));
        sb.AppendLine("WHAT IS KNOWN ABOUT THE CASE (from the person's own records):").AppendLine(memory.Length > 0 ? memory : "(nothing recorded yet)").AppendLine();

        // Score pages by how many of the question's words they contain (each word searched on its own, so it's an OR)
        var words = Keywords(question);
        var score = new Dictionary<(long Doc, int Page), int>();
        foreach (var w in words)
            foreach (var hit in db.Search(w, 40))
            {
                var key = (hit.DocumentId, hit.PageNumber);
                score[key] = score.GetValueOrDefault(key) + 1;
            }
        var docs = db.GetDocuments().ToDictionary(d => d.Id);
        var sources = new List<Source>();
        var extracts = new StringBuilder();
        int budget = maxChars - sb.Length - 1500;
        foreach (var ((docId, page), _) in score.OrderByDescending(s => s.Value).ThenByDescending(s => docs.GetValueOrDefault(s.Key.Doc)?.DocumentDate ?? DateTime.MinValue))
        {
            if (sources.Count >= 6 || budget < 400) break;
            if (!docs.TryGetValue(docId, out var doc)) continue;
            var pages = db.GetAllPageText(docId);
            if (!pages.TryGetValue(page, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            var excerpt = Window(text, words, 700);
            var head = $"[doc:{docId}:{page}] “{doc.Title}”{(doc.DocumentDate is { } dd ? ", dated " + dd.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-GB")) : "")}, page {page}:";
            extracts.AppendLine(head).AppendLine(excerpt).AppendLine();
            budget -= head.Length + excerpt.Length;
            sources.Add(new Source(docId, page, doc.Title));
        }
        if (extracts.Length > 0) sb.AppendLine("MATCHING EXTRACTS FROM THE PERSON'S DOCUMENTS:").Append(extracts);
        else sb.AppendLine("MATCHING EXTRACTS FROM THE PERSON'S DOCUMENTS: none found for these words.").AppendLine();

        var summaries = sources.Select(s => s.DocumentId).Distinct().Select(id => docs[id])
            .Where(d => !string.IsNullOrWhiteSpace(d.Description)).Take(6)
            .Select(d => $"[doc:{d.Id}] “{d.Title}”: {Clip(d.Description, 300)}").ToList();
        if (summaries.Count > 0) sb.AppendLine("SUMMARIES OF THOSE DOCUMENTS:").AppendLine(string.Join("\n", summaries));
        var result = sb.ToString();
        return (result.Length > maxChars ? result[..maxChars] : result, sources);
    }

    /// <summary>About <paramref name="size"/> characters of the page around the first question word it contains.</summary>
    public static string Window(string text, IReadOnlyList<string> words, int size)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length <= size) return text;
        int at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        int start = Math.Clamp(at - size / 3, 0, text.Length - size);
        return (start > 0 ? "…" : "") + text.Substring(start, size) + (start + size < text.Length ? "…" : "");
    }

    // [label](doc:12:3), (doc:12:3), [doc:12:3] or doc:12 – the model writes all of these
    private static readonly Regex DocLink = new(@"\[([^\]]{1,80})\]\(\s*doc:(\d+)(?::(\d+))?\s*\)|[\(\[]doc:(\d+)(?::(\d+))?[\)\]]", RegexOptions.IgnoreCase);

    /// <summary>
    /// Replaces the document references in an answer with readable names and returns them as (document, page, label),
    /// keeping only documents that exist. Unknown ids (a model making one up) are dropped.
    /// </summary>
    public static (string Text, List<Source> Links) ExtractDocLinks(Database db, string answer)
    {
        var links = new List<Source>();
        var text = DocLink.Replace(answer ?? "", m =>
        {
            bool labelled = m.Groups[1].Success;
            long id = long.Parse(labelled ? m.Groups[2].Value : m.Groups[4].Value, CultureInfo.InvariantCulture);
            var pageGroup = labelled ? m.Groups[3] : m.Groups[5];
            int page = pageGroup.Success && int.TryParse(pageGroup.Value, out var p) ? p : 1;
            var doc = db.GetDocument(id);
            if (doc == null || doc.DeletedAt != null) return labelled ? m.Groups[1].Value : "";
            if (!links.Any(l => l.DocumentId == id && l.Page == page)) links.Add(new Source(id, page, doc.Title));
            return labelled ? m.Groups[1].Value : $"(“{Clip(doc.Title, 50)}”, p. {page})";
        });
        return (Regex.Replace(text, @"[ \t]+([.,;:])", "$1").Trim(), links);
    }

    private static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s[..n] + "…" : s;
}
