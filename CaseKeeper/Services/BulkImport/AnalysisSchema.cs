using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

// ======================================================================================
// What the local AI must return for each document (part), how that answer is checked before
// anything is kept, and the helpers that tie every finding back to the words on the page.
// ======================================================================================

/// <summary>The answer for one document (or one part of a long document), after validation.</summary>
public sealed class RawAnalysis
{
    public RawSummary Summary { get; set; } = new();
    public RawCategory Category { get; set; } = new();
    public List<RawFinding> Findings { get; } = new();
    /// <summary>Findings that failed validation and were dropped, so the review screen can say so.</summary>
    public int Dropped { get; set; }
}

public sealed class RawSummary
{
    public string DocumentType, DocumentTitle, DocumentDate, DocumentDateText, CaseNumber, CourtName, AuthorOrIssuer, Recipient, Summary = "";
    public double Confidence;
}

public sealed class RawCategory
{
    public string Key = "UNKNOWN";
    public double Confidence;
    public string Reason = "";
    /// <summary>The AI named a category that wasn't offered; it was replaced with UNKNOWN.</summary>
    public bool WasInvented;
    public List<string> Alternatives { get; } = new();
}

public sealed class RawFinding
{
    public string RecordType = "", Description = "", AssertedBy, Excerpt = "", Reason = "", Date, DateText, DatePrecision = "unknown", DateRole = "mentioned", Time;
    public int? Page, PageTo;
    public double Confidence;
    public Dictionary<string, string> Details { get; } = new();
    public bool? DueDateExplicit;
}

/// <summary>Thrown when an AI answer can't be used: not JSON, or not the shape the schema demands.</summary>
public sealed class AnalysisValidationException : Exception
{
    public string Code { get; }
    public AnalysisValidationException(string code, string message) : base(message) { Code = code; }
}

public static class AnalysisSchema
{
    /// <summary>Bump when the schema changes, so earlier analyses are recognised as out of date.</summary>
    public const string Version = "bulk-schema-2";
    /// <summary>Bump when the checks applied to answers change.</summary>
    public const string AnalysisVersion = "bulk-checks-4";

    public static readonly string[] Precisions = { "exact", "month", "year", "approximate", "unknown" };
    public static readonly string[] HearingStatuses = { "listed", "adjourned", "vacated", "heard", "unknown" };

    public static readonly string[] DetailKeys =
    {
        "court", "venue", "attendanceMode", "hearingType", "judge", "caseNumber", "hearingStatus", "timeEstimate", "previousDate",
        "responsibleParty", "actionRequired", "dueDate", "dueDateText", "sourceDate", "calculationRule", "wording",
        "name", "role", "representative", "dateOfBirth", "dateOfBirthText", "orderType", "madeBy", "contactOutcome",
        "recipient", "reference", "organisation",
        // 0.7: complaints (deadline for a reply, and whether this is the reply), contact details for professionals,
        // and Scott schedule allegations
        "responseDue", "responseDueText", "isResponse", "contactDetails", "allegedBy", "findingSought"
    };

