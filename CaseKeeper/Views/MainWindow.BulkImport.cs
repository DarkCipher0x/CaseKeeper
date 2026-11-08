using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;

namespace CaseKeeper.Views;

/// <summary>
/// Bulk Case Import as the main window sees it: the one runner the whole app shares, its progress
/// banner, picking up interrupted jobs, and the entry points (toolbar, palette, right-click, dashboard).
/// </summary>
public partial class MainWindow
{
    private BulkImportService _bulk;
    private Border _bulkBanner;
    private TextBlock _bulkBannerText;
    private ProgressBar _bulkBannerBar;

    /// <summary>The app's single Bulk Case Import runner. Only one job runs at a time.</summary>
    public BulkImportService Bulk => _bulk ??= CreateBulk();

    private BulkImportService CreateBulk()
    {
        var svc = new BulkImportService(Lib, AiService.BulkClient, AiService.BulkAnalysisSettings, () => AiService.Enabled)
        {
            // "Read text later": hand the documents to the normal background queue
            QueueTextLater = docs => Dispatcher.BeginInvoke(() => EnqueueOcr(docs)),
            // Documents read at once: this PC's tier, or the person's choice; always one on battery (read again for each run)
            AiWorkers = AiService.BulkWorkers(),
            // Cloud AI: the pre-send review, on the UI thread; documents that can't be masked well can go to the local AI instead
            ConfirmCloudSend = req => Dispatcher.InvokeAsync(() => Modules.CloudSendReviewWindow.Ask(this, req)).Task,
            LocalAiFactory = () => AiService.Enabled ? new AiService.OllamaJsonClient() : null
        };
        svc.Progress += p => Dispatcher.BeginInvoke(() => UpdateBulkBanner(p));
        return svc;
    }

