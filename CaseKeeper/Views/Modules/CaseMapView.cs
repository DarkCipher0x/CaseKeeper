using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// Case map: how the case is laid out at a glance. A matrix of the documents by category group (or by party)
/// against time, a lane of the case records on the same time line, the people involved, and the latest case
/// overview. Every cell is a button that opens the documents behind it. Built from what's in the library only;
/// nothing here is sent anywhere.
/// </summary>
public sealed class CaseMapView : DockPanel
{
    private static Data.Database Db => App.Library.Db;

    private readonly StackPanel _body = new();
    private readonly ComboBox _rows = new() { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _period = new() { Width = 130, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _party = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    private bool _loading;

    private enum Period { Month, Quarter, Year }

    public CaseMapView()
    {
        Margin = new Thickness(28, 22, 28, 18);
        var head = new StackPanel();
        head.Children.Add(Ui.Text("Case map", "H1", 21));
        head.Children.Add(Ui.Muted("How your case is laid out: what you have, from when, and what still needs organising. Click any number to see those documents."));
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        foreach (var r in new[] { "Rows: category groups", "Rows: parties" }) _rows.Items.Add(r);
        foreach (var p in new[] { "Periods: automatic", "Periods: months", "Periods: quarters", "Periods: years" }) _period.Items.Add(p);
        _rows.SelectedIndex = Db.GetSetting("casemap.rows", "0") == "1" ? 1 : 0;
        _period.SelectedIndex = int.TryParse(Db.GetSetting("casemap.period", "0"), out var pi) && pi is >= 0 and <= 3 ? pi : 0;
        AutomationProperties.SetName(_rows, "Rows");
        AutomationProperties.SetName(_period, "Period size");
        AutomationProperties.SetName(_party, "Only one party's documents");
        _rows.SelectionChanged += (_, _) => { if (!_loading) { Db.SetSetting("casemap.rows", _rows.SelectedIndex.ToString(CultureInfo.InvariantCulture)); Reload(); } };
        _period.SelectionChanged += (_, _) => { if (!_loading) { Db.SetSetting("casemap.period", _period.SelectedIndex.ToString(CultureInfo.InvariantCulture)); Reload(); } };
        _party.SelectionChanged += (_, _) => { if (!_loading) Reload(); };
        bar.Children.Add(_rows);
        bar.Children.Add(_period);
        bar.Children.Add(_party);
        bar.Children.Add(Ui.Button("Check library", "", (_, _) => MainWindow.Shell?.OpenLibraryCheck(),
            tooltip: "Organise the documents, find copies and versions, and fill in the case sections"));
        bar.Children.Add(Ui.Button("Refresh", "", (_, _) => Reload()));
        head.Children.Add(bar);
        SetDock(head, Dock.Top);
        Children.Add(head);
        Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            _body.Children.Clear();
            var docs = Db.GetDocuments();
            var parties = Db.GetParties();
            var categories = Db.GetCategories();
            FillPartyFilter(parties);
            long? onlyParty = _party.SelectedItem is ComboBoxItem { Tag: long id } ? id : null;
            if (onlyParty != null) docs = docs.Where(d => d.PartyId == onlyParty).ToList();

            _body.Children.Add(OverviewCard());
            if (docs.Count == 0)
            {
                _body.Children.Add(Ui.Card(Ui.Muted(onlyParty == null ? "No documents yet. Import some, then come back to see your case laid out." : "No documents are linked to this party."), new Thickness(0, 0, 0, 12)));
                return;
            }
            _body.Children.Add(Matrix(docs, parties, categories));
            _body.Children.Add(PeopleCard(docs, parties));
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("UI", "Case map couldn't be built", ex);
            _body.Children.Add(Ui.Muted($"The case map couldn't be built (ref {id})."));
        }
        finally { _loading = false; }
    }

    private void FillPartyFilter(List<Party> parties)
    {
        var keep = _party.SelectedItem is ComboBoxItem { Tag: long id } ? id : (long?)null;
        _party.Items.Clear();
        _party.Items.Add(new ComboBoxItem { Content = "All parties", Tag = null });
        foreach (var p in parties) _party.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Id });
        _party.SelectedItem = _party.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is long t && t == keep) ?? _party.Items[0];
    }

    // ------------------------------------------------------------------ overview
    private UIElement OverviewCard()
    {
        var panel = new StackPanel();
        var report = CaseOverview.Latest(Db);
        var title = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (AiService.Enabled)
            buttons.Children.Add(Ui.Button(report == null ? "Write case overview" : "Refresh overview", "",
                (_, _) => _ = MainWindow.Shell?.WriteCaseOverviewAsync(), tooltip: "Uses AI: written from each document's summary and your records, not the full text"));
        DockPanel.SetDock(buttons, Dock.Right);
        title.Children.Add(buttons);
        title.Children.Add(Ui.Text(report == null ? "Case overview" : $"Case overview · {report.CreatedAt:d MMM yyyy}", size: 15));
        panel.Children.Add(title);
        if (report != null)
        {
            var text = new TextBox
            {
                Text = report.Output, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0)
            };
            text.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(text, "Case overview");
            var expander = new Expander { Header = "Show the overview", IsExpanded = Db.GetSetting("casemap.overview.open", "1") == "1", Content = text, Margin = new Thickness(0, 6, 0, 0) };
            expander.Expanded += (_, _) => Db.SetSetting("casemap.overview.open", "1");
            expander.Collapsed += (_, _) => Db.SetSetting("casemap.overview.open", "0");
            panel.Children.Add(expander);
            panel.Children.Add(Ui.Muted($"AI-generated by {report.Model} – check it against the documents. Saved in AI analysis → Reports.", 11.5));
        }
        else
        {
            var s = LibraryCheck.Count(Db);
            panel.Children.Add(Ui.Muted(
                $"{s.Documents} documents · {s.Parties} parties · {s.Children} children · {s.Hearings} hearings ({s.UpcomingHearings} coming up" +
                (s.NextHearing is { } nh ? $", next {nh:d MMM yyyy}" : "") + $") · {s.Orders} orders · {s.OpenActions} open actions" +
                (s.OverdueActions > 0 ? $" ({s.OverdueActions} overdue)" : "") + $" · {s.Complaints} complaints · {s.ScottItems} Scott schedule items · " +
                $"{s.OpenSuggestionTotal} suggestions waiting in Import review."));
            panel.Children.Add(Ui.Muted(AiService.Enabled
                ? "Write a case overview to see the whole case summarised here."
                : "Turn on AI in Settings → AI to have a case overview written here.", 11.5));
        }
        return Ui.Card(panel, new Thickness(0, 0, 0, 12));
    }

    // ------------------------------------------------------------------ the matrix
    private sealed record Row(string Name, List<CaseDocument> Docs, bool Muted = false);

    private UIElement Matrix(List<CaseDocument> docs, List<Party> parties, List<Category> categories)
    {
        static DateTime? When(CaseDocument d) => d.DocumentDate ?? d.DateReceived;
        var dated = docs.Where(d => When(d) != null).Select(d => When(d)!.Value).ToList();
        var period = _period.SelectedIndex switch { 1 => Period.Month, 2 => Period.Quarter, 3 => Period.Year, _ => Auto(dated) };
        var columns = Columns(dated, period);
        var unknown = CategoryMatcher.Unknown(categories)?.Id;
        var catById = categories.ToDictionary(c => c.Id);

        var rows = new List<Row>();
        if (_rows.SelectedIndex == 1)
        {
            foreach (var p in parties) if (docs.Where(d => d.PartyId == p.Id).ToList() is { Count: > 0 } mine) rows.Add(new Row(p.DisplayName, mine));
            if (docs.Where(d => d.PartyId == null).ToList() is { Count: > 0 } none) rows.Add(new Row("No party set", none, true));
        }
        else
        {
            var order = categories.GroupBy(c => c.GroupName).ToDictionary(g => g.Key ?? "", g => g.Min(c => c.SortOrder));
            foreach (var g in docs.Where(d => d.CategoryId != unknown && catById.ContainsKey(d.CategoryId))
                         .GroupBy(d => catById[d.CategoryId].GroupName ?? "").OrderBy(g => order.GetValueOrDefault(g.Key, int.MaxValue)))
                rows.Add(new Row(string.IsNullOrWhiteSpace(g.Key) ? "Other" : g.Key, g.ToList()));
            if (docs.Where(d => d.CategoryId == unknown || !catById.ContainsKey(d.CategoryId)).ToList() is { Count: > 0 } un) rows.Add(new Row("Uncategorised", un, true));
        }

        int max = Math.Max(1, rows.SelectMany(r => r.Docs.GroupBy(d => When(d) is { } w ? Key(w, period) : DateTime.MinValue)).Select(g => g.Count()).DefaultIfEmpty(1).Max());

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        foreach (var _ in columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // undated
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });   // total

        int r0 = 0;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < columns.Count; c++) Put(grid, HeaderText(Label(columns[c], period)), r0, c + 1);
        Put(grid, HeaderText("Undated"), r0, columns.Count + 1);
        Put(grid, HeaderText("Total"), r0, columns.Count + 2);

        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int r = grid.RowDefinitions.Count - 1;
            var name = Ui.Text(row.Name, size: 12.5, wrap: false);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.ToolTip = row.Name;
            if (row.Muted) name.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Put(grid, name, r, 0);
            var byPeriod = row.Docs.Where(d => When(d) != null).GroupBy(d => Key(When(d)!.Value, period)).ToDictionary(g => g.Key, g => g.ToList());
            for (int c = 0; c < columns.Count; c++)
                Put(grid, Cell(byPeriod.GetValueOrDefault(columns[c]) ?? new(), max, $"{row.Name}, {Label(columns[c], period)}"), r, c + 1);
            Put(grid, Cell(row.Docs.Where(d => When(d) == null).ToList(), max, $"{row.Name}, undated", muted: true), r, columns.Count + 1);
            Put(grid, Cell(row.Docs, 0, $"{row.Name}, all periods", total: true), r, columns.Count + 2);
        }

        // Records lane: the case records on the same time line
        var lanes = RecordLanes(period);
        if (lanes.Count > 0)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(grid, HeaderText("CASE RECORDS", left: true), grid.RowDefinitions.Count - 1, 0);
            foreach (var lane in lanes)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int r = grid.RowDefinitions.Count - 1;
                Put(grid, Ui.Text(lane.Name, size: 12.5, wrap: false), r, 0);
                var by = lane.Items.Where(i => i.When != null).GroupBy(i => Key(i.When!.Value, period)).ToDictionary(g => g.Key, g => g.ToList());
                for (int c = 0; c < columns.Count; c++)
                    if (by.TryGetValue(columns[c], out var items)) Put(grid, Marker(items, lane), r, c + 1);
                int undated = lane.Items.Count(i => i.When == null);
                if (undated > 0) Put(grid, Marker(lane.Items.Where(i => i.When == null).ToList(), lane), r, columns.Count + 1);
                Put(grid, Marker(lane.Items, lane, total: true), r, columns.Count + 2);
            }
        }

        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(_rows.SelectedIndex == 1 ? "Documents by party" : "Documents by category group", size: 15));
        panel.Children.Add(Ui.Muted($"By {(period switch { Period.Month => "month", Period.Quarter => "quarter", _ => "year" })} of each document's date (or the date received). " +
                                    "Darker cells hold more documents. Uncategorised and undated documents still need organising."));
        panel.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 0, 0) });
        return Ui.Card(panel, new Thickness(0, 0, 0, 12));
    }

    private static void Put(Grid g, UIElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    private static TextBlock HeaderText(string text, bool left = false)
    {
        var t = Ui.Text(text, size: 11, wrap: false);
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        t.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        t.Margin = new Thickness(2, 0, 2, 4);
        return t;
    }

    private static UIElement Cell(List<CaseDocument> docs, int max, string what, bool muted = false, bool total = false)
    {
        string name = $"{what}: {docs.Count} document{(docs.Count == 1 ? "" : "s")}";
        if (docs.Count == 0)
        {
            var empty = new Border { Height = 30, Margin = new Thickness(2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
            empty.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            empty.Opacity = 0.35;
            return empty;
        }
        double share = total || max <= 0 ? 0 : (double)docs.Count / max;
        var fill = new Border { CornerRadius = new CornerRadius(4), Opacity = total ? 0 : 0.18 + 0.72 * share };
        fill.SetResourceReference(Border.BackgroundProperty, muted ? "MutedBrush" : "Primary");
        var label = new TextBlock { Text = docs.Count.ToString(CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = total ? FontWeights.SemiBold : FontWeights.Normal };
        if (!total && share > 0.55) label.Foreground = Brushes.White;
        else label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var content = new Grid();
        content.Children.Add(fill);
        content.Children.Add(label);
        var b = new Button
        {
            Content = content, Height = 30, Margin = new Thickness(2), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, ToolTip = name, Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(b, name);
        var ids = docs.Select(d => d.Id).ToList();
        b.Click += (_, _) => MainWindow.Shell?.ShowDocumentSet(what, ids);
        return b;
    }

    // ------------------------------------------------------------------ records lane
    private sealed record LaneItem(DateTime? When, string Text);
    private sealed record Lane(string Name, string Screen, List<LaneItem> Items);

    private static List<Lane> RecordLanes(Period period)
    {
        var lanes = new List<Lane>
        {
            new("Hearings", "hearings", Db.All<Hearing>().Select(h => new LaneItem(h.HearingDate, $"{h.HearingDate:d MMM yyyy} · {h.HearingType} ({h.Status})")).ToList()),
            new("Orders", "orders", Db.All<CourtOrder>().Select(o => new LaneItem(o.OrderDate, $"{o.OrderDate:d MMM yyyy} · {o.OrderType}")).ToList()),
            new("Complaints", "complaints", Db.GetComplaints().Select(c => new LaneItem(c.DateSent, $"{c.DateSentText} · {c.Recipient}: {c.Subject} ({c.Status})")).ToList()),
            new("Scott schedule", "scott", Db.All<ScottItem>().Select(s => new LaneItem(s.IncidentDate, $"{s.ItemNo}. {Clip(s.Allegation, 80)}")).ToList()),
            new("Deadlines", "actions", Db.All<CaseAction>().Where(a => a.DueDate != null && a.CompletedDate == null)
                .Select(a => new LaneItem(a.DueDate, $"Due {a.DueDate:d MMM yyyy} · {a.Title}")).ToList()),
        };
        return lanes.Where(l => l.Items.Count > 0).ToList();
    }

    private static UIElement Marker(List<LaneItem> items, Lane lane, bool total = false)
    {
        var tip = string.Join("\n", items.OrderBy(i => i.When).Take(12).Select(i => i.Text)) + (items.Count > 12 ? $"\n…and {items.Count - 12} more" : "");
        var dot = new TextBlock { Text = total ? items.Count.ToString(CultureInfo.CurrentCulture) : $"● {items.Count}", HorizontalAlignment = HorizontalAlignment.Center, FontWeight = total ? FontWeights.SemiBold : FontWeights.Normal };
        dot.SetResourceReference(TextBlock.ForegroundProperty, total ? "TextBrush" : "Primary");
        var b = new Button { Content = dot, Height = 26, Margin = new Thickness(2), Padding = new Thickness(0), ToolTip = tip, Cursor = Cursors.Hand };
        AutomationProperties.SetName(b, $"{lane.Name}: {items.Count}. {tip.Replace('\n', ';')}");
        b.Click += (_, _) => MainWindow.Shell?.GoTo(lane.Screen);
        return b;
    }

    // ------------------------------------------------------------------ people
    private static UIElement PeopleCard(List<CaseDocument> docs, List<Party> parties)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("People", size: 15));
        if (parties.Count == 0)
        {
            panel.Children.Add(Ui.Muted("No parties yet. A Library check (with “Parties & children”) suggests them from your documents."));
            return Ui.Card(panel, new Thickness(0, 0, 0, 12));
        }
        panel.Children.Add(Ui.Muted("Linked: documents set to this party. Mentioned: documents whose title or description includes their name."));
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var p in parties)
        {
            int linked = docs.Count(d => d.PartyId == p.Id);
            int mentioned = string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length < 3 ? 0
                : docs.Count(d => $"{d.Title} {d.Description}".Contains(p.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = p.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
            text.Children.Add(Ui.Muted($"{linked} linked · {mentioned} mentioned", 11.5));
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 8, 12, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(b, $"{p.DisplayName}: {linked} linked documents, {mentioned} mentioned");
            var id = p.Id;
            b.Click += (_, _) => MainWindow.Shell?.ShowPartyDocuments(id);
            wrap.Children.Add(b);
        }
        panel.Children.Add(wrap);
        return Ui.Card(panel, new Thickness(0, 0, 0, 12));
    }

    // ------------------------------------------------------------------ time axis
    private static Period Auto(List<DateTime> dates)
    {
        if (dates.Count == 0) return Period.Year;
        double months = (dates.Max() - dates.Min()).TotalDays / 30.4;
        return months <= 18 ? Period.Month : months <= 60 ? Period.Quarter : Period.Year;
    }

    private static DateTime Key(DateTime d, Period p) => p switch
    {
        Period.Month => new DateTime(d.Year, d.Month, 1),
        Period.Quarter => new DateTime(d.Year, (d.Month - 1) / 3 * 3 + 1, 1),
        _ => new DateTime(d.Year, 1, 1)
    };

    private static DateTime Next(DateTime k, Period p) => p switch { Period.Month => k.AddMonths(1), Period.Quarter => k.AddMonths(3), _ => k.AddYears(1) };

    /// <summary>Every period from the first document to the last (at most 120 columns, from the most recent).</summary>
    private static List<DateTime> Columns(List<DateTime> dates, Period p)
    {
        var list = new List<DateTime>();
        if (dates.Count == 0) return list;
        var last = Key(dates.Max(), p);
        for (var k = Key(dates.Min(), p); k <= last; k = Next(k, p)) list.Add(k);
        return list.Count > 120 ? list.Skip(list.Count - 120).ToList() : list;
    }

    private static string Label(DateTime k, Period p) => p switch
    {
        Period.Month => k.ToString("MMM yy", CultureInfo.CurrentCulture),
        Period.Quarter => $"Q{(k.Month - 1) / 3 + 1} {k:yy}",
        _ => k.Year.ToString(CultureInfo.InvariantCulture)
    };

    private static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s[..n] + "…" : s;
}
