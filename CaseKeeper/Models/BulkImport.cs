using System.Globalization;

namespace CaseKeeper.Models;

// ======================================================================================
// Bulk Case Import: the job, each file found in the chosen folder, and the AI findings
// waiting for review. These live in their own tables (see Database.BulkImport.cs). They are
// working records, not case records: nothing here reaches hearings, the timeline, parties or
// any other part of the case until the person approves it in the Import review screen.
// ======================================================================================

/// <summary>Where a bulk import has got to. Each phase can be resumed after a restart.</summary>
public enum ImportPhase { Scanned = 0, Importing = 1, PreparingText = 2, Analysing = 3, Checking = 4, Review = 5 }

/// <summary>Whether a job is still going.</summary>
public enum ImportJobState { Active = 0, Paused = 1, Cancelled = 2, Completed = 3 }

/// <summary>What happens to a file whose exact contents are already in the library.</summary>
public enum DuplicatePolicy
{
    /// <summary>Leave it out and record which document it matched.</summary>
    SkipAndRecord = 0,
    /// <summary>Import it anyway as a separate document.</summary>
    ImportSeparately = 1,
    /// <summary>Ask about each one in the preview before importing.</summary>
    ReviewEach = 2
}

public enum ImportFileStatus
{
    Pending = 0, Imported = 1, DuplicateSkipped = 2, Failed = 3, Missing = 4,
    Unsupported = 5, Inaccessible = 6, SystemFile = 7, Excluded = 8, ImportedDuplicate = 9
}

public enum TextState { NotStarted = 0, Ready = 1, NoText = 2, Failed = 3, Waiting = 4, NotNeeded = 5 }

public enum AnalysisState
{
    NotStarted = 0, Queued = 1, Running = 2, Done = 3, Failed = 4, NoText = 5,
    /// <summary>Imported fine, but the local AI wasn't available or switched on. Run it later.</summary>
    AwaitingAi = 6,
    /// <summary>The person chose not to analyse this one.</summary>
    Skipped = 7
}

public enum FindingStatus
{
    Proposed = 0, Approved = 1, Rejected = 2, Deferred = 3,
    /// <summary>Applied by the optional advanced rule for very confident categorisations. Still reversible.</summary>
    AutoApplied = 4,
    /// <summary>Replaced by a newer analysis of the same document (the text, prompt or model changed).</summary>
    Superseded = 5,
    /// <summary>Approved, then undone. The record it created was removed.</summary>
    Undone = 6,
    /// <summary>Being approved right now. Left like this only if CaseKeeper closed mid-approval; recovered on the next start.</summary>
    Applying = 7
}

[Entity("ImportJobs", "bulkimport", "bulk import")]
public class ImportJob : IEntity
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    /// <summary>The folder chosen. Kept only in the (encrypted) database so the job can resume; never exported.</summary>
    public string RootPath { get; set; } = "";
    /// <summary>Just the folder's own name, for display.</summary>
    public string RootName { get; set; } = "";
    public string OptionsJson { get; set; } = "";
    public int Phase { get; set; }
    public int State { get; set; }
    public int DuplicateMode { get; set; }
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string AnalysisVersion { get; set; } = "";
    /// <summary>A short code for the last problem (e.g. AI_UNAVAILABLE). Never case content.</summary>
    public string LastErrorCode { get; set; } = "";

    [NotStored] public ImportPhase PhaseValue { get => (ImportPhase)Phase; set => Phase = (int)value; }
    [NotStored] public ImportJobState StateValue { get => (ImportJobState)State; set => State = (int)value; }

    public string CreatedText => CreatedAt.ToString("dd MMM yyyy HH:mm");
    public string PhaseText => PhaseValue switch
    {
        ImportPhase.Scanned => "Scanned, not yet imported",
        ImportPhase.Importing => "Importing",
        ImportPhase.PreparingText => "Reading text",
        ImportPhase.Analysing => "AI analysis",
        ImportPhase.Checking => "Cross-checking documents",
        _ => "Ready for review"
    };
    public string StateText => StateValue switch
    {
        ImportJobState.Paused => "Paused",
        ImportJobState.Cancelled => "Cancelled",
        ImportJobState.Completed => "Finished",
        _ => "In progress"
    };
    public string AuditName => $"Bulk import of “{RootName}” ({CreatedText})";
}

