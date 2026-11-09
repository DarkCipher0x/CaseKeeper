using System.Globalization;
using System.Text;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>
/// Case memory: what CaseKeeper knows about the case, compiled from the case's own records (case details,
/// parties, hearings, orders, directions, complaints, Scott schedule, timeline, documents and the latest case
/// overview), plus what it has learnt from the person's own decisions and the notes they write for the AI.
///
/// It is stored in the library database (tables CaseMemory and CaseLearning), so it is encrypted whenever the
/// library is protected, is in every backup, and is deleted with the library. Building it is local and makes no
/// AI call. The AI reads it when analysing a document (so it links to what is already known), when writing the
/// case overview and when answering questions about the case. Children's contact details and anyone's contact
/// details are never included.
/// </summary>
public static class CaseMemory
{
    public const string UpdatedSetting = "casememory.updated";
    /// <summary>"0" stops the memory being given to the AI with each document (it is still kept and shown).</summary>
    public const string UseSetting = "casememory.useInAnalysis";

    public static readonly (string Key, string Title)[] Sections =
    {
        ("case", "Case details"), ("people", "People"), ("hearings", "Hearings"), ("orders", "Orders"),
        ("actions", "Open directions, tasks and deadlines"), ("complaints", "Complaints"), ("scott", "Scott schedule"),
        ("timeline", "Recent timeline"), ("documents", "Documents"), ("notes", "Things the AI should know"),
        ("overview", "Latest case overview")
    };

    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private static string D(DateTime? d) => d?.ToString("d MMM yyyy", Uk) ?? "no date";

