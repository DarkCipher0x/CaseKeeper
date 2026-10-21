using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Services;

namespace CaseKeeper.Views;

/// <summary>
/// Shown before the main window when the library is password protected. Nothing is read from
/// disk until the password checks out.
/// </summary>
public partial class UnlockWindow : Window
{
    private readonly Vault _vault;
    private int _attempts;

    /// <summary>Wrong passwords entered before the right one (or before giving up).</summary>
    public int FailedAttempts => _attempts;

    /// <param name="relock">True when CaseKeeper locked itself while running, rather than starting up.</param>
    public UnlockWindow(Vault vault, bool relock = false)
    {
        InitializeComponent();
        _vault = vault;
        if (relock)
        {
            SubtitleText.Text = "Locked. Enter your password to carry on.";
            QuitButton.Content = "Close CaseKeeper";
            Topmost = true;
            ShowInTaskbar = true;
        }
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private string Entered => ShowPassword.IsChecked == true ? PlainBox.Text : PasswordBox.Password;

    private void ShowPassword_Changed(object sender, RoutedEventArgs e)
    {
        bool plain = ShowPassword.IsChecked == true;
        if (plain) PlainBox.Text = PasswordBox.Password;
        else PasswordBox.Password = PlainBox.Text;
        PlainBox.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        (plain ? (Control)PlainBox : PasswordBox).Focus();
    }

    private void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Unlock_Click(sender, e);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var password = Entered;
        if (password.Length == 0) { ShowError("Enter your password."); return; }

        UnlockButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        // Deriving the key is deliberately slow, so keep the window responsive
        bool ok = await Task.Run(() => _vault.TryUnlock(password));
        Mouse.OverrideCursor = null;

        if (ok)
        {
            DialogResult = true;
            return;
        }

        _attempts++;
        AppLog.Warn("Security", $"Wrong password entered (attempt {_attempts})");
        // Back off a little after repeated failures, to make guessing tedious
        if (_attempts >= 3)
        {
            ShowError($"That password didn't work. ({_attempts} attempts)");
            await Task.Delay(Math.Min(_attempts - 2, 5) * 1000);
        }
        else ShowError("That password didn't work.");

        UnlockButton.IsEnabled = true;
        PasswordBox.SelectAll();
        PlainBox.SelectAll();
        (ShowPassword.IsChecked == true ? (Control)PlainBox : PasswordBox).Focus();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
