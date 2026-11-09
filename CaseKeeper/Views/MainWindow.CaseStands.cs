using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;

namespace CaseKeeper.Views;

/// <summary>
/// The dashboard's "Where your case stands" card: the stage, the next hearing, the latest case overview, what's
/// overdue or waiting for a reply, what the AI added that still needs checking, the most recent and next dates, and a
/// box to ask about the case. Built from the case records only (no AI call), so it is always up to date.
/// </summary>
public partial class MainWindow
{
    private System.Windows.Threading.DispatcherTimer _memoryTimer;

    /// <summary>
    /// Keeps the case memory up to date: rebuilt a few seconds after the records change (the dashboard refreshes whenever
    /// they do), in the background, so the AI always reads the case as it is now.
    /// </summary>
    private void ScheduleMemoryRebuild()
    {
        if (_memoryTimer == null)
        {
            _memoryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _memoryTimer.Tick += async (_, _) =>
            {
                _memoryTimer.Stop();
                var db = Db;
                try { await Task.Run(() => CaseMemory.Rebuild(db)); }
                catch (Exception ex) { AppLog.Warn("CaseMemory", "Couldn't update the case memory", ex); }
            };
        }
        _memoryTimer.Stop();
        _memoryTimer.Start();
    }

    private UIElement CaseStandsCard(List<Hearing> hearings, List<CaseAction> actions)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var memory = Ui.Button("What CaseKeeper knows", "", (_, _) => GoTo("memory"), tooltip: "The case memory the AI reads");
        DockPanel.SetDock(memory, Dock.Right);
        head.Children.Add(memory);
        var stage = Db.GetSetting("case.stage", "");
        head.Children.Add(Ui.Text("Where your case stands" + (string.IsNullOrWhiteSpace(stage) ? "" : "  ·  " + stage), "H2"));
        sp.Children.Add(head);

        // The overview's first paragraph, or how to get one
        var overview = CaseOverview.Latest(Db);
        if (overview != null)
        {
            var first = (overview.Output ?? "").Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().TrimStart('#', '*', ' ')).FirstOrDefault(p => p.Length > 40) ?? "";
            if (first.Length > 0) sp.Children.Add(Ui.Muted((first.Length > 420 ? first[..420] + "…" : first.Replace("**", "")) + $"  (overview of {overview.CreatedAt:d MMM})", 12.5));
        }

        // What needs attention, each a button to the place to deal with it
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Chip(string text, Action go, bool warn = false)
        {
            var b = Ui.Button(text, warn ? "" : null, (_, _) => go(), primary: warn);
            chips.Children.Add(b);
        }
        var next = hearings.Where(h => h.IsUpcoming).OrderBy(h => h.HearingDate).FirstOrDefault();
        if (next != null)
            Chip($"Next hearing: {next.ShortType} {next.HearingDate:d MMM}" + (next.HearingDate is { } hd ? $" ({(hd.Date - DateTime.Today).Days} days)" : ""), () => GoTo("hearings"));
        int overdue = actions.Count(a => a.IsOverdue);
        if (overdue > 0) Chip($"{overdue} overdue direction(s) or task(s)", () => GoTo("actions"), warn: true);
        int soon = actions.Count(a => a.IsDueSoon);
        if (soon > 0) Chip($"{soon} due this week", () => GoTo("actions"));
        int lateReplies = _complaints.Count(c => c.IsOverdue);
        int awaiting = _complaints.Count(c => c.Status is "Sent" or "Acknowledged" or "Awaiting Response");
        if (lateReplies > 0) Chip($"{lateReplies} complaint repl{(lateReplies == 1 ? "y" : "ies")} overdue", () => GoTo("complaints"), warn: true);
        else if (awaiting > 0) Chip($"{awaiting} complaint(s) awaiting a reply", () => GoTo("complaints"));
        int missing = actions.Count(a => a.IsOpen && a.Title.StartsWith("Get a copy of:", StringComparison.Ordinal));
        if (missing > 0) Chip($"{missing} missing document(s) to get", () => GoTo("actions"));
        int toCheck = 0;
        try { toCheck = TrackAutoAdd.ToCheck(Db).Count; } catch (Exception ex) { AppLog.Warn("Dashboard", "Couldn't count AI entries to check", ex); }
        if (toCheck > 0)
            Chip($"{toCheck} added by AI to check", () => { GoTo("review"); Dispatcher.BeginInvoke(() => _reviewView?.Go("auto"), System.Windows.Threading.DispatcherPriority.Loaded); });
        var last = DateTime.TryParse(Db.GetSetting(LibraryCheck.LastRunSetting, ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var l) ? l : (DateTime?)null;
        Chip(last == null ? "Check my library" : $"Library checked {last:d MMM}", OpenLibraryCheck);
        sp.Children.Add(chips);

        // The last few things that happened and the next few dates, on one line each
        var recent = _events.Where(e => e.EventDate.Date <= DateTime.Today).OrderByDescending(e => e.EventDate).Take(5).ToList();
        var coming = _events.Where(e => e.EventDate.Date > DateTime.Today).OrderBy(e => e.EventDate).Take(5).ToList();
        if (recent.Count + coming.Count > 0)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            UIElement List(string title, List<TimelineEvent> items)
            {
                var col = new StackPanel();
                col.Children.Add(new TextBlock { Text = title, Style = Ui.Style("Label"), Margin = new Thickness(0, 0, 0, 4) });
                if (items.Count == 0) col.Children.Add(Ui.Muted("Nothing yet.", 12));
                foreach (var e in items)
                {
                    var row = new TextBlock { Text = $"{e.EventDate:d MMM yyyy} · {e.Title}", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, Cursor = Cursors.Hand, ToolTip = e.Title };
                    row.MouseLeftButtonUp += (_, _) => GoTo("timeline");
                    AutomationProperties.SetName(row, $"{e.EventDate:d MMMM yyyy}: {e.Title}");
                    col.Children.Add(row);
                }
                return col;
            }
            var left = List("MOST RECENT", recent);
            var right = List("COMING UP", coming);
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            sp.Children.Add(grid);
        }

        // Ask about the case
        var ask = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var box = new TextBox { FontSize = 13.5, Padding = new Thickness(10, 7, 10, 7) };
        AutomationProperties.SetName(box, "Ask about your case");
        var go = Ui.Button("Ask", "", (_, _) => AskAboutCase(box.Text), primary: true);
        go.Margin = new Thickness(8, 0, 0, 0);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AskAboutCase(box.Text); } };
        DockPanel.SetDock(go, Dock.Right);
        ask.Children.Add(go);
        var hintGrid = new Grid();
        hintGrid.Children.Add(box);
        var hint = new TextBlock { Text = "Ask about your case – e.g. what did the last order say about contact?", Margin = new Thickness(12, 8, 0, 0), IsHitTestVisible = false, FontSize = 13.5 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        hint.SetBinding(VisibilityProperty, new System.Windows.Data.Binding("Text")
            { Source = box, Converter = (System.Windows.Data.IValueConverter)Application.Current.FindResource("PlaceholderVis") });
        hintGrid.Children.Add(hint);
        ask.Children.Add(hintGrid);
        sp.Children.Add(ask);
        return Ui.Card(sp, new Thickness(0, 0, 14, 14));
    }
}
