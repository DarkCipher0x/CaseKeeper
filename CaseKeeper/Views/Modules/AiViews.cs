using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using CaseKeeper.UI;
using CaseKeeper.Views.Forms;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// A small window that streams one AI answer as it's written, with Stop, Copy, Save and (optionally)
/// "Use this". Used for rewriting text, explaining things, and questions about the guide.
/// </summary>
public class AiAssistWindow : Window
{
    private readonly TextBox _output;
    private readonly TextBlock _status;
    private readonly Button _accept, _stop;
    private readonly CancellationTokenSource _cts = new();
    private bool _accepted;

    private AiAssistWindow(string title, string acceptLabel)
    {
        Title = title + " – CaseKeeper";
        Width = 760;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = Ui.Style("AppWindow");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new DockPanel { Margin = new Thickness(20) };
        var head = new StackPanel();
        head.Children.Add(Ui.Text(title, "H1", 20));
        var note = Ui.Muted($"Written by the AI model on your Ollama server ({AiService.Model}). Check it against your documents before relying on it. It is not legal advice.", 12);
        head.Children.Add(note);
        _status = Ui.Muted("Starting…", 12);
        _status.Margin = new Thickness(0, 6, 0, 6);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _stop = Ui.Button("Stop", "", (_, _) => _cts.Cancel());
        buttons.Children.Add(_stop);
        buttons.Children.Add(Ui.Button("Copy", "", (_, _) => Ui.CopyToClipboard(_output.Text)));
        buttons.Children.Add(Ui.Button("Save…", "", async (_, _) =>
        {
            var doc = new DocModel { Title = title, Subtitle = $"AI-assisted · {AiService.Model} · {DateTime.Now:d MMMM yyyy HH:mm}", Footer = "AI-generated – check against the source documents. Not legal advice." };
            doc.AddRange(Markup.FromMarkdown(_output.Text));
            await ExportHelper.ExportDocumentAsync(this, doc, null);
        }));
        _accept = Ui.Button(acceptLabel ?? "Close", acceptLabel == null ? null : "", (_, _) => { _accepted = acceptLabel != null; Close(); }, primary: true);
        _accept.IsEnabled = acceptLabel == null;
        buttons.Children.Add(_accept);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _output = new TextBox
        {
            Style = Ui.Style("MultilineBox"), IsReadOnly = acceptLabel == null, FontSize = 13.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(_output);
        Content = root;
        Closing += (_, _) => _cts.Cancel();
    }

    /// <summary>Runs one AI request with the given instruction and text. Returns the edited answer if accepted, otherwise null.</summary>
    public static async Task<string> RunAsync(Window owner, string title, string instruction, string text,
        string acceptLabel = "Use this", bool withCaseBackground = false)
    {
        var w = new AiAssistWindow(title, acceptLabel) { Owner = owner };
        w.Show();
        try
        {
            var user = instruction + (withCaseBackground ? "\n\n" + AiService.CaseBackground() : "") + "\n\n---\n\n" + text;
            App.Library.Db.Audit("ai.request", "ai", null, $"AI: {title} ({AiService.Model})", $"{text.Length:N0} characters sent");
            w._status.Text = "Writing…";
            await AiService.ChatAsync(AiService.Prompt("prompt.system"), user,
                piece => w.Dispatcher.BeginInvoke(() => { w._output.AppendText(piece); w._output.ScrollToEnd(); }), w._cts.Token);
            w._status.Text = "Done. You can edit the text before using it.";
        }
        catch (OperationCanceledException) { w._status.Text = "Stopped."; }
        catch (Exception ex)
        {
            var id = AppLog.Error("AI", "AI request failed", ex);
            w._status.Text = $"Couldn't get an answer: {ex.Message} (ref {id}). Is Ollama running? Check Settings → AI.";
            w._status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        w._stop.IsEnabled = false;
        w._accept.IsEnabled = true;
        var tcs = new TaskCompletionSource<string>();
        w.Closed += (_, _) => tcs.TrySetResult(w._accepted ? w._output.Text : null);
        if (!w.IsVisible) tcs.TrySetResult(w._accepted ? w._output.Text : null);
        return await tcs.Task;
    }
}

/// <summary>
/// AI analysis: choose what to look at (the whole case, some documents, a category, a party, a
/// date range, the timeline or the records), choose a report, and let your local AI write it.
/// Reports are kept, and can be exported or added to the library.
/// </summary>
public class AiView : UserControl
{
    private static Data.Database Db => App.Library.Db;

    private readonly ComboBox _scope, _scopeDetail, _report;
    private readonly DatePicker _from, _to;
    private readonly CheckBox _includeText, _includeNotes, _includeTimeline, _includeRecords, _granular;
    private readonly ListBox _docPicker, _history;
    private readonly TextBox _docFilter, _extra, _output;
    private readonly TextBlock _status, _scopeInfo, _reportInfo, _setupNote;
    private readonly Button _run, _stop;
    private readonly StackPanel _docPanel;
    private CancellationTokenSource _cts;
    private AiReport _shown;

    private sealed class Pick
    {
        public CaseDocument Doc { get; init; }
        public bool Selected { get; set; }
        public string Title => Doc.Title;
        public string Meta => $"{Doc.DocumentDateText} · {Doc.CategoryName}";
    }

    private List<Pick> _picks = new();

    public AiView()
    {
        var root = new Grid { Margin = new Thickness(28, 24, 28, 18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ---------------------------------------------------------------- left: choose
        var left = new StackPanel();
        left.Children.Add(Ui.Text("AI analysis", "H1"));
        left.Children.Add(Ui.Muted("Ask your own local AI (Ollama) to summarise, extract a chronology, find inconsistencies and more. " +
                                   "Your documents stay on your computer.", 12.5));
        _setupNote = Ui.Muted("", 12.5);
        _setupNote.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_setupNote);

        left.Children.Add(new TextBlock { Text = "1. WHAT TO LOOK AT", Style = Ui.Style("Label"), Margin = new Thickness(0, 16, 0, 4) });
        _scope = new ComboBox
        {
            ItemsSource = new[] { "The whole case", "Documents I choose", "One category", "One party's documents", "A date range", "The timeline only", "Records only (hearings, directions, contact, Scott schedule)" },
            SelectedIndex = 1
        };
        left.Children.Add(_scope);
        _scopeDetail = new ComboBox { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        left.Children.Add(_scopeDetail);
        var dates = new Grid { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        _from = new DatePicker { SelectedDate = DateTime.Today.AddMonths(-6) };
        _to = new DatePicker { SelectedDate = DateTime.Today };
        Grid.SetColumn(_to, 2);
        dates.Children.Add(_from);
        dates.Children.Add(_to);
        left.Children.Add(dates);

        _docPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        _docPanel.Children.Add(Ui.SearchBox(out _docFilter, "Filter documents…"));
        _docFilter.TextChanged += (_, _) => FilterPicks();
        _docPicker = new ListBox { Height = 210, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _docPicker.ItemTemplate = PickTemplate();
        _docPanel.Children.Add(_docPicker);
        var pickButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        pickButtons.Children.Add(Ui.Button("Tick all shown", null, (_, _) => { foreach (var p in (IEnumerable<Pick>)_docPicker.ItemsSource) p.Selected = true; _docPicker.Items.Refresh(); UpdateScopeInfo(); }));
        pickButtons.Children.Add(Ui.Button("Clear", null, (_, _) => { foreach (var p in _picks) p.Selected = false; _docPicker.Items.Refresh(); UpdateScopeInfo(); }));
        _docPanel.Children.Add(pickButtons);
        left.Children.Add(_docPanel);

        _includeText = new CheckBox { Content = "Text of the documents (from text recognition)", IsChecked = true };
        _includeNotes = new CheckBox { Content = "My notes on documents and pages", IsChecked = true };
        _includeTimeline = new CheckBox { Content = "Timeline entries in the same period" };
        _includeRecords = new CheckBox { Content = "Hearings, orders, directions and contact log" };
        foreach (var c in new[] { _includeText, _includeNotes, _includeTimeline, _includeRecords })
        {
            c.Checked += (_, _) => UpdateScopeInfo();
            c.Unchecked += (_, _) => UpdateScopeInfo();
            left.Children.Add(c);
        }
        _scopeInfo = Ui.Muted("", 12);
        left.Children.Add(_scopeInfo);

        left.Children.Add(new TextBlock { Text = "2. WHAT KIND OF REPORT", Style = Ui.Style("Label"), Margin = new Thickness(0, 14, 0, 4) });
        var reports = AiService.ReportTypes.Select(r => r.Name).Append("My own question / instructions").ToList();
        _report = new ComboBox { ItemsSource = reports, SelectedIndex = 0 };
        left.Children.Add(_report);
        _reportInfo = Ui.Muted("", 12);
        left.Children.Add(_reportInfo);
        left.Children.Add(new TextBlock { Text = "EXTRA INSTRUCTIONS (OPTIONAL)", Style = Ui.Style("Label") });
        _extra = new TextBox { Style = Ui.Style("MultilineBox"), Height = 70 };
        left.Children.Add(_extra);
        _granular = new CheckBox { Content = "Granular: analyse each part separately, then combine (best for large sets; keeps per-part detail)" };
        left.Children.Add(_granular);

        var runRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _run = Ui.Button("Run analysis", "", async (_, _) => await RunAsync(), primary: true);
        _stop = Ui.Button("Stop", "", (_, _) => _cts?.Cancel());
        _stop.IsEnabled = false;
        runRow.Children.Add(_run);
        runRow.Children.Add(_stop);
        runRow.Children.Add(Ui.Button(null, "", (_, _) => MainWindow.Shell?.GoToSettings("ai"), tooltip: "AI settings and prompts"));
        left.Children.Add(runRow);

        root.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });

        // ---------------------------------------------------------------- right: output and history
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });
        Grid.SetColumn(right, 2);

        var outHead = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        var outButtons = new WrapPanel();
        outButtons.Children.Add(Ui.Button("Save report", "", (_, _) => SaveReport(), tooltip: "Keep this report in the history below"));
        outButtons.Children.Add(Ui.Button("Export…", "", async (_, _) => await ExportAsync()));
        outButtons.Children.Add(Ui.Button("Add to library", "", async (_, _) => await AddToLibraryAsync()));
        outButtons.Children.Add(Ui.Button(null, "", (_, _) => Ui.CopyToClipboard(_output.Text), tooltip: "Copy"));
        DockPanel.SetDock(outButtons, Dock.Right);
        outHead.Children.Add(outButtons);
        _status = Ui.Muted("Choose what to look at and a report, then Run analysis.", 13);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        outHead.Children.Add(_status);
        right.Children.Add(outHead);

        _output = new TextBox { Style = Ui.Style("MultilineBox"), FontSize = 13.5, IsReadOnly = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_output, 1);
        right.Children.Add(_output);

        var hist = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var hh = Ui.Text("Saved reports", "H2");
        DockPanel.SetDock(hh, Dock.Top);
        hist.Children.Add(hh);
        _history = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _history.ItemTemplate = HistoryTemplate();
        _history.SelectionChanged += (_, _) => { if (_history.SelectedItem is AiReport r) ShowReport(r); };
        _history.ContextMenuOpening += (_, _) => _history.ContextMenu = HistoryMenu();
        _history.ContextMenu = new ContextMenu();
        hist.Children.Add(_history);
        Grid.SetRow(hist, 2);
        right.Children.Add(hist);
        root.Children.Add(right);
        Content = root;

        _scope.SelectionChanged += (_, _) =>
        {
            var s = _scope.SelectedIndex;
            _docPanel.Visibility = s == 1 ? Visibility.Visible : Visibility.Collapsed;
            _scopeDetail.Visibility = s is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
            dates.Visibility = s is 4 or 5 ? Visibility.Visible : Visibility.Collapsed;
            FillScopeDetail();
            UpdateScopeInfo();
        };
        _scopeDetail.SelectionChanged += (_, _) => UpdateScopeInfo();
        _from.SelectedDateChanged += (_, _) => UpdateScopeInfo();
        _to.SelectedDateChanged += (_, _) => UpdateScopeInfo();
        _report.SelectionChanged += (_, _) =>
        {
            var i = _report.SelectedIndex;
            _reportInfo.Text = i < AiService.ReportTypes.Length ? AiService.ReportTypes[i].Description : "Type your question or instructions in the box below.";
        };
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        _setupNote.Text = AiService.Enabled
            ? $"Using {(string.IsNullOrWhiteSpace(AiService.Model) ? "(no model chosen)" : AiService.Model)} at {AiService.Endpoint}"
            : "AI is switched off. Turn it on in Settings → AI (it needs Ollama installed).";
        _picks = (MainWindow.Shell?.Documents ?? new List<CaseDocument>()).Select(d => new Pick { Doc = d, Selected = _picks.FirstOrDefault(p => p.Doc.Id == d.Id)?.Selected ?? false }).ToList();
        FilterPicks();
        FillScopeDetail();
        _report.SelectedIndex = Math.Max(0, _report.SelectedIndex);
        _history.ItemsSource = Db.All<AiReport>().OrderByDescending(r => r.CreatedAt).ToList();
        UpdateScopeInfo();
    }

    /// <summary>Selects documents (e.g. from the Documents right-click menu) and a report type.</summary>
    public void Preselect(IEnumerable<long> docIds, string reportKey = null)
    {
        Reload();
        var ids = docIds.ToHashSet();
        foreach (var p in _picks) p.Selected = ids.Contains(p.Doc.Id);
        _scope.SelectedIndex = 1;
        _docPicker.Items.Refresh();
        if (reportKey != null)
        {
            var i = Array.FindIndex(AiService.ReportTypes, r => r.Key == reportKey);
            if (i >= 0) _report.SelectedIndex = i;
        }
        UpdateScopeInfo();
    }

    private void FilterPicks()
    {
        var q = _docFilter.Text.Trim();
        _docPicker.ItemsSource = _picks.Where(p => q.Length == 0 || $"{p.Doc.Title} {p.Doc.CategoryName} {p.Doc.PartyName} {p.Doc.Tags}".Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void FillScopeDetail()
    {
        var shell = MainWindow.Shell;
        if (shell == null) return;
        if (_scope.SelectedIndex == 2)
        {
            _scopeDetail.ItemsSource = shell.Categories.Where(c => c.DocumentCount > 0).ToList();
            _scopeDetail.DisplayMemberPath = "DisplayName";
        }
        else if (_scope.SelectedIndex == 3)
        {
            _scopeDetail.ItemsSource = shell.Parties.ToList();
            _scopeDetail.DisplayMemberPath = "DisplayName";
        }
        if (_scopeDetail.SelectedIndex < 0 && _scopeDetail.Items.Count > 0) _scopeDetail.SelectedIndex = 0;
    }

    private static DataTemplate PickTemplate()
    {
        var t = new DataTemplate();
        var cb = new FrameworkElementFactory(typeof(CheckBox));
        cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Selected") { Mode = System.Windows.Data.BindingMode.TwoWay });
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Meta"));
        meta.SetValue(TextBlock.FontSizeProperty, 11.0);
        meta.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.AppendChild(title);
        stack.AppendChild(meta);
        cb.AppendChild(stack);
        t.VisualTree = cb;
        return t;
    }

    private static DataTemplate HistoryTemplate()
    {
        var t = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
        {
            StringFormat = "{0} · {1} · {2}",
            Bindings = { new System.Windows.Data.Binding("CreatedText"), new System.Windows.Data.Binding("Model"), new System.Windows.Data.Binding("Scope") }
        });
        meta.SetValue(TextBlock.FontSizeProperty, 11.5);
        meta.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        meta.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.AppendChild(title);
        stack.AppendChild(meta);
        t.VisualTree = stack;
        return t;
    }

    // ------------------------------------------------------------------ gathering the material
    private (List<AiService.SourceText> Sources, string Scope) Gather()
    {
        var shell = MainWindow.Shell;
        var docs = shell?.Documents ?? new List<CaseDocument>();
        IEnumerable<CaseDocument> chosen;
        string scope;
        DateTime from = _from.SelectedDate ?? DateTime.MinValue, to = _to.SelectedDate ?? DateTime.MaxValue;
        switch (_scope.SelectedIndex)
        {
            case 0: chosen = docs; scope = "Whole case"; break;
            case 1: chosen = _picks.Where(p => p.Selected).Select(p => p.Doc); scope = $"{_picks.Count(p => p.Selected)} chosen document(s)"; break;
            case 2:
                var cat = _scopeDetail.SelectedItem as Category;
                chosen = docs.Where(d => d.CategoryId == cat?.Id);
                scope = "Category: " + cat?.DisplayName;
                break;
            case 3:
                var party = _scopeDetail.SelectedItem as Party;
                chosen = docs.Where(d => d.PartyId == party?.Id);
                scope = "Party: " + party?.DisplayName;
                break;
            case 4:
                chosen = docs.Where(d => d.DocumentDate >= from && d.DocumentDate <= to);
                scope = $"{from:d MMM yyyy} to {to:d MMM yyyy}";
                break;
            case 5: chosen = Enumerable.Empty<CaseDocument>(); scope = $"Timeline {from:d MMM yyyy} – {to:d MMM yyyy}"; break;
            default: chosen = Enumerable.Empty<CaseDocument>(); scope = "Case records"; break;
        }

        var sources = new List<AiService.SourceText>();
        foreach (var d in chosen.OrderBy(d => d.DocumentDate ?? DateTime.MaxValue))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Category: {d.CategoryName}; dated {d.DocumentDateText}" +
                          (string.IsNullOrWhiteSpace(d.PartyName) ? "" : $"; filed by {d.PartyName}") +
                          (string.IsNullOrWhiteSpace(d.Author) ? "" : $"; from {d.Author}") +
                          (string.IsNullOrWhiteSpace(d.ExhibitRef) ? "" : $"; exhibit {d.ExhibitRef}"));
            if (!string.IsNullOrWhiteSpace(d.Description)) sb.AppendLine("Summary: " + d.Description);
            if (_includeText.IsChecked == true)
                foreach (var (page, text) in Db.GetAllPageText(d.Id).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)))
                    sb.AppendLine($"[Page {page}]").AppendLine(text.Trim());
            if (_includeNotes.IsChecked == true)
            {
                if (!string.IsNullOrWhiteSpace(d.Notes)) sb.AppendLine("My notes on this document: " + d.Notes);
                foreach (var (page, note) in Db.GetPageNotes(d.Id).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)))
                    sb.AppendLine($"My note on page {page}: {note}");
            }
            sources.Add(new AiService.SourceText { Label = $"Document: {d.Title}", Text = sb.ToString() });
        }

        bool timeline = _scope.SelectedIndex is 0 or 5 || _includeTimeline.IsChecked == true;
        if (timeline)
        {
            var range = _scope.SelectedIndex is 4 or 5;
            var events = Db.GetEvents().Where(e => !range || (e.EventDate >= from && e.EventDate <= to)).OrderBy(e => e.EventDate).ToList();
            if (events.Count > 0)
                sources.Add(new AiService.SourceText
                {
                    Label = "Timeline (recorded by the user)",
                    Text = string.Join("\n", events.Select(e => $"{e.EventDate:dd/MM/yyyy} {e.Time} [{e.EventType}] {e.Title}. {e.Description} {e.ExtraText}".Trim()))
                });
        }

        bool records = _scope.SelectedIndex is 0 or 6 || _includeRecords.IsChecked == true;
        if (records)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var h in Db.All<Hearing>().OrderBy(h => h.HearingDate))
                sb.AppendLine($"Hearing {h.DateText}: {h.HearingType} at {h.Court}, {h.Status}. {h.Outcome}");
            foreach (var o in Db.All<CourtOrder>().OrderBy(o => o.OrderDate))
                sb.AppendLine($"Order {o.DateText}: {o.OrderType} ({o.KindText}). {o.Summary}");
            foreach (var a in Db.All<CaseAction>().OrderBy(a => a.DueDate))
                sb.AppendLine($"{a.Kind} – {a.Title}; who: {a.Responsible}; due {a.DueText}; status {a.Status}. {a.Detail}");
            foreach (var c in Db.All<ContactSession>().OrderBy(c => c.SessionDate))
                sb.AppendLine($"Contact {c.DateText}: {c.ContactType}; {c.Outcome}{(c.FlagsText.Length > 0 ? "; " + c.FlagsText : "")}. {c.Notes}");
            foreach (var s in Db.All<ScottItem>().OrderBy(s => s.ItemNo))
                sb.AppendLine($"Scott schedule {s.ItemNo} ({s.WhenText}, alleged by {s.AllegedBy}, {s.Status}): {s.Allegation} Response: {s.Response}");
            if (sb.Length > 0) sources.Add(new AiService.SourceText { Label = "Case records (hearings, orders, directions, contact, Scott schedule)", Text = sb.ToString() });
        }
        return (sources, scope);
    }

    private void UpdateScopeInfo()
    {
        if (!IsLoaded) return;
        try
        {
            var (sources, _) = Gather();
            int chars = sources.Sum(s => s.Text.Length);
            int parts = Math.Max(1, (int)Math.Ceiling(chars / (double)AiService.ChunkChars));
            _scopeInfo.Text = $"{sources.Count} source(s), about {chars / 1000.0:0.#}k characters" +
                              (parts > 1 ? $" – will be read in about {parts} parts" : "");
        }
        catch { _scopeInfo.Text = ""; }
    }

    // ------------------------------------------------------------------ running
    private string TaskText()
    {
        int i = _report.SelectedIndex;
        var task = i < AiService.ReportTypes.Length ? AiService.ReportPrompt(AiService.ReportTypes[i].Key) : "";
        if (!string.IsNullOrWhiteSpace(_extra.Text)) task += (task.Length > 0 ? "\n\nAlso: " : "") + _extra.Text.Trim();
        return task;
    }

    private async Task RunAsync()
    {
        var owner = Window.GetWindow(this);
        if (!AiService.EnsureEnabled(owner)) return;
        var task = TaskText();
        if (string.IsNullOrWhiteSpace(task)) { MessageBox.Show(owner, "Type your question or instructions first.", "AI analysis"); return; }
        var (sources, scope) = Gather();
        if (sources.Count == 0) { MessageBox.Show(owner, "Nothing is selected to analyse.", "AI analysis"); return; }

        _cts = new CancellationTokenSource();
        _run.IsEnabled = false;
        _stop.IsEnabled = true;
        _output.Clear();
        _shown = null;
        var reportName = _report.SelectedIndex < AiService.ReportTypes.Length ? AiService.ReportTypes[_report.SelectedIndex].Name : "Question";
        var progress = new Progress<string>(s => _status.Text = s);
        // Read every control here on the UI thread – the analysis itself runs in the background
        bool granular = _granular.IsChecked == true;
        try
        {
            var result = await Task.Run(() => AiService.AnalyseAsync(task, sources, granular, progress,
                piece => Dispatcher.BeginInvoke(() => { _output.AppendText(piece); _output.ScrollToEnd(); }), _cts.Token));
            // The final report replaces the streamed working notes
            _output.Text = "> AI-generated draft – check it against the source documents. Not legal advice.\n\n" + result.Output;
            _status.Text = $"Finished in {result.DurationMs / 1000.0:0} s · {result.Chunks} part(s). Save it to keep it.";
            _shown = new AiReport
            {
                Title = $"{reportName} – {scope}", ReportType = reportName, Scope = scope, Model = AiService.Model,
                Endpoint = new Uri(AiService.Endpoint).Host, Output = _output.Text, DurationMs = result.DurationMs, ChunkCount = result.Chunks
            };
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex)
        {
            var id = AppLog.Error("AI", "Analysis failed", ex);
            _status.Text = $"Couldn't finish: {ex.Message} (ref {id})";
        }
        finally
        {
            _run.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    private void SaveReport()
    {
        if (string.IsNullOrWhiteSpace(_output.Text)) return;
        var r = _shown ?? new AiReport { Title = "Report", Model = AiService.Model };
        r.Output = _output.Text;
        Db.Save(r);
        _shown = r;
        _history.ItemsSource = Db.All<AiReport>().OrderByDescending(x => x.CreatedAt).ToList();
        _status.Text = "Saved.";
    }

    private void ShowReport(AiReport r)
    {
        _shown = r;
        _output.Text = r.Output;
        _status.Text = $"{r.Title} · {r.CreatedText} · {r.Model}";
    }

    private DocModel CurrentDoc()
    {
        var title = _shown?.Title ?? "AI report";
        var doc = new DocModel
        {
            Title = title,
            Subtitle = $"AI-assisted analysis · {(_shown?.Model ?? AiService.Model)} · {(_shown?.CreatedAt ?? DateTime.Now):d MMMM yyyy HH:mm}",
            Footer = "AI-generated – check against the source documents. Not legal advice."
        };
        doc.AddRange(Markup.FromMarkdown(_output.Text));
        return doc;
    }

    private async Task ExportAsync()
    {
        if (string.IsNullOrWhiteSpace(_output.Text)) return;
        await ExportHelper.ExportDocumentAsync(Window.GetWindow(this), CurrentDoc(), null);
    }

    private async Task AddToLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(_output.Text)) return;
        var doc = CurrentDoc();
        var bytes = await Sta.RunAsync(() => DocumentExporter.Render(doc, null, ExportFormat.Pdf, null, null));
        MainWindow.Shell?.ImportGenerated(bytes, doc.Title + ".pdf", doc.Title, "AI analysis reports", DateTime.Today);
    }

    private ContextMenu HistoryMenu()
    {
        var r = _history.SelectedItem as AiReport;
        return Ui.Menu(
            Ui.Item("Open", "", () => { if (r != null) ShowReport(r); }, enabled: r != null, bold: true),
            Ui.Item("Export…", "", async () => { if (r != null) { ShowReport(r); await ExportAsync(); } }, enabled: r != null),
            Ui.Item("Add to library", "", async () => { if (r != null) { ShowReport(r); await AddToLibraryAsync(); } }, enabled: r != null),
            new Separator(),
            Ui.Item("Delete…", "", () =>
            {
                if (r == null || MessageBox.Show(Window.GetWindow(this), $"Delete “{r.Title}”?", "Delete report", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                Db.Remove<AiReport>(r.Id);
                _history.ItemsSource = Db.All<AiReport>().OrderByDescending(x => x.CreatedAt).ToList();
            }, enabled: r != null));
    }
}
