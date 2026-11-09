using System.Globalization;
using System.Text.RegularExpressions;
using CaseKeeper.Models;

namespace CaseKeeper.Services;

/// <summary>What sort of paper a document is, most important first. The numbers are the order on the Documents screen.</summary>
public enum DocImportance
{
    Starred = 0,
    Court = 1,
    Reports = 2,
    Evidence = 3,
    Correspondence = 4,
    Notes = 5,
    Media = 6,
    Other = 7
}

/// <summary>
/// Puts the Documents list in an order a person can use: what each file is (court papers first, photos and working
/// notes last), and which copy of a document is the latest – so a dozen drafts of one position statement show as one
/// line, "+11 older versions", with the latest on top.
///
/// Everything is worked out on this PC from what's already stored: the category, the title and file type, the dates in
/// the file, and the version groups CaseKeeper's word-by-word comparison found. Nothing is changed, renamed or deleted;
/// older versions are only folded away, and the person can show them again.
/// </summary>
public static class DocumentOrganiser
{
    public const string LatestOnlySetting = "docs.latestOnly", ArrangeSetting = "docs.arrange";
    public const string ArrangeImportance = "importance", ArrangeNewest = "newest", ArrangeImported = "imported";

    /// <summary>Tags set in Import review's "Duplicates &amp; versions" – the person's own decision always wins.</summary>
    public const string LatestTag = "Latest version", OlderTag = "Older version";

    public static string Label(DocImportance i) => i switch
    {
        DocImportance.Starred => "Starred",
        DocImportance.Court => "Court papers & orders",
        DocImportance.Reports => "Reports & assessments",
        DocImportance.Evidence => "Evidence",
        DocImportance.Correspondence => "Letters & correspondence",
        DocImportance.Notes => "My notes & working copies",
        DocImportance.Media => "Photos, screenshots & recordings",
        _ => "Other documents"
    };

