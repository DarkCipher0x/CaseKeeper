using System.Windows;
using System.Windows.Controls;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using CaseKeeper.UI;
using CaseKeeper.Views.Forms;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// The case-management screens: hearings, orders, directions &amp; tasks, contact log, costs and
/// the Scott schedule. Each is a <see cref="RecordListView{T}"/> configured here.
/// </summary>
public static class CaseModules
{
    private static Data.Database Db => App.Library.Db;

    private static IEnumerable<string> PartyNames() =>
        MainWindow.Shell?.Parties.Select(p => string.IsNullOrWhiteSpace(p.Name) ? p.Role : p.Name).Distinct() ?? Enumerable.Empty<string>();

    private static string[] Courts()
    {
        var known = Db.All<Hearing>().Select(h => h.Court).Concat(new[] { Db.GetSetting("case.court", "") })
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        return known.ToArray();
    }

    // ================================================================== hearings
    public static RecordListView<Hearing> Hearings()
    {
        RecordListView<Hearing> view = null;
        view = new RecordListView<Hearing>(new RecordListConfig<Hearing>
        {
            Title = "Hearings",
            Description = "Every hearing in the case: when and where, what kind, who the judge was and what happened. " +
                          "Hearings appear on your timeline and dashboard automatically, with a countdown to the next one.",
            AddLabel = "Add hearing", Noun = "hearing",
            EmptyText = "No hearings yet. Add the first one from your notice of hearing.",
            Create = () => new Hearing { Court = Db.GetSetting("case.court", "") },
            // Upcoming hearings first, soonest at the top; then past hearings, most recent first
            Order = x => x.OrderByDescending(h => h.IsUpcoming)
                .ThenBy(h => h.IsUpcoming ? (h.HearingDate ?? DateTime.MaxValue).Ticks : -(h.HearingDate ?? DateTime.MinValue).Ticks),
            Filters = new[] { "All hearings", "Upcoming", "Past" },
            FilterMatch = (h, f) => f == "Upcoming" ? h.IsUpcoming : !h.IsUpcoming,
            Columns =
            {
                ColumnSpec.Of("When", nameof(Hearing.DateText), -150, bold: true, sort: nameof(Hearing.HearingDate)),
                ColumnSpec.Of("", nameof(Hearing.WhenText), -100, accent: nameof(Hearing.IsUpcoming)),
                ColumnSpec.Of("Type", nameof(Hearing.HearingType), 1.6),
                ColumnSpec.Of("Court", nameof(Hearing.Court), 1),
                ColumnSpec.Of("Judge", nameof(Hearing.Judge), 0.8),
                ColumnSpec.Of("Status", nameof(Hearing.Status), -100),
                ColumnSpec.Of("Outcome", nameof(Hearing.Outcome), 1.6)
            },
            ExportColumns = new()
            {
                ColumnSpec.Of("Date", nameof(Hearing.DateText)), ColumnSpec.Of("Type", nameof(Hearing.HearingType)),
                ColumnSpec.Of("Court", nameof(Hearing.Court)), ColumnSpec.Of("Judge", nameof(Hearing.Judge)),
                ColumnSpec.Of("Attendance", nameof(Hearing.Mode)), ColumnSpec.Of("Status", nameof(Hearing.Status)),
                ColumnSpec.Of("Outcome", nameof(Hearing.Outcome))
            },
            Landscape = true,
            Summary = list =>
            {
                var next = list.Where(h => h.IsUpcoming).OrderBy(h => h.HearingDate).FirstOrDefault();
                return $"{list.Count} hearing(s)" + (next == null ? "" : $"   ·   Next: {next.ShortType} {next.WhenText.ToLowerInvariant()} ({next.DateText})");
            },
            Fields = _ => HearingFields(),
            DocumentOf = h => h.DocumentId,
            AfterSave = SyncHearingEvent,
            AfterDelete = h => { if (h.EventId is { } id) Db.DeleteEvent(id); MainWindow.Shell?.RefreshTimelineAndDashboard(); },
            ExtraMenu = (v, sel) => new object[]
            {
                Ui.Item("Record the order made…", "", () => { if (sel.Count == 1) MainWindow.Shell?.AddOrderForHearing(sel[0]); }, enabled: sel.Count == 1),
                Ui.Item("Add hearing-preparation tasks", "", () => { if (sel.Count == 1) AddPrepTasks(sel[0]); }, enabled: sel.Count == 1),
                Ui.Sub("Mark as", "", Hearing.Statuses.Select(s => (object)Ui.Item(s, null, () => SetStatus(v, sel, s))))
            }
        });
        return view;
    }

