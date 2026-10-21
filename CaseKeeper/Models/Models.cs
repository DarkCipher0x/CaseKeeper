using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CaseKeeper.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class Category : Observable
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Default bundle section letter (PD 27A style: A-F).</summary>
    public string BundleSection { get; set; } = "F";
    public int SortOrder { get; set; }
    public bool IsCustom { get; set; }

    private int _documentCount;
    public int DocumentCount { get => _documentCount; set => Set(ref _documentCount, value); }

    public string DisplayName => string.IsNullOrWhiteSpace(Code) ? Name : $"{Code} – {Name}";
    public override string ToString() => DisplayName;

    /// <summary>"Appl." or "Resp." when this is one half of an Applicant's/Respondent's pair, else empty.</summary>
    public string SideBadge =>
        Name.EndsWith("(Applicant's)", StringComparison.Ordinal) ? "Appl." :
        Name.EndsWith("(Respondent's)", StringComparison.Ordinal) ? "Resp." : "";

    /// <summary>The display name without the "(Applicant's)" / "(Respondent's)" ending.</summary>
    public string DisplayNameWithoutSide
    {
        get
        {
            if (SideBadge.Length == 0) return DisplayName;
            int i = DisplayName.LastIndexOf(" (", StringComparison.Ordinal);
            return i > 0 ? DisplayName[..i] : DisplayName;
        }
    }
}

/// <summary>A person or organisation in the case: Applicant(s), Respondent(s), the child(ren), Cafcass, etc.</summary>
public class Party : Observable
{
    public long Id { get; set; }

    private string _role = "Applicant";
    public string Role { get => _role; set { if (Set(ref _role, value)) OnPropertyChanged(nameof(DisplayName)); } }

    private string _name = "";
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); } }

    private bool _isMe;
    public bool IsMe { get => _isMe; set { if (Set(ref _isMe, value)) OnPropertyChanged(nameof(DisplayName)); } }

    private string _representative = "";
    /// <summary>Solicitor / barrister / firm, or "Litigant in person".</summary>
    public string Representative { get => _representative; set => Set(ref _representative, value); }

    private string _contact = "";
    public string Contact { get => _contact; set => Set(ref _contact, value); }

    private string _notes = "";
    public string Notes { get => _notes; set => Set(ref _notes, value); }

    private DateTime? _dateOfBirth;
    /// <summary>Mainly for children, so their age can be shown and kept up to date automatically.</summary>
    public DateTime? DateOfBirth { get => _dateOfBirth; set { if (Set(ref _dateOfBirth, value)) OnPropertyChanged(nameof(AgeText)); } }

    public string AgeText
    {
        get
        {
            if (DateOfBirth is not { } dob) return "";
            var today = DateTime.Today;
            int age = today.Year - dob.Year - (today.DayOfYear < dob.DayOfYear ? 1 : 0);
            return age < 0 ? "" : $"Age {age} (born {dob:dd MMM yyyy})";
        }
    }

    public int SortOrder { get; set; }

    private int _documentCount;
    public int DocumentCount { get => _documentCount; set => Set(ref _documentCount, value); }

    public string DisplayName => $"{Role}{(string.IsNullOrWhiteSpace(Name) ? "" : " – " + Name)}{(IsMe ? " (me)" : "")}";
    public string RoleGroup => Role.Contains("Applicant") ? "Applicant" : Role.Contains("Respondent") ? "Respondent" : "Other";
    public override string ToString() => DisplayName;

    public static readonly string[] Roles =
    {
        "Applicant", "Cafcass Officer (FCA)", "Child (subject of proceedings)", "Children's Guardian",
        "First Respondent", "Intervenor", "Local Authority", "McKenzie Friend", "Other", "Respondent",
        "Second Applicant", "Second Respondent", "Third Respondent"
    };
}

public enum OcrStatus { Pending = 0, Done = 1, Failed = 2, NotApplicable = 3, Running = 4 }

public class CaseDocument : Observable
{
    public long Id { get; set; }

    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    private long _categoryId;
    public long CategoryId { get => _categoryId; set => Set(ref _categoryId, value); }

    private long? _partyId;
    /// <summary>The party who filed / produced the document, or who it mainly concerns.</summary>
    public long? PartyId { get => _partyId; set => Set(ref _partyId, value); }

    private string _partyName = "";
    public string PartyName { get => _partyName; set => Set(ref _partyName, value); }

    public string OriginalFileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public string FileExtension { get; set; } = "";
    public string FileHash { get; set; } = "";
    public long FileSize { get; set; }

    private DateTime? _documentDate;
    public DateTime? DocumentDate { get => _documentDate; set { if (Set(ref _documentDate, value)) OnPropertyChanged(nameof(DocumentDateText)); } }