    /// <summary>Loads the confidence bands people can change in Import review.</summary>
    public static void LoadBulkThresholds()
    {
        static double Read(string key, double fallback) =>
            double.TryParse(Db.GetSetting(key, ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? Math.Clamp(v, 0.1, 0.99) : fallback;
        BulkThresholds.High = Read("bulk.high", 0.85);
        BulkThresholds.Medium = Math.Min(Read("bulk.medium", 0.6), BulkThresholds.High - 0.05);
    }

    // ------------------------------------------------------------------ entry points
    public void OpenBulkImport()
    {
        if (Bulk.IsRunning)
        {
            MessageBox.Show(this, "A bulk import is already running. You can follow it at the bottom of the window, or in Import review.", "Bulk Case Import");
            GoTo("review");
            return;
        }
        new BulkImportWindow { Owner = this }.Show();
    }

    /// <summary>Asks the AI to organise documents already in the library (right-click → Organise with AI).</summary>
    public void OrganiseWithAi(IReadOnlyList<CaseDocument> docs)
    {
        if (docs.Count == 0) return;
        if (Bulk.IsRunning)
        {
            MessageBox.Show(this, "A bulk import is running. Try again when it has finished.", "Organise with AI");
            return;
        }
        if (!AiService.EnsureEnabled(this, AiService.BulkProvider)) return;
        if (MessageBox.Show(this, $"Ask {(AiProviders.IsCloud(AiService.BulkProvider) ? AiProviders.Get(AiService.BulkProvider).Name : "your local AI")} to suggest a category, dates and records for {(docs.Count == 1 ? "this document" : $"these {docs.Count} documents")}?\n\n" +
                                  "Nothing changes until you approve each suggestion in Import review.", "Organise with AI",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var job = Bulk.CreateLibraryJob(docs);
        _ = RunBulkJobAsync(job.Id);
        GoTo("review");
    }

    /// <summary>Runs a job, keeping the banner and the screens up to date. Returns when it finishes or pauses.</summary>
    public async Task RunBulkJobAsync(long jobId)
    {
        if (Bulk.IsRunning) return;
        ShowBulkBanner("Bulk import starting…");
        Bulk.AiWorkers = AiService.BulkWorkers();
        try { await Bulk.RunAsync(jobId); }
        finally
        {
            HideBulkBanner();
            if (_closeAfterBulk) _ = Dispatcher.BeginInvoke(Close);
            else
            {
                ReloadAll();
                _reviewView?.Reload();
                RefreshDashboard();
            }
        }
    }

    public void ResumeBulkJob(long jobId)
    {
        if (Bulk.IsRunning) { MessageBox.Show(this, "A bulk import is already running.", "Bulk Case Import"); return; }
        BulkImportWindow.Resume(this, jobId);
    }

    // ------------------------------------------------------------------ progress banner
    private void EnsureBulkBanner()
    {
        if (_bulkBanner != null || TaskBanner.Parent is not Grid host) return;
        _bulkBannerText = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(_bulkBannerText, AutomationLiveSetting.Polite);
        _bulkBannerBar = new ProgressBar { Width = 90, Height = 5, Margin = new Thickness(0, 0, 10, 0), Minimum = 0, Maximum = 1 };
        _bulkBannerBar.SetResourceReference(ForegroundProperty, "PrimaryHover");
        _bulkBannerBar.SetResourceReference(BackgroundProperty, "SidebarHover");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_bulkBannerBar);
        row.Children.Add(_bulkBannerText);
        var open = new Button { Content = "Open", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        AutomationProperties.SetName(open, "Open Import review");
        open.Click += (_, _) => GoTo("review");
        row.Children.Add(open);
        _bulkBanner = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 62),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 10, 8), Child = row, Visibility = Visibility.Collapsed
        };
        _bulkBanner.SetResourceReference(Border.BackgroundProperty, "Sidebar");
        AutomationProperties.SetName(_bulkBanner, "Bulk import progress");
        host.Children.Add(_bulkBanner);
    }

    private void ShowBulkBanner(string text)
    {
        EnsureBulkBanner();
        if (_bulkBanner == null) return;
        _bulkBannerText.Text = text;
        _bulkBannerBar.IsIndeterminate = true;
        _bulkBanner.Visibility = Visibility.Visible;
    }

    private void HideBulkBanner()
    {
        if (_bulkBanner != null) _bulkBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateBulkBanner(BulkProgress p)
    {
        if (p.Finished) return;
        EnsureBulkBanner();
        if (_bulkBanner == null) return;
        _bulkBanner.Visibility = Visibility.Visible;
        var phase = p.Phase switch
        {
            ImportPhase.Importing => "Importing", ImportPhase.PreparingText => "Reading text", ImportPhase.Analysing => "AI analysis",
            ImportPhase.Checking => "Cross-checking", _ => "Bulk import"
        };
        _bulkBannerText.Text = p.Total > 0 ? $"{phase}: {p.Done:N0} of {p.Total:N0}{(p.RemainingText.Length > 0 ? " · " + p.RemainingText : "")}" : $"{phase}…";
        _bulkBannerBar.IsIndeterminate = p.Total == 0;
        if (p.Total > 0) { _bulkBannerBar.Maximum = p.Total; _bulkBannerBar.Value = Math.Min(p.Done, p.Total); }
    }

    // ------------------------------------------------------------------ closing while a job runs
    /// <summary>Asks before closing mid-import. Either way the job is safe: it carries on next time.</summary>
    private void ConfirmCloseDuringBulk(CancelEventArgs e)
    {
        if (_bulk == null || !_bulk.IsRunning) return;
        e.Cancel = true;
        if (_closeAfterBulk) return;
        if (MessageBox.Show(this, "A bulk import is still running.\n\nClose CaseKeeper anyway? The import is paused and carries on from where it stopped " +
                                  "the next time you open CaseKeeper (from Import review).", "Bulk import running",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        // Pause first (it takes a moment), so the job is saved as paused; the window closes when the runner stops
        _closeAfterBulk = true;
        ShowBulkBanner("Pausing the import, then closing…");
        _bulk.RequestPause();
    }

    private bool _closeAfterBulk;

    // ------------------------------------------------------------------ dashboard
    /// <summary>A notice on the dashboard when an import stopped part-way, or suggestions are waiting.</summary>
    private void AddBulkDashboardNotice(Panel host)
    {
        List<ImportJob> unfinished;
        int waiting;
        try
        {
            unfinished = Bulk.UnfinishedJobs();
            waiting = Db.Query<AiFinding>("Status IN (0,3)").Count;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Dashboard", "Couldn't read bulk import jobs", ex);
            return;
        }
        if (unfinished.Count == 0 && waiting == 0) return;
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(unfinished.Count > 0 ? "A bulk import stopped part-way" : "Import suggestions waiting for you", "H2"));
        if (unfinished.Count > 0)
        {
            var job = unfinished[0];
            sp.Children.Add(Ui.Muted($"“{job.RootName}”, started {job.CreatedText}: {job.PhaseText.ToLowerInvariant()} ({job.StateText.ToLowerInvariant()}). " +
                                     "Everything done so far is safe. Carry on whenever you're ready.", 12.5));
        }
        if (waiting > 0) sp.Children.Add(Ui.Muted($"{waiting:N0} AI suggestion(s) to check. Nothing is added to your case until you approve it.", 12.5));
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (unfinished.Count > 0)
        {
            var id = unfinished[0].Id;
            row.Children.Add(Ui.Button("Carry on importing", "", (_, _) => ResumeBulkJob(id), primary: true));
        }
        row.Children.Add(Ui.Button("Open Import review", "", (_, _) => GoTo("review"), primary: unfinished.Count == 0));
        sp.Children.Add(row);
        host.Children.Add(Ui.Card(sp, new Thickness(0, 0, 0, 18)));
    }

    private void BulkImport_Click(object sender, RoutedEventArgs e) => OpenBulkImport();

    private void OrganiseSelectedWithAi() => OrganiseWithAi(SelectedDocs());
}