    /// <summary>The hearing form. Shared with Import review, so an approved suggestion is edited on the same form.</summary>
    public static FieldSpec[] HearingFields() => new[]
    {
        FieldSpec.Date("Date", nameof(Hearing.HearingDate), required: true),
        FieldSpec.Time("Time", nameof(Hearing.Time)),
        FieldSpec.Choice("Type of hearing", nameof(Hearing.HearingType), Hearing.Types),
        FieldSpec.Choice("Court / hearing centre", nameof(Hearing.Court), Courts()),
        FieldSpec.Text("Judge / magistrates", nameof(Hearing.Judge), half: true),
        FieldSpec.Text("Time estimate", nameof(Hearing.TimeEstimate), half: true, hint: "e.g. 1 hour, 2 days"),
        FieldSpec.Choice("Attendance", nameof(Hearing.Mode), Hearing.Modes, half: true),
        FieldSpec.Choice("Status", nameof(Hearing.Status), Hearing.Statuses, editable: false, half: true),
        FieldSpec.Multi("Purpose / what it's for", nameof(Hearing.Purpose), 60),
        FieldSpec.Multi("Outcome", nameof(Hearing.Outcome), 80, "What was decided. Add the order itself under Orders."),
        FieldSpec.Multi("Notes", nameof(Hearing.Notes), 80),
        FieldSpec.Document("Linked document (notice of hearing, order…)", nameof(Hearing.DocumentId))
    };

    public static FieldSpec[] OrderFields() => new[]
    {
        FieldSpec.Date("Date of order", nameof(CourtOrder.OrderDate), required: true),
        FieldSpec.Date("Expires / ends (if any)", nameof(CourtOrder.ExpiryDate)),
        FieldSpec.Choice("Type of order", nameof(CourtOrder.OrderType), CourtOrder.Types),
        FieldSpec.Text("Made by (judge / magistrates)", nameof(CourtOrder.MadeBy), half: true),
        FieldSpec.Choice("Court", nameof(CourtOrder.Court), Courts(), half: true),
        FieldSpec.Check("Interim order (not final)", nameof(CourtOrder.IsInterim)),
        FieldSpec.Check("Has a penal notice / warning notice attached", nameof(CourtOrder.HasPenalNotice)),
        FieldSpec.Multi("Summary of what it says", nameof(CourtOrder.Summary), 150,
            "Tip: right-click the order afterwards and choose 'Add a direction from this order' for each thing someone must do."),
        FieldSpec.Document("The order document", nameof(CourtOrder.DocumentId))
    };

    public static FieldSpec[] ActionFields() => new[]
    {
        FieldSpec.Text("What needs doing", nameof(CaseAction.Title), required: true),
        FieldSpec.Choice("Kind", nameof(CaseAction.Kind), CaseAction.Kinds, editable: false, half: true),
        FieldSpec.Choice("Who must do it", nameof(CaseAction.Responsible), CaseAction.Responsibles.Concat(PartyNames()).Distinct(), half: true),
        FieldSpec.Date("Due by", nameof(CaseAction.DueDate)),
        FieldSpec.Time("Time (e.g. 4pm deadline)", nameof(CaseAction.DueTime)),
        FieldSpec.Choice("Priority", nameof(CaseAction.Priority), CaseAction.Priorities, editable: false, half: true),
        FieldSpec.Choice("Status", nameof(CaseAction.Status), CaseAction.Statuses, editable: false, half: true),
        FieldSpec.Date("Date done", nameof(CaseAction.CompletedDate)),
        FieldSpec.Multi("Details / exact wording of the direction", nameof(CaseAction.Detail), 90),
        FieldSpec.Multi("Notes", nameof(CaseAction.Notes), 70),
        FieldSpec.Document("Linked document", nameof(CaseAction.DocumentId))
    };

