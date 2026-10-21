using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Guide;
using CaseKeeper.UI;
using CaseKeeper.Views.Forms;
using CaseKeeper.Views.Modules;

namespace CaseKeeper.Views;

/// <summary>
/// Navigation, modes, shortcuts, the command palette and the services other screens call on
/// ("open this document", "show the history of that", "add this file to the library").
/// </summary>
public partial class MainWindow
{
    /// <summary>The running main window, for screens that need to reach the rest of the app.</summary>
    public static MainWindow Shell { get; private set; }

    public IReadOnlyList<CaseDocument> Documents => _docs;
    public IReadOnlyList<Party> Parties => _parties;
    public IReadOnlyList<Category> Categories => _categories;

    private TabItem CaseTab, ActionsTab, ContactTab, CostsTab, ScottTab, LettersTab, AiTab, ExportsTab, GuideTab, AuditTab, AssistantTab, ReviewTab;
    private HelpAssistantView _assistantView;
    private ImportReviewView _reviewView;

    /// <summary>The screen the person was on before opening the help assistant, so it can tailor directions.</summary>
    public string LastScreenName { get; private set; } = "Dashboard";
    private CaseHub _caseHub;
    private RecordListView<CaseAction> _actionsView;
    private RecordListView<ContactSession> _contactView;
    private RecordListView<Expense> _costsView;
    private RecordListView<ScottItem> _scottView;
    private LettersView _lettersView;
    private AiView _aiView;
    private ExportsView _exportsView;
    private GuideView _guideView;
    private AuditView _auditView;
    private readonly List<(TabItem Tab, bool ProOnly)> _navItems = new();
    private TextBlock _modeText;

    public bool ProfessionalMode => Db.GetSetting("ui.mode", "Simple") == "Professional";

    // ================================================================== navigation
    private static TabItem NavHeader(string text) => new() { Header = text, Style = (Style)Application.Current.FindResource("NavHeader") };

    private TabItem NewTab(string header, string glyph, Func<UIElement> content)
    {
        // The screen itself is built the first time it's opened, so start-up stays quick
        var tab = new TabItem { Header = header, Tag = glyph, Content = new ContentControl() };
        tab.Resources["factory"] = content;
        return tab;
    }

    private void EnsureContent(TabItem tab)
    {
        if (tab?.Resources["factory"] is Func<UIElement> factory && tab.Content is ContentControl { Content: null } holder)
        {
            try { holder.Content = factory(); }
            catch (Exception ex)
            {
                var id = AppLog.Error("UI", $"Couldn't open {tab.Header}", ex);
                holder.Content = Ui.Muted($"This screen couldn't be opened ({ex.Message}). Reference: {id}", 14);
            }
        }
    }

