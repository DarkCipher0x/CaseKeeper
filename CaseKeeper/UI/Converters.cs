using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace CaseKeeper.UI;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => (value is Visibility.Visible) ^ Invert;
}

public class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Default: null = Visible (e.g. "nothing selected" placeholders). Invert: null = Collapsed.</summary>
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value == null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class EmptyStringToVisibilityConverter : IValueConverter
{
    /// <summary>Default: collapsed when the string is empty. Invert: visible only when empty (placeholders).</summary>
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.IsNullOrWhiteSpace(value as string) ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Renders text where «matched words» are shown in bold (used for search snippets).</summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata("", OnChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = e.NewValue as string ?? "";
        bool bold = false;
        foreach (var part in text.Split('«', '»'))
        {
            if (part.Length > 0)
            {
                var run = new Run(part);
                if (bold)
                {
                    run.FontWeight = FontWeights.Bold;
                    // Resource reference, so the highlight follows the light/dark theme
                    run.SetResourceReference(TextElement.BackgroundProperty, "SearchHighlight");
                }
                tb.Inlines.Add(run);
            }
            bold = !bold;
        }
    }
}
