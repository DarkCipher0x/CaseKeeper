using System.Globalization;
using System.Text;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>
/// Library check: runs every document already in the library (or just some of them) through the same reading,
/// organising and cross-checking as Bulk Case Import, with or without AI, and can finish with a case overview.
/// This class holds what it can fill in ("sections"), the counts the check, Import review and the help assistant
/// show (numbers only: never titles or content). The case overview is in <see cref="CaseKeeper.Services.CaseOverview"/>.
/// </summary>
public static class LibraryCheck
{
    public const string SectionsSetting = "librarycheck.sections", LastRunSetting = "librarycheck.last", OverviewSetting = "librarycheck.overview";

    public sealed record Section(string Key, string Name, string Explain, string[] Kinds);

    /// <summary>What a check can fill in. Document details (the summary) are always kept: the overview is built from them.</summary>
    public static readonly Section[] Sections =
    {
        new("categories", "Categories", "File each document under the right category", new[] { FindingKinds.Category }),
        new("people", "Parties & children", "Who's who, with representatives and contact details for professionals", new[] { FindingKinds.Party, FindingKinds.Child }),
        new("court", "Hearings, orders & directions", "Hearings with dates and courts, orders made, and what they direct", new[] { FindingKinds.Hearing, FindingKinds.Order, FindingKinds.Direction }),
        new("timeline", "Timeline & chronology", "Dated events, for the timeline and a chronology", new[] { FindingKinds.TimelineEvent, FindingKinds.ChronologyEntry }),
        new("actions", "Actions & deadlines", "Things someone must do, and by when", new[] { FindingKinds.Task, FindingKinds.Deadline }),
        new("contact", "Contact log", "Contact sessions and handovers", new[] { FindingKinds.Contact }),
        new("complaints", "Complaints", "Complaints and replies: organisation, reference, dates, reply deadline", new[] { FindingKinds.Complaint }),
        new("scott", "Scott schedule", "Allegations, who makes them and when, for a fact-finding hearing", new[] { FindingKinds.ScottItem }),
        new("safeguarding", "Safeguarding", "Safeguarding concerns raised", new[] { FindingKinds.Safeguarding }),
        new("gaps", "Missing documents & issues", "Documents referred to but not in the library, and points to check", new[] { FindingKinds.MissingDocument, FindingKinds.Issue }),
    };

