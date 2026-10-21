using System.Windows;
using System.Windows.Controls;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Modules;

/// <summary>Works out court deadlines and can add the result straight to Directions &amp; tasks.</summary>
public class DeadlineCalculatorWindow : FormDialog
{
    private sealed record Preset(string Name, string Mode, int Amount, string Hint);

    private static readonly Preset[] Presets =
    {
        new("Custom", "Calendar days after", 14, ""),
        new("Appeal: appellant's notice (21 days after the decision)", "Calendar days after", 21,
            "FPR r.30.4: usually 21 days after the decision, unless the court directs otherwise. Check carefully – time limits for appeals are strict."),
        new("Subject access request reply (1 calendar month)", "Calendar months after", 1,
            "UK GDPR: an organisation should normally respond within one month of receiving the request."),
        new("Complaint reply (20 working days)", "Working days after", 20, "Many public bodies aim to reply within 20 working days. Check their policy."),
        new("Document due N clear days before a hearing", "Clear days before", 3, "Use the hearing date as the date, and the number of clear days in the order."),
        new("Filing 'by 4pm, 7 days before the hearing'", "Calendar days before", 7, "Use the hearing date. If the order says 'clear days', use that option instead."),
    };

    private readonly ComboBox _preset, _mode;
    private readonly DatePicker _date;
    private readonly TextBox _amount;
    private readonly TextBlock _result, _hint;
    private DateTime? _answer;

    public DeadlineCalculatorWindow() : base("Deadline calculator", 600, 640, "Add as a task")
    {
        AddHint("Works out a deadline using England & Wales bank holidays and the Family Procedure Rules' way of counting days. " +
                "Always check the wording of your order or the rule itself — they take priority.");
        _preset = Add("Common deadlines", new ComboBox { ItemsSource = Presets.Select(p => p.Name).ToList(), SelectedIndex = 0 });
        _mode = Add("How to count", new ComboBox { ItemsSource = Deadlines.Methods, SelectedIndex = 0 });
        _date = Add("From (or the hearing date, when counting back)", new DatePicker { SelectedDate = DateTime.Today });
        _amount = Add("Number of days / months", Box("14"));
        _hint = Ui.Muted("", 12);
        _hint.Margin = new Thickness(0, 10, 0, 0);
        Fields.Children.Add(_hint);
        _result = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
        _result.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        Fields.Children.Add(_result);

        _preset.SelectionChanged += (_, _) =>
        {
            var p = Presets[_preset.SelectedIndex];
            _mode.SelectedItem = p.Mode;
            _amount.Text = p.Amount.ToString();
            _hint.Text = p.Hint;
            Recalculate();
        };
        _mode.SelectionChanged += (_, _) => Recalculate();
        _date.SelectedDateChanged += (_, _) => Recalculate();
        _amount.TextChanged += (_, _) => Recalculate();
        Loaded += (_, _) => Recalculate();
    }

    private void Recalculate()
    {
        _answer = null;
        if (_date.SelectedDate is not { } from || !int.TryParse(_amount.Text.Trim(), out var n) || n < 0 || n > 3650)
        {
            _result.Text = "Enter a date and a number.";
            ConfirmButton.IsEnabled = false;
            return;
        }
        _answer = Deadlines.Calculate(_mode.SelectedItem as string ?? Deadlines.Methods[0], from, n);
        _result.Text = "Deadline: " + Deadlines.Describe(_answer.Value);
        ConfirmButton.IsEnabled = true;
    }

    protected override bool Validate()
    {
        if (_answer == null) return false;
        var preset = Presets[_preset.SelectedIndex];
        App.Library.Db.Save(new CaseAction
        {
            Kind = "Reminder", Title = preset.Name == "Custom" ? "Deadline" : preset.Name, DueDate = _answer, Status = "Outstanding",
            Detail = $"Worked out as {_amount.Text} {(_mode.SelectedItem as string)?.ToLowerInvariant()} {_date.SelectedDate:d MMMM yyyy}."
        });
        MainWindow.Shell?.RefreshAll();
        return true;
    }
}

/// <summary>Case details: the facts that fill templates, bundle covers and the dashboard.</summary>
public class CaseDetailsView : UserControl
{
    private static readonly (string Key, string Label, string Hint, string[] Choices)[] Fields =
    {
        ("case.title", "Case title", "e.g. Re: A and B (Children), or Smith v Smith", null),
        ("case.number", "Case number", "From the top of any court form or order", null),
        ("case.court", "Court / hearing centre", "e.g. The Family Court sitting at Birmingham", null),
        ("case.type", "Type of proceedings", null, new[]
        {
            "Private law – child arrangements (C100)", "Private law – enforcement (C79)", "Private law – other Children Act application",
            "Public law – care proceedings", "Domestic abuse – non-molestation / occupation (FL401)", "Divorce / dissolution",
            "Financial remedy", "Schedule 1 (financial provision for a child)", "Adoption / special guardianship", "Appeal", "Other"
        }),
        ("case.stage", "Current stage", null, new[]
        {
            "Pre-application (MIAM / mediation)", "Application made", "Waiting for FHDRA (first hearing)", "Section 7 report being prepared",
            "Waiting for DRA", "Fact-finding", "Waiting for final hearing", "Final order made", "Enforcement", "Appeal", "Closed"
        }),
        ("case.judge", "Judge / allocated level", "e.g. District Judge Jones, or magistrates", null),
        ("case.issued", "Date proceedings issued", "dd/mm/yyyy", null),
        ("case.miam", "MIAM / mediation", null, new[] { "MIAM attended", "MIAM exemption claimed", "Mediation in progress", "Mediation not suitable", "Not yet" }),
        ("case.cafcassRef", "Cafcass reference", null, null),
        ("case.laRef", "Local authority reference", null, null),
    };

