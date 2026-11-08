using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;
using CaseKeeper.Views.Forms;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// Import review: every suggestion from Bulk Case Import (and "Organise with AI") waits here until the
/// person approves, edits, rejects or defers it. Each one shows where it came from (document, page and
/// the exact words), how confident the AI was and why, and any warnings from CaseKeeper's own checks.
/// Approving uses the same forms and rules as adding a record by hand.
/// </summary>
public partial class ImportReviewView : UserControl
{
    private static Data.Database Db => App.Library.Db;
    private static MainWindow Shell => MainWindow.Shell;

    private sealed record Section(string Key, string Label, Func<AiFinding, bool> Match);

    private sealed class SectionItem
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public int Count { get; init; }
        public string Text => Count > 0 || Key is "summary" or "failed" or "versions" ? $"{Label}  ({Count:N0})" : Label;
        public override string ToString() => Text;
    }

    private readonly ListBox _sections;
    private readonly TextBlock _title, _subtitle;
    private readonly DataGrid _grid, _files;
    private readonly ContentControl _middle = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(18, 4, 4, 18) };
    private readonly WrapPanel _bulkBar = new() { Margin = new Thickness(0, 0, 0, 6) };
    private TextBox _search;
    private readonly ComboBox _confidence, _document;
    private readonly DatePicker _from, _to;
    private readonly CheckBox _warningsOnly;
    private readonly WrapPanel _filters;

    private List<AiFinding> _all = new();
    private List<ImportFile> _fileRows = new();
    private Dictionary<long, CaseDocument> _docs = new();
    private long _unknownId;
    private string _current = "summary";

    private readonly Section[] _defs;

    public ImportReviewView()
    {
        _defs = new[]
        {
            new Section("summary", "Summary", _ => false),
            new Section("categories", "Categorise documents", f => f.IsOpen && f.Kind == FindingKinds.Category && !IsUnknown(f)),
            new Section("unknown", "Unknown / needs a category", f => f.IsOpen && f.Kind == FindingKinds.Category && IsUnknown(f)),
            new Section("details", "Document details", f => f.IsOpen && f.Kind == FindingKinds.Summary),
            new Section("hearings", "Hearings", f => f.IsOpen && f.Kind == FindingKinds.Hearing),
            new Section("events", "Events & timeline", f => f.IsOpen && f.Kind is FindingKinds.TimelineEvent or FindingKinds.ChronologyEntry or FindingKinds.Safeguarding),
            new Section("orders", "Orders", f => f.IsOpen && f.Kind == FindingKinds.Order),
            new Section("directions", "Directions & deadlines", f => f.IsOpen && f.Kind is FindingKinds.Direction or FindingKinds.Deadline),
            new Section("tasks", "Tasks & missing documents", f => f.IsOpen && f.Kind is FindingKinds.Task or FindingKinds.MissingDocument),
            new Section("contact", "Contact", f => f.IsOpen && f.Kind == FindingKinds.Contact),
            new Section("people", "Parties & children", f => f.IsOpen && f.Kind is FindingKinds.Party or FindingKinds.Child),
            new Section("complaints", "Complaints", f => f.IsOpen && f.Kind == FindingKinds.Complaint),
            new Section("scott", "Scott schedule", f => f.IsOpen && f.Kind == FindingKinds.ScottItem),
            new Section("versions", "Duplicates & versions", _ => false),
            new Section("conflicts", "Conflicts & checks", f => f.IsOpen && (f.Kind is FindingKinds.Conflict or FindingKinds.Issue || f.ConflictGroup.Length > 0)),
            new Section("low", "Low confidence", f => f.IsOpen && f.Confidence < BulkThresholds.Medium),
            new Section("failed", "Failed or not analysed", _ => false),
            new Section("approved", "Approved", f => f.StatusValue == FindingStatus.Approved),
            new Section("auto", "Applied by AI rule", f => f.StatusValue == FindingStatus.AutoApplied),
            new Section("deferred", "Deferred", f => f.StatusValue == FindingStatus.Deferred),
            new Section("rejected", "Rejected", f => f.StatusValue == FindingStatus.Rejected)
        };

        var root = new Grid { Margin = new Thickness(24, 20, 24, 16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.9, GridUnitType.Star), MinWidth = 380 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });

        // ---------------------------------------------------------------- sections
        var left = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(Ui.Text("Import review", "H1"));
        head.Children.Add(Ui.Muted("AI suggestions wait here. Nothing reaches your case until you approve it.", 12));
        var start = Ui.Button("Import a folder…", "", (_, _) => Shell.OpenBulkImport(), primary: true);
        start.Margin = new Thickness(0, 10, 0, 4);
        start.HorizontalAlignment = HorizontalAlignment.Left;
        head.Children.Add(start);
        var check = Ui.Button("Check my library…", "\uE9D5", (_, _) => Shell.OpenLibraryCheck(),
            tooltip: "Organise the documents already in the library, find copies and versions, and fill in the case sections");
        check.Margin = new Thickness(0, 0, 0, 8);
        check.HorizontalAlignment = HorizontalAlignment.Left;
        head.Children.Add(check);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var options = Ui.Button("Review options…", "", (_, _) => ShowOptions(), tooltip: "Confidence levels, AI speed and the advanced auto-apply rule");
        DockPanel.SetDock(options, Dock.Bottom);
        left.Children.Add(options);
        _sections = new ListBox { BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(_sections, "Review sections");
        _sections.SelectionChanged += (_, _) =>
        {
            if (_sections.SelectedItem is SectionItem s && s.Key != _current) { _current = s.Key; ShowSection(); }
        };
        left.Children.Add(_sections);
        root.Children.Add(left);

        // ---------------------------------------------------------------- list
        var middle = new DockPanel();
        Grid.SetColumn(middle, 2);
        var top = new StackPanel();
        _title = Ui.Text("", "H2");
        _subtitle = Ui.Muted("", 12);
        top.Children.Add(_title);
        top.Children.Add(_subtitle);
        var filters = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        filters.Children.Add(Ui.SearchBox(out _search, "Search suggestions…", 200));
        AutomationProperties.SetName(_search, "Search suggestions");
        _search.TextChanged += (_, _) => Refilter();
        _confidence = new ComboBox { ItemsSource = new[] { "Any confidence", "High", "Medium", "Low" }, SelectedIndex = 0, Width = 140, Margin = new Thickness(0, 0, 6, 6) };
        AutomationProperties.SetName(_confidence, "Confidence");
        _confidence.SelectionChanged += (_, _) => Refilter();
        filters.Children.Add(_confidence);
        _document = new ComboBox { Width = 220, Margin = new Thickness(0, 0, 6, 6), DisplayMemberPath = "Title" };
        AutomationProperties.SetName(_document, "Document");
        SearchableCombo.Attach(_document, o => (o as CaseDocument)?.Title ?? "");
        _document.SelectionChanged += (_, _) => Refilter();
        // Keyboard focus lands in the editable box inside the drop-down, so that needs the name too
        _document.Loaded += (_, _) =>
        {
            if (_document.Template?.FindName("PART_EditableTextBox", _document) is TextBox box) AutomationProperties.SetName(box, "Document");
        };
        filters.Children.Add(_document);
        _from = new DatePicker { Width = 130, Margin = new Thickness(0, 0, 6, 6) };
        _to = new DatePicker { Width = 130, Margin = new Thickness(0, 0, 6, 6) };
        AutomationProperties.SetName(_from, "Dates from");
        AutomationProperties.SetName(_to, "Dates to");
        _from.SelectedDateChanged += (_, _) => Refilter();
        _to.SelectedDateChanged += (_, _) => Refilter();
        filters.Children.Add(_from);
        filters.Children.Add(_to);
        _warningsOnly = new CheckBox { Content = "Only with warnings", Margin = new Thickness(0, 6, 6, 6) };
        _warningsOnly.Checked += (_, _) => Refilter();
        _warningsOnly.Unchecked += (_, _) => Refilter();
        filters.Children.Add(_warningsOnly);
        top.Children.Add(filters);
        _filters = filters;
        top.Children.Add(_bulkBar);
        DockPanel.SetDock(top, Dock.Top);
        middle.Children.Add(top);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, EnableRowVirtualization = true
        };
        AutomationProperties.SetName(_grid, "Suggestions");
        _grid.Columns.Add(Col("Status", nameof(AiFinding.StatusText), 96, true));
        _grid.Columns.Add(Col("Suggestion", nameof(AiFinding.ProposedAction), 2.4));
        _grid.Columns.Add(Col("Date", nameof(AiFinding.DateDisplay), 92, true, nameof(AiFinding.DateValue)));
        _grid.Columns.Add(Col("Confidence", nameof(AiFinding.ConfidenceText), 104, true, nameof(AiFinding.Confidence)));
        _grid.Columns.Add(Col("Document", nameof(AiFinding.DocumentTitle), 1.1));
        _grid.Columns.Add(Col("Page", nameof(AiFinding.PageText), 52, true, nameof(AiFinding.PageFrom)));
        _grid.Columns.Add(Col("Check", nameof(AiFinding.WarningSummary), 1.2));
        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.MouseDoubleClick += (_, e) => { if (Selected().Count == 1 && e.OriginalSource is FrameworkElement) OpenSource(Selected()[0]); };
        _grid.PreviewKeyDown += OnGridKey;

        _files = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        AutomationProperties.SetName(_files, "Documents that failed or weren't analysed");
        _files.Columns.Add(Col("File", nameof(ImportFile.RelativePath), 2));
        _files.Columns.Add(Col("Import", nameof(ImportFile.StatusText), 1));
        _files.Columns.Add(Col("Analysis", nameof(ImportFile.AnalysisText), 1.2));
        _files.Columns.Add(Col("Problem", nameof(ImportFile.ErrorMessage), 2));
        middle.Children.Add(_middle);
        root.Children.Add(middle);

        // ---------------------------------------------------------------- detail
        var detailHost = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(14, 0, 0, 0) };
        detailHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetColumn(detailHost, 3);
        detailHost.Child = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(detailHost, "Selected suggestion");
        root.Children.Add(detailHost);

        Content = root;
        Loaded += (_, _) => Reload();
    }

    private static DataGridTextColumn Col(string header, string path, double width, bool fixedWidth = false, string sort = null) => new()
    {
        Header = header, Binding = new Binding(path) { Mode = BindingMode.OneWay }, SortMemberPath = sort ?? path,
        Width = fixedWidth ? new DataGridLength(width) : new DataGridLength(width, DataGridLengthUnitType.Star),
        ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis) } }
    };

    private bool IsUnknown(AiFinding f) => FindingApprover.PLong(f, "categoryId") is not { } c || c == _unknownId;

    // ================================================================== loading
    public void Reload()
    {
        try
        {
            _unknownId = CategoryMatcher.Unknown(Shell.Categories)?.Id ?? 0;
            _docs = Db.GetDocuments().Concat(Db.GetDeletedDocuments()).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
            _all = Db.Query<AiFinding>("Status<>$s", ("$s", (int)FindingStatus.Superseded));
            foreach (var f in _all) f.DocumentTitle = _docs.TryGetValue(f.DocumentId, out var d) ? d.Title : "(deleted document)";
            _fileRows = Db.Query<ImportFile>("(AnalysisStatus IN ($a,$b,$c) AND DocumentId IS NOT NULL) OR Status IN ($d,$e,$f)",
                ("$a", (int)AnalysisState.Failed), ("$b", (int)AnalysisState.NoText), ("$c", (int)AnalysisState.AwaitingAi),
                ("$d", (int)ImportFileStatus.Failed), ("$e", (int)ImportFileStatus.Missing), ("$f", (int)ImportFileStatus.Inaccessible))
                .Where(f => !f.ManuallyReviewed).ToList();
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("Review", "Couldn't load import suggestions", ex);
            _title.Text = "Import review couldn't be loaded";
            _subtitle.Text = $"Reference: {id}";
            return;
        }

        var keepDoc = (_document.SelectedItem as CaseDocument)?.Id ?? 0;
        var docChoices = new List<CaseDocument> { new() { Id = 0, Title = "All documents" } };
        docChoices.AddRange(_all.Select(f => f.DocumentId).Distinct().Where(_docs.ContainsKey).Select(id => _docs[id]).OrderBy(d => d.Title, NaturalComparer.Instance));
        _document.ItemsSource = docChoices;
        _document.SelectedItem = docChoices.FirstOrDefault(d => d.Id == keepDoc) ?? docChoices[0];

        var items = _defs.Select(s => new SectionItem
        {
            Key = s.Key, Label = s.Label,
            Count = s.Key == "summary" ? _all.Count(f => f.IsOpen) : s.Key == "failed" ? _fileRows.Count : s.Key == "versions" ? OpenVersionGroups : _all.Count(s.Match)
        }).ToList();
        // Copies and versions are worked out in the background, and only again when the library changes
        _ = RefreshVersionsAsync();
        _sections.ItemsSource = items;
        _sections.SelectedItem = items.FirstOrDefault(i => i.Key == _current) ?? items[0];
        ShowSection();
    }

    // ================================================================== sections
    private void ShowSection()
    {
        var def = _defs.First(d => d.Key == _current);
        _title.Text = def.Label;
        _bulkBar.Children.Clear();
        _filters.Visibility = _current is "summary" or "failed" or "versions" ? Visibility.Collapsed : Visibility.Visible;
        if (_current == "summary") { ShowSummary(); return; }
        if (_current == "failed") { ShowFailed(); return; }
        if (_current == "versions") { ShowVersions(); return; }

        _subtitle.Text = _current switch
        {
            "categories" => "Approving files the document under that category. You can approve several at once.",
            "unknown" => "The AI couldn't match these with confidence. Pick a category with Edit & approve, or leave them for later.",
            "details" => "The document's own date, author and a short summary, as the AI read them.",
            "conflicts" => "Found by comparing documents. Nothing has been changed – compare the sources and decide.",
            "low" => "The AI wasn't sure about these. Check each one carefully against the document.",
            "approved" or "auto" => "Already added to your case. Undo removes what was added and puts the suggestion back.",
            "rejected" or "deferred" => "Use Reconsider to put one back in the review list.",
            _ => "Check each against the document before approving. Allegations are shown as what someone says, never as fact."
        };
        if (_current is "categories" or "unknown" or "low")
            _bulkBar.Children.Add(Ui.Button("Approve selected categories", "", (_, _) => ApproveSelectedCategories(), primary: _current == "categories",
                tooltip: "Files each selected document under its suggested category (medium or high confidence only)"));
        if (def.Key is not ("approved" or "auto" or "rejected"))
        {
            _bulkBar.Children.Add(Ui.Button("Reject selected", "", (_, _) => RejectSelected()));
            _bulkBar.Children.Add(Ui.Button("Defer selected", "", (_, _) => DeferSelected()));
        }
        _middle.Content = _grid;
        Refilter();
    }

    private void Refilter()
    {
        if (_current is "summary" or "failed" or "versions" || _search == null) return;
        var def = _defs.First(d => d.Key == _current);
        var q = _search.Text.Trim();
        var docId = (_document.SelectedItem as CaseDocument)?.Id ?? 0;
        var band = _confidence.SelectedItem as string;
        var list = _all.Where(def.Match)
            .Where(f => q.Length == 0 || $"{f.Title} {f.Detail} {f.Excerpt} {f.DocumentTitle} {f.KindText} {f.AllWarnings}".Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(f => docId == 0 || f.DocumentId == docId)
            .Where(f => band is null or "Any confidence" || f.ConfidenceBand == band)
            .Where(f => _from.SelectedDate is not { } a || f.DateValue >= a)
            .Where(f => _to.SelectedDate is not { } b || f.DateValue <= b)
            .Where(f => _warningsOnly.IsChecked != true || f.HasWarnings)
            .OrderBy(f => f.Kind == FindingKinds.Conflict ? 0 : 1)
            .ThenByDescending(f => f.CheckNotes.Contains("Coming up"))
            .ThenBy(f => f.DateValue ?? DateTime.MaxValue).ThenBy(f => f.Id)
            .ToList();
        var keep = Selected().Select(f => f.Id).ToHashSet();
        _grid.ItemsSource = list;
        foreach (var f in list.Where(f => keep.Contains(f.Id))) _grid.SelectedItems.Add(f);
        _subtitle.Text = _subtitle.Text.Split("  ·  ")[0] + $"  ·  {list.Count:N0} shown";
        if (_grid.SelectedItems.Count == 0) ShowDetail();
    }

    private List<AiFinding> Selected() => _grid.SelectedItems.Cast<AiFinding>().ToList();

    // ---- summary
    private void ShowSummary()
    {
        int open = _all.Count(f => f.IsOpen);
        _subtitle.Text = open > 0 ? $"{open:N0} suggestion(s) to check." : "Nothing waiting for review.";
        var sp = new StackPanel();
        if (LibraryCheckCard() is { } card) sp.Children.Add(card);
        var counts = new WrapPanel();
        foreach (var def in _defs.Where(d => d.Key is not ("summary" or "approved" or "auto" or "rejected" or "deferred")))
        {
            int n = def.Key == "failed" ? _fileRows.Count : def.Key == "versions" ? OpenVersionGroups : _all.Count(def.Match);
            if (n == 0) continue;
            var key = def.Key;
            var b = Ui.Button($"{def.Label}: {n:N0}", null, (_, _) => Go(key));
            counts.Children.Add(b);
        }
        if (counts.Children.Count > 0)
        {
            sp.Children.Add(Ui.Text("Waiting for you", "H2"));
            sp.Children.Add(counts);
        }

        int uncategorised = Shell.Documents.Count(d => d.CategoryId == _unknownId);
        if (uncategorised > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(Ui.Text($"{uncategorised:N0} document(s) are still in Uncategorised.", null));
            row.Children.Add(Ui.Button("Show them", "", (_, _) => Shell.Navigate("go", "documents")));
            sp.Children.Add(row);
        }

        sp.Children.Add(Ui.Text("AI", "H2"));
        sp.Children.Add(Ui.Muted(AiService.Enabled
            ? (AiProviders.IsCloud(AiService.BulkProvider)
                ? $"Using {AiProviders.Get(AiService.BulkProvider).Name}{(AiService.MaskCloud ? ", with personal details masked" : ", masking OFF")}. You'll see what will be sent before anything goes."
                : $"Using {(string.IsNullOrWhiteSpace(AiService.Model) ? "no model chosen yet" : AiService.Model)} on your own Ollama server. Only that server is contacted.")
            : "AI is switched off. Documents are still imported, and CaseKeeper's own rules suggest categories from form codes and names.", 12.5));
        sp.Children.Add(Ui.Muted("Confidence is how strongly the document supports a suggestion. High confidence means the document strongly " +
                                 "matches, but you should still confirm it. The AI does not make legal decisions or give legal advice.", 12.5));

        // ---- jobs
        List<ImportJob> jobs;
        try { jobs = Db.Query<ImportJob>("").OrderByDescending(j => j.Id).Take(12).ToList(); }
        catch (Exception ex) { AppLog.Warn("Review", "Couldn't list import jobs", ex); jobs = new(); }
        if (jobs.Count > 0)
        {
            sp.Children.Add(Ui.Text("Imports", "H2"));
            foreach (var j in jobs) sp.Children.Add(JobCard(j));
        }
        else sp.Children.Add(Ui.Muted("No imports yet. Use “Import a folder…” to bring in a whole case folder, or “Check my library…” to organise what's already here.", 12.5));
        _middle.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _detail.Children.Clear();
        _detail.Children.Add(Ui.Muted("Choose a section on the left to start reviewing. Press F1 for help.", 12.5));
    }

    /// <summary>The latest Library check: what it found, each line linking to where it waits.</summary>
    private UIElement LibraryCheckCard()
    {
        ImportJob job;
        try { job = Db.Query<ImportJob>("").OrderByDescending(j => j.Id).FirstOrDefault(j => BulkImportService.ReadOptions(j).LibraryCheck); }
        catch (Exception ex) { AppLog.Warn("Review", "Couldn't read the library check", ex); return null; }
        if (job == null) return null;
        var options = BulkImportService.ReadOptions(job);
        var counts = Db.CountImportFiles(job.Id);
        var open = _all.Where(f => f.JobId == job.Id && f.IsOpen).ToList();
        int Open(params string[] kinds) => open.Count(f => kinds.Contains(f.Kind));
        int noText = counts.GetValueOrDefault("a" + (int)AnalysisState.NoText);
        bool running = Shell.Bulk.IsRunning && Shell.Bulk.RunningJobId == job.Id;

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = $"Library check  ·  {job.CreatedText}", FontWeight = FontWeights.SemiBold, FontSize = 15 });
        sp.Children.Add(Ui.Muted($"{(running ? "Running" : job.StateText)} · {(options.RunAi ? "with AI" + (job.Model.Length > 0 ? $" ({job.Model})" : "") : "with CaseKeeper's own rules (no AI)")}", 12));
        var lines = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Line(string text, int n, string key)
        {
            if (n <= 0) return;
            var b = Ui.Button($"{text}: {n:N0}", null, (_, _) => Go(key));
            lines.Children.Add(b);
        }
        Line("Categories to check", Open(FindingKinds.Category), "categories");
        Line("Parties & children", Open(FindingKinds.Party, FindingKinds.Child), "people");
        Line("Hearings", Open(FindingKinds.Hearing), "hearings");
        Line("Orders", Open(FindingKinds.Order), "orders");
        Line("Directions & deadlines", Open(FindingKinds.Direction, FindingKinds.Deadline), "directions");
        Line("Timeline", Open(FindingKinds.TimelineEvent, FindingKinds.ChronologyEntry, FindingKinds.Safeguarding), "events");
        Line("Complaints", Open(FindingKinds.Complaint), "complaints");
        Line("Scott schedule", Open(FindingKinds.ScottItem), "scott");
        Line("Copies & versions", OpenVersionGroups, "versions");
        Line("No readable text", noText, "failed");
        if (lines.Children.Count == 0) lines.Children.Add(Ui.Muted(running ? "Still checking…" : "Nothing new to review from this check.", 12));
        sp.Children.Add(lines);
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(Ui.Button("Open the Case map", "\uE81E", (_, _) => Shell.GoTo("casemap")));
        if (AiService.Enabled)
            actions.Children.Add(Ui.Button(CaseOverview.Latest(Db) == null ? "Write case overview" : "Refresh case overview", "\uE945", (_, _) => _ = Shell.WriteCaseOverviewAsync()));
        sp.Children.Add(actions);
        return Ui.Card(sp, new Thickness(0, 0, 0, 12));
    }

    private UIElement JobCard(ImportJob j)
    {
        var counts = Db.CountImportFiles(j.Id);
        var sp = new StackPanel();
        bool running = Shell.Bulk.IsRunning && Shell.Bulk.RunningJobId == j.Id;
        sp.Children.Add(new TextBlock { Text = $"{j.RootName}  ·  {j.CreatedText}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Ui.Muted($"{(running ? "Running" : j.StateText)} · {j.PhaseText}" +
                                 (j.Model.Length > 0 ? $" · AI model {j.Model}" : "") +
                                 (j.LastErrorCode is "AI_UNAVAILABLE" ? " · the AI couldn't be reached" : j.LastErrorCode is "AI_OFF" ? " · AI was switched off" : j.LastErrorCode is "AI_CLOUD_DECLINED" ? " · not sent to the cloud AI (you chose not to)" : j.LastErrorCode is "AI_NOT_REQUESTED" ? " · CaseKeeper's own rules only (no AI)" : ""), 12));
        sp.Children.Add(Ui.Muted(BulkImportService.Summarise(counts), 11.5));
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        int waiting = counts.GetValueOrDefault("a" + (int)AnalysisState.AwaitingAi);
        int failed = counts.GetValueOrDefault("a" + (int)AnalysisState.Failed);
        int analysed = counts.GetValueOrDefault("a" + (int)AnalysisState.Done);
        // Analysed with a different prompt, model or set of checks than CaseKeeper would use now?
        bool outOfDate = analysed > 0 && j.Model.Length > 0 &&
                         (j.Model != AiService.Model || j.AnalysisVersion != AnalysisSchema.AnalysisVersion ||
                          j.SchemaVersion != AnalysisSchema.Version || j.PromptVersion != AiService.BulkAnalysisSettings().PromptVersion);
        if (outOfDate)
            sp.Children.Add(Ui.Muted("These documents were analysed with a different AI model, prompt or set of checks from the ones in use now. " +
                                     "Re-analysing replaces the suggestions you haven't decided on; decisions you've made are kept.", 12));
        if (running)
            row.Children.Add(Ui.Button("Pause", "", (_, _) => { Shell.Bulk.RequestPause(); Reload(); }));
        else
        {
            if (j.StateValue != ImportJobState.Completed)
                row.Children.Add(Ui.Button("Carry on", "", (_, _) => Shell.ResumeBulkJob(j.Id), primary: true));
            if (waiting > 0) row.Children.Add(Ui.Button($"Analyse {waiting} waiting", "", (_, _) => Reanalyse(j.Id, AnalysisState.AwaitingAi)));
            if (failed > 0) row.Children.Add(Ui.Button($"Retry {failed} failed", "", (_, _) => Reanalyse(j.Id, AnalysisState.Failed)));
            if (outOfDate) row.Children.Add(Ui.Button("Re-analyse with current settings", "", (_, _) => Reanalyse(j.Id, AnalysisState.Done)));
        }
        row.Children.Add(Ui.Button("History", "", (_, _) => Shell.ShowAuditFor("bulk import", j.Id)));
        sp.Children.Add(row);
        return Ui.Card(sp, new Thickness(0, 0, 0, 10));
    }

    private void Go(string key)
    {
        _current = key;
        if (_sections.ItemsSource is IEnumerable<SectionItem> items) _sections.SelectedItem = items.FirstOrDefault(i => i.Key == key);
        ShowSection();
    }

    // ---- failed / not analysed
    private void ShowFailed()
    {
        _subtitle.Text = "Documents whose analysis failed, that have no readable text, or that are waiting for the AI. Files that couldn't be imported are listed too.";
        _files.ItemsSource = _fileRows;
        _bulkBar.Children.Add(Ui.Button("Retry selected", "", (_, _) => RetryFiles(), primary: true, tooltip: "Analyse the selected documents again"));
        _bulkBar.Children.Add(Ui.Button("Open", "", (_, _) => { foreach (var f in SelectedFiles().Where(f => f.DocumentId != null).Take(5)) Shell.OpenDocumentById(f.DocumentId!.Value); }));
        _bulkBar.Children.Add(Ui.Button("Mark as reviewed by hand", "", (_, _) =>
        {
            foreach (var f in SelectedFiles().Where(f => f.DocumentId != null)) FindingApprover.MarkDocumentReviewed(Db, f.DocumentId!.Value);
            Reload();
        }));
        _middle.Content = _files;
        _detail.Children.Clear();
        _detail.Children.Add(Ui.Muted("“No readable text” usually means a photo, a handwritten page or a blank scan. Open it and record anything " +
                                      "important yourself, then mark it as reviewed.\n\n“Waiting for AI” means your local AI wasn't available; " +
                                      "start Ollama and choose Retry.", 12.5));
    }

    private List<ImportFile> SelectedFiles() => _files.SelectedItems.Cast<ImportFile>().ToList();

    private void RetryFiles()
    {
        var files = SelectedFiles().Where(f => f.DocumentId != null).ToList();
        if (files.Count == 0) { MessageBox.Show(Ui.Owner(this), "Select one or more documents first.", "Retry"); return; }
        if (BusyRunning() || !AiService.EnsureEnabled(Ui.Owner(this), AiService.BulkProvider)) return;
        foreach (var g in files.GroupBy(f => f.JobId)) Shell.Bulk.QueueReanalysis(g.Key, g.Select(f => f.DocumentId!.Value));
        _ = RunQueuedAsync(files.Select(f => f.JobId).Distinct().ToList());
    }

    /// <summary>A job can't be changed while one is running (its saved state would overwrite the change).</summary>
    private bool BusyRunning()
    {
        if (!Shell.Bulk.IsRunning) return false;
        MessageBox.Show(Ui.Owner(this), "A bulk import is running. Try again when it has finished (or pause it first).", "Import review");
        return true;
    }

    private void Reanalyse(long jobId, AnalysisState which)
    {
        if (BusyRunning() || !AiService.EnsureEnabled(Ui.Owner(this), AiService.BulkProvider)) return;
        var docs = Db.Query<ImportFile>("JobId=$j AND AnalysisStatus=$s AND DocumentId IS NOT NULL", ("$j", jobId), ("$s", (int)which))
            .Select(f => f.DocumentId!.Value).ToList();
        Shell.Bulk.QueueReanalysis(jobId, docs);
        _ = RunQueuedAsync(new List<long> { jobId });
    }

    private async Task RunQueuedAsync(List<long> jobs)
    {
        foreach (var j in jobs)
        {
            if (Shell.Bulk.IsRunning) { MessageBox.Show(Ui.Owner(this), "A bulk import is already running. Try again when it has finished.", "Import review"); return; }
            await Shell.RunBulkJobAsync(j);
        }
        Reload();
    }

    // ================================================================== detail
    private void ShowDetail()
    {
        _detail.Children.Clear();
        var sel = Selected();
        if (sel.Count == 0)
        {
            _detail.Children.Add(Ui.Muted("Select a suggestion to see where it came from and decide what to do with it.", 12.5));
            return;
        }
        if (sel.Count > 1)
        {
            _detail.Children.Add(Ui.Text($"{sel.Count} suggestions selected", "H2"));
            _detail.Children.Add(Ui.Muted("Use the buttons above the list to deal with them together, or select one to see it in full.", 12.5));
            return;
        }
        var f = sel[0];
        var heading = Ui.Text(f.ProposedAction, "H2");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        _detail.Children.Add(heading);
        _detail.Children.Add(Ui.Muted($"{f.KindText} · {f.StatusText}", 12.5));

        // ---- actions first, so they're reachable straight away by keyboard
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        if (f.IsOpen)
        {
            bool note = f.Kind is FindingKinds.Conflict or FindingKinds.Issue;
            var approve = Ui.Button(note ? "Mark as resolved" : "Approve", "", (_, _) => Approve(f, edit: false), primary: true,
                tooltip: note ? "Nothing is added; this just records that you've dealt with it" : "Add this to your case (Ctrl+Enter)");
            actions.Children.Add(approve);
            if (!note) actions.Children.Add(Ui.Button("Edit & approve", "", (_, _) => Approve(f, edit: true), tooltip: "Check and change the details first (F2)"));
            if (f.Kind == FindingKinds.Deadline || (f.Kind == FindingKinds.Direction && FindingApprover.NeedsCalculation(f)))
                actions.Children.Add(Ui.Button("Work out the date…", "", (_, _) => WorkOutDeadline(f), tooltip: "Use the deadline calculator with a date and rule you check"));
            actions.Children.Add(Ui.Button("Reject", "", (_, _) => Decide(new[] { f }, FindingApprover.Reject), tooltip: "Don't add this (Delete)"));
            if (f.StatusValue == FindingStatus.Proposed) actions.Children.Add(Ui.Button("Defer", "", (_, _) => Decide(new[] { f }, FindingApprover.Defer), tooltip: "Decide later"));
            else actions.Children.Add(Ui.Button("Reconsider", "", (_, _) => Decide(new[] { f }, FindingApprover.Reconsider)));
        }
        else if (f.StatusValue is FindingStatus.Approved or FindingStatus.AutoApplied)
        {
            actions.Children.Add(Ui.Button("Undo", "", (_, _) => Undo(f), tooltip: "Remove what was added and put the suggestion back"));
            if (f.CreatedRecordType.Length > 0) actions.Children.Add(Ui.Button("Show it", "", (_, _) => ShowRecord(f)));
        }
        else if (f.StatusValue == FindingStatus.Rejected)
            actions.Children.Add(Ui.Button("Reconsider", "", (_, _) => Decide(new[] { f }, FindingApprover.Reconsider)));
        _detail.Children.Add(actions);

        var blocker = f.IsOpen ? FindingApprover.Blocker(f, SafeDraft(f)) : null;
        if (blocker != null) _detail.Children.Add(Ui.Callout("Before approving: " + blocker));

        // ---- warnings, never colour alone
        if (f.HasWarnings)
        {
            _detail.Children.Add(Label("CHECK THIS"));
            foreach (var w in f.AllWarnings.Split('\n').Where(w => w.Trim().Length > 0))
            {
                var t = new TextBlock { Text = "⚠ " + w.Trim(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                AutomationProperties.SetName(t, "Warning: " + w.Trim());
                _detail.Children.Add(t);
            }
        }

        // ---- where it came from
        _detail.Children.Add(Label("SOURCE"));
        var src = new WrapPanel();
        src.Children.Add(new TextBlock { Text = $"{f.DocumentTitle}  ·  {f.PageText}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        src.Children.Add(Ui.Button(f.PageFrom is { } p ? $"Open at page {p}" : "Open document", "", (_, _) => OpenSource(f)));
        _detail.Children.Add(src);
        if (!string.IsNullOrWhiteSpace(f.Excerpt))
        {
            var quote = Ui.Callout("“" + f.Excerpt + "”");
            AutomationProperties.SetName(quote, "Quoted from the document: " + f.Excerpt);
            _detail.Children.Add(quote);
        }

        // ---- the substance
        if (f.DateValue != null || f.DateText.Length > 0)
        {
            _detail.Children.Add(Label("DATE"));
            _detail.Children.Add(Ui.Text($"{f.DateDisplay}{(f.RoleText.Length > 0 ? "  ·  " + f.RoleText : "")}{(f.TemporalText.Length > 0 ? "  ·  " + f.TemporalText : "")}"));
            if (f.DateText.Length > 0) _detail.Children.Add(Ui.Muted($"Written in the document as “{f.DateText}”{(f.DatePrecision is "exact" or "unknown" ? "" : $" ({f.DatePrecision})")}.", 12));
        }
        if (f.AssertedBy.Length > 0)
        {
            _detail.Children.Add(Label("WHO SAYS SO"));
            _detail.Children.Add(Ui.Text(f.AssertedBy + " (this is what they say, not a finding of fact)"));
        }
        if (f.Detail.Length > 0)
        {
            _detail.Children.Add(Label("DETAILS"));
            _detail.Children.Add(Ui.Text(f.Detail));
        }
        var facts = Facts(f);
        if (facts.Count > 0)
        {
            _detail.Children.Add(Label("WHAT THE DOCUMENT GIVES"));
            foreach (var (k, v) in facts) _detail.Children.Add(Ui.Text($"{k}: {v}"));
        }

        _detail.Children.Add(Label("CONFIDENCE"));
        _detail.Children.Add(Ui.Text(f.ConfidenceText));
        _detail.Children.Add(Ui.Muted(f.ConfidenceBand switch
        {
            "High" => "High confidence means the document strongly supports this, but you should still confirm it.",
            "Medium" => "Medium confidence: the document seems to support this. Check it before approving.",
            _ => "Low confidence: the AI wasn't sure. Check carefully, or reject it."
        }, 12));
        if (f.Reason.Length > 0)
        {
            _detail.Children.Add(Label("WHY IT WAS SUGGESTED"));
            _detail.Children.Add(Ui.Text(f.Reason));
        }

        // ---- conflicts and duplicates side by side
        if (f.ConflictGroup.Length > 0)
        {
            var related = _all.Where(o => o.ConflictGroup == f.ConflictGroup && o.Id != f.Id).ToList();
            if (related.Count > 0)
            {
                _detail.Children.Add(Label("COMPARE WITH"));
                foreach (var o in related)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var open = Ui.Button("Open", null, (_, _) => OpenSource(o), tooltip: "Open the other document");
                    DockPanel.SetDock(open, Dock.Right);
                    row.Children.Add(open);
                    row.Children.Add(Ui.Text($"{o.DateDisplay} · {AnalysisSchema.Clip(o.Title, 70)} · {o.DocumentTitle} {o.PageText} · {o.StatusText}"));
                    _detail.Children.Add(row);
                }
            }
        }

        _detail.Children.Add(Label("SUGGESTED BY"));
        _detail.Children.Add(Ui.Muted($"{(string.IsNullOrEmpty(f.Model) ? "—" : f.Model)} · prompt {f.PromptVersion} · schema {f.SchemaVersion} · checks {f.AnalysisVersion} · {f.CreatedAt:dd MMM yyyy HH:mm}", 11.5));
        if (f.DecidedAt is { } at) _detail.Children.Add(Ui.Muted($"Decided {at:dd MMM yyyy HH:mm}.", 11.5));
        var more = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        more.Children.Add(Ui.Button("Re-run AI on this document", "", (_, _) => RerunDocument(f)));
        more.Children.Add(Ui.Button("Mark document reviewed", "", (_, _) => { FindingApprover.MarkDocumentReviewed(Db, f.DocumentId); Reload(); }));
        more.Children.Add(Ui.Button("History", "", (_, _) => Shell.ShowAuditFor("AI finding", f.Id)));
        _detail.Children.Add(more);
    }

    private static TextBlock Label(string text) => new() { Text = text, Style = Ui.Style("Label"), Margin = new Thickness(0, 12, 0, 3) };

    /// <summary>Kind-specific details in plain words, with category ids turned into names.</summary>
    private List<(string, string)> Facts(AiFinding f)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(f.PayloadJson)) return list;
        string CatName(string id) => long.TryParse(id, out var c) ? Shell.Categories.FirstOrDefault(x => x.Id == c)?.DisplayName ?? "" : "";
        try
        {
            using var doc = JsonDocument.Parse(f.PayloadJson);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => "yes",
                    JsonValueKind.Array => string.Join(", ", p.Value.EnumerateArray().Select(e => p.Name == "alternativeCategoryIds" ? CatName(e.GetRawText()) : e.ToString())),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(v)) continue;
                var label = p.Name switch
                {
                    "categoryId" => null, "bestGuessId" => "Best guess", "alternativeCategoryIds" => "Other possible categories",
                    "needsCalculation" => null, "hearingType" => "Type of hearing", "attendanceMode" => "Attendance", "hearingStatus" => "Status",
                    "timeEstimate" => "Time estimate", "previousDate" => "Previously listed for", "responsibleParty" => "Who must do it",
                    "actionRequired" => "What must be done", "dueDate" => "Due", "dueDateText" => null, "sourceDate" => "Counted from",
                    "calculationRule" => "Rule in the order", "wording" => null, "dateOfBirth" => "Date of birth", "dateOfBirthText" => null,
                    "orderType" => "Type of order", "madeBy" => "Made by", "contactOutcome" => "Outcome", "caseNumber" => "Case number",
                    "documentType" => "Type of document",
                    "responseDue" => "Reply due", "responseDueText" => "Reply expected (as written)", "isResponse" => null,
                    "contactDetails" => "Contact details", "allegedBy" => "Alleged by", "findingSought" => "Finding sought",
                    _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p.Name)
                };
                if (label == null) continue;
                if (p.Name == "bestGuessId") v = CatName(v);
                list.Add((label, v));
            }
        }
        catch (JsonException) { /* nothing to show */ }
        return list;
    }

    // ================================================================== decisions
    private object SafeDraft(AiFinding f)
    {
        try { return FindingApprover.Draft(Db, f); }
        catch (Exception ex) { AppLog.Warn("Review", $"Couldn't prepare finding #{f.Id}", ex); return null; }
    }

    private void Approve(AiFinding f, bool edit, string note = null, object prepared = null, bool alreadyEdited = false)
    {
        var owner = Ui.Owner(this);
        var draft = prepared ?? SafeDraft(f);
        bool isNote = draft == null && f.Kind is FindingKinds.Conflict or FindingKinds.Issue;
        if (draft == null && !isNote)
        {
            MessageBox.Show(owner, "This suggestion can't be added: the document it came from is no longer in the library.", "Import review");
            return;
        }
        if (!isNote && !alreadyEdited && (edit || FindingApprover.Blocker(f, draft) != null))
        {
            if (!edit && MessageBox.Show(owner, FindingApprover.Blocker(f, draft) + "\n\nOpen the form now?", "Needs a little more",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            if (!EditDraft(f, draft)) return;
            edit = true;
            if (FindingApprover.Blocker(f, draft) is { } still) { MessageBox.Show(owner, still, "Import review"); return; }
        }
        if (!isNote && FindingApprover.DescribeUpdate(Db, draft) is { } update &&
            MessageBox.Show(owner, update + "\n\nNothing already written is changed. You can undo this later.", "Add to an existing record",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;
        if (!isNote && FindingApprover.FindEquivalent(Db, f, draft) is { } dup &&
            MessageBox.Show(owner, dup + "\n\nAdd this as well anyway?", "Possible duplicate", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            FindingApprover.Approve(Db, f, draft, edit, note: note);
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("Review", $"Approving finding #{f.Id} failed", ex);
            MessageBox.Show(owner, $"That couldn't be added: {ex.Message}\nReference: {id}", "Import review");
            return;
        }
        AfterChange();
    }

    /// <summary>Opens the same form used to add that kind of record by hand, filled in from the suggestion.</summary>
    private bool EditDraft(AiFinding f, object draft)
    {
        var owner = Ui.Owner(this);
        var docs = Shell.Documents.ToList();
        switch (draft)
        {
            case CaseDocument d:
                return new DocumentEditWindow(d, Shell.Categories.ToList(), Shell.Parties.ToList()) { Owner = owner }.ShowDialog() == true;
            case Hearing h:
                return new RecordDialog("Check and add this hearing", h, CaseModules.HearingFields(), docs) { Owner = owner }.ShowDialog() == true;
            case CourtOrder o:
                return new RecordDialog("Check and add this order", o, CaseModules.OrderFields(), docs) { Owner = owner }.ShowDialog() == true;
            case CaseAction a:
                return new RecordDialog("Check and add this direction or task", a, CaseModules.ActionFields(), docs) { Owner = owner }.ShowDialog() == true;
            case ContactSession c:
                return new RecordDialog("Check and add this contact entry", c, CaseModules.ContactFields(), docs) { Owner = owner }.ShowDialog() == true;
            case TimelineEvent e:
                if (e.EventDate == DateTime.MinValue)
                {
                    MessageBox.Show(owner, f.DateText.Length > 0
                        ? $"The document gives the date as “{f.DateText}”, which isn't an exact day. Enter the right date in the next window."
                        : "The document doesn't give a date for this. Enter the right date in the next window.", "Date needed");
                    e.EventDate = f.DateValue ?? DateTime.Today;
                }
                return new EventEditWindow(e, docs) { Owner = owner }.ShowDialog() == true;
            case Party p:
                return new PartyEditWindow(p) { Owner = owner }.ShowDialog() == true;
            case Complaint x:
                return new ComplaintEditWindow(x, docs) { Owner = owner }.ShowDialog() == true;
            case ScottItem s:
                return new RecordDialog("Check and add this allegation to the Scott schedule", s, CaseModules.ScottFields(), docs) { Owner = owner }.ShowDialog() == true;
        }
        return false;
    }

    private void WorkOutDeadline(AiFinding f)
    {
        if (SafeDraft(f) is not CaseAction draft) return;
        var dlg = new DeadlineFromOrderWindow(f, _docs.GetValueOrDefault(f.DocumentId)) { Owner = Ui.Owner(this) };
        if (dlg.ShowDialog() != true) return;
        draft.DueDate = dlg.Result;
        draft.Notes = string.Join("\n", new[] { draft.Notes, dlg.Explanation }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!EditDraft(f, draft)) return;
        Approve(f, edit: true, note: "Deadline worked out with the deadline calculator: " + dlg.Explanation, prepared: draft, alreadyEdited: true);
    }

    private void Decide(IEnumerable<AiFinding> findings, Action<Data.Database, AiFinding> action)
    {
        foreach (var f in findings) action(Db, f);
        AfterChange();
    }

    private void RejectSelected()
    {
        var sel = Selected().Where(f => f.IsOpen).ToList();
        if (sel.Count == 0) return;
        if (sel.Count > 1 && MessageBox.Show(Ui.Owner(this), $"Reject {sel.Count} suggestions? Nothing will be added for them. You can reconsider them later from Rejected.",
                "Reject", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Decide(sel, FindingApprover.Reject);
    }

    private void DeferSelected() => Decide(Selected().Where(f => f.StatusValue == FindingStatus.Proposed).ToList(), FindingApprover.Defer);

    private void ApproveSelectedCategories()
    {
        var sel = Selected().Where(f => f.IsOpen && f.Kind == FindingKinds.Category).ToList();
        var ok = sel.Where(f => f.Confidence >= BulkThresholds.Medium && !IsUnknown(f) && SafeDraft(f) is { } d && FindingApprover.Blocker(f, d) == null).ToList();
        if (ok.Count == 0)
        {
            MessageBox.Show(Ui.Owner(this), "Select one or more category suggestions with medium or high confidence first. Low-confidence and unknown " +
                                            "ones need a category chosen with Edit & approve.", "Approve categories");
            return;
        }
        if (MessageBox.Show(Ui.Owner(this), $"File {ok.Count} document(s) under their suggested categories?" +
                                            (sel.Count > ok.Count ? $"\n\n{sel.Count - ok.Count} other selected suggestion(s) need checking individually and won't be changed." : "") +
                                            "\n\nEach change is recorded and can be undone.", "Approve categories",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var f in ok)
            try { FindingApprover.Approve(Db, f, FindingApprover.Draft(Db, f), edited: false); }
            catch (Exception ex) { AppLog.Warn("Review", $"Couldn't approve category finding #{f.Id}", ex); }
        AfterChange();
    }

    private void Undo(AiFinding f)
    {
        var what = f.CreatedRecordType switch
        {
            "document" => "the change to the document's details",
            "hearing" => "the hearing (and its timeline entry)",
            "event" => "the timeline entry",
            "order" => "the order",
            "action" => "the direction or task",
            "contact" => "the contact entry",
            "party" => "the party (their documents are kept, just unlinked)",
            "complaint" => "the complaint",
            _ => "nothing"
        };
        if (MessageBox.Show(Ui.Owner(this), $"Undo this approval?\n\nThis removes {what}, including any changes you've made to it since. " +
                                            "The suggestion goes back in the review list.", "Undo",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        FindingApprover.Undo(Db, f);
        AfterChange();
    }

    private void AfterChange()
    {
        Shell.RefreshAll();
        Shell.RefreshTimelineAndDashboard();
        Reload();
    }

    private void OpenSource(AiFinding f)
    {
        var doc = Db.GetDocument(f.DocumentId);
        if (doc == null) { MessageBox.Show(Ui.Owner(this), "That document is no longer in the library.", "Import review"); return; }
        Shell.OpenDocument(doc, f.PageFrom ?? 1);
    }

    private void ShowRecord(AiFinding f)
    {
        switch (f.CreatedRecordType)
        {
            case "document" when f.CreatedRecordId is { } d: Shell.OpenDocumentById(d); break;
            case "hearing": Shell.GoTo("hearings"); break;
            case "order": Shell.GoTo("orders"); break;
            case "event": Shell.GoTo("timeline"); break;
            case "action": Shell.GoTo("actions"); break;
            case "contact": Shell.GoTo("contact"); break;
            case "party": Shell.GoTo("parties"); break;
            case "complaint": Shell.GoTo("complaints"); break;
        }
    }

    private void RerunDocument(AiFinding f)
    {
        if (BusyRunning() || !AiService.EnsureEnabled(Ui.Owner(this), AiService.BulkProvider)) return;
        var file = Db.Query<ImportFile>("DocumentId=$d", ("$d", f.DocumentId)).OrderByDescending(x => x.Id).FirstOrDefault();
        long jobId;
        if (file != null)
        {
            Shell.Bulk.QueueReanalysis(file.JobId, new[] { f.DocumentId });
            jobId = file.JobId;
        }
        else if (Db.GetDocument(f.DocumentId) is { } doc) jobId = Shell.Bulk.CreateLibraryJob(new[] { doc }).Id;
        else return;
        _ = RunQueuedAsync(new List<long> { jobId });
    }

    private void OnGridKey(object sender, KeyEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && sel.Count == 1 && sel[0].IsOpen) { Approve(sel[0], edit: false); e.Handled = true; }
        else if (e.Key == Key.F2 && sel.Count == 1 && sel[0].IsOpen) { Approve(sel[0], edit: true); e.Handled = true; }
        else if (e.Key == Key.Delete) { RejectSelected(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && sel.Count == 1) { OpenSource(sel[0]); e.Handled = true; }
    }

    private void ShowOptions()
    {
        if (new ReviewOptionsWindow { Owner = Ui.Owner(this) }.ShowDialog() == true)
        {
            MainWindow.LoadBulkThresholds();
            Reload();
        }
    }
}

/// <summary>
/// Works out a deadline from an order's wording with the existing deadline calculator. The person
/// chooses (and confirms) the date counted from and the rule; CaseKeeper never guesses either.
/// </summary>
public class DeadlineFromOrderWindow : FormDialog
{
    private readonly DatePicker _from;
    private readonly ComboBox _method;
    private readonly TextBox _amount;
    private readonly TextBlock _result;
    private readonly CheckBox _checked;
    public DateTime? Result { get; private set; }
    public string Explanation { get; private set; } = "";

    public DeadlineFromOrderWindow(AiFinding f, CaseDocument source) : base("Work out the deadline", 620, 700, "Use this date")
    {
        AddHint("The document gives this deadline as a rule, not a date. Check the wording, choose the date it's counted from and how to " +
                "count, and CaseKeeper works it out using England & Wales bank holidays and the Family Procedure Rules' way of counting days.");
        var rule = FindingApprover.P(f, "calculationRule") ?? "";
        var wording = FindingApprover.P(f, "wording") ?? f.Excerpt;
        Fields.Children.Add(new TextBlock { Text = "THE ORDER SAYS", Style = Ui.Style("Label") });
        Fields.Children.Add(Ui.Callout("“" + (string.IsNullOrWhiteSpace(wording) ? f.Title : wording) + "”"));
        var start = UkDates.ParseIso(FindingApprover.P(f, "sourceDate")) ?? source?.DocumentDate;
        _from = Add("Counted from (for example the date of the order, or the hearing date when counting back)", new DatePicker { SelectedDate = start });
        AutomationProperties.SetName(_from, "Counted from");
        var (method, amount) = Guess(rule + " " + wording);
        _method = Add("How to count", new ComboBox { ItemsSource = Deadlines.Methods, SelectedItem = method });
        _amount = Add("Number of days / months", Box(amount?.ToString(CultureInfo.InvariantCulture) ?? ""));
        _result = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
        _result.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        Fields.Children.Add(_result);
        _checked = new CheckBox { Content = "I've checked the date counted from and the rule against the order", Margin = new Thickness(0, 14, 0, 0) };
        Fields.Children.Add(_checked);
        _from.SelectedDateChanged += (_, _) => Recalculate();
        _method.SelectionChanged += (_, _) => Recalculate();
        _amount.TextChanged += (_, _) => Recalculate();
        _checked.Checked += (_, _) => Recalculate();
        _checked.Unchecked += (_, _) => Recalculate();
        Loaded += (_, _) => Recalculate();
    }

    /// <summary>A starting point from the wording ("within 14 days", "3 clear days before"). The person confirms it.</summary>
    private static (string Method, int? Amount) Guess(string text)
    {
        var t = text.ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"(\d{1,3})\s*(clear\s+)?(working\s+)?(day|week|month)s?");
        int? n = m.Success ? int.Parse(m.Groups[1].Value) * (m.Groups[4].Value == "week" ? 7 : 1) : null;
        bool before = t.Contains("before");
        string method = m.Success && m.Groups[4].Value == "month" ? "Calendar months after"
            : m.Success && m.Groups[2].Success ? "Clear days before"
            : m.Success && m.Groups[3].Success ? (before ? "Working days before" : "Working days after")
            : before ? "Calendar days before" : "Calendar days after";
        return (method, n);
    }

    private void Recalculate()
    {
        Result = null;
        ConfirmButton.IsEnabled = false;
        if (_from.SelectedDate is not { } from || !int.TryParse(_amount.Text.Trim(), out var n) || n < 0 || n > 3650 || _method.SelectedItem is not string method)
        {
            _result.Text = "Choose the date it's counted from, how to count, and the number.";
            return;
        }
        var date = Deadlines.Calculate(method, from, n);
        Explanation = Deadlines.Explain(method, from, n, date);
        _result.Text = "Deadline: " + Deadlines.Describe(date) + "\n" + Explanation;
        Result = date;
        ConfirmButton.IsEnabled = _checked.IsChecked == true;
    }

    protected override bool Validate() => Result != null && _checked.IsChecked == true;
}

/// <summary>Confidence levels, AI speed (recommended for this PC, or chosen by hand) and the advanced auto-apply rule for Bulk Case Import.</summary>
public class ReviewOptionsWindow : FormDialog
{
    private static Data.Database Db => App.Library.Db;
    private readonly TextBox _high, _medium, _autoThreshold, _maxParts;
    private readonly ComboBox _preset, _depth, _model, _workers, _bulkProvider, _cloudModel, _cloudEffort;
    private readonly CheckBox _auto;
    private readonly TextBlock _pc, _pcNote;
    private readonly Button _download;
    private List<(string Name, long Size)> _installed = new();
    private AiRecommendation _rec;

    private sealed record ModelItem(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    public ReviewOptionsWindow() : base("Import review options", 640, 760)
    {
        AddHint("Confidence is how strongly a document supports a suggestion, from 0 to 1. It isn't certainty: every suggestion still needs checking.");
        _high = Add("“High” confidence from", Box(BulkThresholds.High.ToString("0.00", CultureInfo.InvariantCulture)));
        _medium = Add("“Medium” confidence from (below this is “Low”, and categories go to Unknown)", Box(BulkThresholds.Medium.ToString("0.00", CultureInfo.InvariantCulture)));

        // ---- which AI reads the documents: the one in Settings → AI, or another just for bulk import
        Fields.Children.Add(new TextBlock { Text = "AI SERVICE", Style = Ui.Style("Label"), Margin = new Thickness(0, 18, 0, 4) });
        var providerNames = new List<string> { $"Same as Settings → AI ({AiProviders.Get(AiService.Provider).Name})" };
        providerNames.AddRange(AiProviders.All.Select(p => p.Name));
        var savedProvider = Db.GetSetting("bulk.provider", "") ?? "";
        _bulkProvider = Add("Which AI reads the documents", new ComboBox
        {
            ItemsSource = providerNames,
            SelectedIndex = savedProvider.Length == 0 ? 0 : 1 + Math.Max(0, AiProviders.All.ToList().FindIndex(p => p.Id == savedProvider))
        });
        _cloudModel = Add("Cloud model (blank: the one in Settings → AI)", new ComboBox { IsEditable = true, Text = AiService.BulkCloudModel });
        _cloudEffort = Add("Cloud reasoning effort", new ComboBox());
        void FillCloud()
        {
            var p = AiProviders.Get(SelectedBulkProvider());
            _cloudModel.Items.Clear();
            foreach (var (id, _) in p.Models) _cloudModel.Items.Add(id);
            var keep = AiService.BulkCloudEffort;
            _cloudEffort.ItemsSource = new[] { $"Same as Settings → AI ({(p.IsCloud ? AiService.ProviderEffort(p.Id) : "–")})" }.Concat(p.Efforts.Select(e => e.Label)).ToList();
            _cloudEffort.SelectedIndex = 1 + p.Efforts.ToList().FindIndex(e => e.Id == keep) is var i && i > 0 ? i : 0;
            _cloudModel.IsEnabled = _cloudEffort.IsEnabled = p.IsCloud;
        }
        _bulkProvider.SelectionChanged += (_, _) => FillCloud();
        FillCloud();
        AddWrapped("Cloud services read much faster than most PCs and don't need a graphics card, but documents leave this PC. Before every run you see " +
                   "what will be sent, with personal details masked by default. The local AI's speed settings below don't apply to cloud services.");

        // ---- AI speed: what this PC can cope with, and the person's own choices on top
        Fields.Children.Add(new TextBlock { Text = "AI SPEED", Style = Ui.Style("Label"), Margin = new Thickness(0, 18, 0, 4) });
        _pc = new TextBlock { Text = "This PC: checking…", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetLiveSetting(_pc, AutomationLiveSetting.Polite);
        Fields.Children.Add(_pc);
        _pcNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = Ui.Style("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        Fields.Children.Add(_pcNote);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Ui.Button("Use recommended settings", "", (_, _) => UseRecommended()));
        _download = Ui.Button($"Download {AiHardware.DefaultModel}", "", async (_, _) => await DownloadAsync());
        _download.Visibility = Visibility.Collapsed;
        _download.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetHelpText(_download, $"Downloads CaseKeeper's recommended fast model, {AiHardware.DefaultModel} (about 3 GB), into Ollama on this PC.");
        buttons.Children.Add(_download);
        Fields.Children.Add(buttons);

        _preset = Add("Speed", new ComboBox { ItemsSource = new[] { "Fast (recommended)", "Careful (slower, more thorough)" }, SelectedIndex = AiService.BulkPreset == "careful" ? 1 : 0 });
        _depth = Add("How much the AI reads for", new ComboBox
        {
            ItemsSource = new[] { "Automatic (what this PC can manage)", "Everything: categories, dates, hearings, orders and deadlines", "Categories and document details only (much quicker)" },
            SelectedIndex = AiService.BulkDepthSetting switch { "full" => 1, "categories" => 2, _ => 0 }
        });
        _model = Add("AI model", new ComboBox());
        _model.Items.Add(new ModelItem("", "Automatic (the Speed setting picks one that fits this PC)"));
        if (AiService.BulkModelOverride.Length > 0) _model.Items.Add(new ModelItem(AiService.BulkModelOverride, AiService.BulkModelOverride));
        _model.SelectedIndex = _model.Items.Count - 1;
        int savedWorkers = int.TryParse(Db.GetSetting("bulk.aiWorkers", ""), out var w) ? Math.Clamp(w, 0, 4) : 0;
        _workers = Add("Documents the AI reads at once", new ComboBox
        {
            ItemsSource = new[] { "Automatic (recommended for this PC)", "1", "2", "3", "4" },
            SelectedIndex = savedWorkers
        });
        AddWrapped("On a laptop running on battery, or in power-saver mode, CaseKeeper always reads one at a time. If this PC struggles " +
                   "(runs out of memory, or answers take too long), CaseKeeper eases off by itself – fewer at once, smaller parts, a lighter model – and tells you.");
        _maxParts = Add("Longest document read in full (parts of a few pages each)", Box(Db.GetSetting("bulk.maxParts", "40")));

        Fields.Children.Add(new TextBlock { Text = "ADVANCED", Style = Ui.Style("Label"), Margin = new Thickness(0, 18, 0, 4) });
        _auto = new CheckBox
        {
            Content = "Automatically file documents under the AI's category when it is very confident (advanced)",
            IsChecked = Db.GetSetting("bulk.autoApply", "0") == "1"
        };
        Fields.Children.Add(_auto);
        AddHint("Off unless you turn it on. Only ever changes the category of a document that's still in Uncategorised; never adds hearings, " +
                "dates, deadlines or any other record. Every change is in the audit trail, listed under “Applied by AI rule”, and can be undone.");
        _autoThreshold = Add("Only when confidence is at least", Box(Db.GetSetting("bulk.autoApplyThreshold", "0.95")));

        Loaded += async (_, _) => await ProbeAsync();
    }

    private void AddWrapped(string text) =>
        Fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = Ui.Style("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });

    /// <summary>Asks Ollama which models are installed (sizes only), then shows what this PC is recommended to use.</summary>
    private async Task ProbeAsync()
    {
        try { _installed = await AiService.InstalledModelsAsync(); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _installed = new();
            _pcNote.Text = "Couldn't reach the local AI (Ollama), so installed models aren't listed. The recommendations below still apply.";
        }
        _rec = AiHardware.Recommend(_installed);
        _pc.Text = _rec.Summary;
        var lines = new List<string>();
        if (_rec.FastModel != null) lines.Add($"Fast uses {_rec.FastModel}.");
        lines.Add(_rec.CarefulModel != null ? $"Careful uses {_rec.CarefulModel}." : "No Careful model fits this PC, so Careful uses the Fast model.");
        if (_rec.TooLarge.Count > 0) lines.Add($"Too large for this PC: {string.Join(", ", _rec.TooLarge)}.");
        if (_rec.Note.Length > 0) lines.Add(_rec.Note);
        if (_installed.Count > 0 || _pcNote.Text.Length == 0) _pcNote.Text = string.Join(" ", lines);
        _preset.ItemsSource = new[]
        {
            $"Fast (recommended){(_rec.FastModel != null ? " – " + _rec.FastModel : "")}",
            $"Careful (slower, more thorough){(_rec.CarefulModel != null ? " – " + _rec.CarefulModel : " – not available on this PC")}"
        };
        _preset.SelectedIndex = AiService.BulkPreset == "careful" ? 1 : 0;
        _workers.ItemsSource = new[] { $"Automatic ({_rec.Workers} for this PC)", "1", "2", "3", "4" };
        _workers.SelectedIndex = int.TryParse(Db.GetSetting("bulk.aiWorkers", ""), out var w) ? Math.Clamp(w, 0, 4) : 0;

        var chosen = AiService.BulkModelOverride;
        _model.Items.Clear();
        _model.Items.Add(new ModelItem("", "Automatic (the Speed setting picks one that fits this PC)"));
        foreach (var m in _installed.OrderBy(m => m.Size))
        {
            var fit = AiHardware.Fit(m.Size);
            _model.Items.Add(new ModelItem(m.Name, $"{m.Name} ({m.Size / 1e9:0.#} GB){(fit == "too large" ? " – too large for this PC" : fit == "cpu" ? " – slow on this PC (no room on the graphics card)" : "")}"));
        }
        if (chosen.Length > 0 && !_installed.Any(m => AiHardware.Same(m.Name, chosen))) _model.Items.Add(new ModelItem(chosen, $"{chosen} (not installed)"));
        _model.SelectedItem = _model.Items.Cast<ModelItem>().FirstOrDefault(i => i.Name.Length > 0 && AiHardware.Same(i.Name, chosen)) ?? _model.Items[0];
        _download.Visibility = _installed.Any(m => AiHardware.Same(m.Name, AiHardware.DefaultModel)) || _rec.Tier == AiTier.Minimal
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private string SelectedBulkProvider() =>
        _bulkProvider == null || _bulkProvider.SelectedIndex <= 0 ? AiService.Provider : AiProviders.All[_bulkProvider.SelectedIndex - 1].Id;

    private void UseRecommended()
    {
        _bulkProvider.SelectedIndex = 0;
        _cloudModel.Text = "";
        _cloudEffort.SelectedIndex = 0;
        _preset.SelectedIndex = 0;
        _depth.SelectedIndex = 0;
        _model.SelectedIndex = 0;
        _workers.SelectedIndex = 0;
        _maxParts.Text = "40";
        _pc.Text = (_rec?.Summary ?? "This PC") + " – recommended settings chosen. Save to keep them.";
    }

    private async Task DownloadAsync()
    {
        if (MessageBox.Show(this, $"Download the AI model {AiHardware.DefaultModel} into Ollama?\n\nIt's about 3 GB and comes from Ollama's own library. " +
                                  "It can take several minutes. Nothing from your cases is sent.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        _download.IsEnabled = false;
        var label = _download.Content;
        _download.Content = "Downloading… (this can take several minutes)";
        try
        {
            await AiService.PullModelAsync(AiHardware.DefaultModel, CancellationToken.None);
            MessageBox.Show(this, $"{AiHardware.DefaultModel} is ready.", Title);
            await ProbeAsync();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            AppLog.Warn("AI", $"Model download failed ({ex.GetType().Name})");
            MessageBox.Show(this, ex is InvalidOperationException ? ex.Message
                : "Ollama couldn't download the model. Check Ollama is running and this PC is online, then try again.", Title);
        }
        finally
        {
            _download.Content = label;
            _download.IsEnabled = true;
        }
    }

    protected override bool Validate()
    {
        static bool Num(string s, double min, double max, out double v) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;
        if (!Num(_high.Text, 0.5, 0.99, out var high) || !Num(_medium.Text, 0.1, 0.95, out var medium) || medium >= high)
        {
            MessageBox.Show(this, "Enter numbers between 0 and 1, with “High” above “Medium” (for example 0.85 and 0.60).", Title);
            return false;
        }
        if (!Num(_autoThreshold.Text, 0.85, 1.0, out var auto))
        {
            MessageBox.Show(this, "The auto-apply level must be between 0.85 and 1.", Title);
            return false;
        }
        if (!int.TryParse(_maxParts.Text.Trim(), out var parts) || parts < 1 || parts > 500)
        {
            MessageBox.Show(this, "Enter a number of parts between 1 and 500.", Title);
            return false;
        }
        var model = (_model.SelectedItem as ModelItem)?.Name ?? "";
        // The person's choice wins, but not without a warning when this PC can't sensibly run it
        var size = _installed.FirstOrDefault(m => model.Length > 0 && AiHardware.Same(m.Name, model)).Size;
        if (size > 0 && model != AiService.BulkModelOverride && AiHardware.Fit(size) == "too large" &&
            MessageBox.Show(this, $"{model} is too large for this PC. It may be extremely slow or fail to run (CaseKeeper will ease off if it does).\n\nUse it anyway?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        int workers = _workers.SelectedIndex;   // 0 = automatic
        if (_rec != null && workers > _rec.Workers && workers != (int.TryParse(Db.GetSetting("bulk.aiWorkers", ""), out var was) ? was : 0) &&
            MessageBox.Show(this, $"This PC is recommended to read {_rec.Workers} at once. Reading {workers} at once may be slower, or run out of memory.\n\nUse {workers} anyway?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        bool autoOn = _auto.IsChecked == true;
        if (autoOn && Db.GetSetting("bulk.autoApply", "0") != "1" &&
            MessageBox.Show(this, "Turn on automatic filing?\n\nDocuments still in Uncategorised will be filed under the AI's category without asking, " +
                                  "when it is at least this confident. You can undo each one in Import review.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;

        var bulkProvider = _bulkProvider.SelectedIndex <= 0 ? "" : AiProviders.All[_bulkProvider.SelectedIndex - 1].Id;
        var cloudInfo = AiProviders.Get(bulkProvider.Length > 0 ? bulkProvider : AiService.Provider);
        var cloudModel = cloudInfo.IsCloud ? (_cloudModel.Text ?? "").Trim() : "";
        if (cloudModel.Length > 100 || cloudModel.Any(char.IsWhiteSpace))
        {
            MessageBox.Show(this, "That model name isn't valid.", Title);
            return false;
        }
        var cloudEffort = cloudInfo.IsCloud && _cloudEffort.SelectedIndex > 0 ? cloudInfo.Efforts[_cloudEffort.SelectedIndex - 1].Id : "";
        if (cloudInfo.IsCloud && !AiService.HasProviderKey(cloudInfo.Id) &&
            MessageBox.Show(this, $"{cloudInfo.Name} isn't connected yet. Bulk import will wait for it until you add a key in Settings → AI.\n\nKeep this choice?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        Db.SetSetting("bulk.provider", bulkProvider);
        Db.SetSetting("bulk.cloudModel", cloudModel);
        Db.SetSetting("bulk.cloudEffort", cloudEffort);
        var preset = _preset.SelectedIndex == 1 ? "careful" : "fast";
        var depth = _depth.SelectedIndex switch { 1 => "full", 2 => "categories", _ => "auto" };
        Db.SetSetting("bulk.high", high.ToString("0.00", CultureInfo.InvariantCulture));
        Db.SetSetting("bulk.medium", medium.ToString("0.00", CultureInfo.InvariantCulture));
        Db.SetSetting("bulk.preset", preset);
        Db.SetSetting("bulk.depth", depth);
        Db.SetSetting("bulk.model", model);
        Db.SetSetting("bulk.aiWorkers", workers == 0 ? "" : workers.ToString(CultureInfo.InvariantCulture));
        Db.SetSetting("bulk.maxParts", parts.ToString(CultureInfo.InvariantCulture));
        Db.SetSetting("bulk.autoApply", autoOn ? "1" : "0");
        Db.SetSetting("bulk.autoApplyThreshold", auto.ToString("0.00", CultureInfo.InvariantCulture));
        Db.Audit("settings.change", "settings", null, "Changed Import review options",
            $"AI service {(bulkProvider.Length > 0 ? bulkProvider : "as Settings")}{(cloudModel.Length > 0 ? $" ({cloudModel})" : "")}{(cloudEffort.Length > 0 ? $", effort {cloudEffort}" : "")}, " +
            $"high from {high:0.00}, medium from {medium:0.00}, speed {preset}, reading {depth}, model {(model.Length > 0 ? model : "automatic")}, " +
            $"AI reads {(workers == 0 ? "automatic" : workers.ToString(CultureInfo.InvariantCulture))} at once, up to {parts} parts, " +
            $"auto-apply {(autoOn ? $"on at {auto:0.00}" : "off")}");
        if (MainWindow.Shell.Bulk is { IsRunning: false } bulk) bulk.AiWorkers = AiService.BulkWorkers();
        return true;
    }
}
