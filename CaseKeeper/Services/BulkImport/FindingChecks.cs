using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>
/// CaseKeeper's own cross-checks across every document, run after the AI has read them. They are plain
/// rules, not AI: the same suggestion coming from several documents, a hearing given different dates,
/// a hearing later adjourned or vacated, different case numbers, names spelt differently, things already
/// in the case, directions that a later order may have replaced, dates coming up soon, and long gaps in
/// the chronology. Nothing is resolved automatically: each one is flagged, side by side with its
/// sources, for the person to decide.
/// </summary>
public static class FindingChecks
{
    public const string Source = "CaseKeeper checks";

    public sealed class Report
    {
        public int Duplicates, Conflicts, AlreadyRecorded, Urgent, Gaps, Superseded, Trust;
        public string Describe() =>
            $"{Duplicates} possible duplicate(s), {Conflicts} conflict(s), {AlreadyRecorded} already in the case, " +
            $"{Urgent} coming up within two weeks, {Superseded} possibly replaced, {Gaps} gap(s) in the chronology, {Trust} document(s) to treat with care.";
    }

    private static readonly string[] MacroTypes = { ".docm", ".dotm", ".xlsm", ".xltm", ".xlsb", ".pptm", ".potm", ".ppsm" };

    /// <summary>
    /// Plain checks on a document's file and text that the person should know about before relying on it, or before the
    /// AI's reading of it: program code (macros) inside, text that looks badly misread from a scan, and a date written in
    /// the document that is later than the file was last changed (a typo, or a document edited after the event).
    /// </summary>
    public static List<string> TrustSignals(CaseDocument d, IReadOnlyDictionary<int, string> pages, DateTime? fileModified)
    {
        var list = new List<string>();
        var ext = (d.FileExtension ?? "").ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;
        if (MacroTypes.Contains(ext))
            list.Add("It's an Office file that can contain macros (program code). CaseKeeper never runs them, but don't enable editing or macros if you open it in Office unless you trust where it came from.");
        var text = string.Join("\n", pages.OrderBy(p => p.Key).Select(p => p.Value));
        if (text.Length > 400 && Masker.Assess(text).LowQualityText)
            list.Add("The text looks badly misread (a poor scan or handwriting), so quotes, dates and names the AI took from it may be wrong. Check them against the page.");
        if (d.DocumentDate is { } dated && fileModified is { } changed && dated.Date > changed.Date.AddDays(1))
            list.Add($"It's dated {dated:d MMMM yyyy}, but the file was last changed on {changed:d MMMM yyyy}. The date may be a mistake, or the file may not be the final version.");
        return list;
    }

    private static bool Live(AiFinding f) => f.StatusValue is FindingStatus.Proposed or FindingStatus.Deferred or FindingStatus.Approved or FindingStatus.AutoApplied;

