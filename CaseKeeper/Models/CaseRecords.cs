using System.Globalization;

namespace CaseKeeper.Models;

/// <summary>Names the table a record is stored in, and how it is described in the audit trail.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EntityAttribute : Attribute
{
    public EntityAttribute(string table, string auditKey, string label)
    {
        Table = table;
        AuditKey = auditKey;
        Label = label;
    }

    public string Table { get; }
    /// <summary>Prefix for audit actions, e.g. "hearing" gives "hearing.create".</summary>
    public string AuditKey { get; }
    /// <summary>What one of these is called in plain English, e.g. "hearing".</summary>
    public string Label { get; }
}

/// <summary>A settable property that is only for display and must not be written to the database.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotStoredAttribute : Attribute { }

/// <summary>A record kept by the generic record store (see Database.Records.cs).</summary>
public interface IEntity
{
    long Id { get; set; }
    /// <summary>A short description for the audit trail and confirmations, e.g. "FHDRA on 12 Mar 2026".</summary>
    string AuditName { get; }
}

internal static class Fmt
{
    public static string Date(DateTime? d) => d?.ToString("dd MMM yyyy") ?? "—";
    public static string Days(DateTime? d)
    {
        if (d is not { } date) return "";
        int days = (date.Date - DateTime.Today).Days;
        return days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            < 0 => $"{-days} days ago",
            _ => $"In {days} days"
        };
    }
}

// ====================================================================== hearings & orders
[Entity("Hearings", "hearing", "hearing")]
public class Hearing : IEntity
{
    public long Id { get; set; }
    public DateTime? HearingDate { get; set; } = DateTime.Today;
    public string Time { get; set; } = "10:00";
    public string HearingType { get; set; } = "";
    public string Court { get; set; } = "";
    public string Judge { get; set; } = "";
    public string Mode { get; set; } = "In person";
    public string TimeEstimate { get; set; } = "";
    public string Status { get; set; } = "Listed";
    public string Purpose { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Notes { get; set; } = "";
    public long? DocumentId { get; set; }
    /// <summary>The timeline entry kept in step with this hearing, so it appears in chronologies.</summary>
    public long? EventId { get; set; }

    public string DateText => Fmt.Date(HearingDate) + (string.IsNullOrWhiteSpace(Time) ? "" : "  " + Time);
    public string WhenText => Fmt.Days(HearingDate);
    public bool IsUpcoming => HearingDate?.Date >= DateTime.Today && Status is "Listed" or "Part-heard";
    public string ShortType => HearingType.Contains('(') && HearingType.EndsWith(')')
        ? HearingType[(HearingType.LastIndexOf('(') + 1)..^1] : HearingType;
    public string AuditName => $"{(string.IsNullOrWhiteSpace(ShortType) ? "Hearing" : ShortType)} on {Fmt.Date(HearingDate)}";

    public static readonly string[] Types =
    {
        "Appeal hearing", "Case Management Hearing (CMH)", "Directions hearing", "Dispute Resolution Appointment (DRA)",
        "Enforcement hearing", "Fact-finding hearing", "Final hearing", "Financial Dispute Resolution (FDR)",
        "First Appointment (FDA)", "First Hearing Dispute Resolution Appointment (FHDRA)",
        "Issues Resolution Hearing (IRH)", "Other", "Return hearing (on notice)", "Review hearing", "Without notice hearing"
    };
    public static readonly string[] Modes = { "In person", "Remote – video (CVP / Teams)", "Remote – telephone", "Hybrid" };
    public static readonly string[] Statuses = { "Listed", "Heard", "Part-heard", "Adjourned", "Vacated", "Judgment reserved" };
}

[Entity("CourtOrders", "order", "court order")]
public class CourtOrder : IEntity
{
    public long Id { get; set; }
    public DateTime? OrderDate { get; set; } = DateTime.Today;
    public string OrderType { get; set; } = "";
    public string MadeBy { get; set; } = "";
    public string Court { get; set; } = "";
    public bool IsInterim { get; set; }
    public bool HasPenalNotice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string Summary { get; set; } = "";
    public long? DocumentId { get; set; }
    public long? HearingId { get; set; }