[Entity("ImportFiles", "bulkfile", "bulk import file")]
public class ImportFile : IEntity
{
    public long Id { get; set; }
    public long JobId { get; set; }
    /// <summary>Path relative to the chosen folder, e.g. "Orders\2025\order.pdf".</summary>
    public string RelativePath { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime? SourceModifiedAt { get; set; }
    public DateTime? SourceCreatedAt { get; set; }
    public string ContentHash { get; set; } = "";
    public bool Included { get; set; } = true;
    public int Status { get; set; }
    /// <summary>The library document this file's contents already match, when it's a duplicate.</summary>
    public long? DuplicateOfDocumentId { get; set; }
    /// <summary>Another file in the same folder with identical contents, imported instead of this one.</summary>
    public long? DuplicateOfFileId { get; set; }
    public long? DocumentId { get; set; }
    /// <summary>Category chosen in the preview by mapping the source folder. Null means "Uncategorised for now".</summary>
    public long? MappedCategoryId { get; set; }
    public string MappedTags { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    /// <summary>A plain, non-sensitive explanation such as "The file was removed after the scan".</summary>
    public string ErrorMessage { get; set; } = "";
    public int TextStatus { get; set; }
    public int AnalysisStatus { get; set; }
    /// <summary>Fingerprint of the inputs to the last completed analysis (content, text, prompt, schema, model).</summary>
    public string AnalysisFingerprint { get; set; } = "";
    public int AnalysisAttempts { get; set; }
    public DateTime? AnalysedAt { get; set; }
    public bool ManuallyReviewed { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [NotStored] public ImportFileStatus StatusValue { get => (ImportFileStatus)Status; set => Status = (int)value; }
    [NotStored] public TextState TextValue { get => (TextState)TextStatus; set => TextStatus = (int)value; }
    [NotStored] public AnalysisState AnalysisValue { get => (AnalysisState)AnalysisStatus; set => AnalysisStatus = (int)value; }

    public string FileName => System.IO.Path.GetFileName(RelativePath);
    public string Folder
    {
        get
        {
            var i = RelativePath.LastIndexOfAny(new[] { '\\', '/' });
            return i > 0 ? RelativePath[..i] : "";
        }
    }
    public string TypeText => Extension.TrimStart('.').ToUpperInvariant();
    public string SizeText => SizeBytes < 1024 * 1024 ? $"{Math.Max(1, SizeBytes / 1024)} KB" : $"{SizeBytes / 1024d / 1024d:0.0} MB";
    public string ModifiedText => SourceModifiedAt?.ToString("dd MMM yyyy") ?? "";
    public bool CanImport => StatusValue is ImportFileStatus.Pending;

    public string StatusText => StatusValue switch
    {
        ImportFileStatus.Pending => DuplicateOfDocumentId.HasValue ? "Already in library" : DuplicateOfFileId.HasValue ? "Same as another file here" : "Ready",
        ImportFileStatus.Imported => "Imported",
        ImportFileStatus.ImportedDuplicate => "Imported (duplicate kept)",
        ImportFileStatus.DuplicateSkipped => "Skipped – duplicate",
        ImportFileStatus.Failed => "Failed",
        ImportFileStatus.Missing => "Missing",
        ImportFileStatus.Unsupported => "Unsupported type",
        ImportFileStatus.Inaccessible => "Can't be read",
        ImportFileStatus.SystemFile => "System / temporary file",
        ImportFileStatus.Excluded => "Left out",
        _ => ""
    };

    public string ProposedAction => !Included && StatusValue == ImportFileStatus.Pending ? "Leave out"
        : StatusValue switch
        {
            ImportFileStatus.Pending when DuplicateOfDocumentId.HasValue || DuplicateOfFileId.HasValue => "Duplicate – see duplicate setting",
            ImportFileStatus.Pending => "Import",
            ImportFileStatus.Unsupported or ImportFileStatus.SystemFile or ImportFileStatus.Inaccessible => "Skip",
            _ => StatusText
        };

    public string AnalysisText => AnalysisValue switch
    {
        AnalysisState.Queued => "Waiting for AI",
        AnalysisState.Running => "Analysing…",
        AnalysisState.Done => ManuallyReviewed ? "Analysed · reviewed" : "Analysed",
        AnalysisState.Failed => "Analysis failed",
        AnalysisState.NoText => "No readable text – review by hand",
        AnalysisState.AwaitingAi => "Waiting – AI not available",
        AnalysisState.Skipped => "Not analysed",
        _ => ""
    };

    public string AuditName => $"File “{FileName}”";

    public override string ToString() => $"{RelativePath}: {StatusText}{(AnalysisText.Length > 0 ? ", " + AnalysisText : "")}";
}

[Entity("AiFindings", "finding", "AI finding")]
public class AiFinding : IEntity
{
    public long Id { get; set; }
    public long? JobId { get; set; }
    public long DocumentId { get; set; }
    /// <summary>One of <see cref="FindingKinds"/>.</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>The date the finding is about, when the source states one. Never a guess.</summary>
    public DateTime? DateValue { get; set; }
    /// <summary>The date exactly as written in the document.</summary>
    public string DateText { get; set; } = "";
    public string DatePrecision { get; set; } = "unknown";
    /// <summary>What the date means: hearing listed, document issued, alleged incident… See <see cref="DateRoles"/>.</summary>
    public string DateRole { get; set; } = "";
    public string TimeText { get; set; } = "";
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    /// <summary>The shortest passage that lets the person check the finding. Cleared if the finding is rejected.</summary>
    public string Excerpt { get; set; } = "";
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Who says it ("The applicant states…"). Allegations stay allegations.</summary>
    public string AssertedBy { get; set; } = "";
    /// <summary>Kind-specific details (court, judge, responsible party, category id…) as JSON.</summary>
    public string PayloadJson { get; set; } = "";
    /// <summary>Stable fingerprint used to spot the same finding coming from several parts or documents.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Findings that conflict or duplicate each other share a group key.</summary>
    public string ConflictGroup { get; set; } = "";
    /// <summary>Plain-English warnings: possible duplicate, conflict, excerpt not found, date in the past…</summary>
    public string Warnings { get; set; } = "";
    /// <summary>Notes from CaseKeeper's cross-document checks (duplicates, conflicts, existing records). Recalculated each time.</summary>
    public string CheckNotes { get; set; } = "";
    public string MatchedRecordType { get; set; } = "";
    public long? MatchedRecordId { get; set; }
    public int Status { get; set; }
    public string CreatedRecordType { get; set; } = "";
    public long? CreatedRecordId { get; set; }
    /// <summary>What was there before an approval changed an existing value (e.g. the old category id), for undo.</summary>
    public string PreviousValue { get; set; } = "";
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string AnalysisVersion { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? DecidedAt { get; set; }

    [NotStored] public FindingStatus StatusValue { get => (FindingStatus)Status; set => Status = (int)value; }
    /// <summary>Display only: the document's title, filled in by the screen.</summary>
    [NotStored] public string DocumentTitle { get; set; } = "";

    public bool IsOpen => StatusValue is FindingStatus.Proposed or FindingStatus.Deferred;
    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings) || !string.IsNullOrWhiteSpace(CheckNotes);
    public string AllWarnings => string.Join("\n", new[] { CheckNotes, Warnings }.Where(s => !string.IsNullOrWhiteSpace(s)));
    /// <summary>First warning only, for a list column.</summary>
    public string WarningSummary => AllWarnings.Split('\n').FirstOrDefault()?.Trim() ?? "";

    public string KindText => FindingKinds.Label(Kind);
    public string ConfidenceBand => Confidence >= BulkThresholds.High ? "High" : Confidence >= BulkThresholds.Medium ? "Medium" : "Low";
    public string ConfidenceText => $"{ConfidenceBand} ({Confidence.ToString("0.00", CultureInfo.InvariantCulture)})";
    public string DateDisplay => DateValue.HasValue
        ? DatePrecision switch
        {
            "month" => DateValue.Value.ToString("MMM yyyy"),
            "year" => DateValue.Value.ToString("yyyy"),
            "approximate" => "About " + DateValue.Value.ToString("dd MMM yyyy"),
            _ => DateValue.Value.ToString("dd MMM yyyy")
        } + (string.IsNullOrWhiteSpace(TimeText) ? "" : " " + TimeText)
        : string.IsNullOrWhiteSpace(DateText) ? "—" : DateText;
    public string PageText => PageFrom is null ? "—" : PageTo is { } t && t != PageFrom ? $"pp. {PageFrom}–{t}" : $"p. {PageFrom}";
    public string RoleText => DateRoles.Label(DateRole);
    public string TemporalText => DateValue is null ? "" : DateValue.Value.Date < DateTime.Today ? "Past" : DateValue.Value.Date == DateTime.Today ? "Today" : "Future";

    public string StatusText => StatusValue switch
    {
        FindingStatus.Proposed => "Needs review",
        FindingStatus.Approved => "Approved",
        FindingStatus.Rejected => "Rejected",
        FindingStatus.Deferred => "Deferred",
        FindingStatus.AutoApplied => "Applied by AI rule",
        FindingStatus.Superseded => "Replaced by newer analysis",
        FindingStatus.Undone => "Undone",
        FindingStatus.Applying => "Being approved",
        _ => ""
    };

    /// <summary>A short glyph-free status marker, so status never depends on colour alone.</summary>
    public string StatusMark => StatusValue switch
    {
        FindingStatus.Approved => "✓",
        FindingStatus.AutoApplied => "✓*",
        FindingStatus.Rejected => "✗",
        FindingStatus.Deferred => "…",
        FindingStatus.Superseded or FindingStatus.Undone => "–",
        _ => HasWarnings ? "!" : "?"
    };

    public string ProposedAction => Kind switch
    {
        FindingKinds.Category => "File under: " + Title,
        FindingKinds.Summary => "Document details: " + Title,
        FindingKinds.Hearing => "Add hearing: " + Title,
        FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry => "Add to timeline: " + Title,
        FindingKinds.Order => "Record order: " + Title,
        FindingKinds.Direction => "Add direction: " + Title,
        FindingKinds.Task => "Add task: " + Title,
        FindingKinds.Deadline => "Add deadline: " + Title,
        FindingKinds.Contact => "Log contact: " + Title,
        FindingKinds.Party => "Add party: " + Title,
        FindingKinds.Child => "Add child: " + Title,
        FindingKinds.Complaint => "Add complaint: " + Title,
        FindingKinds.Safeguarding => "Safeguarding note: " + Title,
        FindingKinds.MissingDocument => "Missing document: " + Title,
        FindingKinds.Conflict => "Check: " + Title,
        _ => "Check: " + Title
    };

    public string AuditName => $"{KindText}: {Title}";

    /// <summary>What a screen reader announces for the row: the suggestion, its date, confidence and status.</summary>
    public override string ToString() =>
        $"{ProposedAction}. {(DateValue.HasValue ? DateDisplay + ". " : "")}{ConfidenceText} confidence. {StatusText}{(HasWarnings ? ". Has warnings" : "")}";
}

/// <summary>The kinds of finding the AI may propose. Each maps to one existing kind of CaseKeeper record (or to a review note).</summary>
public static class FindingKinds
{
    public const string Summary = "summary";
    public const string Category = "category";
    public const string Hearing = "hearing";
    public const string TimelineEvent = "timelineEvent";
    public const string ChronologyEntry = "chronologyEntry";
    public const string Order = "order";
    public const string Direction = "direction";
    public const string Task = "task";
    public const string Deadline = "deadline";
    public const string Contact = "contact";
    public const string Party = "party";
    public const string Child = "child";
    public const string Complaint = "complaint";
    public const string Safeguarding = "safeguarding";
    public const string MissingDocument = "missingDocument";
    public const string Issue = "issueForReview";
    /// <summary>An allegation for the Scott schedule (what is said to have happened, by whom, when).</summary>
    public const string ScottItem = "scottItem";
    /// <summary>Raised by CaseKeeper's own cross-checks (not the AI): conflicting dates, case numbers and so on.</summary>
    public const string Conflict = "conflict";

    /// <summary>Kinds the model is allowed to return in its findings list.</summary>
    public static readonly string[] Extractable =
    {
        Hearing, TimelineEvent, ChronologyEntry, Order, Direction, Task, Deadline, Contact, Party, Child,
        Complaint, Safeguarding, MissingDocument, Issue, ScottItem
    };

    public static string Label(string kind) => kind switch
    {
        Summary => "Document details",
        Category => "Category",
        Hearing => "Hearing",
        TimelineEvent => "Timeline event",
        ChronologyEntry => "Chronology entry",
        Order => "Court order",
        Direction => "Direction",
        Task => "Task",
        Deadline => "Deadline",
        Contact => "Contact",
        Party => "Party",
        Child => "Child",
        Complaint => "Complaint",
        Safeguarding => "Safeguarding",
        MissingDocument => "Missing document",
        Issue => "For review",
        ScottItem => "Scott schedule",
        Conflict => "Conflict",
        _ => kind
    };
}

/// <summary>What a date in a document actually means. Keeps "listed for" apart from "mentioned".</summary>
public static class DateRoles
{
    public static readonly (string Key, string Label)[] All =
    {
        ("documentIssued", "Date of the document"), ("filed", "Filed with the court"), ("served", "Served"),
        ("received", "Received"), ("applicationMade", "Application made"), ("orderMade", "Order made"),
        ("hearingListed", "Hearing listed for"), ("hearingAdjournedFrom", "Hearing adjourned from"),
        ("hearingAdjournedTo", "Hearing adjourned / re-listed to"), ("hearingVacated", "Hearing vacated"),
        ("hearingHeld", "Hearing took place"), ("deadline", "Must be done by"), ("review", "Review date"),
        ("contact", "Contact"), ("appointment", "Appointment"), ("report", "Report date"),
        ("incidentAlleged", "Alleged incident"), ("eventStated", "Event described"), ("complaint", "Complaint"),
        ("correspondence", "Letter / email"), ("birth", "Date of birth"), ("conditional", "Conditional / proposed date"),
        ("recurring", "Recurring arrangement"), ("mentioned", "Date mentioned only")
    };

    public static readonly string[] Keys = All.Select(a => a.Key).ToArray();

    public static string Label(string key) => All.FirstOrDefault(a => a.Key == key).Label ?? (string.IsNullOrEmpty(key) ? "" : key);
}

/// <summary>Confidence bands for AI suggestions. Changeable in the Import review screen.</summary>
public static class BulkThresholds
{
    public static double High { get; set; } = 0.85;
    public static double Medium { get; set; } = 0.6;
}
