using System.Globalization;
using System.Text.RegularExpressions;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>
/// Matches documents to CaseKeeper's own category catalogue without AI: by form code ("C100", "FL401"),
/// by recognisable titles and headings, and by words shared with a category's name and description.
/// Used by ordinary imports to pre-fill the category, and by Bulk Case Import to give the local AI a
/// short list of real categories to choose from (a small model can't read all 470 every time).
/// </summary>
public static class CategoryMatcher
{
    public sealed record Candidate(Category Category, double Score, string Why);

    /// <summary>The "don't know yet" category. Every library has it; documents wait here for review.</summary>
    public static Category Unknown(IEnumerable<Category> categories) =>
        categories.FirstOrDefault(c => c.Name == "Uncategorised") ?? categories.LastOrDefault();

    /// <summary>
    /// Guesses the category from form codes or category names in a file name. Where a category comes as
    /// an Applicant's/Respondent's pair, the name decides which: anything mentioning the respondent picks
    /// the respondent's version, otherwise the applicant's.
    /// </summary>
    public static long? GuessFromName(string fileName, IReadOnlyList<Category> categories)
    {
        var lower = (fileName ?? "").ToLowerInvariant();
        bool wantsRespondent = Regex.IsMatch(lower, @"respondent|\bresp\b|\bres\b|other party|\br1\b|\br2\b");

        long? Pick(IEnumerable<Category> matches)
        {
            var list = matches.ToList();
            if (list.Count == 0) return null;
            var wanted = wantsRespondent ? Data.DefaultCategories.RespondentSuffix : Data.DefaultCategories.ApplicantSuffix;
            var preferred = list.FirstOrDefault(c => c.Name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
            // Fall back to a version with no suffix at all, then to whatever matched
            return (preferred ?? list.FirstOrDefault(c => !c.Name.EndsWith(')')) ?? list[0]).Id;
        }

        foreach (var code in categories.Where(c => !string.IsNullOrWhiteSpace(c.Code))
                     .Select(c => c.Code).Distinct().OrderByDescending(c => c.Length))
        {
            var pattern = Regex.Escape(code.ToLowerInvariant()).Replace("\\ ", "[\\s_-]?");
            if (Regex.IsMatch(lower, $@"(^|[^a-z0-9]){pattern}($|[^a-z0-9])"))
                return Pick(categories.Where(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)));
        }

        // Match on the category name itself, ignoring the "(Applicant's)" / "(Respondent's)" part
        foreach (var group in categories
                     .Select(c => new { Cat = c, Base = BaseName(c.Name) })
                     .Where(x => x.Base.Length > 6)
                     .GroupBy(x => x.Base, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Key.Length))
        {
            if (lower.Contains(group.Key.ToLowerInvariant()))
                return Pick(group.Select(x => x.Cat));
        }

        foreach (var (pattern, name) in Hints)
            if (Regex.IsMatch(lower, pattern))
            {
                var hit = Pick(categories.Where(c => BaseName(c.Name).Equals(name, StringComparison.OrdinalIgnoreCase)));
                if (hit.HasValue) return hit;
            }
        return null;
    }

    private static readonly (string Pattern, string Name)[] Hints =
    {
        ("safeguarding", "Safeguarding letter"), ("section 7|s7 report|s\\.7", "Section 7 report"),
        ("position statement", "Position statement"), ("witness statement", "Witness statement"),
        ("exhibit", "Individual exhibit"), ("cafcass", "Cafcass correspondence"),
        ("judgment|judgement", "Judgment / approved judgment"),
        ("notice of hearing|hearing notice", "Notice of hearing"), ("directions|case management", "Directions / case management order"),
        ("police", "Police disclosure / records"), ("school", "School records"), ("\\bmp\\b", "Letters to my MP"),
        ("screenshot|whatsapp|text message", "Messages / texts / emails (screenshots)")
    };

