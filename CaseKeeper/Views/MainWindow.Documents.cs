using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.Models;
using CaseKeeper.Services;
using Microsoft.Win32;

namespace CaseKeeper.Views;

public partial class MainWindow
{
    /// <summary>Current Documents filter: "all", "star", "cat:{id}", "party:{id}" or "noparty".</summary>
    private string _filterKey = "all";
    private bool _buildingTree;

    // ------------------------------------------------------------------ category / party tree
    private void BuildCategoryTree()
    {
        _buildingTree = true;
        CategoryTree.Items.Clear();
        TreeViewItem toSelect = null;

        TreeViewItem Node(string text, int count, string key, string icon = null, bool bold = false,
            bool muted = false, string badge = null, string tooltip = null)
        {
            var panel = new DockPanel { LastChildFill = true };
            var countText = new TextBlock
            {
                Text = count > 0 ? count.ToString() : "", FontSize = 12,
                Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center
            };
            // Resource references rather than resolved brushes, so these follow a light/dark switch
            countText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            DockPanel.SetDock(countText, Dock.Right);
            panel.Children.Add(countText);

            // "Appl." / "Resp." sits on the right so it survives when a long name is trimmed
            if (badge != null)
            {
                var chipText = new TextBlock { Text = badge, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
                chipText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                var chip = new Border
                {
                    Child = chipText, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                };
                chip.SetResourceReference(Border.BackgroundProperty, "PrimarySoft");
                DockPanel.SetDock(chip, Dock.Right);
                panel.Children.Add(chip);
            }
            if (icon != null)
            {
                var glyph = new TextBlock { Text = icon, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
                glyph.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
                panel.Children.Add(glyph);
            }
            var label = new TextBlock
            {
                Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip ?? text
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
            panel.Children.Add(label);
            var item = new TreeViewItem { Header = panel, Tag = key, Padding = new Thickness(2, 4, 2, 4) };
            if (key != null && key == _filterKey) toSelect = item;
            return item;
        }

        var filter = TreeFilterBox?.Text.Trim() ?? "";
        bool filtering = filter.Length > 0;
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string text) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

        // Folders the documents came from in a Bulk Case Import, so the original structure can still be browsed
        var sourceFolders = _docs.Where(d => d.SourceFolder.Length > 0).GroupBy(SourceTop).OrderBy(g => g.Key, UI.NaturalComparer.Instance).ToList();
        void AddSourceFolders()
        {
            if (sourceFolders.Count == 0 || filtering) return;
            var srcNode = Node("By original folder", 0, null, "", true, tooltip: "Folders the documents came from in a Bulk Case Import");
            srcNode.IsExpanded = _filterKey.StartsWith("src:");
            foreach (var g in sourceFolders) srcNode.Items.Add(Node(g.Key, g.Count(), $"src:{g.Key}"));
            CategoryTree.Items.Add(srcNode);
        }

        if (!filtering)
        {
            CategoryTree.Items.Add(Node("All documents", _docs.Count, "all", "", true));
            CategoryTree.Items.Add(Node("Starred", _docs.Count(d => d.IsImportant), "star", "", true));
            CategoryTree.Items.Add(Node("Recently added", _docs.Count(d => d.ImportedAt >= DateTime.Now.AddDays(-14)), "recent", "", true));
            CategoryTree.Items.Add(Node("Needs attention", _docs.Count(NeedsAttention), "attention", "", true,
                tooltip: "No date, no category, or text scanning failed"));
        }

        var partiesNode = Node("By party", 0, null, "", true);
        partiesNode.IsExpanded = true;
        foreach (var p in _parties.Where(p => !filtering || Matches(p.DisplayName)))
            partiesNode.Items.Add(Node(p.DisplayName, p.DocumentCount, $"party:{p.Id}", muted: p.DocumentCount == 0));
        if (!filtering) partiesNode.Items.Add(Node("No party set", _docs.Count(d => d.PartyId == null), "noparty", muted: true));
        if (partiesNode.Items.Count > 0) CategoryTree.Items.Add(partiesNode);
        AddSourceFolders();

        IEnumerable<Category> cats = _categories;
        if (filtering) cats = cats.Where(c => Matches($"{c.Code} {c.Name} {c.GroupName} {c.Description}"));
        if (_treeHideEmpty && !filtering) cats = cats.Where(c => c.DocumentCount > 0 || $"cat:{c.Id}" == _filterKey);

        if (_treeSort == "flat" || filtering)
        {
            // One alphabetical list: easiest when looking for a form by code or name
            var flat = cats.OrderBy(c => c.DisplayName, UI.NaturalComparer.Instance).ToList();
            var listNode = Node(filtering ? $"Categories matching “{filter}”" : "All categories A–Z", 0, null, "", true);
            listNode.IsExpanded = true;
            foreach (var c in flat)
                listNode.Items.Add(Node(c.DisplayNameWithoutSide, c.DocumentCount, $"cat:{c.Id}", muted: c.DocumentCount == 0,
                    badge: c.SideBadge.Length > 0 ? c.SideBadge : null,
                    tooltip: $"{c.DisplayName}\n{c.GroupName}{(string.IsNullOrWhiteSpace(c.Description) ? "" : "\n" + c.Description)}"));
            if (listNode.Items.Count == 0) listNode.Items.Add(Node("No matching category – try fewer words", 0, null, muted: true));
            CategoryTree.Items.Add(listNode);
        }
        else
        {
            // Grouped: groups A–Z (or busiest first), categories A–Z (or busiest first) inside each
            var groups = cats.GroupBy(c => c.GroupName);
            groups = _treeSort == "count"
                ? groups.OrderByDescending(g => g.Sum(c => c.DocumentCount)).ThenBy(g => g.Key, UI.NaturalComparer.Instance)
                : groups.OrderBy(g => g.Key, UI.NaturalComparer.Instance);
            foreach (var group in groups)
            {
                int total = group.Sum(c => c.DocumentCount);
                var groupNode = Node(group.Key, total, $"group:{group.Key}", null, true);
                var members = _treeSort == "count"
                    ? group.OrderByDescending(c => c.DocumentCount).ThenBy(c => c.DisplayName, UI.NaturalComparer.Instance)
                    : group.OrderBy(c => c.DisplayName, UI.NaturalComparer.Instance);
                foreach (var c in members)
                    groupNode.Items.Add(Node(c.DisplayNameWithoutSide, c.DocumentCount, $"cat:{c.Id}",
                        muted: c.DocumentCount == 0,
                        badge: c.SideBadge.Length > 0 ? c.SideBadge : null,
                        tooltip: $"{c.DisplayName}{(string.IsNullOrWhiteSpace(c.Description) ? "" : "\n" + c.Description)}"));
                var key = group.Key;
                groupNode.IsExpanded = total > 0 || group.Any(c => $"cat:{c.Id}" == _filterKey) || _expandedGroups.Contains(key);
                groupNode.Expanded += (_, _) => _expandedGroups.Add(key);
                groupNode.Collapsed += (_, _) => _expandedGroups.Remove(key);
                CategoryTree.Items.Add(groupNode);
            }
        }

        if (!filtering)
            CategoryTree.Items.Add(Node("Deleted items", _deletedDocs.Count, "deleted", "", true, muted: _deletedDocs.Count == 0,
                tooltip: "Deleted documents are kept here for a while so you can restore them"));

        if (toSelect != null) toSelect.IsSelected = true;
        else if (!filtering && !_filterKey.StartsWith("group:")) _filterKey = "all";
        _buildingTree = false;
    }

    /// <summary>The top-level folder a bulk-imported document came from ("" for ordinary imports).</summary>
    private static string SourceTop(CaseDocument d)
    {
        var folder = d.SourceFolder;
        var i = folder.IndexOfAny(new[] { '\\', '/' });
        return i < 0 ? folder : folder[..i];
    }

    private static bool NeedsAttention(CaseDocument d) =>
        d.DocumentDate == null || d.OcrStatus == OcrStatus.Failed || d.CategoryName == "Uncategorised";

    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_buildingTree) return;
        if (e.NewValue is TreeViewItem { Tag: string key })
        {
            _filterKey = key;
            RefreshDocumentList();
        }
        else if (e.NewValue is TreeViewItem group)
            group.IsExpanded = !group.IsExpanded; // headings without a list of their own just expand/collapse
    }

