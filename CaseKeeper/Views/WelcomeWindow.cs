using System.Windows;
using System.Windows.Controls;
using CaseKeeper.Models;
using CaseKeeper.UI;

namespace CaseKeeper.Views;

/// <summary>
/// First-run setup: how CaseKeeper will be used (Simple or Professional), who you are in the case,
/// the case basics, and whether to protect the library with a password. Everything can be changed later.
/// </summary>
public class WelcomeWindow : FormDialog
{
    private readonly RadioButton _simple, _pro;
    private readonly ComboBox _role, _caseType, _theme;
    private readonly TextBox _name, _caseNo, _court;
    private readonly CheckBox _protect;

    public bool Professional => _pro.IsChecked == true;
    public string Role => _role.Text.Trim();
    public string MyName => _name.Text.Trim();
    public string CaseNumber => _caseNo.Text.Trim();
    public string Court => _court.Text.Trim();
    public string CaseType => _caseType.Text.Trim();
    public string Theme => _theme.SelectedItem as string ?? "Dark";
    public bool ProtectNow => _protect.IsChecked == true;

    public WelcomeWindow() : base("Welcome to CaseKeeper", 640, 820, "Get started")
    {
        AddHint("A private place on your own computer for everything in your family court case: documents, hearings, deadlines, " +
                "letters and more. Nothing is uploaded. Answer a few questions to set it up – you can skip any of them and change everything later.");

        Fields.Children.Add(new TextBlock { Text = "HOW WILL YOU USE IT?", Style = Ui.Style("Label"), Margin = new Thickness(0, 16, 0, 6) });
        _simple = new RadioButton { GroupName = "mode", IsChecked = true, Content = Option("Simple", "I'm dealing with my own case. Show me the essentials, in plain English.") };
        _pro = new RadioButton { GroupName = "mode", Margin = new Thickness(0, 10, 0, 0), Content = Option("Professional",
            "I'm a legal professional, McKenzie friend or power user. Show every tool: costs, Scott schedules, audit trail, AI analysis, exports.") };
        Fields.Children.Add(_simple);
        Fields.Children.Add(_pro);

        Fields.Children.Add(new TextBlock { Text = "About you (optional)", Style = Ui.Style("H2"), Margin = new Thickness(0, 18, 0, 0) });
        _role = Add("Your role in the case", new ComboBox { ItemsSource = Party.Roles.Append("Legal representative").Append("Supporter / McKenzie friend").ToList(), IsEditable = true, Text = "Applicant" });
        _name = Add("Your name", Box());
        AddHint("This is used to fill in your letters and statements. Leave it blank if you'd rather not.");

        Fields.Children.Add(new TextBlock { Text = "The case (optional)", Style = Ui.Style("H2"), Margin = new Thickness(0, 18, 0, 0) });
        _caseNo = Add("Case number", Box());
        _court = Add("Court", Box());
        _caseType = Add("Type of case", new ComboBox
        {
            IsEditable = true,
            ItemsSource = new[]
            {
                "Private law – child arrangements (C100)", "Domestic abuse – non-molestation / occupation (FL401)", "Public law – care proceedings",
                "Divorce / dissolution", "Financial remedy", "Private law – enforcement (C79)", "Appeal", "Other"
            }
        });

        Fields.Children.Add(new TextBlock { Text = "Look and security", Style = Ui.Style("H2"), Margin = new Thickness(0, 18, 0, 0) });
        _theme = Add("Appearance", new ComboBox { ItemsSource = new[] { "Dark", "Light", "System" }, SelectedIndex = 0 });
        _protect = new CheckBox { Content = "Protect this library with a password (recommended – you'll set it next)", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        Fields.Children.Add(_protect);
        AddHint("Password protection encrypts everything on disk with AES-256. If you forget the password, the data can't be recovered, so keep it safe.");
    }

    private static StackPanel Option(string heading, string detail)
    {
        var p = new StackPanel { MaxWidth = 540 };
        p.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Style = Ui.Style("Muted") });
        return p;
    }

    protected override bool Validate() => true;
}
