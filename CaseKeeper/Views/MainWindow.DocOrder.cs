using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;

namespace CaseKeeper.Views;

/// <summary>
/// The Documents list in a usable order: what each file is (court papers first), the latest version of each document
/// on top with its older versions folded under "+N older versions", and a view of every version of one document.
/// See <see cref="DocumentOrganiser"/>. Nothing here changes a document, except "Mark as the latest version", which
/// sets the same tags as Import review's "Duplicates &amp; versions".
/// </summary>
public partial class MainWindow
{
    private bool _arrangeReady;
    private List<IReadOnlyList<long>> _contentGroups;
    private string _contentSignature = "", _contentRunning = "";

    private bool LatestOnly => LatestOnlyBox.IsChecked == true;
    private string Arrangement => (ArrangeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? DocumentOrganiser.ArrangeImportance;

    /// <summary>Loads the saved choices once, and sets up the headings ("Court papers &amp; orders  12").</summary>
    private void EnsureDocArrange()
    {
        if (_arrangeReady) return;
        _arrangeReady = true;
        LatestOnlyBox.IsChecked = Db.GetSetting(DocumentOrganiser.LatestOnlySetting, "1") != "0";
        var saved = Db.GetSetting(DocumentOrganiser.ArrangeSetting, DocumentOrganiser.ArrangeImportance);
        ArrangeBox.SelectedItem = ArrangeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == saved) ?? ArrangeBox.Items[0];
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(DocGrid, true);
        DocGrid.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = (DataTemplate)XamlReader.Parse(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "<Border Padding='8,12,0,5'><TextBlock FontWeight='SemiBold' FontSize='12.5' Foreground='{DynamicResource TextBrush}'>" +
                "<Run Text='{Binding Name, Mode=OneWay}'/><Run Text='   '/><Run Text='{Binding ItemCount, Mode=OneWay}' Foreground='{DynamicResource MutedBrush}'/>" +
                "</TextBlock></Border></DataTemplate>")
        });
        // Clicking a column header sorts inside each heading, so the most important papers stay on top
        DocGrid.Sorting += (_, e) =>
        {
            if (DocGrid.ItemsSource is not ListCollectionView { GroupDescriptions.Count: > 0 } view) return;
            e.Handled = true;
            var dir = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
                ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
            foreach (var c in DocGrid.Columns) if (c != e.Column) c.SortDirection = null;
            e.Column.SortDirection = dir;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(CaseDocument.ImportanceRank), System.ComponentModel.ListSortDirection.Ascending));
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(e.Column.SortMemberPath, dir));
            }
        };
    }

    private void DocArrange_Changed(object sender, RoutedEventArgs e)
    {
        if (!_arrangeReady || !IsLoaded) return;
        Db.SetSetting(DocumentOrganiser.LatestOnlySetting, LatestOnly ? "1" : "0");
        Db.SetSetting(DocumentOrganiser.ArrangeSetting, Arrangement);
        RefreshDocumentList();
    }

    /// <summary>
    /// The documents to show, in order. Works out kinds and versions over the whole library (so a version filed in
    /// another category still counts), then applies "latest only" and the chosen order to this view's documents.
    /// </summary>
    private List<CaseDocument> ArrangeDocuments(List<CaseDocument> items, out int folded)
    {
        folded = 0;
        EnsureDocArrange();
        try
        {
            DocumentOrganiser.Organise(_docs, _categories.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First()), _contentGroups);
            _ = EnsureContentGroupsAsync();
            if (_filterKey.StartsWith("family:", StringComparison.Ordinal))
                return items.OrderBy(d => d.VersionIndex).ToList();
            var arranged = DocumentOrganiser.Arrange(items, LatestOnly, Arrangement);
            folded = items.Count - arranged.Count;
            return arranged;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Documents", "Couldn't arrange the document list; showing it as stored", ex);
            return items;
        }
    }

    /// <summary>Shows the list, with headings by kind when ordered by importance.</summary>
    private void ShowDocuments(List<CaseDocument> items)
    {
        var view = new ListCollectionView(items);
        if (Arrangement == DocumentOrganiser.ArrangeImportance && !_filterKey.StartsWith("family:", StringComparison.Ordinal) && _filterKey != "deleted")
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CaseDocument.ImportanceText)));
        foreach (var c in DocGrid.Columns) c.SortDirection = null;
        DocGrid.ItemsSource = view;
    }

    /// <summary>
    /// The version groups found by comparing the documents' words (the same check as Import review's "Duplicates &amp;
    /// versions"), worked out in the background when the library changes. Groups the person said aren't related are left out.
    /// </summary>
    private async Task EnsureContentGroupsAsync()
    {
        var docs = _docs.ToList();
        var signature = $"{docs.Count}|{(docs.Count == 0 ? 0 : docs.Max(d => d.Id))}|{docs.Sum(d => (long)d.PageCount)}|{docs.Sum(d => d.FileHash?.Length ?? 0)}";
        if (signature == _contentSignature || signature == _contentRunning || docs.Count < 2) return;
        _contentRunning = signature;
        try
        {
            var db = Db;
            var groups = await Task.Run(() =>
            {
                var inputs = docs.Select(d => new VersionInput(d.Id, d.Title,
                    string.Join("\n", db.GetAllPageText(d.Id).OrderBy(p => p.Key).Select(p => p.Value)),
                    d.DocumentDate, d.SourceModifiedAt, d.FileHash, d.SavedAt, d.Revision, d.ImportedAt)).ToList();
                return VersionDetector.Find(inputs);
            });
            Dictionary<string, string> decisions;
            try { decisions = JsonSerializer.Deserialize<Dictionary<string, string>>(Db.GetSetting("versions.decisions", "{}") ?? "{}") ?? new(); }
            catch (JsonException) { decisions = new(); }
            _contentGroups = groups.Where(g => !decisions.TryGetValue(g.Key, out var d) || d is "older" or "linked")
                .Select(g => (IReadOnlyList<long>)g.Members.Select(m => m.DocumentId).ToList()).ToList();
            _contentSignature = signature;
            AppLog.Info("Documents", $"Versions by wording: {_contentGroups.Count} group(s) among {docs.Count} documents");
            if (_contentGroups.Count > 0) RefreshDocumentList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Documents", "The version check for the document list failed; versions are matched by title only", ex);
        }
        finally { if (_contentRunning == signature) _contentRunning = ""; }
    }

    private void VersionBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { DataContext: CaseDocument d } && d.VersionFamily.Length > 0) ShowVersions(d);
    }

    /// <summary>Every version of one document, latest first.</summary>
    private void ShowVersions(CaseDocument d)
    {
        var members = _docs.Where(x => x.VersionFamily == d.VersionFamily).ToList();
        _setIds = members.Select(x => x.Id).ToHashSet();
        var latest = members.FirstOrDefault(x => !x.IsOlderVersion) ?? d;
        _setTitle = $"Versions of “{latest.Title}”";
        SelectFilter("family:" + d.VersionFamily);
    }

    /// <summary>The person's choice of which copy is the latest; the same tags as Import review's "Duplicates &amp; versions".</summary>
    private void MarkLatestVersion(CaseDocument d)
    {
        var members = _docs.Where(x => x.VersionFamily == d.VersionFamily && x.VersionFamily.Length > 0).ToList();
        if (members.Count < 2) return;
        foreach (var m in members)
        {
            var tags = (m.Tags ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0 &&
                !t.Equals(DocumentOrganiser.LatestTag, StringComparison.OrdinalIgnoreCase) && !t.Equals(DocumentOrganiser.OlderTag, StringComparison.OrdinalIgnoreCase)).ToList();
            tags.Add(m.Id == d.Id ? DocumentOrganiser.LatestTag : DocumentOrganiser.OlderTag);
            var text = string.Join(", ", tags);
            if (text == m.Tags) continue;
            m.Tags = text;
            Db.UpdateDocument(m);
        }
        Db.Audit("document.versions", "document", d.Id, $"Marked “{d.Title}” as the latest version, {members.Count - 1} older",
            string.Join("\n", members.Select(m => $"#{m.Id} {m.Title}")));
        RefreshAll();
    }
}
