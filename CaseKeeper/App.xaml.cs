using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CaseKeeper.Services;
using CaseKeeper.UI;
using Microsoft.Win32;

namespace CaseKeeper;

public partial class App : Application
{
    public static LibraryService Library { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch everything, so a fault is logged with a reference instead of the app vanishing
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Fatal("Crash", "Unhandled error outside the UI", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Background", "A background task failed", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        // The website installer's uninstaller runs  CaseKeeper.exe --remove-data  when someone asks
        // for their library to be deleted too. Nothing is opened: it only confirms and deletes.
        if (e.Args.Any(a => a.Equals("--remove-data", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(AppTheme.Dark);
            Shutdown(RemoveDataForUninstaller());
            return;
        }

        AppLog.Info("App", $"CaseKeeper {Views.MainWindow.AppVersion} starting on Windows {Environment.OSVersion.Version}, " +
                           $"{Environment.ProcessorCount} cores");
        AppLog.Prune();

        // Any prompt shown before the main window would otherwise count as the main window,
        // and closing it would end the app. Switched back once the real main window is up.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Swaps the stock SQLite engine for SQLCipher. Must happen before any database is opened.
        SQLitePCL.Batteries_V2.Init();

        // Decrypted copies left behind by a previous session (e.g. after a crash) go first
        LibraryService.PurgeTempCopies();

        // The saved theme lives in the database, which may still be locked, so start in the
        // brand's dark theme and apply the user's own choice once we're in.
        ThemeManager.Apply(AppTheme.Dark);

        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OpenEditableComboOnClick));

        var root = LibraryService.ResolveRoot();
        if (!OfferWebsiteLibrary(root)) { Shutdown(); return; }
        Vault vault;
        string problem;
        while (!TryReachLibrary(root, out vault, out problem))
        {
            AppLog.Warn("Library", "Library folder not reachable: " + problem);
            if (LibraryService.LocationIsFixedByEnvironment)
            {
                MessageBox.Show("CaseKeeper could not reach its library folder:\n\n" + problem +
                    "\n\nThe location is set by the CASEKEEPER_LIBRARY environment variable.",
                    "CaseKeeper", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            var next = Views.LibraryUnavailableWindow.Ask(root, problem);
            if (next == null) { Shutdown(1); return; }
            root = next;
        }

        // Two PCs writing to one library over a network share can corrupt it
        if (LibraryLock.FindOtherHolder(root) is { } holder &&
            MessageBox.Show("This library is already open on another computer:\n\n" + LibraryLock.Describe(holder) +
                    "\n\nOpening it on two computers at the same time can damage it. Close CaseKeeper on the other " +
                    "computer first.\n\nOnly choose Yes if you're sure it isn't really open there " +
                    "(for example, that PC crashed or lost its network connection).\n\nOpen it anyway?",
                    "Library in use", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Shutdown();
            return;
        }

        int failedUnlocks = 0;
        if (vault.IsEncrypted)
        {
            var unlock = new Views.UnlockWindow(vault);
            var unlocked = unlock.ShowDialog().GetValueOrDefault();
            failedUnlocks = unlock.FailedAttempts;
            if (!unlocked)
            {
                Shutdown();     // cancelled at the password prompt
                return;
            }
        }

        try
        {
            Library = new LibraryService(root, vault);
        }
        catch (Exception ex)
        {
            var id = AppLog.Fatal("Library", "Could not open the library database", ex);
            MessageBox.Show("CaseKeeper could not open its document library:\n\n" + ex.Message +
                            $"\n\nReference: {id}. Details are in the log folder:\n{AppLog.Folder}", "CaseKeeper",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        LibraryLock.Acquire(root);
        LibraryService.RememberLibrary(root);
        Exit += (_, _) => LibraryLock.Release();
        Exit += (_, _) =>
        {
            LibraryService.PurgeTempCopies();
            AppLog.Info("App", "CaseKeeper closed");
        };

        ApplyStoredPreferences();
        StartupHousekeeping(failedUnlocks);

        ThemeManager.Apply(ThemeManager.Parse(Library.Db.GetSetting("theme", "Dark")));
        // Follow along if Windows is switched between light and dark while we're running
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        var main = new Views.MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        SessionGuard.Start();
    }

    /// <summary>
    /// The uninstaller's "delete my library too". Exit code 0 = deleted, 1 = cancelled or nothing
    /// to delete, 2 = some of it couldn't be deleted. The uninstaller reports which.
    /// </summary>
    private static int RemoveDataForUninstaller()
    {
        const string title = "Remove all CaseKeeper data";
        if (DataRemoval.OtherCopyRunning())
        {
            MessageBox.Show("CaseKeeper is still open. Nothing has been deleted.\n\nClose CaseKeeper, then use Settings → Factory reset → " +
                            "Remove all CaseKeeper data, or delete the library folder yourself.", title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        var plan = DataRemoval.Build();
        if (plan.IsEmpty)
        {
            MessageBox.Show("CaseKeeper has no data on this PC to delete.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
        if (new Views.RemoveAllDataWindow(plan) { WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true }
                .ShowDialog() != true)
            return 1;
        LibraryService.PurgeTempCopies();
        var problems = DataRemoval.Execute(plan);
        if (problems.Count == 0) return 0;
        MessageBox.Show("These couldn't be deleted (a file may be open in another program):\n\n" + string.Join("\n", problems.Take(15)) +
                        "\n\nDelete them yourself.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return 2;
    }

    /// <summary>
    /// First run of the Microsoft Store copy on a PC that already has a library from the website
    /// version: offer to copy it into the Store copy's library folder. The website version's copy is
    /// left exactly as it was, so either version can still be used.
    /// </summary>
    /// <returns>False when CaseKeeper should close instead of carrying on.</returns>
    private static bool OfferWebsiteLibrary(string root)
    {
        var source = LibraryService.FindWebsiteLibraryToCopy(root);
        if (source == null) return true;
        if (MessageBox.Show("CaseKeeper found the library used by the version installed from the website:\n\n" + source +
                            "\n\nThe Microsoft Store version keeps its library in a different folder:\n\n" + root +
                            "\n\nCopy your existing library there now? Your documents, notes and settings come across exactly as they are, " +
                            "and the original stays where it is.\n\nChoose No to start with an empty library instead. " +
                            "(You can still bring the old one across later with a backup: Settings → Safety & recovery.)",
                "CaseKeeper", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return true;
        if (LibraryLock.FindOtherHolder(source) != null || DataRemoval.OtherCopyRunning())
        {
            MessageBox.Show("The website version of CaseKeeper seems to be open. Close it, then start CaseKeeper again to copy the library.",
                "CaseKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var busy = new Window
        {
            Title = "CaseKeeper", Width = 420, Height = 130, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Style = (Style)Current.FindResource("AppWindow"),
            Content = new TextBlock { Text = "Copying your library…", Margin = new Thickness(22), TextWrapping = TextWrapping.Wrap }
        };
        busy.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var status = (TextBlock)busy.Content;
        Exception failure = null;
        int copied = 0;
        busy.Loaded += async (_, _) =>
        {
            var progress = new Progress<string>(s => status.Text = s);
            try { copied = await Task.Run(() => LibraryService.CopyLibrary(source, root, progress)); }
            catch (Exception ex) { failure = ex; }
            busy.Close();
        };
        busy.ShowDialog();

        if (failure == null)
        {
            AppLog.Info("Library", $"Copied the website version's library into the Store library folder ({copied} files)");
            return true;
        }
        var id = AppLog.Error("Library", "Copying the website version's library failed", failure);
        // CopyLibrary has already taken back its partial copy; the original was only ever read
        MessageBox.Show("The library could not be copied:\n\n" + failure.Message + $"\n\nNothing has been changed in the original. Reference: {id}" +
                        "\n\nCaseKeeper will start with an empty library. You can bring the old one across later with a backup.",
            "CaseKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    /// <summary>Reads the settings that shape how the app behaves rather than what it shows.</summary>
    public static void ApplyStoredPreferences()
    {
        var db = Library.Db;
        AppLog.MinimumLevel = db.GetSetting("log.verbose", "0") == "1" ? LogLevel.Debug : LogLevel.Info;
        AppLog.RetentionDays = int.TryParse(db.GetSetting("log.retentionDays", "30"), out var days) ? Math.Clamp(days, 1, 365) : 30;
        Data.Database.Station = string.IsNullOrWhiteSpace(db.GetSetting("station.label", ""))
            ? Data.Database.DefaultStation()
            : db.GetSetting("station.label", "").Trim();
        Performance.Mode = Enum.TryParse<PerformanceMode>(db.GetSetting("performance.mode", "Auto"), out var mode) ? mode : PerformanceMode.Auto;
        SessionGuard.IdleMinutes = int.TryParse(db.GetSetting("security.idleLockMinutes", "15"), out var idle) ? Math.Max(0, idle) : 15;
        SessionGuard.LockWithWindows = db.GetSetting("security.lockWithWindows", "1") == "1";
    }

    /// <summary>Health check, restore point and tidying, done once each time the library opens.</summary>
    private static void StartupHousekeeping(int failedUnlocks)
    {
        var db = Library.Db;
        try
        {
            var check = db.QuickCheck();
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Error("Database", "Integrity check reported: " + check);
                MessageBox.Show("CaseKeeper's database health check found a problem:\n\n" + check +
                                "\n\nYour data has not been changed. Consider restoring the latest snapshot or backup from " +
                                "Settings → Safety & recovery.", "CaseKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { AppLog.Error("Database", "Health check could not run", ex); }

        db.Audit("library.open", "library", null, $"Opened the library (CaseKeeper {Views.MainWindow.AppVersion})",
            failedUnlocks > 0 ? $"{failedUnlocks} wrong password(s) entered before unlocking" : "");

        int keep = int.TryParse(db.GetSetting("snapshots.keep", "7"), out var k) ? k : 7;
        Library.SnapshotIfDue(keep);

        int binDays = int.TryParse(db.GetSetting("bin.days", "30"), out var b) ? b : 30;
        try { Library.PurgeExpired(binDays); }
        catch (Exception ex) { AppLog.Warn("Library", "Couldn't empty old items from Deleted items", ex); }
    }

    /// <summary>
    /// Checks the library folder can be reached. The default folder is created on first run, but a
    /// folder the user chose is never recreated: if it's missing, the drive is most likely
    /// disconnected, and quietly starting an empty library there would hide their real one.
    /// </summary>
    private static bool TryReachLibrary(string root, out Vault vault, out string problem)
    {
        vault = null;
        problem = null;
        try
        {
            bool chosen = !string.Equals(Path.GetFullPath(root).TrimEnd('\\'),
                Path.GetFullPath(LibraryService.DefaultRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (chosen && !LibraryService.LocationIsFixedByEnvironment && !Directory.Exists(root))
            {
                problem = "The folder can't be found. If it's on a network drive, NAS or external disk, check it's connected.";
                return false;
            }
            // The database engine can't use paths beyond Windows' classic 260-character limit, and it
            // adds suffixes such as "-journal" to the file name, so leave some room
            if (Path.Combine(Path.GetFullPath(root), "casekeeper.db-journal").Length >= 255)
            {
                problem = "The folder's location is too long for Windows (its full path has more than about 230 characters). " +
                          "Choose a folder closer to the top of the drive, for example C:\\CaseKeeper or D:\\Cases\\Smith.";
                return false;
            }
            Directory.CreateDirectory(root);
            vault = Vault.Load(root);
            return true;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// An editable dropdown is mostly text box, so clicking it would only place the caret.
    /// Open the list as well, unless the click was selecting some of the text.
    /// </summary>
    private static void OpenEditableComboOnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox { IsEditable: true, IsDropDownOpen: false } combo) return;
        if (combo.Template?.FindName("PART_EditableTextBox", combo) is not TextBox box) return;
        if (e.OriginalSource is not Visual hit || !(hit == box || box.IsAncestorOf(hit))) return;
        if (box.SelectionLength > 0) return;
        // Wait until the text box has finished with the click, so the list keeps the mouse
        combo.Dispatcher.BeginInvoke(() => combo.IsDropDownOpen = true, DispatcherPriority.Input);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(ThemeManager.RefreshIfFollowingWindows);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // After "Remove all CaseKeeper data", anything still trying to reach the deleted library is
        // expected to fail on the way out; there is nothing to report
        if (Data.Database.Retired) { e.Handled = true; return; }
        var id = AppLog.Error("Crash", "Unhandled error in the window", e.Exception);
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message +
                        $"\n\nYour data has not been lost. Reference: {id}\n(The details are in Settings → Logs & diagnostics.)",
            "CaseKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
