using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>The local AI, as the analyser sees it. The app uses Ollama; the tests use a stand-in.</summary>
public interface IAiJsonClient
{
    string ModelName { get; }
    /// <summary>True when the server answers. Never throws.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);
    /// <summary>
    /// Sends one request whose answer must match <paramref name="schema"/>. Throws
    /// <see cref="AiUnavailableException"/> when the server can't be reached, and
    /// <see cref="TimeoutException"/> when it takes too long.
    /// </summary>
    Task<string> CompleteJsonAsync(string system, string user, JsonObject schema, CancellationToken ct);

    /// <summary>True when this PC (or the person) chose the quick categories-only reading.</summary>
    bool CategoriesOnly => false;

    /// <summary>How much of the loaded model sits on the graphics card (1 = all), or null if unknown.</summary>
    Task<double?> GpuShareAsync(CancellationToken ct) => Task.FromResult<double?>(null);

    /// <summary>Switches to a lighter installed model when this PC is struggling. False if there isn't one.</summary>
    bool Lighten() => false;
}

/// <summary>The AI server couldn't be reached (not running, wrong address, connection dropped).</summary>
public sealed class AiUnavailableException : Exception
{
    public AiUnavailableException(string message, Exception inner = null) : base(message, inner) { }
}

/// <summary>The AI server is there but couldn't cope with this request (out of memory). CaseKeeper slows down rather than stopping.</summary>
public sealed class AiOverloadException : Exception
{
    public AiOverloadException(string message) : base(message) { }
}

public sealed class AnalysisSettings
{
    /// <summary>How much document text goes into one request, in characters (from the AI context setting).</summary>
    public int PartChars { get; init; } = 9000;
    /// <summary>Very long documents: only this many parts are analysed, and the review screen says so.</summary>
    public int MaxParts { get; init; } = 40;
    public string Prompt { get; init; } = "";
    /// <summary>Waits between attempts when the AI server isn't answering. Bounded: never retries for ever.</summary>
    public TimeSpan[] RetryDelays { get; init; } = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12) };
    /// <summary>Quick reading: the first part only, for document details and a category, no other findings.</summary>
    public bool CategoriesOnly { get; init; }
    /// <summary>Most findings asked for per part. Shorter answers are much quicker for a local model to write.</summary>
    public int MaxFindingsPerPart { get; init; } = 12;

    public string PromptVersion => "p-" + AnalysisSchema.Hash(Prompt)[..10];
}

public sealed class DocumentAnalysisResult
{
    public bool Ok { get; set; }
    /// <summary>Safe code such as AI_TIMEOUT or AI_INVALID_JSON. Never contains document text.</summary>
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public List<AiFinding> Findings { get; } = new();
    public int PartsAnalysed { get; set; }
    public int PartsTotal { get; set; }
    public int Dropped { get; set; }
    /// <summary>True when CaseKeeper's own rules were certain and the AI wasn't asked.</summary>
    public bool SkippedAi { get; set; }
}

/// <summary>
/// Reads one document with the local AI and turns what it says into findings for review.
///
/// The model is treated as an assistant, never an authority. Its answer must match a strict schema; the
/// category must be one CaseKeeper offered; every finding must quote words that really are on the page
/// it cites; and every date must actually be written in the document. Anything that fails a check is
/// dropped or clearly flagged and its confidence lowered. Nothing here changes the case: findings only
/// become records when the person approves them.
/// </summary>
public sealed class DocumentAnalyzer
{
    private readonly IAiJsonClient _ai;
    private readonly AnalysisSettings _settings;
    private readonly IReadOnlyList<Category> _categories;
    private readonly Func<DateTime> _today;

    public DocumentAnalyzer(IAiJsonClient ai, AnalysisSettings settings, IReadOnlyList<Category> categories, Func<DateTime> today = null)
    {
        _ai = ai;
        _settings = settings;
        _categories = categories;
        _today = today ?? (() => DateTime.Today);
    }

    public sealed record Part(int FirstPage, int LastPage, string Text);

    /// <summary>Quick reading (document details and category only), chosen by the person, this PC's tier, or a step-down.</summary>
    public bool CategoriesOnly => _settings.CategoriesOnly || _ai.CategoriesOnly;

