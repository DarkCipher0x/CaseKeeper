using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Data;
using CaseKeeper.Models;
using CaseKeeper.Services;
using Microsoft.Win32;

namespace CaseKeeper.Views;

public partial class MainWindow : Window
{
    private static LibraryService Lib => App.Library;
    private static Database Db => Lib.Db;

    private List<Category> _categories = new();
    private List<Party> _parties = new();
    private List<CaseDocument> _docs = new();
    private List<TimelineEvent> _events = new();
    private List<Complaint> _complaints = new();

    private readonly Queue<long> _ocrQueue = new();
    private bool _ocrRunning;

    public MainWindow()
    {
        InitializeComponent();
        Shell = this;
        Title = $"CaseKeeper {AppVersion}";
        BuildNavigation();
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, e) => ConfirmCloseDuringBulk(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitTimelineFilters();
        InitBundle();
        LoadTreePreferences();
        LoadColumnPreferences();
        ReloadAll();
        LoadSettings();
        BuildSettingsExtras();
        LoadBulkThresholds();
        try { Db.RecoverInterruptedApprovals(); }
        catch (Exception ex) { AppLog.Warn("Review", "Couldn't check for interrupted approvals", ex); }
        // Resume any text scanning that didn't finish last time
        EnqueueOcr(_docs.Where(d => d.OcrStatus is OcrStatus.Pending or OcrStatus.Running));
        Dispatcher.BeginInvoke(FirstRunIfNeeded, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // An update started last time is reported once the running version proves it; then the opt-in automatic check
        UpdatesAtStartup();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleGlobalKeys(e)) { e.Handled = true; return; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Nav.SelectedItem = SearchTab;
            Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); });
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ data loading
    private void ReloadAll()
    {
        _categories = Db.GetCategories();
        _parties = Db.GetParties();
        _docs = Db.GetDocuments();
        ApplyDisplayNames(_docs);
        _deletedDocs = Db.GetDeletedDocuments();
        ApplyDisplayNames(_deletedDocs);
        _events = Db.GetEvents();
        _complaints = Db.GetComplaints();

        BuildCategoryTree();
        RefreshDocumentList();
        RefreshDashboard();
        RefreshParties();
        RefreshTimeline();
        RefreshComplaints();
        RefreshSearchCategories();
        RefreshBundleSources();
        RefreshCategoryList();
    }

    private void ApplyDisplayNames(IEnumerable<CaseDocument> docs)
    {
        var cats = _categories.ToDictionary(c => c.Id);
        var parties = _parties.ToDictionary(p => p.Id);
        foreach (var d in docs)
        {
            d.CategoryName = cats.TryGetValue(d.CategoryId, out var c) ? c.DisplayName : "Uncategorised";
            d.PartyName = d.PartyId.HasValue && parties.TryGetValue(d.PartyId.Value, out var p) ? p.DisplayName : "";
        }
    }

    private CaseDocument FindDoc(long id) => _docs.FirstOrDefault(d => d.Id == id);

    private long UncategorisedId =>
        _categories.FirstOrDefault(c => c.Name == "Uncategorised")?.Id ?? _categories.LastOrDefault()?.Id ?? 0;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Nav) return; // ignore selection events bubbling up from lists inside tabs
        if (Nav.SelectedItem == DashboardTab) RefreshDashboard();
        OnNavChangedLazy();
    }

    // ------------------------------------------------------------------ dashboard
    private void RefreshDashboard()
    {
        StatDocs.Text = _docs.Count.ToString();
        StatPages.Text = _docs.Sum(d => d.PageCount).ToString();
        int scanning = _docs.Count(d => d.OcrStatus is OcrStatus.Pending or OcrStatus.Running);
        StatPagesLabel.Text = scanning > 0 ? $"Pages ({scanning} doc(s) still being scanned)" : "Pages, all searchable";

        var upcoming = _events.Where(e => e.IsUpcoming).OrderBy(e => e.EventDate).ThenBy(e => e.Time).ToList();
        StatUpcoming.Text = upcoming.Count.ToString();
        UpcomingList.ItemsSource = upcoming.Take(6).ToList();
        NoUpcomingText.Visibility = upcoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var open = _complaints.Where(c => c.Status is not ("Resolved" or "Closed" or "Draft")).ToList();
        StatComplaints.Text = open.Count.ToString();
        OpenComplaintsList.ItemsSource = open.OrderByDescending(c => c.IsOverdue).ThenBy(c => c.ResponseDue ?? DateTime.MaxValue).Take(5).ToList();
        NoComplaintsText.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RecentList.ItemsSource = _docs.OrderByDescending(d => d.ImportedAt).Take(6).ToList();
        NoRecentText.Visibility = _docs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var starred = _docs.Where(d => d.IsImportant).ToList();
        StarredList.ItemsSource = starred.Take(8).ToList();
        NoStarredText.Visibility = starred.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var me = _parties.FirstOrDefault(p => p.IsMe);
        WelcomeText.Text = me != null && !string.IsNullOrWhiteSpace(me.Name)
            ? $"Hello, {me.Name.Split(' ')[0]}"
            : "Your case at a glance";
        RefreshDashboardExtras();
        ScheduleMemoryRebuild();
    }

    private void OpenDocFromList_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CaseDocument d) OpenDocument(d);
    }

    private void GoSearch_Click(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = SearchTab;
        Dispatcher.BeginInvoke(() => SearchBox.Focus());
    }

    private void GoBundle_Click(object sender, RoutedEventArgs e) => Nav.SelectedItem = BundleTab;

    // ------------------------------------------------------------------ opening documents
    public void OpenDocument(CaseDocument doc, int page = 1)
    {
        if (doc == null) return;
        var fresh = Db.GetDocument(doc.Id);
        if (fresh == null) return;
        ApplyDisplayNames(new[] { fresh });
        var viewer = new DocumentViewerWindow(fresh, page, _categories, _parties) { Owner = this };
        viewer.Closed += (_, _) => ReloadKeepingSelection();
        viewer.EventAdded += () => { _events = Db.GetEvents(); RefreshTimeline(); RefreshDashboard(); };
        viewer.Show();
    }

    private void ReloadKeepingSelection()
    {
        var selectedIds = DocGrid.SelectedItems.Cast<CaseDocument>().Select(d => d.Id).ToHashSet();
        ReloadAll();
        foreach (var d in DocGrid.Items.Cast<CaseDocument>().Where(d => selectedIds.Contains(d.Id)))
            DocGrid.SelectedItems.Add(d);
    }

    // ------------------------------------------------------------------ import
    private const string ImportFilter =
        "Supported documents|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif;*.docx;*.txt;*.csv;*.md|" +
        "PDF documents|*.pdf|Images and scans|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif|Word documents|*.docx|All files|*.*";

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = ImportFilter, Title = "Choose documents to add" };
        if (dlg.ShowDialog(this) == true) ImportFiles(dlg.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        var files = new List<string>();
        foreach (var p in dropped)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories));
            else if (File.Exists(p)) files.Add(p);
        }
        // Skip hidden/system clutter such as Thumbs.db and desktop.ini
        files = files.Where(f => !Path.GetFileName(f).StartsWith('.') &&
                                 !new[] { ".db", ".ini", ".lnk", ".tmp" }.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (files.Count > 0) Dispatcher.BeginInvoke(() => ImportFiles(files.ToArray()));
    }

    private void ImportFiles(string[] paths)
    {
        long defaultCategory = _filterKey.StartsWith("cat:") ? long.Parse(_filterKey[4..]) : UncategorisedId;
        long? defaultParty = _filterKey.StartsWith("party:") ? long.Parse(_filterKey[6..]) : null;
        var dlg = new ImportWindow(paths, _categories, _parties, defaultCategory, defaultParty) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        var imported = new List<CaseDocument>();
        var failed = new List<string>();
        try
        {
            var rows = dlg.Rows.ToList();
            var hashes = rows.ToDictionary(r => r, r => { try { return LibraryService.ComputeHash(r.SourcePath); } catch { return null; } });
            var duplicates = rows.Where(r => hashes[r] != null && Db.FindByHash(hashes[r]) != null).ToList();
            if (duplicates.Count > 0)
            {
                Mouse.OverrideCursor = null;
                var names = string.Join("\n", duplicates.Take(10).Select(r => "• " + Path.GetFileName(r.SourcePath)));
                var answer = MessageBox.Show(this,
                    $"{duplicates.Count} file(s) are already in your library:\n\n{names}\n\nImport them again anyway?\n(Choose No to skip them.)",
                    "Already imported", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.No) rows = rows.Except(duplicates).ToList();
                Mouse.OverrideCursor = Cursors.Wait;
            }

            foreach (var r in rows)
            {
                try
                {
                    var details = new CaseDocument
                    {
                        Title = r.Title, CategoryId = r.CategoryId, PartyId = r.PartyId > 0 ? r.PartyId : null,
                        DocumentDate = r.DocumentDate, DateReceived = r.DateReceived, Author = r.Author ?? ""
                    };
                    imported.Add(Lib.Import(r.SourcePath, details, hashes[r]));
                }
                catch (Exception ex) { failed.Add($"{Path.GetFileName(r.SourcePath)}: {ex.Message}"); }
            }
        }
        finally { Mouse.OverrideCursor = null; }

        ReloadAll();
        EnqueueOcr(imported);
        QueueAutoCheck(imported);
        Nav.SelectedItem = DocumentsTab;
        if (failed.Count > 0)
            MessageBox.Show(this, "Some files could not be imported:\n\n" + string.Join("\n", failed), "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ------------------------------------------------------------------ background text scanning
    private void EnqueueOcr(IEnumerable<CaseDocument> docs)
    {
        foreach (var d in docs)
            if (OcrService.CanExtract(d.FileExtension) && !_ocrQueue.Contains(d.Id))
                _ocrQueue.Enqueue(d.Id);
        if (!_ocrRunning && _ocrQueue.Count > 0) _ = RunOcrQueueAsync();
    }

    /// <summary>
    /// Scans queued documents in the background. The performance profile decides how many run at
    /// once: one at a time on a laptop on battery, several on a desktop. Each worker has its own OCR
    /// engine and runs at reduced priority so the app stays responsive.
    /// </summary>
    private async Task RunOcrQueueAsync()
    {
        _ocrRunning = true;
        TaskBanner.Visibility = Visibility.Visible;
        int workers = Math.Max(1, Performance.OcrWorkers);
        int active = 0, done = 0;
        string current = "";
        void Banner()
        {
            var more = _ocrQueue.Count > 0 ? $" · {_ocrQueue.Count} queued" : "";
            TaskBannerText.Text = active > 1
                ? $"Reading text from {active} documents at once{more} · {done} done"
                : $"Reading text: “{current}”{more}";
        }

        async Task Worker()
        {
            var engine = OcrService.CreateEngine();
            while (_ocrQueue.Count > 0)
            {
                var id = _ocrQueue.Dequeue();
                var doc = Db.GetDocument(id);
                if (doc == null || doc.DeletedAt != null) continue;
                current = doc.Title.Length > 40 ? doc.Title[..40] + "…" : doc.Title;
                var mem = FindDoc(id);
                if (mem != null) mem.OcrStatus = OcrStatus.Running;
                active++;
                Banner();
                var progress = new Progress<string>(s => { if (active <= 1) TaskBannerText.Text = $"Reading text: “{current}” – {s}"; });
                try
                {
                    await Task.Factory.StartNew(() =>
                    {
                        Thread.CurrentThread.Priority = Performance.BackgroundPriority;
                        try { Lib.ProcessTextAsync(doc, progress, engine).GetAwaiter().GetResult(); }
                        finally { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (Exception ex) { AppLog.Error("OCR", $"Background scan crashed for document #{id}", ex); }
                active--;
                done++;
                if (mem != null)
                {
                    mem.OcrStatus = doc.OcrStatus;
                    mem.PageCount = doc.PageCount;
                }
                Banner();
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Min(workers, Math.Max(1, _ocrQueue.Count))).Select(_ => Worker()));
        }
        finally
        {
            _ocrRunning = false;
            TaskBanner.Visibility = Visibility.Collapsed;
            if (done > 0) AppLog.Info("OCR", $"Scanned {done} document(s) with {workers} worker(s)");
            RefreshDashboard();
            UpdateBundleSummary();
        }
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>Reminds the user about family court confidentiality rules before anything leaves the app.</summary>
    private bool ConfirmConfidentiality() => ConfirmConfidentiality(this);

    public static bool ConfirmConfidentiality(Window owner)
    {
        if (Db.GetSetting("remindConfidential", "1") != "1") return true;
        return MessageBox.Show(owner,
            "Before you share:\n\n" +
            "Papers from family proceedings about children are usually confidential. Sharing them with anyone who isn't a party " +
            "can be contempt of court unless the Family Procedure Rules (r.12.73 / PD 12G) allow it or the court gives permission.\n\n" +
            "If you're unsure, check PD 12G or ask the court or a legal adviser first.\n\nContinue?",
            "Confidentiality reminder", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
    }

    internal static void OpenWithShell(string path)
    {
        if (File.Exists(path) && FileKinds.IsRunnable(path))
        {
            // Opening this would run it as a program, not display it
            AppLog.Warn("Shell", "Refused to open a runnable file type: " + Path.GetExtension(path));
            MessageBox.Show($"\"{Path.GetFileName(path)}\" is a type of file Windows would run as a program, so CaseKeeper " +
                            "won't open it. It will be shown in File Explorer instead.", "CaseKeeper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            try { ShowInExplorer(path); } catch (Exception ex) { AppLog.Warn("Shell", "Couldn't show a file in Explorer", ex); }
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppLog.Warn("Shell", "Couldn't open a file or folder", ex);
            MessageBox.Show("Couldn't open the file: " + ex.Message, "CaseKeeper");
        }
    }

    private static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
}