    /// <summary>The sections chosen last time (all of them the first time).</summary>
    public static List<string> SavedSections(Database db)
    {
        var raw = db.GetSetting(SectionsSetting, "") ?? "";
        var keys = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => Sections.Any(s => s.Key == k)).ToList();
        return keys.Count == 0 ? Sections.Select(s => s.Key).ToList() : keys;
    }

    /// <summary>The finding kinds a set of sections allows. Null means everything (a normal bulk import).</summary>
    public static HashSet<string> KindsFor(IEnumerable<string> sections)
    {
        if (sections == null) return null;
        var kinds = new HashSet<string>(StringComparer.Ordinal) { FindingKinds.Summary, FindingKinds.Conflict };
        foreach (var s in Sections.Where(s => sections.Contains(s.Key))) kinds.UnionWith(s.Kinds);
        return kinds;
    }

    /// <summary>True when only categories were asked for, so the AI can use its quick reading.</summary>
    public static bool CategoriesOnly(IEnumerable<string> sections) => sections != null && sections.All(s => s == "categories") && sections.Any();

    // ------------------------------------------------------------------ counts (numbers only)
    public sealed class Stats
    {
        public int Documents, Uncategorised, TextNotRead, NoText, ExactCopyGroups, ExactCopies, CheckedBefore;
        public Dictionary<string, int> OpenSuggestions { get; } = new();
        public int Parties, Children, Hearings, UpcomingHearings, Orders, OpenActions, OverdueActions, Complaints, ComplaintsNoReplyDate, ScottItems, TimelineEvents;
        public DateTime? LastCheck, NextHearing, NextDeadline;
        public int OpenSuggestionTotal => OpenSuggestions.Values.Sum();
    }

    public static Stats Count(Database db)
    {
        var s = new Stats();
        var docs = db.GetDocuments();
        var unknown = CategoryMatcher.Unknown(db.GetCategories())?.Id;
        s.Documents = docs.Count;
        s.Uncategorised = docs.Count(d => d.CategoryId == unknown);
        s.TextNotRead = docs.Count(d => d.OcrStatus is OcrStatus.Pending or OcrStatus.Running);
        s.NoText = docs.Count(d => d.OcrStatus == OcrStatus.Failed);
        var copies = docs.Where(d => !string.IsNullOrEmpty(d.FileHash)).GroupBy(d => d.FileHash).Where(g => g.Count() > 1).ToList();
        s.ExactCopyGroups = copies.Count;
        s.ExactCopies = copies.Sum(g => g.Count() - 1);
        var analysed = db.Query<ImportFile>("DocumentId IS NOT NULL AND AnalysisStatus=$a", ("$a", (int)AnalysisState.Done)).Select(f => f.DocumentId!.Value).ToHashSet();
        s.CheckedBefore = docs.Count(d => analysed.Contains(d.Id));
        foreach (var g in db.Query<AiFinding>("Status IN (0,3)").GroupBy(f => f.Kind)) s.OpenSuggestions[g.Key] = g.Count();
        var parties = db.GetParties();
        s.Children = parties.Count(p => p.Role.StartsWith("Child", StringComparison.Ordinal));
        s.Parties = parties.Count - s.Children;
        var hearings = db.All<Hearing>();
        s.Hearings = hearings.Count;
        var upcoming = hearings.Where(h => h.HearingDate?.Date >= DateTime.Today && h.Status is not ("Vacated" or "Adjourned")).OrderBy(h => h.HearingDate).ToList();
        s.UpcomingHearings = upcoming.Count;
        s.NextHearing = upcoming.FirstOrDefault()?.HearingDate;
        s.Orders = db.All<CourtOrder>().Count;
        var actions = db.All<CaseAction>().Where(a => a.Status is not ("Done" or "Complete" or "Completed" or "Cancelled")).ToList();
        s.OpenActions = actions.Count;
        s.OverdueActions = actions.Count(a => a.DueDate?.Date < DateTime.Today);
        s.NextDeadline = actions.Where(a => a.DueDate?.Date >= DateTime.Today).OrderBy(a => a.DueDate).FirstOrDefault()?.DueDate;
        var complaints = db.GetComplaints();
        s.Complaints = complaints.Count;
        s.ComplaintsNoReplyDate = complaints.Count(c => c.ResponseDue == null && c.Status is "Sent" or "Acknowledged" or "Awaiting Response");
        s.ScottItems = db.All<ScottItem>().Count;
        s.TimelineEvents = db.GetEvents().Count;
        s.LastCheck = DateTime.TryParse(db.GetSetting(LastRunSetting, ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) ? last : null;
        return s;
    }

    /// <summary>
    /// The library's state for the help assistant: counts only, never a title, name or any document content,
    /// so it's safe to send with a question even to a cloud AI.
    /// </summary>
    public static string DescribeForAssistant(Stats s)
    {
        var sb = new StringBuilder("LIBRARY STATE (counts only):\n");
        sb.AppendLine($"- Documents: {s.Documents}; not yet categorised: {s.Uncategorised}; text not read yet: {s.TextNotRead}; no readable text: {s.NoText}");
        sb.AppendLine($"- Exact copies: {s.ExactCopies} in {s.ExactCopyGroups} group(s); documents already organised by a check or import: {s.CheckedBefore}");
        sb.AppendLine($"- Suggestions waiting in Import review: {s.OpenSuggestionTotal}" +
                      (s.OpenSuggestions.Count == 0 ? "" : " (" + string.Join(", ", s.OpenSuggestions.OrderByDescending(k => k.Value).Select(k => $"{FindingKinds.Label(k.Key)} {k.Value}")) + ")"));
        sb.AppendLine($"- Parties: {s.Parties}; children: {s.Children}; hearings: {s.Hearings} ({s.UpcomingHearings} coming up); orders: {s.Orders}");
        sb.AppendLine($"- Open actions: {s.OpenActions} ({s.OverdueActions} overdue); complaints: {s.Complaints} ({s.ComplaintsNoReplyDate} without a reply date); Scott schedule items: {s.ScottItems}; timeline entries: {s.TimelineEvents}");
        sb.AppendLine(s.LastCheck is { } d ? $"- Last library check: {d:d MMM yyyy}" : "- A library check has never been run.");
        return sb.ToString();
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
