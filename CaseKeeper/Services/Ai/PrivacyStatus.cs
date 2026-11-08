using System.Windows;
using System.Windows.Input;

namespace CaseKeeper.Services;

/// <summary>
/// The sidebar's corner status: "Local only · nothing uploaded" while every AI feature stays on this PC, and
/// "Cloud AI on · provider" (amber) when the app or Bulk Case Import is set to use a cloud AI. Shown through
/// application resources the sidebar template binds to, and refreshed whenever an AI setting changes.
/// </summary>
public static class PrivacyStatus
{
    public const string TextKey = "PrivacyText", TipKey = "PrivacyTip", LocalKey = "PrivacyLocalVis", CloudKey = "PrivacyCloudVis";

    private static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai.enabled", "ai.provider", "bulk.provider", "ai.cloud.mask"
    };

    /// <summary>Opens Settings → AI (the status is clickable).</summary>
    public static ICommand OpenSettings { get; } = new Command(() => Views.MainWindow.Shell?.GoToSettings("ai"));

    /// <summary>Subscribes to setting changes. Called once at startup.</summary>
    public static void Start()
    {
        Data.Database.SettingChanged -= OnSetting;
        Data.Database.SettingChanged += OnSetting;
        Refresh();
    }

    private static void OnSetting(string key)
    {
        if (Watched.Contains(key)) Refresh();
    }

    /// <summary>The cloud providers in use, or an empty list when nothing leaves this PC.</summary>
    public static List<string> CloudInUse()
    {
        var names = new List<string>();
        try
        {
            if (App.Library?.Db == null || !AiService.Enabled) return names;
            foreach (var id in new[] { AiService.Provider, AiService.BulkProvider }.Distinct())
                if (AiProviders.IsCloud(id)) names.Add(AiProviders.Get(id).Name);
        }
        catch (Exception ex) { AppLog.Warn("Privacy", "Couldn't read the AI settings for the status", ex); }
        return names;
    }

    /// <summary>Updates the status text, dot and tooltip. Safe to call from any thread.</summary>
    public static void Refresh()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(Refresh); return; }
        var cloud = CloudInUse();
        bool masked = true;
        try { if (App.Library?.Db != null) masked = AiService.MaskCloud; } catch (Exception) { /* no library open yet */ }
        string text, tip;
        if (cloud.Count == 0)
        {
            text = "Local only · nothing uploaded";
            tip = "Your documents, records and AI work all stay on this PC. Click for AI settings.";
        }
        else
        {
            text = "Cloud AI on · " + string.Join(", ", cloud.Select(Short).Distinct());
            tip = $"A cloud AI is selected in Settings → AI ({string.Join(", ", cloud)}). The text of documents you ask it to analyse is sent " +
                  $"to that service over HTTPS, only after a review screen, with names and personal details {(masked ? "masked" : "NOT masked")}. " +
                  "Everything else stays on this PC. Click to change it.";
        }
        app.Resources[TextKey] = text;
        app.Resources[TipKey] = tip;
        app.Resources[LocalKey] = cloud.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        app.Resources[CloudKey] = cloud.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public static string Short(string name) =>
        name.StartsWith("Anthropic", StringComparison.Ordinal) ? "Anthropic"
        : name.StartsWith("OpenAI (", StringComparison.Ordinal) ? "OpenAI"
        : name.StartsWith("Claude on", StringComparison.Ordinal) ? "Claude on Foundry"
        : name.StartsWith("OpenAI on", StringComparison.Ordinal) ? "OpenAI on Foundry"
        : name;

    private sealed class Command : ICommand
    {
        private readonly Action _run;
        public Command(Action run) => _run = run;
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _run();
    }
}