    /// <summary>Wording that points to a family of categories, used only to build the AI's short list.</summary>
    private static readonly (string Pattern, string[] Names)[] TextHints =
    {
        (@"\b(gp|doctor|dr|surgery|hospital|nhs|health visitor|midwife|clinic|nurse)\b",
            new[] { "Correspondence with health services", "Medical / health records", "GP records", "Medical letter / fit note" }),
        (@"\b(school|headteacher|head teacher|teacher|pupil|term)\b",
            new[] { "Correspondence with the school", "School correspondence", "School records", "School attendance records", "School reports" }),
        (@"\b(police|crime reference|incident number|officer in the case)\b", new[] { "Correspondence with the police", "Police disclosure / records" }),
        (@"\b(cafcass|family court adviser)\b", new[] { "Cafcass correspondence", "Safeguarding letter" }),
        (@"\bcontact centre\b", new[] { "Contact centre report" }),
        (@"\b(notice of hearing|take notice|will take place on|has been listed)\b", new[] { "Notice of hearing", "Court listing / hearing notice email" }),
        (@"(^|\n)\s*dear\b|\byours (sincerely|faithfully)\b",
            new[] { "Letters I have written", "Correspondence between the parties", "Third-party letters", "Court correspondence" })
    };

    /// <summary>"Position statement (Respondent's)" becomes "Position statement".</summary>
    public static string BaseName(string name)
    {
        int i = name.LastIndexOf(" (", StringComparison.Ordinal);
        return i > 0 && name.EndsWith(')') ? name[..i] : name;
    }

    /// <summary>Finds a date like 2026-03-14, 14.03.2026, 14-03-26 or 14 March 2026 in a file name (UK day-first order).</summary>
    public static DateTime? GuessDate(string fileName)
    {
        var uk = CultureInfo.GetCultureInfo("en-GB");
        var m = Regex.Match(fileName, @"(20\d{2})[-_. ]?(0[1-9]|1[0-2])[-_. ]?(0[1-9]|[12]\d|3[01])");
        if (m.Success && DateTime.TryParseExact($"{m.Groups[1]}-{m.Groups[2]}-{m.Groups[3]}", "yyyy-MM-dd", uk, DateTimeStyles.None, out var d1)) return d1;
        m = Regex.Match(fileName, @"(\d{1,2})[-_.](\d{1,2})[-_.](\d{2,4})");
        if (m.Success)
        {
            var y = m.Groups[3].Value.Length == 2 ? "20" + m.Groups[3].Value : m.Groups[3].Value;
            if (DateTime.TryParseExact($"{m.Groups[1].Value.PadLeft(2, '0')}/{m.Groups[2].Value.PadLeft(2, '0')}/{y}", "dd/MM/yyyy", uk, DateTimeStyles.None, out var d2)) return d2;
        }
        m = Regex.Match(fileName, @"(\d{1,2})(st|nd|rd|th)?[\s_-]+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*[\s_-]+(20\d{2})", RegexOptions.IgnoreCase);
        if (m.Success && DateTime.TryParse($"{m.Groups[1]} {m.Groups[3]} {m.Groups[4]}", uk, DateTimeStyles.None, out var d3)) return d3;
        return null;
    }

    /// <summary>A tidy title from a file name: underscores to spaces, no doubled spaces.</summary>
    public static string CleanTitle(string name)
    {
        var t = Regex.Replace(name ?? "", @"[_]+", " ");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim();
        return t.Length == 0 ? (name ?? "") : t;
    }