    private void SelectFilter(string key)
    {
        _filterKey = key;
        BuildCategoryTree();
        RefreshDocumentList();
    }

    // ------------------------------------------------------------------ document list
    private void RefreshDocumentList()
    {
        IEnumerable<CaseDocument> list = _docs;
        string title = "All documents", subtitle = "";
        bool deletedView = _filterKey == "deleted";
        DeletedColumn.Visibility = deletedView ? Visibility.Visible : Visibility.Collapsed;
        if (_filterKey == "star") { list = list.Where(d => d.IsImportant); title = "Starred documents"; }
        else if (_filterKey == "noparty") { list = list.Where(d => d.PartyId == null); title = "Documents with no party set"; }
        else if (_filterKey == "recent") { list = list.Where(d => d.ImportedAt >= DateTime.Now.AddDays(-14)).OrderByDescending(d => d.ImportedAt); title = "Added in the last 14 days"; }
        else if (_filterKey == "attention")
        {
            list = list.Where(NeedsAttention);
            title = "Needs attention";
            subtitle = "No date, no category, or the text couldn't be read";
        }
        else if (deletedView)
        {
            list = _deletedDocs;
            title = "Deleted items";
            var days = Db.GetSetting("bin.days", "30");
            subtitle = $"Kept for {days} days, then removed for good. Right-click to restore";
        }
        else if (_filterKey.StartsWith("group:"))
        {
            var group = _filterKey[6..];
            var ids = _categories.Where(c => c.GroupName == group).Select(c => c.Id).ToHashSet();
            list = list.Where(d => ids.Contains(d.CategoryId));
            title = group;
        }
        else if (_filterKey.StartsWith("cat:"))
        {
            var id = long.Parse(_filterKey[4..]);
            var cat = _categories.FirstOrDefault(c => c.Id == id);
            list = list.Where(d => d.CategoryId == id);
            title = cat?.DisplayName ?? "Category";
            subtitle = cat?.Description ?? "";
        }
        else if (_filterKey.StartsWith("src:"))
        {
            // Documents brought in by Bulk Case Import from one top-level folder of the original
            var folder = _filterKey[4..];
            list = list.Where(d => SourceTop(d) == folder);
            title = "From the folder “" + folder + "”";
            subtitle = "Where these files were in the folder you imported";
        }
        else if (_filterKey.StartsWith("party:"))
        {
            var id = long.Parse(_filterKey[6..]);
            var party = _parties.FirstOrDefault(p => p.Id == id);
            list = list.Where(d => d.PartyId == id);
            title = party?.DisplayName ?? "Party";
            subtitle = string.IsNullOrWhiteSpace(party?.Representative) ? "" : "Represented by " + party.Representative;
        }

        var filter = DocFilterBox.Text.Trim();
        if (filter.Length > 0)
            list = list.Where(d => $"{d.Title} {d.Author} {d.Tags} {d.OriginalFileName} {d.CategoryName} {d.PartyName} {d.Description} {d.SourcePath}"
                .Contains(filter, StringComparison.OrdinalIgnoreCase));

        var items = list.ToList();
        DocGrid.ItemsSource = items;
        DocListTitle.Text = title;
        DocListSubtitle.Text = $"{items.Count} document(s)" + (subtitle.Length > 0 ? " · " + subtitle : "");
        EmptyDocsPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailsPanel();
    }

