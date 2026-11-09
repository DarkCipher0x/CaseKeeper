using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.UI;

namespace CaseKeeper.Views;

/// <summary>
/// The Documents screen as a file explorer: right-click menus everywhere, drag and drop between
/// the list and the tree, rename, move, tag, exhibit numbering, column choice and Deleted items.
/// Also the right-click menus for the timeline, complaints, search results, bundle and parties.
/// </summary>
public partial class MainWindow
{
    private List<CaseDocument> _deletedDocs = new();
    private string _treeSort = "grouped";     // grouped | flat | count
    private bool _treeHideEmpty;
    private readonly HashSet<string> _expandedGroups = new();
    private Point _dragStart;
    private bool _dragArmed;

    private const string DocDragFormat = "CaseKeeper.DocumentIds";

    // ================================================================== tree options
    private void LoadTreePreferences()
    {
        _treeSort = Db.GetSetting("tree.sort", "grouped");
        _treeHideEmpty = Db.GetSetting("tree.hideEmpty", "0") == "1";
    }

    private void TreeFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) BuildCategoryTree();
    }

    private void TreeOptions_Click(object sender, RoutedEventArgs e)
    {
        void SetSort(string s) { _treeSort = s; Db.SetSetting("tree.sort", s); BuildCategoryTree(); }
        var menu = Ui.Menu(
            Ui.Check("Grouped by type, A–Z", _treeSort == "grouped", () => SetSort("grouped")),
            Ui.Check("One list, A–Z (by form code / name)", _treeSort == "flat", () => SetSort("flat")),
            Ui.Check("Busiest first (most documents)", _treeSort == "count", () => SetSort("count")),
            new Separator(),
            Ui.Check("Hide categories with no documents", _treeHideEmpty, () =>
            {
                _treeHideEmpty = !_treeHideEmpty;
                Db.SetSetting("tree.hideEmpty", _treeHideEmpty ? "1" : "0");
                BuildCategoryTree();
            }),
            Ui.Item("Expand all groups", "", () => { foreach (TreeViewItem i in CategoryTree.Items) i.IsExpanded = true; }),
            Ui.Item("Collapse all groups", "", () => { foreach (TreeViewItem i in CategoryTree.Items) i.IsExpanded = false; _expandedGroups.Clear(); }),
            new Separator(),
            Ui.Item("Add your own category…", "", () => AddCategory_Click(this, new RoutedEventArgs())),
            Ui.Item("Manage categories (Settings)…", "", () => GoToSettings("categories")));
        menu.PlacementTarget = TreeOptionsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private TreeViewItem TreeItemAt(DependencyObject source) => Ui.FindParent<TreeViewItem>(source);

    private void CategoryTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = TreeItemAt(e.OriginalSource as DependencyObject);
        var key = item?.Tag as string;
        if (item != null) item.IsSelected = true;
        var items = new List<object>();

        if (key == null && item != null)
        {
            items.Add(Ui.Item(item.IsExpanded ? "Collapse" : "Expand", null, () => item.IsExpanded = !item.IsExpanded));
        }
        else if (key == "deleted")
        {
            items.Add(Ui.Item("Restore everything", "", () => { foreach (var d in _deletedDocs.ToList()) Lib.Restore(d); ReloadAll(); }, enabled: _deletedDocs.Count > 0));
            items.Add(Ui.Item("Empty Deleted items…", "", EmptyDeletedItems, enabled: _deletedDocs.Count > 0));
        }
        else if (key != null)
        {
            var docsHere = DocsForKey(key);
            items.Add(Ui.Item("Show these documents", "", () => SelectFilter(key), bold: true));
            if (key.StartsWith("cat:") || key.StartsWith("party:") || key is "all" or "star")
                items.Add(Ui.Item("Import documents here…", "", () => { _filterKey = key; Import_Click(this, new RoutedEventArgs()); }));
            items.Add(Ui.Item($"Add all {docsHere.Count} to the bundle", "", () => { AddToBundle(docsHere); Nav.SelectedItem = BundleTab; }, enabled: docsHere.Count > 0));
            items.Add(Ui.Item("Save copies of all to a folder…", "", () => ExportDocsToFolder(docsHere), enabled: docsHere.Count > 0));
            items.Add(Ui.Item("Analyse with AI…", "", () => AnalyseWithAi(docsHere, "summary"), enabled: docsHere.Count > 0));

            if (key.StartsWith("cat:") && _categories.FirstOrDefault(c => c.Id == long.Parse(key[4..])) is { } cat)
            {
                items.Add(new Separator());
                items.Add(Ui.Item("Edit category…", "", () => EditCategory(cat)));
                items.Add(Ui.Item("Add a category in this group…", "", () => AddCategoryInGroup(cat.GroupName)));
                items.Add(Ui.Item("Copy name", "", () => Ui.CopyToClipboard(cat.DisplayName)));
                items.Add(Ui.Item("Guide: look this up", "", () => OpenGuide(search: string.IsNullOrWhiteSpace(cat.Code) ? cat.Name : cat.Code)));
                items.Add(new Separator());
                items.Add(Ui.Item("Delete category…", "", () => DeleteCategory(cat), enabled: cat.Id != UncategorisedId));
            }
            else if (key.StartsWith("group:"))
            {
                items.Add(new Separator());
                items.Add(Ui.Item("Add a category in this group…", "", () => AddCategoryInGroup(key[6..])));
            }
            else if (key.StartsWith("party:") && _parties.FirstOrDefault(p => p.Id == long.Parse(key[6..])) is { } party)
            {
                items.Add(new Separator());
                items.Add(Ui.Item("Edit party…", "", () => EditParty_Click(new FrameworkElement { Tag = party }, new RoutedEventArgs())));
            }
        }
        if (items.Count == 0) { e.Handled = true; return; }
        CategoryTree.ContextMenu = Ui.Menu(items.ToArray());
    }

    private List<CaseDocument> DocsForKey(string key)
    {
        if (key == "all") return _docs.ToList();
        if (key == "star") return _docs.Where(d => d.IsImportant).ToList();
        if (key == "recent") return _docs.Where(d => d.ImportedAt >= DateTime.Now.AddDays(-14)).ToList();
        if (key == "attention") return _docs.Where(NeedsAttention).ToList();
        if (key == "noparty") return _docs.Where(d => d.PartyId == null).ToList();
        if (key == "deleted") return _deletedDocs.ToList();
        if (key.StartsWith("cat:")) { var id = long.Parse(key[4..]); return _docs.Where(d => d.CategoryId == id).ToList(); }
        if (key.StartsWith("party:")) { var id = long.Parse(key[6..]); return _docs.Where(d => d.PartyId == id).ToList(); }
        if (key.StartsWith("group:"))
        {
            var ids = _categories.Where(c => c.GroupName == key[6..]).Select(c => c.Id).ToHashSet();
            return _docs.Where(d => ids.Contains(d.CategoryId)).ToList();
        }
        return new List<CaseDocument>();
    }

    private void AddCategoryInGroup(string group)
    {
        var cat = new Category { GroupName = group, IsCustom = true, BundleSection = "F" };
        var dlg = new CategoryEditWindow(cat, _categories.Select(c => c.GroupName).Distinct()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.AddCategory(cat);
        ReloadAll();
    }

    private void EditCategory(Category sel)
    {
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

    private void DeleteCategory(Category sel)
    {
        if (MessageBox.Show(this, $"Delete the category “{sel.DisplayName}”?" +
                (sel.DocumentCount > 0 ? $"\n\nIts {sel.DocumentCount} document(s) will be moved to 'Uncategorised'." : ""),
                "Delete category", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Db.DeleteCategory(sel.Id, UncategorisedId);
        if (_filterKey == $"cat:{sel.Id}") _filterKey = "all";
        ReloadAll();
    }

    // ================================================================== drag and drop
    private void DocGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) != null;
    }

    private void DocGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var ids = SelectedDocs().Select(d => d.Id).ToArray();
        if (ids.Length == 0) return;
        // The ids travel as plain text: .NET 9+ can no longer serialise custom types (BinaryFormatter is gone)
        DragDrop.DoDragDrop(DocGrid, new DataObject(DocDragFormat, string.Join(",", ids)), DragDropEffects.Move);
    }

    private void CategoryTree_DragOver(object sender, DragEventArgs e)
    {
        var key = TreeItemAt(e.OriginalSource as DependencyObject)?.Tag as string;
        bool internalDrag = e.Data.GetDataPresent(DocDragFormat);
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        bool ok = key != null && (internalDrag
            ? key.StartsWith("cat:") || key.StartsWith("party:") || key is "star" or "deleted" or "noparty"
            : files && (key.StartsWith("cat:") || key.StartsWith("party:") || key == "all"));
        e.Effects = ok ? (internalDrag ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        if (TreeItemAt(e.OriginalSource as DependencyObject) is { } item && ok) item.IsSelected = false;
        e.Handled = true;
    }

    private void CategoryTree_Drop(object sender, DragEventArgs e)
    {
        var key = TreeItemAt(e.OriginalSource as DependencyObject)?.Tag as string;
        e.Handled = true;
        if (key == null) return;

        if (e.Data.GetData(DocDragFormat) is string idList)
        {
            var ids = idList.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, out var id) ? id : 0).Where(id => id > 0).ToArray();
            var docs = ids.Select(FindDoc).Where(d => d != null).ToList();
            if (key.StartsWith("cat:")) MoveToCategory(docs, long.Parse(key[4..]));
            else if (key.StartsWith("party:")) SetParty(docs, long.Parse(key[6..]));
            else if (key == "noparty") SetParty(docs, null);
            else if (key == "star") { foreach (var d in docs) Update(d, x => x.IsImportant = true); ReloadKeepingSelection(); }
            else if (key == "deleted")
            {
                foreach (var d in docs) Lib.Delete(d);
                ReloadAll();
            }
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // Files from Windows dropped onto a category or party: the import window starts with that one chosen
            _filterKey = key;
            Window_Drop(this, e);
        }
    }

    // ================================================================== document actions used by menus
    private void Update(CaseDocument d, Action<CaseDocument> change)
    {
        var fresh = Db.GetDocument(d.Id);
        if (fresh == null) return;
        change(fresh);
        Db.UpdateDocument(fresh);
    }

    private void MoveToCategory(IEnumerable<CaseDocument> docs, long categoryId)
    {
        foreach (var d in docs) Update(d, x => x.CategoryId = categoryId);
        ReloadKeepingSelection();
    }

    private void SetParty(IEnumerable<CaseDocument> docs, long? partyId)
    {
        foreach (var d in docs) Update(d, x => x.PartyId = partyId);
        ReloadKeepingSelection();
    }

    private void RenameSelected()
    {
        var d = SelectedDocs().FirstOrDefault();
        if (d == null) return;
        var dlg = new Modules.TextPromptWindow("Rename document", "Title", d.Title) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Update(d, x => x.Title = dlg.Value);
        ReloadKeepingSelection();
    }

    private void AddTag(List<CaseDocument> docs)
    {
        var dlg = new Modules.TextPromptWindow("Add tags", "Tags (separate with commas)", "", "e.g. school, police, contact centre") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        foreach (var d in docs)
            Update(d, x => x.Tags = string.Join(", ", (x.Tags + "," + dlg.Value).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)));
        ReloadKeepingSelection();
    }

    private void RemoveTag(List<CaseDocument> docs, string tag)
    {
        foreach (var d in docs)
            Update(d, x => x.Tags = string.Join(", ", x.Tags.Split(',').Select(t => t.Trim())
                .Where(t => t.Length > 0 && !t.Equals(tag, StringComparison.OrdinalIgnoreCase))));
        ReloadKeepingSelection();
    }

    /// <summary>Gives the selected documents exhibit numbers in date order, continuing from the last one used.</summary>
    private void AssignExhibits(List<CaseDocument> docs)
    {
        var me = _parties.FirstOrDefault(p => p.IsMe);
        var initials = me == null || string.IsNullOrWhiteSpace(me.Name) ? "EX"
            : string.Concat(me.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]))).PadRight(2, 'X')[..2];
        var dlg = new Modules.TextPromptWindow("Exhibit numbers", "Prefix (usually your initials)", Db.GetSetting("exhibit.prefix", initials),
            "Documents are numbered in date order, e.g. AB1, AB2… continuing after the highest number already used.") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var prefix = dlg.Value.Trim().ToUpperInvariant();
        Db.SetSetting("exhibit.prefix", prefix);
        int next = _docs.Select(d => d.ExhibitRef ?? "").Where(r => r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(r => int.TryParse(r[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        foreach (var d in docs.Where(d => string.IsNullOrWhiteSpace(d.ExhibitRef)).OrderBy(d => d.DocumentDate ?? DateTime.MaxValue))
            Update(d, x => x.ExhibitRef = $"{prefix}{next++}");
        ReloadKeepingSelection();
    }

    private void AddDocToTimeline(CaseDocument d)
    {
        var ev = new TimelineEvent { EventDate = d.DocumentDate ?? DateTime.Today, DocumentId = d.Id, Title = d.Title, EventType = "Communication" };
        var dlg = new EventEditWindow(ev, _docs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveEvent(ev);
        RefreshTimelineAndDashboard();
    }

    private void LinkToComplaint(List<CaseDocument> docs, Complaint c)
    {
        var fresh = Db.GetComplaints().FirstOrDefault(x => x.Id == c.Id);
        if (fresh == null) return;
        fresh.DocumentIds = fresh.DocumentIds.Concat(docs.Select(d => d.Id)).Distinct().ToList();
        Db.SaveComplaint(fresh);
        _complaints = Db.GetComplaints();
        RefreshComplaints();
    }

    private void ExportDocsToFolder(List<CaseDocument> docs)
    {
        if (docs.Count == 0 || !ConfirmConfidentiality()) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to save copies into" };
        if (dlg.ShowDialog(this) != true) return;
        string last = null;
        foreach (var d in docs) last = Lib.MakeFriendlyCopy(d, dlg.FolderName);
        Db.Audit("export.copies", "export", null, $"Saved readable copies of {docs.Count} document(s) outside the library",
            string.Join("\n", docs.Take(50).Select(d => d.Title)));
        if (last != null) ShowInExplorer(last);
    }

    private void AnalyseWithAi(IEnumerable<CaseDocument> docs, string reportKey)
    {
        if (!AiService.EnsureEnabled(this)) return;
        var ids = docs.Select(d => d.Id).ToList();
        GoTo("ai");
        Dispatcher.BeginInvoke(() => _aiView?.Preselect(ids, reportKey), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async Task VerifyIntegrityAsync(IEnumerable<CaseDocument> only = null)
    {
        TaskBanner.Visibility = Visibility.Visible;
        TaskBannerText.Text = "Checking document fingerprints…";
        var progress = new Progress<string>(s => TaskBannerText.Text = s);
        try
        {
            LibraryService.IntegrityReport report;
            if (only != null)
            {
                report = new LibraryService.IntegrityReport();
                foreach (var d in only)
                {
                    report.Checked++;
                    var path = Lib.GetFilePath(d);
                    if (!File.Exists(path)) { report.Missing++; report.Problems.Add("Missing: " + d.Title); continue; }
                    var hash = await Task.Run(() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Lib.ReadDocumentBytes(d))));
                    if (!hash.Equals(d.FileHash, StringComparison.OrdinalIgnoreCase)) { report.Changed++; report.Problems.Add("Changed: " + d.Title); }
                }
                Db.Audit("integrity.verify", "document", only.Count() == 1 ? only.First().Id : null,
                    report.Ok ? "Integrity check passed" : "Integrity check found problems", report.Describe());
            }
            else report = await Task.Run(() => Lib.VerifyIntegrity(progress));
            MessageBox.Show(this, report.Describe(), "Document integrity", MessageBoxButton.OK,
                report.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("Integrity", "Integrity check failed", ex);
            MessageBox.Show(this, $"The check couldn't finish: {ex.Message}\nReference: {id}", "Document integrity");
        }
        finally { if (!_ocrRunning) TaskBanner.Visibility = Visibility.Collapsed; }
    }

    // ================================================================== document right-click menu
    private void DocGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-clicking a row that isn't selected selects just that row, as in File Explorer
        if (e.OriginalSource is DependencyObject src && FindParent<DataGridRow>(src) is { Item: CaseDocument rowDoc } && !DocGrid.SelectedItems.Contains(rowDoc))
        {
            DocGrid.SelectedItems.Clear();
            DocGrid.SelectedItem = rowDoc;
        }
        var docs = SelectedDocs();
        bool any = docs.Count > 0, one = docs.Count == 1;

        if (_filterKey == "deleted")
        {
            DocGrid.ContextMenu = Ui.Menu(
                Ui.Item("Restore", "", RestoreSelected, enabled: any, bold: true),
                Ui.Item("Open", "", () => OpenDoc_Click(this, new RoutedEventArgs()), enabled: one),
                new Separator(),
                Ui.Item("Delete permanently…", "", PurgeSelected, "Del", any),
                Ui.Item("Empty Deleted items…", "", EmptyDeletedItems, enabled: _deletedDocs.Count > 0));
            return;
        }
        if (!any)
        {
            DocGrid.ContextMenu = Ui.Menu(
                Ui.Item("Import documents…", "", () => Import_Click(this, new RoutedEventArgs()), "Ctrl+I", bold: true),
                Ui.Item("Select all", "", () => DocGrid.SelectAll(), "Ctrl+A"));
            return;
        }

        var d0 = docs[0];
        var moveTo = _categories.GroupBy(c => c.GroupName).OrderBy(g => g.Key, NaturalComparer.Instance)
            .Select(g => (object)Ui.Sub(g.Key, null, g.OrderBy(c => c.DisplayName, NaturalComparer.Instance)
                .Select(c => (object)Ui.Check(c.DisplayName, one && d0.CategoryId == c.Id, () => MoveToCategory(docs, c.Id))))).ToList();
        moveTo.Insert(0, Ui.Item("Choose from a searchable list…", "", () => EditDoc_Click(this, new RoutedEventArgs())));
        moveTo.Insert(1, new Separator());

        var parties = _parties.Select(p => (object)Ui.Check(p.DisplayName, one && d0.PartyId == p.Id, () => SetParty(docs, p.Id))).ToList();
        parties.Add(new Separator());
        parties.Add(Ui.Item("No party", null, () => SetParty(docs, null)));

        var tags = docs.SelectMany(d => (d.Tags ?? "").Split(',')).Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        var tagItems = new List<object> { Ui.Item("Add tags…", "", () => AddTag(docs)) };
        if (tags.Count > 0)
        {
            tagItems.Add(new Separator());
            tagItems.AddRange(tags.Select(t => (object)Ui.Item($"Remove “{t}”", null, () => RemoveTag(docs, t))));
        }

        var complaints = _complaints.Select(c => (object)Ui.Item(c.Subject, null, () => LinkToComplaint(docs, c))).ToList();
        var ai = AiService.ReportTypes.Select(r => (object)Ui.Item(r.Name, null, () => AnalyseWithAi(docs, r.Key))).ToList();
        // Same engine as Bulk Case Import: suggestions only, each one approved in Import review
        ai.Insert(0, Ui.Item("Organise – suggest a category, dates and records for review…", "", () => OrganiseWithAi(docs), bold: true));
        ai.Insert(1, new Separator());

        DocGrid.ContextMenu = Ui.Menu(
            Ui.Item(one ? "Open" : $"Open ({Math.Min(docs.Count, 5)})", "", () => OpenDoc_Click(this, new RoutedEventArgs()), "Enter", bold: true),
            Ui.Item("Open with default app", "", () => OpenExternal_Click(this, new RoutedEventArgs())),
            Ui.Item(d0.VersionFamily.Length > 0 ? $"Show all versions ({_docs.Count(x => x.VersionFamily == d0.VersionFamily)})" : "Show all versions", "",
                () => ShowVersions(d0), enabled: one && d0.VersionFamily.Length > 0),
            Ui.Item("Mark as the latest version", "", () => MarkLatestVersion(d0), enabled: one && d0.IsOlderVersion),
            new Separator(),
            Ui.Item(one ? "Edit details…" : $"Edit {docs.Count} documents…", "", () => EditDoc_Click(this, new RoutedEventArgs()), "Ctrl+E"),
            Ui.Item("Rename…", "", RenameSelected, "F2", one),
            Ui.Sub("Move to category", "", moveTo),
            Ui.Sub("Filed by / party", "", parties),
            Ui.Sub("Tags", "", tagItems),
            Ui.Item(docs.All(d => d.IsImportant) ? "Unstar" : "Star", "", () => ToggleStar_Click(this, new RoutedEventArgs()), "Ctrl+Shift+S"),
            Ui.Item("Give exhibit numbers…", "", () => AssignExhibits(docs)),
            new Separator(),
            Ui.Item("Add to bundle", "", () => AddToBundle_Click(this, new RoutedEventArgs()), "Ctrl+B"),
            Ui.Item("Add to timeline…", "", () => AddDocToTimeline(d0), enabled: one),
            Ui.Sub("Link to complaint", "", complaints),
            Ui.Sub("Analyse with AI", "", ai),
            new Separator(),
            Ui.Item("Share…", "", () => ShareDoc_Click(this, new RoutedEventArgs())),
            Ui.Item(one ? "Save a copy to…" : "Save copies to a folder…", "", () => ExportDoc_Click(this, new RoutedEventArgs())),
            Ui.Sub("Copy", "", new object[]
            {
                Ui.Item("Title", null, () => Ui.CopyToClipboard(string.Join("\n", docs.Select(d => d.Title)))),
                Ui.Item("Reference (title, date, exhibit)", null, () => Ui.CopyToClipboard(string.Join("\n", docs.Select(d =>
                    $"{d.Title} dated {d.DocumentDateText}{(string.IsNullOrWhiteSpace(d.ExhibitRef) ? "" : $" (exhibit {d.ExhibitRef})")}")))),
                Ui.Item("SHA-256 fingerprint", null, () => Ui.CopyToClipboard(string.Join("\n", docs.Select(d => $"{d.FileHash}  {d.OriginalFileName}"))))
            }),
            new Separator(),
            Ui.Item("Scan text again", "", () => RescanDoc_Click(this, new RoutedEventArgs())),
            Ui.Item("Check it hasn't changed (integrity)", "", async () => await VerifyIntegrityAsync(docs)),
            Ui.Item("History…", "", () => ShowAuditFor("document", d0.Id), enabled: one),
            new Separator(),
            Ui.Item("Delete…", "", () => DeleteDoc_Click(this, new RoutedEventArgs()), "Del"));
    }

    // ================================================================== columns
    private void LoadColumnPreferences()
    {
        var hidden = (Db.GetSetting("docgrid.columns", "") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (hidden.Count == 0) return;
        foreach (var col in DocGrid.Columns.Where(c => c.Header is string h && h.Length > 0 && h != "Deleted"))
            col.Visibility = hidden.Contains("+" + col.Header) ? Visibility.Visible : hidden.Contains("-" + col.Header) ? Visibility.Collapsed : col.Visibility;
    }

    private void SaveColumnPreferences() =>
        Db.SetSetting("docgrid.columns", string.Join("|", DocGrid.Columns.Where(c => c.Header is string h && h.Length > 0 && h != "Deleted")
            .Select(c => (c.Visibility == Visibility.Visible ? "+" : "-") + c.Header)));

    private void DocGridHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.DataGridColumnHeader header) return;
        var items = DocGrid.Columns.Where(c => c.Header is string h && h.Length > 0 && h != "Deleted" && h != "Title")
            .Select(c => (object)Ui.Check(c.Header as string, c.Visibility == Visibility.Visible, () =>
            {
                c.Visibility = c.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                SaveColumnPreferences();
            })).ToList();
        items.Add(new Separator());
        items.Add(Ui.Item("Reset columns", null, () =>
        {
            foreach (var c in DocGrid.Columns.Where(c => c.Header is string))
                c.Visibility = (c.Header as string) is "Exhibit" or "Tags" or "From" or "Type" or "Size" or "Added" or "Deleted" ? Visibility.Collapsed : Visibility.Visible;
            Db.SetSetting("docgrid.columns", "");
        }));
        header.ContextMenu = Ui.Menu(items.ToArray());
    }

    // ================================================================== other lists' right-click menus
    private void TimelineList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var sel = TimelineList.SelectedItems.OfType<TimelineEvent>().ToList();
        var ev = sel.FirstOrDefault();
        TimelineList.ContextMenu = Ui.Menu(
            Ui.Item("Add event…", "", () => AddEvent_Click(this, new RoutedEventArgs()), bold: true),
            Ui.Item(ev?.IsDocumentEntry == true ? "Open document" : "Edit…", "", () => EditEvent_Click(this, new RoutedEventArgs()), "Enter", ev != null),
            Ui.Item("Duplicate…", "", () => DuplicateEvent(ev), enabled: ev is { IsDocumentEntry: false }),
            Ui.Item("Open linked document", "", () => OpenDocumentById(ev?.DocumentId ?? 0), enabled: ev?.DocumentId != null),
            Ui.Item(ev?.IncludeInChronology == false ? "Include in chronology" : "Leave out of chronology", "", () => ToggleChronology(sel),
                enabled: sel.Any(x => !x.IsDocumentEntry)),
            new Separator(),
            Ui.Item("Copy", "", () => Ui.CopyToClipboard(string.Join("\n", sel.Select(x => $"{x.EventDate:dd/MM/yyyy} {x.Time}\t{x.EventType}\t{x.Title}\t{x.Description}")))),
            Ui.Item("Export chronology (PDF)…", "", () => ExportChronology_Click(this, new RoutedEventArgs())),
            Ui.Item("Export as table…", "", async () => await Forms.ExportHelper.ExportTableAsync(this, "Timeline", $"{_events.Count} entries",
                new[] { "Date", "Time", "Type", "What happened", "Details", "Where", "Witnesses", "Reference" },
                _events.OrderBy(x => x.EventDate).Select(x => (IList<string>)new[] { x.EventDate.ToString("dd/MM/yyyy"), x.Time, x.EventType, x.Title, x.Description, x.Location, x.Witnesses, x.Reference }).ToList(),
                landscape: true)),
            Ui.Item("History…", "", () => ShowAuditFor("event", ev?.Id), enabled: ev is { IsDocumentEntry: false }),
            new Separator(),
            Ui.Item("Delete…", "", () => DeleteEvent_Click(this, new RoutedEventArgs()), "Del", ev is { IsDocumentEntry: false }));
    }

    private void TimelineList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EditEvent_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteEvent_Click(this, e); e.Handled = true; }
    }

    private void DuplicateEvent(TimelineEvent ev)
    {
        if (ev == null) return;
        var copy = new TimelineEvent
        {
            EventDate = ev.EventDate, Time = ev.Time, Title = ev.Title, Description = ev.Description, EventType = ev.EventType,
            DocumentId = ev.DocumentId, IncludeInChronology = ev.IncludeInChronology, Location = ev.Location, Witnesses = ev.Witnesses, Reference = ev.Reference
        };
        var dlg = new EventEditWindow(copy, _docs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveEvent(copy);
        RefreshTimelineAndDashboard();
    }

    private void ToggleChronology(List<TimelineEvent> sel)
    {
        bool include = sel.Any(x => !x.IncludeInChronology);
        foreach (var ev in sel.Where(x => !x.IsDocumentEntry))
        {
            var fresh = Db.GetEvents().FirstOrDefault(x => x.Id == ev.Id);
            if (fresh == null) continue;
            fresh.IncludeInChronology = include;
            Db.SaveEvent(fresh);
        }
        RefreshTimelineAndDashboard();
    }

    private void ComplaintGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var c = ComplaintGrid.SelectedItem as Complaint;
        ComplaintGrid.ContextMenu = Ui.Menu(
            Ui.Item("Edit…", "", () => EditComplaint_Click(this, new RoutedEventArgs()), enabled: c != null, bold: true),
            Ui.Item("New complaint…", "", () => AddComplaint_Click(this, new RoutedEventArgs())),
            Ui.Sub("Set status", "", Complaint.Statuses.Select(s => (object)Ui.Item(s, null, () =>
            {
                if (c == null) return;
                var fresh = Db.GetComplaints().FirstOrDefault(x => x.Id == c.Id);
                if (fresh == null) return;
                fresh.Status = s;
                Db.SaveComplaint(fresh);
                _complaints = Db.GetComplaints();
                RefreshComplaints();
                RefreshDashboard();
            }))),
            Ui.Item("Write a letter for this complaint…", "", () => { if (c != null) WriteComplaintLetter(c); }, enabled: c != null),
            Ui.Item("Build a complaint pack…", "", () => ComplaintPack_Click(this, new RoutedEventArgs()), enabled: c != null),
            Ui.Item("History…", "", () => ShowAuditFor("complaint", c?.Id), enabled: c != null),
            new Separator(),
            Ui.Item("Delete…", "", () => DeleteComplaint_Click(this, new RoutedEventArgs()), enabled: c != null));
    }

    private void WriteComplaintLetter(Complaint c)
    {
        var t = Db.All<DocTemplate>().FirstOrDefault(x => x.BuiltInKey == (c.Recipient.Contains("MP") ? "letter.mp" : "letter.complaint"));
        GoTo("letters");
        Dispatcher.BeginInvoke(() => _lettersView?.NewFromTemplate(t, string.IsNullOrWhiteSpace(c.RecipientName) ? c.Recipient : c.RecipientName),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SearchResults_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var h = SearchResults.SelectedItem as SearchHit;
        var d = h == null ? null : FindDoc(h.DocumentId);
        SearchResults.ContextMenu = Ui.Menu(
            Ui.Item(h?.PageNumber > 0 ? $"Open at page {h.PageNumber}" : "Open", "", OpenSelectedSearchHit, "Enter", d != null, bold: true),
            Ui.Item("Show in Documents", "", () => { if (d != null) { SelectFilterAndShow($"cat:{d.CategoryId}"); DocGrid.SelectedItem = d; DocGrid.ScrollIntoView(d); } }, enabled: d != null),
            Ui.Item("Add to bundle", "", () => { if (d != null) AddToBundle(new[] { d }); }, enabled: d != null),
            Ui.Item("Copy snippet", "", () => Ui.CopyToClipboard(h?.Snippet.Replace("«", "").Replace("»", "")), enabled: h != null),
            Ui.Item("Export results…", "", async () =>
            {
                var hits = (SearchResults.ItemsSource as IEnumerable<SearchHit>)?.ToList() ?? new();
                await Forms.ExportHelper.ExportTableAsync(this, $"Search results – {SearchBox.Text}", $"{hits.Count} match(es)",
                    new[] { "Document", "Where", "Date", "Snippet" },
                    hits.Select(x => (IList<string>)new[] { x.DocumentTitle, x.LocationText, x.DocumentDateText, x.Snippet.Replace("«", "").Replace("»", "") }).ToList());
            }));
    }

    private void BundleGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var sel = BundleGrid.SelectedItems.OfType<BundleItem>().ToList();
        var sections = Data.DefaultCategories.BundleSections.Select(kv => (object)Ui.Item($"{kv.Key} – {kv.Value}", null, () =>
        {
            BundleGrid.CommitEdit();
            foreach (var b in sel) b.Section = kv.Key;
            BundleGrid.Items.Refresh();
        }));
        BundleGrid.ContextMenu = Ui.Menu(
            Ui.Item("Open document", "", () => { if (sel.FirstOrDefault() is { } b) OpenDocument(b.Document); }, enabled: sel.Count == 1, bold: true),
            Ui.Sub("Move to section", "", sections),
            Ui.Item("Move up", "", () => BundleUp_Click(this, new RoutedEventArgs()), enabled: sel.Count == 1),
            Ui.Item("Move down", "", () => BundleDown_Click(this, new RoutedEventArgs()), enabled: sel.Count == 1),
            Ui.Item("Sort by section and date", "", () => BundleSort_Click(this, new RoutedEventArgs())),
            new Separator(),
            Ui.Item("Remove from bundle", "", () => BundleRemove_Click(this, new RoutedEventArgs()), enabled: sel.Count > 0));
    }

    private void PartyCard_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Party p) return;
        var holder = new FrameworkElement { Tag = p };
        ((FrameworkElement)sender).ContextMenu = Ui.Menu(
            Ui.Item("Show their documents", "", () => PartyDocuments_Click(holder, new RoutedEventArgs()), bold: true),
            Ui.Item("Edit…", "", () => EditParty_Click(holder, new RoutedEventArgs())),
            Ui.Item("Write to them…", "", () =>
            {
                GoTo("letters");
                Dispatcher.BeginInvoke(() => _lettersView?.NewFromTemplate(Db.All<DocTemplate>().FirstOrDefault(t => t.BuiltInKey == "letter.general"), p.Name),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }),
            Ui.Item("Copy contact details", "", () => Ui.CopyToClipboard($"{p.Name}\n{p.Contact}".Trim())),
            Ui.Item("History…", "", () => ShowAuditFor("party", p.Id)),
            new Separator(),
            Ui.Item("Remove…", "", () => DeleteParty_Click(holder, new RoutedEventArgs())));
    }
}