    public static FieldSpec[] ContactFields() => new[]
    {
        FieldSpec.Date("Date", nameof(ContactSession.SessionDate), required: true),
        FieldSpec.Time("Time", nameof(ContactSession.Time)),
        FieldSpec.Text("Children", nameof(ContactSession.Children)),
        FieldSpec.Choice("Type of contact", nameof(ContactSession.ContactType), ContactSession.Types, half: true),
        FieldSpec.Text("Where / handover point", nameof(ContactSession.Location), half: true),
        FieldSpec.Choice("What happened", nameof(ContactSession.Outcome), ContactSession.Outcomes),
        FieldSpec.Int("Minutes late (if any)", nameof(ContactSession.MinutesLate)),
        FieldSpec.Int("Length of contact (minutes)", nameof(ContactSession.DurationMinutes)),
        FieldSpec.Check("Supervised or supported", nameof(ContactSession.Supervised)),
        FieldSpec.Check("I have concerns about this session", nameof(ContactSession.ConcernsRaised)),
        FieldSpec.Multi("Notes (facts only: what was said and done, by whom)", nameof(ContactSession.Notes), 120),
        FieldSpec.Document("Linked document (messages, contact centre notes…)", nameof(ContactSession.DocumentId))
    };

    private static void SetStatus(RecordListView<Hearing> v, IReadOnlyList<Hearing> sel, string status)
    {
        foreach (var h in sel)
        {
            var copy = Ui.Clone(h);
            copy.Status = status;
            Db.Save(copy);
            SyncHearingEvent(copy);
        }
        v.Reload();
    }

    /// <summary>Keeps a timeline entry in step with each hearing, so it shows in chronologies and the dashboard.</summary>
    public static void SyncHearingEvent(Hearing h)
    {
        try { HearingSync.Sync(Db, h); }
        catch (Exception ex) { AppLog.Warn("Hearings", "Couldn't update the timeline entry for a hearing", ex); }
        MainWindow.Shell?.RefreshTimelineAndDashboard();
    }

    /// <summary>A standard checklist of things to do before a hearing, each due a sensible number of days beforehand.</summary>
    public static void AddPrepTasks(Hearing h)
    {
        if (h.HearingDate is not { } date) return;
        var tasks = new (string Title, int DaysBefore)[]
        {
            ("Check the latest order for anything due before this hearing", 21),
            ("Prepare / update my position statement", 7),
            ("Check the bundle (index, page numbers, my documents included)", 5),
            ("Send my position statement to the court and the other party (if directed)", 2),
            ("Confirm attendance details: time, court address or video link", 2),
            ("Print documents and prepare questions / notes", 1),
        };
        foreach (var (title, days) in tasks)
            Db.Save(new CaseAction
            {
                Kind = "My task", Title = title, Responsible = "Me", Status = "Outstanding",
                // Short lead times count working days (so they never land on a weekend or bank holiday)
                DueDate = days <= 5 ? Deadlines.SubtractWorkingDays(date, days) : date.AddDays(-days),
                Notes = $"For the {h.AuditName}"
            });
        MainWindow.Shell?.RefreshAll();
        MessageBox.Show(Application.Current.MainWindow, $"{tasks.Length} preparation tasks were added to Directions & tasks.", "Hearing preparation");
    }

    // ================================================================== orders
    public static RecordListView<CourtOrder> Orders() => new(new RecordListConfig<CourtOrder>
    {
        Title = "Court orders",
        Description = "A register of every order made: what kind, when, by whom, and a summary of what it says. Add the " +
                      "directions in each order to Directions & tasks so you never miss a deadline.",
        AddLabel = "Add order", Noun = "order",
        EmptyText = "No orders recorded yet.",
        Order = x => x.OrderByDescending(o => o.OrderDate),
        Filters = new[] { "All orders", "Interim", "Final", "With penal notice" },
        FilterMatch = (o, f) => f switch { "Interim" => o.IsInterim, "Final" => !o.IsInterim, _ => o.HasPenalNotice },
        Columns =
        {
            ColumnSpec.Of("Date", nameof(CourtOrder.DateText), -110, bold: true, sort: nameof(CourtOrder.OrderDate)),
            ColumnSpec.Of("Type", nameof(CourtOrder.OrderType), 1.5),
            ColumnSpec.Of("Made by", nameof(CourtOrder.MadeBy), 0.8),
            ColumnSpec.Of("Kind", nameof(CourtOrder.KindText), -150),
            ColumnSpec.Of("Expires", nameof(CourtOrder.ExpiryText), -100),
            ColumnSpec.Of("Summary", nameof(CourtOrder.Summary), 2)
        },
        Landscape = true,
        Fields = _ => OrderFields(),
        DocumentOf = o => o.DocumentId,
        ExtraMenu = (v, sel) => new object[]
        {
            Ui.Item("Add a direction from this order…", "", () => { if (sel.Count == 1) MainWindow.Shell?.AddDirectionForOrder(sel[0]); }, enabled: sel.Count == 1)
        }
    });