    /// <summary>
    /// Splits a document into parts that fit one request, never splitting a page unless the page alone is
    /// too big, and marking every page so the AI can cite it ("[Page 3]").
    /// </summary>
    public static List<Part> Split(IReadOnlyDictionary<int, string> pages, int budget)
    {
        budget = Math.Max(1500, budget);
        var parts = new List<Part>();
        var sb = new StringBuilder();
        int first = 0, last = 0;
        void Flush()
        {
            if (sb.Length == 0) return;
            parts.Add(new Part(first, last, sb.ToString()));
            sb.Clear();
            first = 0;
        }
        foreach (var (n, raw) in pages.OrderBy(p => p.Key))
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) continue;
            var block = $"[Page {n}]\n{text}\n\n";
            if (sb.Length + block.Length > budget) Flush();
            if (block.Length <= budget)
            {
                if (first == 0) first = n;
                last = n;
                sb.Append(block);
                continue;
            }
            // One enormous page: split it on line breaks, keeping the page marker on every piece
            int pos = 0;
            while (pos < text.Length)
            {
                int len = Math.Min(budget - 40, text.Length - pos);
                if (pos + len < text.Length)
                {
                    int cut = text.LastIndexOf('\n', pos + len, Math.Min(len, 1500));
                    if (cut > pos + 200) len = cut - pos;
                }
                parts.Add(new Part(n, n, $"[Page {n}{(pos > 0 ? " continued" : "")}]\n{text.Substring(pos, len)}\n"));
                pos += len;
            }
        }
        Flush();
        return parts;
    }

    /// <summary>Total characters of real text: under ~25 means the document has nothing to analyse.</summary>
    public static int UsefulChars(IReadOnlyDictionary<int, string> pages) =>
        pages.Values.Sum(t => (t ?? "").Count(ch => !char.IsWhiteSpace(ch)));

    /// <summary>Fingerprint of everything an analysis depends on. If any of it changes, the analysis is out of date.</summary>
    public string InputFingerprint(CaseDocument doc, IReadOnlyDictionary<int, string> pages)
    {
        var textHash = AnalysisSchema.Hash(string.Join("\u001e", pages.OrderBy(p => p.Key).Select(p => p.Value)));
        var depth = CategoriesOnly ? "categories" : "full";
        return AnalysisSchema.Hash($"{doc.FileHash}|{textHash}|{_settings.PromptVersion}|{AnalysisSchema.Version}|{AnalysisSchema.AnalysisVersion}|{_ai.ModelName}|{_settings.PartChars}|{depth}")[..24];
    }

    public async Task<DocumentAnalysisResult> AnalyseAsync(CaseDocument doc, IReadOnlyDictionary<int, string> pages, long? jobId, CancellationToken ct)
    {
        var result = new DocumentAnalysisResult();
        var source = new SourcePages(pages);
        var firstText = pages.OrderBy(p => p.Key).Select(p => p.Value).FirstOrDefault() ?? "";
        var candidates = CategoryMatcher.Candidates(doc.Title, doc.OriginalFileName, firstText, _categories, 10);
        var keys = candidates.Select((c, i) => $"K{i + 1}").ToList();
        // Photos with little writing (receipts, screenshots, pictures) only need a category and a description
        bool quick = CategoriesOnly || IsLightDocument(doc, pages);
        // When a quick reading is all that's wanted and CaseKeeper's own rules already know the category for certain
        // (a form code in the file name or "Form C100" in the text), the AI has nothing to add: don't ask it
        if (quick && RulesAreSure(candidates) && RuleBasedCategory(doc, pages, _categories, jobId) is { } sure)
        {
            result.Ok = true;
            result.SkippedAi = true;
            result.PartsTotal = 1;
            result.Findings.Add(sure);
            return result;
        }
        var schema = AnalysisSchema.Build(keys, quick ? 0 : _settings.MaxFindingsPerPart);

        var catList = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i].Category;
            catList.Append($"{keys[i]}: {c.DisplayName}");
            if (!string.IsNullOrWhiteSpace(c.Description)) catList.Append(" – ").Append(AnalysisSchema.Clip(c.Description, 140));
            catList.Append('\n');
        }
        catList.Append("UNKNOWN: none of these clearly fits\n");

        var header = $"DOCUMENT TITLE: {doc.Title}\nFILE NAME: {doc.OriginalFileName}\nTODAY'S DATE: {_today():d MMMM yyyy}\n\n" +
                     $"CANDIDATE CATEGORIES (choose categoryId from these keys only):\n{catList}\n";
        int budget = Math.Min(_settings.PartChars - _settings.Prompt.Length - header.Length - 300, 12000);
        var parts = Split(pages, budget);
        result.PartsTotal = parts.Count;
        // Quick reading only needs the start of a document; full reading stops at the size limit
        if (quick) parts = parts.Take(1).ToList();
        else if (parts.Count > _settings.MaxParts) parts = parts.Take(_settings.MaxParts).ToList();

        var merged = new Dictionary<string, AiFinding>();
        AiFinding summary = null, category = null;
        int unavailable = 0;

        for (int i = 0; i < parts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var part = parts[i];
            var user = header + (quick
                ? "QUICK READING: fill in documentSummary and categorySuggestion only; findings must be an empty list.\n\n"
                : $"List at most {_settings.MaxFindingsPerPart} findings for this part, the most important first, with a one-sentence reason each.\n\n") +
                $"TEXT (part {i + 1} of {parts.Count}, pages {part.FirstPage}–{part.LastPage}):\n{part.Text}";
            RawAnalysis raw = null;
            string failCode = null;
            int invalid = 0, timeouts = 0;
            while (raw == null)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var reminder = invalid > 0
                        ? "\n\nIMPORTANT: your previous answer was rejected because it did not match the required JSON format or used a category key that was not offered. Reply with JSON only, exactly as specified."
                        : "";
                    var json = await _ai.CompleteJsonAsync(_settings.Prompt, user + reminder, schema, ct);
                    // On the last allowed attempt, a made-up category is ignored rather than throwing the rest away
                    raw = AnalysisSchema.Parse(json, keys, tolerateBadCategory: invalid >= 1);
                }
                catch (AiUnavailableException)
                {
                    if (unavailable >= _settings.RetryDelays.Length) throw;
                    await Task.Delay(_settings.RetryDelays[unavailable++], ct);
                }
                catch (TimeoutException)
                {
                    if (++timeouts >= 2) { failCode = "AI_TIMEOUT"; break; }
                }
                catch (AiOverloadException)
                {
                    // No point retrying the same request at the same size: let the runner step down
                    failCode = "AI_OVERLOAD";
                    break;
                }
                catch (AnalysisValidationException ex)
                {
                    if (++invalid >= 2) { failCode = ex.Code; break; }
                }
            }
            if (raw == null)
            {
                // A part that can't be read is a failure for the whole document: half an analysis would mislead
                result.Ok = false;
                result.ErrorCode = failCode ?? "AI_FAILED";
                result.ErrorMessage = failCode switch
                {
                    "AI_TIMEOUT" => "The AI took too long to answer. Try a smaller context size or a faster model, then retry.",
                    "AI_INVALID_JSON" => "The AI's answer wasn't in the required format, twice. Retry, or try a different model.",
                    "AI_BAD_CATEGORY" => "The AI kept choosing a category that doesn't exist.",
                    "AI_OVERLOAD" => "The AI couldn't cope with this document (out of memory, or the AI service was busy). CaseKeeper has switched to gentler settings; retry it.",
                    "AI_REQUEST" => "The AI server couldn't handle this document. Retry it, or try a different model.",
                    _ => "The AI's answer didn't match what CaseKeeper needs, twice. Retry, or try a different model."
                };
                return result;
            }

            result.PartsAnalysed++;
            result.Dropped += raw.Dropped;
            if (i == 0 || (summary == null && !string.IsNullOrWhiteSpace(raw.Summary.Summary)))
                summary ??= BuildSummary(doc, raw.Summary, source, part, jobId);
            var cat = BuildCategory(doc, raw.Category, candidates, keys, source, jobId);
            if (category == null || cat.Confidence > category.Confidence + 0.05) category = cat;

            foreach (var f in quick ? Enumerable.Empty<RawFinding>() : raw.Findings)
            {
                var built = BuildFinding(doc, f, source, part, jobId, out var drop);
                if (drop) { result.Dropped++; continue; }
                if (merged.TryGetValue(built.Fingerprint, out var existing))
                {
                    // The same thing found again in another part: keep one, widen its page range
                    existing.PageFrom = Min(existing.PageFrom, built.PageFrom);
                    existing.PageTo = Max(existing.PageTo ?? existing.PageFrom, built.PageTo ?? built.PageFrom);
                    if (built.Confidence > existing.Confidence)
                    {
                        existing.Confidence = built.Confidence;
                        existing.Excerpt = built.Excerpt;
                        existing.Reason = built.Reason;
                    }
                    existing.Warnings = JoinWarnings(existing.Warnings, built.Warnings);
                }
                else merged[built.Fingerprint] = built;
            }
        }

        var others = merged.Values.OrderBy(f => f.PageFrom ?? int.MaxValue).ThenBy(f => f.DateValue ?? DateTime.MaxValue).ToList();
        if (summary != null) CheckDocumentDate(summary, others);
        if (category != null && IsUnknownCategory(category))
            foreach (var f in others)
                f.Warnings = JoinWarnings(f.Warnings, "The AI couldn't match this document to the case's papers – check this is relevant before adding it.");
        if (summary != null) result.Findings.Add(summary);
        if (category != null) result.Findings.Add(category);
        result.Findings.AddRange(others);
        // A suggestion with anything to check is never shown as "high" confidence, whatever the model said
        foreach (var f in result.Findings.Where(f => f.Warnings.Length > 0)) f.Confidence = Math.Min(f.Confidence, 0.75);
        if (result.PartsTotal > result.PartsAnalysed && !quick)
        {
            var lastPage = parts[^1].LastPage;
            result.Findings.Add(Stamp(new AiFinding
            {
                DocumentId = doc.Id, JobId = jobId, Kind = FindingKinds.Issue, Confidence = 1,
                Title = $"Only pages 1–{lastPage} were analysed",
                Detail = $"This document is very long, so the AI read the first {result.PartsAnalysed} of {result.PartsTotal} parts. Check the rest yourself.",
                Reason = "Size limit for one document", PageFrom = lastPage
            }, "CaseKeeper"));
        }
        foreach (var f in result.Findings) Stamp(f, _ai.ModelName);
        result.Ok = true;
        return result;
    }

    /// <summary>
    /// True when the file name, title or first pages name the form. Short codes ("C", "E1") must appear as
    /// "Form C" so a stray letter in "s.1(3)(c)" doesn't count.
    /// </summary>
    public static bool MentionsForm(string code, CaseDocument doc, SourcePages source)
    {
        var text = $"{doc.Title} {doc.OriginalFileName} {string.Join(" ", source.Pages.OrderBy(p => p.Key).Take(2).Select(p => p.Value))}".ToLowerInvariant();
        var c = System.Text.RegularExpressions.Regex.Escape(code.ToLowerInvariant()).Replace("\\ ", "[\\s_-]?");
        var pattern = code.Length <= 2 ? $@"\bform\s+{c}($|[^a-z0-9])" : $@"(^|[^a-z0-9]){c}($|[^a-z0-9])";
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern);
    }

    private bool IsUnknownCategory(AiFinding category) =>
        FindingApprover.PLong(category, "categoryId") is not { } id || id == CategoryMatcher.Unknown(_categories)?.Id;

    /// <summary>
    /// A document can't be written in the future, and a "document date" that is really the date of a hearing or
    /// event it mentions is a common small-model mistake. Future dates are removed; borrowed ones are flagged.
    /// </summary>
    private void CheckDocumentDate(AiFinding summary, List<AiFinding> others)
    {
        if (summary.DateValue is not { } d) return;
        if (d.Date > _today())
        {
            summary.DateValue = null;
            summary.DatePrecision = "unknown";
            summary.Warnings = JoinWarnings(summary.Warnings,
                $"The AI gave {d:d MMM yyyy} as the date of the document, but that's in the future – it's probably a date mentioned in it, so it was left blank.");
            return;
        }
        var same = others.FirstOrDefault(o => o.DateValue == d && o.DateRole != "documentIssued");
        if (same != null)
            summary.Warnings = JoinWarnings(summary.Warnings,
                $"{d:d MMM yyyy} is also the date of “{AnalysisSchema.Clip(same.Title, 50)}” in this document. Check it's really the date the document was written.");
    }

    private static readonly System.Text.RegularExpressions.Regex HearingWords = new(
        @"\b(hearing|fhdra|dra|fda|fdr|irh|first appointment|final appointment|listed|re-?listed|adjourn\w*|vacated|trial|before (district |circuit )?judge)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ContactWords = new(
        @"\b(contact|handover|hand-over|handed over|visit\w*|session|spend time|spends time|spending time|supervised|overnight|video call|phone call|collected|collection|pick-?up|drop-?off)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DirectionWords = new(
        @"\b(shall|must|is to|are to|is required to|are required to|is directed to|are directed to)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Small models often pick the wrong kind of record. The wording decides: a "hearing" has to be described as a
    /// hearing, "contact" as contact, and something someone "shall" do is a direction rather than an order. Returns
    /// the corrected kind, or null when the finding should be dropped.
    /// </summary>
    private static string CheckKind(RawFinding r, List<string> warnings)
    {
        var evidence = $"{r.Excerpt} {r.Description} {r.Details.GetValueOrDefault("hearingType")}";
        switch (r.RecordType)
        {
            case FindingKinds.Hearing when !HearingWords.IsMatch(evidence):
                warnings.Add("Suggested as a hearing, but the wording doesn't describe a hearing, so it's shown as a timeline event.");
                return FindingKinds.TimelineEvent;
            case FindingKinds.Order when DirectionWords.IsMatch(r.Excerpt ?? "") && !System.Text.RegularExpressions.Regex.IsMatch(r.Description, @"\border\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase):
                warnings.Add("Shown as a direction: the wording is something someone must do.");
                return FindingKinds.Direction;
            case FindingKinds.Order when !OrderWords.IsMatch(evidence):
                warnings.Add("Suggested as a court order, but the wording doesn't describe an order, so it's shown as a task to check.");
                return FindingKinds.Task;
            case FindingKinds.Contact when !ContactWords.IsMatch(evidence):
                if (string.IsNullOrWhiteSpace(r.Date) && string.IsNullOrWhiteSpace(r.DateText)) return null;
                warnings.Add("Suggested as contact, but the wording isn't about contact, so it's shown as a timeline event.");
                return FindingKinds.TimelineEvent;
            case FindingKinds.Complaint when !ComplaintWords.IsMatch(evidence):
                warnings.Add("Suggested as a complaint, but the wording isn't about a complaint, so it's here for you to check.");
                return FindingKinds.Issue;
            // A timeline or chronology entry with no date at all can't go on a timeline
            case FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry when string.IsNullOrWhiteSpace(r.Date) && string.IsNullOrWhiteSpace(r.DateText):
                return null;
        }
        return r.RecordType;
    }

    private static readonly System.Text.RegularExpressions.Regex RuleWords = new(
        @"\b(within \d{1,3} (clear |working )?(days?|weeks?|months?)( of [a-z' ]{3,40})?|\d{1,3} (clear |working )?days? (before|after) [a-z' ]{3,40})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex OrderWords = new(
        @"\b(order\w*|it is ordered|ordered that|the court (orders|directs)|judgment|undertaking|injunction|penal notice)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ComplaintWords = new(
        @"\b(complain\w*|ombudsman|grievance|formal concern|stage (one|two|1|2)|escalat\w*)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A date's meaning has to fit the kind of record: only hearings are "listed".</summary>
    private static string FitRole(string kind, string role)
    {
        if (kind == FindingKinds.Hearing || !role.StartsWith("hearing", StringComparison.Ordinal)) return role;
        return kind is FindingKinds.Direction or FindingKinds.Deadline or FindingKinds.Task ? "deadline" : "eventStated";
    }

    private AiFinding Stamp(AiFinding f, string model)
    {
        f.Model = string.IsNullOrEmpty(f.Model) ? model : f.Model;
        f.PromptVersion = _settings.PromptVersion;
        f.SchemaVersion = AnalysisSchema.Version;
        f.AnalysisVersion = AnalysisSchema.AnalysisVersion;
        if (string.IsNullOrEmpty(f.Fingerprint)) f.Fingerprint = AnalysisSchema.Fingerprint(f.Kind, f.DateValue, f.Title);
        return f;
    }

    private static int? Min(int? a, int? b) => a == null ? b : b == null ? a : Math.Min(a.Value, b.Value);
    private static int? Max(int? a, int? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);

    public static string JoinWarnings(params string[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).SelectMany(p => p.Split('\n')).Select(s => s.Trim())
            .Where(s => s.Length > 0).Distinct());

    // ------------------------------------------------------------------ turning the answer into findings
    private AiFinding BuildSummary(CaseDocument doc, RawSummary s, SourcePages source, Part part, long? jobId)
    {
        var warnings = new List<string>();
        var payload = new JsonObject();
        string Checked(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (source.TextAppears(value, 1, LastPage(source))) return value.Trim();
            warnings.Add($"The {label} the AI gave isn't written in the text, so it was left out.");
            return null;
        }
        var caseNumber = Checked(s.CaseNumber, "case number");
        var court = Checked(s.CourtName, "court name");
        var author = Checked(s.AuthorOrIssuer, "author");
        var recipient = Checked(s.Recipient, "recipient");
        payload["documentType"] = AnalysisSchema.Clip(s.DocumentType, 80);
        payload["caseNumber"] = caseNumber;
        payload["court"] = court;
        payload["author"] = author;
        payload["recipient"] = recipient;

        var f = new AiFinding
        {
            DocumentId = doc.Id, JobId = jobId, Kind = FindingKinds.Summary,
            Title = AnalysisSchema.Clip(string.IsNullOrWhiteSpace(s.DocumentTitle) ? doc.Title : s.DocumentTitle.Trim(), 140),
            Detail = AnalysisSchema.Clip(s.Summary ?? "", 700),
            Confidence = s.Confidence, Reason = "What the document says about itself",
            DateRole = "documentIssued", PageFrom = part.FirstPage
        };
        ApplyDate(f, s.DocumentDate, s.DocumentDateText, "exact", source, part, warnings);
        f.PayloadJson = payload.ToJsonString();
        f.Warnings = JoinWarnings(warnings.ToArray());
        f.Fingerprint = AnalysisSchema.Fingerprint(f.Kind, null, "summary " + doc.Id);
        return f;
    }

    private AiFinding BuildCategory(CaseDocument doc, RawCategory c, List<CategoryMatcher.Candidate> candidates, List<string> keys,
        SourcePages source, long? jobId)
    {
        var unknown = CategoryMatcher.Unknown(_categories);
        Category chosen = null;
        int idx = keys.FindIndex(k => k.Equals(c.Key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) chosen = candidates[idx].Category;
        var alternatives = c.Alternatives.Select(k => keys.FindIndex(x => x.Equals(k, StringComparison.OrdinalIgnoreCase)))
            .Where(i => i >= 0).Select(i => candidates[i].Category.Id).Distinct().ToList();

        var warnings = new List<string>();
        if (c.WasInvented) warnings.Add("The AI suggested a category that doesn't exist; it was ignored.");
        // A specific court form is only proposed when the document actually identifies that form
        if (chosen != null && !string.IsNullOrWhiteSpace(chosen.Code) && !MentionsForm(chosen.Code, doc, source))
        {
            c.Confidence = Math.Min(c.Confidence, 0.5);
            warnings.Add($"The document doesn't mention form {chosen.Code}, so filing it there needs checking.");
        }
        long? bestGuess = null;
        if (chosen != null && c.Confidence < BulkThresholds.Medium)
        {
            // Low confidence: propose "Uncategorised" for review, keeping the guess as an option
            bestGuess = chosen.Id;
            warnings.Add($"Low confidence. Best guess: {chosen.DisplayName}.");
            chosen = null;
        }
        if (chosen == null && bestGuess == null) warnings.Add("The AI couldn't match this document to a category.");
        var target = chosen ?? unknown;

        var firstLine = (source.Pages.OrderBy(p => p.Key).Select(p => p.Value).FirstOrDefault() ?? "")
            .Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3) ?? "";
        var payload = new JsonObject
        {
            ["categoryId"] = target?.Id,
            ["bestGuessId"] = bestGuess,
            ["alternativeCategoryIds"] = new JsonArray(alternatives.Select(a => (JsonNode)a).ToArray())
        };
        var f = new AiFinding
        {
            DocumentId = doc.Id, JobId = jobId, Kind = FindingKinds.Category,
            Title = target == null ? "Uncategorised" : chosen == null ? target.DisplayName + " (needs review)" : target.DisplayName,
            Confidence = c.Confidence, Reason = string.IsNullOrWhiteSpace(c.Reason) ? "Chosen by the AI from CaseKeeper's categories" : c.Reason,
            Excerpt = AnalysisSchema.Clip(firstLine, 200), PageFrom = firstLine.Length > 0 ? source.Pages.Keys.DefaultIfEmpty(1).Min() : null,
            PayloadJson = payload.ToJsonString(), Warnings = JoinWarnings(warnings.ToArray())
        };
        f.Fingerprint = AnalysisSchema.Fingerprint(f.Kind, null, "category " + doc.Id);
        return f;
    }

    private AiFinding BuildFinding(CaseDocument doc, RawFinding r, SourcePages source, Part part, long? jobId, out bool drop)
    {
        drop = false;
        var warnings = new List<string>();
        var kind = CheckKind(r, warnings);
        if (kind == null) { drop = true; return null; }
        if (kind != r.RecordType)
        {
            r.Confidence = Math.Min(r.Confidence, 0.6);
            r.RecordType = kind;
        }
        r.DateRole = FitRole(kind, r.DateRole);
        var f = new AiFinding
        {
            DocumentId = doc.Id, JobId = jobId, Kind = r.RecordType, Confidence = r.Confidence,
            Reason = r.Reason, AssertedBy = r.AssertedBy?.Trim() ?? "", DateRole = r.DateRole,
            TimeText = NormaliseTime(r.Time)
        };

        // ---- where it comes from: the quote has to be on the page
        var located = source.Locate(r.Excerpt, r.Page, part.FirstPage, part.LastPage);
        if (located is { } page)
        {
            f.PageFrom = page;
            f.PageTo = r.PageTo is { } pt && pt > page && pt <= part.LastPage ? pt : null;
            f.Excerpt = AnalysisSchema.Clip(r.Excerpt.Trim(), 240);
        }
        else
        {
            f.PageFrom = r.Page is { } p && p >= part.FirstPage && p <= part.LastPage ? p : null;
            f.Excerpt = AnalysisSchema.Clip(r.Excerpt?.Trim() ?? "", 240);
            f.Confidence = Math.Min(f.Confidence, 0.35);
            warnings.Add(f.PageFrom == null
                ? "No page could be found for this, and the quoted words aren't in the document. Treat with caution."
                : "The quoted words weren't found on that page. Check the document before relying on this.");
        }

        // ---- names must be in the text too
        var details = new Dictionary<string, string>(r.Details);
        foreach (var key in new[] { "name", "judge", "court", "venue", "representative", "recipient", "madeBy", "organisation", "caseNumber", "reference" })
        {
            if (!details.TryGetValue(key, out var v)) continue;
            if (source.TextAppears(v, 1, LastPage(source))) continue;
            details.Remove(key);
            if (key == "name" && r.RecordType is FindingKinds.Party or FindingKinds.Child)
            {
                drop = true;            // a person the document doesn't name is not a finding
                return null;
            }
            warnings.Add($"Removed a {Friendly(key)} that isn't written in the document.");
        }

        // ---- dates: only ones actually written in the document
        if (r.RecordType is FindingKinds.Deadline or FindingKinds.Direction or FindingKinds.Task)
        {
            // The wording itself shows a rule rather than a date ("within 14 days of service", "7 days before the hearing on
            // 5 June"). Then any date given is what the rule counts from, never the deadline itself.
            var rule = RuleWords.Match($"{r.Excerpt} {details.GetValueOrDefault("wording")}");
            bool isRule = rule.Success || r.DueDateExplicit == false;
            if (isRule)
            {
                if (!details.ContainsKey("calculationRule") && rule.Success) details["calculationRule"] = rule.Value.Trim();
                if (!details.ContainsKey("sourceDate") && (details.GetValueOrDefault("dueDate") ?? r.Date) is { } anchor) details["sourceDate"] = anchor;
                f.DateText = "";
                f.DatePrecision = "unknown";
            }
            else
            {
                var explicitDue = details.GetValueOrDefault("dueDate") ?? (r.DateRole is "deadline" or "hearingListed" ? r.Date : null);
                var dueText = details.GetValueOrDefault("dueDateText") ?? (r.DateRole is "deadline" or "hearingListed" ? r.DateText : null);
                ApplyDate(f, explicitDue, dueText, "exact", source, part, warnings);
                if (f.DateValue != null) f.DateRole = "deadline";
            }
            bool needsCalc = f.DateValue == null && (isRule || details.ContainsKey("calculationRule"));
            if (needsCalc)
            {
                details["needsCalculation"] = "true";
                warnings.Add("The due date has to be worked out from the order's wording. Use the deadline calculator and check it before adding.");
            }
            // The order or source date, kept separately so it's never mistaken for the deadline
            if (details.TryGetValue("sourceDate", out var sd))
            {
                var probe = new AiFinding();
                ApplyDate(probe, sd, null, "exact", source, part, new List<string>());
                if (probe.DateValue is { } ok) details["sourceDate"] = ok.ToString("yyyy-MM-dd");
                else details.Remove("sourceDate");
            }
        }
        else ApplyDate(f, r.Date, r.DateText, r.DatePrecision, source, part, warnings);

        // A time has to be written in the document too (10:30, 10.30am, 2pm…)
        if (f.TimeText.Length > 0 && !TimeAppears(f.TimeText, source))
        {
            warnings.Add($"A time ({f.TimeText}) was suggested but isn't written in the document, so it was left out.");
            f.TimeText = "";
        }
        if (r.RecordType == FindingKinds.Contact && details.TryGetValue("contactOutcome", out var outcome))
            warnings.Add($"“{AnalysisSchema.Clip(outcome, 60)}” is the AI's reading of what happened at contact – check it against the document.");

        // Anyone's date of birth – adult or child – has to be written in the document
        if (r.RecordType is FindingKinds.Child or FindingKinds.Party && details.TryGetValue("dateOfBirth", out var dob))
        {
            var probe = new AiFinding();
            ApplyDate(probe, dob, details.GetValueOrDefault("dateOfBirthText"), "exact", source, part, new List<string>());
            if (probe.DateValue is { } ok) details["dateOfBirth"] = ok.ToString("yyyy-MM-dd");
            else { details.Remove("dateOfBirth"); warnings.Add("A date of birth was suggested but isn't written in the document, so it was left out."); }
        }

        if (r.RecordType == FindingKinds.Hearing)
        {
            var status = details.GetValueOrDefault("hearingStatus") ?? "unknown";
            if (f.DateValue is { } hd && hd.Date < _today() && status is "listed" or "unknown" && f.DateRole != "hearingHeld")
                warnings.Add("This hearing was listed for a date that has passed. The document doesn't show whether it went ahead.");
            if (details.TryGetValue("previousDate", out var prev))
            {
                var probe = new AiFinding();
                ApplyDate(probe, prev, null, "exact", source, part, new List<string>());
                if (probe.DateValue is { } ok) details["previousDate"] = ok.ToString("yyyy-MM-dd");
                else details.Remove("previousDate");
            }
        }

        // ---- wording: allegations stay allegations
        var description = r.Description.Trim();
        if (!string.IsNullOrWhiteSpace(f.AssertedBy) && !description.StartsWith(f.AssertedBy, StringComparison.OrdinalIgnoreCase) &&
            (r.DateRole == "incidentAlleged" || r.RecordType is FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry or FindingKinds.Safeguarding))
            description = $"{f.AssertedBy} states: {char.ToLowerInvariant(description[0])}{description[1..]}";
        if (r.DateRole == "incidentAlleged" && string.IsNullOrWhiteSpace(f.AssertedBy) &&
            !description.StartsWith("It is alleged", StringComparison.OrdinalIgnoreCase))
            description = "The document alleges: " + char.ToLowerInvariant(description[0]) + description[1..];

        f.Title = AnalysisSchema.Clip(description, 140);
        f.Detail = AnalysisSchema.Clip(string.Join("\n", new[]
        {
            description.Length > 140 ? description : null,
            details.GetValueOrDefault("wording") is { } w ? "Wording: " + w : null
        }.Where(s => s != null)), 700);
        f.PayloadJson = JsonSerializer.Serialize(details);
        f.Warnings = JoinWarnings(warnings.ToArray());
        f.Fingerprint = AnalysisSchema.Fingerprint(f.Kind, f.DateValue,
            f.Title + " " + string.Join(" ", details.Where(d => d.Key is "hearingType" or "name").Select(d => d.Value)));
        return f;
    }

    private static int LastPage(SourcePages s) => s.Pages.Count == 0 ? 1 : s.Pages.Keys.Max();

    private static string Friendly(string key) => key switch
    {
        "madeBy" => "judge's name", "caseNumber" => "case number", "organisation" => "organisation", _ => key
    };

    /// <summary>
    /// Sets the finding's date only when it can be checked in the text. The date as written wins over the
    /// AI's reading of it; numbers-only dates are read day-first and flagged.
    /// </summary>
    private void ApplyDate(AiFinding f, string iso, string dateText, string precision, SourcePages source, Part part, List<string> warnings)
    {
        f.DateText = AnalysisSchema.Clip(dateText?.Trim() ?? "", 80);
        if (string.IsNullOrWhiteSpace(iso) && string.IsNullOrWhiteSpace(dateText)) { f.DatePrecision = "unknown"; return; }
        var fromModel = UkDates.ParseIso(iso);
        bool parsed = UkDates.TryParse(dateText, out var fromText, out var textPrecision, out var ambiguous);
        int last = Math.Max(part.LastPage, LastPage(source));

        if (parsed && textPrecision is "month" or "year")
        {
            if (source.TextAppears(dateText, 1, last))
            {
                f.DateValue = fromText;
                f.DatePrecision = precision == "approximate" ? "approximate" : textPrecision;
            }
            else warnings.Add("The date given isn't written in the document, so it was left blank.");
            return;
        }

        DateTime? candidate = parsed ? fromText : fromModel;
        if (candidate is { } d && source.DateAppears(d, dateText, 1, last))
        {
            if (parsed && fromModel is { } m && m != fromText)
                warnings.Add($"The AI read the date as {m:d MMM yyyy}, but the document says “{dateText}”. The date as written has been used.");
            if (ambiguous) warnings.Add($"Written as numbers (“{dateText}”), read the UK way as day/month: {d:d MMMM yyyy}.");
            f.DateValue = d;
            f.DatePrecision = precision == "approximate" ? "approximate" : "exact";
            return;
        }
        f.DateValue = null;
        f.DatePrecision = "unknown";
        if (!source.TextAppears(f.DateText, 1, last)) f.DateText = "";
        warnings.Add("No date that can be checked in the document, so the date was left blank.");
    }

    /// <summary>True when a time (as HH:mm) is written somewhere in the document in a common UK form.</summary>
    public static bool TimeAppears(string hhmm, SourcePages source)
    {
        if (!TimeSpan.TryParse(hhmm, System.Globalization.CultureInfo.InvariantCulture, out var t)) return false;
        int h = t.Hours, m = t.Minutes, h12 = h % 12 == 0 ? 12 : h % 12;
        var ampm = h < 12 ? "am" : "pm";
        var forms = new List<string> { $"{h}:{m:00}", $"{h:00}:{m:00}", $"{h}.{m:00}", $"{h:00}.{m:00}", $"{h12}:{m:00}{ampm}", $"{h12}.{m:00}{ampm}",
            $"{h12}:{m:00} {ampm}", $"{h12}.{m:00} {ampm}" };
        if (m == 0) forms.AddRange(new[] { $"{h12}{ampm}", $"{h12} {ampm}", $"{h12} o'clock" });
        if (h == 12 && m == 0) forms.Add("midday");
        var normal = forms.Select(SourcePages.Normalise).ToList();
        return source.Pages.Values.Select(SourcePages.Normalise).Any(page => normal.Any(f => SourcePages.HasWhole(page, f)));
    }

    private static string NormaliseTime(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(t.Trim().ToLowerInvariant(), @"^(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm)?$");
        if (!m.Success) return "";
        int h = int.Parse(m.Groups[1].Value), min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (m.Groups[3].Value == "pm" && h < 12) h += 12;
        if (m.Groups[3].Value == "am" && h == 12) h = 0;
        return h is >= 0 and < 24 && min < 60 ? $"{h:00}:{min:00}" : "";
    }

    private static readonly string[] PhotoTypes = { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>A photo or picture with little writing on it: a quick reading (category and description) is enough.</summary>
    public static bool IsLightDocument(CaseDocument doc, IReadOnlyDictionary<int, string> pages) =>
        PhotoTypes.Contains(System.IO.Path.GetExtension(doc.OriginalFileName ?? "").ToLowerInvariant()) &&
        pages.Values.Sum(p => p?.Length ?? 0) < 600;

    /// <summary>True when the rules found a form code, with nothing else close behind.</summary>
    public static bool RulesAreSure(IReadOnlyList<CategoryMatcher.Candidate> candidates) =>
        candidates.Count > 0 && candidates[0].Score >= 0.9 && (candidates.Count == 1 || candidates[0].Score - candidates[1].Score >= 0.2);

    // ------------------------------------------------------------------ without AI
    /// <summary>
    /// A category suggestion from CaseKeeper's own rules (form codes, file names, headings), for when the AI
    /// is off or unavailable. Capped below "high" confidence so it is never applied automatically.
    /// </summary>
    public static AiFinding RuleBasedCategory(CaseDocument doc, IReadOnlyDictionary<int, string> pages, IReadOnlyList<Category> categories, long? jobId)
    {
        var firstText = pages.OrderBy(p => p.Key).Select(p => p.Value).FirstOrDefault() ?? "";
        var best = CategoryMatcher.Candidates(doc.Title, doc.OriginalFileName, firstText, categories, 1).FirstOrDefault();
        if (best == null || best.Score < 0.7) return null;
        var firstLine = firstText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3) ?? "";
        var f = new AiFinding
        {
            DocumentId = doc.Id, JobId = jobId, Kind = FindingKinds.Category, Title = best.Category.DisplayName,
            Confidence = Math.Min(0.8, best.Score), Reason = best.Why, Model = "CaseKeeper rules",
            Excerpt = AnalysisSchema.Clip(firstLine, 200), PageFrom = firstLine.Length > 0 ? pages.Keys.DefaultIfEmpty(1).Min() : null,
            PayloadJson = new JsonObject { ["categoryId"] = best.Category.Id, ["alternativeCategoryIds"] = new JsonArray() }.ToJsonString(),
            SchemaVersion = AnalysisSchema.Version, AnalysisVersion = AnalysisSchema.AnalysisVersion, PromptVersion = "rules"
        };
        f.Fingerprint = AnalysisSchema.Fingerprint(f.Kind, null, "category " + doc.Id);
        return f;
    }
}
