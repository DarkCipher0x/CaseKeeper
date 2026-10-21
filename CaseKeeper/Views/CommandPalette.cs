using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.UI;

namespace CaseKeeper.Views;

/// <summary>One thing the command palette can do.</summary>
public sealed class PaletteCommand
{
    public string Title { get; init; } = "";
    public string Group { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Gesture { get; init; } = "";
    public string Keywords { get; init; } = "";
    public Action Run { get; init; }
}

/// <summary>
/// Ctrl+K: a search box that finds any screen, action, document, record or guide article by
/// typing part of its name. Arrow keys to choose, Enter to go, Esc to close.
/// </summary>
public class CommandPalette : Window
{
    private readonly TextBox _box;
    private readonly ListBox _list;
    private readonly Func<string, IEnumerable<PaletteCommand>> _source;
    private bool _closing;

    public CommandPalette(Func<string, IEnumerable<PaletteCommand>> source)
    {
        _source = source;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Width = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");

        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(10) };
        card.SetResourceReference(Border.BackgroundProperty, "PopupBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "Primary");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35 };

        var stack = new StackPanel();
        var grid = Ui.SearchBox(out _box, "Type a command, screen, document or question…  (Esc to close)");
        _box.FontSize = 16;
        _box.Padding = new Thickness(30, 9, 8, 9);
        stack.Children.Add(grid);
        _list = new ListBox { MaxHeight = 440, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _list.SetResourceReference(BackgroundProperty, "PopupBackground");
        _list.ItemTemplate = ItemLayout();
        _list.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && _list.SelectedItem != null) Execute(); };
        stack.Children.Add(_list);
        var hint = Ui.Muted("↑ ↓ to choose · Enter to run · Esc to close", 11);
        hint.Margin = new Thickness(6, 6, 0, 0);
        stack.Children.Add(hint);
        card.Child = stack;
        Content = card;

        _box.TextChanged += (_, _) => Refresh();
        PreviewKeyDown += OnKey;
        // Clicking elsewhere closes it – but closing also deactivates it, so only close once
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing) Close(); };
        Loaded += (_, _) => { Refresh(); _box.Focus(); };
    }

    private static DataTemplate ItemLayout()
    {
        var t = new DataTemplate();
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(MarginProperty, new Thickness(4, 5, 4, 5));
        var gesture = new FrameworkElementFactory(typeof(TextBlock));
        gesture.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Gesture"));
        gesture.SetValue(DockPanel.DockProperty, Dock.Right);
        gesture.SetValue(TextBlock.FontSizeProperty, 11.5);
        gesture.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Glyph"));
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        icon.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 12, 0));
        icon.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        var text = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.FontSizeProperty, 13.5);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var group = new FrameworkElementFactory(typeof(TextBlock));
        group.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Group"));
        group.SetValue(TextBlock.FontSizeProperty, 11.0);
        group.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.AppendChild(title);
        text.AppendChild(group);
        dock.AppendChild(gesture);
        dock.AppendChild(icon);
        dock.AppendChild(text);
        t.VisualTree = dock;
        return t;
    }

    private void Refresh()
    {
        var q = _box.Text.Trim();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = _source(q)
            .Select(c => (Cmd: c, Score: Score(c, words)))
            .Where(x => words.Length == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(60)
            .Select(x => x.Cmd)
            .ToList();
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private static int Score(PaletteCommand c, string[] words)
    {
        if (words.Length == 0) return 1;
        var title = c.Title.ToLowerInvariant();
        var hay = $"{title} {c.Group} {c.Keywords}".ToLowerInvariant();
        int score = 0;
        foreach (var w in words.Select(w => w.ToLowerInvariant()))
        {
            if (!hay.Contains(w)) return 0;
            score += title.StartsWith(w) ? 6 : title.Contains(" " + w) ? 4 : title.Contains(w) ? 3 : 1;
        }
        if (c.Group == "Search") score = 1;
        return score;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: if (!_closing) Close(); e.Handled = true; break;
            case Key.Down:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter: Execute(); e.Handled = true; break;
        }
    }

    private void Execute()
    {
        if (_list.SelectedItem is not PaletteCommand c || _closing) return;
        var owner = Owner;
        Close();
        owner?.Activate();
        // Run after the palette has fully gone, so commands can open their own windows
        owner?.Dispatcher.BeginInvoke(() =>
        {
            try { c.Run?.Invoke(); }
            catch (Exception ex)
            {
                var id = Services.AppLog.Error("Palette", $"Command '{c.Title}' failed", ex);
                MessageBox.Show(owner, $"That didn't work: {ex.Message}\nReference: {id}", "CaseKeeper");
            }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
