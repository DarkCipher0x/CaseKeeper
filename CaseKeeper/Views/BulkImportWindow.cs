using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;
using Microsoft.Win32;

namespace CaseKeeper.Views;

/// <summary>One file in the preview, wrapped so ticking it updates the list straight away.</summary>
public sealed class PreviewRow : Observable
{
    public ImportFile File { get; }
    public PreviewRow(ImportFile file) => File = file;

    public bool Included
    {
        get => File.Included;
        set
        {
            if (File.Included == value || !CanInclude) return;
            File.Included = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Action));
        }
    }

    public bool CanInclude => File.StatusValue == ImportFileStatus.Pending;
    public bool IsDuplicate => File.DuplicateOfDocumentId != null || File.DuplicateOfFileId != null;
    public string Path => File.RelativePath;
    public string Type => File.TypeText;
    public string Size => File.SizeText;
    public long SizeBytes => File.SizeBytes;
    public string Modified => File.ModifiedText;
    public string Status => File.StatusText;
    public string Action => File.ProposedAction;
    public string Note => File.ErrorMessage;
    public string Mapping => string.Join(" · ", new[] { File.MappedTags, File.MappedCategoryId.HasValue ? "category set" : null }.Where(s => !string.IsNullOrEmpty(s)));
    public void Refresh() { OnPropertyChanged(nameof(Mapping)); OnPropertyChanged(nameof(Action)); }

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() => $"{Path}: {Action}{(Note.Length > 0 ? ". " + Note : "")}";
}

/// <summary>
/// Bulk Case Import: choose a folder, see everything that's in it, import it safely, read the text and
/// let the local AI suggest how to organise it. Nothing reaches the case until it's approved in Import review.
/// </summary>
public class BulkImportWindow : Window
{
    private static Data.Database Db => App.Library.Db;
    private static BulkImportService Bulk => MainWindow.Shell.Bulk;

