using System.Globalization;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>What one automatic pass did, for the Library check card and the notification.</summary>
public sealed class AutoAddReport
{
    public int Added { get; set; }
    public int AlreadyInCase { get; set; }
    public int Waiting { get; set; }
    public Dictionary<string, int> ByKind { get; } = new();

    public string Describe() =>
        Added == 0 ? "Nothing was added automatically."
            : $"Added {Added} to your case ({string.Join(", ", ByKind.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {FindingKinds.Label(k.Key).ToLowerInvariant()}"))}), marked to check." +
              (AlreadyInCase > 0 ? $" {AlreadyInCase} were already in your case." : "") + (Waiting > 0 ? $" {Waiting} need you to look at them." : "");
}

/// <summary>
/// Track auto-add: after a bulk import or library check, adds what the AI found to the case – Case &amp; hearings,
/// Timeline, Directions &amp; tasks, Contact log, Complaints, Parties, Scott schedule, categories and document details –
/// when it is safe to, so the Track sections fill themselves.
///
/// Safe means: the suggestion is new (not deferred, rejected or undone before), medium or high confidence, has no
/// warnings and no conflict with another document, has what the record needs (a hearing has a date, for example), and
/// nothing like it is already in the case. Updates to a party or complaint already in the case only fill empty fields.
/// Document details only fill empty fields, and a category is only set while the document is Uncategorised.
///
/// Every record goes through <see cref="FindingApprover.Approve"/> – the same database calls, validation, audit and undo
/// as approving by hand – and is marked "From AI · check it against the document". Everything else waits in Import
/// review. On by default; switched off with the setting <see cref="Setting"/>.
/// </summary>
public static class TrackAutoAdd
{
    public const string Setting = "track.autoAdd";

    public static bool Enabled(Database db) => db.GetSetting(Setting, "1") != "0";

    /// <summary>Parties first (so later records use their names), categories and document details last.</summary>
    private static readonly string[] Order =
    {
        FindingKinds.Party, FindingKinds.Child, FindingKinds.Hearing, FindingKinds.Order, FindingKinds.Complaint,
        FindingKinds.Direction, FindingKinds.Deadline, FindingKinds.Task, FindingKinds.MissingDocument,
        FindingKinds.TimelineEvent, FindingKinds.ChronologyEntry, FindingKinds.Safeguarding, FindingKinds.Contact,
        FindingKinds.ScottItem, FindingKinds.Category, FindingKinds.Summary
    };

    /// <summary>Runs over the open suggestions for the documents in this job. Returns what it did.</summary>
    public static AutoAddReport Run(Database db, ImportJob job, IReadOnlyCollection<string> kinds = null)
    {
        var report = new AutoAddReport();
        var docIds = db.Query<ImportFile>("JobId=$j AND DocumentId IS NOT NULL", ("$j", job.Id))
            .Select(f => f.DocumentId!.Value).ToHashSet();
        var open = db.Query<AiFinding>("Status=$s", ("$s", (int)FindingStatus.Proposed))
            .Where(f => docIds.Contains(f.DocumentId) || f.JobId == job.Id).ToList();
        if (open.Count == 0) return report;
        var categories = db.GetCategories();
        var unknown = CategoryMatcher.Unknown(categories)?.Id;

        // Complaints: new ones before replies, so a reply finds the complaint it answers
        int Rank(AiFinding f) => Array.IndexOf(Order, f.Kind) is var i and >= 0 ? i : Order.Length;
        // The same thing found in several documents: only the first (most confident) is added, the others are copies
        var sameDone = new HashSet<string>();
        foreach (var f in open.OrderBy(Rank).ThenBy(f => f.Kind == FindingKinds.Complaint && FindingApprover.P(f, "isResponse") == "true" ? 1 : 0)
                     .ThenBy(f => f.DateValue ?? DateTime.MaxValue).ThenByDescending(f => f.Confidence).ThenBy(f => f.Id))
        {
            if (Rank(f) >= Order.Length) continue;                       // notes for review (issues, conflicts) are never "added"
            if (kinds != null && !kinds.Contains(f.Kind)) continue;       // a library check only fills the sections chosen
            if (f.Confidence < BulkThresholds.Medium || Doubtful(f)) { report.Waiting++; continue; }
            if (IsSameAs(f) && sameDone.Contains(f.ConflictGroup)) { report.AlreadyInCase++; continue; }
            object draft;
            try { draft = Prepare(db, f, unknown); }
            catch (Exception ex) { AppLog.Warn("AutoAdd", $"Couldn't prepare finding #{f.Id}", ex); report.Waiting++; continue; }
            if (draft == null) continue;
            if (FindingApprover.Blocker(f, draft) != null) { report.Waiting++; continue; }
            if (draft is not CaseDocument && FindingApprover.FindEquivalent(db, f, draft) != null) { report.AlreadyInCase++; continue; }
            if (draft is Party { Id: > 0 } or Complaint { Id: > 0 } && FindingApprover.DescribeUpdate(db, draft) is { } upd && upd.Contains("nothing new to add"))
            { report.AlreadyInCase++; continue; }
            try
            {
                f.AppliedByJobId = job.Id;
                var outcome = FindingApprover.Approve(db, f, draft, edited: false, automatic: true,
                    note: $"Track auto-add, run #{job.Id} (confidence at least {BulkThresholds.Medium.ToString("0.00", CultureInfo.InvariantCulture)}, no warnings or conflicts).");
                if (!outcome.Ok) { f.AppliedByJobId = null; continue; }
                report.Added++;
                report.ByKind[f.Kind] = report.ByKind.GetValueOrDefault(f.Kind) + 1;
                if (IsSameAs(f)) sameDone.Add(f.ConflictGroup);
            }
            catch (Exception ex)
            {
                AppLog.Warn("AutoAdd", $"Couldn't add finding #{f.Id} automatically", ex);
                f.AppliedByJobId = null;
                db.SaveQuiet(f);
                report.Waiting++;
            }
        }
        if (report.Added > 0)
        {
            db.Audit("ai.autoadd", "bulk import", job.Id, $"Added {report.Added} AI suggestion(s) to the case automatically", report.Describe());
            AppLog.Info("AutoAdd", $"Job #{job.Id}: {report.Added} added, {report.AlreadyInCase} already in the case, {report.Waiting} waiting");
        }
        return report;
    }

