using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Services;
using CaseKeeper.UI;
using Microsoft.Win32;

namespace CaseKeeper.Views;

/// <summary>The newer Settings sections, built in code and added to the Settings page.</summary>
public partial class MainWindow
{
    private readonly Dictionary<string, FrameworkElement> _settingsSections = new();
    private ComboBox _aiModel, _promptPicker;
    private TextBox _aiEndpoint, _promptText;
    private TextBlock _aiStatus, _perfText, _snapshotInfo;
    private ListBox _snapshotList, _recentErrors;
    private bool _settingsBuilt;

    private void BuildSettingsExtras()
    {
        if (_settingsBuilt) return;
        _settingsBuilt = true;
        var stack = SettingsStack;

        // Name the cards that are already in the XAML, so "go to settings → backup" can find them
        foreach (var border in stack.Children.OfType<Border>())
            if (border.Child is StackPanel { Children.Count: > 0 } sp && sp.Children[0] is TextBlock h)
            {
                var key = h.Text switch
                {
                    "Appearance" => "appearance", "Password protection" => "password", "Your library" => "library",
                    "Backup & transfer" => "backup", "Text recognition (OCR)" => "ocr", "Categories" => "categories",
                    "Sharing & confidentiality" => "sharing", "About" => "about", "Factory reset" => "reset", _ => null
                };
                if (key != null) _settingsSections[key] = border;
            }

        int insertAt = 1;
        void Insert(UIElement e) => stack.Children.Insert(insertAt++, e);

        // ---- jump bar
        var jump = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var (label, key) in new[]
                 {
                     ("Layout", "layout"), ("Security", "security"), ("Password", "password"), ("Library", "library"), ("Backup", "backup"),
                     ("Recovery", "recovery"), ("AI", "ai"), ("Performance", "performance"), ("Logs", "logs"), ("Updates", "updates"), ("Transfer", "transfer"),
                     ("Categories", "categories"), ("About", "about")
                 })
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            b.Click += (_, _) => ScrollSettingsTo(key);
            jump.Children.Add(b);
        }
        Insert(jump);
        Insert(Ui.Muted("Press F1 for help with any setting. Every change to a setting that matters for security is recorded in the audit trail.", 12));

        Insert(Section("layout", "Layout & experience", LayoutCard()));
        Insert(Section("security", "Security", SecurityCard()));

        // The rest go after the existing Backup card
        insertAt = stack.Children.IndexOf(_settingsSections.GetValueOrDefault("backup")) + 1;
        if (insertAt <= 0) insertAt = stack.Children.Count;
        Insert(Section("recovery", "Safety & recovery", RecoveryCard()));
        Insert(Section("transfer", "Settings, templates and other cases", TransferCard()));
        Insert(Section("ai", "AI (Ollama)", AiCard()));
        Insert(Section("performance", "Performance", PerformanceCard()));
        Insert(Section("logs", "Logs & diagnostics", LogsCard()));
        Insert(Section("updates", "Updates", UpdatesCard()));

        RefreshSnapshots();
        RefreshRecentErrors();
        AppLog.Written += _ => Dispatcher.BeginInvoke(RefreshRecentErrors);
    }

    private Border Section(string key, string title, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(title, "H2"));
        sp.Children.Add(body);
        var card = Ui.Card(sp, new Thickness(0, 0, 0, 14));
        _settingsSections[key] = card;
        return card;
    }

    private void ScrollSettingsTo(string key)
    {
        if (!_settingsSections.TryGetValue(key, out var el)) return;
        el.BringIntoView(new Rect(0, 0, 10, SettingsScroller.ViewportHeight * 0.8));
        // Briefly highlight it so it's obvious where to look
        if (el is Border b)
        {
            b.SetResourceReference(Border.BorderBrushProperty, "Primary");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => { t.Stop(); b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); };
            t.Start();
        }
    }

    private static StackPanel Labelled(string label, UIElement control, string hint = null)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        s.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Style("Label") });
        s.Children.Add(control);
        if (hint != null) s.Children.Add(Ui.Muted(hint, 11.5));
        return s;
    }

    private CheckBox SettingCheck(string content, string key, string fallback, Action<bool> after = null, string auditLabel = null)
    {
        var cb = new CheckBox { Content = content, IsChecked = Db.GetSetting(key, fallback) == "1", Margin = new Thickness(0, 8, 0, 0) };
        cb.Checked += (_, _) => Changed(true);
        cb.Unchecked += (_, _) => Changed(false);
        void Changed(bool on)
        {
            Db.SetSetting(key, on ? "1" : "0");
            if (auditLabel != null) Db.Audit("settings.change", "settings", null, $"{auditLabel}: {(on ? "on" : "off")}");
            App.ApplyStoredPreferences();
            after?.Invoke(on);
        }
        return cb;
    }

    private ComboBox SettingCombo(string key, string fallback, (string Value, string Label)[] options, Action after = null, string auditLabel = null)
    {
        var combo = new ComboBox { ItemsSource = options.Select(o => o.Label).ToList(), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var current = Db.GetSetting(key, fallback);
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value == current));
        combo.SelectionChanged += (_, _) =>
        {
            var v = options[Math.Max(0, combo.SelectedIndex)].Value;
            Db.SetSetting(key, v);
            if (auditLabel != null) Db.Audit("settings.change", "settings", null, $"{auditLabel}: {options[combo.SelectedIndex].Label}");
            App.ApplyStoredPreferences();
            after?.Invoke();
        };
        return combo;
    }

    // ------------------------------------------------------------------ layout
    private UIElement LayoutCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Muted("Simple shows the essentials in plain English. Professional adds costs, the Scott schedule, AI analysis, exports, the audit trail and more. Nothing is lost when you switch.", 12.5));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var simple = new RadioButton { GroupName = "uimode", Content = "Simple", IsChecked = !ProfessionalMode, Margin = new Thickness(0, 0, 24, 0) };
        var pro = new RadioButton { GroupName = "uimode", Content = "Professional", IsChecked = ProfessionalMode };
        simple.Checked += (_, _) => { if (ProfessionalMode) SetMode(false); };
        pro.Checked += (_, _) => { if (!ProfessionalMode) SetMode(true); };
        row.Children.Add(simple);
        row.Children.Add(pro);
        sp.Children.Add(row);

        var station = new TextBox { Text = Db.GetSetting("station.label", ""), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        station.LostFocus += (_, _) =>
        {
            Db.SetSetting("station.label", station.Text.Trim());
            App.ApplyStoredPreferences();
        };
        sp.Children.Add(Labelled("Name for this PC in the audit trail", station,
            $"Optional, e.g. 'Home laptop'. Left blank it shows as {Data.Database.DefaultStation()} – a code, not your computer's real name."));
        var checklist = new CheckBox
        {
            Content = "Show the getting-started checklist on the dashboard",
            IsChecked = Db.GetSetting("ui.hideGettingStarted", "0") != "1",
            Margin = new Thickness(0, 10, 0, 0)
        };
        checklist.Checked += (_, _) => { Db.SetSetting("ui.hideGettingStarted", "0"); RefreshDashboardExtras(); };
        checklist.Unchecked += (_, _) => { Db.SetSetting("ui.hideGettingStarted", "1"); RefreshDashboardExtras(); };
        sp.Children.Add(checklist);
        return sp;
    }

    // ------------------------------------------------------------------ security
    private UIElement SecurityCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Muted("Lock CaseKeeper when you step away, and choose how strict to be. The strongest protection is password protection (below), which encrypts everything on disk.", 12.5));

        var max = Ui.Button("Turn on maximum security", "", (_, _) => MaximumSecurity(), primary: true,
            tooltip: "Auto-lock after 5 minutes, lock with Windows, password-protected backups, local-only AI, share reminders, short log retention");
        max.Margin = new Thickness(0, 10, 0, 4);
        max.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(max);
        sp.Children.Add(Ui.Muted("One click: lock after 5 minutes idle and whenever Windows locks, require a password on every backup, keep the AI on this computer or your own network only, " +
                                 "remind you before anything is shared, and keep logs for 14 days. Turn on password protection too for full encryption.", 12));

        sp.Children.Add(Labelled("Lock after no activity for", SettingCombo("security.idleLockMinutes", "15",
            new[] { ("0", "Never"), ("5", "5 minutes"), ("10", "10 minutes"), ("15", "15 minutes"), ("30", "30 minutes"), ("60", "1 hour") },
            auditLabel: "Auto-lock"), "Only applies when the library is password protected. Ctrl+L locks straight away."));
        sp.Children.Add(SettingCheck("Lock when Windows is locked (Windows key + L)", "security.lockWithWindows", "1", auditLabel: "Lock with Windows"));
        sp.Children.Add(SettingCheck("Always require a password on backups", "security.requireBackupPassword", "0", auditLabel: "Require backup password"));
        var lockNow = Ui.Button("Lock now", "", (_, _) =>
        {
            if (SessionGuard.CanLock) SessionGuard.Lock("locked from Settings");
            else MessageBox.Show(this, "Locking needs password protection to be turned on first.", "Lock");
        });
        lockNow.Margin = new Thickness(0, 12, 0, 0);
        lockNow.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(lockNow);
        return sp;
    }

    private void MaximumSecurity()
    {
        Db.SetSetting("security.idleLockMinutes", "5");
        Db.SetSetting("security.lockWithWindows", "1");
        Db.SetSetting("security.requireBackupPassword", "1");
        Db.SetSetting("backupEncrypt", "1");
        Db.SetSetting("remindConfidential", "1");
        Db.SetSetting("ai.allowRemote", "0");
        Db.SetSetting("log.verbose", "0");
        Db.SetSetting("log.retentionDays", "14");
        App.ApplyStoredPreferences();
        Db.Audit("settings.security", "settings", null, "Turned on maximum security",
            "Auto-lock 5 min, lock with Windows, backup passwords required, AI local only, share reminders, 14-day logs");
        LoadSettings();
        var msg = "Maximum security is on.";
        if (!Lib.Vault.IsEncrypted)
        {
            msg += "\n\nYour library isn't password protected yet, so documents are stored unencrypted. Turn on password protection now?";
            if (MessageBox.Show(this, msg, "Security", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                EnableProtection_Click(this, new RoutedEventArgs());
        }
        else MessageBox.Show(this, msg + " Your library is also encrypted.", "Security", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------ recovery
    private UIElement RecoveryCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Muted("CaseKeeper saves a restore point of its database once a day (encrypted if your library is). Documents never change after import, " +
                                 "so a snapshot plus your files can put everything back as it was on that day. Backups are still essential – snapshots live inside the library.", 12.5));
        sp.Children.Add(Labelled("Keep daily snapshots for", SettingCombo("snapshots.keep", "7",
            new[] { ("0", "Off"), ("3", "3 days"), ("7", "7 days"), ("14", "14 days"), ("30", "30 days") }, auditLabel: "Snapshots kept")));
        _snapshotList = new ListBox { Height = 120, Margin = new Thickness(0, 10, 0, 6) };
        sp.Children.Add(_snapshotList);
        _snapshotInfo = Ui.Muted("", 12);
        sp.Children.Add(_snapshotInfo);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Ui.Button("Take a snapshot now", "", (_, _) =>
        {
            try { Lib.TakeSnapshot("manual"); Db.Audit("snapshot.create", "library", null, "Took a snapshot"); }
            catch (Exception ex) { MessageBox.Show(this, "Snapshot failed: " + ex.Message, "Snapshots"); }
            RefreshSnapshots();
        }));
        row.Children.Add(Ui.Button("Restore selected…", "", (_, _) => RestoreSnapshot()));
        row.Children.Add(Ui.Button("Check document integrity", "", async (_, _) => await VerifyIntegrityAsync(),
            tooltip: "Confirms every document is byte-for-byte what you imported"));
        row.Children.Add(Ui.Button("Check database health", "", (_, _) =>
        {
            var r = Db.QuickCheck();
            Db.Audit("health.check", "library", null, "Checked database health", r);
            MessageBox.Show(this, r == "ok" ? "The database is healthy." : "The health check reported:\n\n" + r, "Database health",
                MessageBoxButton.OK, r == "ok" ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }));
        sp.Children.Add(row);
        sp.Children.Add(Labelled("Keep deleted documents for", SettingCombo("bin.days", "30",
            new[] { ("7", "7 days"), ("30", "30 days"), ("90", "90 days"), ("365", "1 year"), ("0", "Until I empty it") }, auditLabel: "Deleted items kept"),
            "After this, documents in Deleted items are removed for good the next time CaseKeeper opens."));
        return sp;
    }

    private void RefreshSnapshots()
    {
        if (_snapshotList == null) return;
        var snaps = Lib.ListSnapshots();
        _snapshotList.ItemsSource = snaps.Select(f => $"{f.LastWriteTime:ddd d MMM yyyy  HH:mm}   ·   {f.Length / 1024} KB").ToList();
        _snapshotList.Tag = snaps;
        _snapshotInfo.Text = snaps.Count == 0 ? "No snapshots yet." : $"{snaps.Count} snapshot(s), newest first.";
    }

    private void RestoreSnapshot()
    {
        if (_snapshotList.Tag is not List<FileInfo> snaps || _snapshotList.SelectedIndex < 0) { MessageBox.Show(this, "Choose a snapshot first.", "Snapshots"); return; }
        var snap = snaps[_snapshotList.SelectedIndex];
        if (MessageBox.Show(this, $"Put the database back as it was on {snap.LastWriteTime:d MMMM yyyy HH:mm}?\n\n" +
                "Anything added or changed since then will be undone (a snapshot of the current state is taken first, so you can come back). CaseKeeper will restart.",
                "Restore snapshot", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (BusyWithScanning("restoring a snapshot")) return;
        try
        {
            Lib.RestoreSnapshot(snap.FullName);
            RestartApp();
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("Snapshot", "Snapshot restore failed", ex);
            MessageBox.Show(this, $"The snapshot couldn't be restored: {ex.Message}\nReference: {id}", "Snapshots");
        }
    }

    // ------------------------------------------------------------------ transfer & libraries
    private UIElement TransferCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Muted("A full backup (above) contains everything, including all of these settings. To copy just your set-up – preferences, AI prompts, " +
                                 "letterheads and templates – to another PC or another case, use a settings file. It never contains case details or documents.", 12.5));
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(Ui.Button("Export settings & templates…", "", (_, _) => ExportProfile()));
        row.Children.Add(Ui.Button("Import settings & templates…", "", (_, _) => ImportProfile()));
        sp.Children.Add(row);

        sp.Children.Add(new TextBlock { Text = "OTHER CASES ON THIS PC", Style = Ui.Style("Label"), Margin = new Thickness(0, 16, 0, 4) });
        sp.Children.Add(Ui.Muted("Keep each case in its own library (e.g. one per client or per child). Switch between them here or with Ctrl+K.", 12));
        var libs = LibraryService.RecentLibraries();
        foreach (var lib in libs)
        {
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            bool current = string.Equals(lib, Lib.RootFolder, StringComparison.OrdinalIgnoreCase);
            if (!current)
            {
                var forget = Ui.Button(null, "", (_, _) => { LibraryService.ForgetLibrary(lib); MessageBox.Show(this, "Removed from the list. The library itself is untouched.", "Libraries"); },
                    tooltip: "Remove from this list (doesn't delete anything)");
                DockPanel.SetDock(forget, Dock.Right);
                line.Children.Add(forget);
                var go = Ui.Button("Switch", "", (_, _) => SwitchLibrary(lib));
                DockPanel.SetDock(go, Dock.Right);
                line.Children.Add(go);
            }
            var t = new TextBlock { Text = lib + (current ? "   (open now)" : ""), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            line.Children.Add(t);
            sp.Children.Add(line);
        }
        var newLib = Ui.Button("Start or open another case library…", "", (_, _) => ChangeLibrary_Click(this, new RoutedEventArgs()));
        newLib.Margin = new Thickness(0, 10, 0, 0);
        newLib.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(newLib);
        return sp;
    }

    private void ExportProfile()
    {
        var dlg = new SaveFileDialog { FileName = $"CaseKeeper settings {DateTime.Today:yyyy-MM-dd}.ckprofile", Filter = "CaseKeeper settings|*.ckprofile" };
        if (dlg.ShowDialog(this) != true) return;
        string password = null;
        if (MessageBox.Show(this, "Protect the settings file with a password?\n\nLetterheads can contain your name and address.", "Export settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            var pw = new PasswordDialog(true, "Password for the settings file", "You'll need it to import the file.") { Owner = this };
            if (pw.ShowDialog() != true) return;
            password = pw.Password;
        }
        try
        {
            SettingsTransfer.Export(Db, dlg.FileName, password);
            MessageBox.Show(this, "Settings, letterheads and templates saved.", "Export settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't save: " + ex.Message, "Export settings"); }
    }

    private void ImportProfile()
    {
        var dlg = new OpenFileDialog { Filter = "CaseKeeper settings|*.ckprofile|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        string password = null;
        try
        {
            if (SettingsTransfer.IsEncrypted(dlg.FileName))
            {
                var pw = new PasswordDialog(false, "Settings file password", "This settings file is password protected.") { Owner = this };
                if (pw.ShowDialog() != true) return;
                password = pw.Password;
            }
            var summary = SettingsTransfer.Import(Db, dlg.FileName, password);
            App.ApplyStoredPreferences();
            ApplyMode();
            LoadSettings();
            MessageBox.Show(this, summary, "Import settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ------------------------------------------------------------------ AI
    private UIElement AiCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Muted("CaseKeeper can use Ollama – a free program that runs AI models on your own computer – to summarise documents, pull out chronologies, " +
                                 "spot inconsistencies and explain the guide in plain English. Nothing is sent anywhere unless you turn this on, and then only to the server below.", 12.5));
        var help = Ui.Button("How to set up Ollama", "", (_, _) => OpenGuide("app.ai"));
        help.HorizontalAlignment = HorizontalAlignment.Left;
        help.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(help);

        sp.Children.Add(SettingCheck("Use AI features", "ai.enabled", "0", _ => { ApplyMode(); _aiView?.Reload(); }, auditLabel: "AI features"));

        _aiEndpoint = new TextBox { Text = AiService.Endpoint, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        _aiEndpoint.LostFocus += (_, _) =>
        {
            var v = _aiEndpoint.Text.Trim().TrimEnd('/');
            var problem = AiService.CheckEndpoint(v, AiService.AllowRemote);
            _aiStatus.Text = problem ?? "";
            if (v == AiService.Endpoint) return;
            Db.SetSetting("ai.endpoint", v);
            Db.Audit("settings.change", "settings", null, "AI server address changed", new Uri(v.Contains("://") ? v : "http://" + v).Host);
        };
        sp.Children.Add(Labelled("Ollama address", _aiEndpoint, "Usually http://localhost:11434. A server elsewhere on your network works too (e.g. a PC with a big graphics card)."));
        sp.Children.Add(SettingCheck("Allow a server outside my home network (must use https://)", "ai.allowRemote", "0", auditLabel: "Remote AI server allowed"));

        var keyBox = new PasswordBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        keyBox.LostFocus += (_, _) =>
        {
            if (keyBox.Password.Length == 0) return;
            AiService.ApiKey = keyBox.Password;
            keyBox.Clear();
            _aiStatus.Text = "API key saved (encrypted for your Windows account).";
            Db.Audit("settings.change", "settings", null, "AI API key set");
        };
        var clearKey = Ui.Button("Clear key", null, (_, _) => { AiService.ApiKey = ""; _aiStatus.Text = "API key removed."; });
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(keyBox);
        clearKey.Margin = new Thickness(8, 0, 0, 0);
        keyRow.Children.Add(clearKey);
        sp.Children.Add(Labelled("API key (only if your server needs one)", keyRow, "Stored encrypted with Windows (DPAPI) for your account only. Not included in settings files."));

        var testRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        testRow.Children.Add(Ui.Button("Test connection", "", async (_, _) => await TestAiAsync(), primary: true));
        _aiModel = new ComboBox { Width = 300, IsEditable = true, Text = AiService.Model, Margin = new Thickness(0, 0, 6, 6) };
        _aiModel.LostFocus += (_, _) => SaveModel();
        _aiModel.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(SaveModel);
        testRow.Children.Add(_aiModel);
        sp.Children.Add(testRow);
        _aiStatus = Ui.Muted("", 12.5);
        _aiStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        sp.Children.Add(_aiStatus);

        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        left.Children.Add(Labelled("Graphics card (GPU)", SettingCombo("ai.gpu", "auto",
            new[] { ("auto", "Let Ollama decide (recommended)"), ("gpu", "Use the GPU as much as possible"), ("cpu", "CPU only (no GPU)") }),
            "GPU makes answers much faster. Choose CPU only on laptops without a suitable graphics card, or to save battery."));
        left.Children.Add(Labelled("How much it reads at once (context)", SettingCombo("ai.numCtx", "8192",
            new[] { ("4096", "4,000 tokens – small / fast"), ("8192", "8,000 tokens – balanced"), ("16384", "16,000 tokens"), ("32768", "32,000 tokens"),
                    ("65536", "64,000 tokens – needs lots of memory"), ("131072", "128,000 tokens – large models only") }),
            "Bigger reads more in one go but uses more memory. Larger document sets are split automatically."));
        left.Children.Add(Labelled("Longest answer", SettingCombo("ai.numPredict", "2048",
            new[] { ("1024", "Short (≈750 words)"), ("2048", "Medium (≈1,500 words)"), ("4096", "Long (≈3,000 words)"), ("8192", "Very long") })));
        right.Children.Add(Labelled("Creativity (temperature)", SettingCombo("ai.temperature", "0.2",
            new[] { ("0", "0 – most factual, repeatable"), ("0.2", "0.2 – factual (recommended)"), ("0.5", "0.5 – balanced"), ("0.8", "0.8 – more varied wording") })));
        right.Children.Add(Labelled("CPU threads", SettingCombo("ai.threads", "0",
            new[] { ("0", "Automatic (from the performance setting)"), ("2", "2"), ("4", "4"), ("6", "6"), ("8", "8"), ("12", "12"), ("16", "16") })));
        right.Children.Add(Labelled("Keep the model loaded", SettingCombo("ai.keepAlive", "10m",
            new[] { ("0", "Unload straight away (frees memory)"), ("5m", "5 minutes"), ("10m", "10 minutes"), ("30m", "30 minutes"), ("-1", "Until Ollama closes") })));
        right.Children.Add(Labelled("Give up after", SettingCombo("ai.timeoutMinutes", "20",
            new[] { ("5", "5 minutes"), ("20", "20 minutes"), ("60", "1 hour"), ("180", "3 hours") })));
        sp.Children.Add(grid);
        // What Bulk Case Import recommends for this hardware (models are checked when Import review → Options opens)
        var thisPc = Ui.Muted(AiHardware.Recommend(null).Summary +
                              $". Bulk import uses its own fast model ({AiHardware.DefaultModel} by default); change its speed in Import review → Review options….", 12.5);
        thisPc.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(thisPc);
        sp.Children.Add(SettingCheck("Give the AI the case summary and the list of parties as background", "ai.includeCaseSummary", "1"));

        // ---- prompts
        sp.Children.Add(new TextBlock { Text = "PROMPTS – WHAT THE AI IS TOLD TO DO", Style = Ui.Style("Label"), Margin = new Thickness(0, 18, 0, 4) });
        sp.Children.Add(Ui.Muted("Edit the instructions the AI follows. The system prompt applies to everything; each report type has its own. {task} is replaced with the report's instructions.", 12));
        var promptKeys = new List<(string Key, string Label)>
        {
            ("prompt.system", "System prompt (applies to everything)"), ("prompt.helpdesk", "Help assistant (how to use CaseKeeper)"),
            ("prompt.map", "Granular: reading each part"),
            ("prompt.reduce", "Granular: combining the parts"), ("prompt.rewrite", "Improve wording (letters)"),
            ("prompt.explain", "Explain simply (guide)"), ("prompt.guide.answer", "Answer questions from the guide"),
            ("prompt.guide.apply", "How a guide article applies to me"),
            ("prompt.bulk.analyse", "Bulk Case Import: reading each document (JSON)")
        };
        promptKeys.AddRange(AiService.ReportTypes.Select(r => ("prompt.report." + r.Key, "Report: " + r.Name)));
        _promptPicker = new ComboBox { ItemsSource = promptKeys.Select(p => p.Label).ToList(), SelectedIndex = 0, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        _promptText = new TextBox { Style = Ui.Style("MultilineBox"), Height = 150, Margin = new Thickness(0, 6, 0, 0), FontSize = 12.5 };
        _promptPicker.SelectionChanged += (_, _) => _promptText.Text = AiService.Prompt(promptKeys[_promptPicker.SelectedIndex].Key);
        _promptText.Text = AiService.Prompt(promptKeys[0].Key);
        sp.Children.Add(_promptPicker);
        sp.Children.Add(_promptText);
        var pRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        pRow.Children.Add(Ui.Button("Save prompt", "", (_, _) =>
        {
            var key = promptKeys[_promptPicker.SelectedIndex].Key;
            Db.SetSetting(key, _promptText.Text);
            Db.Audit("settings.prompt", "settings", null, $"Edited AI prompt: {promptKeys[_promptPicker.SelectedIndex].Label}");
            _aiStatus.Text = "Prompt saved.";
        }, primary: true));
        pRow.Children.Add(Ui.Button("Reset this prompt", "", (_, _) =>
        {
            var key = promptKeys[_promptPicker.SelectedIndex].Key;
            Db.SetSetting(key, "");
            _promptText.Text = AiService.Prompt(key);
        }));
        pRow.Children.Add(Ui.Button("Reset all prompts", null, (_, _) =>
        {
            if (MessageBox.Show(this, "Put every AI prompt back to the original?", "Prompts", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var (k, _) in promptKeys) Db.SetSetting(k, "");
            _promptText.Text = AiService.Prompt(promptKeys[_promptPicker.SelectedIndex].Key);
            Db.Audit("settings.prompt", "settings", null, "Reset all AI prompts");
        }));
        sp.Children.Add(pRow);
        return sp;
    }

    private void SaveModel()
    {
        var m = _aiModel.Text?.Trim() ?? "";
        if (m == AiService.Model) return;
        Db.SetSetting("ai.model", m);
        Db.Audit("settings.change", "settings", null, "AI model chosen", m);
        _aiView?.Reload();
    }

    private async Task TestAiAsync()
    {
        _aiStatus.Text = "Connecting…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var endpoint = _aiEndpoint.Text.Trim().TrimEnd('/');
            Db.SetSetting("ai.endpoint", endpoint);
            var info = await AiService.ProbeAsync(endpoint, AiService.AllowRemote);
            _aiModel.ItemsSource = info.Models;
            // No model chosen yet: CaseKeeper's recommended one if it's installed (a model someone already chose is kept)
            if (string.IsNullOrWhiteSpace(_aiModel.Text) && info.Models.Count > 0)
                _aiModel.Text = info.Models.FirstOrDefault(m => AiHardware.Same(m, AiHardware.DefaultModel)) ?? info.Models[0];
            SaveModel();
            _aiStatus.Text = info.Models.Count == 0
                ? $"Connected to Ollama {info.Version}, but no models are installed. Install CaseKeeper's recommended model with 'ollama pull gemma3:4b' (about 3 GB), or the Download button in Import review → Review options…, then test again."
                : $"✓ Connected to Ollama {info.Version}. {info.Models.Count} model(s) available – choose one.";
            AppLog.Info("AI", $"Connection test OK ({info.Models.Count} models)");
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "Connection test failed", ex);
            _aiStatus.Text = "Couldn't connect: " + ex.Message + "\nIs Ollama installed and running? Check the address.";
        }
        finally { Mouse.OverrideCursor = null; }
    }

    // ------------------------------------------------------------------ performance
    private UIElement PerformanceCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Muted("How hard CaseKeeper works your computer when scanning documents and running AI. Automatic uses everything a desktop can give, " +
                                 "and drops to a gentle mode when a laptop is running on battery.", 12.5));
        _perfText = Ui.Muted("", 12.5);
        sp.Children.Add(Labelled("Mode", SettingCombo("performance.mode", "Auto",
            new[] { ("Auto", "Automatic (recommended)"), ("PowerSaver", "Power saver – laptops, quiet"), ("Balanced", "Balanced"), ("Maximum", "Maximum – use every core") },
            () => _perfText.Text = Performance.Describe())));
        _perfText.Margin = new Thickness(0, 8, 0, 0);
        _perfText.Text = Performance.Describe();
        sp.Children.Add(_perfText);
        sp.Children.Add(Ui.Muted(AiHardware.Recommend(null).Summary + " (for Bulk Case Import). Change it in Import review → Review options…, where Use recommended settings puts it back.", 12));
        sp.Children.Add(Ui.Muted("Converting Word layouts to PDF: " + Services.Documents.OfficeConverter.Describe(), 12));
        return sp;
    }

    // ------------------------------------------------------------------ logs
    private UIElement LogsCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Muted("A technical log of what CaseKeeper does and any errors, for troubleshooting. It never contains your documents, notes or case details, " +
                                 "and your Windows user name is removed. Errors on screen show a reference (like E-7F3A2C) that matches an entry here.", 12.5));
        sp.Children.Add(SettingCheck("Detailed logging (for troubleshooting)", "log.verbose", "0"));
        sp.Children.Add(Labelled("Keep logs for", SettingCombo("log.retentionDays", "30",
            new[] { ("7", "7 days"), ("14", "14 days"), ("30", "30 days"), ("90", "90 days") })));
        sp.Children.Add(new TextBlock { Text = "RECENT WARNINGS AND ERRORS", Style = Ui.Style("Label"), Margin = new Thickness(0, 12, 0, 4) });
        _recentErrors = new ListBox { Height = 140 };
        _recentErrors.SetResourceReference(FontFamilyProperty, "MonoFont");
        _recentErrors.FontSize = 11.5;
        sp.Children.Add(_recentErrors);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Ui.Button("Open log folder", "", (_, _) => { Directory.CreateDirectory(AppLog.Folder); OpenWithShell(AppLog.Folder); }));
        row.Children.Add(Ui.Button("Copy diagnostic summary", "", (_, _) =>
        {
            Ui.CopyToClipboard(DiagnosticSummary());
            MessageBox.Show(this, "A summary of your set-up (no personal or case information) has been copied, ready to paste into a support request.", "Diagnostics");
        }));
        sp.Children.Add(row);
        return sp;
    }

    private void RefreshRecentErrors()
    {
        if (_recentErrors == null) return;
        var items = AppLog.RecentEntries.Where(e => e.Level >= LogLevel.Warning).Reverse().Take(60)
            .Select(e => $"{e.TimeText}  {e.LevelText,-7} {e.ErrorId,-9} {e.Area}: {e.Message}").ToList();
        _recentErrors.ItemsSource = items.Count == 0 ? new List<string> { "No warnings or errors this session." } : items;
    }

    private string DiagnosticSummary() => string.Join("\n", new[]
    {
        $"CaseKeeper {AppVersion}",
        $"Windows {Environment.OSVersion.Version} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}",
        Performance.Describe().Replace("\n", " · "),
        $"Library: {(Lib.Vault.IsEncrypted ? "password protected" : "not protected")}, {(Lib.IsOnNetworkDrive ? "network drive" : "local drive")}",
        $"Documents: {_docs.Count} · deleted items: {_deletedDocs.Count} · audit entries: {Db.AuditCount()}",
        $"OCR: {OcrService.EngineDescription}",
        $"AI: {(AiService.Enabled ? $"on, model {AiService.Model}" : "off")}",
        $"Mode: {(ProfessionalMode ? "Professional" : "Simple")}",
        "Recent errors:",
        string.Join("\n", AppLog.RecentEntries.Where(e => e.Level >= LogLevel.Error).TakeLast(10).Select(e => $"  {e.ErrorId} {e.Area}: {e.Message}"))
    });
}