    public string DateText => Fmt.Date(OrderDate);
    public string ExpiryText => ExpiryDate.HasValue ? Fmt.Date(ExpiryDate) : "";
    public string KindText => (IsInterim ? "Interim" : "Final") + (HasPenalNotice ? " · penal notice" : "");
    public string AuditName => $"{(string.IsNullOrWhiteSpace(OrderType) ? "Order" : OrderType)} of {Fmt.Date(OrderDate)}";

    public static readonly string[] Types =
    {
        "Case management / directions order", "Child Arrangements Order – lives with", "Child Arrangements Order – spends time with",
        "Consent order", "Enforcement order", "Financial order", "Interim Care / Supervision Order", "Non-Molestation Order",
        "Occupation Order", "Other", "Prohibited Steps Order", "Section 91(14) order", "Specific Issue Order"
    };
}

// ====================================================================== directions & tasks
[Entity("CaseActions", "action", "direction or task")]
public class CaseAction : IEntity
{
    public long Id { get; set; }
    public string Kind { get; set; } = "Court direction";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Responsible { get; set; } = "Me";
    public DateTime? DueDate { get; set; }
    public string DueTime { get; set; } = "";
    public string Status { get; set; } = "Outstanding";
    public string Priority { get; set; } = "Normal";
    public DateTime? CompletedDate { get; set; }
    public long? OrderId { get; set; }
    public long? DocumentId { get; set; }
    public string Notes { get; set; } = "";

    public bool IsOpen => Status is "Outstanding" or "In progress";
    public bool IsOverdue => IsOpen && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
    public bool IsDueSoon => IsOpen && DueDate.HasValue && !IsOverdue && (DueDate.Value.Date - DateTime.Today).TotalDays <= 7;
    public string DueText => DueDate.HasValue ? Fmt.Date(DueDate) + (string.IsNullOrWhiteSpace(DueTime) ? "" : " " + DueTime) : "No date";
    public string WhenText => IsOpen ? Fmt.Days(DueDate) : (CompletedDate.HasValue ? "Done " + Fmt.Date(CompletedDate) : Status);
    public string AuditName => $"{Kind}: {Title}";

    public static readonly string[] Kinds = { "Court direction", "My task", "Other party's task", "Reminder" };
    public static readonly string[] Statuses = { "Outstanding", "In progress", "Done", "Done late", "Missed", "Varied by the court", "No longer needed" };
    public static readonly string[] Priorities = { "Normal", "High", "Urgent" };
    public static readonly string[] Responsibles =
    {
        "Me", "Applicant", "Respondent", "Both parties", "Cafcass", "Local authority", "The court", "Expert", "School", "Other"
    };
}

// ====================================================================== contact log
[Entity("ContactSessions", "contact", "contact session")]
public class ContactSession : IEntity
{
    public long Id { get; set; }
    public DateTime? SessionDate { get; set; } = DateTime.Today;
    public string Time { get; set; } = "";
    public string Children { get; set; } = "";
    public string ContactType { get; set; } = "Direct (in person)";
    public string Location { get; set; } = "";
    public string Outcome { get; set; } = "Took place as arranged";
    public int MinutesLate { get; set; }
    public int DurationMinutes { get; set; }
    public bool Supervised { get; set; }
    public bool ConcernsRaised { get; set; }
    public string Notes { get; set; } = "";
    public long? DocumentId { get; set; }

    public string DateText => SessionDate?.ToString("ddd dd MMM yyyy") ?? "—";
    public bool TookPlace => Outcome.StartsWith("Took place", StringComparison.Ordinal);
    public string DurationText => DurationMinutes > 0 ? $"{DurationMinutes / 60}h {DurationMinutes % 60:00}m" : "";
    public string FlagsText => string.Join(" · ", new[]
    {
        Supervised ? "Supervised" : null, MinutesLate > 0 ? $"{MinutesLate} min late" : null, ConcernsRaised ? "Concerns noted" : null
    }.Where(s => s != null));
    public string AuditName => $"Contact on {Fmt.Date(SessionDate)}";