    private static JsonObject Str() => new() { ["type"] = "string" };
    private static JsonObject StrOrNull() => new() { ["type"] = new JsonArray("string", "null") };
    private static JsonObject Num() => new() { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };
    private static JsonObject IntOrNull() => new() { ["type"] = new JsonArray("integer", "null") };
    private static JsonObject Enum(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
    private static JsonArray Req(params string[] names) => new(names.Select(n => (JsonNode)n).ToArray());

    /// <summary>
    /// The JSON Schema sent to Ollama as the required output format. The category must be one of the
    /// short keys supplied for this document (K1, K2…) or UNKNOWN, so the model can't make one up.
    /// </summary>
    public static JsonObject Build(IReadOnlyCollection<string> categoryKeys, int maxFindings = 12)
    {
        var details = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        foreach (var k in DetailKeys)
            ((JsonObject)details["properties"])[k] = k == "hearingStatus" ? Enum(HearingStatuses) : StrOrNull();
        ((JsonObject)details["properties"])["dueDateExplicit"] = new JsonObject { ["type"] = new JsonArray("boolean", "null") };

        var finding = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["recordType"] = Enum(FindingKinds.Extractable),
                ["description"] = Str(),
                ["assertedBy"] = StrOrNull(),
                ["page"] = IntOrNull(),
                ["pageTo"] = IntOrNull(),
                ["excerpt"] = Str(),
                ["confidence"] = Num(),
                ["reason"] = Str(),
                ["date"] = StrOrNull(),
                ["dateText"] = StrOrNull(),
                ["datePrecision"] = Enum(Precisions),
                ["dateRole"] = Enum(DateRoles.Keys),
                ["time"] = StrOrNull(),
                ["details"] = details
            },
            ["required"] = Req("recordType", "description", "page", "excerpt", "confidence", "reason", "date", "dateText", "datePrecision", "dateRole")
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["documentSummary"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["documentType"] = StrOrNull(), ["documentTitle"] = StrOrNull(), ["documentDate"] = StrOrNull(),
                        ["documentDateText"] = StrOrNull(), ["caseNumber"] = StrOrNull(), ["courtName"] = StrOrNull(),
                        ["authorOrIssuer"] = StrOrNull(), ["recipient"] = StrOrNull(), ["summary"] = Str(), ["confidence"] = Num()
                    },
                    ["required"] = Req("documentType", "documentTitle", "documentDate", "documentDateText", "caseNumber", "courtName",
                        "authorOrIssuer", "recipient", "summary", "confidence")
                },
                ["categorySuggestion"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["categoryId"] = Enum(categoryKeys.Append("UNKNOWN")),
                        ["confidence"] = Num(),
                        ["reason"] = Str(),
                        ["alternativeCategoryIds"] = new JsonObject { ["type"] = "array", ["items"] = Enum(categoryKeys.Append("UNKNOWN")) }
                    },
                    ["required"] = Req("categoryId", "confidence", "reason", "alternativeCategoryIds")
                },
                ["findings"] = new JsonObject { ["type"] = "array", ["items"] = finding, ["maxItems"] = maxFindings }
            },
            ["required"] = Req("documentSummary", "categorySuggestion", "findings")
        };
    }

    // ------------------------------------------------------------------ validation
    /// <summary>
    /// Checks an answer against the schema by hand (never trusting that the server enforced it) and turns
    /// it into typed values. Throws <see cref="AnalysisValidationException"/> when the answer as a whole
    /// is unusable; drops individual findings that are malformed and counts them.
    /// </summary>
    public static RawAnalysis Parse(string json, IReadOnlyCollection<string> categoryKeys, bool tolerateBadCategory = false)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new AnalysisValidationException("AI_EMPTY", "The AI returned nothing.");
        var text = json.Trim();
        // Some models wrap JSON in a Markdown code fence even when told not to
        if (text.StartsWith("```"))
        {
            int first = text.IndexOf('\n');
            int last = text.LastIndexOf("```", StringComparison.Ordinal);
            if (first > 0 && last > first) text = text[(first + 1)..last].Trim();
        }

        JsonNode root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new AnalysisValidationException("AI_INVALID_JSON", "The AI's answer was not valid JSON."); }
        if (root is not JsonObject obj) throw new AnalysisValidationException("AI_SCHEMA", "The AI's answer was not a JSON object.");

        var result = new RawAnalysis();
        if (obj["documentSummary"] is not JsonObject sum) throw new AnalysisValidationException("AI_SCHEMA", "documentSummary is missing.");
        result.Summary = new RawSummary
        {
            DocumentType = OptStr(sum, "documentType"), DocumentTitle = OptStr(sum, "documentTitle"),
            DocumentDate = OptStr(sum, "documentDate"), DocumentDateText = OptStr(sum, "documentDateText"),
            CaseNumber = OptStr(sum, "caseNumber"), CourtName = OptStr(sum, "courtName"),
            AuthorOrIssuer = OptStr(sum, "authorOrIssuer"), Recipient = OptStr(sum, "recipient"),
            Summary = OptStr(sum, "summary") ?? "",
            Confidence = Conf(sum["confidence"]) ?? throw new AnalysisValidationException("AI_SCHEMA", "documentSummary.confidence must be a number from 0 to 1.")
        };

        if (obj["categorySuggestion"] is not JsonObject cat) throw new AnalysisValidationException("AI_SCHEMA", "categorySuggestion is missing.");
        var key = OptStr(cat, "categoryId") ?? "";
        var validKeys = new HashSet<string>(categoryKeys, StringComparer.OrdinalIgnoreCase) { "UNKNOWN" };
        bool invented = !validKeys.Contains(key);
        if (invented && !tolerateBadCategory)
            throw new AnalysisValidationException("AI_BAD_CATEGORY", "The AI chose a category that wasn't offered.");
        result.Category = new RawCategory
        {
            Key = invented ? "UNKNOWN" : key.ToUpperInvariant(),
            WasInvented = invented,
            Confidence = Conf(cat["confidence"]) ?? throw new AnalysisValidationException("AI_SCHEMA", "categorySuggestion.confidence must be a number from 0 to 1."),
            Reason = Clip(OptStr(cat, "reason") ?? "", 300)
        };
        if (cat["alternativeCategoryIds"] is JsonArray alts)
            foreach (var a in alts)
                if (a is JsonValue v && v.TryGetValue<string>(out var s) && validKeys.Contains(s) && !s.Equals(key, StringComparison.OrdinalIgnoreCase))
                    result.Category.Alternatives.Add(s.ToUpperInvariant());

        if (obj["findings"] is not JsonArray findings) throw new AnalysisValidationException("AI_SCHEMA", "findings is missing.");
        foreach (var node in findings.Take(80))
        {
            var f = ParseFinding(node as JsonObject);
            if (f == null) result.Dropped++;
            else result.Findings.Add(f);
        }
        if (findings.Count > 80) result.Dropped += findings.Count - 80;
        if (result.Findings.Count == 0 && result.Dropped > 0)
            throw new AnalysisValidationException("AI_SCHEMA", "None of the AI's findings matched the required format.");
        return result;
    }

    private static RawFinding ParseFinding(JsonObject o)
    {
        if (o == null) return null;
        var type = OptStr(o, "recordType");
        if (type == null || !FindingKinds.Extractable.Contains(type)) return null;
        var description = OptStr(o, "description");
        if (string.IsNullOrWhiteSpace(description)) return null;
        var confidence = Conf(o["confidence"]);
        if (confidence == null) return null;
        var f = new RawFinding
        {
            RecordType = type,
            Description = Clip(description.Trim(), 300),
            AssertedBy = Clip(OptStr(o, "assertedBy"), 80),
            Excerpt = Clip(OptStr(o, "excerpt") ?? "", 400),
            Reason = Clip(OptStr(o, "reason") ?? "", 300),
            Confidence = confidence.Value,
            Date = OptStr(o, "date"),
            DateText = Clip(OptStr(o, "dateText"), 80),
            DatePrecision = OptStr(o, "datePrecision") is { } p && Precisions.Contains(p) ? p : "unknown",
            DateRole = OptStr(o, "dateRole") is { } r && DateRoles.Keys.Contains(r) ? r : "mentioned",
            Time = Clip(OptStr(o, "time"), 20),
            Page = OptInt(o["page"]),
            PageTo = OptInt(o["pageTo"])
        };
        if (o["details"] is JsonObject details)
        {
            foreach (var k in DetailKeys)
                if (OptStr(details, k) is { } v && !string.IsNullOrWhiteSpace(v)) f.Details[k] = Clip(v.Trim(), 300);
            if (details["dueDateExplicit"] is JsonValue dv && dv.TryGetValue<bool>(out var b)) f.DueDateExplicit = b;
        }
        return f;
    }

    private static string OptStr(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? OptInt(JsonNode n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9 && d is > 0 and < 100000) return (int)d;
        return null;
    }

    private static double? Conf(JsonNode n)
    {
        if (n is not JsonValue v || !v.TryGetValue<double>(out var d) || double.IsNaN(d) || double.IsInfinity(d)) return null;
        return d is < 0 or > 1 ? null : d;
    }

    public static string Clip(string s, int n) => s == null ? null : s.Length > n ? s[..n].TrimEnd() + "…" : s;

    // ------------------------------------------------------------------ fingerprints
    /// <summary>
    /// A stable key for "the same finding": same kind of record, same date, and the same key words. Used
    /// to merge repeats from different parts of one document and to spot the same event in several documents.
    /// </summary>
    public static string Fingerprint(string kind, DateTime? date, string text)
    {
        var words = CategoryMatcher.Words(text).OrderBy(w => w, StringComparer.Ordinal).Take(8);
        var key = $"{KindFamily(kind)}|{date:yyyy-MM-dd}|{string.Join(" ", words)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
    }

    /// <summary>Kinds that describe the same sort of thing are compared with each other.</summary>
    public static string KindFamily(string kind) => kind switch
    {
        FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry => "event",
        FindingKinds.Direction or FindingKinds.Deadline or FindingKinds.Task => "action",
        FindingKinds.Party or FindingKinds.Child => "person",
        _ => kind
    };

    /// <summary>How alike two descriptions are, 0 to 1, by shared key words.</summary>
    public static double Similarity(string a, string b)
    {
        var wa = CategoryMatcher.Words(a);
        var wb = CategoryMatcher.Words(b);
        if (wa.Count == 0 || wb.Count == 0) return 0;
        int shared = wa.Count(wb.Contains);
        return (double)shared / (wa.Count + wb.Count - shared);
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
}