    private DateTime? _dateReceived;
    public DateTime? DateReceived { get => _dateReceived; set => Set(ref _dateReceived, value); }

    private string _author = "";
    /// <summary>Who produced / sent the document (e.g. "Cafcass – J. Smith", "Respondent's solicitor").</summary>
    public string Author { get => _author; set => Set(ref _author, value); }

    private string _tags = "";
    public string Tags { get => _tags; set => Set(ref _tags, value); }

    private string _description = "";
    public string Description { get => _description; set => Set(ref _description, value); }

    private string _notes = "";
    public string Notes { get => _notes; set => Set(ref _notes, value); }

    private bool _isImportant;
    public bool IsImportant { get => _isImportant; set => Set(ref _isImportant, value); }

    private int _pageCount;
    public int PageCount { get => _pageCount; set => Set(ref _pageCount, value); }

    private OcrStatus _ocrStatus;
    public OcrStatus OcrStatus { get => _ocrStatus; set { if (Set(ref _ocrStatus, value)) OnPropertyChanged(nameof(OcrStatusText)); } }

    public DateTime ImportedAt { get; set; }

    private string _exhibitRef = "";
    /// <summary>Exhibit reference for witness statements, e.g. "AB1", "AB2".</summary>
    public string ExhibitRef { get => _exhibitRef; set => Set(ref _exhibitRef, value); }

    /// <summary>When it was moved to Deleted items; null while it's in the library.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Where the file sat inside a folder brought in by Bulk Case Import, relative to the folder chosen
    /// (e.g. "Court orders\2025\order.pdf"). Never the full Windows path. Empty for ordinary imports.
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// The original file's last-modified time, kept as information only. It is never treated as the
    /// document's date unless the person chooses to use it.
    /// </summary>
    public DateTime? SourceModifiedAt { get; set; }

    public string SourceFolder
    {
        get
        {
            if (string.IsNullOrEmpty(SourcePath)) return "";
            var i = SourcePath.LastIndexOfAny(new[] { '\\', '/' });
            return i > 0 ? SourcePath[..i] : "";
        }
    }