    private void DocFilterBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDocumentList();

    private void DocGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetailsPanel();

    private void UpdateDetailsPanel()
    {
        var doc = DocGrid.SelectedItem as CaseDocument;
        DetailsPanel.DataContext = doc;
        DetailsPanel.Visibility = doc == null ? Visibility.Collapsed : Visibility.Visible;
        NoSelectionText.Visibility = doc == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<CaseDocument> SelectedDocs() => DocGrid.SelectedItems.Cast<CaseDocument>().ToList();

    private void DocGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindParent<DataGridRow>(src) != null && DocGrid.SelectedItem is CaseDocument d)
            OpenDocument(d);
    }

    private void DocGrid_KeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Enter) { OpenDoc_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteDoc_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F2) { RenameSelected(); e.Handled = true; }
        else if (e.Key == Key.E && mods == ModifierKeys.Control) { EditDoc_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.B && mods == ModifierKeys.Control) { AddToBundle_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift)) { ToggleStar_Click(sender, e); e.Handled = true; }
    }

    private static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ------------------------------------------------------------------ document actions
    private void OpenDoc_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in SelectedDocs().Take(5)) OpenDocument(d);
    }

    private void EditDoc_Click(object sender, RoutedEventArgs e)
    {
        var docs = SelectedDocs();
        if (docs.Count == 0) return;
        if (docs.Count > 1)
        {
            // Bulk change: category and/or party for many documents at once
            var bulk = new BulkEditWindow(docs.Count, _categories, _parties) { Owner = this };
            if (bulk.ShowDialog() != true) return;
            foreach (var d in docs)
            {
                var fresh = Db.GetDocument(d.Id);
                if (bulk.CategoryId.HasValue) fresh.CategoryId = bulk.CategoryId.Value;
                if (bulk.ChangeParty) fresh.PartyId = bulk.PartyId;
                if (!string.IsNullOrWhiteSpace(bulk.AddTags))
                    fresh.Tags = string.Join(", ", (fresh.Tags + "," + bulk.AddTags).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
                Db.UpdateDocument(fresh);
            }
            ReloadKeepingSelection();
            return;
        }
        var doc = Db.GetDocument(docs[0].Id);
        var dlg = new DocumentEditWindow(doc, _categories, _parties) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Db.UpdateDocument(doc);
            ReloadKeepingSelection();
        }
    }

    private void ToggleStar_Click(object sender, RoutedEventArgs e)
    {
        var docs = SelectedDocs();
        if (docs.Count == 0) return;
        bool newValue = !docs.All(d => d.IsImportant);
        foreach (var d in docs)
        {
            var fresh = Db.GetDocument(d.Id);
            fresh.IsImportant = newValue;
            Db.UpdateDocument(fresh);
        }
        ReloadKeepingSelection();
    }

    private async void ShareDoc_Click(object sender, RoutedEventArgs e)
    {
        var docs = SelectedDocs();
        if (docs.Count == 0 || !ConfirmConfidentiality()) return;
        try
        {
            var paths = docs.Select(d => Lib.MakeFriendlyCopy(d)).ToList();
            await ShareService.ShareFilesAsync(this, docs.Count == 1 ? docs[0].Title : $"{docs.Count} documents", paths);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open the Windows Share window: " + ex.Message, "Share");
        }
    }

    private void ExportDoc_Click(object sender, RoutedEventArgs e)
    {
        var docs = SelectedDocs();
        if (docs.Count == 0 || !ConfirmConfidentiality()) return;
        if (docs.Count == 1)
        {
            var d = docs[0];
            var dlg = new SaveFileDialog
            {
                FileName = LibraryService.SafeFileName(d.Title) + d.FileExtension,
                Filter = $"{d.FileTypeLabel} file|*{d.FileExtension}|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            File.Copy(Lib.GetFilePath(d), dlg.FileName, true);
            ShowInExplorer(dlg.FileName);
        }
        else
        {
            var dlg = new OpenFolderDialog { Title = "Choose a folder to save copies into" };
            if (dlg.ShowDialog(this) != true) return;
            string last = null;
            foreach (var d in docs) last = Lib.MakeFriendlyCopy(d, dlg.FolderName);
            if (last != null) ShowInExplorer(last);
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        // Opens a temporary copy so the library's copy can never be changed by another program
        foreach (var d in SelectedDocs().Take(5)) OpenWithShell(Lib.MakeFriendlyCopy(d));
    }

    private void AddToBundle_Click(object sender, RoutedEventArgs e)
    {
        AddToBundle(SelectedDocs());
        Nav.SelectedItem = BundleTab;
    }

    private void RescanDoc_Click(object sender, RoutedEventArgs e) => EnqueueOcr(SelectedDocs());

    private void DeleteDoc_Click(object sender, RoutedEventArgs e)
    {
        var docs = SelectedDocs();
        if (docs.Count == 0) return;
        if (_filterKey == "deleted") { PurgeSelected(); return; }
        var what = docs.Count == 1 ? $"“{docs[0].Title}”" : $"these {docs.Count} documents";
        var days = Db.GetSetting("bin.days", "30");
        if (MessageBox.Show(this, $"Move {what} to Deleted items?\n\nYou can restore {(docs.Count == 1 ? "it" : "them")} from there for {days} days. " +
                "Your original file (wherever you imported it from) is not affected.", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        foreach (var d in docs) Lib.Delete(d);
        foreach (var item in _bundleItems.Where(b => docs.Any(d => d.Id == b.Document.Id)).ToList()) _bundleItems.Remove(item);
        ReloadAll();
    }

    private void RestoreSelected()
    {
        var docs = SelectedDocs();
        foreach (var d in docs) Lib.Restore(d);
        ReloadAll();
    }

    private void PurgeSelected()
    {
        var docs = SelectedDocs();
        if (docs.Count == 0) return;
        if (MessageBox.Show(this, $"Permanently delete {(docs.Count == 1 ? $"“{docs[0].Title}”" : $"these {docs.Count} documents")}?\n\n" +
                "Its copy, notes and scanned text are removed for good. This can't be undone. (The original file you imported from is not affected.)",
                "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var d in docs) Lib.Purge(d);
        ReloadAll();
    }

    private void EmptyDeletedItems()
    {
        if (_deletedDocs.Count == 0) return;
        if (MessageBox.Show(this, $"Permanently delete all {_deletedDocs.Count} document(s) in Deleted items?\n\nThis can't be undone.",
                "Empty Deleted items", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var d in _deletedDocs.ToList()) Lib.Purge(d);
        ReloadAll();
    }

    // ------------------------------------------------------------------ categories
    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var cat = new Category { GroupName = "My categories", IsCustom = true, BundleSection = "F" };
        var dlg = new CategoryEditWindow(cat, _categories.Select(c => c.GroupName).Distinct()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.AddCategory(cat);
        ReloadAll();
    }

    // ------------------------------------------------------------------ parties
    private void RefreshParties()
    {
        var applicants = _parties.Where(p => p.RoleGroup == "Applicant").ToList();
        var respondents = _parties.Where(p => p.RoleGroup == "Respondent").ToList();
        var others = _parties.Where(p => p.RoleGroup == "Other").ToList();
        ApplicantsList.ItemsSource = applicants;
        RespondentsList.ItemsSource = respondents;
        OthersList.ItemsSource = others;
        NoApplicantsText.Visibility = applicants.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRespondentsText.Visibility = respondents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoOthersText.Visibility = others.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoFillBundleParties();
    }

    private void AddParty_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string ?? "Applicant";
        var role = tag switch
        {
            "Applicant" => _parties.Any(p => p.RoleGroup == "Applicant") ? "Second Applicant" : "Applicant",
            "Respondent" => _parties.Count(p => p.RoleGroup == "Respondent") switch { 0 => "Respondent", 1 => "Second Respondent", _ => "Third Respondent" },
            _ => "Child (subject of proceedings)"
        };
        var party = new Party { Role = role };
        var dlg = new PartyEditWindow(party) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveParty(party);
        ReloadAll();
    }

    private void EditParty_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Party p) return;
        var copy = new Party
        {
            Id = p.Id, Role = p.Role, Name = p.Name, IsMe = p.IsMe, Representative = p.Representative, Contact = p.Contact, Notes = p.Notes,
            DateOfBirth = p.DateOfBirth
        };
        var dlg = new PartyEditWindow(copy) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveParty(copy);
        ReloadAll();
    }

    private void DeleteParty_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Party p) return;
        if (MessageBox.Show(this, $"Remove {p.DisplayName}?\n\nTheir documents stay in the library; they just won't be linked to a party any more.",
                "Remove party", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Db.DeleteParty(p.Id);
        if (_filterKey == $"party:{p.Id}") _filterKey = "all";
        ReloadAll();
    }

    private void PartyDocuments_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Party p) return;
        DocFilterBox.Text = "";
        SelectFilter($"party:{p.Id}");
        Nav.SelectedItem = DocumentsTab;
    }
}