    // ================================================================== directions & tasks
    public static RecordListView<CaseAction> Actions()
    {
        RecordListView<CaseAction> view = null;
        view = new RecordListView<CaseAction>(new RecordListConfig<CaseAction>
        {
            Title = "Directions & tasks",
            Description = "Everything that has to be done, by whom and by when: directions in court orders, your own to-do list, " +
                          "and things the other side must do. Overdue items turn red and appear on the dashboard.",
            AddLabel = "Add direction or task", Noun = "item",
            EmptyText = "Nothing to do yet. Add the directions from your latest order, or your own tasks.",
            Create = () => new CaseAction { DueDate = DateTime.Today.AddDays(7) },
            Order = x => x.OrderByDescending(a => a.IsOpen).ThenBy(a => a.DueDate ?? DateTime.MaxValue).ThenBy(a => a.Priority == "Urgent" ? 0 : a.Priority == "High" ? 1 : 2),
            Filters = new[] { "Everything", "Still to do", "Overdue", "Due in the next 7 days", "Done", "Court directions", "My tasks", "Other party's tasks" },
            FilterMatch = (a, f) => f switch
            {
                "Still to do" => a.IsOpen,
                "Overdue" => a.IsOverdue,
                "Due in the next 7 days" => a.IsOpen && a.DueDate.HasValue && a.DueDate.Value.Date <= DateTime.Today.AddDays(7),
                "Done" => !a.IsOpen,
                "Court directions" => a.Kind == "Court direction",
                "My tasks" => a.Kind == "My task" || a.Responsible == "Me",
                "Other party's tasks" => a.Kind == "Other party's task",
                _ => true
            },
            Columns =
            {
                ColumnSpec.Of("Due", nameof(CaseAction.DueText), -140, bold: true, danger: nameof(CaseAction.IsOverdue), sort: nameof(CaseAction.DueDate)),
                ColumnSpec.Of("", nameof(CaseAction.WhenText), -110, danger: nameof(CaseAction.IsOverdue), accent: nameof(CaseAction.IsDueSoon)),
                ColumnSpec.Of("What", nameof(CaseAction.Title), 2.2, bold: true),
                ColumnSpec.Of("Who", nameof(CaseAction.Responsible), 0.7),
                ColumnSpec.Of("Kind", nameof(CaseAction.Kind), 0.8),
                ColumnSpec.Of("Priority", nameof(CaseAction.Priority), -80),
                ColumnSpec.Of("Status", nameof(CaseAction.Status), -110)
            },
            Summary = list =>
            {
                int open = list.Count(a => a.IsOpen), overdue = list.Count(a => a.IsOverdue), soon = list.Count(a => a.IsDueSoon);
                return $"{open} still to do   ·   {overdue} overdue   ·   {soon} due within a week   ·   {list.Count(a => !a.IsOpen)} done";
            },
            Fields = _ => ActionFields(),
            DocumentOf = a => a.DocumentId,
            AfterSave = _ => MainWindow.Shell?.RefreshTimelineAndDashboard(),
            AfterDelete = _ => MainWindow.Shell?.RefreshTimelineAndDashboard(),
            ExtraButtons = v => new[]
            {
                Ui.Button("Mark done", "", (_, _) => MarkDone(v, v.SelectedItems)),
                Ui.Button("Deadline calculator", "", (_, _) => new DeadlineCalculatorWindow { Owner = Ui.Owner(v) }.ShowDialog())
            },
            ExtraMenu = (v, sel) => new object[]
            {
                Ui.Item("Mark done today", "", () => MarkDone(v, sel), enabled: sel.Count > 0),
                Ui.Sub("Set status", "", CaseAction.Statuses.Select(s => (object)Ui.Item(s, null, () => SetActionStatus(v, sel, s)))),
                Ui.Sub("Postpone", "", new object[]
                {
                    Ui.Item("by 1 day", null, () => Postpone(v, sel, 1)),
                    Ui.Item("by 1 week", null, () => Postpone(v, sel, 7)),
                    Ui.Item("by 2 weeks", null, () => Postpone(v, sel, 14))
                })
            }
        });
        return view;
    }