    public static Report Run(Database db, Func<DateTime> today = null)
    {
        var now = (today ?? (() => DateTime.Today))();
        var report = new Report();

        // Conflicts this check raised before and nobody has decided on are rebuilt from scratch
        db.DeleteQuiet<AiFinding>("Kind=$k AND Model=$m AND Status IN (0,3)", ("$k", FindingKinds.Conflict), ("$m", Source));
        var all = db.Query<AiFinding>("Status IN (0,1,3,4)");
        var decidedConflicts = db.Query<AiFinding>("Kind=$k AND Status NOT IN (0,3)", ("$k", FindingKinds.Conflict))
            .Select(f => f.Fingerprint).ToHashSet();
        foreach (var f in all.Where(f => f.IsOpen)) { f.CheckNotes = ""; f.ConflictGroup = ""; f.MatchedRecordType = ""; f.MatchedRecordId = null; }

        var docs = db.GetDocuments().Concat(db.GetDeletedDocuments()).ToDictionary(d => d.Id);
        string DocName(long id) => docs.TryGetValue(id, out var d) ? $"“{AnalysisSchema.Clip(d.Title, 60)}”" : "a document";
        string Where(AiFinding f) => DocName(f.DocumentId) + (f.PageFrom is { } p ? $" p.{p}" : "");
        void Note(AiFinding f, string text)
        {
            if (!f.IsOpen) return;
            f.CheckNotes = DocumentAnalyzer.JoinWarnings(f.CheckNotes, text);
        }
        var newConflicts = new List<AiFinding>();
        void Conflict(long docId, string title, string detail, string group, IEnumerable<AiFinding> related, bool isGap = false)
        {
            var fp = AnalysisSchema.Fingerprint(FindingKinds.Conflict, null, title + " " + group);
            if (decidedConflicts.Contains(fp) || newConflicts.Any(c => c.Fingerprint == fp)) return;
            newConflicts.Add(new AiFinding
            {
                DocumentId = docId, Kind = FindingKinds.Conflict, Title = AnalysisSchema.Clip(title, 140), Detail = AnalysisSchema.Clip(detail, 900),
                Confidence = 1, Reason = "Found by comparing documents", Model = Source, PromptVersion = "rules",
                SchemaVersion = AnalysisSchema.Version, AnalysisVersion = AnalysisSchema.AnalysisVersion, ConflictGroup = group,
                Fingerprint = fp, JobId = related.FirstOrDefault()?.JobId
            });
            if (isGap) report.Gaps++;
            else report.Conflicts++;
        }

        var live = all.Where(Live).Where(f => f.Kind != FindingKinds.Conflict).ToList();

        // ---- 1. the same thing suggested from several documents (same kind, same day, similar words)
        foreach (var group in live.Where(f => f.DateValue != null && f.Kind is not (FindingKinds.Summary or FindingKinds.Category))
                     .GroupBy(f => (AnalysisSchema.KindFamily(f.Kind), f.DateValue!.Value.Date)))
        {
            var items = group.ToList();
            var clusters = new List<List<AiFinding>>();
            foreach (var f in items)
            {
                var home = clusters.FirstOrDefault(c => c.Any(o => f.Kind == FindingKinds.Hearing && o.Kind == FindingKinds.Hearing
                                                                   || AnalysisSchema.Similarity(o.Title, f.Title) >= 0.5));
                if (home == null) clusters.Add(new List<AiFinding> { f });
                else home.Add(f);
            }
            foreach (var cluster in clusters.Where(c => c.Count > 1 && c.Any(x => x.IsOpen)))
            {
                var key = "dup-" + AnalysisSchema.Hash(string.Join(",", cluster.Select(c => c.Id).OrderBy(i => i)))[..10];
                bool oneApproved = cluster.Any(c => !c.IsOpen);
                foreach (var f in cluster.Where(c => c.IsOpen))
                {
                    f.ConflictGroup = key;
                    var others = cluster.Where(o => o != f).Select(Where).Distinct().Take(3);
                    Note(f, oneApproved
                        ? $"Already approved from another document ({string.Join(", ", cluster.Where(o => !o.IsOpen).Select(Where).Take(2))}). Reject this one unless it's different."
                        : $"Probably the same as: {string.Join("; ", others)}. Approve one and reject the others.");
                    report.Duplicates++;
                }
            }
        }

        // ---- 2. hearings: different dates for the same hearing, and hearings later moved or vacated
        var hearings = live.Where(f => f.Kind == FindingKinds.Hearing && f.DateValue != null).ToList();
        foreach (var moved in hearings.Where(h => h.DateRole == "hearingAdjournedFrom" || FindingApprover.P(h, "previousDate") != null))
        {
            var from = moved.DateRole == "hearingAdjournedFrom" ? moved.DateValue : UkDates.ParseIso(FindingApprover.P(moved, "previousDate"));
            foreach (var h in hearings.Where(x => x != moved && x.DateValue == from && x.DateRole is "hearingListed"))
                Note(h, $"A later document says this hearing was moved or adjourned ({Where(moved)}). Check before approving.");
        }
        foreach (var vacated in hearings.Where(h => h.DateRole == "hearingVacated" || FindingApprover.P(h, "hearingStatus") is "vacated" or "adjourned"))
            foreach (var h in hearings.Where(x => x != vacated && x.DateValue == vacated.DateValue && x.DateRole == "hearingListed"))
                Note(h, $"{Where(vacated)} says this hearing was {(FindingApprover.P(vacated, "hearingStatus") == "adjourned" ? "adjourned" : "vacated")}.");

        var listed = hearings.Where(h => h.DateRole is "hearingListed" or "hearingAdjournedTo" && FindingApprover.P(h, "hearingStatus") is null or "listed" or "unknown").ToList();
        // The type comes from the hearing details, or failing that from its description ("Final hearing listed")
        string TypeOf(AiFinding h) => FindingApprover.HearingTypeFor(FindingApprover.P(h, "hearingType") ?? h.Title);
        foreach (var byType in listed.GroupBy(h => TypeOf(h).ToLowerInvariant()).Where(g => g.Key.Length > 0 && Hearing.Types.Contains(TypeOf(g.First()))))
        {
            var dates = byType.GroupBy(h => h.DateValue!.Value.Date).OrderBy(g => g.Key).ToList();
            for (int i = 1; i < dates.Count; i++)
            {
                var a = dates[i - 1];
                var b = dates[i];
                if ((b.Key - a.Key).TotalDays > 120) continue;
                // A later notice re-listing the hearing explains the difference; anything else is a conflict
                if (b.Any(x => x.DateRole == "hearingAdjournedTo" || FindingApprover.P(x, "previousDate") != null)) continue;
                var type = TypeOf(a.First());
                var group = "hearing-" + AnalysisSchema.Hash($"{type}|{a.Key:yyyyMMdd}|{b.Key:yyyyMMdd}")[..10];
                Conflict(b.First().DocumentId, $"Different dates given for the {type}: {a.Key:d MMM yyyy} and {b.Key:d MMM yyyy}",
                    $"{string.Join("; ", a.Select(Where).Distinct())} says {a.Key:d MMMM yyyy}.\n{string.Join("; ", b.Select(Where).Distinct())} says {b.Key:d MMMM yyyy}.\n" +
                    "Check which is right. A later notice may have moved the hearing; if so, approve the later date and reject the other.",
                    group, a.Concat(b));
                foreach (var h in a.Concat(b))
                {
                    h.ConflictGroup = group;
                    Note(h, "Another document gives a different date for this hearing. See the conflict in Conflicts.");
                }
            }
        }

        // ---- 3. case numbers
        var numbers = live.Where(f => f.Kind == FindingKinds.Summary && FindingApprover.P(f, "caseNumber") != null)
            .Select(f => (Finding: f, Number: NormaliseCaseNumber(FindingApprover.P(f, "caseNumber")))).Where(x => x.Number.Length >= 4).ToList();
        var known = NormaliseCaseNumber(db.GetSetting("case.number", ""));
        var distinct = numbers.Select(n => n.Number).Append(known).Where(n => n.Length >= 4).Distinct().ToList();
        if (distinct.Count > 1)
        {
            var counts = numbers.GroupBy(n => n.Number).ToDictionary(g => g.Key, g => g.Count());
            var main = known.Length >= 4 ? known : counts.OrderByDescending(kv => kv.Value).First().Key;
            Conflict(numbers.First(n => n.Number != main).Finding.DocumentId, "Different case numbers appear in the documents",
                string.Join("\n", distinct.Select(n => $"{n}: {(counts.TryGetValue(n, out var c) ? $"{c} document(s)" : "")}{(n == known ? " (your case details)" : "")}")) +
                "\nDocuments with a different number may belong to a different case, or the number may have been misread.",
                "caseno-" + AnalysisSchema.Hash(string.Join("|", distinct.OrderBy(x => x)))[..10], numbers.Select(n => n.Finding));
            foreach (var (f, n) in numbers.Where(x => x.Number != main))
                Note(f, $"Case number {n} differs from {main}. This document may belong to a different case.");
        }

        // ---- 4. names spelt differently
        var people = live.Where(f => f.Kind is FindingKinds.Party or FindingKinds.Child && FindingApprover.P(f, "name") != null)
            .Select(f => (Finding: f, Name: FindingApprover.P(f, "name")!.Trim())).ToList();
        var existingNames = db.GetParties().Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name.Trim()).ToList();
        foreach (var (f, name) in people)
        {
            var near = people.Select(p => p.Name).Concat(existingNames)
                .Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase) && n.Length >= 4 && Distance(n.ToLowerInvariant(), name.ToLowerInvariant()) <= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (near.Count == 0) continue;
            Note(f, $"Spelt differently elsewhere: {string.Join(", ", near.Select(n => "“" + n + "”"))}. Check which spelling is right.");
            var pair = new[] { name, near[0] }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            Conflict(f.DocumentId, $"Name spelt differently: “{pair[0]}” and “{pair[1]}”",
                $"{Where(f)} has “{name}”; elsewhere it's “{near[0]}”. They may be the same person.",
                "name-" + AnalysisSchema.Hash(string.Join("|", pair).ToLowerInvariant())[..10], new[] { f });
        }

        // ---- 5. already in the case
        var existing = new Existing(db);
        foreach (var f in live.Where(f => f.IsOpen))
        {
            var match = existing.Match(f);
            if (match == null) continue;
            f.MatchedRecordType = match.Value.Type;
            f.MatchedRecordId = match.Value.Id;
            Note(f, match.Value.Text);
            report.AlreadyRecorded++;
        }

        // ---- 6. directions a later order may have replaced
        DateTime? DocDate(AiFinding f) => docs.TryGetValue(f.DocumentId, out var d) ? d.DocumentDate : null;
        var directions = live.Where(f => f.Kind is FindingKinds.Direction or FindingKinds.Deadline).ToList();
        foreach (var older in directions.Where(f => f.IsOpen))
        {
            var newer = directions.FirstOrDefault(n => n.DocumentId != older.DocumentId && DocDate(n) is { } nd && DocDate(older) is { } od && nd > od &&
                                                     FindingApprover.P(n, "responsibleParty") == FindingApprover.P(older, "responsibleParty") &&
                                                     AnalysisSchema.Similarity(n.Title, older.Title) >= 0.5);
            if (newer == null) continue;
            Note(older, $"A later document ({Where(newer)}) has a similar direction. This one may have been replaced or varied.");
            report.Superseded++;
        }

        // ---- 7. coming up soon
        foreach (var f in live.Where(f => f.IsOpen && f.Kind is FindingKinds.Hearing or FindingKinds.Deadline or FindingKinds.Direction or FindingKinds.Task
                                          && f.DateValue is { } d && d.Date >= now && d.Date <= now.AddDays(14)))
        {
            Note(f, $"Coming up on {f.DateValue:dddd d MMMM} – review this first.");
            report.Urgent++;
        }

        // ---- 8. long gaps in the chronology
        var dated = live.Where(f => f.DateValue != null && f.DatePrecision == "exact" && f.Kind is FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry
                                    or FindingKinds.Hearing or FindingKinds.Order or FindingKinds.Contact or FindingKinds.Safeguarding)
            .OrderBy(f => f.DateValue).ToList();
        if (dated.Select(f => f.DateValue!.Value.Date).Distinct().Count() >= 5)
        {
            var eventDates = db.GetEvents().Select(e => e.EventDate.Date).ToList();
            for (int i = 1; i < dated.Count; i++)
            {
                var a = dated[i - 1].DateValue!.Value;
                var b = dated[i].DateValue!.Value;
                if ((b - a).TotalDays < 183 || eventDates.Any(e => e > a && e < b)) continue;
                Conflict(dated[i - 1].DocumentId, $"Nothing dated between {a:MMM yyyy} and {b:MMM yyyy}",
                    $"The documents analysed so far have no dated events in this {Math.Round((b - a).TotalDays / 30.4)}-month gap. " +
                    "If things happened then, there may be papers you haven't imported yet.",
                    "gap-" + AnalysisSchema.Hash($"{a:yyyyMMdd}|{b:yyyyMMdd}")[..10], new[] { dated[i - 1], dated[i] }, isGap: true);
            }
        }

        // Notes only, and only for rows still undecided: a decision made while the checks ran is never overwritten
        db.SaveCheckNotes(all.Where(f => f.IsOpen));
        // ---- 9. documents to treat with care: things about the file itself that make it less trustworthy as it stands
        var modified = db.Query<ImportFile>("DocumentId IS NOT NULL").GroupBy(f => f.DocumentId!.Value).ToDictionary(g => g.Key, g => g.Max(f => f.SourceModifiedAt));
        foreach (var docId in all.Select(f => f.DocumentId).Distinct())
        {
            if (!docs.TryGetValue(docId, out var d) || d.DeletedAt != null) continue;
            var signals = TrustSignals(d, db.GetAllPageText(d.Id), modified.GetValueOrDefault(d.Id));
            if (signals.Count == 0) continue;
            report.Trust++;
            Conflict(d.Id, $"Treat with care: {AnalysisSchema.Clip(d.Title, 80)}", string.Join("\n", signals.Select(x => "• " + x)), "trust:" + d.Id, Array.Empty<AiFinding>());
        }

        if (newConflicts.Count > 0) db.SaveQuietBatch(newConflicts);
        return report;
    }

    public static string NormaliseCaseNumber(string s) =>
        new string((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Levenshtein distance, capped for speed: anything over 3 is "different".</summary>
    public static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3) return 4;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>The case's current records, loaded once, for "this is already recorded" checks.</summary>
    private sealed class Existing
    {
        private readonly List<Hearing> _hearings;
        private readonly List<TimelineEvent> _events;
        private readonly List<CourtOrder> _orders;
        private readonly List<CaseAction> _actions;
        private readonly List<ContactSession> _contact;
        private readonly List<Party> _parties;
        private readonly List<Complaint> _complaints;
        private readonly Dictionary<long, long> _docCategory;

        public Existing(Database db)
        {
            _hearings = db.All<Hearing>();
            _events = db.GetEvents();
            _orders = db.All<CourtOrder>();
            _actions = db.All<CaseAction>();
            _contact = db.All<ContactSession>();
            _parties = db.GetParties();
            _complaints = db.GetComplaints();
            _docCategory = db.GetDocuments().ToDictionary(d => d.Id, d => d.CategoryId);
        }

        public (string Type, long Id, string Text)? Match(AiFinding f)
        {
            var d = f.DateValue?.Date;
            switch (f.Kind)
            {
                case FindingKinds.Hearing when d != null:
                    var h = _hearings.FirstOrDefault(x => x.HearingDate?.Date == d);
                    return h == null ? null : ("hearing", h.Id, $"Already recorded: {h.AuditName}.");
                case FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry or FindingKinds.Safeguarding when d != null:
                    var e = _events.FirstOrDefault(x => x.EventDate.Date == d && AnalysisSchema.Similarity(x.Title, f.Title) >= 0.4);
                    return e == null ? null : ("event", e.Id, $"The timeline already has “{AnalysisSchema.Clip(e.Title, 60)}” on this date.");
                case FindingKinds.Order when d != null:
                    var o = _orders.FirstOrDefault(x => x.OrderDate?.Date == d);
                    return o == null ? null : ("order", o.Id, $"Already recorded: {o.AuditName}.");
                case FindingKinds.Direction or FindingKinds.Deadline or FindingKinds.Task:
                    var a = _actions.FirstOrDefault(x => (d == null || x.DueDate?.Date == d) && AnalysisSchema.Similarity(x.Title, f.Title) >= 0.5);
                    return a == null ? null : ("action", a.Id, $"Directions & tasks already has “{AnalysisSchema.Clip(a.Title, 60)}”.");
                case FindingKinds.Contact when d != null:
                    var c = _contact.FirstOrDefault(x => x.SessionDate?.Date == d);
                    return c == null ? null : ("contact", c.Id, "The contact log already has an entry for this date.");
                case FindingKinds.Party or FindingKinds.Child:
                    var name = FindingApprover.P(f, "name");
                    var p = name == null ? null : _parties.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    return p == null ? null : ("party", p.Id, $"{p.DisplayName} is already in Parties.");
                case FindingKinds.Complaint:
                    var x2 = _complaints.FirstOrDefault(x => AnalysisSchema.Similarity(x.Subject, f.Title) >= 0.6);
                    return x2 == null ? null : ("complaint", x2.Id, $"There's already a complaint: “{AnalysisSchema.Clip(x2.Subject, 60)}”.");
                case FindingKinds.Category:
                    var cat = FindingApprover.PLong(f, "categoryId");
                    return cat != null && _docCategory.TryGetValue(f.DocumentId, out var current) && current == cat
                        ? ("document", f.DocumentId, "The document is already filed under this category.") : null;
            }
            return null;
        }
    }
}
