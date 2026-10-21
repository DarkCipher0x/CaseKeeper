using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Models;
using CaseKeeper.Services;
using Microsoft.Win32;

namespace CaseKeeper.Views;

/// <summary>Search, Timeline and Complaints tabs.</summary>
public partial class MainWindow
{
    // ================================================================== SEARCH
    private void RefreshSearchCategories()
    {
        var selected = (SearchCategory.SelectedItem as Category)?.Id ?? 0;
        var items = new List<Category> { new() { Id = 0, Name = "All categories" } };
        items.AddRange(_categories.Where(c => c.DocumentCount > 0));
        SearchCategory.ItemsSource = items;
        SearchCategory.SelectedItem = items.FirstOrDefault(c => c.Id == selected) ?? items[0];
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunSearch();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void SearchFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && SearchBox.Text.Trim().Length > 0) RunSearch();
    }

    private void RunSearch()
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            SearchResults.ItemsSource = null;
            SearchSummary.Text = "";
            return;
        }
        var docs = _docs.ToDictionary(d => d.Id);
        var catId = (SearchCategory.SelectedItem as Category)?.Id ?? 0;
        DateTime? from = SearchFrom.SelectedDate, to = SearchTo.SelectedDate;
        bool notesOnly = SearchNotesOnly.IsChecked == true;

        var hits = Db.Search(text)
            .Where(h => docs.ContainsKey(h.DocumentId))
            .Where(h => !notesOnly || h.Source is "notes" or "pagenote")
            .Where(h =>
            {
                var d = docs[h.DocumentId];
                if (catId != 0 && d.CategoryId != catId) return false;
                if (from.HasValue && (d.DocumentDate == null || d.DocumentDate < from)) return false;
                if (to.HasValue && (d.DocumentDate == null || d.DocumentDate > to)) return false;
                return true;
            })
            .ToList();
        foreach (var h in hits)
        {
            var d = docs[h.DocumentId];
            h.DocumentTitle = d.Title;
            h.CategoryName = d.CategoryName + (string.IsNullOrEmpty(d.PartyName) ? "" : " · " + d.PartyName);
            h.DocumentDateText = d.DocumentDateText;
        }
        SearchResults.ItemsSource = hits;
        int docCount = hits.Select(h => h.DocumentId).Distinct().Count();
        int pending = _docs.Count(d => d.OcrStatus is OcrStatus.Pending or OcrStatus.Running);
        SearchSummary.Text = (hits.Count == 0 ? "No matches found." : $"{hits.Count} match(es) in {docCount} document(s). Double-click a result to open it at that page.")
                             + (pending > 0 ? $"  ({pending} document(s) are still being scanned, so try again shortly for complete results.)" : "");
    }

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedSearchHit();

    private void SearchResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelectedSearchHit();
    }

    private void OpenSelectedSearchHit()
    {
        if (SearchResults.SelectedItem is SearchHit h && FindDoc(h.DocumentId) is { } d)
            OpenDocument(d, Math.Max(1, h.PageNumber));
    }

    // ================================================================== TIMELINE
    private void InitTimelineFilters()
    {
        TimelineTypeFilter.ItemsSource = new[] { "All event types" }.Concat(TimelineEvent.Types).ToList();
        TimelineTypeFilter.SelectedIndex = 0;
    }

    private void TimelineFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshTimeline();
    }

    private void RefreshTimeline()
    {
        var docs = _docs.ToDictionary(d => d.Id);
        foreach (var ev in _events)
            ev.LinkedDocumentTitle = ev.DocumentId.HasValue && docs.TryGetValue(ev.DocumentId.Value, out var d) ? d.Title : "";

        var type = TimelineTypeFilter.SelectedIndex > 0 ? TimelineTypeFilter.SelectedItem as string : null;
        IEnumerable<TimelineEvent> items = _events.Where(e => type == null || e.EventType == type);
        if (TimelineShowDocs.IsChecked == true && type == null)
            items = items.Concat(_docs.Where(d => d.DocumentDate.HasValue).Select(d => new TimelineEvent
            {
                EventDate = d.DocumentDate.Value, Title = d.Title, EventType = d.CategoryName,
                Description = string.IsNullOrWhiteSpace(d.PartyName) ? d.Author : d.PartyName,
                DocumentId = d.Id, IsDocumentEntry = true
            }));
        if (TimelineUpcomingOnly.IsChecked == true) items = items.Where(e => e.IsUpcoming);

        var list = items.OrderBy(e => e.EventDate).ThenBy(e => e.Time).ThenBy(e => e.IsDocumentEntry).ToList();
        TimelineList.ItemsSource = list;
        NoEventsText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var firstUpcoming = list.FirstOrDefault(e => e.IsUpcoming);
        if (firstUpcoming != null) TimelineList.ScrollIntoView(firstUpcoming);
    }

    private void AddEvent_Click(object sender, RoutedEventArgs e)
    {
        var ev = new TimelineEvent { EventDate = DateTime.Today };
        var dlg = new EventEditWindow(ev, _docs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveEvent(ev);
        _events = Db.GetEvents();
        RefreshTimeline();
        RefreshDashboard();
    }

    private void EditEvent_Click(object sender, RoutedEventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineEvent sel) return;
        if (sel.IsDocumentEntry) { OpenDocument(FindDoc(sel.DocumentId ?? 0)); return; }
        var copy = new TimelineEvent
        {
            Id = sel.Id, EventDate = sel.EventDate, Time = sel.Time, Title = sel.Title, Description = sel.Description,
            EventType = sel.EventType, DocumentId = sel.DocumentId, IncludeInChronology = sel.IncludeInChronology,
            Location = sel.Location, Witnesses = sel.Witnesses, Reference = sel.Reference
        };
        var dlg = new EventEditWindow(copy, _docs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveEvent(copy);
        _events = Db.GetEvents();
        RefreshTimeline();
        RefreshDashboard();
    }

    private void DeleteEvent_Click(object sender, RoutedEventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineEvent { IsDocumentEntry: false } sel) return;
        if (MessageBox.Show(this, $"Delete the event “{sel.Title}”?", "Delete event", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Db.DeleteEvent(sel.Id);
        _events = Db.GetEvents();
        RefreshTimeline();
        RefreshDashboard();
    }

    private void TimelineList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditEvent_Click(sender, e);

    private void TimelineDocLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.Tag is TimelineEvent { DocumentId: long id }) OpenDocument(FindDoc(id));
    }

    private async void ExportChronology_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"Chronology {DateTime.Today:yyyy-MM-dd}.pdf", Filter = "PDF|*.pdf", InitialDirectory = Lib.ExportsFolder
        };
        if (dlg.ShowDialog(this) != true) return;
        var options = new BundleOptions
        {
            SummaryMode = true, IncludeCover = false, IncludeChronology = true, Title = "Chronology",
            CaseNumber = BundleCaseNo.Text, ConfidentialFooter = true, StampPages = true, Events = _events
        };
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            await new BundleService(Lib).BuildAsync(options, dlg.FileName, null);
            Mouse.OverrideCursor = null;
            OpenWithShell(dlg.FileName);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, "Couldn't create the chronology: " + ex.Message, "Chronology");
        }
    }

    // ================================================================== COMPLAINTS
    private void RefreshComplaints()
    {
        var selectedId = (ComplaintGrid.SelectedItem as Complaint)?.Id;
        ComplaintGrid.ItemsSource = _complaints;
        NoComplaintsGridText.Visibility = _complaints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ComplaintGrid.SelectedItem = _complaints.FirstOrDefault(c => c.Id == selectedId);
        UpdateComplaintDetail();
    }

    private void ComplaintGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateComplaintDetail();

    private void UpdateComplaintDetail()
    {
        var c = ComplaintGrid.SelectedItem as Complaint;
        ComplaintDetail.DataContext = c;
        ComplaintDetail.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        ComplaintDocsList.ItemsSource = c == null ? null : c.DocumentIds.Select(FindDoc).Where(d => d != null).ToList();
    }

    private void AddComplaint_Click(object sender, RoutedEventArgs e)
    {
        var c = new Complaint { Recipient = Complaint.Recipients[0], Status = "Draft" };
        var dlg = new ComplaintEditWindow(c, _docs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveComplaint(c);
        _complaints = Db.GetComplaints();
        RefreshComplaints();
        RefreshDashboard();
    }

    private void EditComplaint_Click(object sender, RoutedEventArgs e)
    {
        if (ComplaintGrid.SelectedItem is not Complaint sel) return;
        var copy = new Complaint
        {
            Id = sel.Id, Recipient = sel.Recipient, RecipientName = sel.RecipientName, Reference = sel.Reference, Subject = sel.Subject,
            Summary = sel.Summary, DateSent = sel.DateSent, ResponseDue = sel.ResponseDue, Status = sel.Status, Outcome = sel.Outcome,
            DocumentIds = sel.DocumentIds.ToList()
        };
        var dlg = new ComplaintEditWindow(copy, _docs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Db.SaveComplaint(copy);
        _complaints = Db.GetComplaints();
        RefreshComplaints();
        RefreshDashboard();
    }

    private void ComplaintGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindParent<DataGridRow>(src) != null) EditComplaint_Click(sender, e);
    }

    private void DeleteComplaint_Click(object sender, RoutedEventArgs e)
    {
        if (ComplaintGrid.SelectedItem is not Complaint sel) return;
        if (MessageBox.Show(this, $"Delete the complaint record “{sel.Subject}”?\n\nLinked documents are not deleted.", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Db.DeleteComplaint(sel.Id);
        _complaints = Db.GetComplaints();
        RefreshComplaints();
        RefreshDashboard();
    }

    private void ComplaintDocLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.Tag is CaseDocument d) OpenDocument(d);
    }

    /// <summary>Loads the complaint's linked documents and summary into the Bundles tab as a summary report.</summary>
    private void ComplaintPack_Click(object sender, RoutedEventArgs e)
    {
        if (ComplaintGrid.SelectedItem is not Complaint c)
        {
            MessageBox.Show(this, "Select a complaint first.", "Complaint pack");
            return;
        }
        _bundleItems.Clear();
        AddToBundle(c.DocumentIds.Select(FindDoc).Where(d => d != null));
        ModeSummary.IsChecked = true;
        BundleTitle.Text = string.IsNullOrWhiteSpace(c.Subject) ? $"Complaint to {c.Recipient}" : c.Subject;
        var intro = new List<string>();
        intro.Add($"To: {c.Recipient}{(string.IsNullOrWhiteSpace(c.RecipientName) ? "" : " – " + c.RecipientName)}");
        if (!string.IsNullOrWhiteSpace(c.Reference)) intro.Add($"Reference: {c.Reference}");
        if (c.DateSent.HasValue) intro.Add($"Date: {c.DateSent:dd MMMM yyyy}");
        intro.Add("");
        intro.Add(c.Summary);
        BundleIntro.Text = string.Join(Environment.NewLine, intro);
        OptChronology.IsChecked = true;
        BundleStatus.Text = $"Loaded {_bundleItems.Count} linked document(s). A summary report lists them with your notes; switch to 'Court bundle' to attach the full documents.";
        Nav.SelectedItem = BundleTab;
    }
}