    // Display helpers (not stored)
    public string DeletedText => DeletedAt?.ToString("dd MMM yyyy HH:mm") ?? "";
    public string SizeText => FileSize < 1024 * 1024 ? $"{Math.Max(1, FileSize / 1024)} KB" : $"{FileSize / 1024d / 1024d:0.0} MB";
    public string ImportedText => ImportedAt == DateTime.MinValue ? "" : ImportedAt.ToString("dd MMM yyyy");
    private string _categoryName = "";
    public string CategoryName { get => _categoryName; set => Set(ref _categoryName, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string DocumentDateText => DocumentDate?.ToString("dd MMM yyyy") ?? "—";

    public string OcrStatusText => OcrStatus switch
    {
        OcrStatus.Pending => "Waiting for text scan",
        OcrStatus.Running => "Scanning text…",
        OcrStatus.Done => "Text searchable",
        OcrStatus.Failed => "Text scan failed",
        _ => "Not scannable"
    };

    public string FileTypeLabel => FileExtension.TrimStart('.').ToUpperInvariant();

    public bool IsPdf => FileExtension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public bool IsImage => FileKinds.IsImage(FileExtension);
    public bool IsText => FileKinds.IsText(FileExtension);
    public bool IsWord => FileExtension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public CaseDocument Clone() => (CaseDocument)MemberwiseClone();
    public override string ToString() => Title;
}

public static class FileKinds
{
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif" };
    public static readonly string[] TextExtensions = { ".txt", ".csv", ".log", ".md" };

    public static bool IsImage(string ext) => ImageExtensions.Contains(ext.ToLowerInvariant());
    public static bool IsText(string ext) => TextExtensions.Contains(ext.ToLowerInvariant());
    public static bool CanBundle(string ext) => ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || IsImage(ext);
}

public class PageInfo : Observable
{
    public int PageNumber { get; set; }

    private string _note = "";
    public string Note { get => _note; set { if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public string Label => $"Page {PageNumber}";
}

public class TimelineEvent : Observable
{
    public long Id { get; set; }

    private DateTime _eventDate = DateTime.Today;
    public DateTime EventDate { get => _eventDate; set => Set(ref _eventDate, value); }

    private string _time = "";
    public string Time { get => _time; set => Set(ref _time, value); }

    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _description = "";
    public string Description { get => _description; set => Set(ref _description, value); }

    private string _eventType = "Other";
    public string EventType { get => _eventType; set => Set(ref _eventType, value); }

    private long? _documentId;
    public long? DocumentId { get => _documentId; set => Set(ref _documentId, value); }

    private bool _includeInChronology = true;
    public bool IncludeInChronology { get => _includeInChronology; set => Set(ref _includeInChronology, value); }

    private string _location = "";
    public string Location { get => _location; set => Set(ref _location, value); }

    private string _witnesses = "";
    /// <summary>Who saw or heard it, for incidents.</summary>
    public string Witnesses { get => _witnesses; set => Set(ref _witnesses, value); }

    private string _reference = "";
    /// <summary>Police incident / crime number, or another organisation's reference.</summary>
    public string Reference { get => _reference; set => Set(ref _reference, value); }

    // Display helpers
    public bool IsDocumentEntry { get; set; }
    public string ExtraText => string.Join("  ·  ", new[]
    {
        string.IsNullOrWhiteSpace(Location) ? null : "At " + Location,
        string.IsNullOrWhiteSpace(Witnesses) ? null : "Witnesses: " + Witnesses,
        string.IsNullOrWhiteSpace(Reference) ? null : "Ref: " + Reference
    }.Where(s => s != null));
    public string LinkedDocumentTitle { get; set; } = "";
    public string DateText => EventDate.ToString("ddd dd MMM yyyy") + (string.IsNullOrWhiteSpace(Time) ? "" : "  " + Time);
    public bool IsUpcoming => EventDate.Date >= DateTime.Today;
    public int DaysAway => (EventDate.Date - DateTime.Today).Days;
    public string DaysAwayText => DaysAway switch
    {
        0 => "Today",
        1 => "Tomorrow",
        < 0 => $"{-DaysAway} days ago",
        _ => $"In {DaysAway} days"
    };

    public static readonly string[] Types =
    {
        "Cafcass Contact", "Communication", "Complaint", "Contact / Handover", "Deadline", "Domestic Abuse",
        "Filing / Service", "Hearing", "Incident", "Medical", "Missed Contact", "Order Made", "Other", "Police",
        "Safeguarding Concern", "School", "Social Services"
    };
}

public class Complaint : Observable
{
    public long Id { get; set; }

    private string _recipient = "";
    public string Recipient { get => _recipient; set => Set(ref _recipient, value); }

    private string _recipientName = "";
    public string RecipientName { get => _recipientName; set => Set(ref _recipientName, value); }

    private string _reference = "";
    public string Reference { get => _reference; set => Set(ref _reference, value); }

    private string _subject = "";
    public string Subject { get => _subject; set => Set(ref _subject, value); }

    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private DateTime? _dateSent;
    public DateTime? DateSent { get => _dateSent; set => Set(ref _dateSent, value); }

    private DateTime? _responseDue;
    public DateTime? ResponseDue { get => _responseDue; set => Set(ref _responseDue, value); }

    private string _status = "Draft";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _outcome = "";
    public string Outcome { get => _outcome; set => Set(ref _outcome, value); }

    public List<long> DocumentIds { get; set; } = new();

    public string DateSentText => DateSent?.ToString("dd MMM yyyy") ?? "Not sent";
    public string ResponseDueText => ResponseDue?.ToString("dd MMM yyyy") ?? "—";
    public bool IsOverdue => ResponseDue.HasValue && ResponseDue.Value.Date < DateTime.Today
                             && Status is not ("Resolved" or "Closed");

    public static readonly string[] Recipients =
    {
        "Cafcass", "Cafcass Cymru", "Children's Commissioner", "HMCTS (Court Service)",
        "Independent Office for Police Conduct (IOPC)", "Judicial Conduct Investigations Office (JCIO)",
        "Legal Ombudsman", "Local Authority / Children's Services",
        "Local Government & Social Care Ombudsman (LGSCO)", "Member of Parliament (MP)", "Other",
        "Parliamentary & Health Service Ombudsman (PHSO)", "Police", "Social Work England",
        "Solicitors Regulation Authority (SRA)"
    };

    public static readonly string[] Statuses =
    {
        "Acknowledged", "Awaiting Response", "Closed", "Draft", "Escalated", "Resolved", "Response Received", "Sent"
    };
}

public class SearchHit
{
    public long DocumentId { get; set; }
    public int PageNumber { get; set; }
    public string Source { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string DocumentDateText { get; set; } = "";

    public string LocationText => Source switch
    {
        "page" => $"Page {PageNumber} text",
        "pagenote" => $"Your note on page {PageNumber}",
        "notes" => "Your document notes",
        _ => "Title / details"
    };
}

public class BundleItem : Observable
{
    public CaseDocument Document { get; set; }

    private string _section = "F";
    public string Section { get => _section; set => Set(ref _section, value); }

    public string Title => Document?.Title ?? "";
    public string DateText => Document?.DocumentDateText ?? "";
    public int PageCount => Document?.PageCount ?? 0;
    public bool CanInclude => Document != null && FileKinds.CanBundle(Document.FileExtension);
}
