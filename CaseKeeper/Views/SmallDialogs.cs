using System.Windows;
using System.Windows.Controls;
using CaseKeeper.Data;
using CaseKeeper.Models;
using CaseKeeper.Services;

namespace CaseKeeper.Views;

/// <summary>
/// Base for simple form dialogs built in code: a title, a vertical list of labelled fields and Save/Cancel buttons.
/// </summary>
public abstract class FormDialog : Window
{
    protected readonly StackPanel Fields = new() { Margin = new Thickness(0, 0, 8, 0) };

    /// <summary>The Save / confirm button, so dialogs can enable or disable it as the user types.</summary>
    protected readonly Button ConfirmButton;

    protected FormDialog(string title, double width, double height, string confirmLabel = "Save", bool destructive = false)
    {
        Title = title;
        Width = width;
        Height = height;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Style = (Style)Application.Current.FindResource("AppWindow");
        // A local value, so plain text inside the dialog inherits it and follows the theme
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 7, 18, 7) });
        ConfirmButton = new Button
        {
            Content = confirmLabel,
            // A destructive action is never the Enter-key default - it has to be clicked on purpose
            IsDefault = !destructive,
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0),
            Style = (Style)Application.Current.FindResource(destructive ? "DangerButton" : "PrimaryButton")
        };
        ConfirmButton.Click += (_, _) => { if (Validate()) DialogResult = true; };
        buttons.Children.Add(ConfirmButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        Fields.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("H1"), FontSize = 20 });
        root.Children.Add(new ScrollViewer { Content = Fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    protected abstract bool Validate();

    protected T Add<T>(string label, T control) where T : FrameworkElement
    {
        Fields.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = (Style)Application.Current.FindResource("Label") });
        Fields.Children.Add(control);
        // Screen readers announce the label with the field (unless the dialog has named it already)
        if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control)))
            System.Windows.Automation.AutomationProperties.SetName(control, label);
        return control;
    }

    protected void AddHint(string text) =>
        Fields.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });

    protected static TextBox Box(string text = "") => new() { Text = text ?? "" };

    protected static TextBox Multi(string text, double height) => new()
    {
        Text = text ?? "", Height = height, Style = (Style)Application.Current.FindResource("MultilineBox")
    };

    protected bool Require(TextBox box, string what)
    {
        if (!string.IsNullOrWhiteSpace(box.Text)) return true;
        MessageBox.Show(this, $"Please enter {what}.", Title);
        box.Focus();
        return false;
    }
}

/// <summary>
/// Choosing how to bring a backup in: add it to what's here, or replace everything with it.
/// Also collects the password when the backup turns out to be encrypted.
/// </summary>
public class ImportBackupWindow : FormDialog
{
    private readonly RadioButton _merge, _replace;
    private readonly PasswordBox _password = new();

    public bool ReplaceEverything => _replace.IsChecked == true;
    public string Password { get; private set; } = "";

    public ImportBackupWindow(string fileName, bool encrypted, int currentDocCount)
        : base("Import from backup", 600, encrypted ? 640 : 560, "Import")
    {
        AddHint($"Backup file: {fileName}");

        Fields.Children.Add(new TextBlock
        {
            Text = "How should this be brought in?",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 8)
        });

        _merge = new RadioButton { GroupName = "mode", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };
        _merge.Content = Wrap("Add to this library",
            currentDocCount == 0
                ? "Brings in every document, note, timeline entry and complaint. Nothing here is removed."
                : $"Keeps the {currentDocCount} document(s) already here and adds the backup's on top. " +
                  "Anything already present is recognised by its file fingerprint and skipped, so importing twice is safe.");
        Fields.Children.Add(_merge);

        _replace = new RadioButton { GroupName = "mode", Margin = new Thickness(0, 12, 0, 0) };
        _replace.Content = Wrap("Replace everything with this backup",
            "For disaster recovery or moving to a new PC. Everything currently in CaseKeeper is set aside " +
            "in a '_before_restore' folder and the app restarts.");
        Fields.Children.Add(_replace);

        if (encrypted)
        {
            Add("This backup is password protected", _password);
            var show = new CheckBox { Content = "Show password", Margin = new Thickness(0, 8, 0, 0) };
            var plain = Box();
            plain.Visibility = Visibility.Collapsed;
            Fields.Children.Add(plain);
            Fields.Children.Add(show);
            show.Checked += (_, _) => { plain.Text = _password.Password; plain.Visibility = Visibility.Visible; _password.Visibility = Visibility.Collapsed; };
            show.Unchecked += (_, _) => { _password.Password = plain.Text; _password.Visibility = Visibility.Visible; plain.Visibility = Visibility.Collapsed; };
            _plain = plain;
        }
        Loaded += (_, _) => (encrypted ? (Control)_password : _merge).Focus();
    }

    private TextBox _plain;

    private static StackPanel Wrap(string heading, string detail)
    {
        var panel = new StackPanel { MaxWidth = 500 };
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var sub = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(sub);
        return panel;
    }

    protected override bool Validate()
    {
        Password = _plain is { Visibility: Visibility.Visible } ? _plain.Text : _password.Password;
        return true;
    }
}