/// <summary>Reading dates the way they are written in England and Wales (day before month).</summary>
public static class UkDates
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private static readonly string Months = "january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec";

    /// <summary>
    /// Reads a date written in a document. Precision says how much of it was stated: "exact" (day, month and
    /// year), "month" or "year". Ambiguous is true for all-number dates where day and month could swap
    /// (e.g. 03/04/2025), which are read day-first, as UK documents are.
    /// </summary>
    public static bool TryParse(string text, out DateTime date, out string precision, out bool ambiguous)
    {
        date = default;
        precision = "unknown";
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
        t = Regex.Replace(t, @"\b(\d{1,2})(st|nd|rd|th)\b", "$1");
        t = Regex.Replace(t, @"\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun),?\s*", "");
        t = t.Replace(" of ", " ").Replace(",", " ").Trim();
        t = Regex.Replace(t, @"\s+", " ");

        Match m;
        if ((m = Regex.Match(t, @"\b(\d{4})-(\d{2})-(\d{2})\b")).Success &&
            TryMake(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out date))
        { precision = "exact"; return true; }

        if ((m = Regex.Match(t, $@"\b(\d{{1,2}}) ({Months})\.? (\d{{4}})\b")).Success &&
            TryMake(int.Parse(m.Groups[3].Value), MonthNumber(m.Groups[2].Value), int.Parse(m.Groups[1].Value), out date))
        { precision = "exact"; return true; }

        if ((m = Regex.Match(t, $@"\b({Months})\.? (\d{{1,2}}) (\d{{4}})\b")).Success &&
            TryMake(int.Parse(m.Groups[3].Value), MonthNumber(m.Groups[1].Value), int.Parse(m.Groups[2].Value), out date))
        { precision = "exact"; return true; }

        if ((m = Regex.Match(t, @"\b(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{2}|\d{4})\b")).Success)
        {
            int d = int.Parse(m.Groups[1].Value), mo = int.Parse(m.Groups[2].Value), y = int.Parse(m.Groups[3].Value);
            if (y < 100) y += 2000;
            if (TryMake(y, mo, d, out date))
            {
                precision = "exact";
                ambiguous = d <= 12 && mo <= 12 && d != mo;
                return true;
            }
            return false;
        }

        if ((m = Regex.Match(t, $@"\b({Months})\.? (\d{{4}})\b")).Success && TryMake(int.Parse(m.Groups[2].Value), MonthNumber(m.Groups[1].Value), 1, out date))
        { precision = "month"; return true; }

        if ((m = Regex.Match(t, @"^(?:in |during |about |around |circa |c\. )?((?:19|20)\d{2})$")).Success && TryMake(int.Parse(m.Groups[1].Value), 1, 1, out date))
        { precision = "year"; return true; }

        return false;
    }

    private static int MonthNumber(string m) => m[..3] switch
    {
        "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
        "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12, _ => 0
    };

    private static bool TryMake(int y, int m, int d, out DateTime date)
    {
        date = default;
        if (y is < 1900 or > 2100 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateTime(y, m, d);
        return true;
    }

    /// <summary>Strict ISO yyyy-MM-dd, the only form the schema allows in date fields.</summary>
    public static DateTime? ParseIso(string s) =>
        s != null && DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year is >= 1900 and <= 2100 ? d : null;

    /// <summary>The ways a given day is commonly written in UK documents, for checking a date really appears in the text.</summary>
    public static IEnumerable<string> Spellings(DateTime d)
    {
        yield return d.ToString("d MMMM yyyy", Uk);
        yield return d.ToString("d MMM yyyy", Uk);
        yield return d.ToString("dd/MM/yyyy", Uk);
        yield return d.ToString("d/M/yyyy", Uk);
        yield return d.ToString("dd/MM/yy", Uk);
        yield return d.ToString("dd.MM.yyyy", Uk);
        yield return d.ToString("dd-MM-yyyy", Uk);
        yield return d.ToString("yyyy-MM-dd", Uk);
        yield return d.ToString("MMMM d yyyy", Uk);
    }
}

