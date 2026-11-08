using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>
/// Recognises complaint letters and replies without AI: complaint wording plus an organisation CaseKeeper knows
/// (Cafcass, HMCTS, the police, a council, an ombudsman, the JCIO, an MP…). Suggests the matching complaint
/// category and a complaint record for Import review, filled in only with what the document shows: the
/// organisation, any reference, the date, whether it's the organisation's reply, and a reply deadline when the
/// letter gives one. Every suggestion is marked "CaseKeeper rules" and waits for the person to approve it.
/// </summary>
public static class ComplaintRules
{
    public const string Model = "CaseKeeper rules";

    /// <summary>An organisation: how it's written, the name in Complaints, and the categories for a complaint and a reply.</summary>
    private sealed record Body(Regex Pattern, string Recipient, string Category, string ReplyCategory);

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Most specific first: an ombudsman letter about a council is the ombudsman's
    private static readonly Body[] Bodies =
    {
        new(R(@"\b(LGSCO|local government (and|&) social care ombudsman|local government ombudsman)\b"), "Local Government & Social Care Ombudsman (LGSCO)", "Ombudsman (PHSO / LGSCO)", "Ombudsman (PHSO / LGSCO)"),
        new(R(@"\b(PHSO|parliamentary (and|&) health service ombudsman)\b"), "Parliamentary & Health Service Ombudsman (PHSO)", "Ombudsman (PHSO / LGSCO)", "Ombudsman (PHSO / LGSCO)"),
        new(R(@"\blegal ombudsman\b"), "Legal Ombudsman", "Legal Ombudsman complaint|Other complaint letters", "Complaint responses"),
        new(R(@"\b(JCIO|judicial conduct investigations office)\b"), "Judicial Conduct Investigations Office (JCIO)", "Judicial Conduct (JCIO)", "Judicial Conduct (JCIO)"),
        new(R(@"\b(IOPC|independent office for police conduct)\b"), "Independent Office for Police Conduct (IOPC)", "Police complaint / IOPC|Other complaint letters", "Complaint responses"),
        new(R(@"\b(SRA|solicitors regulation authority)\b"), "Solicitors Regulation Authority (SRA)", "Solicitors Regulation Authority (SRA) report|Other complaint letters", "Complaint responses"),
        new(R(@"\bsocial work england\b"), "Social Work England", "Social Work England concern|Other complaint letters", "Complaint responses"),
        new(R(@"\bchildren'?s commissioner\b"), "Children's Commissioner", "Children's Commissioner|Other complaint letters", "Complaint responses"),
        new(R(@"\bcafcass cymru\b"), "Cafcass Cymru", "Cafcass complaint", "Cafcass complaint response"),
        new(R(@"\bcafcass\b"), "Cafcass", "Cafcass complaint", "Cafcass complaint response"),
        new(R(@"\b(HMCTS|courts? (and|&) tribunals? service|court service)\b"), "HMCTS (Court Service)", "HMCTS complaint", "Complaint responses"),
        new(R(@"\b(member of parliament|house of commons|\bMP\b)"), "Member of Parliament (MP)", "Letters to my MP", "MP responses"),
        new(R(@"\b(police|constabulary)\b"), "Police", "Police complaint / IOPC|Other complaint letters", "Complaint responses"),
        new(R(@"\b(council|local authority|children'?s services|social services)\b"), "Local Authority / Children's Services", "Local authority complaint (stages 1–3)|Other complaint letters", "Complaint responses"),
    };

    private static readonly Regex Reply = R(@"\b(thank you for your (complaint|letter|email)|we (have )?received your complaint|in response to your complaint|" +
        @"(our|my) response to your complaint|we have (now )?investigated|outcome of (your|the) complaint|(not |partially |partly )?upheld|" +
        @"i am writing in response to|stage (one|two|1|2) response|final response)\b");

    private static readonly Regex Reference = R(@"\b(?:our|your|complaint|case)\s*(?:ref(?:erence)?\.?|no\.?|number)\s*[:#]?\s*(?<r>[A-Z0-9][A-Z0-9/\-]{3,24})\b");

    private static readonly Regex WorkingDays = R(@"\b(?:within|in)\s+(?<n>\d{1,2})\s+working\s+days\b");

    private static readonly Regex ByDate = R(@"\b(?:respond|reply|response|hear from us|write to you)\w*\s+(?:to you\s+)?by\s+(?<d>\d{1,2}(?:st|nd|rd|th)?\s+[A-Z][a-z]+\s+\d{4})");