    // ------------------------------------------------------------------ what a document is
    private static readonly Dictionary<string, DocImportance> GroupTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Court Applications & Forms"] = DocImportance.Court, ["Court Orders & Notices"] = DocImportance.Court,
        ["Hearings & Case Management"] = DocImportance.Court, ["Statements & Submissions"] = DocImportance.Court,
        ["Appeals & Enforcement"] = DocImportance.Court, ["Adoption & Special Guardianship"] = DocImportance.Court,
        ["CAFCASS"] = DocImportance.Reports, ["Experts & Assessments"] = DocImportance.Reports, ["Local Authority & Agencies"] = DocImportance.Reports,
        ["Police & Criminal"] = DocImportance.Reports, ["Health & Medical"] = DocImportance.Reports, ["Education & Childcare"] = DocImportance.Reports,
        ["Evidence"] = DocImportance.Evidence, ["Evidence Exhibits"] = DocImportance.Evidence,
        ["Correspondence"] = DocImportance.Correspondence, ["Complaints & Representations"] = DocImportance.Correspondence,
        ["Legal Aid, Fees & Costs"] = DocImportance.Correspondence, ["Mediation & Out-of-Court"] = DocImportance.Correspondence,
        ["Divorce & Finances"] = DocImportance.Correspondence, ["International & Relocation"] = DocImportance.Correspondence,
        ["Personal & Identity Documents"] = DocImportance.Correspondence,
        ["My Notes & Working Papers"] = DocImportance.Notes, ["Legal Research & Authorities"] = DocImportance.Notes
    };

    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".webp", ".mp3", ".m4a", ".wav", ".ogg", ".aac", ".amr", ".mp4", ".mov", ".avi", ".wmv", ".3gp" };

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex MediaWords = new(@"\b(screenshot|screen shot|img|image|photo|picture|pic|png|jpe?g|heic|recording|voice ?note|audio|video|call)\b", Opts);
    private static readonly Regex NoteWords = new(@"\b(checklist|check list|analysis|not for filing|prep|preparation|notes?|resume|inventory|re-?index|index|to ?do|plan|working|scratch|research|evidence review|summary for me)\b", Opts);
    private static readonly Regex CourtWords = new(@"\b(order|orders|c\d{1,3}a?|fl\d{3}a?|position statement|(?<!bank )statement|witness|affidavit|application|applicant|respondent|judgment|judgement|directions?|notice of (?:hearing|proceedings)|hearing bundle|bundle|skeleton argument|submissions?|court|tribunal|appeal|undertaking|injunction|non-molestation)\b", Opts);
    private static readonly Regex ReportWords = new(@"\b(report|assessment|section ?7|s\.? ?7|section ?16a|s\.? ?16a|16a|cafcass|safeguarding|risk assessment|psycholog\w*|expert|medical|gp|hospital|school|nursery|social work\w*|local authority|police disclosure|dbs|drug test|hair strand|parenting)\b", Opts);
    private static readonly Regex LetterWords = new(@"\b(letter|email|e-mail|correspondence|messages?|reply|response|complaint|transcript|text|whatsapp|sms|invoice|receipt|certificate|referral|re-?allocation|confirmation)\b", Opts);

    /// <summary>What sort of paper this is: from its category when it's filed, otherwise from its file type and title.</summary>
    public static DocImportance Importance(CaseDocument d, Category category)
    {
        if (d.IsImportant) return DocImportance.Starred;
        if (category != null && !IsUncategorised(category) && GroupTiers.TryGetValue(category.GroupName ?? "", out var tier))
        {
            // A photo or recording filed as evidence is still a photo – the Evidence tier is for papers
            if (tier == DocImportance.Evidence && MediaTypes.Contains(d.FileExtension ?? "")) return DocImportance.Media;
            return tier;
        }
        var title = $"{d.Title} {System.IO.Path.GetFileNameWithoutExtension(d.OriginalFileName ?? "")}".Replace('_', ' ');
        if (MediaTypes.Contains(d.FileExtension ?? "") || MediaWords.IsMatch(title) || Regex.IsMatch(title, @"^\s*(img|dsc|pxl|vid)[ _-]?\d", Opts))
            return DocImportance.Media;
        if (NoteWords.IsMatch(title)) return DocImportance.Notes;
        if (CourtWords.IsMatch(title)) return DocImportance.Court;
        if (ReportWords.IsMatch(title)) return DocImportance.Reports;
        if (LetterWords.IsMatch(title)) return DocImportance.Correspondence;
        return DocImportance.Other;
    }

    private static bool IsUncategorised(Category c) =>
        string.Equals(c.Name, "Uncategorised", StringComparison.OrdinalIgnoreCase) || string.Equals(c.DisplayName, "Uncategorised", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ versions of one document, from the title
    // A version word only counts after a separator (" - ", "_", "(") so "Notice of final hearing" stays whole,
    // while "Position statement - REVISED COPY v6" and "Position statement v2" both become "position statement".
    private static readonly Regex Separator = new(@"\s+[-–—]\s+|_+|\(|\[", Opts);
    private static readonly Regex MarkerStart = new(@"^\s*(revised|revision|original|amended|updated|final|draft|copy|version|tone[- ]?edited|clean|tracked|redline|marked[- ]?up|save[ds]?|corrected|v\d+|rev\s?\d+|\d+\s*[\)\]])\b", Opts);
    private static readonly Regex VersionNumber = new(@"\b(?:v|ver|version|rev|revision)\s?\.?\s?(\d{1,3})\b", Opts);
    private static readonly Regex Extension = new(@"\.(pdf|docx?|odt|rtf|txt|png|jpe?g|heic|tiff?)$", Opts);

    /// <summary>
    /// The name shared by every version of a document: the title up to its first version marker ("REVISED COPY",
    /// "ORIGINAL", "v6", "(final)", "- Copy", "(2)"), lower-cased. A title without a marker is its own family.
    /// </summary>
    public static string Family(string title)
    {
        var t = Extension.Replace((title ?? "").Trim(), "");
        int cut = t.Length;
        foreach (Match sep in Separator.Matches(t))
        {
            var rest = t[(sep.Index + sep.Length)..];
            if (MarkerStart.IsMatch(rest)) { cut = sep.Index; break; }
        }
        var v = VersionNumber.Match(t);
        if (v.Success && v.Index > 0 && v.Index < cut) cut = v.Index;
        if (cut == t.Length) return Normalise(t);
        var family = Normalise(t[..cut]);
        // Too little left to be sure it's the same document ("A - copy"): keep the whole title
        return family.Length >= 4 && family.Any(char.IsLetter) ? family : Normalise(t);
    }

    private static string Normalise(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant().Replace('_', ' '), @"\s+", " "), @"^[\s\-–—.,:;]+|[\s\-–—.,:;]+$", "");

    /// <summary>
    /// How late a version the title says it is: (stage, number). Draft −1, original or unmarked 0, revised/amended/updated 1;
    /// the number is the highest "v6" / "version 6" / "rev 6" in the title.
    /// </summary>
    public static (int Stage, int Number) VersionMarker(string title)
    {
        var t = title ?? "";
        int stage = Regex.IsMatch(t, @"\b(revised|revision|amended|updated|corrected)\b", Opts) ? 1
                  : Regex.IsMatch(t, @"\bdraft\b", Opts) ? -1 : 0;
        int number = 0;
        foreach (Match m in VersionNumber.Matches(t))
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) number = Math.Max(number, n);
        return (stage, number);
    }

    /// <summary>When the file was last edited, as far as CaseKeeper knows (never the date written in the document).</summary>
    public static DateTime LastEdited(CaseDocument d)
    {
        var best = d.ImportedAt;
        if (d.SourceModifiedAt is { } m && m > DateTime.MinValue) best = m;
        if (d.SavedAt is { } s && s > DateTime.MinValue && (d.SourceModifiedAt == null || s > best)) best = s;
        return best;
    }

    // ------------------------------------------------------------------ the whole list
    /// <summary>
    /// Sets each document's <see cref="CaseDocument.ImportanceRank"/>, version family and whether it's an older version.
    /// <paramref name="contentGroups"/> are the version groups found by comparing the documents' words (Import review's
    /// "Duplicates &amp; versions"), newest first; groups the person said aren't related are left out by the caller.
    /// </summary>
    public static void Organise(IReadOnlyList<CaseDocument> docs, IReadOnlyDictionary<long, Category> categories,
        IEnumerable<IReadOnlyList<long>> contentGroups = null)
    {
        // Union-find over title families and word-for-word groups
        var parent = docs.ToDictionary(d => d.Id, d => d.Id);
        long Find(long x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(long a, long b) { if (!parent.ContainsKey(a) || !parent.ContainsKey(b)) return; a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

        foreach (var g in docs.GroupBy(d => Family(d.Title)).Where(g => g.Key.Length > 0))
        {
            var first = g.First().Id;
            foreach (var d in g.Skip(1)) Union(first, d.Id);
        }
        // Content groups: the same words in several files (Import review's "Duplicates & versions")
        if (contentGroups != null)
            foreach (var g in contentGroups)
                for (int i = 1; i < g.Count; i++) Union(g[0], g[i]);

        foreach (var d in docs)
        {
            categories.TryGetValue(d.CategoryId, out var cat);
            var imp = Importance(d, cat);
            d.ImportanceRank = (int)imp;
            d.ImportanceText = Label(imp);
            d.LatestEdit = LastEdited(d);
            d.IsOlderVersion = false;
            d.OlderVersionCount = 0;
            d.VersionFamily = "";
            d.LatestVersionId = d.Id;
            d.VersionNote = "";
            d.VersionIndex = 0;
        }

        foreach (var family in docs.GroupBy(d => Find(d.Id)).Where(g => g.Count() > 1))
        {
            var ordered = family
                .OrderByDescending(d => Tags(d).Contains(LatestTag))         // the person's own choice first
                .ThenBy(d => Tags(d).Contains(OlderTag))
                .ThenByDescending(d => VersionMarker(d.Title).Stage)
                .ThenByDescending(d => VersionMarker(d.Title).Number)
                .ThenByDescending(d => d.LatestEdit)
                .ThenByDescending(d => d.PageCount)
                .ThenByDescending(d => d.Id)
                .ToList();
            var latest = ordered[0];
            var key = "v" + latest.Id.ToString(CultureInfo.InvariantCulture);
            // Everything in the family sits with the most important of its members (a starred draft lifts the set)
            int rank = ordered.Min(d => d.ImportanceRank);
            var rankText = ordered.First(d => d.ImportanceRank == rank).ImportanceText;
            foreach (var d in ordered)
            {
                d.VersionFamily = key;
                d.LatestVersionId = latest.Id;
                d.ImportanceRank = rank;
                d.ImportanceText = rankText;
            }
            latest.OlderVersionCount = ordered.Count - 1;
            latest.VersionNote = ordered.Count == 2 ? "Latest · 1 older version" : $"Latest · {ordered.Count - 1} older versions";
            for (int i = 1; i < ordered.Count; i++)
            {
                ordered[i].IsOlderVersion = true;
                ordered[i].VersionIndex = i;
                ordered[i].VersionNote = $"Older version ({i + 1} of {ordered.Count})";
            }
        }
    }

    private static HashSet<string> Tags(CaseDocument d) =>
        (d.Tags ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list as shown: latest versions only (unless asked otherwise), in the chosen order.</summary>
    public static List<CaseDocument> Arrange(IEnumerable<CaseDocument> docs, bool latestOnly, string arrange)
    {
        var all = docs.ToList();
        // An older version is folded away only when its latest is in the same view – otherwise it's the newest one here
        var here = all.Select(d => d.Id).ToHashSet();
        var list = latestOnly ? all.Where(d => !d.IsOlderVersion || !here.Contains(d.LatestVersionId)) : all;
        return arrange switch
        {
            ArrangeImported => list.OrderByDescending(d => d.ImportedAt).ThenByDescending(d => d.Id).ToList(),
            ArrangeNewest => list.OrderByDescending(d => d.DocumentDate ?? d.LatestEdit).ThenBy(d => d.IsOlderVersion).ThenByDescending(d => d.Id).ToList(),
            // Most important first; within each kind the newest; versions of one document stay together, latest on top
            _ => list.GroupBy(d => string.IsNullOrEmpty(d.VersionFamily) ? "d" + d.Id.ToString(CultureInfo.InvariantCulture) : d.VersionFamily)
                     .Select(g => g.OrderBy(d => d.VersionIndex).ToList())
                     .OrderBy(g => g[0].ImportanceRank)
                     .ThenByDescending(g => g.Max(d => d.DocumentDate ?? d.LatestEdit))
                     .ThenBy(g => g[0].Title, StringComparer.OrdinalIgnoreCase)
                     .SelectMany(g => g).ToList()
        };
    }
}