/// <summary>
/// Last stop before everything is deleted. Spells out exactly what will go, offers to take a
/// backup there and then, and only unlocks the button once the word RESET has been typed.
/// </summary>
public class FactoryResetWindow : FormDialog
{
    private const string Phrase = "RESET";
    private readonly TextBox _confirm;
    private bool _backedUp;

    public FactoryResetWindow(string summary, string libraryPath, Func<Task<bool>> backupNow)
        : base("Factory reset", 600, 620, "Delete everything", destructive: true)
    {
        var warning = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(1)
        };
        warning.SetResourceReference(Border.BackgroundProperty, "DangerSoft");
        warning.SetResourceReference(Border.BorderBrushProperty, "Danger");
        var warnText = new TextBlock
        {
            Text = "This permanently deletes everything in CaseKeeper and cannot be undone.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        };
        warnText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        warning.Child = warnText;
        Fields.Children.Add(warning);

        Fields.Children.Add(new TextBlock
        {
            Text = "About to be deleted:",
            Margin = new Thickness(0, 16, 0, 6),
            FontWeight = FontWeights.SemiBold
        });
        Fields.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap });

        AddHint($"Your imported copies, notes, timeline, complaints, parties, saved exports and settings " +
                $"are all removed from:\n{libraryPath}\n\n" +
                "The original files you imported from (wherever you kept them) are not touched. " +
                "Categories go back to the built-in list and CaseKeeper restarts.");

        var backupRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var backupButton = new Button { Content = "Create a backup first…", Margin = new Thickness(0, 0, 10, 0) };
        var backupState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        backupState.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        backupState.Text = "Strongly recommended.";
        backupButton.Click += async (_, _) =>
        {
            backupButton.IsEnabled = false;
            try
            {
                if (await backupNow())
                {
                    _backedUp = true;
                    backupState.Text = "✓ Backup saved.";
                    backupState.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                }
            }
            finally { backupButton.IsEnabled = true; }
        };
        backupRow.Children.Add(backupButton);
        backupRow.Children.Add(backupState);
        Fields.Children.Add(backupRow);

        _confirm = Add($"Type {Phrase} to confirm", Box());
        _confirm.TextChanged += (_, _) => ConfirmButton.IsEnabled = IsPhraseTyped;
        ConfirmButton.IsEnabled = false;
        Loaded += (_, _) => _confirm.Focus();
    }

    private bool IsPhraseTyped => _confirm.Text.Trim().Equals(Phrase, StringComparison.Ordinal);

    protected override bool Validate()
    {
        if (!IsPhraseTyped)
        {
            MessageBox.Show(this, $"Type {Phrase} in the box to confirm.", Title);
            return false;
        }
        if (!_backedUp &&
            MessageBox.Show(this,
                "You haven't made a backup. Everything will be gone for good.\n\nDelete it all anyway?",
                "Factory reset", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        return true;
    }
}

/// <summary>
/// Sets up password protection. Deliberately hard to rush: the consequence of forgetting the
/// password is spelled out, a backup is offered, and the button stays disabled until the risk
/// has been acknowledged explicitly.
/// </summary>
public class EnableProtectionWindow : FormDialog
{
    private readonly PasswordBox _password = new(), _confirm = new();
    private readonly TextBox _plain = new(), _plainConfirm = new();
    private readonly CheckBox _understood, _show;
    private readonly TextBlock _strength = new();
    private readonly TextBlock _match = new();
    private bool _backedUp;

    public string Password { get; private set; } = "";

    public EnableProtectionWindow(int documentCount, Func<Task<bool>> backupNow)
        : base("Protect this library with a password", 620, 760, "Encrypt this library", destructive: false)
    {
        var warning = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(1)
        };
        warning.SetResourceReference(Border.BackgroundProperty, "DangerSoft");
        warning.SetResourceReference(Border.BorderBrushProperty, "Danger");
        var warnStack = new StackPanel();
        var warnTitle = new TextBlock
        {
            Text = "If you forget this password, everything here is lost.",
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        warnTitle.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        warnStack.Children.Add(warnTitle);
        var warnBody = new TextBlock
        {
            Text = "There is no reset, no recovery code and no back door. Nobody — not you, not the developer, " +
                   "not anyone else — can open this library again without the password. Your documents, notes, " +
                   "timeline and complaint records would be permanently unreadable.\n\n" +
                   "Write the password down and keep it somewhere safe and separate from this computer.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        warnStack.Children.Add(warnBody);
        warning.Child = warnStack;
        Fields.Children.Add(warning);

        AddHint($"Once protected, CaseKeeper will ask for this password every time it opens. " +
                $"All {documentCount} document file(s) and the whole database are encrypted on disk " +
                $"(AES-256), so they can't be read by anyone browsing the folder.");

        _password.PasswordChanged += (_, _) => Revalidate();
        _confirm.PasswordChanged += (_, _) => Revalidate();
        _plain.TextChanged += (_, _) => Revalidate();
        _plainConfirm.TextChanged += (_, _) => Revalidate();

        Add("Password", Stack(_password, _plain));
        _strength.FontSize = 12;
        _strength.Margin = new Thickness(0, 4, 0, 0);
        Fields.Children.Add(_strength);

        Add("Type it again to confirm", Stack(_confirm, _plainConfirm));
        _match.FontSize = 12;
        _match.Margin = new Thickness(0, 4, 0, 0);
        Fields.Children.Add(_match);

        _show = new CheckBox { Content = "Show passwords", Margin = new Thickness(0, 10, 0, 0) };
        _show.Checked += (_, _) => SetPlain(true);
        _show.Unchecked += (_, _) => SetPlain(false);
        Fields.Children.Add(_show);

        var backupRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        var backupButton = new Button { Content = "Create a backup first…", Margin = new Thickness(0, 0, 10, 0) };
        var backupState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Text = "Strongly recommended." };
        backupState.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        backupButton.Click += async (_, _) =>
        {
            backupButton.IsEnabled = false;
            try
            {
                if (await backupNow())
                {
                    _backedUp = true;
                    backupState.Text = "✓ Backup saved.";
                    backupState.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                }
            }
            finally { backupButton.IsEnabled = true; }
        };
        backupRow.Children.Add(backupButton);
        backupRow.Children.Add(backupState);
        Fields.Children.Add(backupRow);

        _understood = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "I understand that if I lose this password, all of the data in this library is lost for good.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500
            },
            Margin = new Thickness(0, 18, 0, 0)
        };
        _understood.Checked += (_, _) => Revalidate();
        _understood.Unchecked += (_, _) => Revalidate();
        Fields.Children.Add(_understood);

        ConfirmButton.IsEnabled = false;
        Loaded += (_, _) => _password.Focus();
    }

    private static Grid Stack(PasswordBox box, TextBox plain)
    {
        plain.Visibility = Visibility.Collapsed;
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(plain);
        return g;
    }

    private void SetPlain(bool plain)
    {
        if (plain) { _plain.Text = _password.Password; _plainConfirm.Text = _confirm.Password; }
        else { _password.Password = _plain.Text; _confirm.Password = _plainConfirm.Text; }
        _password.Visibility = _confirm.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        _plain.Visibility = _plainConfirm.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Plain => _show?.IsChecked == true;
    private string Entered => Plain ? _plain.Text : _password.Password;
    private string Repeated => Plain ? _plainConfirm.Text : _confirm.Password;

    private void Revalidate()
    {
        var pw = Entered;
        var (score, label) = Vault.RatePassword(pw);
        _strength.Text = pw.Length == 0 ? "" : $"Strength: {label}";
        _strength.SetResourceReference(TextBlock.ForegroundProperty,
            score >= 3 ? "Success" : score >= 2 ? "Warning" : "Danger");

        bool matches = pw.Length > 0 && pw == Repeated;
        _match.Text = Repeated.Length == 0 ? "" : matches ? "✓ Passwords match" : "Passwords don't match yet";
        _match.SetResourceReference(TextBlock.ForegroundProperty, matches ? "Success" : "MutedBrush");

        if (ConfirmButton != null)
            ConfirmButton.IsEnabled = matches && pw.Length >= 8 && _understood?.IsChecked == true;
    }

    protected override bool Validate()
    {
        Password = Entered;
        if (Password.Length < 8) { MessageBox.Show(this, "Use at least 8 characters.", Title); return false; }
        if (Password != Repeated) { MessageBox.Show(this, "The two passwords don't match.", Title); return false; }
        if (_understood.IsChecked != true) { MessageBox.Show(this, "Please confirm you understand the risk.", Title); return false; }

        if (!_backedUp && MessageBox.Show(this,
                "You haven't made a backup.\n\nIf anything goes wrong during encryption, or you later forget the " +
                "password, there would be no copy to fall back on.\n\nContinue without a backup?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;

        return MessageBox.Show(this,
            "Last check: is the password written down somewhere safe?\n\nEncrypt this library now?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}

/// <summary>Changes the library password. Only the wrapped key is rewritten, so this is instant.</summary>
public class ChangePasswordWindow : FormDialog
{
    private readonly PasswordBox _current = new(), _fresh = new(), _confirm = new();
    public string CurrentPassword { get; private set; } = "";
    public string NewPassword { get; private set; } = "";

    public ChangePasswordWindow() : base("Change library password", 540, 480, "Change password")
    {
        Add("Current password", _current);
        Add("New password", _fresh);
        Add("Type the new password again", _confirm);
        AddHint("Changing the password doesn't re-encrypt your documents, so it takes a moment. " +
                "The same warning applies: if the new password is lost, so is the data.");
        Loaded += (_, _) => _current.Focus();
    }

    protected override bool Validate()
    {
        CurrentPassword = _current.Password;
        NewPassword = _fresh.Password;
        if (NewPassword.Length < 8) { MessageBox.Show(this, "The new password needs at least 8 characters.", Title); return false; }
        if (NewPassword != _confirm.Password) { MessageBox.Show(this, "The two new passwords don't match.", Title); return false; }
        return true;
    }
}

/// <summary>
/// Chooses a new home for the library. Warns first, offers a backup, and explains what moving
/// to a network drive does and doesn't support.
/// </summary>
public class MoveLibraryWindow : FormDialog
{
    private readonly TextBox _path;
    private readonly TextBlock _problem = new();
    private readonly string _currentRoot;
    private bool _backedUp;

    public string TargetPath { get; private set; } = "";

    public MoveLibraryWindow(string currentRoot, long sizeBytes, Func<Task<bool>> backupNow)
        : base("Move the library", 660, 720, "Move the library")
    {
        _currentRoot = currentRoot;

        var warning = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1) };
        warning.SetResourceReference(Border.BackgroundProperty, "WarningSoft");
        warning.SetResourceReference(Border.BorderBrushProperty, "CalloutBorder");
        warning.Child = new TextBlock
        {
            Text = "Back up before moving. Everything — the database and every document — is copied to the new " +
                   "folder, checked file by file, and only then deleted from the old one. If anything fails the " +
                   "original is left untouched, but a backup is your safety net either way.",
            TextWrapping = TextWrapping.Wrap
        };
        Fields.Children.Add(warning);

        AddHint($"Currently stored at:\n{currentRoot}\n\nAbout {Math.Round(sizeBytes / 1024d / 1024d, 1)} MB to move.");

        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _path = Box();
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(browse, 1);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for the CaseKeeper library" };
            if (dlg.ShowDialog(this) == true)
            {
                _path.Text = dlg.FolderName;
                Recheck();
            }
        };
        _path.TextChanged += (_, _) => Recheck();
        row.Children.Add(_path);
        row.Children.Add(browse);
        Fields.Children.Add(new TextBlock { Text = "NEW LOCATION", Style = (Style)Application.Current.FindResource("Label") });
        Fields.Children.Add(row);

        _problem.TextWrapping = TextWrapping.Wrap;
        _problem.FontSize = 12;
        _problem.Margin = new Thickness(0, 6, 0, 0);
        Fields.Children.Add(_problem);

        var netNote = new TextBlock
        {
            Text = "Network drives: CaseKeeper will switch the database to a journal mode that works over a " +
                   "network share. Keep in mind it will be slower, the drive must be connected whenever you open " +
                   "the app, and only one computer should use the library at a time.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0)
        };
        netNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Fields.Children.Add(netNote);

        var backupRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        var backupButton = new Button { Content = "Create a backup first…", Margin = new Thickness(0, 0, 10, 0) };
        var backupState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = "Strongly recommended." };
        backupState.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        backupButton.Click += async (_, _) =>
        {
            backupButton.IsEnabled = false;
            try
            {
                if (await backupNow())
                {
                    _backedUp = true;
                    backupState.Text = "✓ Backup saved.";
                    backupState.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                }
            }
            finally { backupButton.IsEnabled = true; }
        };
        backupRow.Children.Add(backupButton);
        backupRow.Children.Add(backupState);
        Fields.Children.Add(backupRow);

        ConfirmButton.IsEnabled = false;
        Loaded += (_, _) => _path.Focus();
    }

    private void Recheck()
    {
        var problem = LibraryService.DescribeTargetProblem(_path.Text.Trim(), _currentRoot);
        _problem.Text = problem ?? "✓ That folder can be used.";
        _problem.SetResourceReference(TextBlock.ForegroundProperty, problem == null ? "Success" : "Danger");
        ConfirmButton.IsEnabled = problem == null;
    }

    protected override bool Validate()
    {
        TargetPath = _path.Text.Trim();
        var problem = LibraryService.DescribeTargetProblem(TargetPath, _currentRoot);
        if (problem != null) { MessageBox.Show(this, problem, Title); return false; }

        if (!_backedUp && MessageBox.Show(this,
                "You haven't made a backup.\n\nMoving is designed to be safe — nothing is deleted until the copy " +
                "has been verified — but a backup means you're covered whatever happens.\n\nMove without a backup?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        return true;
    }
}

/// <summary>
/// Points CaseKeeper at a library in another folder without copying, moving or deleting anything,
/// e.g. one kept on a NAS that several PCs use in turn. Starts a fresh library if the folder is empty.
/// </summary>
public class ChangeLibraryWindow : FormDialog
{
    private readonly TextBox _path;
    private readonly TextBlock _status = new();
    private readonly string _currentRoot;

    public string TargetPath { get; private set; } = "";

    public ChangeLibraryWindow(string currentRoot, string reason = null)
        : base("Use a different library", 660, reason == null ? 640 : 700, "Switch library")
    {
        _currentRoot = currentRoot;

        if (reason != null) AddHint(reason);

        var info = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1) };
        info.SetResourceReference(Border.BackgroundProperty, "PrimarySoft");
        info.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        info.Child = new TextBlock
        {
            Text = "Nothing is copied, moved or deleted. CaseKeeper will simply open the library in the folder you " +
                   "choose. The library it's using now stays exactly where it is, and you can switch back at any time.",
            TextWrapping = TextWrapping.Wrap
        };
        Fields.Children.Add(info);

        AddHint($"Currently using:\n{currentRoot}");

        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _path = Box();
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(browse, 1);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder that holds the CaseKeeper library" };
            if (dlg.ShowDialog(this) == true) _path.Text = dlg.FolderName;
        };
        _path.TextChanged += (_, _) => Recheck();
        row.Children.Add(_path);
        row.Children.Add(browse);
        Fields.Children.Add(new TextBlock { Text = "LIBRARY FOLDER", Style = (Style)Application.Current.FindResource("Label") });
        Fields.Children.Add(row);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12;
        _status.Margin = new Thickness(0, 6, 0, 0);
        Fields.Children.Add(_status);

        var shareNote = new TextBlock
        {
            Text = "Sharing one library between computers: keep it on a network share (for example \\\\NAS\\CaseKeeper) " +
                   "and point each computer at that folder. Use it on one computer at a time; CaseKeeper warns you " +
                   "if it's already open somewhere else.\n\n" +
                   "Don't share it through a sync folder such as OneDrive, Dropbox or Synology Drive. Each computer " +
                   "then works on its own copy of the database, and changes made on one can overwrite the other's.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0)
        };
        shareNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Fields.Children.Add(shareNote);

        ConfirmButton.IsEnabled = false;
        Loaded += (_, _) => _path.Focus();
    }

    private void Recheck()
    {
        var check = LibraryService.InspectFolder(_path.Text.Trim(), _currentRoot);
        string text, brush;
        if (check.Problem != null) { text = check.Problem; brush = "Danger"; }
        else if (check.HasLibrary)
        {
            text = "✓ Found a CaseKeeper library here." +
                   (check.IsProtected ? " It's password protected, so you'll be asked for its password." : "");
            if (check.IsNetwork) text += "\nIt's on a network drive, so it needs to be connected whenever you open CaseKeeper.";
            brush = "Success";
            if (check.OpenElsewhere != null)
            {
                text += $"\n\nIt's open right now on {LibraryLock.Describe(check.OpenElsewhere)}. Close it there first.";
                brush = "Warning";
            }
        }
        else if (check.NestedLibrary != null)
        {
            text = $"There's no library directly in this folder, but there is one in:\n{check.NestedLibrary}";
            brush = "Warning";
        }
        else
        {
            text = "There's no CaseKeeper library in this folder yet. A new, empty library will be started here.";
            brush = "Warning";
        }
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        ConfirmButton.IsEnabled = check.Problem == null;
    }

    protected override bool Validate()
    {
        var target = _path.Text.Trim();
        var check = LibraryService.InspectFolder(target, _currentRoot);
        if (check.Problem != null) { MessageBox.Show(this, check.Problem, Title); return false; }

        if (!check.HasLibrary && check.NestedLibrary != null)
        {
            var answer = MessageBox.Show(this,
                $"There's a CaseKeeper library in the folder inside this one:\n{check.NestedLibrary}\n\nUse that library?",
                Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes) { TargetPath = check.NestedLibrary; return true; }
        }

        if (!check.HasLibrary && MessageBox.Show(this,
                $"There's no CaseKeeper library in:\n{target}\n\nStart a new, empty library there?\n\n" +
                "If you expected to find your library here, choose No and check the folder (and that the drive is connected).",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;

        TargetPath = target;
        return true;
    }
}

/// <summary>
/// Shown at startup when the library folder can't be reached, typically a NAS or external disk
/// that isn't connected. Offers to try again or to open a different library, instead of just quitting.
/// </summary>
public class LibraryUnavailableWindow : FormDialog
{
    private readonly RadioButton _retry, _choose;

    private LibraryUnavailableWindow(string root, string problem)
        : base("Library not available", 580, 470, "Continue")
    {
        var callout = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1) };
        callout.SetResourceReference(Border.BackgroundProperty, "WarningSoft");
        callout.SetResourceReference(Border.BorderBrushProperty, "CalloutBorder");
        callout.Child = new TextBlock { Text = $"CaseKeeper can't reach its library:\n{root}\n\n{problem}", TextWrapping = TextWrapping.Wrap };
        Fields.Children.Add(callout);

        _retry = new RadioButton { Content = "Try again (after connecting the drive)", IsChecked = true, Margin = new Thickness(0, 16, 0, 0) };
        _choose = new RadioButton { Content = "Open a library in a different folder…", Margin = new Thickness(0, 8, 0, 0) };
        Fields.Children.Add(_retry);
        Fields.Children.Add(_choose);
        AddHint("Cancel closes CaseKeeper. Nothing in the library is changed either way.");
    }

    protected override bool Validate() => true;

    /// <summary>The folder to try next, or null to quit.</summary>
    public static string Ask(string root, string problem)
    {
        while (true)
        {
            var dlg = new LibraryUnavailableWindow(root, problem);
            if (dlg.ShowDialog() != true) return null;
            if (dlg._retry.IsChecked == true) return root;

            var change = new ChangeLibraryWindow(root, "Choose the folder that holds your library, or an empty folder to start a new one.");
            if (change.ShowDialog() == true)
            {
                LibraryService.UseExistingLibraryAt(change.TargetPath);
                return change.TargetPath;
            }
        }
    }
}

