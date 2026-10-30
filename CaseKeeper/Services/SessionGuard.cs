using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CaseKeeper.Services;

/// <summary>
/// Locks the screen of a password-protected library after a period of inactivity, when Windows
/// itself is locked, or on request (Ctrl+L). Every window is hidden behind the password prompt
/// until the right password is entered.
/// </summary>
public static class SessionGuard
{
    private static DispatcherTimer _timer;
    private static DateTime _lastActivity = DateTime.Now;
    private static bool _locked;

    /// <summary>Minutes without keyboard or mouse use before locking. 0 turns it off.</summary>
    public static int IdleMinutes { get; set; } = 15;
    public static bool LockWithWindows { get; set; } = true;

    public static bool CanLock => App.Library?.Vault.IsEncrypted == true;

    public static void Start()
    {
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or MouseWheelEventArgs)
                _lastActivity = DateTime.Now;
            else if (e.StagingItem.Input is MouseEventArgs) _lastActivity = DateTime.Now;
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) =>
        {
            if (IdleMinutes > 0 && CanLock && !_locked && DateTime.Now - _lastActivity > TimeSpan.FromMinutes(IdleMinutes))
                Lock($"no activity for {IdleMinutes} minute(s)");
        };
        _timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Application.Current.Exit += (_, _) => SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    /// <summary>Stops locking for good, for when the library is about to be deleted and the app closed.</summary>
    public static void Stop()
    {
        _timer?.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && LockWithWindows)
            Application.Current?.Dispatcher.BeginInvoke(() => Lock("Windows was locked"));
    }

    public static void Lock(string reason)
    {
        if (_locked || !CanLock) return;
        _locked = true;
        var hidden = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToList();
        try
        {
            AppLog.Info("Security", "Screen locked: " + reason);
            App.Library.Db.Audit("library.lock", "library", null, "Locked the screen", reason);
            foreach (var w in hidden) w.Hide();

            var prompt = new Views.UnlockWindow(App.Library.Vault, relock: true);
            bool ok = prompt.ShowDialog() == true;
            if (!ok)
            {
                App.Library.Db.Audit("library.close", "library", null, "Closed from the lock screen",
                    prompt.FailedAttempts > 0 ? $"{prompt.FailedAttempts} wrong password(s) entered" : "");
                Application.Current.Shutdown();
                return;
            }
            App.Library.Db.Audit("library.unlock", "library", null, "Unlocked the screen",
                prompt.FailedAttempts > 0 ? $"{prompt.FailedAttempts} wrong password(s) entered first" : "");
            foreach (var w in hidden) w.Show();
            hidden.FirstOrDefault()?.Activate();
        }
        finally
        {
            _locked = false;
            _lastActivity = DateTime.Now;
        }
    }
}
