using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

public sealed class ApprovalOutcome
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string RecordType { get; init; } = "";
    public long? RecordId { get; init; }
}

/// <summary>
/// Turns an approved AI finding into a real case record, using the same database calls (and so the
/// same validation and audit trail) as the screens where people add records by hand. Nothing is ever
/// filled in that the document didn't say: an unknown time stays blank, an unknown outcome is "Other".
/// Every approval, rejection and undo is written to the audit trail, saying that the value was
/// suggested by AI and approved by the person.
/// </summary>
public static class FindingApprover
{
    // ------------------------------------------------------------------ payload helpers
    public static string P(AiFinding f, string key)
    {
        if (string.IsNullOrWhiteSpace(f.PayloadJson)) return null;
        try
        {
            var node = JsonNode.Parse(f.PayloadJson)?[key];
            return node switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s,
                JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                _ => node.ToJsonString()
            };
        }
        catch (JsonException) { return null; }
    }

    public static long? PLong(AiFinding f, string key) => long.TryParse(P(f, key), out var v) ? v : null;

    private static DateTime? PDate(AiFinding f, string key) => UkDates.ParseIso(P(f, key));

    public static bool NeedsCalculation(AiFinding f) => P(f, "needsCalculation") == "true";

    private static string Provenance(Database db, AiFinding f)
    {
        var doc = db.GetDocument(f.DocumentId);
        var page = f.PageFrom is { } p ? $", page {p}" : "";
        return $"From “{doc?.Title ?? "a document"}”{page}. Suggested by AI, checked and approved {DateTime.Now:d MMM yyyy}.";
    }

    private static string Pick(string value, IEnumerable<string> allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var exact = allowed.FirstOrDefault(a => a.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return exact ?? fallback;
    }

    public static string HearingTypeFor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = raw.ToLowerInvariant();
        (string Key, string Type)[] map =
        {
            ("fhdra", "First Hearing Dispute Resolution Appointment (FHDRA)"), ("first hearing dispute", "First Hearing Dispute Resolution Appointment (FHDRA)"),
            ("dispute resolution appointment", "Dispute Resolution Appointment (DRA)"), ("dra", "Dispute Resolution Appointment (DRA)"),
            ("fact-finding", "Fact-finding hearing"), ("fact finding", "Fact-finding hearing"), ("final", "Final hearing"),
            ("issues resolution", "Issues Resolution Hearing (IRH)"), ("irh", "Issues Resolution Hearing (IRH)"),
            ("case management", "Case Management Hearing (CMH)"), ("cmh", "Case Management Hearing (CMH)"),
            ("first appointment", "First Appointment (FDA)"), ("fda", "First Appointment (FDA)"),
            ("financial dispute", "Financial Dispute Resolution (FDR)"), ("fdr", "Financial Dispute Resolution (FDR)"),
            ("directions", "Directions hearing"), ("review", "Review hearing"), ("appeal", "Appeal hearing"),
            ("enforcement", "Enforcement hearing"), ("without notice", "Without notice hearing"), ("return", "Return hearing (on notice)")
        };
        foreach (var (key, type) in map)
            if (System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{System.Text.RegularExpressions.Regex.Escape(key)}\b")) return type;
        return raw.Trim();
    }

    public static string OrderTypeFor(string raw, string title)
    {
        var t = $"{raw} {title}".ToLowerInvariant();
        if (t.Contains("non-molestation") || t.Contains("non molestation")) return "Non-Molestation Order";
        if (t.Contains("occupation order")) return "Occupation Order";
        if (t.Contains("prohibited steps")) return "Prohibited Steps Order";
        if (t.Contains("specific issue")) return "Specific Issue Order";
        if (t.Contains("lives with") || t.Contains("live with")) return "Child Arrangements Order – lives with";
        if (t.Contains("spend time") || t.Contains("spends time")) return "Child Arrangements Order – spends time with";
        if (t.Contains("91(14)")) return "Section 91(14) order";
        if (t.Contains("interim care") || t.Contains("supervision order")) return "Interim Care / Supervision Order";
        if (t.Contains("enforcement order")) return "Enforcement order";
        if (t.Contains("consent order") || t.Contains("by consent")) return "Consent order";
        if (t.Contains("financial")) return "Financial order";
        if (t.Contains("case management") || t.Contains("directions")) return "Case management / directions order";
        return raw?.Trim() ?? "";
    }

    private static string ModeFor(string raw)
    {
        var t = (raw ?? "").ToLowerInvariant();
        if (t.Length == 0) return "";
        if (t.Contains("hybrid")) return "Hybrid";
        if (t.Contains("video") || t.Contains("cvp") || t.Contains("teams") || t.Contains("remote")) return "Remote – video (CVP / Teams)";
        if (t.Contains("phone")) return "Remote – telephone";
        if (t.Contains("person") || t.Contains("attend")) return "In person";
        return raw.Trim();
    }

    private static string ResponsibleFor(string raw)
    {
        var t = (raw ?? "").ToLowerInvariant();
        if (t.Contains("both") || t.Contains("parties")) return "Both parties";
        if (t.Contains("applicant")) return "Applicant";
        if (t.Contains("respondent")) return "Respondent";
        if (t.Contains("cafcass")) return "Cafcass";
        if (t.Contains("local authority") || t.Contains("council") || t.Contains("social")) return "Local authority";
        if (t.Contains("court")) return "The court";
        if (t.Contains("expert")) return "Expert";
        if (t.Contains("school")) return "School";
        return "Other";
    }

    private static string RoleFor(string raw, bool child)
    {
        if (child) return "Child (subject of proceedings)";
        var t = (raw ?? "").ToLowerInvariant();
        if (t.Contains("second applicant")) return "Second Applicant";
        if (t.Contains("second respondent")) return "Second Respondent";
        if (t.Contains("first respondent")) return "First Respondent";
        if (t.Contains("applicant")) return "Applicant";
        if (t.Contains("respondent")) return "Respondent";
        if (t.Contains("guardian")) return "Children's Guardian";
        if (t.Contains("cafcass") || t.Contains("fca")) return "Cafcass Officer (FCA)";
        if (t.Contains("local authority") || t.Contains("council")) return "Local Authority";
        if (t.Contains("mckenzie")) return "McKenzie Friend";
        if (t.Contains("intervenor")) return "Intervenor";
        if (t.Contains("child")) return "Child (subject of proceedings)";
        return "Other";
    }

    private static string EventTypeFor(AiFinding f)
    {
        if (f.Kind == FindingKinds.Safeguarding) return "Safeguarding Concern";
        var text = (f.Title + " " + f.Detail).ToLowerInvariant();
        return f.DateRole switch
        {
            "hearingListed" or "hearingHeld" or "hearingAdjournedFrom" or "hearingAdjournedTo" or "hearingVacated" => "Hearing",
            "orderMade" => "Order Made",
            "filed" or "served" or "applicationMade" => "Filing / Service",
            "contact" => "Contact / Handover",
            "deadline" => "Deadline",
            "complaint" => "Complaint",
            "correspondence" or "received" => text.Contains("cafcass") ? "Cafcass Contact" : "Communication",
            "incidentAlleged" => text.Contains("police") ? "Police" : "Incident",
            _ => text.Contains("police") ? "Police" : text.Contains("school") ? "School" : text.Contains("social") ? "Social Services" :
                 text.Contains("doctor") || text.Contains("hospital") || text.Contains("gp ") ? "Medical" : "Other"
        };
    }

    // ------------------------------------------------------------------ the record approving would create
    /// <summary>
    /// The record that approving this finding would create or change, pre-filled only with what the
    /// document said. Null for findings that are notes for review (conflicts, issues), which are resolved
    /// rather than turned into records.
    /// </summary>
    public static object Draft(Database db, AiFinding f)
    {
        var provenance = Provenance(db, f);
        switch (f.Kind)
        {
            case FindingKinds.Category:
            {
                var doc = db.GetDocument(f.DocumentId);
                if (doc == null) return null;
                if (PLong(f, "categoryId") is { } cat) doc.CategoryId = cat;
                return doc;
            }
            case FindingKinds.Summary:
            {
                var doc = db.GetDocument(f.DocumentId);
                if (doc == null) return null;
                if (f.DateValue is { } d && f.DatePrecision == "exact") doc.DocumentDate = d;
                if (P(f, "author") is { } a) doc.Author = a;
                if (!string.IsNullOrWhiteSpace(f.Detail)) doc.Description = f.Detail;
                return doc;
            }
            case FindingKinds.Hearing:
            {
                var status = P(f, "hearingStatus") switch
                {
                    "adjourned" => "Adjourned", "vacated" => "Vacated",
                    "heard" when f.DateRole == "hearingHeld" => "Heard",
                    _ => f.DateRole == "hearingVacated" ? "Vacated" : "Listed"
                };
                // The type from the details, or from the description when it names a known type ("Final hearing listed")
                var fromDescription = HearingTypeFor(f.Title);
                var hearingType = P(f, "hearingType") is { } given ? HearingTypeFor(given)
                    : Hearing.Types.Contains(fromDescription) ? fromDescription : "";
                return new Hearing
                {
                    HearingDate = f.DatePrecision == "exact" ? f.DateValue : null, Time = f.TimeText ?? "",
                    HearingType = hearingType, Court = P(f, "court") ?? P(f, "venue") ?? "",
                    Judge = P(f, "judge") ?? "", Mode = ModeFor(P(f, "attendanceMode")), TimeEstimate = P(f, "timeEstimate") ?? "",
                    Status = status, Purpose = f.Title, Notes = provenance, DocumentId = f.DocumentId
                };
            }
            case FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry or FindingKinds.Safeguarding:
                return new TimelineEvent
                {
                    EventDate = f.DatePrecision == "exact" && f.DateValue is { } ed ? ed : DateTime.MinValue,
                    Time = f.TimeText ?? "", Title = f.Title, EventType = EventTypeFor(f),
                    Description = string.Join("\n", new[] { f.Detail, f.DatePrecision is "month" or "year" or "approximate" ? $"Date given as “{f.DateText}”." : null, provenance }
                        .Where(s => !string.IsNullOrWhiteSpace(s))),
                    DocumentId = f.DocumentId, IncludeInChronology = true,
                    Location = P(f, "venue") ?? "", Reference = P(f, "reference") ?? ""
                };
            case FindingKinds.Order:
            {
                var text = (f.Title + " " + P(f, "orderType")).ToLowerInvariant();
                return new CourtOrder
                {
                    OrderDate = f.DatePrecision == "exact" ? f.DateValue : null, OrderType = OrderTypeFor(P(f, "orderType"), f.Title),
                    MadeBy = P(f, "madeBy") ?? P(f, "judge") ?? "",
                    Court = P(f, "court") ?? "", IsInterim = text.Contains("interim"), HasPenalNotice = text.Contains("penal notice"),
                    Summary = string.Join("\n", new[] { f.Detail.Length > 0 ? f.Detail : f.Title, provenance }), DocumentId = f.DocumentId
                };
            }
            case FindingKinds.Direction or FindingKinds.Task or FindingKinds.Deadline or FindingKinds.MissingDocument:
                return new CaseAction
                {
                    Kind = f.Kind switch
                    {
                        FindingKinds.Direction => "Court direction", FindingKinds.Deadline => "Court direction",
                        FindingKinds.MissingDocument => "My task", _ => "Reminder"
                    },
                    Title = f.Kind == FindingKinds.MissingDocument ? "Get a copy of: " + f.Title : AnalysisSchema.Clip(P(f, "actionRequired") ?? f.Title, 140),
                    Detail = P(f, "wording") ?? f.Detail,
                    Responsible = f.Kind == FindingKinds.MissingDocument ? "Me" : ResponsibleFor(P(f, "responsibleParty")),
                    DueDate = f.DatePrecision == "exact" ? f.DateValue : null, Status = "Outstanding",
                    DocumentId = f.DocumentId, Notes = provenance
                };
            case FindingKinds.Contact:
                return new ContactSession
                {
                    SessionDate = f.DatePrecision == "exact" ? f.DateValue : null, Time = f.TimeText ?? "",
                    Outcome = Pick(P(f, "contactOutcome"), ContactSession.Outcomes, "Other"), ContactType = "Other",
                    Notes = string.Join("\n", new[] { f.Title, f.Detail, provenance }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
                    DocumentId = f.DocumentId
                };
            case FindingKinds.Party or FindingKinds.Child:
                return new Party
                {
                    Role = RoleFor(P(f, "role"), f.Kind == FindingKinds.Child), Name = P(f, "name") ?? "",
                    Representative = P(f, "representative") ?? "", DateOfBirth = PDate(f, "dateOfBirth"),
                    Notes = provenance
                };
            case FindingKinds.Complaint:
                return new Complaint
                {
                    Recipient = Pick(P(f, "recipient") ?? P(f, "organisation"), Complaint.Recipients, "Other"),
                    RecipientName = P(f, "recipient") ?? "", Reference = P(f, "reference") ?? "", Subject = f.Title,
                    Summary = string.Join("\n", new[] { f.Detail, provenance }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    DateSent = f.DatePrecision == "exact" && f.DateRole == "complaint" ? f.DateValue : null,
                    Status = f.DatePrecision == "exact" && f.DateRole == "complaint" ? "Sent" : "Draft",
                    DocumentIds = new List<long> { f.DocumentId }
                };
            default:
                return null;
        }
    }

    /// <summary>Why a finding can't be approved as it stands, or null. The person can usually fix it with Edit &amp; approve.</summary>
    public static string Blocker(AiFinding f, object draft)
    {
        switch (draft)
        {
            case Hearing { HearingDate: null }: return "A hearing needs a date. Use Edit & approve and take the date from the document.";
            case TimelineEvent e when e.EventDate == DateTime.MinValue:
                return "A timeline entry needs an exact date. Use Edit & approve to add one from the document.";
            case ContactSession { SessionDate: null }: return "A contact entry needs a date. Use Edit & approve to add one.";
            case CaseAction { DueDate: null } when f.Kind == FindingKinds.Deadline && NeedsCalculation(f):
                return "This deadline has to be worked out. Use “Work out the date…” and check the result against the order.";
            case CaseAction { DueDate: null } when f.Kind == FindingKinds.Deadline:
                return "A deadline needs a date. Use Edit & approve, or “Work out the date…”.";
            case Party p when string.IsNullOrWhiteSpace(p.Name): return "There's no name to add.";
            case CaseDocument when f.Kind == FindingKinds.Category && PLong(f, "categoryId") == null:
                return "No category was chosen. Use Edit & approve to pick one.";
        }
        return null;
    }

    /// <summary>A record already in the case that looks like the same thing, described for a warning, or null.</summary>
    public static string FindEquivalent(Database db, AiFinding f, object draft)
    {
        switch (draft)
        {
            case Hearing h when h.HearingDate is { } d:
                var hearing = db.All<Hearing>().FirstOrDefault(x => x.HearingDate?.Date == d.Date);
                return hearing == null ? null : $"There's already a hearing on {d:d MMM yyyy}: {hearing.AuditName}.";
            case TimelineEvent e when e.EventDate != DateTime.MinValue:
                var ev = db.GetEvents().FirstOrDefault(x => x.EventDate.Date == e.EventDate.Date &&
                                                            (AnalysisSchema.Similarity(x.Title, e.Title) >= 0.4 || x.EventType == "Hearing" && e.EventType == "Hearing"));
                return ev == null ? null : $"The timeline already has “{ev.Title}” on {ev.EventDate:d MMM yyyy}.";
            case CourtOrder o when o.OrderDate is { } od:
                var order = db.All<CourtOrder>().FirstOrDefault(x => x.OrderDate?.Date == od.Date);
                return order == null ? null : $"There's already an order dated {od:d MMM yyyy}: {order.AuditName}.";
            case CaseAction a:
                var action = db.All<CaseAction>().FirstOrDefault(x =>
                    (a.DueDate == null || x.DueDate?.Date == a.DueDate?.Date) && AnalysisSchema.Similarity(x.Title, a.Title) >= 0.5);
                return action == null ? null : $"Directions & tasks already has “{action.Title}”{(action.DueDate is { } dd ? $" due {dd:d MMM yyyy}" : "")}.";
            case ContactSession c when c.SessionDate is { } cd:
                var contact = db.All<ContactSession>().FirstOrDefault(x => x.SessionDate?.Date == cd.Date);
                return contact == null ? null : $"The contact log already has an entry for {cd:d MMM yyyy}.";
            case Party p when !string.IsNullOrWhiteSpace(p.Name):
                var party = db.GetParties().FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                return party == null ? null : $"{party.DisplayName} is already in Parties.";
            case Complaint x:
                var complaint = db.GetComplaints().FirstOrDefault(c =>
                    (!string.IsNullOrWhiteSpace(x.Reference) && c.Reference.Equals(x.Reference, StringComparison.OrdinalIgnoreCase)) ||
                    AnalysisSchema.Similarity(c.Subject, x.Subject) >= 0.6);
                return complaint == null ? null : $"There's already a complaint: “{complaint.Subject}”.";
            case CaseDocument d when f.Kind == FindingKinds.Category:
                var current = db.GetDocument(d.Id);
                return current != null && current.CategoryId == d.CategoryId ? "The document is already filed under this category." : null;
        }
        return null;
    }

    // ------------------------------------------------------------------ decisions
    /// <summary>
    /// Creates (or updates) the record through the normal database calls, then marks the finding approved
    /// and audits the decision. <paramref name="record"/> is the draft, possibly edited by the person.
    /// </summary>
    public static ApprovalOutcome Approve(Database db, AiFinding f, object record, bool edited, bool automatic = false, string note = null)
    {
        // Read the current state, so a decision made in another window (or by the checks) isn't approved twice
        var fresh = db.Query<AiFinding>("Id=$i", ("$i", f.Id)).FirstOrDefault();
        if (!f.IsOpen || (f.Id != 0 && fresh != null && !fresh.IsOpen))
            return new ApprovalOutcome { Message = "This suggestion has already been dealt with." };
        string type;
        long? id;
        string previous = "";

        // Mark it as being approved first: if CaseKeeper stops between adding the record and recording the decision,
        // it comes back with a warning to check, rather than silently allowing a duplicate
        var wasStatus = f.StatusValue;
        f.StatusValue = FindingStatus.Applying;
        db.SaveQuiet(f);
        try
        {
        switch (record)
        {
            case CaseDocument doc:
            {
                var before = db.GetDocument(doc.Id) ?? throw new InvalidOperationException("That document is no longer in the library.");
                previous = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["categoryId"] = before.CategoryId.ToString(CultureInfo.InvariantCulture),
                    ["documentDate"] = before.DocumentDate?.ToString("yyyy-MM-dd") ?? "",
                    ["dateReceived"] = before.DateReceived?.ToString("yyyy-MM-dd") ?? "",
                    ["partyId"] = before.PartyId?.ToString(CultureInfo.InvariantCulture) ?? "",
                    ["author"] = before.Author ?? "", ["description"] = before.Description ?? "", ["title"] = before.Title ?? "",
                    ["tags"] = before.Tags ?? "", ["notes"] = before.Notes ?? "", ["exhibitRef"] = before.ExhibitRef ?? "",
                    ["starred"] = before.IsImportant ? "1" : "0"
                });
                db.UpdateDocument(doc);
                (type, id) = ("document", doc.Id);
                break;
            }
            case Hearing h:
                db.Save(h);
                HearingSync.Sync(db, h);
                (type, id) = ("hearing", h.Id);
                break;
            case TimelineEvent e:
                db.SaveEvent(e);
                (type, id) = ("event", e.Id);
                break;
            case CourtOrder o:
                db.Save(o);
                (type, id) = ("order", o.Id);
                break;
            case CaseAction a:
                db.Save(a);
                (type, id) = ("action", a.Id);
                break;
            case ContactSession c:
                db.Save(c);
                (type, id) = ("contact", c.Id);
                break;
            case Party p:
                db.SaveParty(p);
                (type, id) = ("party", p.Id);
                break;
            case Complaint x:
                db.SaveComplaint(x);
                (type, id) = ("complaint", x.Id);
                break;
            case null:
                (type, id) = ("", null);       // a note for review, resolved without creating anything
                break;
            default:
                throw new ArgumentException("Can't approve into " + record.GetType().Name);
        }
        }
        catch
        {
            // Nothing was added (or the database refused it): the suggestion goes back as it was
            f.StatusValue = wasStatus;
            db.SaveQuiet(f);
            throw;
        }

        f.StatusValue = automatic ? FindingStatus.AutoApplied : FindingStatus.Approved;
        f.CreatedRecordType = type;
        f.CreatedRecordId = id;
        f.PreviousValue = previous;
        f.DecidedAt = DateTime.Now;
        db.SaveQuiet(f);

        var what = record == null ? "Marked as resolved" : $"Created/updated {type} #{id}";
        db.Audit(automatic ? "ai.autoapply" : "ai.approve", "AI finding", f.Id,
            automatic ? $"Applied AI category suggestion by rule: {f.Title}" : $"Approved AI suggestion ({f.KindText}): {AnalysisSchema.Clip(f.Title, 90)}",
            $"AI-suggested, {(automatic ? "applied by the auto-apply rule the person switched on" : "approved by the user")}" +
            $"{(edited ? " after editing" : "")}.\n{what}.\nSource: document #{f.DocumentId}{(f.PageFrom is { } pg ? $", page {pg}" : "")}.\n" +
            $"Model: {f.Model} · prompt {f.PromptVersion} · schema {f.SchemaVersion} · confidence {f.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}" +
            (string.IsNullOrWhiteSpace(note) ? "" : "\n" + note));
        return new ApprovalOutcome { Ok = true, RecordType = type, RecordId = id, Message = what };
    }

    /// <summary>Rejects a suggestion. The decision is kept; the quoted evidence isn't, as it's no longer needed.</summary>
    public static void Reject(Database db, AiFinding f)
    {
        if (!f.IsOpen) return;
        f.StatusValue = FindingStatus.Rejected;
        f.DecidedAt = DateTime.Now;
        f.Excerpt = "";
        f.Detail = "";
        f.Reason = "";
        db.SaveQuiet(f);
        db.Audit("ai.reject", "AI finding", f.Id, $"Rejected AI suggestion ({f.KindText})",
            $"Document #{f.DocumentId}. Model: {f.Model} · prompt {f.PromptVersion}");
    }

    public static void Defer(Database db, AiFinding f)
    {
        if (f.StatusValue != FindingStatus.Proposed) return;
        f.StatusValue = FindingStatus.Deferred;
        f.DecidedAt = DateTime.Now;
        db.SaveQuiet(f);
        db.Audit("ai.defer", "AI finding", f.Id, $"Deferred AI suggestion ({f.KindText})", $"Document #{f.DocumentId}");
    }

    /// <summary>Puts a rejected or deferred suggestion back in the review list.</summary>
    public static void Reconsider(Database db, AiFinding f)
    {
        if (f.StatusValue is not (FindingStatus.Rejected or FindingStatus.Deferred)) return;
        var was = f.StatusText;
        f.StatusValue = FindingStatus.Proposed;
        f.DecidedAt = null;
        if (string.IsNullOrWhiteSpace(f.Excerpt))
            f.Warnings = string.Join("\n", new[] { f.Warnings, "The quoted words were removed when this was rejected. Open the document at the page and check it before approving." }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        db.SaveQuiet(f);
        db.Audit("ai.reconsider", "AI finding", f.Id, $"Put an AI suggestion back for review ({f.KindText})", $"Was: {was}");
    }

    /// <summary>Records that the person has gone through a document themselves (for example one with no readable text).</summary>
    public static void MarkDocumentReviewed(Database db, long documentId)
    {
        var files = db.Query<ImportFile>("DocumentId=$d", ("$d", documentId));
        foreach (var file in files) { file.ManuallyReviewed = true; file.UpdatedAt = DateTime.Now; }
        if (files.Count > 0) db.SaveQuietBatch(files);
        db.Audit("bulkimport.reviewed", "document", documentId, "Marked a document as reviewed by hand", "");
    }

    /// <summary>
    /// Reverses an approval: removes the record it created (or puts back what it changed) through the
    /// normal database calls, and returns the suggestion to the review list.
    /// </summary>
    public static ApprovalOutcome Undo(Database db, AiFinding f)
    {
        if (f.StatusValue is not (FindingStatus.Approved or FindingStatus.AutoApplied))
            return new ApprovalOutcome { Message = "Only approved suggestions can be undone." };
        var id = f.CreatedRecordId;
        string removed = "nothing to remove";
        if (id is { } rid)
        {
            switch (f.CreatedRecordType)
            {
                case "document":
                {
                    var doc = db.GetDocument(rid);
                    var prev = string.IsNullOrWhiteSpace(f.PreviousValue) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(f.PreviousValue);
                    if (doc != null && prev != null)
                    {
                        // Everything the approval (or the Edit & approve form) could have changed is put back
                        if (long.TryParse(prev.GetValueOrDefault("categoryId"), out var cat)) doc.CategoryId = cat;
                        doc.DocumentDate = UkDates.ParseIso(prev.GetValueOrDefault("documentDate"));
                        if (prev.ContainsKey("dateReceived")) doc.DateReceived = UkDates.ParseIso(prev["dateReceived"]);
                        if (prev.ContainsKey("partyId")) doc.PartyId = long.TryParse(prev["partyId"], out var pid) ? pid : null;
                        doc.Author = prev.GetValueOrDefault("author") ?? "";
                        doc.Description = prev.GetValueOrDefault("description") ?? "";
                        if (!string.IsNullOrWhiteSpace(prev.GetValueOrDefault("title"))) doc.Title = prev["title"];
                        if (prev.ContainsKey("tags")) doc.Tags = prev["tags"];
                        if (prev.ContainsKey("notes")) doc.Notes = prev["notes"];
                        if (prev.ContainsKey("exhibitRef")) doc.ExhibitRef = prev["exhibitRef"];
                        if (prev.ContainsKey("starred")) doc.IsImportant = prev["starred"] == "1";
                        db.UpdateDocument(doc);
                        removed = "document details put back";
                    }
                    break;
                }
                case "hearing":
                    if (db.Find<Hearing>(rid) is { } h)
                    {
                        if (h.EventId is { } ev) db.DeleteEvent(ev);
                        db.Remove<Hearing>(rid);
                        removed = "hearing removed";
                    }
                    break;
                case "event":
                    if (db.GetEvents().Any(e => e.Id == rid)) { db.DeleteEvent(rid); removed = "timeline entry removed"; }
                    break;
                case "order":
                    if (db.Find<CourtOrder>(rid) != null) { db.Remove<CourtOrder>(rid); removed = "order removed"; }
                    break;
                case "action":
                    if (db.Find<CaseAction>(rid) != null) { db.Remove<CaseAction>(rid); removed = "direction or task removed"; }
                    break;
                case "contact":
                    if (db.Find<ContactSession>(rid) != null) { db.Remove<ContactSession>(rid); removed = "contact entry removed"; }
                    break;
                case "party":
                    if (db.GetParties().Any(p => p.Id == rid)) { db.DeleteParty(rid); removed = "party removed"; }
                    break;
                case "complaint":
                    if (db.GetComplaints().Any(c => c.Id == rid)) { db.DeleteComplaint(rid); removed = "complaint removed"; }
                    break;
            }
        }
        f.StatusValue = FindingStatus.Proposed;
        f.CreatedRecordType = "";
        f.CreatedRecordId = null;
        f.PreviousValue = "";
        f.DecidedAt = null;
        db.SaveQuiet(f);
        db.Audit("ai.undo", "AI finding", f.Id, $"Undid approval of AI suggestion ({f.KindText})", $"Result: {removed}. Back in the review list.");
        return new ApprovalOutcome { Ok = true, Message = removed };
    }
}