/// <summary>Asks for a backup password, with a confirmation box when one is being set.</summary>
public class PasswordDialog : FormDialog
{
    private readonly PasswordBox _pw = new(), _confirm = new();
    private readonly TextBox _pwPlain = new(), _confirmPlain = new();
    private readonly bool _needsConfirm;

    public string Password { get; private set; } = "";

    public PasswordDialog(bool needsConfirm, string title, string explanation)
        : base(title, 520, needsConfirm ? 400 : 320)
    {
        _needsConfirm = needsConfirm;
        AddHint(explanation);
        Add("Password", Stack(_pw, _pwPlain));
        if (needsConfirm) Add("Type it again to confirm", Stack(_confirm, _confirmPlain));

        var show = new CheckBox { Content = "Show password", Margin = new Thickness(0, 12, 0, 0) };
        show.Checked += (_, _) => SetVisible(true);
        show.Unchecked += (_, _) => SetVisible(false);
        Fields.Children.Add(show);
        Loaded += (_, _) => _pw.Focus();
    }

    private static Grid Stack(PasswordBox box, TextBox plain)
    {
        plain.Visibility = Visibility.Collapsed;
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(plain);
        return g;
    }

    private void SetVisible(bool plain)
    {
        Swap(_pw, _pwPlain, plain);
        if (_needsConfirm) Swap(_confirm, _confirmPlain, plain);

        static void Swap(PasswordBox box, TextBox text, bool showPlain)
        {
            if (showPlain) text.Text = box.Password;
            else box.Password = text.Text;
            box.Visibility = showPlain ? Visibility.Collapsed : Visibility.Visible;
            text.Visibility = showPlain ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string Read(PasswordBox box, TextBox plain) =>
        box.Visibility == Visibility.Visible ? box.Password : plain.Text;

    protected override bool Validate()
    {
        Password = Read(_pw, _pwPlain);
        if (Password.Length == 0)
        {
            MessageBox.Show(this, "Please enter a password.", Title);
            return false;
        }
        if (_needsConfirm)
        {
            if (Password != Read(_confirm, _confirmPlain))
            {
                MessageBox.Show(this, "The two passwords don't match.", Title);
                return false;
            }
            if (Password.Length < 8 &&
                MessageBox.Show(this, "That password is quite short. A longer one (or a few words together) is much harder to break.\n\nUse it anyway?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
        }
        return true;
    }
}

public class PartyEditWindow : FormDialog
{
    private readonly Party _p;
    private readonly ComboBox _role;
    private readonly TextBox _name, _rep, _contact, _notes;
    private readonly CheckBox _isMe;
    private readonly DatePicker _dob;

    public PartyEditWindow(Party p) : base(p.Id == 0 ? "Add party" : "Edit party", 560, 720)
    {
        _p = p;
        _role = Add("Role in the case", new ComboBox { ItemsSource = Party.Roles, IsEditable = true, Text = p.Role });
        _name = Add("Full name (or organisation)", Box(p.Name));
        _dob = Add("Date of birth (for children – keeps their age up to date)", new DatePicker { SelectedDate = p.DateOfBirth });
        _isMe = new CheckBox { Content = "This is me", IsChecked = p.IsMe, Margin = new Thickness(0, 10, 0, 0) };
        Fields.Children.Add(_isMe);
        _rep = Add("Represented by (solicitor / barrister / firm, or 'Litigant in person')", Box(p.Representative));
        _contact = Add("Contact details (address, email, phone, reference)", Multi(p.Contact, 70));
        AddHint("If any address is confidential (e.g. on a C8 form), think carefully before recording it here.");
        _notes = Add("Notes", Multi(p.Notes, 110));
        Loaded += (_, _) => _name.Focus();
    }

    protected override bool Validate()
    {
        if (string.IsNullOrWhiteSpace(_role.Text)) { MessageBox.Show(this, "Please choose a role.", Title); return false; }
        _p.Role = _role.Text.Trim();
        _p.Name = _name.Text.Trim();
        _p.DateOfBirth = _dob.SelectedDate;
        _p.IsMe = _isMe.IsChecked == true;
        _p.Representative = _rep.Text.Trim();
        _p.Contact = _contact.Text.Trim();
        _p.Notes = _notes.Text.Trim();
        return true;
    }
}

public class CategoryEditWindow : FormDialog
{
    private readonly Category _c;
    private readonly TextBox _code, _name, _desc;
    private readonly ComboBox _group, _section;

    public CategoryEditWindow(Category c, IEnumerable<string> groups) : base(c.Id == 0 ? "Add category" : "Edit category", 520, 560)
    {
        _c = c;
        _name = Add("Name", Box(c.Name));
        _code = Add("Form code (optional, e.g. C63)", Box(c.Code));
        _group = Add("Group", new ComboBox
        {
            ItemsSource = UI.NaturalComparer.Sort(groups.Append("My categories").Distinct()),
            IsEditable = true, Text = c.GroupName
        });
        _section = Add("Default bundle section", new ComboBox
        {
            ItemsSource = DefaultCategories.BundleSections.Select(kv => new KeyValuePair<string, string>(kv.Key, $"{kv.Key} – {kv.Value}")).ToList(),
            DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = c.BundleSection
        });
        _desc = Add("Description", Multi(c.Description, 70));
        Loaded += (_, _) => _name.Focus();
    }

    protected override bool Validate()
    {
        if (!Require(_name, "a name")) return false;
        _c.Name = _name.Text.Trim();
        _c.Code = _code.Text.Trim();
        _c.GroupName = string.IsNullOrWhiteSpace(_group.Text) ? "My categories" : _group.Text.Trim();
        _c.BundleSection = _section.SelectedValue as string ?? "F";
        _c.Description = _desc.Text.Trim();
        return true;
    }
}

public class BulkEditWindow : FormDialog
{
    private readonly ComboBox _category, _party;
    private readonly CheckBox _changeParty;
    private readonly TextBox _tags;

    public long? CategoryId { get; private set; }
    public bool ChangeParty { get; private set; }
    public long? PartyId { get; private set; }
    public string AddTags { get; private set; }

    public BulkEditWindow(int count, List<Category> categories, List<Party> parties) : base($"Edit {count} documents", 520, 470)
    {
        var cats = new List<Category> { new() { Id = 0, Name = "(leave unchanged)" } };
        cats.AddRange(categories);
        _category = Add("Move to category", new ComboBox { ItemsSource = cats, DisplayMemberPath = "DisplayName", SelectedIndex = 0 });
        UI.SearchableCombo.Attach(_category, o => (o as Category)?.DisplayName ?? "");
        AddHint("Type any word or form code to find a category.");
        _changeParty = new CheckBox { Content = "Change the party for all of them", Margin = new Thickness(0, 14, 0, 0) };
        Fields.Children.Add(_changeParty);
        _party = Add("Filed by / party", new ComboBox { ItemsSource = DocumentEditWindow.PartyChoices(parties), DisplayMemberPath = "DisplayName", SelectedIndex = 0 });
        _tags = Add("Add tags (comma separated)", Box());
    }

    protected override bool Validate()
    {
        CategoryId = _category.SelectedItem is Category { Id: > 0 } c ? c.Id : null;
        ChangeParty = _changeParty.IsChecked == true;
        PartyId = _party.SelectedItem is Party { Id: > 0 } p ? p.Id : null;
        AddTags = _tags.Text;
        return true;
    }
}

public class EventEditWindow : FormDialog
{
    private readonly TimelineEvent _e;
    private readonly DatePicker _date;
    private readonly TextBox _time, _title, _desc, _location, _witnesses, _reference;
    private readonly ComboBox _type, _doc;
    private readonly CheckBox _chron;

    public EventEditWindow(TimelineEvent e, List<CaseDocument> docs) : base(e.Id == 0 ? "Add event" : "Edit event", 620, 860)
    {
        _e = e;
        _date = Add("Date", new DatePicker { SelectedDate = e.EventDate });
        _time = Add("Time (optional, e.g. 10:30)", Box(e.Time));
        _type = Add("Type", new ComboBox { ItemsSource = TimelineEvent.Types, IsEditable = true, Text = e.EventType });
        _title = Add("What happened / what is due", Box(e.Title));
        _desc = Add("Details (who, where, what was said; keep it factual)", Multi(e.Description, 150));
        _location = Add("Where (optional)", Box(e.Location));
        _witnesses = Add("Who else was there / witnesses (optional)", Box(e.Witnesses));
        _reference = Add("Reference (police incident or crime number, case reference…)", Box(e.Reference));
        var choices = new List<CaseDocument> { new() { Id = 0, Title = "— None —" } };
        choices.AddRange(UI.NaturalComparer.Sort(docs, d => d.Title));
        _doc = Add("Linked document (optional)", new ComboBox
        {
            ItemsSource = choices, DisplayMemberPath = "Title", SelectedValuePath = "Id", SelectedValue = e.DocumentId ?? 0,
            IsEditable = true, IsTextSearchEnabled = true
        });
        _chron = new CheckBox { Content = "Include in chronology for bundles and reports", IsChecked = e.IncludeInChronology, Margin = new Thickness(0, 14, 0, 0) };
        Fields.Children.Add(_chron);
        Loaded += (_, _) => _title.Focus();
    }

    protected override bool Validate()
    {
        if (!Require(_title, "a short title")) return false;
        if (_date.SelectedDate == null) { MessageBox.Show(this, "Please choose a date.", Title); return false; }
        _e.EventDate = _date.SelectedDate.Value;
        _e.Time = _time.Text.Trim();
        _e.EventType = string.IsNullOrWhiteSpace(_type.Text) ? "Other" : _type.Text.Trim();
        _e.Title = _title.Text.Trim();
        _e.Description = _desc.Text.Trim();
        _e.Location = _location.Text.Trim();
        _e.Witnesses = _witnesses.Text.Trim();
        _e.Reference = _reference.Text.Trim();
        _e.DocumentId = _doc.SelectedValue is long id && id > 0 ? id : null;
        _e.IncludeInChronology = _chron.IsChecked == true;
        return true;
    }
}

public class ComplaintEditWindow : FormDialog
{
    private readonly Complaint _c;
    private readonly ComboBox _recipient, _status;
    private readonly TextBox _name, _ref, _subject, _summary, _outcome;
    private readonly DatePicker _sent, _due;
    private readonly ListBox _docs;

    public ComplaintEditWindow(Complaint c, List<CaseDocument> docs) : base(c.Id == 0 ? "New complaint" : "Edit complaint", 680, 860)
    {
        _c = c;
        _subject = Add("Subject", Box(c.Subject));
        _recipient = Add("Sent to", new ComboBox { ItemsSource = Complaint.Recipients, IsEditable = true, Text = c.Recipient });
        _name = Add("Name of person / office (e.g. your MP's name)", Box(c.RecipientName));
        _ref = Add("Their reference number", Box(c.Reference));
        var dates = new Grid();
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "DATE SENT", Style = (Style)Application.Current.FindResource("Label") });
        left.Children.Add(_sent = new DatePicker { SelectedDate = c.DateSent });
        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = "RESPONSE DUE BY", Style = (Style)Application.Current.FindResource("Label") });
        right.Children.Add(_due = new DatePicker { SelectedDate = c.ResponseDue });
        Grid.SetColumn(right, 2);
        dates.Children.Add(left);
        dates.Children.Add(right);
        Fields.Children.Add(dates);
        AddHint("Tip: many organisations aim to respond within 20 working days; check their complaints policy.");
        _status = Add("Status", new ComboBox { ItemsSource = Complaint.Statuses, SelectedItem = c.Status });
        _summary = Add("Summary of the complaint", Multi(c.Summary, 140));
        _outcome = Add("Response / outcome", Multi(c.Outcome, 80));

        _docs = Add("Supporting documents (tick to link)", new ListBox { Height = 180 });
        foreach (var d in UI.NaturalComparer.Sort(docs, d => d.Title))
            _docs.Items.Add(new CheckBox { Content = $"{d.Title}  ·  {d.DocumentDateText}", Tag = d.Id, IsChecked = c.DocumentIds.Contains(d.Id), Margin = new Thickness(2) });
        Loaded += (_, _) => _subject.Focus();
    }

    protected override bool Validate()
    {
        if (!Require(_subject, "a subject")) return false;
        _c.Subject = _subject.Text.Trim();
        _c.Recipient = _recipient.Text.Trim();
        _c.RecipientName = _name.Text.Trim();
        _c.Reference = _ref.Text.Trim();
        _c.DateSent = _sent.SelectedDate;
        _c.ResponseDue = _due.SelectedDate;
        _c.Status = _status.SelectedItem as string ?? "Draft";
        _c.Summary = _summary.Text.Trim();
        _c.Outcome = _outcome.Text.Trim();
        _c.DocumentIds = _docs.Items.Cast<CheckBox>().Where(cb => cb.IsChecked == true).Select(cb => (long)cb.Tag).ToList();
        return true;
    }
}