    private readonly Dictionary<string, Control> _boxes = new();
    private readonly TextBox _summary;
    private readonly TextBlock _children, _saved;

    public CaseDetailsView()
    {
        var db = App.Library.Db;
        var stack = new StackPanel { Margin = new Thickness(36, 28, 36, 30), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Ui.Text("Case details", "H1"));
        stack.Children.Add(Ui.Muted("The basic facts of your case. They fill in letters, statements, bundle covers and reports automatically, " +
                                    "so you only type them once. Everything saves as you type.", 13));

        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < Fields.Length; i++)
        {
            var (key, label, hint, choices) = Fields[i];
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Style("Label") });
            Control editor;
            if (choices != null)
            {
                var combo = new ComboBox { ItemsSource = choices, IsEditable = true, Text = db.GetSetting(key, "") };
                combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Save(key, combo.Text)));
                combo.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => Save(key, combo.Text));
                editor = combo;
            }
            else
            {
                var box = new TextBox { Text = db.GetSetting(key, "") };
                box.TextChanged += (_, _) => Save(key, box.Text);
                editor = box;
            }
            _boxes[key] = editor;
            cell.Children.Add(editor);
            if (hint != null) cell.Children.Add(new TextBlock { Text = hint, Style = Ui.Style("Muted"), FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2 == 0 ? 0 : 2);
            grid.Children.Add(cell);
        }
        stack.Children.Add(Ui.Card(grid, new Thickness(0, 4, 0, 14)));

        var kids = new StackPanel();
        kids.Children.Add(Ui.Text("The children", "H2"));
        _children = Ui.Muted("", 13);
        kids.Children.Add(_children);
        var addChild = Ui.Button("Add or edit children on the Parties page", "", (_, _) => MainWindow.Shell?.GoTo("parties"));
        addChild.Margin = new Thickness(0, 10, 0, 0);
        addChild.HorizontalAlignment = HorizontalAlignment.Left;
        kids.Children.Add(addChild);
        stack.Children.Add(Ui.Card(kids, new Thickness(0, 0, 0, 14)));

        var sum = new StackPanel();
        sum.Children.Add(Ui.Text("Case summary", "H2"));
        sum.Children.Add(Ui.Muted("A few neutral paragraphs: who is involved, what the case is about, and where it has got to. " +
                                  "Used by the AI tools as background, and handy for your case summary document.", 12.5));
        _summary = new TextBox { Style = Ui.Style("MultilineBox"), Height = 180, Margin = new Thickness(0, 8, 0, 0), Text = db.GetSetting("case.summary", "") };
        _summary.TextChanged += (_, _) => Save("case.summary", _summary.Text);
        sum.Children.Add(_summary);
        _saved = Ui.Muted("", 11.5);
        _saved.Margin = new Thickness(0, 6, 0, 0);
        sum.Children.Add(_saved);
        stack.Children.Add(Ui.Card(sum));

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => RefreshChildren();
    }

    private System.Windows.Threading.DispatcherTimer _auditTimer;
    private readonly HashSet<string> _changedKeys = new();

    private void Save(string key, string value)
    {
        var db = App.Library.Db;
        if ((db.GetSetting(key, "") ?? "") == (value ?? "")) return;
        db.SetSetting(key, value ?? "");
        _saved.Text = "✓ Saved";
        // Typing saves continuously; the audit trail gets one entry once you pause
        _changedKeys.Add(key);
        _auditTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _auditTimer.Stop();
        _auditTimer.Tick -= FlushAudit;
        _auditTimer.Tick += FlushAudit;
        _auditTimer.Start();
    }

    private void FlushAudit(object sender, EventArgs e)
    {
        _auditTimer.Stop();
        if (_changedKeys.Count == 0) return;
        var db = App.Library.Db;
        db.Audit("case.update", "case", null, "Changed case details",
            string.Join("\n", _changedKeys.Select(k => $"{k.Replace("case.", "")}: {Shorten(db.GetSetting(k, ""))}")));
        _changedKeys.Clear();
        MainWindow.Shell?.RefreshTimelineAndDashboard();
    }

    private static string Shorten(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public void RefreshChildren()
    {
        var kids = MainWindow.Shell?.Parties.Where(p => p.Role.StartsWith("Child", StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
        _children.Text = kids.Count == 0
            ? "No children added yet. Add each child as a party with the role 'Child (subject of proceedings)' and their date of birth – their ages then stay up to date everywhere."
            : string.Join("\n", kids.Select(k => $"•  {(string.IsNullOrWhiteSpace(k.Name) ? "(no name)" : k.Name)}{(k.AgeText.Length > 0 ? " — " + k.AgeText : "")}"));
    }
}
