using System.Windows;
using CaseKeeper.Models;

namespace CaseKeeper.Views;

/// <summary>Edits a document's details in place; the caller saves it if this returns true.</summary>
public partial class DocumentEditWindow : Window
{
    private readonly CaseDocument _doc;

    public DocumentEditWindow(CaseDocument doc, List<Category> categories, List<Party> parties)
    {
        InitializeComponent();
        _doc = doc;
        DataContext = doc;
        // Its own copy of the list, so filtering as you type doesn't affect anything else
        var cats = categories.ToList();
        CategoryBox.ItemsSource = cats;
        UI.SearchableCombo.Attach(CategoryBox, o => (o as Category) is { } c ? $"{c.DisplayName} {c.GroupName} {c.Description}" : "");
        UI.SearchableCombo.GroupCategories(CategoryBox);
        CategoryBox.SelectedItem = cats.FirstOrDefault(c => c.Id == doc.CategoryId);
        PartyBox.ItemsSource = PartyChoices(parties);
        PartyBox.SelectedValue = doc.PartyId ?? 0;
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    public static List<Party> PartyChoices(List<Party> parties)
    {
        var list = new List<Party> { new() { Id = 0, Role = "— None —" } };
        list.AddRange(parties);
        return list;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_doc.Title))
        {
            MessageBox.Show(this, "Please give the document a title.", "Document details");
            return;
        }
        if (CategoryBox.SelectedItem is not Category cat)
        {
            MessageBox.Show(this, "Please choose a category from the list.", "Document details");
            CategoryBox.Focus();
            return;
        }
        _doc.CategoryId = cat.Id;
        var partyId = PartyBox.SelectedValue is long id ? id : 0;
        _doc.PartyId = partyId > 0 ? partyId : null;
        DialogResult = true;
    }
}