    /// <summary>Builds the sidebar: section headings, the new screens, and the mode switch at the bottom.</summary>
    private void BuildNavigation()
    {
        CaseTab = NewTab("Case & hearings", "", () => _caseHub = new CaseHub());
        ActionsTab = NewTab("Directions & tasks", "", () => _actionsView = CaseModules.Actions());
        ContactTab = NewTab("Contact log", "", () => _contactView = CaseModules.Contact());
        CostsTab = NewTab("Costs", "", () => _costsView = CaseModules.Costs());
        ScottTab = NewTab("Scott schedule", "", () => _scottView = CaseModules.Scott());
        LettersTab = NewTab("Letters & templates", "", () => _lettersView = new LettersView());
        AiTab = NewTab("AI analysis", "", () => _aiView = new AiView());
        ExportsTab = NewTab("Exports", "", () => _exportsView = new ExportsView());
        GuideTab = NewTab("Guide & FAQ", "", () => _guideView = new GuideView());
        AuditTab = NewTab("Audit trail", "", () => _auditView = new AuditView());
        AssistantTab = NewTab("Help assistant", "", () => _assistantView = new HelpAssistantView());
        ReviewTab = NewTab("Import review", "", () => _reviewView = new ImportReviewView());
        DashboardTab.Header = "Dashboard";
        BundleTab.Header = "Bundles & reports";
        SettingsTab.Header = "Settings";

        var order = new (TabItem Tab, bool Pro)[]
        {
            (NavHeader("OVERVIEW"), false), (DashboardTab, false), (CaseTab, false),
            (NavHeader("PAPERS"), false), (DocumentsTab, false), (ReviewTab, false), (SearchTab, false), (PartiesTab, false),
            (NavHeader("TRACK"), false), (TimelineTab, false), (ActionsTab, false), (ContactTab, false), (ComplaintsTab, false),
            (CostsTab, true), (ScottTab, true),
            (NavHeader("PRODUCE"), false), (LettersTab, false), (BundleTab, false), (AiTab, true), (ExportsTab, true),
            (NavHeader("HELP & SYSTEM"), false), (GuideTab, false), (AssistantTab, false), (AuditTab, true), (SettingsTab, false)
        };
        Nav.Items.Clear();
        foreach (var (tab, pro) in order)
        {
            Nav.Items.Add(tab);
            _navItems.Add((tab, pro));
        }
        Nav.SelectedItem = DashboardTab;

        // ---- sidebar footer: mode switch and the command palette hint
        var footer = new StackPanel();
        var palette = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 6, 10, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0), ToolTip = "Find any screen, action or document (Ctrl+K)"
        };
        var pRow = new StackPanel { Orientation = Orientation.Horizontal };
        var pIcon = Ui.Icon("", 13, "SidebarText");
        pIcon.Margin = new Thickness(0, 0, 10, 0);
        var pText = new TextBlock { Text = "Find anything   Ctrl+K", FontSize = 12 };
        pText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarText");
        pRow.Children.Add(pIcon);
        pRow.Children.Add(pText);
        palette.Content = pRow;
        palette.Click += (_, _) => ShowPalette();
        footer.Children.Add(palette);

        var mode = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 6, 10, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0),
            ToolTip = "Simple shows the essentials; Professional shows every tool. Nothing is lost when you switch."
        };
        var mRow = new StackPanel { Orientation = Orientation.Horizontal };
        var mIcon = Ui.Icon("", 13, "SidebarAccent");
        mIcon.Margin = new Thickness(0, 0, 10, 0);
        _modeText = new TextBlock { FontSize = 12 };
        _modeText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarText");
        mRow.Children.Add(mIcon);
        mRow.Children.Add(_modeText);
        mode.Content = mRow;
        mode.Click += (_, _) => SetMode(!ProfessionalMode);
        footer.Children.Add(mode);
        Nav.Tag = footer;
        ApplyMode();
    }

    public void SetMode(bool professional)
    {
        Db.SetSetting("ui.mode", professional ? "Professional" : "Simple");
        Db.Audit("settings.mode", "settings", null, $"Switched to {(professional ? "Professional" : "Simple")} mode");
        ApplyMode();
        LoadSettings();
    }

    private void ApplyMode()
    {
        bool pro = ProfessionalMode;
        bool aiOn = AiService.Enabled;
        foreach (var (tab, proOnly) in _navItems)
        {
            bool show = !proOnly || pro || (tab == AiTab && aiOn);
            tab.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show && Nav.SelectedItem == tab) Nav.SelectedItem = DashboardTab;
        }
        if (_modeText != null) _modeText.Text = pro ? "Professional mode  ·  switch" : "Simple mode  ·  switch";
    }

    /// <summary>Opens a screen by name. Used by the palette, the dashboard and other screens.</summary>
    public void GoTo(string key)
    {
        TabItem tab = key switch
        {
            "dashboard" => DashboardTab, "case" or "hearings" or "orders" => CaseTab, "documents" => DocumentsTab, "search" => SearchTab,
            "parties" => PartiesTab, "timeline" => TimelineTab, "actions" => ActionsTab, "contact" => ContactTab, "complaints" => ComplaintsTab,
            "costs" => CostsTab, "scott" => ScottTab, "letters" => LettersTab, "bundles" => BundleTab, "ai" => AiTab, "exports" => ExportsTab,
            "guide" => GuideTab, "audit" => AuditTab, "settings" => SettingsTab, "assistant" => AssistantTab, "review" => ReviewTab, _ => null
        };
        if (tab == null) return;
        if (tab.Visibility != Visibility.Visible) tab.Visibility = Visibility.Visible;   // reachable from the palette even when hidden
        Nav.SelectedItem = tab;
        EnsureContent(tab);
        if (key is "hearings" or "orders") Dispatcher.BeginInvoke(() => _caseHub?.Show(key));
    }

    public void GoToSettings(string section)
    {
        GoTo("settings");
        Dispatcher.BeginInvoke(() => ScrollSettingsTo(section), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Follows a link from the help assistant: (go:screen), (settings:section) or (guide:article).</summary>
    public void Navigate(string kind, string target)
    {
        try
        {
            switch (kind)
            {
                case "go" when target == "import": Import_Click(this, new RoutedEventArgs()); break;
                case "go" when target == "bulkimport": OpenBulkImport(); break;
                case "go": GoTo(target); break;
                case "settings": GoToSettings(target); break;
                case "guide":
                    if (GuideContent.All.Any(a => a.Id == target)) OpenGuide(target);
                    else OpenGuide(search: target.Replace("app.", "").Replace('.', ' '));
                    break;
            }
        }
        catch (Exception ex) { AppLog.Warn("UI", $"Couldn't follow help link {kind}:{target}", ex); }
    }

    /// <summary>Opens the help assistant, optionally asking a question straight away.</summary>
    public void AskAssistant(string question = null)
    {
        GoTo("assistant");
        if (!string.IsNullOrWhiteSpace(question))
            Dispatcher.BeginInvoke(() => _assistantView?.Ask(question), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void OpenGuide(string articleId = null, string search = null)
    {
        GoTo("guide");
        Dispatcher.BeginInvoke(() =>
        {
            if (articleId != null) _guideView?.Open(articleId);
            if (search != null) _guideView?.SearchFor(search);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnNavChangedLazy()
    {
        if (Nav.SelectedItem is not TabItem tab) return;
        if (tab != AssistantTab && tab.Header is string name) LastScreenName = name;
        EnsureContent(tab);
        // Refresh the record screens when they're shown, in case something changed elsewhere
        if (tab == ActionsTab) _actionsView?.Reload();
        else if (tab == ContactTab) _contactView?.Reload();
        else if (tab == CostsTab) _costsView?.Reload();
        else if (tab == ScottTab) _scottView?.Reload();
        else if (tab == CaseTab) _caseHub?.Reload();
        else if (tab == AiTab) _aiView?.Reload();
        else if (tab == ExportsTab) _exportsView?.Reload();
        else if (tab == AuditTab) _auditView?.Reload();
        else if (tab == ReviewTab) _reviewView?.Reload();
    }

    // ================================================================== services for other screens
    public void OpenDocumentById(long id)
    {
        var d = FindDoc(id) ?? Db.GetDocument(id);
        if (d == null) { MessageBox.Show(this, "That document is no longer in the library.", "CaseKeeper"); return; }
        OpenDocument(d);
    }

    public void ShowAuditFor(Type type, long id)
    {
        var entity = type.GetCustomAttributes(typeof(EntityAttribute), false).OfType<EntityAttribute>().FirstOrDefault();
        ShowAuditFor(entity?.Label ?? type.Name.ToLowerInvariant(), id);
    }

    public void ShowAuditFor(string entityType, long? id)
    {
        GoTo("audit");
        Dispatcher.BeginInvoke(() => _auditView?.FocusOn(entityType, id), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void RefreshTimelineAndDashboard()
    {
        _events = Db.GetEvents();
        RefreshTimeline();
        RefreshDashboard();
    }

    public void RefreshAll()
    {
        ReloadKeepingSelection();
        _actionsView?.Reload();
        _caseHub?.Reload();
    }

    public void AddOrderForHearing(Hearing h)
    {
        GoTo("orders");
        Dispatcher.BeginInvoke(() =>
        {
            var view = _caseHub?.OrdersView;
            if (view == null) return;
            var o = new CourtOrder { OrderDate = h.HearingDate, Court = h.Court, MadeBy = h.Judge, HearingId = h.Id, DocumentId = h.DocumentId };
            var dlg = new RecordDialog("Record the order made", o, new[]
            {
                FieldSpec.Date("Date of order", nameof(CourtOrder.OrderDate), required: true),
                FieldSpec.Choice("Type of order", nameof(CourtOrder.OrderType), CourtOrder.Types),
                FieldSpec.Text("Made by", nameof(CourtOrder.MadeBy)),
                FieldSpec.Check("Interim order", nameof(CourtOrder.IsInterim)),
                FieldSpec.Multi("Summary", nameof(CourtOrder.Summary), 150),
                FieldSpec.Document("The order document", nameof(CourtOrder.DocumentId))
            }, _docs) { Owner = this };
            if (dlg.ShowDialog() == true) view.Save(o);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void AddDirectionForOrder(CourtOrder o)
    {
        GoTo("actions");
        Dispatcher.BeginInvoke(() =>
        {
            if (_actionsView == null) return;
            var a = new CaseAction { Kind = "Court direction", OrderId = o.Id, DocumentId = o.DocumentId, Notes = $"From the {o.AuditName}" };
            var dlg = new RecordDialog("Add a direction from this order", a, new[]
            {
                FieldSpec.Text("What must be done", nameof(CaseAction.Title), required: true),
                FieldSpec.Choice("Who must do it", nameof(CaseAction.Responsible), CaseAction.Responsibles, half: true),
                FieldSpec.Date("By", nameof(CaseAction.DueDate)),
                FieldSpec.Time("Time", nameof(CaseAction.DueTime)),
                FieldSpec.Multi("Exact wording", nameof(CaseAction.Detail), 100)
            }, _docs) { Owner = this };
            if (dlg.ShowDialog() == true) _actionsView.Save(a);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Adds a file CaseKeeper produced (a letter, an AI report…) to the library as a document.</summary>
    public void ImportGenerated(byte[] bytes, string fileName, string title, string categoryName, DateTime? date)
    {
        try
        {
            var dir = Path.Combine(LibraryService.TempCopiesFolder, "gen-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, LibraryService.SafeFileName(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName));
            File.WriteAllBytes(path, bytes);
            var cat = _categories.FirstOrDefault(c => c.Name == categoryName)?.Id ?? UncategorisedId;
            var doc = Lib.Import(path, new CaseDocument { Title = title, CategoryId = cat, DocumentDate = date ?? DateTime.Today, Author = "Me (CaseKeeper)" });
            try { Directory.Delete(dir, true); } catch { /* wiped at exit */ }
            ReloadAll();
            EnqueueOcr(new[] { doc });
            if (MessageBox.Show(this, $"“{title}” has been added to your documents.\n\nOpen it now?", "Added to library",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenDocument(doc);
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("Library", "Couldn't add a generated file to the library", ex);
            MessageBox.Show(this, $"Couldn't add it to the library: {ex.Message}\nReference: {id}", "CaseKeeper");
        }
    }

    // ================================================================== keyboard
    private bool HandleGlobalKeys(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.K && mods == ModifierKeys.Control) { ShowPalette(); return true; }
        if (e.Key == Key.L && mods == ModifierKeys.Control && SessionGuard.CanLock) { SessionGuard.Lock("locked with Ctrl+L"); return true; }
        if (e.Key == Key.F1) { OpenGuide(HelpArticleForCurrentTab()); return true; }
        if (e.Key == Key.I && mods == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) { Import_Click(this, e); return true; }
        if (e.Key == Key.I && mods == (ModifierKeys.Control | ModifierKeys.Shift)) { OpenBulkImport(); return true; }
        return false;
    }

    private string HelpArticleForCurrentTab()
    {
        var t = Nav.SelectedItem;
        if (t == DocumentsTab) return "app.documents";
        if (t == SearchTab) return "app.search";
        if (t == CaseTab) return "app.case";
        if (t == ActionsTab) return "app.actions";
        if (t == ContactTab) return "app.contact";
        if (t == CostsTab || t == ScottTab) return "app.costs";
        if (t == LettersTab) return "app.letters";
        if (t == BundleTab) return "app.bundles";
        if (t == AiTab) return "app.ai";
        if (t == AuditTab) return "app.audit";
        if (t == AssistantTab) return "app.assistant";
        if (t == ReviewTab) return "app.bulkimport";
        if (t == SettingsTab) return "app.security";
        return "quickstart";
    }

    // ================================================================== command palette
    private void ShowPalette()
    {
        var palette = new CommandPalette(PaletteCommands) { Owner = this };
        palette.ShowDialog();
    }

    private IEnumerable<PaletteCommand> PaletteCommands(string query)
    {
        PaletteCommand Go(string title, string key, string glyph, string keywords = "", string gesture = "") =>
            new() { Title = title, Group = "Go to", Glyph = glyph, Keywords = keywords, Gesture = gesture, Run = () => GoTo(key) };
        PaletteCommand Do(string title, string glyph, Action run, string keywords = "", string gesture = "") =>
            new() { Title = title, Group = "Action", Glyph = glyph, Keywords = keywords, Gesture = gesture, Run = run };

        var list = new List<PaletteCommand>
        {
            Go("Dashboard", "dashboard", "", "home overview"),
            Go("Case details", "case", "", "case number court judge"),
            Go("Hearings", "hearings", "", "hearing listed fhdra dra final"),
            Go("Court orders", "orders", "", "order made"),
            Go("Documents", "documents", "", "files papers library explorer"),
            Go("Import review", "review", "", "ai suggestions approve reject review findings bulk import unknown conflicts"),
            Go("Search everything", "search", "", "find text", "Ctrl+F"),
            Go("Parties", "parties", "", "people applicant respondent children cafcass"),
            Go("Timeline & chronology", "timeline", "", "events incidents chronology"),
            Go("Directions & tasks", "actions", "", "deadlines to do overdue"),
            Go("Contact log", "contact", "", "handover missed contact"),
            Go("Complaints", "complaints", "", "mp ombudsman"),
            Go("Costs & expenses", "costs", "", "money fees"),
            Go("Scott schedule", "scott", "", "allegations fact finding"),
            Go("Letters & templates", "letters", "", "write letter statement template letterhead word"),
            Go("Bundles & reports", "bundles", "", "court bundle pdf pd27a"),
            Go("AI analysis", "ai", "", "ollama summary chronology"),
            Go("Exports", "exports", "", "saved files pdf"),
            Go("Guide & FAQ", "guide", "", "help law faq glossary", "F1"),
            Go("Help assistant", "assistant", "", "help how do i ask question chat ai directions"),
            Go("Audit trail", "audit", "", "history log changes"),
            Go("Settings", "settings", "", "options preferences"),
            Do("Import documents…", "", () => Import_Click(this, new RoutedEventArgs()), "add files", "Ctrl+I"),
            Do("Bulk Case Import – a whole folder…", "", OpenBulkImport, "folder directory subfolders bulk case import organise ai", "Ctrl+Shift+I"),
            Do("Add a hearing…", "", () => { GoTo("hearings"); Dispatcher.BeginInvoke(() => _caseHub?.HearingsView?.Add(), System.Windows.Threading.DispatcherPriority.Loaded); }),
            Do("Add a direction or task…", "", () => { GoTo("actions"); Dispatcher.BeginInvoke(() => _actionsView?.Add(), System.Windows.Threading.DispatcherPriority.Loaded); }),
            Do("Log contact…", "", () => { GoTo("contact"); Dispatcher.BeginInvoke(() => _contactView?.Add(), System.Windows.Threading.DispatcherPriority.Loaded); }),
            Do("Add a timeline event…", "", () => AddEvent_Click(this, new RoutedEventArgs())),
            Do("Add an expense…", "", () => { GoTo("costs"); Dispatcher.BeginInvoke(() => _costsView?.Add(), System.Windows.Threading.DispatcherPriority.Loaded); }),
            Do("Write a new letter…", "", () => { GoTo("letters"); }),
            Do("Deadline calculator…", "", () => new DeadlineCalculatorWindow { Owner = this }.ShowDialog(), "date days clear working"),
            Do("Create a backup…", "", async () => await RunBackupAsync(), "save zip"),
            Do("Check document integrity", "", async () => await VerifyIntegrityAsync(), "verify hash sha256"),
            Do("Verify the audit trail", "", () => { GoTo("audit"); }, "chain"),
            Do(ProfessionalMode ? "Switch to Simple mode" : "Switch to Professional mode", "", () => SetMode(!ProfessionalMode), "layout power user"),
            Do("Lock CaseKeeper", "", () => SessionGuard.Lock("locked from the command palette"), "password", "Ctrl+L"),
            Do("AI settings", "", () => GoToSettings("ai"), "ollama model"),
            Do("Open the log folder", "", () => OpenWithShell(AppLog.Folder), "errors diagnostics"),
        };

        foreach (var lib in LibraryService.RecentLibraries().Where(p => !string.Equals(p, Lib.RootFolder, StringComparison.OrdinalIgnoreCase)))
            list.Add(new PaletteCommand { Title = "Switch to library: " + lib, Group = "Library", Glyph = "", Keywords = "case matter switch", Run = () => SwitchLibrary(lib) });

        var q = query.Trim();
        if (q.Length >= 2)
        {
            foreach (var d in _docs.Where(d => $"{d.Title} {d.CategoryName} {d.ExhibitRef} {d.Tags}".Contains(q, StringComparison.OrdinalIgnoreCase)).Take(12))
                list.Add(new PaletteCommand { Title = d.Title, Group = $"Document · {d.CategoryName} · {d.DocumentDateText}", Glyph = "", Keywords = $"{d.CategoryName} {d.ExhibitRef} {d.Tags}", Run = () => OpenDocument(d) });
            foreach (var p in _parties.Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(5))
                list.Add(new PaletteCommand { Title = p.DisplayName, Group = "Party", Glyph = "", Run = () => SelectFilterAndShow($"party:{p.Id}") });
            foreach (var c in _categories.Where(c => $"{c.DisplayName} {c.GroupName}".Contains(q, StringComparison.OrdinalIgnoreCase)).Take(8))
                list.Add(new PaletteCommand { Title = c.DisplayName, Group = $"Category · {c.GroupName} · {c.DocumentCount} doc(s)", Glyph = "", Keywords = c.GroupName, Run = () => SelectFilterAndShow($"cat:{c.Id}") });
            foreach (var (a, _) in GuideView.Search(q).Take(5))
                list.Add(new PaletteCommand { Title = a.Title, Group = "Guide · " + a.Topic, Glyph = "", Keywords = a.Keywords, Run = () => OpenGuide(a.Id) });
            list.Add(new PaletteCommand
            {
                Title = $"Ask the help assistant: “{q}”", Group = "Search", Glyph = "", Keywords = q,
                Run = () => AskAssistant(q)
            });
            list.Add(new PaletteCommand
            {
                Title = $"Search everything for “{q}”", Group = "Search", Glyph = "", Keywords = q,
                Run = () => { GoTo("search"); SearchBox.Text = q; RunSearch(); }
            });
        }
        return list;
    }

    private void SelectFilterAndShow(string key)
    {
        DocFilterBox.Text = "";
        TreeFilterBox.Text = "";
        SelectFilter(key);
        Nav.SelectedItem = DocumentsTab;
    }

    private void SwitchLibrary(string path)
    {
        var check = LibraryService.InspectFolder(path, Lib.RootFolder);
        if (check.Problem != null) { MessageBox.Show(this, check.Problem, "Switch library"); return; }
        if (MessageBox.Show(this, $"Switch to the library in:\n{path}\n\nCaseKeeper will restart.", "Switch library",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        LibraryService.UseExistingLibraryAt(path);
        RestartApp();
    }

    // ================================================================== first run
    private void FirstRunIfNeeded()
    {
        if (Db.GetSetting("onboarded", "0") == "1") return;
        bool brandNew = _docs.Count == 0 && _parties.Count == 0 && _events.Count == 0;
        Db.SetSetting("onboarded", "1");
        if (!brandNew)
        {
            // An existing library from an earlier version: keep what people know, but say what's new
            Db.SetSetting("ui.mode", "Simple");
            ApplyMode();
            MessageBox.Show(this,
                $"CaseKeeper has been updated to version {AppVersion}.\n\n" +
                "New: hearings and orders, directions and deadlines, a contact log, letters and templates with your own letterhead " +
                "(Word, PDF and web exports), a built-in UK family law guide, an audit trail, automatic snapshots, and optional local AI.\n\n" +
                "Press Ctrl+K at any time to find anything. Switch to Professional mode (bottom of the sidebar) to see every tool.\n\n" +
                "All your existing documents and notes are exactly as they were.",
                "What's new", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var w = new WelcomeWindow { Owner = this };
        if (w.ShowDialog() != true) return;
        Db.SetSetting("ui.mode", w.Professional ? "Professional" : "Simple");
        if (!string.IsNullOrWhiteSpace(w.CaseNumber)) Db.SetSetting("case.number", w.CaseNumber);
        if (!string.IsNullOrWhiteSpace(w.Court)) Db.SetSetting("case.court", w.Court);
        if (!string.IsNullOrWhiteSpace(w.CaseType)) Db.SetSetting("case.type", w.CaseType);
        if (!string.IsNullOrWhiteSpace(w.MyName) || !string.IsNullOrWhiteSpace(w.Role))
            Db.SaveParty(new Party { Role = string.IsNullOrWhiteSpace(w.Role) ? "Applicant" : w.Role, Name = w.MyName, IsMe = true });
        var theme = UI.ThemeManager.Parse(w.Theme);
        UI.ThemeManager.Apply(theme);
        Db.SetSetting("theme", theme.ToString());
        Db.Audit("library.setup", "library", null, "Completed first-run setup", w.Professional ? "Professional mode" : "Simple mode");
        ReloadAll();
        ApplyMode();
        LoadSettings();
        if (w.ProtectNow) EnableProtection_Click(this, new RoutedEventArgs());
    }

    // ================================================================== dashboard extras
    private void RefreshDashboardExtras()
    {
        if (DashboardExtras == null) return;
        DashboardExtras.Children.Clear();
        AddBulkDashboardNotice(DashboardExtras);
        List<Hearing> hearings;
        List<CaseAction> actions;
        List<ContactSession> contact;
        List<Expense> costs;
        try
        {
            hearings = Db.All<Hearing>();
            actions = Db.All<CaseAction>();
            contact = Db.All<ContactSession>();
            costs = Db.All<Expense>();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Dashboard", "Couldn't load records for the dashboard", ex);
            return;
        }

        var tiles = new WrapPanel();
        Border Tile(string glyph, string big, string label, string sub, string go, string brush = "AccentText")
        {
            var s = new StackPanel();
            s.Children.Add(Ui.Icon(glyph, 20, brush));
            var b = new TextBlock { Text = big, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            b.SetResourceReference(TextBlock.ForegroundProperty, brush == "AccentText" ? "TextBrush" : brush);
            s.Children.Add(b);
            s.Children.Add(Ui.Text(label, "Muted"));
            if (sub != null) s.Children.Add(Ui.Muted(sub, 11.5));
            var card = new Border { Style = Ui.Style("Card"), Width = 215, Margin = new Thickness(0, 0, 14, 14), Child = s, Cursor = Cursors.Hand, ToolTip = "Open" };
            card.MouseLeftButtonUp += (_, _) => GoTo(go);
            return card;
        }

        var next = hearings.Where(h => h.IsUpcoming).OrderBy(h => h.HearingDate).FirstOrDefault();
        tiles.Children.Add(Tile("", next == null ? "—" : next.WhenText, "Next hearing",
            next == null ? "None listed" : $"{next.ShortType} · {next.HearingDate:d MMM}", "hearings"));
        int overdue = actions.Count(a => a.IsOverdue), open = actions.Count(a => a.IsOpen);
        tiles.Children.Add(Tile("", overdue > 0 ? $"{overdue} overdue" : open.ToString(), overdue > 0 ? "Directions & tasks" : "Things to do",
            $"{open} still to do", "actions", overdue > 0 ? "Danger" : "AccentText"));
        var recent = contact.Where(c => c.SessionDate >= DateTime.Today.AddDays(-90)).ToList();
        tiles.Children.Add(Tile("", recent.Count == 0 ? "—" : $"{100 * recent.Count(c => c.TookPlace) / recent.Count}%", "Contact (90 days)",
            recent.Count == 0 ? "Nothing logged" : $"{recent.Count(c => c.TookPlace)} of {recent.Count} took place", "contact"));
        if (ProfessionalMode)
            tiles.Children.Add(Tile("", Expense.Money(costs.Sum(c => c.AmountPence)), "Costs so far", $"{costs.Count} item(s)", "costs"));
        DashboardExtras.Children.Add(tiles);

        // ---- due soon
        var due = new List<(DateTime When, string What, string Kind, string Go, bool Late)>();
        foreach (var h in hearings.Where(h => h.IsUpcoming && h.HearingDate <= DateTime.Today.AddDays(30)))
            due.Add((h.HearingDate!.Value, $"{h.HearingType}{(string.IsNullOrWhiteSpace(h.Time) ? "" : " at " + h.Time)}", "Hearing", "hearings", false));
        foreach (var a in actions.Where(a => a.IsOpen && a.DueDate.HasValue && a.DueDate <= DateTime.Today.AddDays(14)))
            due.Add((a.DueDate!.Value, $"{a.Title} ({a.Responsible})", a.Kind, "actions", a.IsOverdue));
        foreach (var c in _complaints.Where(c => c.ResponseDue.HasValue && c.Status is not ("Resolved" or "Closed") && c.ResponseDue <= DateTime.Today.AddDays(14)))
            due.Add((c.ResponseDue!.Value, $"Reply due: {c.Subject}", "Complaint", "complaints", c.IsOverdue));
        if (due.Count > 0)
        {
            var list = new StackPanel();
            list.Children.Add(Ui.Text("Due in the next two weeks", "H2"));
            foreach (var d in due.OrderBy(d => d.When).Take(10))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand };
                var when = new TextBlock { Text = d.When.Date == DateTime.Today ? "Today" : d.When.ToString("ddd d MMM"), Width = 110, FontWeight = FontWeights.SemiBold };
                when.SetResourceReference(TextBlock.ForegroundProperty, d.Late ? "Danger" : "AccentText");
                var kind = Ui.Muted(d.Kind, 12);
                kind.Width = 130;
                row.Children.Add(when);
                row.Children.Add(kind);
                row.Children.Add(new TextBlock { Text = d.What + (d.Late ? "  — overdue" : ""), TextTrimming = TextTrimming.CharacterEllipsis });
                var go = d.Go;
                row.MouseLeftButtonUp += (_, _) => GoTo(go);
                list.Children.Add(row);
            }
            DashboardExtras.Children.Add(Ui.Card(list, new Thickness(0, 0, 0, 18)));
        }

        // ---- getting started, until the basics are done
        var steps = new (bool Done, string Text, Action Go)[]
        {
            (_parties.Any(p => p.IsMe), "Add yourself and the other parties", () => GoTo("parties")),
            (!string.IsNullOrWhiteSpace(Db.GetSetting("case.number", "")), "Fill in the case details", () => GoTo("case")),
            (_docs.Count > 0, "Import your documents – a few files, or a whole case folder with Bulk Case Import", () => OpenBulkImport()),
            (hearings.Count > 0, "Add your next hearing", () => GoTo("hearings")),
            (Lib.Vault.IsEncrypted, "Protect the library with a password", () => GoToSettings("security")),
            (Db.GetSetting("lastBackup") != null, "Make your first backup", () => GoToSettings("backup")),
        };
        if (steps.Any(s => !s.Done) && Db.GetSetting("ui.hideGettingStarted", "0") != "1")
        {
            var gs = new StackPanel();
            var head = new DockPanel();
            var hide = new Button { Content = "Hide", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0) };
            hide.Click += (_, _) => { Db.SetSetting("ui.hideGettingStarted", "1"); RefreshDashboardExtras(); };
            DockPanel.SetDock(hide, Dock.Right);
            head.Children.Add(hide);
            head.Children.Add(Ui.Text($"Getting started  ·  {steps.Count(s => s.Done)} of {steps.Length} done", "H2"));
            gs.Children.Add(head);
            foreach (var (done, text, go) in steps)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Cursor = done ? null : Cursors.Hand };
                row.Children.Add(Ui.Icon(done ? "" : "", 13, done ? "Success" : "MutedBrush"));
                var t = new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0) };
                if (done) t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                else t.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                row.Children.Add(t);
                if (!done) row.MouseLeftButtonUp += (_, _) => go();
                gs.Children.Add(row);
            }
            gs.Children.Add(Ui.Muted("Press Ctrl+K at any time to find anything, or F1 for help with the screen you're on.", 12));
            DashboardExtras.Children.Add(Ui.Card(gs, new Thickness(0, 0, 0, 18)));
        }
    }
}

/// <summary>Case &amp; hearings: case details, hearings and orders on one screen.</summary>
public class CaseHub : UserControl
{
    private readonly TabControl _tabs;
    private readonly CaseDetailsView _details;
    public RecordListView<Hearing> HearingsView { get; }
    public RecordListView<CourtOrder> OrdersView { get; }

    public CaseHub()
    {
        _details = new CaseDetailsView();
        HearingsView = CaseModules.Hearings();
        OrdersView = CaseModules.Orders();
        _tabs = new TabControl { Margin = new Thickness(24, 18, 24, 0) };
        _tabs.Items.Add(new TabItem { Header = "Case details", Content = _details });
        _tabs.Items.Add(new TabItem { Header = "Hearings", Content = HearingsView });
        _tabs.Items.Add(new TabItem { Header = "Orders", Content = OrdersView });
        Content = _tabs;
    }

    public void Show(string which) => _tabs.SelectedIndex = which switch { "hearings" => 1, "orders" => 2, _ => 0 };

    public void Reload()
    {
        HearingsView.Reload();
        OrdersView.Reload();
        _details.RefreshChildren();
    }
}
