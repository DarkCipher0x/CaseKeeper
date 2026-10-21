using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CaseKeeper.Data;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.UI;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace CaseKeeper.Views;

/// <summary>Bundles &amp; Reports and Settings tabs.</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<BundleItem> _bundleItems = new();
    private string _lastBundlePath;
    private string _lastAutoParties = "";

    // ================================================================== BUNDLE
    private void InitBundle()
    {
        BundleSectionColumn.ItemsSource = DefaultCategories.BundleSections
            .Select(kv => new KeyValuePair<string, string>(kv.Key, $"{kv.Key} – {kv.Value}")).ToList();
        BundleGrid.ItemsSource = _bundleItems;
        _bundleItems.CollectionChanged += (_, _) => UpdateBundleSummary();

        BundleCaseNo.Text = Db.GetSetting("bundle.caseNo", "");
        BundleCourt.Text = Db.GetSetting("bundle.court", "");
        BundlePreparedBy.Text = Db.GetSetting("bundle.preparedBy", "");
        BundleParties.Text = Db.GetSetting("bundle.parties", "");
        UpdateBundleSummary();
    }

    /// <summary>Fills the cover-page parties box from the Parties tab, unless the user has typed their own.</summary>
    private void AutoFillBundleParties()
    {
        var lines = new List<string>();
        foreach (var p in _parties.Where(p => p.RoleGroup != "Other" && !string.IsNullOrWhiteSpace(p.Name)))
            lines.Add($"{p.Name}  ({p.Role})");
        var text = string.Join(Environment.NewLine, lines);
        if (string.IsNullOrWhiteSpace(BundleParties.Text) || BundleParties.Text == _lastAutoParties)
        {
            BundleParties.Text = text;
            _lastAutoParties = text;
        }
    }

    private void RefreshBundleSources()
    {
        var selected = (BundleCategoryFilter.SelectedItem as Category)?.Id ?? 0;
        var cats = new List<Category> { new() { Id = 0, Name = "All categories" } };
        cats.AddRange(_categories.Where(c => c.DocumentCount > 0));
        BundleCategoryFilter.ItemsSource = cats;
        BundleCategoryFilter.SelectedItem = cats.FirstOrDefault(c => c.Id == selected) ?? cats[0];
        FilterBundleSource();

        // keep bundle items pointing at fresh document objects
        var byId = _docs.ToDictionary(d => d.Id);
        for (int i = 0; i < _bundleItems.Count; i++)
        {
            if (byId.TryGetValue(_bundleItems[i].Document.Id, out var fresh))
                _bundleItems[i] = new BundleItem { Document = fresh, Section = _bundleItems[i].Section };
        }
    }

    private void BundleSourceFilter_Changed(object sender, EventArgs e)
    {
        if (IsLoaded) FilterBundleSource();
    }

    private void FilterBundleSource()
    {
        var catId = (BundleCategoryFilter.SelectedItem as Category)?.Id ?? 0;
        var text = BundleSourceFilterBox.Text.Trim();
        BundleSource.ItemsSource = _docs
            .Where(d => catId == 0 || d.CategoryId == catId)
            .Where(d => text.Length == 0 || $"{d.Title} {d.Author} {d.Tags} {d.PartyName}".Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.DocumentDate ?? DateTime.MaxValue)
            .ToList();
    }

    private void AddToBundle(IEnumerable<CaseDocument> docs)
    {
        var cats = _categories.ToDictionary(c => c.Id);
        foreach (var d in docs)
        {
            if (_bundleItems.Any(b => b.Document.Id == d.Id)) continue;
            var section = cats.TryGetValue(d.CategoryId, out var c) && !string.IsNullOrEmpty(c.BundleSection) ? c.BundleSection : "F";
            _bundleItems.Add(new BundleItem { Document = d, Section = section });
        }
    }

    private void BundleAddTicked_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _docs.Where(d => d.IsSelected).ToList();
        if (ticked.Count == 0)
        {
            MessageBox.Show(this, "Tick the documents you want on the left first.", "Bundle");
            return;
        }
        AddToBundle(ticked);
        foreach (var d in ticked) d.IsSelected = false;
    }

    private void BundleAddAllShown_Click(object sender, RoutedEventArgs e) =>
        AddToBundle((BundleSource.ItemsSource as IEnumerable<CaseDocument>) ?? Enumerable.Empty<CaseDocument>());

    private void BundleUp_Click(object sender, RoutedEventArgs e) => MoveBundleItem(-1);
    private void BundleDown_Click(object sender, RoutedEventArgs e) => MoveBundleItem(1);

    private void MoveBundleItem(int delta)
    {
        if (BundleGrid.SelectedItem is not BundleItem item) return;
        BundleGrid.CommitEdit();
        int i = _bundleItems.IndexOf(item), j = i + delta;
        if (j < 0 || j >= _bundleItems.Count) return;
        _bundleItems.Move(i, j);
        BundleGrid.SelectedItem = item;
        BundleGrid.ScrollIntoView(item);
    }

    private void BundleRemove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in BundleGrid.SelectedItems.Cast<BundleItem>().ToList()) _bundleItems.Remove(item);
    }

    private void BundleSort_Click(object sender, RoutedEventArgs e)
    {
        BundleGrid.CommitEdit();
        var sorted = _bundleItems.OrderBy(b => b.Section).ThenBy(b => b.Document.DocumentDate ?? DateTime.MaxValue).ToList();
        _bundleItems.Clear();
        foreach (var b in sorted) _bundleItems.Add(b);
    }

    private void BundleClear_Click(object sender, RoutedEventArgs e)
    {
        if (_bundleItems.Count > 0 && MessageBox.Show(this, "Remove everything from this bundle? (Documents stay in your library.)", "Clear",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _bundleItems.Clear();
    }

    private void UpdateBundleSummary()
    {
        if (BundleSummaryText == null) return;
        int pages = _bundleItems.Sum(b => b.Document.PageCount);
        int unsupported = _bundleItems.Count(b => !FileKinds.CanBundle(b.Document.FileExtension) && !b.Document.IsWord && !b.Document.IsText);
        var text = $"{_bundleItems.Count} document(s) · about {pages} page(s) of documents";
        if (pages > 350 && ModeBundle.IsChecked == true) text += "  ⚠ over the usual 350-page limit";
        if (unsupported > 0) text += $" · {unsupported} file(s) of a type that can't go into a PDF (save them as PDF first)";
        BundleSummaryText.Text = text;
    }

    private void BundleMode_Changed(object sender, RoutedEventArgs e)
    {
        if (BundleTitle == null) return;
        bool summary = ModeSummary.IsChecked == true;
        IntroPanel.Visibility = summary ? Visibility.Visible : Visibility.Collapsed;
        OptIndex.IsEnabled = OptDividers.IsEnabled = OptNotes.IsEnabled = !summary;
        if (summary && BundleTitle.Text == "Court Bundle") BundleTitle.Text = "Summary Report";
        if (!summary && BundleTitle.Text == "Summary Report") BundleTitle.Text = "Court Bundle";
        OptStamp.Content = summary ? "Page numbers (Page 1 of N)" : "Page numbers (A1, B12…)";
        UpdateBundleSummary();
    }

    private async void GenerateBundle_Click(object sender, RoutedEventArgs e)
    {
        BundleGrid.CommitEdit();
        bool summary = ModeSummary.IsChecked == true;
        if (_bundleItems.Count == 0 && !(OptChronology.IsChecked == true))
        {
            MessageBox.Show(this, "Add some documents (or tick 'Chronology') first.", "Create PDF");
            return;
        }
        var dlg = new SaveFileDialog
        {
            FileName = $"{LibraryService.SafeFileName(BundleTitle.Text)} {DateTime.Today:yyyy-MM-dd}.pdf",
            Filter = "PDF|*.pdf",
            InitialDirectory = Lib.ExportsFolder
        };
        if (dlg.ShowDialog(this) != true) return;

        Db.SetSetting("bundle.caseNo", BundleCaseNo.Text);
        Db.SetSetting("bundle.court", BundleCourt.Text);
        Db.SetSetting("bundle.preparedBy", BundlePreparedBy.Text);
        if (BundleParties.Text != _lastAutoParties) Db.SetSetting("bundle.parties", BundleParties.Text);

        var docs = _docs.ToDictionary(d => d.Id);
        foreach (var ev in _events)
            ev.LinkedDocumentTitle = ev.DocumentId.HasValue && docs.TryGetValue(ev.DocumentId.Value, out var d) ? d.Title : "";

        var options = new BundleOptions
        {
            SummaryMode = summary, Title = BundleTitle.Text.Trim(), CaseNumber = BundleCaseNo.Text.Trim(), Court = BundleCourt.Text.Trim(),
            Parties = BundleParties.Text.Trim(), PreparedBy = BundlePreparedBy.Text.Trim(), HearingDate = BundleHearing.Text.Trim(),
            IntroText = summary ? BundleIntro.Text : "",
            IncludeCover = OptCover.IsChecked == true, IncludeIndex = OptIndex.IsChecked == true,
            IncludeChronology = OptChronology.IsChecked == true, IncludeNotes = OptNotes.IsChecked == true,
            SectionDividers = OptDividers.IsChecked == true, StampPages = OptStamp.IsChecked == true,
            ConfidentialFooter = OptConfidential.IsChecked == true,
            Items = _bundleItems.ToList(), Events = _events.ToList()
        };

        GenerateButton.IsEnabled = false;
        BundleResultPanel.Visibility = Visibility.Collapsed;
        var progress = new Progress<string>(s => BundleStatus.Text = s);
        try
        {
            var result = await new BundleService(Lib).BuildAsync(options, dlg.FileName, progress);
            // Saved inside a protected library, it's encrypted like everything else there
            Lib.ProtectExportInPlace(result.OutputPath);
            Db.Audit("export.bundle", "export", null, $"Created {(summary ? "a summary report" : "a court bundle")}: {options.Title}",
                $"{result.TotalPages} pages, {options.Items.Count} document(s)\n" + Services.Documents.DocumentExporter.DescribeLocation(result.OutputPath));
            _lastBundlePath = result.OutputPath;
            BundleStatus.Text = $"Created {Path.GetFileName(result.OutputPath)} ({result.TotalPages} pages).";
            BundleResultPanel.Visibility = Visibility.Visible;
            if (result.Warnings.Count > 0)
                MessageBox.Show(this, "The PDF was created, but note:\n\n" + string.Join("\n", result.Warnings.Take(15)), "Create PDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            BundleStatus.Text = "Failed: " + ex.Message;
            MessageBox.Show(this, "Couldn't create the PDF:\n\n" + ex.Message + "\n\n(If the file is open in another program, close it and try again.)",
                "Create PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { GenerateButton.IsEnabled = true; }
    }

    private void BundleOpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_lastBundlePath)) OpenWithShell(Lib.ReadableExportPath(_lastBundlePath));
    }

    private async void BundleShareResult_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_lastBundlePath) || !ConfirmConfidentiality()) return;
        try
        {
            await ShareService.ShareFilesAsync(this, Path.GetFileNameWithoutExtension(_lastBundlePath), new[] { Lib.ReadableExportPath(_lastBundlePath) });
            Db.Audit("export.share", "export", null, $"Shared {Path.GetFileName(_lastBundlePath)}");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't open the Windows Share window: " + ex.Message, "Share"); }
    }

    private void BundleShowResult_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_lastBundlePath)) ShowInExplorer(_lastBundlePath);
    }

    // ================================================================== SETTINGS
    /// <summary>Reads the version stamped into the exe at build time, so it can never drift.</summary>
    public static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "0.1.0";

    private void LoadSettings()
    {
        AboutVersionText.Text = $"Version {AppVersion}";
        LibraryPathText.Text = Lib.RootFolder;
        RefreshProtectionStatus();
        RefreshLocationNote();
        OcrEngineText.Text = "Engine: " + OcrService.EngineDescription;
        var last = Db.GetSetting("lastBackup");
        LastBackupText.Text = last == null ? "No backup made yet." : $"Last backup: {last}";
        LastBackupText.SetResourceReference(TextBlock.ForegroundProperty, last == null ? "Warning" : "Success");
        ConfidentialityReminder.IsChecked = Db.GetSetting("remindConfidential", "1") == "1";
        BackupEncrypt.IsChecked = Db.GetSetting("backupEncrypt", "0") == "1";

        var theme = ThemeManager.Current;
        ThemeSystem.IsChecked = theme == AppTheme.System;
        ThemeLight.IsChecked = theme == AppTheme.Light;
        ThemeDark.IsChecked = theme == AppTheme.Dark;
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var theme = ThemeLight.IsChecked == true ? AppTheme.Light
            : ThemeDark.IsChecked == true ? AppTheme.Dark
            : AppTheme.System;
        if (theme == ThemeManager.Current) return;
        ThemeManager.Apply(theme);
        Db.SetSetting("theme", theme.ToString());
        BuildCategoryTree(); // its rows are built in code, so rebuild them against the new palette
    }

    private void ConfidentialityReminder_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Db.SetSetting("remindConfidential", ConfidentialityReminder.IsChecked == true ? "1" : "0");
    }

    private void BackupEncrypt_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Db.SetSetting("backupEncrypt", BackupEncrypt.IsChecked == true ? "1" : "0");
    }

    private async void Backup_Click(object sender, RoutedEventArgs e) => await RunBackupAsync();

    /// <summary>Runs the whole backup flow. Returns true only if a backup file was actually written.</summary>
    private async Task<bool> RunBackupAsync()
    {
        // Maximum security (or the user's own choice) can insist every backup has a password
        if (Db.GetSetting("security.requireBackupPassword", "0") == "1" && BackupEncrypt.IsChecked != true)
        {
            BackupEncrypt.IsChecked = true;
            Db.SetSetting("backupEncrypt", "1");
        }

        // A backup's contents are always written in the clear so it can be restored anywhere.
        // For a protected library that's a real step down unless the zip itself has a password.
        if (Lib.Vault.IsEncrypted && BackupEncrypt.IsChecked != true)
        {
            var answer = MessageBox.Show(this,
                "This library is password protected, but the backup isn't set to use a password.\n\n" +
                "A backup has to hold readable data so it can be restored on any PC, so an unprotected " +
                "backup would contain your documents in the clear — undoing the protection you've set up.\n\n" +
                "Protect the backup with a password?",
                "Backup", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes)
            {
                BackupEncrypt.IsChecked = true;
                Db.SetSetting("backupEncrypt", "1");
            }
        }

        string password = null;
        if (BackupEncrypt.IsChecked == true)
        {
            var ask = new PasswordDialog(true, "Password for this backup",
                "The backup will be encrypted with AES-256. Without this password nobody can open it, including you, " +
                "so write it down somewhere safe and separate from the backup itself.") { Owner = this };
            if (ask.ShowDialog() != true) return false;
            password = ask.Password;
        }

        var dlg = new SaveFileDialog
        {
            FileName = $"CaseKeeper backup {DateTime.Now:yyyy-MM-dd HHmm}.zip", Filter = "Backup (zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return false;
        TaskBanner.Visibility = Visibility.Visible;
        TaskBannerText.Text = password == null ? "Creating backup…" : "Creating encrypted backup…";
        try
        {
            await Task.Run(() => BackupService.CreateBackup(Lib, dlg.FileName, password));
            var label = password == null ? "" : " (password protected)";
            Db.Audit("backup.create", "library", null, $"Created a backup{label}", Services.Documents.DocumentExporter.DescribeLocation(dlg.FileName));
            Db.SetSetting("lastBackup", $"{DateTime.Now:dd MMM yyyy HH:mm}{label} → {dlg.FileName}");
            LoadSettings();
            MessageBox.Show(this, $"Backup created{label}:\n{dlg.FileName}", "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Backup failed: " + ex.Message, "Backup");
            return false;
        }
        finally { if (!_ocrRunning) TaskBanner.Visibility = Visibility.Collapsed; }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrRunning)
        {
            MessageBox.Show(this, "Please wait for text scanning to finish before importing.", "Import");
            return;
        }
        var file = new OpenFileDialog { Filter = "CaseKeeper backup (zip)|*.zip|All files|*.*" };
        if (file.ShowDialog(this) != true) return;

        if (!BackupService.IsValidBackup(file.FileName, out bool encrypted))
        {
            MessageBox.Show(this, "That file doesn't look like a CaseKeeper backup.\n\n" +
                "A backup is a .zip created by CaseKeeper and contains a 'casekeeper.db' file.", "Import");
            return;
        }

        var options = new ImportBackupWindow(Path.GetFileName(file.FileName), encrypted, _docs.Count) { Owner = this };
        if (options.ShowDialog() != true) return;
        var password = string.IsNullOrEmpty(options.Password) ? null : options.Password;

        if (options.ReplaceEverything)
        {
            if (MessageBox.Show(this,
                    "Replace everything in CaseKeeper with this backup?\n\n" +
                    "Your current data is moved to a '_before_restore' folder first, and CaseKeeper restarts.",
                    "Import", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
                BackupService.RestoreBackup(Lib, file.FileName, password);
                SqliteConnection.ClearAllPools();
                AppPackage.StartNewCopy();
                Application.Current.Shutdown();
            }
            catch (BackupPasswordException ex) { PasswordFailed(ex); }
            catch (Exception ex) { MessageBox.Show(this, "Import failed: " + ex.Message, "Import"); }
            return;
        }

        TaskBanner.Visibility = Visibility.Visible;
        TaskBannerText.Text = "Importing backup…";
        try
        {
            var result = await Task.Run(() => BackupService.MergeBackup(Lib, file.FileName, password));
            ReloadAll();
            // Anything that hadn't finished being scanned in the other copy gets picked up here
            EnqueueOcr(_docs.Where(d => d.OcrStatus is OcrStatus.Pending or OcrStatus.Failed));
            MessageBox.Show(this, result.Describe(), "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (BackupPasswordException ex) { PasswordFailed(ex); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Import failed: " + ex.Message + "\n\nNothing has been changed.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { if (!_ocrRunning) TaskBanner.Visibility = Visibility.Collapsed; }
    }

    private void PasswordFailed(BackupPasswordException ex) =>
        MessageBox.Show(this, ex.Message + "\n\nNothing has been changed. Try again with the correct password.",
            "Import", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void RescanFailed_Click(object sender, RoutedEventArgs e)
    {
        var docs = _docs.Where(d => d.OcrStatus is OcrStatus.Failed or OcrStatus.Pending).ToList();
        if (docs.Count == 0) { MessageBox.Show(this, "All documents have been scanned.", "Text recognition"); return; }
        EnqueueOcr(docs);
    }

    private void RescanAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Re-scan the text of every document? This can take a while for large libraries, but you can keep working.",
                "Text recognition", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            EnqueueOcr(_docs);
    }

    private void FactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrRunning)
        {
            MessageBox.Show(this, "Please wait for text scanning to finish first.", "Factory reset");
            return;
        }

        int pages = _docs.Sum(d => d.PageCount);
        int exports = Directory.Exists(Lib.ExportsFolder) ? Directory.GetFiles(Lib.ExportsFolder).Length : 0;
        int customCats = _categories.Count(c => c.IsCustom);
        var lines = new List<string>
        {
            $"•  {_docs.Count} document(s), {pages} scanned page(s), and every note you've written",
            $"•  {_events.Count} timeline event(s)",
            $"•  {_complaints.Count} complaint record(s)",
            $"•  {_parties.Count} part(y/ies)",
        };
        if (customCats > 0) lines.Add($"•  {customCats} category/categories you added yourself");
        if (exports > 0) lines.Add($"•  {exports} PDF(s) saved in the library's Exports folder");
        lines.Add("•  your settings, including theme and bundle cover details");

        var dlg = new FactoryResetWindow(string.Join("\n", lines), Lib.RootFolder, RunBackupAsync) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try
        {
            foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
            Lib.FactoryReset();
            SqliteConnection.ClearAllPools();
            AppPackage.StartNewCopy();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The reset could not be completed: " + ex.Message +
                "\n\nIf a document is open in another program, close it and try again.", "Factory reset",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================================================================== PASSWORD PROTECTION
    private void RefreshProtectionStatus()
    {
        bool on = Lib.Vault.IsEncrypted;
        var since = Lib.Vault.ProtectedSince;
        ProtectionStatusText.Text = on
            ? $"On — this library is encrypted{(since.HasValue ? $", protected since {since:d MMMM yyyy}" : "")}."
            : "Off — documents and the database are stored unencrypted on this PC.";
        ProtectionStatusText.SetResourceReference(TextBlock.ForegroundProperty, on ? "Success" : "Warning");

        EnableProtectionButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ChangePasswordButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DisableProtectionButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Encryption changes how everything on disk is read, so the app reopens afterwards.</summary>
    private void RestartApp()
    {
        foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
        SqliteConnection.ClearAllPools();
        // Before the new copy starts, so our exit can't remove the marker it has just written
        LibraryLock.Release();
        AppPackage.StartNewCopy();
        Application.Current.Shutdown();
    }

    private bool BusyWithScanning(string what)
    {
        if (!_ocrRunning) return false;
        MessageBox.Show(this, $"Please wait for text scanning to finish before {what}.", "CaseKeeper");
        return true;
    }

    private async void EnableProtection_Click(object sender, RoutedEventArgs e)
    {
        if (BusyWithScanning("turning on password protection")) return;

        var dlg = new EnableProtectionWindow(_docs.Count, RunBackupAsync) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        TaskBanner.Visibility = Visibility.Visible;
        TaskBannerText.Text = "Encrypting your library…";
        var progress = new Progress<string>(s => TaskBannerText.Text = s);
        try
        {
            await Task.Run(() => Lib.EnableProtection(dlg.Password, progress));
            MessageBox.Show(this,
                "Your library is now encrypted.\n\nCaseKeeper will restart and ask for the password.\n\n" +
                "Keep that password somewhere safe — it cannot be recovered.",
                "Password protection", MessageBoxButton.OK, MessageBoxImage.Information);
            RestartApp();
        }
        catch (Exception ex)
        {
            TaskBanner.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                "Encryption could not be completed:\n\n" + ex.Message +
                "\n\nCaseKeeper will restart so it can check the state of your library. If anything looks " +
                "wrong, restore your backup.", "Password protection", MessageBoxButton.OK, MessageBoxImage.Error);
            RestartApp();
        }
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ChangePasswordWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Lib.Vault.ChangePassword(dlg.CurrentPassword, dlg.NewPassword);
            MessageBox.Show(this, "Password changed. Use the new one next time you open CaseKeeper.",
                "Password protection", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (WrongPasswordException)
        {
            MessageBox.Show(this, "That is not the current password. Nothing has been changed.",
                "Password protection", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The password could not be changed: " + ex.Message, "Password protection");
        }
    }

    private async void DisableProtection_Click(object sender, RoutedEventArgs e)
    {
        if (BusyWithScanning("turning off password protection")) return;
        if (MessageBox.Show(this,
                "Turn off password protection?\n\nYour documents and database will be written back to this PC " +
                "unencrypted, and CaseKeeper will stop asking for a password. Anyone who can reach the library " +
                "folder would be able to read them.\n\nContinue?",
                "Turn off protection", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        TaskBanner.Visibility = Visibility.Visible;
        TaskBannerText.Text = "Decrypting your library…";
        var progress = new Progress<string>(s => TaskBannerText.Text = s);
        try
        {
            await Task.Run(() => Lib.DisableProtection(progress));
            MessageBox.Show(this, "Password protection is off. CaseKeeper will restart.",
                "Password protection", MessageBoxButton.OK, MessageBoxImage.Information);
            RestartApp();
        }
        catch (Exception ex)
        {
            TaskBanner.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, "Could not finish decrypting:\n\n" + ex.Message +
                "\n\nCaseKeeper will restart to re-check your library.", "Password protection",
                MessageBoxButton.OK, MessageBoxImage.Error);
            RestartApp();
        }
    }

    // ================================================================== LIBRARY LOCATION
    private void RefreshLocationNote()
    {
        if (LibraryService.LocationIsFixedByEnvironment)
        {
            LibraryLocationNote.Text = "This location is set by the CASEKEEPER_LIBRARY environment variable, " +
                                       "so it can't be changed from here.";
            MoveLibraryButton.IsEnabled = ChangeLibraryButton.IsEnabled = false;
        }
        else if (Lib.IsOnNetworkDrive)
        {
            LibraryLocationNote.Text = "On a network drive. The drive needs to be connected whenever you open " +
                                       "CaseKeeper. Other computers can use this library too, but only one at a " +
                                       "time; CaseKeeper warns if it's already open elsewhere.";
            MoveLibraryButton.IsEnabled = ChangeLibraryButton.IsEnabled = true;
        }
        else
        {
            LibraryLocationNote.Text = "";
            MoveLibraryButton.IsEnabled = ChangeLibraryButton.IsEnabled = true;
        }
    }

    /// <summary>Switches to a library somewhere else without touching either library's files.</summary>
    private void ChangeLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (BusyWithScanning("switching library")) return;

        var dlg = new ChangeLibraryWindow(Lib.RootFolder) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try { LibraryService.UseExistingLibraryAt(dlg.TargetPath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the new location:\n\n" + ex.Message, "Use a different library",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, $"CaseKeeper will now restart and open the library in:\n{dlg.TargetPath}\n\n" +
                              $"The previous library hasn't been changed. It's still in:\n{Lib.RootFolder}",
            "Use a different library", MessageBoxButton.OK, MessageBoxImage.Information);
        RestartApp();
    }

    private async void MoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (BusyWithScanning("moving the library")) return;

        var dlg = new MoveLibraryWindow(Lib.RootFolder, Lib.EstimateSizeBytes(), RunBackupAsync) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
        TaskBanner.Visibility = Visibility.Visible;
        TaskBannerText.Text = "Moving the library…";
        var progress = new Progress<string>(s => TaskBannerText.Text = s);
        try
        {
            var outcome = await Task.Run(() => LibraryService.MoveLibrary(Lib, dlg.TargetPath, progress));
            var message = outcome.Describe() + "\n\nCaseKeeper will restart.";
            if (outcome.CleanupWarning != null) message += "\n\n" + outcome.CleanupWarning;
            MessageBox.Show(this, message, "Move library", MessageBoxButton.OK, MessageBoxImage.Information);
            RestartApp();
        }
        catch (Exception ex)
        {
            TaskBanner.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                "The library was not moved:\n\n" + ex.Message +
                "\n\nNothing has been deleted — your library is still where it was.",
                "Move library", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshCategoryList() => CategoryList.ItemsSource = _categories;

    private void EditCategory_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is not Category sel) return;
        var copy = new Category
        {
            Id = sel.Id, Code = sel.Code, Name = sel.Name, GroupName = sel.GroupName, Description = sel.Description,
            BundleSection = sel.BundleSection, IsCustom = sel.IsCustom
        };
        var dlg = new CategoryEditWindow(copy, _categories.Select(c => c.GroupName).Distinct()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.UpdateCategory(copy);
        ReloadAll();
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is not Category sel) return;
        if (sel.Id == UncategorisedId) { MessageBox.Show(this, "The 'Uncategorised' category can't be deleted.", "Categories"); return; }
        if (MessageBox.Show(this, $"Delete the category “{sel.DisplayName}”?" +
                (sel.DocumentCount > 0 ? $"\n\nIts {sel.DocumentCount} document(s) will be moved to 'Uncategorised'." : ""),
                "Delete category", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Db.DeleteCategory(sel.Id, UncategorisedId);
        if (_filterKey == $"cat:{sel.Id}") _filterKey = "all";
        ReloadAll();
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e) => OpenWithShell(Lib.RootFolder);

    /// <summary>The full user guide ships next to the exe; the in-app Guide covers the same ground.</summary>
    private void ShowUserGuide_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "USER-GUIDE.md");
        if (File.Exists(path)) OpenWithShell(path);
        else OpenGuide("start");
    }

    /// <summary>Opens the patch notes that ship next to the exe; falls back to a short summary.</summary>
    private void ShowChangelog_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        if (File.Exists(path)) OpenWithShell(path);
        else
            MessageBox.Show(this,
                $"CaseKeeper v{AppVersion}\n\nThe patch notes file wasn't found next to the program. " +
                "You can read the notes for every release on the project's Releases page.",
                "What's new", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
