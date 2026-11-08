using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CaseKeeper.Services;
using CaseKeeper.UI;
using Microsoft.Data.Sqlite;

namespace CaseKeeper.Views;

/// <summary>
/// Settings → Updates: signed in-app updates for the website copy (the same scheme as DarkCipher ServerManagement).
/// Check, a verified download, a backup prompt, then the normal setup wizard; afterwards CaseKeeper confirms the new
/// version is running. Optional: a daily or weekly automatic check, and downloading in the background. Nothing is ever
/// installed without you. The Microsoft Store copy is updated by the Store instead. See <see cref="Updater"/>.
/// </summary>
public partial class MainWindow
{
    private UpdateInfo _update;
    private string _updatePath;
    private TextBlock _updResult, _updNotes, _updPackage, _updVerify;
    private StackPanel _updDetails;
    private ProgressBar _updProgress;
    private Button _updCheck, _updDownload, _updInstall;
    private Border _updNotice;
    private TextBlock _updNoticeText;

    private UIElement UpdatesCard()
    {
        var sp = new StackPanel();
        if (!Updater.Enabled)
        {
            sp.Children.Add(Ui.Muted($"Installed version {AppVersion}.", 12.5));
            sp.Children.Add(Ui.Callout(Updater.DisabledMessage));
            sp.Children.Add(Ui.Muted(AppPackage.IsPackaged ? "Nothing here contacts the internet. Windows checks the Store's signature on every update." : "Nothing here contacts the internet.", 11.5));
            return sp;
        }

        sp.Children.Add(Ui.Muted($"Installed version {AppVersion}. CaseKeeper only looks for an update when you click Check for updates, or on the schedule you choose below. " +
                                 "A check is a plain download of a small file and its two signatures: nothing about you, this PC or your case is sent.", 12.5));

        // ---- where to look
        var source = new ComboBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var sources = new[]
        {
            ("website", "The DarkCipher Systems website (recommended)"),
            ("github", $"GitHub releases (github.com/{Updater.GitHubRepo})"),
            ("url", "An https address of my own")
        };
        source.ItemsSource = sources.Select(s => s.Item2).ToList();
        source.SelectedIndex = Math.Max(0, Array.FindIndex(sources, s => s.Item1 == Updater.Settings.Source));
        AutomationProperties.SetName(source, "Updates from");

        var endpoint = new TextBox { Text = Updater.Settings.Endpoint, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        endpoint.SetResourceReference(FontFamilyProperty, "MonoFont");
        AutomationProperties.SetName(endpoint, "Update address");
        var endpointRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        endpointRow.Children.Add(endpoint);
        endpointRow.Children.Add(Ui.Button("Save address", "", (_, _) =>
        {
            var v = endpoint.Text.Trim();
            if (Updater.ValidateEndpoint(v) is { } bad) { MessageBox.Show(this, bad, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            Updater.Settings.Endpoint = v; SaveUpdateSettings();
            Db.Audit("settings.change", "settings", null, "Update address changed");
        }));
        var endpointPanel = Labelled("Update address", endpointRow, "The full address of update.json, for example a mirror you run. The setup program must be in the same folder, on the same server.");

        var tokenBox = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(tokenBox, "GitHub access token");
        var tokenState = Ui.Muted("", 12);
        tokenState.VerticalAlignment = VerticalAlignment.Center;
        tokenState.Margin = new Thickness(8, 0, 0, 0);
        void ShowTokenState() => tokenState.Text = Updater.GitHubToken != null ? "A token is saved (encrypted)." : "No token: the repository must be public.";
        var tokenRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        tokenRow.Children.Add(tokenBox);
        tokenRow.Children.Add(Ui.Button("Save token", "", (_, _) =>
        {
            var t = tokenBox.Password.Trim();
            if (t.Length == 0) return;
            if (Updater.ValidateToken(t) is { } bad) { MessageBox.Show(this, bad, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            Updater.GitHubToken = t; SaveUpdateSettings(); tokenBox.Clear(); ShowTokenState();
            Db.Audit("settings.change", "settings", null, "Update access token saved");
        }));
        tokenRow.Children.Add(Ui.Button("Remove", "", (_, _) =>
        {
            Updater.GitHubToken = null; SaveUpdateSettings(); tokenBox.Clear(); ShowTokenState();
            Db.Audit("settings.change", "settings", null, "Update access token removed");
        }));
        tokenRow.Children.Add(tokenState);
        ShowTokenState();
        var tokenPanel = Labelled("GitHub access token (only while the repository is private)", tokenRow,
            $"A fine-grained token with read-only Contents access to {Updater.GitHubRepo} only. It is encrypted with your Windows account, never put in a backup, " +
            "and sent only to api.github.com. Updates are still installed only when CaseKeeper's own release keys have signed them.");

        void ShowSource()
        {
            var v = Updater.Settings.Source;
            endpointPanel.Visibility = v == "url" ? Visibility.Visible : Visibility.Collapsed;
            tokenPanel.Visibility = v == "github" ? Visibility.Visible : Visibility.Collapsed;
        }
        source.SelectionChanged += (_, _) =>
        {
            var v = sources[Math.Max(0, source.SelectedIndex)].Item1;
            if (v == Updater.Settings.Source) return;
            Updater.Settings.Source = v; SaveUpdateSettings(); ShowSource();
            Db.Audit("settings.change", "settings", null, "Updates from: " + sources[source.SelectedIndex].Item2);
        };
        ShowSource();
        sp.Children.Add(Labelled("Updates from", source));
        sp.Children.Add(endpointPanel);
        sp.Children.Add(tokenPanel);

        // ---- check, download, install
        _updCheck = Ui.Button("Check for updates", "", async (_, _) => await CheckForUpdateAsync(), primary: true);
        _updCheck.Margin = new Thickness(0, 12, 8, 0);
        _updCheck.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(_updCheck);
        _updResult = Ui.Text("", null, 13);
        _updResult.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_updResult, AutomationLiveSetting.Polite);
        sp.Children.Add(_updResult);

        _updDetails = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        _updDetails.Children.Add(new TextBlock { Text = "WHAT'S NEW", Style = Ui.Style("Label"), Margin = new Thickness(0, 4, 0, 2) });
        _updNotes = Ui.Text("", null, 12.5);
        _updDetails.Children.Add(new ScrollViewer { Content = _updNotes, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _updDetails.Children.Add(new TextBlock { Text = "SETUP PROGRAM", Style = Ui.Style("Label"), Margin = new Thickness(0, 10, 0, 2) });
        _updPackage = Ui.Text("", null, 11.5);
        _updPackage.SetResourceReference(FontFamilyProperty, "MonoFont");
        _updDetails.Children.Add(_updPackage);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _updDownload = Ui.Button("Download and verify", "", async (_, _) => await DownloadUpdateAsync(), primary: true);
        _updInstall = Ui.Button("Install… (backup first)", "", async (_, _) => await InstallUpdateAsync(), primary: true);
        _updInstall.IsEnabled = false;
        actions.Children.Add(_updDownload);
        actions.Children.Add(_updInstall);
        _updDetails.Children.Add(actions);
        _updProgress = new ProgressBar { Height = 6, Margin = new Thickness(0, 8, 0, 0), Maximum = 1, Visibility = Visibility.Collapsed };
        _updDetails.Children.Add(_updProgress);
        _updVerify = Ui.Text("", null, 12.5);
        _updVerify.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_updVerify, AutomationLiveSetting.Polite);
        _updDetails.Children.Add(_updVerify);
        sp.Children.Add(_updDetails);

        // ---- automatic checks: off by default
        sp.Children.Add(new TextBlock { Text = "AUTOMATIC CHECKS", Style = Ui.Style("Label"), Margin = new Thickness(0, 16, 0, 0) });
        sp.Children.Add(Ui.Muted("Off unless you switch them on. When on, CaseKeeper checks the source above at most once in the period you choose, a little while after it starts, " +
                                 "and shows a notice. Installing always asks you first.", 12));
        var auto = new CheckBox { Content = "Check for updates automatically", IsChecked = Updater.Settings.AutoCheck, Margin = new Thickness(0, 8, 0, 0) };
        var interval = new ComboBox { ItemsSource = new[] { "Once a week", "Once a day" }, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = Updater.Settings.Interval == "daily" ? 1 : 0 };
        AutomationProperties.SetName(interval, "How often");
        var autoDl = new CheckBox { Content = "Also download and verify a new version in the background (installing still asks you)", IsChecked = Updater.Settings.AutoDownload, Margin = new Thickness(0, 8, 0, 0) };
        void ShowAuto() { interval.IsEnabled = autoDl.IsEnabled = auto.IsChecked == true; }
        auto.Click += (_, _) =>
        {
            if (auto.IsChecked == true && MessageBox.Show(this,
                    $"CaseKeeper will contact {new Uri(Updater.Endpoint).Host} {(Updater.Settings.Interval == "daily" ? "once a day" : "once a week")}, shortly after it starts, " +
                    "to see whether a new version is available. Only the update information and its signatures are downloaded; nothing about you, this PC or your case is sent.\n\n" +
                    "Switch automatic checks on?", "Automatic update checks", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                auto.IsChecked = false;
                return;
            }
            Updater.Settings.AutoCheck = auto.IsChecked == true; SaveUpdateSettings(); ShowAuto();
            Db.Audit("settings.change", "settings", null, "Automatic update checks: " + (Updater.Settings.AutoCheck ? "on" : "off"));
        };
        interval.SelectionChanged += (_, _) =>
        {
            Updater.Settings.Interval = interval.SelectedIndex == 1 ? "daily" : "weekly"; SaveUpdateSettings();
            Db.Audit("settings.change", "settings", null, "Automatic update checks: " + interval.SelectedItem);
        };
        autoDl.Click += (_, _) =>
        {
            Updater.Settings.AutoDownload = autoDl.IsChecked == true; SaveUpdateSettings();
            Db.Audit("settings.change", "settings", null, "Background update download: " + (Updater.Settings.AutoDownload ? "on" : "off"));
        };
        ShowAuto();
        sp.Children.Add(auto);
        sp.Children.Add(Labelled("How often", interval));
        sp.Children.Add(autoDl);

        sp.Children.Add(Ui.Callout($"Trust: an update is offered only when its update information is signed by both CaseKeeper release keys built into this copy " +
                                   $"(ECDSA P-256 {Updater.ReleaseKeyX[..8]}…{Updater.ReleaseKeyY[^8..]} and post-quantum ML-DSA-65). The website, GitHub or any other server only " +
                                   "carries the files, so nothing changed there can pass. The setup program must then match the signed size and SHA-256 exactly. Older versions are never offered."));

        if (Updater.Ready is { } ready && File.Exists(ready.Path) && ready.Info.Version > Updater.Installed) ShowReadyUpdate(ready.Info, ready.Path);
        return sp;
    }

    private void SaveUpdateSettings()
    {
        try { Updater.SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var id = AppLog.Error("Updates", "Couldn't save the update settings", ex);
            MessageBox.Show(this, $"The update settings couldn't be saved ({id}).", "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowUpdateDetails(UpdateInfo i)
    {
        _update = i;
        _updNotes.Text = (i.Released.Length > 0 ? $"Released {i.Released}\n\n" : "") + (i.Notes.Length > 0 ? i.Notes : "(no release notes)") +
                         (i.SchemaChange ? "\n\nThis version changes how the library is stored: a backup before updating is strongly recommended." : "");
        _updPackage.Text = $"{i.FileName} · {i.Bytes / 1048576.0:0.0} MB\nSHA-256 {i.Sha256}\nfrom {i.Url.Host}\nUpdate information signed by both CaseKeeper release keys";
        _updDetails.Visibility = Visibility.Visible;
    }

    private void ShowReadyUpdate(UpdateInfo i, string path)
    {
        if (_updDetails == null) return;
        ShowUpdateDetails(i);
        _updatePath = path;
        _updResult.Text = $"Version {i.Version} was downloaded and verified in the background.";
        SetVerifyText(true, "Verified: ready to install.");
        _updInstall.IsEnabled = true;
    }

    private void SetVerifyText(bool ok, string text)
    {
        _updVerify.Text = (ok ? "✓ " : "✕ ") + text;
        _updVerify.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Success" : "Warning");
    }

    private async Task CheckForUpdateAsync()
    {
        _updCheck.IsEnabled = false;
        _updResult.Text = "Checking…";
        _updDetails.Visibility = Visibility.Collapsed; _update = null; _updatePath = null; _updInstall.IsEnabled = false; _updVerify.Text = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var (i, msg) = await Updater.CheckAsync(cts.Token);
            Updater.Settings.LastCheck = DateTime.Now; SaveUpdateSettings();
            _updResult.Text = msg;
            AppLog.Info("Updates", i != null ? $"Checked: {i.Version} is available" : "Checked: " + msg);
            if (i != null) ShowUpdateDetails(i);
        }
        finally { _updCheck.IsEnabled = true; }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_update is not { } i) return;
        _updDownload.IsEnabled = false; _updInstall.IsEnabled = false;
        _updProgress.Visibility = Visibility.Visible; _updProgress.Value = 0;
        _updVerify.Text = "Downloading into a private temporary folder…";
        _updVerify.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var (path, v) = await Updater.DownloadAndVerifyAsync(i, new Progress<double>(p => _updProgress.Value = p), cts.Token);
            SetVerifyText(v.Ok, v.Message);
            _updatePath = path;
            _updInstall.IsEnabled = v.Ok && path != null;
            Db.Audit("app.update.download", "app", null, $"Update {i.Version} downloaded: {(v.Ok ? "verified" : v.Verdict.ToString())}");
            if (!v.Ok) AppLog.Warn("Updates", $"Download of {i.Version} refused: {v.Verdict}");
        }
        catch (OperationCanceledException) { SetVerifyText(false, "The download took too long and was stopped; nothing was kept. Try again later."); }
        finally { _updDownload.IsEnabled = true; _updProgress.Visibility = Visibility.Collapsed; }
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is not { } i || _updatePath == null) return;
        // Checked again right before anything happens: nothing may have changed since the download
        var again = Updater.Verify(_updatePath, i);
        if (!again.Ok)
        {
            SetVerifyText(false, again.Message); _updInstall.IsEnabled = false;
            MessageBox.Show(this, "The downloaded setup program no longer verifies, so it won't be used:\n\n" + again.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_bulk is { IsRunning: true })
        {
            MessageBox.Show(this, "A bulk import is running. Pause it or let it finish, then install the update.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var choice = MessageBox.Show(this,
            "Back up your library before updating?\n\n" +
            (i.SchemaChange ? "This version changes how the library is stored, so a backup is strongly recommended.\n\n"
                            : "Updating keeps your library exactly where it is, but a backup is the safe way to be sure.\n\n") +
            "Yes: make a backup first.   No: update without a backup.   Cancel: don't update now.",
            "Update CaseKeeper", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.Yes && !await RunBackupAsync())
        {
            MessageBox.Show(this, "The backup wasn't completed, so the update was not started.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"CaseKeeper will close and the setup program will update it from {AppVersion} to {i.Version}. Windows may ask for permission. " +
                "Your library and settings stay where they are.\n\nWhen CaseKeeper opens again it checks that the new version is really running.",
                "Install update", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK) != MessageBoxResult.OK) return;

        try { Lib.TakeSnapshot($"before updating to {i.Version}"); }
        catch (Exception ex)
        {
            var id = AppLog.Error("Updates", "The safety snapshot before the update failed", ex);
            if (MessageBox.Show(this, $"The safety snapshot of your database failed ({id}).\n\nInstall the update anyway?", "Install update",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        try
        {
            Db.Audit("app.update.install", "app", null, $"Update installer started: {AppVersion} → {i.Version}");
            Updater.LaunchInstaller(_updatePath, i);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            var id = AppLog.Error("Updates", "The setup program wasn't started", ex);
            // Win32 1223: the person said no at the Windows permission prompt
            var why = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "Windows permission wasn't given." : ex.Message;
            Updater.Settings.PendingVersion = ""; SaveUpdateSettings();
            MessageBox.Show(this, $"The update wasn't started ({id}): {why}\n\nNothing has changed.", "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Close cleanly so the setup program can replace the files
        foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
        SqliteConnection.ClearAllPools();
        LibraryLock.Release();
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ start-up

    /// <summary>After an update: says whether the new version is really running. Then the opt-in automatic check.</summary>
    private void UpdatesAtStartup()
    {
        try
        {
            if (Updater.ConfirmPendingUpdate() is { } done)
            {
                Db.Audit(done.Completed ? "app.update.verified" : "app.update.failed", "app", null, done.Message);
                Dispatcher.BeginInvoke(() => MessageBox.Show(this, done.Message, "Update",
                    MessageBoxButton.OK, done.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex) { AppLog.Warn("Updates", "Couldn't check the result of the last update", ex); }
        _ = AutoUpdateCheckAsync();
    }

    /// <summary>The opt-in automatic check (off by default), daily or weekly: shows a notice; with the background
    /// download allowed it also downloads and verifies the update. It never installs.</summary>
    private async Task AutoUpdateCheckAsync()
    {
        if (!Updater.AutoCheckDue) return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var (info, msg) = await Updater.CheckAsync(cts.Token);
            Updater.Settings.LastCheck = DateTime.Now;
            Updater.SaveSettings();
            AppLog.Info("Updates", info != null ? $"Automatic check: {info.Version} is available" : "Automatic check: " + msg);
            if (info == null) return;
            if (!Updater.Settings.AutoDownload) { ShowUpdateNotice($"CaseKeeper {info.Version} is available (you have {AppVersion})."); return; }
            using var dl = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var (path, v) = await Updater.DownloadAndVerifyAsync(info, null, dl.Token);
            Db.Audit("app.update.download", "app", null, $"Update {info.Version} downloaded in the background: {(v.Ok ? "verified" : v.Verdict.ToString())}");
            if (v.Ok && path != null)
            {
                Updater.Ready = (info, path);
                ShowReadyUpdate(info, path);
                ShowUpdateNotice($"CaseKeeper {info.Version} is downloaded and verified, ready to install.");
            }
            else ShowUpdateNotice($"CaseKeeper {info.Version} is available, but its download didn't verify, so nothing was kept.");
        }
        catch (Exception ex) { AppLog.Warn("Updates", "The automatic update check failed", ex); }
    }

    /// <summary>A small notice at the bottom right with a way to Settings → Updates. It stays until dismissed.</summary>
    private void ShowUpdateNotice(string text)
    {
        if (_updNotice == null)
        {
            if (TaskBanner.Parent is not Grid host) return;
            _updNoticeText = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
            AutomationProperties.SetLiveSetting(_updNoticeText, AutomationLiveSetting.Polite);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_updNoticeText);
            var open = new Button { Content = "Review", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            AutomationProperties.SetName(open, "Open Settings, Updates");
            open.Click += (_, _) => { _updNotice.Visibility = Visibility.Collapsed; GoToSettings("updates"); };
            var close = new Button { Content = "✕", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            AutomationProperties.SetName(close, "Dismiss");
            close.Click += (_, _) => _updNotice.Visibility = Visibility.Collapsed;
            row.Children.Add(open);
            row.Children.Add(close);
            _updNotice = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 110),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 10, 8), Child = row
            };
            _updNotice.SetResourceReference(Border.BackgroundProperty, "Sidebar");
            AutomationProperties.SetName(_updNotice, "Update notice");
            host.Children.Add(_updNotice);
        }
        _updNoticeText.Text = text;
        _updNotice.Visibility = Visibility.Visible;
    }
}
