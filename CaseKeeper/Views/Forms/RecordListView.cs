using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Forms;

/// <summary>A column in a record list: what it shows, how wide, and how it's styled.</summary>
public sealed class ColumnSpec
{
    public string Header { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>Positive = share of the spare width (star); negative = fixed width in pixels.</summary>
    public double Width { get; init; } = 1;
    public bool Bold { get; init; }
    /// <summary>A bool property: when true, the cell is shown in the warning (red) colour.</summary>
    public string DangerWhen { get; init; }
    /// <summary>A bool property: when true, the cell is shown in the accent colour.</summary>
    public string AccentWhen { get; init; }
    public string SortPath { get; init; }
    public bool Wrap { get; init; }

    public static ColumnSpec Of(string header, string path, double width = 1, bool bold = false, string danger = null,
        string sort = null, string accent = null, bool wrap = false) =>
        new() { Header = header, Path = path, Width = width, Bold = bold, DangerWhen = danger, SortPath = sort, AccentWhen = accent, Wrap = wrap };
}

/// <summary>Everything that makes one record list different from another.</summary>
public sealed class RecordListConfig<T> where T : IEntity, new()
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string AddLabel { get; init; } = "Add";
    public string Noun { get; init; } = "item";
    public string EmptyText { get; init; } = "Nothing here yet.";
    public Func<T> Create { get; init; } = () => new T();
    public Func<T, IEnumerable<FieldSpec>> Fields { get; init; }
    public List<ColumnSpec> Columns { get; init; } = new();
    public Func<IEnumerable<T>, IEnumerable<T>> Order { get; init; } = x => x;
    /// <summary>Choices for the filter dropdown; the first is "show everything".</summary>
    public string[] Filters { get; init; }
    public Func<T, string, bool> FilterMatch { get; init; }
    public Func<T, string> SearchText { get; init; }
    public Func<IReadOnlyList<T>, string> Summary { get; init; }
    public Action<T> AfterSave { get; init; }
    public Action<T> AfterDelete { get; init; }
    public Func<T, long?> DocumentOf { get; init; }
    /// <summary>Extra right-click items for the selected record(s).</summary>
    public Func<RecordListView<T>, IReadOnlyList<T>, IEnumerable<object>> ExtraMenu { get; init; }
    /// <summary>Extra toolbar buttons.</summary>
    public Func<RecordListView<T>, IEnumerable<Button>> ExtraButtons { get; init; }
    public bool Landscape { get; init; }
    public double DialogHeight { get; init; } = 760;
    /// <summary>Columns for exports; defaults to the on-screen columns.</summary>
    public List<ColumnSpec> ExportColumns { get; init; }
    public Func<IReadOnlyList<T>, IEnumerable<Block>> ExportPreamble { get; init; }
}

/// <summary>
/// The standard screen for a list of records: title and explanation, a toolbar (add, edit,
/// delete, filter, search, export), a sortable table with a right-click menu and keyboard
/// shortcuts, and a summary line. Hearings, orders, directions, contact, costs and the Scott
/// schedule all use it.
/// </summary>
public class RecordListView<T> : UserControl where T : IEntity, new()
{
    private readonly RecordListConfig<T> _cfg;
    private readonly DataGrid _grid;
    private readonly TextBox _search;
    private readonly ComboBox _filter;
    private readonly TextBlock _summary, _empty;
    private List<T> _all = new();

    public event Action Changed;

    public IReadOnlyList<T> All => _all;
    public T Selected => _grid.SelectedItem is T t ? t : default;
    public IReadOnlyList<T> SelectedItems => _grid.SelectedItems.OfType<T>().ToList();
    public IReadOnlyList<T> Visible => (_grid.ItemsSource as IEnumerable<T>)?.ToList() ?? new List<T>();