/// <summary>
/// The text of one document by page, with a normalised copy for matching. Every finding has to be
/// traceable back to this: the quoted excerpt must appear on the page cited, and any date must
/// actually be written in the document.
/// </summary>
public sealed class SourcePages
{
    private readonly Dictionary<int, string> _normal = new();
    public IReadOnlyDictionary<int, string> Pages { get; }

    public SourcePages(IReadOnlyDictionary<int, string> pages)
    {
        Pages = pages;
        foreach (var (n, t) in pages) _normal[n] = Normalise(t);
    }

    public static string Normalise(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.ToLowerInvariant()
            .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-');
        t = Regex.Replace(t, @"\b(\d{1,2})(st|nd|rd|th)\b", "$1");
        t = Regex.Replace(t, @"[^\p{L}\p{N}/.\-:'£ ]+", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Finds the page an excerpt is on: the cited page if it's really there, otherwise any page in the
    /// range searched. Short excerpts are matched in full; long ones by their opening words, because
    /// models sometimes trim the middle. Null when the words can't be found at all.
    /// </summary>
    public int? Locate(string excerpt, int? citedPage, int firstPage, int lastPage)
    {
        var e = Normalise(excerpt);
        if (e.Length < 8) return null;
        var probe = e.Length > 60 ? e[..60] : e;
        var cut = probe.LastIndexOf(' ');
        if (e.Length > 60 && cut > 30) probe = probe[..cut];
        bool On(int page) => _normal.TryGetValue(page, out var t) && (t.Contains(e) || t.Contains(probe));
        if (citedPage is { } cp && cp >= firstPage && cp <= lastPage && On(cp)) return cp;
        for (int p = firstPage; p <= lastPage; p++) if (On(p)) return p;
        return null;
    }

    /// <summary>True when the date is written in the text on one of the given pages (in any common UK form).</summary>
    public bool DateAppears(DateTime date, string dateText, int firstPage, int lastPage)
    {
        var spellings = UkDates.Spellings(date).Select(Normalise).ToList();
        var stated = Normalise(dateText);
        for (int p = firstPage; p <= lastPage; p++)
        {
            if (!_normal.TryGetValue(p, out var t)) continue;
            if (stated.Length >= 4 && HasWhole(t, stated) && UkDates.TryParse(dateText, out var sd, out _, out _) && sd == date) return true;
            if (spellings.Any(s => HasWhole(t, s))) return true;
        }
        return false;
    }

    public bool TextAppears(string text, int firstPage, int lastPage)
    {
        var n = Normalise(text);
        if (n.Length < 3) return false;
        for (int p = firstPage; p <= lastPage; p++)
            if (_normal.TryGetValue(p, out var t) && HasWhole(t, n)) return true;
        return false;
    }

    /// <summary>
    /// The phrase appears as whole words and numbers: "1 may 2025" doesn't count inside "21 may 2025", "01/05/20"
    /// doesn't count inside "01/05/2025", and "tom" doesn't count inside "bottom".
    /// </summary>
    public static bool HasWhole(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack)) return false;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            int end = i + needle.Length;
            bool startOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]) || !char.IsLetterOrDigit(needle[0]);
            bool endOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]) || !char.IsLetterOrDigit(needle[^1]);
            if (startOk && endOk) return true;
        }
        return false;
    }
}