    // ------------------------------------------------------------------ candidates for the AI
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "of", "to", "in", "on", "a", "an", "or", "by", "with", "from", "this", "that", "is", "are", "be",
        "applicant's", "respondent's", "applicant", "respondent", "form", "court", "family", "case", "order", "document", "letter",
        "re", "dated", "date", "page", "who", "what", "your", "you", "my", "it", "as", "at", "not", "any", "all", "has", "have"
    };

    public static HashSet<string> Words(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z][a-z'\-]{2,}"))
        {
            if (StopWords.Contains(m.Value)) continue;
            var w = m.Value.TrimEnd('\'');
            // "letters" and "letter", "records" and "record" are the same word for matching
            if (w.Length > 4 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
            set.Add(w);
        }
        return set;
    }

    /// <summary>
    /// A short list of the categories most likely to fit a document, best first: form codes found in the
    /// title or the first page beat everything; then the file-name rules; then shared words. The
    /// Uncategorised category is never in the list itself – the AI says "UNKNOWN" instead.
    /// </summary>
    public static List<Candidate> Candidates(string title, string fileName, string firstText, IReadOnlyList<Category> categories, int max = 10)
    {
        var scores = new Dictionary<long, (double Score, string Why)>();
        void Bump(Category c, double s, string why)
        {
            if (c == null || c.Name == "Uncategorised") return;
            if (!scores.TryGetValue(c.Id, out var cur) || cur.Score < s) scores[c.Id] = (Math.Max(s, cur.Score), why);
            else scores[c.Id] = (cur.Score + s * 0.1, cur.Why);
        }

        var head = (firstText ?? "").Length > 4000 ? firstText[..4000] : firstText ?? "";
        var lowerHead = head.ToLowerInvariant();
        var lowerName = $"{title} {fileName}".ToLowerInvariant();

        // 1. Form codes, e.g. "Form C100" at the top of page 1, or "C1A" in the file name
        foreach (var c in categories.Where(c => !string.IsNullOrWhiteSpace(c.Code)))
        {
            var pattern = $@"(^|[^a-z0-9]){Regex.Escape(c.Code.ToLowerInvariant()).Replace("\\ ", "[\\s_-]?")}($|[^a-z0-9])";
            if (Regex.IsMatch(lowerName, pattern)) Bump(c, 0.95, $"The file name contains the form code {c.Code}");
            else if (Regex.IsMatch(lowerHead, @"\bform\s+" + Regex.Escape(c.Code.ToLowerInvariant()) + @"\b"))
                Bump(c, 0.9, $"The text says “Form {c.Code}”");
            else if (Regex.IsMatch(lowerHead.Length > 600 ? lowerHead[..600] : lowerHead, pattern))
                Bump(c, 0.7, $"The form code {c.Code} appears near the top");
        }

        // 2. The rules ordinary imports use on file names
        if (GuessFromName($"{title} {fileName}", categories) is { } guessed)
            Bump(categories.First(c => c.Id == guessed), 0.75, "The file name matches this category");

        // 3. Recognisable headings in the text
        foreach (var (pattern, name) in Hints)
            if (Regex.IsMatch(lowerHead.Length > 1500 ? lowerHead[..1500] : lowerHead, pattern))
                foreach (var c in categories.Where(c => BaseName(c.Name).Equals(name, StringComparison.OrdinalIgnoreCase)))
                    Bump(c, 0.55, "The text mentions “" + name.ToLowerInvariant() + "”");

        // 3b. What kind of paper it is, from its wording: letters, health, school, police, Cafcass, notices
        var start = lowerHead.Length > 2500 ? lowerHead[..2500] : lowerHead;
        foreach (var (pattern, names) in TextHints)
            if (Regex.IsMatch(start, pattern))
                foreach (var c in categories.Where(c => names.Contains(BaseName(c.Name), StringComparer.OrdinalIgnoreCase)))
                    Bump(c, 0.45, "The wording suggests " + BaseName(c.Name).ToLowerInvariant());

        // 4. Shared words between the document and each category's name and description
        var docWords = Words($"{title} {fileName} {head}");
        if (docWords.Count > 0)
            foreach (var c in categories)
            {
                var catWords = Words($"{BaseName(c.Name)} {c.Description}");
                if (catWords.Count == 0) continue;
                int shared = catWords.Count(docWords.Contains);
                if (shared == 0) continue;
                var nameWords = Words(BaseName(c.Name));
                double nameShare = nameWords.Count == 0 ? 0 : (double)nameWords.Count(docWords.Contains) / nameWords.Count;
                Bump(c, Math.Min(0.5, 0.15 * shared + 0.3 * nameShare), "Shares words with this category");
            }

        return scores.OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => categories.First(c => c.Id == kv.Key).DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(kv => new Candidate(categories.First(c => c.Id == kv.Key), Math.Round(kv.Value.Score, 2), kv.Value.Why))
            .ToList();
    }
}