    private readonly ContentControl _body = new();
    private readonly TextBlock _steps = new() { FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _back, _next, _cancel;
    private int _step;

    // step 1
    private TextBox _folder;
    private CheckBox _subfolders, _hiddenFiles, _hiddenFolders, _links, _skipSystem, _preserve, _runOcr, _runAi, _remember;
    private RadioButton _mapNone, _mapTags, _mapCategories, _dupSkip, _dupImport, _dupReview;
    private TextBlock _scanStatus;
    private CancellationTokenSource _scanCts;

    // step 2
    private ScanResult _scan;
    private BulkOptions _options;
    private readonly ObservableCollection<PreviewRow> _rows = new();
    private ICollectionView _view;
    private TextBox _search;
    private ComboBox _statusFilter, _typeFilter;
    private TextBlock _summary;
    private TreeView _tree;
    private Dictionary<string, long?> _folderCategories = new(StringComparer.OrdinalIgnoreCase);

    // step 3
    private long _jobId;
    private ProgressBar _bar;
    private TextBlock _phaseText, _liveStatus, _currentText;
    private readonly Dictionary<ImportPhase, TextBlock> _phaseMarks = new();
    private Button _pause, _stop;
    private DateTime _lastAnnounce = DateTime.MinValue;

    public BulkImportWindow()
    {
        Title = "Bulk Case Import – CaseKeeper";
        Width = 1180;
        Height = 800;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = Ui.Style("AppWindow");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 18) };
        var head = new StackPanel();
        var titleRow = new DockPanel();
        var help = Ui.Button("Help", "", (_, _) => MainWindow.Shell?.OpenGuide("app.bulkimport"), tooltip: "How Bulk Case Import works (F1)");
        DockPanel.SetDock(help, Dock.Right);
        titleRow.Children.Add(help);
        titleRow.Children.Add(Ui.Text("Bulk Case Import", "H1", 22));
        head.Children.Add(titleRow);
        head.Children.Add(Ui.Muted("Bring in a whole case folder at once. Your original files are never moved, renamed or changed: CaseKeeper " +
                                   "copies them into your library. Any AI suggestions wait for you to check them in Import review.", 12.5));
        _steps.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        head.Children.Add(_steps);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _cancel = Ui.Button("Cancel", null, (_, _) => Close());
        DockPanel.SetDock(_cancel, Dock.Left);
        buttons.Children.Add(_cancel);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _back = Ui.Button("Back", "", (_, _) => GoBack());
        _next = Ui.Button("Next", "", async (_, _) => await GoNextAsync(), primary: true);
        right.Children.Add(_back);
        right.Children.Add(_next);
        buttons.Children.Add(right);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _body.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(_body);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1) { MainWindow.Shell?.OpenGuide("app.bulkimport"); e.Handled = true; }
        };
        Closing += OnClosing;
        Closed += (_, _) => Bulk.Progress -= OnProgress;
        ShowStep1();
    }

    /// <summary>Opens the window on a job that was paused or interrupted, and carries on with it.</summary>
    public static void Resume(Window owner, long jobId)
    {
        var w = new BulkImportWindow { Owner = owner };
        w.Show();
        w._jobId = jobId;
        w.ShowProgress();
        _ = w.RunJobAsync();
    }

    private void SetSteps(int current)
    {
        _step = current;
        var names = new[] { "Choose a folder", "Check what was found", "Import and read", "Finished" };
        _steps.Text = string.Join("   ›   ", names.Select((n, i) => i + 1 == current ? $"Step {i + 1}: {n} (current)" : $"{i + 1}. {n}"));
        AutomationProperties.SetName(_steps, $"Step {current} of 4: {names[current - 1]}");
    }

    // ================================================================== step 1: choose a folder
    private void ShowStep1()
    {
        SetSteps(1);
        var saved = LoadOptions();
        var sp = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };

        sp.Children.Add(Label("THE FOLDER WITH YOUR CASE PAPERS"));
        var row = new DockPanel();
        var browse = Ui.Button("Choose folder…", "", (_, _) => Browse(), tooltip: "Choose the folder to import");
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        _folder = new TextBox { Text = _folder?.Text ?? "" };
        AutomationProperties.SetName(_folder, "Folder to import");
        row.Children.Add(_folder);
        sp.Children.Add(row);
        sp.Children.Add(Ui.Muted("Everything in it is listed first so you can check it. Nothing is imported until you say so.", 12));

        var cols = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var rightCol = new StackPanel();
        Grid.SetColumn(rightCol, 2);
        cols.Children.Add(left);
        cols.Children.Add(rightCol);
        sp.Children.Add(cols);

        left.Children.Add(Label("WHAT TO INCLUDE"));
        _subfolders = Check(left, "Include all subfolders", saved.Scan.IncludeSubfolders);
        _hiddenFiles = Check(left, "Include hidden files", saved.Scan.IncludeHiddenFiles);
        _hiddenFolders = Check(left, "Include hidden folders", saved.Scan.IncludeHiddenFolders);
        _links = Check(left, "Follow shortcuts and linked folders (advanced)", saved.Scan.FollowLinks,
            "Off by default: a link can point outside the folder or back into it. Loops are detected either way.");
        _skipSystem = Check(left, "Skip temporary and system files (e.g. Thumbs.db, ~$ lock files)", saved.Scan.SkipSystemFiles);

        left.Children.Add(Label("ORIGINAL FOLDERS"));
        _preserve = Check(left, "Keep each file's folder path with the document", saved.PreserveStructure,
            "So you can still see and filter by where it came from. Only the path inside the chosen folder is kept.");
        _mapNone = Radio(left, "folders", "Don't create anything from folder names", saved.FolderMapping == "none");
        _mapTags = Radio(left, "folders", "Turn folder names into tags", saved.FolderMapping == "tags");
        _mapCategories = Radio(left, "folders", "Choose a category for each top-level folder (shown on the next step)", saved.FolderMapping == "categories");

        rightCol.Children.Add(Label("FILES ALREADY IN YOUR LIBRARY"));
        rightCol.Children.Add(Ui.Muted("Matched by their contents (a SHA-256 fingerprint), never just by name.", 12));
        _dupSkip = Radio(rightCol, "dups", "Skip them, and note which document they match (recommended)", saved.Duplicates == DuplicatePolicy.SkipAndRecord);
        _dupImport = Radio(rightCol, "dups", "Import them again as separate documents", saved.Duplicates == DuplicatePolicy.ImportSeparately);
        _dupReview = Radio(rightCol, "dups", "Let me decide for each one on the next step", saved.Duplicates == DuplicatePolicy.ReviewEach);

        rightCol.Children.Add(Label("AFTER IMPORTING"));
        _runOcr = Check(rightCol, "Read the text now (including scans and photos)", saved.RunOcr,
            "Needed for search and for the AI. Untick to import quickly and let the text be read in the background.");
        _runAi = Check(rightCol, "Ask my local AI to suggest categories, dates and records", saved.RunAi,
            AiService.Enabled
                ? AiHardware.Tier == AiTier.Minimal
                    ? "This PC has no graphics card the AI can use and limited memory, so local AI would be very slow. CaseKeeper's own rules still suggest categories from form codes and titles."
                    : $"Uses the speed settings recommended for this PC (change them in Import review → Review options…) through Ollama on this computer or your own network. You approve every suggestion."
                : "AI is switched off in Settings, so documents will be imported now and can be analysed later. CaseKeeper's own rules still suggest categories from form codes.");
        _remember = Check(rightCol, "Remember these choices for this library", saved.Remember);

        _scanStatus = Ui.Muted("", 12.5);
        _scanStatus.Margin = new Thickness(0, 14, 0, 0);
        AutomationProperties.SetLiveSetting(_scanStatus, AutomationLiveSetting.Polite);
        sp.Children.Add(_scanStatus);

        _body.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _back.Visibility = Visibility.Collapsed;
        _next.Visibility = Visibility.Visible;
        SetNext("Find files", "");
        Dispatcher.BeginInvoke(() => _folder.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TextBlock Label(string text) => new() { Text = text, Style = Ui.Style("Label"), Margin = new Thickness(0, 12, 0, 4) };

    private static CheckBox Check(Panel parent, string text, bool value, string hint = null)
    {
        // Wrapping text, so long labels are never cut off at narrow widths or large text sizes
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 4, 0, 2) };
        AutomationProperties.SetName(c, text);
        if (hint != null)
        {
            AutomationProperties.SetHelpText(c, hint);
            parent.Children.Add(c);
            var h = Ui.Muted(hint, 11.5);
            h.Margin = new Thickness(24, 0, 0, 4);
            parent.Children.Add(h);
        }
        else parent.Children.Add(c);
        return c;
    }

    private static RadioButton Radio(Panel parent, string group, string text, bool value)
    {
        var r = new RadioButton { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, GroupName = group, IsChecked = value, Margin = new Thickness(0, 4, 0, 2) };
        AutomationProperties.SetName(r, text);
        parent.Children.Add(r);
        return r;
    }

    private void SetNext(string label, string glyph)
    {
        _next.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Ui.Icon(glyph, 13), new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } }
        };
        AutomationProperties.SetName(_next, label);
    }

    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder with your case papers" };
        if (Directory.Exists(_folder.Text)) dlg.InitialDirectory = _folder.Text;
        if (dlg.ShowDialog(this) == true) _folder.Text = dlg.FolderName;
    }

    private BulkOptions LoadOptions()
    {
        try
        {
            var json = Db.GetSetting("bulk.options", "");
            if (!string.IsNullOrWhiteSpace(json)) return JsonSerializer.Deserialize<BulkOptions>(json) ?? new BulkOptions();
        }
        catch (JsonException) { /* fall back to defaults */ }
        // On a PC too modest for local AI, it starts unticked (the person can still tick it)
        return new BulkOptions { RunAi = AiService.Enabled && AiHardware.Tier != AiTier.Minimal };
    }

    private BulkOptions ReadOptions() => new()
    {
        Scan = new ScanOptions
        {
            IncludeSubfolders = _subfolders.IsChecked == true, IncludeHiddenFiles = _hiddenFiles.IsChecked == true,
            IncludeHiddenFolders = _hiddenFolders.IsChecked == true, FollowLinks = _links.IsChecked == true,
            SkipSystemFiles = _skipSystem.IsChecked == true
        },
        PreserveStructure = _preserve.IsChecked == true,
        FolderMapping = _mapTags.IsChecked == true ? "tags" : _mapCategories.IsChecked == true ? "categories" : "none",
        Duplicates = _dupImport.IsChecked == true ? DuplicatePolicy.ImportSeparately : _dupReview.IsChecked == true ? DuplicatePolicy.ReviewEach : DuplicatePolicy.SkipAndRecord,
        RunOcr = _runOcr.IsChecked == true,
        RunAi = _runAi.IsChecked == true,
        Remember = _remember.IsChecked == true
    };

    private async Task ScanAsync()
    {
        var folder = _folder.Text.Trim().Trim('"');
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "Choose a folder that exists. If it's on a network drive or USB stick, check it's connected.", Title);
            return;
        }
        if (Path.GetFullPath(folder).TrimEnd('\\').StartsWith(Path.GetFullPath(App.Library.RootFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "That folder is inside CaseKeeper's own library. Choose the folder where your original papers are.", Title);
            return;
        }
        _options = ReadOptions();
        if (_options.Remember) Db.SetSetting("bulk.options", JsonSerializer.Serialize(_options));

        _scanCts = new CancellationTokenSource();
        _next.IsEnabled = false;
        _cancel.Content = "Stop scanning";
        var status = new Progress<string>(s => _scanStatus.Text = s);
        try
        {
            _scanStatus.Text = "Looking through the folder…";
            var options = _options.Scan;
            var ct = _scanCts.Token;
            _scan = await Task.Run(() => FolderScanner.Scan(folder, options, ct, status), ct);
            var hashes = Db.GetDocumentHashes();
            var hashProgress = new Progress<(int Done, int Total)>(p => _scanStatus.Text = $"Checking for duplicates… {p.Done:N0} of {p.Total:N0}");
            await Task.Run(() => FolderScanner.FindDuplicates(_scan, hashes, ct, hashProgress), ct);
            var categories = MainWindow.Shell.Categories.ToList();
            var rows = BulkImportService.PreviewRows(_scan, _options, categories);
            if (_options.FolderMapping == "categories")
            {
                _folderCategories = BulkImportService.SuggestFolderCategories(rows, categories);
                foreach (var r in rows) BulkImportService.ApplyMapping(r, _options, categories, _folderCategories);
            }
            _rows.Clear();
            foreach (var r in rows) _rows.Add(new PreviewRow(r));
            ShowPreview();
        }
        catch (OperationCanceledException) { _scanStatus.Text = "Scan stopped. Nothing was imported."; }
        catch (Exception ex)
        {
            var id = AppLog.Error("BulkImport", "Folder scan failed", ex);
            _scanStatus.Text = $"The folder couldn't be scanned: {ex.Message} (ref {id})";
        }
        finally
        {
            _next.IsEnabled = true;
            _cancel.Content = "Cancel";
            _scanCts = null;
        }
    }

    // ================================================================== step 2: preview
    private void ShowPreview()
    {
        SetSteps(2);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var top = new StackPanel();
        Grid.SetColumnSpan(top, 3);
        _summary = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        top.Children.Add(_summary);
        foreach (var w in ScanWarnings()) top.Children.Add(Ui.Callout(w));
        grid.Children.Add(top);

        // ---- folder tree: tick or untick whole branches
        var left = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(left, 1);
        left.Children.Add(DockTop(Label("FOLDERS – UNTICK TO LEAVE A BRANCH OUT")));
        _tree = new TreeView();
        AutomationProperties.SetName(_tree, "Folders found");
        if (_options.FolderMapping == "categories" && _folderCategories.Count > 0)
        {
            var mapping = BuildFolderMapping();
            DockPanel.SetDock(mapping, Dock.Bottom);
            left.Children.Add(mapping);
        }
        left.Children.Add(_tree);
        grid.Children.Add(left);

        // ---- file list
        var right = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 2);
        var bar = new WrapPanel();
        bar.Children.Add(Ui.SearchBox(out _search, "Search the list…", 230));
        AutomationProperties.SetName(_search, "Search the list of files");
        _search.TextChanged += (_, _) => _view.Refresh();
        _statusFilter = new ComboBox
        {
            ItemsSource = new[] { "Everything", "Will be imported", "Duplicates", "Left out", "Unsupported", "Problems", "System files" },
            SelectedIndex = 0, Width = 170, Margin = new Thickness(0, 0, 6, 6)
        };
        AutomationProperties.SetName(_statusFilter, "Show");
        _statusFilter.SelectionChanged += (_, _) => _view?.Refresh();
        bar.Children.Add(_statusFilter);
        _typeFilter = new ComboBox
        {
            ItemsSource = new[] { "All types" }.Concat(_rows.Select(r => r.Type).Where(t => t.Length > 0).Distinct().OrderBy(t => t)).ToList(),
            SelectedIndex = 0, Width = 120, Margin = new Thickness(0, 0, 6, 6)
        };
        AutomationProperties.SetName(_typeFilter, "File type");
        _typeFilter.SelectionChanged += (_, _) => _view?.Refresh();
        bar.Children.Add(_typeFilter);
        bar.Children.Add(Ui.Button("Include all shown", "", (_, _) => SetShown(true)));
        bar.Children.Add(Ui.Button("Leave out all shown", "", (_, _) => SetShown(false)));
        bar.Children.Add(Ui.Button("Copy summary", "", (_, _) =>
        {
            Ui.CopyToClipboard(BulkImportService.ScanSummary(_scan, _rows.Select(r => r.File).ToList()));
            _summary.Text += "  ·  Summary copied.";
        }, tooltip: "Copies counts and paths inside the folder only – nothing from inside the files"));
        right.Children.Add(DockTop(bar));

        var dg = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, IsReadOnly = false,
            SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
            EnableRowVirtualization = true, EnableColumnVirtualization = true
        };
        VirtualizingPanel.SetIsVirtualizing(dg, true);
        VirtualizingPanel.SetVirtualizationMode(dg, VirtualizationMode.Recycling);
        AutomationProperties.SetName(dg, "Files found");
        dg.Columns.Add(IncludeColumn());
        dg.Columns.Add(TextCol("File", nameof(PreviewRow.Path), 3));
        dg.Columns.Add(TextCol("Type", nameof(PreviewRow.Type), 55, fixedWidth: true));
        dg.Columns.Add(TextCol("Size", nameof(PreviewRow.Size), 70, fixedWidth: true, sort: nameof(PreviewRow.SizeBytes)));
        dg.Columns.Add(TextCol("File date (info only)", nameof(PreviewRow.Modified), 120, fixedWidth: true));
        dg.Columns.Add(TextCol("Status", nameof(PreviewRow.Status), 1.1));
        dg.Columns.Add(TextCol("What will happen", nameof(PreviewRow.Action), 1.2));
        if (_options.FolderMapping != "none") dg.Columns.Add(TextCol("From folder name", nameof(PreviewRow.Mapping), 1));
        dg.Columns.Add(TextCol("Note", nameof(PreviewRow.Note), 1.4));
        dg.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || Keyboard.FocusedElement is CheckBox) return;
            var selected = dg.SelectedItems.Cast<PreviewRow>().ToList();
            bool to = !selected.All(r => r.Included);
            foreach (var r in selected) r.Included = to;
            UpdateSummary();
            e.Handled = true;
        };
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Matches;
        dg.ItemsSource = _view;
        right.Children.Add(dg);
        grid.Children.Add(right);

        _body.Content = grid;
        BuildTree();
        UpdateSummary();
        _back.Visibility = Visibility.Visible;
        SetNext("Import", "");
    }

    private static UIElement DockTop(UIElement e)
    {
        DockPanel.SetDock(e, Dock.Top);
        return e;
    }

    private IEnumerable<string> ScanWarnings()
    {
        if (_scan.InaccessibleFolders.Count > 0)
            yield return $"{_scan.InaccessibleFolders.Count} folder(s) couldn't be opened, so anything in them isn't listed: " +
                         string.Join(", ", _scan.InaccessibleFolders.Take(5)) + (_scan.InaccessibleFolders.Count > 5 ? "…" : "");
        if (_scan.TruncatedAtLimit) yield return "This folder has more files than CaseKeeper lists at once (50,000). Import it in smaller parts.";
        if (_scan.TooDeep) yield return "Some folders are nested too deeply to list. Move them nearer the top and scan again.";
        if (_scan.LinksNotFollowed > 0) yield return $"{_scan.LinksNotFollowed} shortcut(s) or linked folder(s) weren't followed. Tick 'Follow shortcuts' on step 1 to include them.";
        if (_scan.HiddenSkipped > 0) yield return $"{_scan.HiddenSkipped} hidden item(s) left out.";
        if (_scan.LoopsAvoided > 0) yield return $"{_scan.LoopsAvoided} folder loop(s) found and skipped.";
    }

    private bool Matches(object o)
    {
        if (o is not PreviewRow r) return false;
        var s = _search?.Text.Trim() ?? "";
        if (s.Length > 0 && !r.Path.Contains(s, StringComparison.OrdinalIgnoreCase) && !r.Status.Contains(s, StringComparison.OrdinalIgnoreCase)) return false;
        if (_typeFilter?.SelectedIndex > 0 && r.Type != (string)_typeFilter.SelectedItem) return false;
        return (_statusFilter?.SelectedItem as string) switch
        {
            "Will be imported" => r.CanInclude && r.Included,
            "Duplicates" => r.IsDuplicate,
            "Left out" => r.CanInclude && !r.Included,
            "Unsupported" => r.File.StatusValue == ImportFileStatus.Unsupported,
            "Problems" => r.File.StatusValue is ImportFileStatus.Inaccessible or ImportFileStatus.Failed or ImportFileStatus.Missing,
            "System files" => r.File.StatusValue == ImportFileStatus.SystemFile,
            _ => true
        };
    }

    private static DataGridTextColumn TextCol(string header, string path, double width, bool fixedWidth = false, string sort = null) => new()
    {
        Header = header, Binding = new Binding(path) { Mode = BindingMode.OneWay }, IsReadOnly = true, SortMemberPath = sort ?? path,
        Width = fixedWidth ? new DataGridLength(width) : new DataGridLength(width, DataGridLengthUnitType.Star)
    };

    private DataGridTemplateColumn IncludeColumn()
    {
        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetBinding(ToggleButtonIsChecked, new Binding(nameof(PreviewRow.Included)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        factory.SetBinding(IsEnabledProperty, new Binding(nameof(PreviewRow.CanInclude)) { Mode = BindingMode.OneWay });
        factory.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(PreviewRow.Path)) { StringFormat = "Import {0}" });
        factory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        factory.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => UpdateSummary()));
        return new DataGridTemplateColumn
        {
            Header = "Import", Width = new DataGridLength(58), SortMemberPath = nameof(PreviewRow.Included),
            CellTemplate = new DataTemplate { VisualTree = factory }
        };
    }

    private static readonly DependencyProperty ToggleButtonIsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private void SetShown(bool include)
    {
        foreach (var r in _view.Cast<PreviewRow>()) r.Included = include;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int found = _rows.Count, import = _rows.Count(r => r.CanInclude && r.Included);
        int dups = _rows.Count(r => r.IsDuplicate), unsupported = _rows.Count(r => r.File.StatusValue == ImportFileStatus.Unsupported);
        int problems = _rows.Count(r => r.File.StatusValue is ImportFileStatus.Inaccessible or ImportFileStatus.Failed or ImportFileStatus.Missing);
        long bytes = _rows.Where(r => r.CanInclude && r.Included).Sum(r => r.SizeBytes);
        _summary.Text = $"{found:N0} file(s) found  ·  {import:N0} will be imported ({bytes / 1024d / 1024d:0.0} MB)  ·  {dups:N0} duplicate(s)  ·  " +
                        $"{unsupported:N0} unsupported  ·  {problems:N0} can't be read";
        _next.IsEnabled = import > 0;
        RefreshTreeChecks();
    }

    // ---- the folder tree
    private sealed class FolderNode
    {
        public string Path = "";
        public string Name = "";
        public readonly SortedDictionary<string, FolderNode> Children = new(StringComparer.OrdinalIgnoreCase);
        public CheckBox Box;
        public int Count;
    }

    private FolderNode _rootNode;

    private void BuildTree()
    {
        _rootNode = new FolderNode { Name = new DirectoryInfo(_scan.Root).Name };
        foreach (var r in _rows.Where(r => r.CanInclude))
        {
            var node = _rootNode;
            node.Count++;
            var folder = r.File.Folder;
            if (folder.Length == 0) continue;
            var path = "";
            foreach (var part in folder.Split('\\', '/'))
            {
                path = path.Length == 0 ? part : path + "\\" + part;
                if (!node.Children.TryGetValue(part, out var child)) node.Children[part] = child = new FolderNode { Path = path, Name = part };
                node = child;
                node.Count++;
            }
        }
        _tree.Items.Clear();
        _tree.Items.Add(TreeItem(_rootNode, true));
        RefreshTreeChecks();
    }

    private TreeViewItem TreeItem(FolderNode node, bool expand)
    {
        var box = new CheckBox { Content = $"{node.Name}  ({node.Count:N0})", IsThreeState = true };
        AutomationProperties.SetName(box, $"Import everything in {node.Name}, {node.Count} file(s)");
        box.Click += (_, _) =>
        {
            bool include = box.IsChecked != false;
            foreach (var r in _rows.Where(r => r.CanInclude && InFolder(r, node.Path))) r.Included = include;
            UpdateSummary();
        };
        node.Box = box;
        var item = new TreeViewItem { Header = box, IsExpanded = expand };
        foreach (var c in node.Children.Values) item.Items.Add(TreeItem(c, false));
        return item;
    }

    private static bool InFolder(PreviewRow r, string folder) =>
        folder.Length == 0 || r.File.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        r.File.Folder.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    private void RefreshTreeChecks()
    {
        if (_rootNode == null) return;
        void Walk(FolderNode n)
        {
            var rows = _rows.Where(r => r.CanInclude && InFolder(r, n.Path)).ToList();
            int included = rows.Count(r => r.Included);
            if (n.Box != null)
            {
                n.Box.IsChecked = included == rows.Count ? true : included > 0 ? null : false;
                // Say how many in words too: a part-ticked box isn't obvious in every theme, and never by colour alone
                var label = included == rows.Count || included == 0 ? $"{n.Name}  ({rows.Count:N0})" : $"{n.Name}  ({included:N0} of {rows.Count:N0})";
                n.Box.Content = label;
                AutomationProperties.SetName(n.Box, $"Import from {n.Name}: {included} of {rows.Count} file(s) ticked");
            }
            foreach (var c in n.Children.Values) Walk(c);
        }
        Walk(_rootNode);
    }

    // ---- folder → category mapping
    private UIElement BuildFolderMapping()
    {
        var categories = MainWindow.Shell.Categories.ToList();
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(Label("CATEGORY FOR EACH TOP-LEVEL FOLDER"));
        sp.Children.Add(Ui.Muted("Suggested from the folder's name. Leave blank to keep those documents in Uncategorised for review.", 11.5));
        var list = new StackPanel();
        foreach (var (folder, catId) in _folderCategories.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var choices = new List<Category> { new() { Id = 0, Name = "— Uncategorised for now —", GroupName = "" } };
            choices.AddRange(categories);
            var combo = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(c => c.Id == (catId ?? 0)) ?? choices[0], Margin = new Thickness(0, 2, 0, 6) };
            SearchableCombo.Attach(combo, o => (o as Category)?.DisplayName ?? "");
            AutomationProperties.SetName(combo, $"Category for the folder {folder}");
            var f = folder;
            combo.SelectionChanged += (_, _) =>
            {
                _folderCategories[f] = combo.SelectedItem is Category { Id: > 0 } c ? c.Id : null;
                foreach (var r in _rows) { BulkImportService.ApplyMapping(r.File, _options, categories, _folderCategories); r.Refresh(); }
            };
            list.Children.Add(new TextBlock { Text = folder, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            list.Children.Add(combo);
        }
        sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return sp;
    }

    // ================================================================== step 3: importing and analysing
    private async Task StartImportAsync()
    {
        int count = _rows.Count(r => r.CanInclude && r.Included);
        if (Bulk.IsRunning)
        {
            MessageBox.Show(this, "Another bulk import is running. Wait for it to finish, or pause it, then try again.", Title);
            return;
        }
        var ai = _options.RunAi && AiService.Enabled
            ? "\n\nThen your local AI will read each document and suggest categories, dates and records. Nothing is added to your case until you approve it in Import review."
            : "";
        if (MessageBox.Show(this, $"Import {count:N0} file(s) into your library?\n\nYour original files stay exactly where they are." + ai +
                                  "\n\nYou can pause at any time, and carry on later – even after closing CaseKeeper.",
                "Bulk Case Import", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        try
        {
            var job = Bulk.CreateJob(_scan.Root, _options, _rows.Select(r => r.File).ToList());
            _jobId = job.Id;
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("BulkImport", "Couldn't create the import job", ex);
            MessageBox.Show(this, $"The import couldn't be started: {ex.Message}\nReference: {id}", Title);
            return;
        }
        ShowProgress();
        await RunJobAsync();
    }

    private void ShowProgress()
    {
        SetSteps(3);
        var sp = new StackPanel { MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        _phaseText = Ui.Text("Starting…", "H2");
        sp.Children.Add(_phaseText);
        _bar = new ProgressBar { Height = 10, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 6) };
        AutomationProperties.SetName(_bar, "Progress");
        sp.Children.Add(_bar);
        _currentText = Ui.Muted("", 12.5);
        sp.Children.Add(_currentText);
        _liveStatus = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        // A live region: screen readers announce its text when it changes (no fixed name, or that's all they'd say)
        AutomationProperties.SetLiveSetting(_liveStatus, AutomationLiveSetting.Polite);
        sp.Children.Add(_liveStatus);

        sp.Children.Add(Label("STAGES"));
        _phaseMarks.Clear();
        foreach (var (phase, text) in new[]
                 {
                     (ImportPhase.Importing, "Copy the files into your library (encrypted if your library is protected)"),
                     (ImportPhase.PreparingText, "Read the text, including scans and photos"),
                     (ImportPhase.Analysing, "Local AI suggests categories, dates and records"),
                     (ImportPhase.Checking, "Compare documents: duplicates, conflicts, gaps")
                 })
        {
            var mark = new TextBlock { Width = 110, FontWeight = FontWeights.SemiBold, Text = "Waiting" };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(mark);
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(row);
            _phaseMarks[phase] = mark;
        }

        var controls = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        _pause = Ui.Button("Pause", "", (_, _) => { Bulk.RequestPause(); _pause.IsEnabled = false; _liveStatus.Text = "Pausing after the current document…"; });
        _stop = Ui.Button("Stop", "", (_, _) =>
        {
            if (MessageBox.Show(this, "Stop this import?\n\nDocuments already imported stay in your library. You can carry on later from Import review.",
                    "Stop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Bulk.RequestCancel();
            _stop.IsEnabled = false;
        });
        controls.Children.Add(_pause);
        controls.Children.Add(_stop);
        controls.Children.Add(Ui.Button("Keep working while it runs", "", (_, _) => Close(),
            tooltip: "Close this window. The import carries on, with progress at the bottom of the main window."));
        sp.Children.Add(controls);
        sp.Children.Add(Ui.Muted("You can close CaseKeeper at any time; the import carries on from where it stopped next time.", 12));

        _body.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _back.Visibility = Visibility.Collapsed;
        _next.Visibility = Visibility.Collapsed;
        _cancel.Content = "Close";
        Bulk.Progress -= OnProgress;
        Bulk.Progress += OnProgress;
    }

    private async Task RunJobAsync()
    {
        try { await MainWindow.Shell.RunBulkJobAsync(_jobId); }
        catch (Exception ex)
        {
            var id = AppLog.Error("BulkImport", "Bulk import stopped with an error", ex);
            if (IsLoaded) _liveStatus.Text = $"The import stopped because of an error (ref {id}). What was done so far is kept; you can carry on from Import review.";
            return;
        }
        if (IsLoaded) ShowFinished();
    }

    private void OnProgress(BulkProgress p)
    {
        if (p.JobId != _jobId) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || _bar == null) return;
            foreach (var (phase, mark) in _phaseMarks)
                mark.Text = phase < p.Phase ? "✓ Done" : phase == p.Phase && !p.Finished ? "▶ In progress" : phase == p.Phase ? "✓ Done" : "Waiting";
            _phaseText.Text = p.Phase switch
            {
                ImportPhase.Importing => "Importing files",
                ImportPhase.PreparingText => "Reading text",
                ImportPhase.Analysing => "AI analysis",
                ImportPhase.Checking => "Cross-checking",
                _ => "Finishing"
            };
            _bar.IsIndeterminate = p.Total == 0 && !p.Finished;
            if (p.Total > 0) { _bar.Maximum = p.Total; _bar.Value = Math.Min(p.Done, p.Total); }
            _currentText.Text = p.Current.Length > 0 ? AnalysisSchema.Clip(p.Current, 90) : "";
            var text = p.Total > 0 ? $"{p.Message}: {p.Done:N0} of {p.Total:N0}" + (p.RemainingText.Length > 0 ? $" · {p.RemainingText}" : "") : p.Message;
            // Screen readers hear an update every few seconds, not every file
            if ((DateTime.Now - _lastAnnounce).TotalSeconds >= 4 || p.Finished)
            {
                _liveStatus.Text = text;
                _lastAnnounce = DateTime.Now;
                UIElementAutomationPeer.FromElement(_liveStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            else _liveStatus.Text = text;
        });
    }

    // ================================================================== step 4: done
    private void ShowFinished()
    {
        SetSteps(4);
        Bulk.Progress -= OnProgress;
        var job = Bulk.GetJob(_jobId);
        var counts = Db.CountImportFiles(_jobId);
        var findings = Db.Query<AiFinding>("JobId=$j AND Status IN (0,3)", ("$j", _jobId)).Count;
        var sp = new StackPanel { MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(Ui.Text(job?.StateValue switch
        {
            ImportJobState.Paused => "Paused",
            ImportJobState.Cancelled => "Stopped",
            _ => "Import finished"
        }, "H2"));
        sp.Children.Add(Ui.Text(BulkImportService.Summarise(counts)));
        if (job?.LastErrorCode is "AI_UNAVAILABLE" or "AI_OFF")
            sp.Children.Add(Ui.Callout(job.LastErrorCode == "AI_OFF"
                ? "AI is switched off, so the documents haven't been analysed. Switch it on in Settings → AI, then use “Analyse waiting documents” in Import review."
                : "Your local AI (Ollama) couldn't be reached, so some documents are waiting to be analysed. Start Ollama, then use “Analyse waiting documents” in Import review."));
        sp.Children.Add(Ui.Text(findings > 0
            ? $"{findings:N0} suggestion(s) are waiting for you in Import review. Nothing has been added to your case yet."
            : "There's nothing to review yet.", null));
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(Ui.Button("Open Import review", "", (_, _) => { MainWindow.Shell.GoTo("review"); Close(); }, primary: true));
        row.Children.Add(Ui.Button("Show the imported documents", "", (_, _) => { MainWindow.Shell.GoTo("documents"); Close(); }));
        sp.Children.Add(row);
        _body.Content = sp;
        _cancel.Content = "Close";
    }

    // ================================================================== navigation
    private bool _busy;

    private async Task GoNextAsync()
    {
        // One step at a time: a second click while a scan or the confirmation is open does nothing
        if (_busy) return;
        _busy = true;
        try
        {
            if (_step == 1) await ScanAsync();
            else if (_step == 2) await StartImportAsync();
        }
        finally { _busy = false; }
    }

    private void GoBack()
    {
        if (_step == 2) ShowStep1();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        _scanCts?.Cancel();
    }
}