    public RecordListView(RecordListConfig<T> cfg)
    {
        _cfg = cfg;
        var root = new DockPanel { Margin = new Thickness(36, 28, 36, 22) };

        // ---- header
        var header = new StackPanel();
        header.Children.Add(Ui.Text(cfg.Title, "H1"));
        if (!string.IsNullOrWhiteSpace(cfg.Description)) header.Children.Add(Ui.Muted(cfg.Description, 13));

        var toolbar = new WrapPanel { Margin = new Thickness(0, 16, 0, 8) };
        toolbar.Children.Add(Ui.Button(cfg.AddLabel, "", (_, _) => Add(), primary: true, tooltip: "Ctrl+N"));
        toolbar.Children.Add(Ui.Button("Edit", "", (_, _) => EditSelected(), tooltip: "Enter or double-click"));
        var del = Ui.Button("Delete", "", (_, _) => DeleteSelected(), tooltip: "Delete key");
        del.Style = Ui.Style("DangerButton");
        del.Margin = new Thickness(0, 0, 18, 6);
        toolbar.Children.Add(del);
        if (cfg.ExtraButtons != null)
            foreach (var b in cfg.ExtraButtons(this)) toolbar.Children.Add(b);

        if (cfg.Filters is { Length: > 0 })
        {
            _filter = new ComboBox { ItemsSource = cfg.Filters, SelectedIndex = 0, Width = 190, Margin = new Thickness(0, 0, 6, 6) };
            _filter.SelectionChanged += (_, _) => Refresh();
            toolbar.Children.Add(_filter);
        }
        var searchGrid = Ui.SearchBox(out _search, "Search…", 220);
        _search.TextChanged += (_, _) => Refresh();
        toolbar.Children.Add(searchGrid);
        var export = Ui.Button("Export", "", null, tooltip: "PDF, Word, web page or spreadsheet");
        export.Click += async (_, _) => await ExportAsync(Visible);
        toolbar.Children.Add(export);
        header.Children.Add(toolbar);

        _summary = Ui.Muted("", 12.5);
        _summary.Margin = new Thickness(0, 0, 0, 8);
        header.Children.Add(_summary);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---- grid
        _grid = new DataGrid { SelectionMode = DataGridSelectionMode.Extended };
        foreach (var c in cfg.Columns) _grid.Columns.Add(BuildColumn(c));
        _grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && Ui.FindParent<DataGridRow>(d) != null) EditSelected();
        };
        _grid.PreviewKeyDown += OnGridKey;
        _grid.ContextMenuOpening += (_, _) => _grid.ContextMenu = BuildMenu();
        _grid.ContextMenu = new ContextMenu();
        _grid.SelectionChanged += (_, _) => UpdateSummary();

        _empty = Ui.Muted(cfg.EmptyText, 14);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.MaxWidth = 520;
        _empty.IsHitTestVisible = false;

        var holder = new Grid();
        holder.Children.Add(_grid);
        holder.Children.Add(_empty);
        root.Children.Add(holder);

        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { Add(); e.Handled = true; }
        };
        Loaded += (_, _) => { if (_all.Count == 0) Reload(); };
    }

    private DataGridColumn BuildColumn(ColumnSpec c)
    {
        var col = new DataGridTextColumn
        {
            Header = c.Header,
            Binding = new Binding(c.Path) { Mode = BindingMode.OneWay },
            SortMemberPath = c.SortPath ?? c.Path,
            Width = c.Width > 0 ? new DataGridLength(c.Width, DataGridLengthUnitType.Star) : new DataGridLength(-c.Width)
        };
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (c.Wrap) style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        if (c.Bold) style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        if (c.AccentWhen != null)
        {
            var t = new DataTrigger { Binding = new Binding(c.AccentWhen), Value = true };
            t.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentText")));
            t.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            style.Triggers.Add(t);
        }
        if (c.DangerWhen != null)
        {
            var t = new DataTrigger { Binding = new Binding(c.DangerWhen), Value = true };
            t.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Danger")));
            t.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            style.Triggers.Add(t);
        }
        col.ElementStyle = style;
        return col;
    }

    // ------------------------------------------------------------------ data
    public void Reload()
    {
        try { _all = App.Library.Db.All<T>(); }
        catch (Exception ex)
        {
            AppLog.Error("Records", $"Couldn't load {_cfg.Title}", ex);
            _all = new List<T>();
        }
        Refresh();
    }

    public void Refresh()
    {
        IEnumerable<T> items = _cfg.Order(_all);
        if (_filter is { SelectedIndex: > 0 } && _cfg.FilterMatch != null)
        {
            var f = _filter.SelectedItem as string;
            items = items.Where(x => _cfg.FilterMatch(x, f));
        }
        var q = _search.Text.Trim();
        if (q.Length > 0)
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(x =>
            {
                var text = _cfg.SearchText?.Invoke(x) ?? string.Join(" ", _cfg.Columns.Select(c => Value(x, c.Path)));
                return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
            });
        }
        var selectedIds = SelectedItems.Select(s => s.Id).ToHashSet();
        var list = items.ToList();
        _grid.ItemsSource = list;
        foreach (var x in list.Where(x => selectedIds.Contains(x.Id))) _grid.SelectedItems.Add(x);
        _empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _all.Count == 0 ? _cfg.EmptyText : "Nothing matches the search or filter.";
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var visible = Visible;
        var text = _cfg.Summary?.Invoke(visible) ?? $"{visible.Count} {_cfg.Noun}(s)";
        if (SelectedItems.Count > 1) text += $"   ·   {SelectedItems.Count} selected";
        _summary.Text = text;
    }

    public static string Value(object o, string path)
    {
        if (o == null || string.IsNullOrEmpty(path)) return "";
        var p = o.GetType().GetProperty(path);
        var v = p?.GetValue(o);
        return v switch
        {
            null => "",
            DateTime d => d.ToString("dd/MM/yyyy"),
            bool b => b ? "Yes" : "",
            _ => v.ToString()
        };
    }

    // ------------------------------------------------------------------ actions
    public void Add() => Edit(_cfg.Create(), isNew: true);

    public void EditSelected()
    {
        if (Selected is { } s) Edit(Ui.Clone(s), isNew: false);
    }

    private void Edit(T record, bool isNew)
    {
        var owner = Ui.Owner(this);
        var title = isNew ? _cfg.AddLabel : $"Edit {_cfg.Noun}";
        var dlg = new RecordDialog(title, record, _cfg.Fields(record), MainWindow.Shell?.Documents, height: _cfg.DialogHeight) { Owner = owner };
        if (dlg.ShowDialog() != true) return;
        Save(record);
    }

    public void Save(T record)
    {
        try
        {
            App.Library.Db.Save(record);
            _cfg.AfterSave?.Invoke(record);
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("Records", $"Couldn't save {_cfg.Noun}", ex);
            MessageBox.Show(Ui.Owner(this), $"Couldn't save: {ex.Message}\n\nReference: {id}", _cfg.Title);
            return;
        }
        Reload();
        _grid.SelectedItem = _all.FirstOrDefault(x => x.Id == record.Id);
        if (_grid.SelectedItem != null) _grid.ScrollIntoView(_grid.SelectedItem);
        Changed?.Invoke();
    }

    public void Duplicate()
    {
        if (Selected is not { } s) return;
        var copy = Ui.Clone(s);
        copy.Id = 0;
        Edit(copy, isNew: true);
    }

    public void DeleteSelected()
    {
        var items = SelectedItems;
        if (items.Count == 0) return;
        var what = items.Count == 1 ? $"“{items[0].AuditName}”" : $"these {items.Count} {_cfg.Noun}s";
        if (MessageBox.Show(Ui.Owner(this), $"Delete {what}?\n\nThe deletion is recorded in the audit trail.", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var i in items)
        {
            try
            {
                App.Library.Db.Remove<T>(i.Id);
                _cfg.AfterDelete?.Invoke(i);
            }
            catch (Exception ex) { AppLog.Error("Records", $"Couldn't delete {_cfg.Noun}", ex); }
        }
        Reload();
        Changed?.Invoke();
    }

    private void OnGridKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EditSelected(); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control) { Duplicate(); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopySelected(); e.Handled = true; }
    }

    private void CopySelected()
    {
        var cols = ExportColumns;
        Ui.CopyToClipboard(string.Join("\n", SelectedItems.Select(x => string.Join("\t", cols.Select(c => Value(x, c.Path))))));
    }

    private List<ColumnSpec> ExportColumns => _cfg.ExportColumns ?? _cfg.Columns;

    private ContextMenu BuildMenu()
    {
        var sel = SelectedItems;
        bool one = sel.Count == 1, any = sel.Count > 0;
        var items = new List<object>
        {
            Ui.Item("Edit…", "", EditSelected, "Enter", one, bold: true),
            Ui.Item($"{_cfg.AddLabel}…", "", Add, "Ctrl+N"),
            Ui.Item("Duplicate…", "", Duplicate, "Ctrl+D", one)
        };
        if (_cfg.DocumentOf != null)
        {
            var docId = one ? _cfg.DocumentOf(sel[0]) : null;
            items.Add(Ui.Item("Open linked document", "", () => MainWindow.Shell?.OpenDocumentById(docId ?? 0), enabled: docId.HasValue));
        }
        if (_cfg.ExtraMenu != null)
        {
            var extra = _cfg.ExtraMenu(this, sel).ToList();
            if (extra.Count > 0) { items.Add(new Separator()); items.AddRange(extra); }
        }
        items.Add(new Separator());
        items.Add(Ui.Item("Copy", "", CopySelected, "Ctrl+C", any));
        items.Add(Ui.Item("Export selected…", "", async () => await ExportAsync(sel), enabled: any));
        items.Add(Ui.Item("Export everything shown…", "", async () => await ExportAsync(Visible)));
        items.Add(Ui.Item("History of this item…", "", () => MainWindow.Shell?.ShowAuditFor(typeof(T), sel[0].Id), enabled: one));
        items.Add(new Separator());
        items.Add(Ui.Item("Delete…", "", DeleteSelected, "Del", any));
        return Ui.Menu(items.ToArray());
    }

    public async Task ExportAsync(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            MessageBox.Show(Ui.Owner(this), "There's nothing to export.", _cfg.Title);
            return;
        }
        var cols = ExportColumns;
        var caseNo = App.Library.Db.GetSetting("case.number", "");
        await ExportHelper.ExportTableAsync(Ui.Owner(this), _cfg.Title,
            $"{(string.IsNullOrWhiteSpace(caseNo) ? "" : "Case " + caseNo + " · ")}{items.Count} {_cfg.Noun}(s) · produced {DateTime.Today:d MMMM yyyy}",
            cols.Select(c => c.Header).ToList(),
            items.Select(x => (IList<string>)cols.Select(c => Value(x, c.Path)).ToList()).ToList(),
            _cfg.Landscape, _cfg.ExportPreamble?.Invoke(items));
    }
}