    /// <summary>Found in several documents (CaseKeeper's cross-check groups these as "dup-…").</summary>
    private static bool IsSameAs(AiFinding f) => f.ConflictGroup.StartsWith("dup-", StringComparison.Ordinal);

    /// <summary>
    /// True when something about the suggestion needs a person: a warning that isn't just <see cref="Informational"/>, a conflict with
    /// another document, or a cross-check note. Two notes are not doubts: "probably the same as …" (the same thing in
    /// several documents – one is added, the rest are copies) and "coming up on …" (a reminder that it's soon).
    /// </summary>
    public static bool Doubtful(AiFinding f)
    {
        foreach (var w in (f.Warnings ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Informational(w)) return true;
        if (f.ConflictGroup.Length > 0 && !IsSameAs(f)) return true;
        foreach (var line in (f.CheckNotes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!line.StartsWith("Probably the same as:", StringComparison.Ordinal) && !line.StartsWith("Coming up on ", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Warnings that say what CaseKeeper made safe, not that the finding itself is in doubt: something the document
    /// doesn't say was left out (a time, a reply date, a child's contact details), the document isn't filed under a
    /// category yet, a past hearing, the AI's wording of a contact outcome, or the finding was moved to the kind its
    /// wording fits. The record is still marked "From AI · check". Anything else – quoted words not found, a date read
    /// differently from how it's written, a date that has to be worked out, "here for you to check" – waits for a person.
    /// </summary>
    public static bool Informational(string warning) =>
        warning.StartsWith("The AI couldn't match this document to the case's papers", StringComparison.Ordinal) ||
        warning.EndsWith("so it was left out.", StringComparison.Ordinal) ||
        warning.StartsWith("Removed a ", StringComparison.Ordinal) ||
        warning == "Contact details for a child are never added." ||
        warning.Contains("is the AI's reading of what happened at contact", StringComparison.Ordinal) ||
        warning.StartsWith("This hearing was listed for a date that has passed.", StringComparison.Ordinal) ||
        warning == "Shown as a direction: the wording is something someone must do." ||
        warning.EndsWith("so it's shown as a timeline event.", StringComparison.Ordinal);

    /// <summary>
    /// The record to add, or null to leave the suggestion alone. Document details and categories never overwrite what's
    /// there: only empty details are filled, and only an Uncategorised document is filed.
    /// </summary>
    private static object Prepare(Database db, AiFinding f, long? unknown)
    {
        switch (f.Kind)
        {
            case FindingKinds.Category:
            {
                var cat = FindingApprover.PLong(f, "categoryId");
                var doc = db.GetDocument(f.DocumentId);
                if (cat == null || cat == unknown || doc == null || doc.CategoryId != unknown) return null;
                doc.CategoryId = cat.Value;
                return doc;
            }
            case FindingKinds.Summary:
            {
                var doc = db.GetDocument(f.DocumentId);
                if (doc == null) return null;
                bool changed = false;
                if (string.IsNullOrWhiteSpace(doc.Description) && !string.IsNullOrWhiteSpace(f.Detail)) { doc.Description = f.Detail; changed = true; }
                if (doc.DocumentDate == null && f.DateValue is { } d && f.DatePrecision == "exact") { doc.DocumentDate = d; changed = true; }
                if (string.IsNullOrWhiteSpace(doc.Author) && FindingApprover.P(f, "author") is { } a) { doc.Author = a; changed = true; }
                return changed ? doc : null;
            }
            case FindingKinds.Deadline when FindingApprover.NeedsCalculation(f):
                return null;                                              // worked-out dates are always checked by a person
            default:
                return FindingApprover.Draft(db, f, automatic: true);
        }
    }

    /// <summary>Everything still marked "to check" (added automatically and not yet checked).</summary>
    public static List<AiFinding> ToCheck(Database db) => db.Query<AiFinding>("Status=$s", ("$s", (int)FindingStatus.AutoApplied));

    /// <summary>Undoes everything one run added, newest first. Returns how many were undone.</summary>
    public static int UndoRun(Database db, long jobId)
    {
        int n = 0;
        foreach (var f in db.Query<AiFinding>("Status=$s AND AppliedByJobId=$j", ("$s", (int)FindingStatus.AutoApplied), ("$j", jobId)).OrderByDescending(f => f.DecidedAt).ThenByDescending(f => f.Id))
            try { if (FindingApprover.Undo(db, f).Ok) n++; }
            catch (Exception ex) { AppLog.Warn("AutoAdd", $"Couldn't undo finding #{f.Id}", ex); }
        if (n > 0) db.Audit("ai.autoadd.undo", "bulk import", jobId, $"Undid {n} thing(s) the AI added automatically", $"Run #{jobId}");
        return n;
    }

    /// <summary>The most recent run that added something automatically, or null.</summary>
    public static long? LastRun(Database db) =>
        db.Query<AiFinding>("Status=$s AND AppliedByJobId IS NOT NULL", ("$s", (int)FindingStatus.AutoApplied))
            .OrderByDescending(f => f.DecidedAt).FirstOrDefault()?.AppliedByJobId;
}