    // ------------------------------------------------------------------ building the snapshot
    /// <summary>Rebuilds every section from the case records and saves it. Safe to call often (it's quick and quiet).</summary>
    public static IReadOnlyList<CaseMemoryEntry> Rebuild(Database db)
    {
        var entries = new List<CaseMemoryEntry>();
        void Add(string key, IReadOnlyCollection<string> lines, int sources = -1)
        {
            var text = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            entries.Add(new CaseMemoryEntry { Section = key, Text = text, Sources = sources < 0 ? lines.Count : sources, UpdatedAt = DateTime.Now });
        }

        // Case details
        var details = new List<string>();
        void Setting(string label, string key)
        {
            var v = db.GetSetting(key, "");
            if (!string.IsNullOrWhiteSpace(v)) details.Add($"{label}: {Clip(v.Trim(), 600)}");
        }
        Setting("Case", "case.title"); Setting("Type", "case.type"); Setting("Stage", "case.stage"); Setting("Case number", "case.number");
        Setting("Court", "case.court"); Setting("Judge", "case.judge"); Setting("Cafcass reference", "case.cafcassRef");
        Setting("Local authority reference", "case.laRef"); Setting("Issued", "case.issued"); Setting("Summary", "case.summary");
        Add("case", details);

        // People: names, roles, representatives and children's ages. Never contact details.
        var parties = db.GetParties();
        Add("people", parties.Select(p =>
        {
            var child = (p.Role ?? "").StartsWith("Child", StringComparison.Ordinal);
            var bits = new List<string> { $"{p.Role}: {(string.IsNullOrWhiteSpace(p.Name) ? "(no name)" : p.Name)}" };
            if (p.IsMe) bits.Add("the person using CaseKeeper");
            if (!string.IsNullOrWhiteSpace(p.Representative)) bits.Add("represented by " + p.Representative);
            if (child && p.DateOfBirth is { } dob) bits.Add($"born {dob.Year}");
            return string.Join(" · ", bits);
        }).ToList());

        var hearings = db.All<Hearing>().OrderBy(h => h.HearingDate ?? DateTime.MaxValue).ToList();
        Add("hearings", hearings.TakeLast(25).Select(h =>
            $"{D(h.HearingDate)} – {(string.IsNullOrWhiteSpace(h.HearingType) ? "Hearing" : h.HearingType)} – {h.Status}" +
            (string.IsNullOrWhiteSpace(h.Court) ? "" : " – " + h.Court) + (string.IsNullOrWhiteSpace(h.Purpose) ? "" : $" ({Clip(h.Purpose, 100)})")).ToList(), hearings.Count);

        var orders = db.All<CourtOrder>().OrderBy(o => o.OrderDate ?? DateTime.MaxValue).ToList();
        Add("orders", orders.TakeLast(20).Select(o =>
            $"{D(o.OrderDate)} – {(string.IsNullOrWhiteSpace(o.OrderType) ? "Order" : o.OrderType)} ({o.KindText})" +
            (string.IsNullOrWhiteSpace(o.MadeBy) ? "" : " – " + o.MadeBy) + (string.IsNullOrWhiteSpace(o.Summary) ? "" : ": " + Clip(FirstLine(o.Summary), 140))).ToList(), orders.Count);

        var actions = db.All<CaseAction>().Where(a => a.IsOpen).OrderBy(a => a.DueDate ?? DateTime.MaxValue).ToList();
        Add("actions", actions.Take(25).Select(a =>
            $"{(a.DueDate is { } due ? "due " + D(due) : "no date")} – {Clip(a.Title, 140)} ({a.Responsible}){(a.IsOverdue ? " – OVERDUE" : "")}").ToList(), actions.Count);

        var complaints = db.GetComplaints();
        Add("complaints", complaints.Take(20).Select(c =>
            $"{c.Recipient}{(string.IsNullOrWhiteSpace(c.Reference) ? "" : ", ref " + c.Reference)}: {Clip(c.Subject, 120)} – {c.Status}" +
            (c.DateSent is { } s ? ", sent " + D(s) : "") + (c.ResponseDue is { } r ? ", reply due " + D(r) : "")).ToList(), complaints.Count);

        var scott = db.All<ScottItem>().OrderBy(s => s.ItemNo).ToList();
        var scottLines = new List<string>();
        if (scott.Count > 0) scottLines.Add($"{scott.Count} item(s): " + string.Join(", ", scott.GroupBy(s => s.Status).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")));
        scottLines.AddRange(scott.Take(20).Select(s => $"{s.ItemNo}. {s.WhenText} – {s.AllegedBy}: {Clip(s.Allegation, 120)}"));
        Add("scott", scottLines, scott.Count);

        var events = db.GetEvents().OrderBy(e => e.EventDate).ToList();
        Add("timeline", events.TakeLast(40).Select(e => $"{D(e.EventDate)} – {e.EventType} – {Clip(e.Title, 120)}").ToList(), events.Count);

        var docs = db.GetDocuments();
        var categories = db.GetCategories();
        var unknown = CategoryMatcher.Unknown(categories)?.Id;
        var docLines = new List<string> { $"{docs.Count} document(s); {docs.Count(d => d.CategoryId == unknown)} not yet categorised." };
        var groups = docs.GroupBy(d => categories.FirstOrDefault(c => c.Id == d.CategoryId)?.GroupName ?? "Other")
            .OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key}: {g.Count()}");
        docLines.Add("By group: " + string.Join("; ", groups));
        var dated = docs.Where(d => d.DocumentDate.HasValue).Select(d => d.DocumentDate!.Value).ToList();
        if (dated.Count > 0) docLines.Add($"Dated from {D(dated.Min())} to {D(dated.Max())}.");
        var missing = db.All<CaseAction>().Count(a => a.IsOpen && a.Title.StartsWith("Get a copy of:", StringComparison.Ordinal));
        if (missing > 0) docLines.Add($"{missing} document(s) known to be missing.");
        Add("documents", docLines, docs.Count);

        Add("notes", new[] { Note(db) });

        var overview = db.All<AiReport>().Where(r => r.ReportType == "Case overview").OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        Add("overview", overview == null ? Array.Empty<string>() : new[] { $"(written {overview.CreatedAt.ToString("d MMM yyyy", Uk)})", Clip(overview.Output, 3000) });

        db.DeleteQuiet<CaseMemoryEntry>("Id>0");
        db.SaveQuietBatch(entries);
        db.SetSetting(UpdatedSetting, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        return entries;
    }

    /// <summary>The saved snapshot, rebuilt first if there isn't one yet.</summary>
    public static List<CaseMemoryEntry> Load(Database db)
    {
        var saved = db.Query<CaseMemoryEntry>("");
        return saved.Count > 0 ? saved : Rebuild(db).ToList();
    }

    public static DateTime? UpdatedAt(Database db) =>
        DateTime.TryParse(db.GetSetting(UpdatedSetting, ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>
    /// The memory as one block for a prompt, the most useful sections first, clipped to <paramref name="maxChars"/>.
    /// <paramref name="sections"/> limits it to some sections (null = all).
    /// </summary>
    public static string Text(Database db, int maxChars, IReadOnlyCollection<string> sections = null)
    {
        var saved = Load(db).ToDictionary(e => e.Section, e => e.Text);
        var sb = new StringBuilder();
        foreach (var (key, title) in Sections)
        {
            if (sections != null && !sections.Contains(key)) continue;
            if (!saved.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            var block = $"{title.ToUpperInvariant()}:\n{text}\n\n";
            if (sb.Length + block.Length > maxChars)
            {
                var room = maxChars - sb.Length - title.Length - 8;
                if (room > 200) sb.Append($"{title.ToUpperInvariant()}:\n{text[..Math.Min(text.Length, room)]}…\n\n");
                break;
            }
            sb.Append(block);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The short block given to the AI with every document it reads, so it links to what's already known.</summary>
    public static string ForAnalysis(Database db) =>
        Text(db, 2500, new[] { "case", "people", "notes", "hearings", "orders", "complaints" });

    // ------------------------------------------------------------------ the person's notes for the AI
    public static string Note(Database db) =>
        db.Query<CaseLearning>("Kind=$k", ("$k", CaseLearning.NoteKind)).FirstOrDefault()?.Value ?? "";

    public static void SetNote(Database db, string text)
    {
        var existing = db.Query<CaseLearning>("Kind=$k", ("$k", CaseLearning.NoteKind)).FirstOrDefault()
                       ?? new CaseLearning { Kind = CaseLearning.NoteKind, Note = "Your notes for the AI" };
        existing.Value = Clip((text ?? "").Trim(), 4000);
        existing.CreatedAt = DateTime.Now;
        if (existing.Value.Length == 0) { if (existing.Id > 0) db.DeleteQuiet<CaseLearning>("Id=$i", ("$i", existing.Id)); }
        else db.SaveQuiet(existing);
        db.Audit("casememory.note", "case memory", null, "Changed the notes for the AI", $"{existing.Value.Length} characters");
    }

    // ------------------------------------------------------------------ learning from the person's decisions
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "from", "with", "this", "that", "copy", "scan", "scanned", "final", "draft", "new", "old",
        "pdf", "docx", "doc", "jpg", "jpeg", "png", "img", "image", "file", "document", "page", "pages", "version", "letter"
    };

    /// <summary>The distinctive words of a document's file name and title (no numbers, no common words).</summary>
    public static List<string> Signature(CaseDocument doc)
    {
        var text = $"{System.IO.Path.GetFileNameWithoutExtension(doc.OriginalFileName ?? "")} {doc.Title}".ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.Split(text, @"[^a-z]+")
            .Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct().OrderBy(w => w, StringComparer.Ordinal).Take(6).ToList();
    }

    /// <summary>Remembers that documents like this one belong in the category the person chose.</summary>
    public static void LearnCategory(Database db, CaseDocument doc, long categoryId, long? findingId, string categoryName)
    {
        var sig = Signature(doc);
        if (sig.Count < 2) return;
        var pattern = string.Join(' ', sig);
        var existing = db.Query<CaseLearning>("Kind=$k AND Pattern=$p", ("$k", CaseLearning.CategoryKind), ("$p", pattern)).FirstOrDefault()
                       ?? new CaseLearning { Kind = CaseLearning.CategoryKind, Pattern = pattern };
        existing.Value = categoryId.ToString(CultureInfo.InvariantCulture);
        existing.Note = $"Documents like “{Clip(doc.Title, 60)}” go in {categoryName}";
        existing.FindingId = findingId;
        existing.CreatedAt = DateTime.Now;
        db.SaveQuiet(existing);
    }

    /// <summary>The category the person chose for similar documents before, or null.</summary>
    public static long? LearnedCategory(IReadOnlyList<CaseLearning> learnt, CaseDocument doc)
    {
        if (learnt == null || learnt.Count == 0) return null;
        var sig = Signature(doc).ToHashSet(StringComparer.Ordinal);
        if (sig.Count < 2) return null;
        long? best = null;
        double bestScore = 0;
        foreach (var l in learnt.Where(l => l.Kind == CaseLearning.CategoryKind))
        {
            var words = l.Pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) continue;
            double score = words.Count(sig.Contains) / (double)words.Length;
            if (score >= 0.8 && score > bestScore && long.TryParse(l.Value, out var cat)) { best = cat; bestScore = score; }
        }
        return best;
    }

    private static string RejectionPattern(AiFinding f) => $"{f.Kind}|{f.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{f.Title}";

    /// <summary>Remembers a rejected suggestion, so the same one isn't suggested again.</summary>
    public static void LearnRejection(Database db, AiFinding f, string kind = CaseLearning.RejectedKind)
    {
        if (f.Kind is FindingKinds.Category or FindingKinds.Summary or FindingKinds.Conflict || string.IsNullOrWhiteSpace(f.Title)) return;
        var pattern = RejectionPattern(f);
        if (db.Query<CaseLearning>("Kind=$k AND Pattern=$p", ("$k", kind), ("$p", pattern)).Count > 0) return;
        db.SaveQuiet(new CaseLearning
        {
            Kind = kind, Pattern = pattern, Value = f.Kind, FindingId = f.Id,
            Note = $"{FindingKinds.Label(f.Kind)}: “{Clip(f.Title, 80)}”{(f.DateValue is { } d ? " (" + D(d) + ")" : "")}"
        });
    }

    /// <summary>True when the person has already rejected much the same suggestion (same kind and date, similar wording).</summary>
    public static bool WasRejected(IReadOnlyList<CaseLearning> learnt, AiFinding f) => Matches(learnt, f, CaseLearning.RejectedKind);

    /// <summary>True when the person undid much the same thing after it was added automatically: it's suggested, never added by itself.</summary>
    public static bool WasUndone(IReadOnlyList<CaseLearning> learnt, AiFinding f) => Matches(learnt, f, CaseLearning.UndoneKind);

    private static bool Matches(IReadOnlyList<CaseLearning> learnt, AiFinding f, string kind)
    {
        if (learnt == null) return false;
        var date = f.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        foreach (var l in learnt)
        {
            if (l.Kind != kind || l.Value != f.Kind) continue;
            var parts = l.Pattern.Split('|', 3);
            if (parts.Length < 3 || parts[1] != date) continue;
            if (AnalysisSchema.Similarity(parts[2], f.Title) >= 0.75) return true;
        }
        return false;
    }

    public static List<CaseLearning> Learnt(Database db) =>
        db.Query<CaseLearning>("Kind IN ($a,$b,$c)", ("$a", CaseLearning.CategoryKind), ("$b", CaseLearning.RejectedKind), ("$c", CaseLearning.UndoneKind));

    public static void Forget(Database db, CaseLearning item)
    {
        db.DeleteQuiet<CaseLearning>("Id=$i", ("$i", item.Id));
        db.Audit("casememory.forget", "case memory", item.Id, "Forgot something CaseKeeper had learnt", item.KindText);
    }

    /// <summary>Forgets everything learnt from decisions (the person's notes are kept).</summary>
    public static int ForgetAll(Database db)
    {
        int n = db.DeleteQuiet<CaseLearning>("Kind IN ($a,$b,$c)", ("$a", CaseLearning.CategoryKind), ("$b", CaseLearning.RejectedKind), ("$c", CaseLearning.UndoneKind));
        db.Audit("casememory.clear", "case memory", null, "Cleared what CaseKeeper had learnt", $"{n} item(s)");
        return n;
    }

    private static string FirstLine(string s) => (s ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
    private static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s[..n] + "…" : s;
}
