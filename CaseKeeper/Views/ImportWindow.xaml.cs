using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using CaseKeeper.Models;

namespace CaseKeeper.Views;

public class ImportRow : Observable
{
    public string SourcePath { get; set; }
    public string FileName => Path.GetFileName(SourcePath);

    private string _title;
    public string Title { get => _title; set => Set(ref _title, value); }

    private long _categoryId;
    public long CategoryId { get => _categoryId; set => Set(ref _categoryId, value); }

    private long _partyId;
    public long PartyId { get => _partyId; set => Set(ref _partyId, value); }

    private DateTime? _documentDate;
    public DateTime? DocumentDate { get => _documentDate; set => Set(ref _documentDate, value); }

    public DateTime? DateReceived { get; set; } = DateTime.Today;

    private string _author = "";
    public string Author { get => _author; set => Set(ref _author, value); }
}

public partial class ImportWindow : Window
{
    public List<Category> Categories { get; }
    public List<Party> Parties { get; }
    public ObservableCollection<ImportRow> Rows { get; }

    public ImportWindow(string[] paths, List<Category> categories, List<Party> parties, long defaultCategory, long? defaultParty)
    {
        // Copies, so grouping and filtering here never affects lists elsewhere in the app
        Categories = categories.ToList();
        Parties = new List<Party> { new() { Id = 0, Role = "— None —" } };
        Parties.AddRange(parties);
        InitializeComponent();

        // Hundreds of categories: show them under group headings in every dropdown, and let the
        // "apply to all" box be searched by typing any word or form code
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Categories);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("GroupName"));
        AllCategory.ItemsSource = categories.ToList();
        UI.SearchableCombo.Attach(AllCategory, o => (o as Category)?.DisplayName ?? "");
        UI.SearchableCombo.GroupCategories(AllCategory);

        Rows = new ObservableCollection<ImportRow>(paths.Select(p =>
        {
            var name = Path.GetFileNameWithoutExtension(p);
            return new ImportRow
            {
                SourcePath = p,
                Title = CleanTitle(name),
                CategoryId = GuessCategory(name, categories) ?? defaultCategory,
                PartyId = defaultParty ?? 0,
                DocumentDate = GuessDate(name)
            };
        }));
        Rows.CollectionChanged += (_, _) => UpdateCount();
        Grid.ItemsSource = Rows;
        AllCategory.SelectedItem = ((IEnumerable<Category>)AllCategory.ItemsSource).FirstOrDefault(c => c.Id == defaultCategory);
        AllParty.SelectedIndex = 0;
        UpdateCount();
    }

    private void UpdateCount()
    {
        HeaderText.Text = Rows.Count == 1 ? "Add 1 document" : $"Add {Rows.Count} documents";
        CountText.Text = Rows.Count == 0
            ? "Nothing left to add."
            : $"{Rows.Count} file(s) ready to add.";
        AddButton.IsEnabled = Rows.Count > 0;
        EmptyPanel.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string CleanTitle(string name) => Services.BulkImport.CategoryMatcher.CleanTitle(name);

    /// <summary>Guesses the category from form codes or category names in the file name (shared with Bulk Case Import).</summary>
    public static long? GuessCategory(string fileName, List<Category> categories) =>
        Services.BulkImport.CategoryMatcher.GuessFromName(fileName, categories);

    /// <summary>Finds a date in a file name (UK day-first order).</summary>
    public static DateTime? GuessDate(string fileName) => Services.BulkImport.CategoryMatcher.GuessDate(fileName);

    // ------------------------------------------------------------------ removing files from the list
    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ImportRow row) Rows.Remove(row);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = Grid.SelectedItems.Cast<ImportRow>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Click a row (or Ctrl-click several) first, then press Remove selected.", "Remove");
            return;
        }
        foreach (var row in selected) Rows.Remove(row);
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        // Only when not typing in one of the row's boxes
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            RemoveSelected_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ApplyCategory_Click(object sender, RoutedEventArgs e)
    {
        if (AllCategory.SelectedItem is Category c) foreach (var r in Rows) r.CategoryId = c.Id;
    }

    private void ApplyParty_Click(object sender, RoutedEventArgs e)
    {
        if (AllParty.SelectedItem is Party p) foreach (var r in Rows) r.PartyId = p.Id;
    }

    private void ApplyDate_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in Rows) r.DocumentDate = AllDate.SelectedDate;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.Count == 0) return;
        foreach (var r in Rows.Where(r => string.IsNullOrWhiteSpace(r.Title))) r.Title = Path.GetFileNameWithoutExtension(r.SourcePath);
        DialogResult = true;
    }
}