    private static void MarkDone(RecordListView<CaseAction> v, IReadOnlyList<CaseAction> sel)
    {
        foreach (var a in sel.Where(a => a.IsOpen))
        {
            var c = Ui.Clone(a);
            c.Status = c.DueDate.HasValue && c.DueDate.Value.Date < DateTime.Today ? "Done late" : "Done";
            c.CompletedDate = DateTime.Today;
            Db.Save(c);
        }
        v.Reload();
        MainWindow.Shell?.RefreshTimelineAndDashboard();
    }

    private static void SetActionStatus(RecordListView<CaseAction> v, IReadOnlyList<CaseAction> sel, string status)
    {
        foreach (var a in sel)
        {
            var c = Ui.Clone(a);
            c.Status = status;
            if (!c.IsOpen && c.CompletedDate == null) c.CompletedDate = DateTime.Today;
            Db.Save(c);
        }
        v.Reload();
        MainWindow.Shell?.RefreshTimelineAndDashboard();
    }

    private static void Postpone(RecordListView<CaseAction> v, IReadOnlyList<CaseAction> sel, int days)
    {
        foreach (var a in sel.Where(a => a.DueDate.HasValue))
        {
            var c = Ui.Clone(a);
            c.DueDate = c.DueDate!.Value.AddDays(days);
            Db.Save(c);
        }
        v.Reload();
    }

