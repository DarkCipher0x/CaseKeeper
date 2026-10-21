using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CaseKeeper.UI;

/// <summary>Small helpers for building themed UI in code: text, icons, buttons and menus.</summary>
public static class Ui
{
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Text(string text, string style = null, double? size = null, bool wrap = true)
    {
        var t = new TextBlock { Text = text ?? "" };
        if (style != null) t.Style = Style(style);
        if (size.HasValue) t.FontSize = size.Value;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    public static TextBlock Muted(string text, double size = 12.5) => Text(text, "Muted", size);

    public static TextBlock Icon(string glyph, double size = 14, string brush = null)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>A button with an icon and a label, optionally the primary (filled) style.</summary>
    public static Button Button(string label, string glyph, RoutedEventHandler click, bool primary = false, string tooltip = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
        {
            var icon = Icon(glyph, 13);
            icon.Margin = new Thickness(0, 0, label == null ? 0 : 8, 0);
            panel.Children.Add(icon);
        }
        if (label != null) panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = panel, Margin = new Thickness(0, 0, 6, 6), ToolTip = tooltip };
        // Screen readers (and UI automation) need a name: the label, or the tooltip for icon-only buttons
        System.Windows.Automation.AutomationProperties.SetName(b, label ?? tooltip ?? "");
        if (primary) b.Style = Style("PrimaryButton");
        if (click != null) b.Click += click;
        return b;
    }

    public static Border Card(UIElement child, Thickness? margin = null)
    {
        var b = new Border { Child = child, Style = Style("Card") };
        if (margin.HasValue) b.Margin = margin.Value;
        return b;
    }

    public static Border Callout(string text)
    {
        return new Border { Style = Style("Callout"), Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, Margin = new Thickness(0, 8, 0, 8) };
    }

    /// <summary>A text box with a grey hint shown while it's empty.</summary>
    public static Grid SearchBox(out TextBox box, string placeholder, double width = double.NaN)
    {
        var grid = new Grid { Width = width, Margin = new Thickness(0, 0, 6, 6) };
        box = new TextBox { Padding = new Thickness(28, 6, 8, 6) };
        var icon = Icon("", 12, "MutedBrush");
        icon.Margin = new Thickness(10, 0, 0, 0);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.IsHitTestVisible = false;
        var hint = new TextBlock { Text = placeholder, Margin = new Thickness(29, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        hint.SetBinding(UIElement.VisibilityProperty, new Binding("Text") { Source = box, Converter = (IValueConverter)Application.Current.FindResource("PlaceholderVis") });
        grid.Children.Add(box);
        grid.Children.Add(icon);
        grid.Children.Add(hint);
        return grid;
    }

    // ------------------------------------------------------------------ menus
    public static MenuItem Item(string header, string glyph, Action click, string gesture = null, bool enabled = true, bool bold = false)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        if (glyph != null) mi.Icon = Icon(glyph, 13, "AccentText");
        if (bold) mi.FontWeight = FontWeights.SemiBold;
        if (click != null) mi.Click += (_, _) => click();
        return mi;
    }

    public static MenuItem Sub(string header, string glyph, IEnumerable<object> items)
    {
        var mi = new MenuItem { Header = header };
        if (glyph != null) mi.Icon = Icon(glyph, 13, "AccentText");
        foreach (var i in items) mi.Items.Add(i);
        if (mi.Items.Count == 0) mi.IsEnabled = false;
        return mi;
    }

    public static MenuItem Check(string header, bool isChecked, Action click)
    {
        var mi = new MenuItem { Header = header, IsCheckable = false, IsChecked = isChecked };
        mi.Click += (_, _) => click();
        return mi;
    }

    public static ContextMenu Menu(params object[] items)
    {
        var cm = new ContextMenu();
        foreach (var i in items.Where(i => i != null)) cm.Items.Add(i);
        return cm;
    }

    // ------------------------------------------------------------------ visual tree
    public static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    public static Window Owner(DependencyObject d) => Window.GetWindow(d) ?? Application.Current.MainWindow;

    // ------------------------------------------------------------------ copying records
    /// <summary>A copy of every settable public property, so a dialog can edit without touching the original.</summary>
    public static T Clone<T>(T source) where T : new()
    {
        var copy = new T();
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
            p.SetValue(copy, p.GetValue(source));
        return copy;
    }

    public static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text ?? ""); }
        catch { /* clipboard busy */ }
    }
}

/// <summary>
/// Makes an editable ComboBox filter its list as you type, matching every word anywhere in an
/// item's text, so a long list (like the 400+ categories) can be found by code or by any word.
/// Usage: SearchableCombo.Attach(combo, item =&gt; text).
/// </summary>
public static class SearchableCombo
{
    public static void Attach(ComboBox combo, Func<object, string> textOf)
    {
        combo.IsEditable = true;
        combo.IsTextSearchEnabled = false;
        combo.StaysOpenOnEdit = true;
        bool selecting = false;

        combo.SelectionChanged += (_, _) =>
        {
            selecting = true;
            combo.Dispatcher.BeginInvoke(() => selecting = false, System.Windows.Threading.DispatcherPriority.Background);
        };

        combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (selecting) return;
            var view = CollectionViewSource.GetDefaultView(combo.ItemsSource);
            if (view == null) return;
            var text = combo.Text ?? "";
            var selectedText = combo.SelectedItem == null ? null : textOf(combo.SelectedItem);
            if (string.IsNullOrWhiteSpace(text) || text == selectedText) { view.Filter = null; return; }
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            view.Filter = o =>
            {
                var s = textOf(o) ?? "";
                return words.All(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));
            };
            if (!combo.IsDropDownOpen) combo.IsDropDownOpen = true;
        }));

        combo.DropDownClosed += (_, _) =>
        {
            var view = CollectionViewSource.GetDefaultView(combo.ItemsSource);
            if (view != null) view.Filter = null;
            if (combo.SelectedItem != null) combo.Text = textOf(combo.SelectedItem);
        };
        combo.LostKeyboardFocus += (_, _) =>
        {
            if (combo.SelectedItem != null && combo.Text != textOf(combo.SelectedItem)) combo.Text = textOf(combo.SelectedItem);
        };
    }

    /// <summary>Groups a category list by its group name, shown as headings inside the dropdown.</summary>
    public static void GroupCategories(ComboBox combo)
    {
        var view = CollectionViewSource.GetDefaultView(combo.ItemsSource);
        if (view == null) return;
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription("GroupName"));
        var header = new DataTemplate();
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        f.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        f.SetValue(TextBlock.FontSizeProperty, 11.5);
        f.SetValue(TextBlock.MarginProperty, new Thickness(8, 8, 4, 2));
        f.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        header.VisualTree = f;
        combo.GroupStyle.Clear();
        combo.GroupStyle.Add(new GroupStyle { HeaderTemplate = header });
        // Grouping turns off virtualisation; keep long lists responsive with a capped height
        combo.MaxDropDownHeight = 420;
    }
}