    public static readonly string[] Types =
    {
        "Direct (in person)", "Overnight / staying", "Handover only", "Supervised (contact centre)", "Supported (contact centre)",
        "Video call", "Phone call", "Letters / cards / gifts", "Holiday", "Other"
    };
    public static readonly string[] Outcomes =
    {
        "Took place as arranged", "Took place – late start", "Took place – cut short", "Cancelled by me",
        "Cancelled by the other parent", "Cancelled by agreement", "Cancelled – child unwell", "Other parent did not attend",
        "Child not brought / not handed over", "I could not attend", "Other"
    };
}

// ====================================================================== costs
[Entity("Expenses", "expense", "expense")]
public class Expense : IEntity
{
    public long Id { get; set; }
    public DateTime? ExpenseDate { get; set; } = DateTime.Today;
    public string Category { get; set; } = "Court fee";
    public string Description { get; set; } = "";
    /// <summary>Stored in pence so totals never pick up rounding errors.</summary>
    public long AmountPence { get; set; }
    public string PaidTo { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public bool HelpWithFees { get; set; }
    public bool Recoverable { get; set; }
    public string Reference { get; set; } = "";
    public long? DocumentId { get; set; }

    public decimal Amount => AmountPence / 100m;
    public string AmountText => Money(AmountPence);
    public string DateText => Fmt.Date(ExpenseDate);
    public string AuditName => $"{Category} {AmountText} on {Fmt.Date(ExpenseDate)}";

    public static string Money(long pence) => (pence / 100m).ToString("C", CultureInfo.GetCultureInfo("en-GB"));

    public static readonly string[] Categories =
    {
        "Barrister / counsel", "Childcare", "Contact centre", "Court fee", "Drug / alcohol testing", "Expert report",
        "Legal advice / solicitor", "Lost earnings", "Mediation / MIAM", "Other", "Parking", "Printing & postage",
        "Transcripts", "Travel"
    };
}

// ====================================================================== Scott schedule
[Entity("ScottItems", "scott", "Scott schedule item")]
public class ScottItem : IEntity
{
    public long Id { get; set; }
    public int ItemNo { get; set; }
    public DateTime? IncidentDate { get; set; }
    /// <summary>For allegations without an exact date: "Summer 2024", "Between May and July 2023".</summary>
    public string DateText { get; set; } = "";
    public string AllegedBy { get; set; } = "Applicant";
    public string Allegation { get; set; } = "";
    public string Response { get; set; } = "";
    public string EvidenceRefs { get; set; } = "";
    public string FindingSought { get; set; } = "";
    public string Status { get; set; } = "Alleged";
    public string Notes { get; set; } = "";

    public string WhenText => !string.IsNullOrWhiteSpace(DateText) ? DateText : Fmt.Date(IncidentDate);
    public string AuditName => $"Scott schedule item {ItemNo}";

