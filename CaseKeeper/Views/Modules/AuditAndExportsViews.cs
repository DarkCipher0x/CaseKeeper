using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using CaseKeeper.UI;
using CaseKeeper.Views.Forms;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// The audit trail: every change and significant action, with what changed, protected by a hash
/// chain that can be verified. Read-only: entries can be filtered and exported, never edited.
/// </summary>
public class AuditView : UserControl
{
    private static Data.Database Db => App.Library.Db;
    private readonly DataGrid _grid;
    private readonly TextBox _search;
    private readonly ComboBox _group;
    private readonly DatePicker _from, _to;
    private readonly TextBlock _summary, _detail;
    private List<AuditEntry> _all = new();
    private (string Type, long? Id)? _focus;

    public AuditView()
    {
        var root = new DockPanel { Margin = new Thickness(36, 28, 36, 22) };
        var head = new StackPanel();
        head.Children.Add(Ui.Text("Audit trail", "H1"));
        head.Children.Add(Ui.Muted("A permanent record of everything done in this library: imports, edits (with old and new values), notes, " +
                                   "hearings, letters, exports, backups, sign-ins and AI requests. Each entry is chained to the one before " +
                                   "with a SHA-256 fingerprint, so any alteration can be detected with Verify.", 13));
        var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        bar.Children.Add(Ui.Button("Verify integrity", "", (_, _) => Verify(), primary: true, tooltip: "Recalculate every fingerprint in the chain"));
        _group = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 6, 6) };
        _group.SelectionChanged += (_, _) => Refresh();
        bar.Children.Add(_group);
        _from = new DatePicker { Width = 130, Margin = new Thickness(0, 0, 6, 6) };
        _to = new DatePicker { Width = 130, Margin = new Thickness(0, 0, 6, 6) };
        _from.SelectedDateChanged += (_, _) => Refresh();
        _to.SelectedDateChanged += (_, _) => Refresh();
        bar.Children.Add(_from);
        bar.Children.Add(_to);
        bar.Children.Add(Ui.SearchBox(out _search, "Search the trail…", 220));
        _search.TextChanged += (_, _) => Refresh();
        bar.Children.Add(Ui.Button("Clear filters", null, (_, _) => ClearFilters()));
        var export = Ui.Button("Export", "", async (_, _) => await ExportAsync());
        bar.Children.Add(export);
        head.Children.Add(bar);
        _summary = Ui.Muted("", 12.5);
        head.Children.Add(_summary);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var detailBorder = new Border { Style = Ui.Style("Card"), Margin = new Thickness(0, 10, 0, 0), MaxHeight = 190 };
        _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        detailBorder.Child = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(detailBorder, Dock.Bottom);
        root.Children.Add(detailBorder);

        _grid = new DataGrid { SelectionMode = DataGridSelectionMode.Extended };
        _grid.Columns.Add(new DataGridTextColumn { Header = "When", Binding = new Binding("TimeText"), SortMemberPath = "Id", Width = new DataGridLength(160) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Action", Binding = new Binding("Action"), Width = new DataGridLength(150) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "What happened", Binding = new Binding("Summary"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "PC", Binding = new Binding("Station"), Width = new DataGridLength(90) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("Id"), Width = new DataGridLength(60) });
        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.ContextMenuOpening += (_, _) => _grid.ContextMenu = Menu();
        _grid.ContextMenu = new ContextMenu();
        root.Children.Add(_grid);
        Content = root;
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        _all = Db.GetAudit(20000);
        var groups = new[] { "All actions" }.Concat(_all.Select(a => a.ActionGroup).Distinct().OrderBy(s => s)).ToList();
        var sel = _group.SelectedItem as string;
        _group.ItemsSource = groups;
        _group.SelectedItem = groups.Contains(sel) ? sel : groups[0];
        Refresh();
    }

    /// <summary>Shows only the history of one record (from a right-click "History" elsewhere).</summary>
    public void FocusOn(string entityType, long? id)
    {
        _focus = (entityType, id);
        Reload();
    }

    private void ClearFilters()
    {
        _focus = null;
        _search.Text = "";
        _from.SelectedDate = _to.SelectedDate = null;
        _group.SelectedIndex = 0;
        Refresh();
    }

    private void Refresh()
    {
        IEnumerable<AuditEntry> items = _all;
        if (_focus is { } f) items = items.Where(a => a.EntityType == f.Type && (f.Id == null || a.EntityId == f.Id));
        if (_group.SelectedIndex > 0) items = items.Where(a => a.ActionGroup == _group.SelectedItem as string);
        if (_from.SelectedDate is { } from) items = items.Where(a => a.At.ToLocalTime().Date >= from);
        if (_to.SelectedDate is { } to) items = items.Where(a => a.At.ToLocalTime().Date <= to);
        var q = _search.Text.Trim();
        if (q.Length > 0) items = items.Where(a => $"{a.Action} {a.Summary} {a.Details}".Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = items.ToList();
        _grid.ItemsSource = list;
        _summary.Text = $"{list.Count:N0} of {_all.Count:N0} entries" + (_focus is { } ff ? $"   ·   showing the history of one {ff.Type} (Clear filters to see everything)" : "");
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (_grid.SelectedItem is not AuditEntry a)
        {
            _detail.Text = "Select an entry to see exactly what changed.";
            return;
        }
        _detail.Text = $"#{a.Id}   {a.TimeText}   {a.Station}   {a.Action}\n{a.Summary}\n\n" +
                       (string.IsNullOrWhiteSpace(a.Details) ? "(no further detail)" : a.Details) +
                       $"\n\nFingerprint: {a.Hash}\nPrevious:    {(a.PrevHash.Length == 0 ? "(start of the chain)" : a.PrevHash)}";
    }

    private void Verify()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        AuditVerification v;
        try { v = Db.VerifyAudit(); }
        finally { Mouse.OverrideCursor = null; }
        AppLog.Info("Audit", v.Ok ? $"Audit chain verified ({v.Checked} entries)" : $"Audit chain broken at #{v.FirstBrokenId}");
        Db.Audit("audit.verify", "audit", null, v.Ok ? "Verified the audit trail" : "Audit trail verification FAILED", v.Describe());
        MessageBox.Show(Window.GetWindow(this), v.Describe(), "Audit trail", MessageBoxButton.OK,
            v.Ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        Reload();
    }

    private async Task ExportAsync(IEnumerable<AuditEntry> only = null)
    {
        var items = (only ?? (_grid.ItemsSource as IEnumerable<AuditEntry>) ?? Enumerable.Empty<AuditEntry>()).OrderBy(a => a.Id).ToList();
        if (items.Count == 0) return;
        var v = Db.VerifyAudit();
        await ExportHelper.ExportTableAsync(Window.GetWindow(this), "Audit trail",
            $"{items.Count:N0} entries · exported {DateTime.Now:d MMMM yyyy HH:mm} · chain {(v.Ok ? "verified" : "NOT verified")}",
            new[] { "#", "When", "PC", "Action", "What happened", "Details", "Fingerprint" },
            items.Select(a => (IList<string>)new[] { a.Id.ToString(), a.TimeText, a.Station, a.Action, a.Summary, a.Details, a.Hash }).ToList(),
            landscape: true,
            extraBlocks: new[] { Block.Para(v.Describe()) });
    }

    private ContextMenu Menu()
    {
        var a = _grid.SelectedItem as AuditEntry;
        var sel = _grid.SelectedItems.OfType<AuditEntry>().ToList();
        return Ui.Menu(
            Ui.Item("Show the history of this item", "", () => { if (a != null) FocusOn(a.EntityType, a.EntityId); }, enabled: a?.EntityId != null),
            Ui.Item("Show all of this kind", "", () => { if (a != null) _group.SelectedItem = a.ActionGroup; }, enabled: a != null),
            Ui.Item("Open the document", "", () => { if (a?.EntityId is { } id) MainWindow.Shell?.OpenDocumentById(id); },
                enabled: a?.EntityType == "document" && a.EntityId != null),
            new Separator(),
            Ui.Item("Copy", "", () => Ui.CopyToClipboard(string.Join("\n", sel.Select(x => $"#{x.Id}\t{x.TimeText}\t{x.Action}\t{x.Summary}\t{x.Details.Replace("\n", " | ")}")))),
            Ui.Item("Export selected…", "", async () => await ExportAsync(sel), enabled: sel.Count > 0));
    }
}

/// <summary>
/// Everything CaseKeeper has saved into the library's Exports folder (bundles, letters, reports),
/// with open, show in folder, share, rename, delete and "add to library". Encrypted files in a
/// protected library are opened through a temporary decrypted copy.
/// </summary>
public class ExportsView : UserControl
{
    public sealed class ExportFile
    {
        public FileInfo Info { get; init; }
        public string Name => Info.Name;
        public string Type => Info.Extension.TrimStart('.').ToUpperInvariant();
        public string SizeText => Info.Length < 1024 * 1024 ? $"{Math.Max(1, Info.Length / 1024)} KB" : $"{Info.Length / 1048576d:0.0} MB";
        public string ModifiedText => Info.LastWriteTime.ToString("dd MMM yyyy HH:mm");
        public DateTime Modified => Info.LastWriteTime;
        public bool Encrypted { get; init; }
        public string Lock => Encrypted ? "Encrypted" : "";
    }

    private readonly DataGrid _grid;
    private readonly TextBox _search;
    private readonly TextBlock _summary;
    private List<ExportFile> _all = new();

    public ExportsView()
    {
        var root = new DockPanel { Margin = new Thickness(36, 28, 36, 22) };
        var head = new StackPanel();
        head.Children.Add(Ui.Text("Exports", "H1"));
        head.Children.Add(Ui.Muted("Bundles, letters, reports and other files you've saved into the library's Exports folder. " +
                                   "In a password-protected library these are encrypted too, and open through a temporary copy that is wiped when CaseKeeper closes.", 13));
        var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        bar.Children.Add(Ui.Button("Open", "", (_, _) => OpenSelected(), primary: true));
        bar.Children.Add(Ui.Button("Show folder", "", (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Library.ExportsFolder}\"") { UseShellExecute = true })));
        bar.Children.Add(Ui.Button("Refresh", "", (_, _) => Reload()));
        bar.Children.Add(Ui.SearchBox(out _search, "Filter…", 220));
        _search.TextChanged += (_, _) => Refresh();
        head.Children.Add(bar);
        _summary = Ui.Muted("", 12.5);
        head.Children.Add(_summary);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        _grid = new DataGrid { SelectionMode = DataGridSelectionMode.Extended };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding("Type"), Width = new DataGridLength(70) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new Binding("SizeText"), SortMemberPath = "Info.Length", Width = new DataGridLength(80) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Saved", Binding = new Binding("ModifiedText"), SortMemberPath = "Modified", Width = new DataGridLength(150) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new Binding("Lock"), Width = new DataGridLength(90) });
        _grid.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject d && Ui.FindParent<DataGridRow>(d) != null) OpenSelected(); };
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.F2) { RenameSelected(); e.Handled = true; }
        };
        _grid.ContextMenuOpening += (_, _) => _grid.ContextMenu = Menu();
        _grid.ContextMenu = new ContextMenu();
        root.Children.Add(_grid);
        Content = root;
        Loaded += (_, _) => Reload();
    }

    private List<ExportFile> Selected => _grid.SelectedItems.OfType<ExportFile>().ToList();

    public void Reload()
    {
        var dir = App.Library.ExportsFolder;
        Directory.CreateDirectory(dir);
        _all = new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories)
            .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ExportFile { Info = f, Encrypted = LooksEncrypted(f.FullName) })
            .OrderByDescending(f => f.Modified).ToList();
        Refresh();
    }

    private static bool LooksEncrypted(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[4];
            return fs.Read(head, 0, 4) == 4 && System.Text.Encoding.ASCII.GetString(head) == "CKE1";
        }
        catch { return false; }
    }

    private void Refresh()
    {
        var q = _search.Text.Trim();
        var list = _all.Where(f => q.Length == 0 || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _grid.ItemsSource = list;
        _summary.Text = $"{list.Count} file(s) · {list.Sum(f => f.Info.Length) / 1048576d:0.0} MB";
    }

    private void OpenSelected()
    {
        foreach (var f in Selected.Take(5)) ExportHelper.OpenFile(f.Info.FullName);
    }

    private async void ShareSelected()
    {
        var files = Selected;
        if (files.Count == 0 || !MainWindow.ConfirmConfidentiality(Window.GetWindow(this))) return;
        try
        {
            var paths = files.Select(f => App.Library.ReadableExportPath(f.Info.FullName)).ToList();
            await ShareService.ShareFilesAsync(Window.GetWindow(this), files.Count == 1 ? files[0].Name : $"{files.Count} files", paths);
            App.Library.Db.Audit("export.share", "export", null, $"Shared {files.Count} exported file(s)", string.Join("\n", files.Select(f => f.Name)));
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "Couldn't open the Windows Share window: " + ex.Message, "Share"); }
    }

    private void SaveCopy()
    {
        var f = Selected.FirstOrDefault();
        if (f == null || !MainWindow.ConfirmConfidentiality(Window.GetWindow(this))) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = f.Name, Filter = $"{f.Type} file|*{f.Info.Extension}|All files|*.*" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllBytes(dlg.FileName, App.Library.ReadExport(f.Info.FullName));
        App.Library.Db.Audit("export.copy", "export", null, $"Saved a readable copy of {f.Name}", DocumentExporter.DescribeLocation(dlg.FileName));
    }

    private void RenameSelected()
    {
        var f = Selected.FirstOrDefault();
        if (f == null) return;
        var dlg = new TextPromptWindow("Rename file", "New name", Path.GetFileNameWithoutExtension(f.Name)) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var target = Path.Combine(f.Info.DirectoryName!, LibraryService.SafeFileName(dlg.Value) + f.Info.Extension);
        if (File.Exists(target)) { MessageBox.Show(Window.GetWindow(this), "A file with that name already exists.", "Rename"); return; }
        File.Move(f.Info.FullName, target);
        App.Library.Db.Audit("export.rename", "export", null, $"Renamed {f.Name} to {Path.GetFileName(target)}");
        Reload();
    }

    private void DeleteSelected()
    {
        var files = Selected;
        if (files.Count == 0) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Permanently delete {(files.Count == 1 ? files[0].Name : files.Count + " files")} from the Exports folder?\n\n" +
                "This can't be undone. Documents in your library are not affected.", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var f in files)
        {
            try { f.Info.Delete(); }
            catch (Exception ex) { AppLog.Warn("Exports", "Couldn't delete an exported file", ex); }
        }
        App.Library.Db.Audit("export.delete", "export", null, $"Deleted {files.Count} exported file(s)", string.Join("\n", files.Select(f => f.Name)));
        Reload();
    }

    private void AddToLibrary()
    {
        foreach (var f in Selected)
            MainWindow.Shell?.ImportGenerated(App.Library.ReadExport(f.Info.FullName), f.Name, Path.GetFileNameWithoutExtension(f.Name), null, f.Modified.Date);
    }

    private ContextMenu Menu()
    {
        bool any = Selected.Count > 0, one = Selected.Count == 1;
        return Ui.Menu(
            Ui.Item("Open", "", OpenSelected, "Enter", any, bold: true),
            Ui.Item("Share…", "", ShareSelected, enabled: any),
            Ui.Item("Save a readable copy to…", "", SaveCopy, enabled: one),
            Ui.Item("Add to my documents", "", AddToLibrary, enabled: any),
            Ui.Item("Show in folder", "", () =>
            {
                var f = Selected.FirstOrDefault();
                if (f != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f.Info.FullName}\"") { UseShellExecute = true });
            }, enabled: one),
            Ui.Item("Copy file name", "", () => Ui.CopyToClipboard(string.Join("\n", Selected.Select(f => f.Name))), enabled: any),
            Ui.Item("Rename…", "", RenameSelected, "F2", one),
            new Separator(),
            Ui.Item("Delete…", "", DeleteSelected, "Del", any));
    }
}

/// <summary>Asks for one line of text.</summary>
public class TextPromptWindow : FormDialog
{
    private readonly TextBox _box;
    public string Value { get; private set; } = "";

    public TextPromptWindow(string title, string label, string initial, string hint = null) : base(title, 480, hint == null ? 280 : 320, "OK")
    {
        _box = Add(label, Box(initial));
        if (hint != null) AddHint(hint);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    protected override bool Validate()
    {
        if (!Require(_box, "a value")) return false;
        Value = _box.Text.Trim();
        return true;
    }
}