    // ================================================================== contact log
    public static RecordListView<ContactSession> Contact() => new(new RecordListConfig<ContactSession>
    {
        Title = "Contact log",
        Description = "A factual record of each time the children were due to see the other parent (or you): whether it happened, " +
                      "lateness, cancellations and any concerns. Courts and Cafcass often ask for exactly this. Keep entries brief and neutral.",
        AddLabel = "Log contact", Noun = "contact session",
        EmptyText = "No contact logged yet. Add each session soon after it happens, while it's fresh.",
        Create = () => new ContactSession
        {
            Children = string.Join(", ", MainWindow.Shell?.Parties.Where(p => p.Role.StartsWith("Child")).Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)) ?? Enumerable.Empty<string>())
        },
        Order = x => x.OrderByDescending(c => c.SessionDate),
        Filters = new[] { "All", "Took place", "Did not take place", "Concerns noted", "Last 30 days", "Last 90 days" },
        FilterMatch = (c, f) => f switch
        {
            "Took place" => c.TookPlace,
            "Did not take place" => !c.TookPlace,
            "Concerns noted" => c.ConcernsRaised,
            "Last 30 days" => c.SessionDate >= DateTime.Today.AddDays(-30),
            _ => c.SessionDate >= DateTime.Today.AddDays(-90)
        },
        Columns =
        {
            ColumnSpec.Of("Date", nameof(ContactSession.DateText), -140, bold: true, sort: nameof(ContactSession.SessionDate)),
            ColumnSpec.Of("Time", nameof(ContactSession.Time), -60),
            ColumnSpec.Of("Type", nameof(ContactSession.ContactType), 1),
            ColumnSpec.Of("What happened", nameof(ContactSession.Outcome), 1.4),
            ColumnSpec.Of("Duration", nameof(ContactSession.DurationText), -80),
            ColumnSpec.Of("Flags", nameof(ContactSession.FlagsText), 1, danger: nameof(ContactSession.ConcernsRaised)),
            ColumnSpec.Of("Notes", nameof(ContactSession.Notes), 1.6)
        },
        Landscape = true,
        Summary = ContactSummary,
        ExportPreamble = list => new[] { Block.Para(ContactSummary(list)) },
        Fields = _ => ContactFields(),
        DocumentOf = c => c.DocumentId
    });

    private static string ContactSummary(IReadOnlyList<ContactSession> list)
    {
        if (list.Count == 0) return "No sessions in view.";
        int took = list.Count(c => c.TookPlace);
        int byOther = list.Count(c => c.Outcome is "Cancelled by the other parent" or "Other parent did not attend");
        int byMe = list.Count(c => c.Outcome is "Cancelled by me" or "I could not attend");
        int notBrought = list.Count(c => c.Outcome == "Child not brought / not handed over");
        int late = list.Count(c => c.MinutesLate > 0);
        return $"{list.Count} session(s): {took} took place ({100.0 * took / list.Count:0}%)   ·   {byOther} cancelled/missed by the other parent   ·   " +
               $"{byMe} by me   ·   {notBrought} child not brought   ·   {late} late   ·   {list.Count(c => c.ConcernsRaised)} with concerns";
    }

    // ================================================================== costs
    public static RecordListView<Expense> Costs() => new(new RecordListConfig<Expense>
    {
        Title = "Costs & expenses",
        Description = "What the case is costing: court fees, legal fees, travel, reports and anything else. Useful for budgeting, " +
                      "a Help with Fees application, or if you later ask the court for costs. Mark the items you might claim back.",
        AddLabel = "Add expense", Noun = "expense",
        EmptyText = "No costs recorded yet.",
        Order = x => x.OrderByDescending(e => e.ExpenseDate),
        Filters = new[] { "All" }.Concat(Expense.Categories).Append("Might claim back").Append("Help with Fees").ToArray(),
        FilterMatch = (e, f) => f switch { "Might claim back" => e.Recoverable, "Help with Fees" => e.HelpWithFees, _ => e.Category == f },
        Columns =
        {
            ColumnSpec.Of("Date", nameof(Expense.DateText), -110, sort: nameof(Expense.ExpenseDate)),
            ColumnSpec.Of("Category", nameof(Expense.Category), 1),
            ColumnSpec.Of("Description", nameof(Expense.Description), 2),
            ColumnSpec.Of("Paid to", nameof(Expense.PaidTo), 1),
            ColumnSpec.Of("Amount", nameof(Expense.AmountText), -110, bold: true, sort: nameof(Expense.AmountPence)),
            ColumnSpec.Of("Reference", nameof(Expense.Reference), 0.8)
        },
        Summary = CostSummary,
        ExportPreamble = list => new[]
        {
            Block.Para(CostSummary(list)),
            Block.TableOf(new[] { "Category", "Total" },
                list.GroupBy(e => e.Category).OrderByDescending(g => g.Sum(e => e.AmountPence))
                    .Select(g => (IEnumerable<string>)new[] { g.Key, Expense.Money(g.Sum(e => e.AmountPence)) }))
        },
        Fields = e => new[]
        {
            FieldSpec.Date("Date", nameof(Expense.ExpenseDate), required: true),
            FieldSpec.Money("Amount (£)", nameof(Expense.AmountPence)),
            FieldSpec.Choice("Category", nameof(Expense.Category), Expense.Categories),
            FieldSpec.Text("Description", nameof(Expense.Description), required: true),
            FieldSpec.Text("Paid to", nameof(Expense.PaidTo), half: true),
            FieldSpec.Text("How paid", nameof(Expense.PaymentMethod), half: true),
            FieldSpec.Text("Receipt / invoice / HwF reference", nameof(Expense.Reference)),
            FieldSpec.Check("Covered by Help with Fees (record the fee anyway)", nameof(Expense.HelpWithFees)),
            FieldSpec.Check("I might ask for this back (costs)", nameof(Expense.Recoverable)),
            FieldSpec.Document("Receipt or invoice", nameof(Expense.DocumentId))
        },
        DocumentOf = e => e.DocumentId
    });

    private static string CostSummary(IReadOnlyList<Expense> list)
    {
        long total = list.Sum(e => e.AmountPence);
        long recoverable = list.Where(e => e.Recoverable).Sum(e => e.AmountPence);
        var top = list.GroupBy(e => e.Category).OrderByDescending(g => g.Sum(e => e.AmountPence)).Take(3)
            .Select(g => $"{g.Key} {Expense.Money(g.Sum(e => e.AmountPence))}");
        return $"Total {Expense.Money(total)} across {list.Count} item(s)   ·   might claim back {Expense.Money(recoverable)}" +
               (list.Count > 0 ? "   ·   biggest: " + string.Join(", ", top) : "");
    }

    // ================================================================== Scott schedule
    /// <summary>The Scott schedule form (also used by Import review's Edit &amp; approve).</summary>
    public static FieldSpec[] ScottFields() => new[]
    {
        FieldSpec.Int("Number", nameof(ScottItem.ItemNo)),
        FieldSpec.Date("Date (if known)", nameof(ScottItem.IncidentDate)),
        FieldSpec.Text("Or approximate date", nameof(ScottItem.DateText), hint: "e.g. Summer 2024, between May and July 2023"),
        FieldSpec.Choice("Alleged by", nameof(ScottItem.AllegedBy), new[] { "Applicant", "Respondent" }.Concat(PartyNames()).Distinct(), half: true),
        FieldSpec.Choice("Status", nameof(ScottItem.Status), ScottItem.Statuses, editable: false, half: true),
        FieldSpec.Multi("Allegation (what is said to have happened)", nameof(ScottItem.Allegation), 110),
        FieldSpec.Multi("Response", nameof(ScottItem.Response), 90),
        FieldSpec.Text("Evidence relied on (bundle page refs, e.g. C12–C15)", nameof(ScottItem.EvidenceRefs)),
        FieldSpec.Multi("Finding sought", nameof(ScottItem.FindingSought), 60),
        FieldSpec.Multi("Notes / finding made", nameof(ScottItem.Notes), 60)
    };

    public static RecordListView<ScottItem> Scott()
    {
        RecordListView<ScottItem> view = null;
        view = new RecordListView<ScottItem>(new RecordListConfig<ScottItem>
        {
            Title = "Scott schedule",
            Description = "A table of allegations for a fact-finding hearing: each allegation, the date, the response, and where the " +
                          "evidence is in the bundle. Follow any format the court has directed (often a limited number of allegations).",
            AddLabel = "Add allegation", Noun = "allegation",
            EmptyText = "No allegations listed. A Scott schedule is usually only needed if the court directs one.",
            Create = () => new ScottItem { ItemNo = (Db.All<ScottItem>().Select(s => s.ItemNo).DefaultIfEmpty(0).Max()) + 1 },
            Order = x => x.OrderBy(s => s.ItemNo),
            Filters = new[] { "All" }.Concat(ScottItem.Statuses).ToArray(),
            FilterMatch = (s, f) => s.Status == f,
            Columns =
            {
                ColumnSpec.Of("No.", nameof(ScottItem.ItemNo), -50, bold: true),
                ColumnSpec.Of("Date", nameof(ScottItem.WhenText), -120),
                ColumnSpec.Of("Allegation", nameof(ScottItem.Allegation), 2.2, wrap: true),
                ColumnSpec.Of("Response", nameof(ScottItem.Response), 1.6, wrap: true),
                ColumnSpec.Of("Evidence (bundle refs)", nameof(ScottItem.EvidenceRefs), 1),
                ColumnSpec.Of("Status", nameof(ScottItem.Status), -100)
            },
            ExportColumns = new()
            {
                ColumnSpec.Of("No.", nameof(ScottItem.ItemNo)), ColumnSpec.Of("Date", nameof(ScottItem.WhenText)),
                ColumnSpec.Of("Allegation", nameof(ScottItem.Allegation)), ColumnSpec.Of("Alleged by", nameof(ScottItem.AllegedBy)),
                ColumnSpec.Of("Evidence relied on", nameof(ScottItem.EvidenceRefs)), ColumnSpec.Of("Response", nameof(ScottItem.Response)),
                ColumnSpec.Of("Finding sought", nameof(ScottItem.FindingSought)), ColumnSpec.Of("Judge's finding", nameof(ScottItem.Notes))
            },
            Landscape = true,
            DialogHeight = 820,
            Fields = _ => ScottFields(),
            ExtraButtons = v => new[] { Ui.Button("Renumber 1, 2, 3…", "", (_, _) => Renumber(v)) }
        });
        return view;
    }

    private static void Renumber(RecordListView<ScottItem> v)
    {
        int n = 1;
        foreach (var s in v.All.OrderBy(s => s.ItemNo).ThenBy(s => s.IncidentDate))
        {
            if (s.ItemNo == n) { n++; continue; }
            var c = Ui.Clone(s);
            c.ItemNo = n++;
            Db.Save(c);
        }
        v.Reload();
    }
}