    public static readonly string[] Statuses = { "Alleged", "Admitted", "Partly admitted", "Denied", "Found proved", "Not proved", "Withdrawn" };
}

// ====================================================================== letters & templates
[Entity("Letterheads", "letterhead", "letterhead")]
public class Letterhead : IEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "My letterhead";
    public string SenderName { get; set; } = "";
    public string AddressLines { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Website { get; set; } = "";
    public string ReferenceLine { get; set; } = "";
    public string FooterText { get; set; } = "Private and confidential";
    public string AccentColor { get; set; } = "#0B7A75";
    public string FontFamily { get; set; } = "Segoe UI";
    public string Layout { get; set; } = "Classic";
    public string LogoAlign { get; set; } = "Right";
    public byte[] LogoPng { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>An imported Word letterhead (.docx), used as the base for Word exports.</summary>
    public byte[] WordTemplate { get; set; }
    public string WordTemplateName { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool HasWordTemplate => WordTemplate is { Length: > 0 };
    public string SummaryText => string.Join(" · ", new[] { Layout, FontFamily, HasWordTemplate ? "Word layout: " + WordTemplateName : null }
        .Where(s => !string.IsNullOrWhiteSpace(s)));
    public string AuditName => $"Letterhead “{Name}”";
    public override string ToString() => Name;

    public static readonly string[] Layouts = { "Classic", "Band", "Minimal" };
    public static readonly string[] Fonts = { "Segoe UI", "Calibri", "Arial", "Georgia", "Times New Roman", "Cambria", "Verdana" };
    public static readonly string[] Alignments = { "Left", "Centre", "Right" };
}

[Entity("DocTemplates", "template", "template")]
public class DocTemplate : IEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Letter";
    public string Description { get; set; } = "";
    public string Body { get; set; } = "";
    public string DefaultSubject { get; set; } = "";
    public string DefaultSalutation { get; set; } = "Dear Sir or Madam";
    public string DefaultSignOff { get; set; } = "Yours faithfully";
    /// <summary>A formal document (statement etc.) rather than a letter: no addresses or sign-off.</summary>
    public bool IsDocument { get; set; }
    public byte[] WordTemplate { get; set; }
    public string WordTemplateName { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public string BuiltInKey { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool HasWordTemplate => WordTemplate is { Length: > 0 };
    public string SourceText => HasWordTemplate ? "Word layout · " + WordTemplateName : IsBuiltIn ? "Built-in" : "Your template";
    public string AuditName => $"Template “{Name}”";
    public override string ToString() => Name;

    public static readonly string[] Kinds = { "Letter", "Statement", "Position statement", "Application / form", "Note", "Other" };
}

[Entity("Letters", "letter", "letter")]
public class LetterDraft : IEntity
{
    public long Id { get; set; }
    public string Title { get; set; } = "New letter";
    public long? TemplateId { get; set; }
    public long? LetterheadId { get; set; }
    public string RecipientName { get; set; } = "";
    public string RecipientAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TheirReference { get; set; } = "";
    public string OurReference { get; set; } = "";
    public DateTime? LetterDate { get; set; } = DateTime.Today;
    public string Salutation { get; set; } = "Dear Sir or Madam";
    public string Body { get; set; } = "";
    public string SignOff { get; set; } = "Yours faithfully";
    public string Status { get; set; } = "Draft";
    public DateTime? SentDate { get; set; }
    /// <summary>When set, the letter is laid out as a formal document (statement etc.) rather than a letter.</summary>
    public bool IsDocument { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string UpdatedText => UpdatedAt.ToString("dd MMM yyyy HH:mm");
    public string AuditName => $"Letter “{Title}”";
    public override string ToString() => Title;

    public static readonly string[] Statuses = { "Draft", "Final", "Sent", "Filed with court", "Archived" };
}

// ====================================================================== AI
[Entity("AiReports", "ai", "AI report")]
public class AiReport : IEntity
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Title { get; set; } = "";
    public string ReportType { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Model { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Output { get; set; } = "";
    public long DurationMs { get; set; }
    public int ChunkCount { get; set; }

    public string CreatedText => CreatedAt.ToString("dd MMM yyyy HH:mm");
    public string AuditName => $"AI report “{Title}”";
    public override string ToString() => Title;
}

// ====================================================================== audit
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Station { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public long? EntityId { get; set; }
    public string Summary { get; set; } = "";
    public string Details { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";

    public string TimeText => At.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss");
    public string ActionGroup => Action.Contains('.') ? Action[..Action.IndexOf('.')] : Action;
    public string ShortHash => Hash.Length >= 12 ? Hash[..12] : Hash;
}

public class AuditVerification
{
    public bool Ok { get; set; }
    public int Checked { get; set; }
    public long? FirstBrokenId { get; set; }
    public string LastHash { get; set; } = "";
    public string Describe() => Ok
        ? $"All {Checked} audit entries check out. Nothing has been altered or removed.\n\nFinal fingerprint: {LastHash}"
        : $"The audit trail has been altered: entry #{FirstBrokenId} doesn't match the record before it. " +
          $"{Checked} entries were checked.";
}