    /// <summary>The complaint record (and category) suggested for this document, or nothing when it isn't a complaint.</summary>
    public static List<AiFinding> Suggest(CaseDocument doc, IReadOnlyDictionary<int, string> pages, IReadOnlyList<Category> categories, long? jobId)
    {
        var found = new List<AiFinding>();
        if (doc == null || pages == null || pages.Count == 0) return found;
        var ordered = pages.OrderBy(p => p.Key).ToList();
        var text = string.Join("\n", ordered.Take(6).Select(p => p.Value));
        if (text.Length > 20000) text = text[..20000];
        var heading = $"{doc.Title} {doc.OriginalFileName}";
        if (!DocumentAnalyzer.IsComplaintWording(text) && !DocumentAnalyzer.IsComplaintWording(heading)) return found;
        var body = Bodies.FirstOrDefault(b => b.Pattern.IsMatch(text) || b.Pattern.IsMatch(heading));
        bool isReply = Reply.IsMatch(text);

        // Where it says so: the first sentence about the complaint, and its page
        var (page, sentence) = FirstMention(ordered);
        var reference = Reference.Match(text) is { Success: true } m ? m.Groups["r"].Value.Trim('-', '/') : null;
        if (reference != null && !reference.Any(char.IsDigit)) reference = null;      // "Our ref: ABOUT…" isn't a reference

        var payload = new JsonObject
        {
            ["recipient"] = body?.Recipient ?? "Other",
            ["isResponse"] = isReply ? "true" : "false"
        };
        if (reference != null) payload["reference"] = reference;
        var notes = new List<string>();
        if (ByDate.Match(text) is { Success: true } bd && UkDates.TryParse(bd.Groups["d"].Value, out var due, out var precision, out _) && precision == "exact")
            payload["responseDue"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (WorkingDays.Match(text) is { Success: true } wd && int.TryParse(wd.Groups["n"].Value, out var days) && days is > 0 and <= 60)
        {
            payload["responseDueText"] = wd.Value;
            if (doc.DocumentDate is { } from)
            {
                payload["responseDue"] = AddWorkingDays(from, days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                notes.Add($"Reply due worked out by CaseKeeper: {days} working days from {from:d MMM yyyy}, skipping weekends but not bank holidays. Check it.");
            }
        }

        var title = isReply
            ? $"Reply from {Short(body?.Recipient)} about a complaint"
            : $"Complaint to {Short(body?.Recipient)}";
        var complaint = new AiFinding
        {
            DocumentId = doc.Id, JobId = jobId, Kind = FindingKinds.Complaint, Title = title,
            Detail = string.Join("\n", new[] { AnalysisSchema.Clip(sentence, 300) }.Concat(notes).Where(s => !string.IsNullOrWhiteSpace(s))),
            Confidence = body != null ? 0.65 : 0.45, Model = Model, Reason = body != null
                ? $"Complaint wording and “{body.Recipient}” appear in the document."
                : "Complaint wording appears in the document, but no organisation CaseKeeper knows.",
            Excerpt = AnalysisSchema.Clip(sentence, 200), PageFrom = page,
            DateValue = doc.DocumentDate, DatePrecision = doc.DocumentDate != null ? "exact" : "unknown", DateRole = isReply ? "correspondence" : "complaint",
            PayloadJson = payload.ToJsonString(), SchemaVersion = AnalysisSchema.Version, AnalysisVersion = AnalysisSchema.AnalysisVersion, PromptVersion = "rules"
        };
        complaint.Fingerprint = AnalysisSchema.Fingerprint(complaint.Kind, complaint.DateValue, "rules complaint " + doc.Id);
        found.Add(complaint);

        // The matching complaint category, when the document hasn't got a real one yet
        var unknown = CategoryMatcher.Unknown(categories)?.Id;
        var wanted = isReply ? body?.ReplyCategory ?? "Complaint responses" : body?.Category ?? "Other complaint letters";
        var category = wanted.Split('|').Select(w => categories.FirstOrDefault(c => c.Name.Equals(w, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c != null);
        if (category != null && doc.CategoryId == unknown)
        {
            var cat = new AiFinding
            {
                DocumentId = doc.Id, JobId = jobId, Kind = FindingKinds.Category, Title = category.DisplayName,
                Confidence = Math.Min(0.8, complaint.Confidence + 0.1), Reason = complaint.Reason, Model = Model,
                Excerpt = complaint.Excerpt, PageFrom = page,
                PayloadJson = new JsonObject { ["categoryId"] = category.Id, ["alternativeCategoryIds"] = new JsonArray() }.ToJsonString(),
                SchemaVersion = AnalysisSchema.Version, AnalysisVersion = AnalysisSchema.AnalysisVersion, PromptVersion = "rules"
            };
            cat.Fingerprint = AnalysisSchema.Fingerprint(cat.Kind, null, "category " + doc.Id);
            found.Add(cat);
        }
        return found;
    }

    /// <summary>Adds working days (Monday to Friday). Bank holidays aren't known, so the result is always marked to check.</summary>
    public static DateTime AddWorkingDays(DateTime from, int days)
    {
        var d = from.Date;
        while (days > 0)
        {
            d = d.AddDays(1);
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--;
        }
        return d;
    }

    private static (int? Page, string Sentence) FirstMention(List<KeyValuePair<int, string>> pages)
    {
        foreach (var (page, text) in pages)
            foreach (var s in Regex.Split(text ?? "", @"(?<=[.!?])\s+|\n+"))
                if (DocumentAnalyzer.IsComplaintWording(s) && s.Trim().Length > 12) return (page, s.Trim());
        return (pages.FirstOrDefault().Key, "");
    }

    private static string Short(string recipient) => recipient switch
    {
        null or "Other" => "an organisation",
        "HMCTS (Court Service)" => "HMCTS",
        "Local Authority / Children's Services" => "the local authority",
        "Member of Parliament (MP)" => "an MP",
        "Police" => "the police",
        _ => recipient
    };
}
