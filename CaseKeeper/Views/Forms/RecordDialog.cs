using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CaseKeeper.Models;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Forms;

public enum FieldKind { Text, Multi, Date, Time, Choice, EditableChoice, Check, Int, Money, Document, Heading, Hint }

/// <summary>One field on a record form: which property it edits, how, and what to say about it.</summary>
public sealed class FieldSpec
{
    public string Label { get; init; } = "";
    public string Property { get; init; } = "";
    public FieldKind Kind { get; init; } = FieldKind.Text;
    public IEnumerable<string> Choices { get; init; }
    public string Hint { get; init; }
    public bool Required { get; init; }
    /// <summary>Two half-width fields in a row sit side by side.</summary>
    public bool Half { get; init; }
    public double Height { get; init; } = 90;

    public static FieldSpec Text(string label, string prop, bool required = false, string hint = null, bool half = false) =>
        new() { Label = label, Property = prop, Required = required, Hint = hint, Half = half };
    public static FieldSpec Multi(string label, string prop, double height = 90, string hint = null) =>
        new() { Label = label, Property = prop, Kind = FieldKind.Multi, Height = height, Hint = hint };
    public static FieldSpec Date(string label, string prop, bool required = false, bool half = true) =>
        new() { Label = label, Property = prop, Kind = FieldKind.Date, Required = required, Half = half };
    public static FieldSpec Time(string label, string prop, bool half = true) =>
        new() { Label = label, Property = prop, Kind = FieldKind.Time, Half = half, Hint = null };
    public static FieldSpec Choice(string label, string prop, IEnumerable<string> choices, bool editable = true, bool half = false) =>
        new() { Label = label, Property = prop, Kind = editable ? FieldKind.EditableChoice : FieldKind.Choice, Choices = choices, Half = half };
    public static FieldSpec Check(string label, string prop) => new() { Label = label, Property = prop, Kind = FieldKind.Check };
    public static FieldSpec Int(string label, string prop, bool half = true) => new() { Label = label, Property = prop, Kind = FieldKind.Int, Half = half };
    public static FieldSpec Money(string label, string prop, bool half = true) => new() { Label = label, Property = prop, Kind = FieldKind.Money, Half = half };
    public static FieldSpec Document(string label, string prop) => new() { Label = label, Property = prop, Kind = FieldKind.Document };
    public static FieldSpec Heading(string text) => new() { Label = text, Kind = FieldKind.Heading };
    public static FieldSpec Note(string text) => new() { Label = text, Kind = FieldKind.Hint };
}

/// <summary>
/// A form generated from a list of <see cref="FieldSpec"/>s, editing any record's properties.
/// Used for hearings, orders, directions, contact sessions, costs and the Scott schedule, so
/// they all look and behave the same way.
/// </summary>
public class RecordDialog : FormDialog
{
    private readonly object _record;
    private readonly List<(FieldSpec Spec, PropertyInfo Prop, FrameworkElement Editor)> _editors = new();

    public RecordDialog(string title, object record, IEnumerable<FieldSpec> fields, IReadOnlyList<CaseDocument> documents = null,
        double width = 640, double height = 760)
        : base(title, width, height)
    {
        _record = record;
        var type = record.GetType();
        var pending = new List<FrameworkElement>();
        Grid row = null;

        foreach (var spec in fields)
        {
            if (spec.Kind == FieldKind.Heading)
            {
                row = null;
                var h = Ui.Text(spec.Label, "H2");
                h.Margin = new Thickness(0, 18, 0, 0);
                Fields.Children.Add(h);
                continue;
            }
            if (spec.Kind == FieldKind.Hint)
            {
                row = null;
                AddHint(spec.Label);
                continue;
            }

            var prop = type.GetProperty(spec.Property) ?? throw new ArgumentException($"{type.Name} has no property {spec.Property}");
            var value = prop.GetValue(record);
            FrameworkElement editor = spec.Kind switch
            {
                FieldKind.Multi => Multi(value as string, spec.Height),
                FieldKind.Date => new DatePicker { SelectedDate = value as DateTime? },
                FieldKind.Check => new CheckBox { Content = spec.Label, IsChecked = value is true, Margin = new Thickness(0, 12, 0, 0) },
                FieldKind.Choice => new ComboBox { ItemsSource = spec.Choices?.ToList(), SelectedItem = value },
                FieldKind.EditableChoice => new ComboBox { ItemsSource = spec.Choices?.ToList(), IsEditable = true, Text = value as string ?? "" },
                FieldKind.Int => Box(Convert.ToString(value, CultureInfo.InvariantCulture) == "0" ? "" : Convert.ToString(value, CultureInfo.InvariantCulture)),
                FieldKind.Money => Box(value is long pence && pence != 0 ? (pence / 100m).ToString("0.00", CultureInfo.InvariantCulture) : ""),
                FieldKind.Document => DocumentPicker(documents, value as long?),
                FieldKind.Time => Box(value as string),
                _ => Box(value as string)
            };
            _editors.Add((spec, prop, editor));
            // Screen readers announce the field's label, not just "edit" or "combo box"
            System.Windows.Automation.AutomationProperties.SetName(editor, spec.Label);
            if (spec.Hint != null) System.Windows.Automation.AutomationProperties.SetHelpText(editor, spec.Hint);

            FrameworkElement cell;
            if (spec.Kind == FieldKind.Check) cell = editor;
            else
            {
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = spec.Label.ToUpperInvariant() + (spec.Required ? " *" : ""), Style = Ui.Style("Label") });
                stack.Children.Add(editor);
                if (spec.Hint != null)
                    stack.Children.Add(new TextBlock { Text = spec.Hint, Style = Ui.Style("Muted"), FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) });
                cell = stack;
            }

            if (spec.Half)
            {
                if (row == null || row.Children.Count >= 2)
                {
                    row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    Fields.Children.Add(row);
                }
                Grid.SetColumn(cell, row.Children.Count == 0 ? 0 : 2);
                row.Children.Add(cell);
            }
            else
            {
                row = null;
                Fields.Children.Add(cell);
            }
        }

        Loaded += (_, _) => _editors.Select(e => e.Editor).OfType<Control>().FirstOrDefault(c => c is TextBox or ComboBox or DatePicker)?.Focus();
    }

    private static ComboBox DocumentPicker(IReadOnlyList<CaseDocument> docs, long? selected)
    {
        var choices = new List<CaseDocument> { new() { Id = 0, Title = "— None —" } };
        if (docs != null) choices.AddRange(NaturalComparer.Sort(docs, d => d.Title));
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Title", SelectedValuePath = "Id", SelectedValue = selected ?? 0 };
        SearchableCombo.Attach(combo, o => (o as CaseDocument)?.Title ?? "");
        return combo;
    }

    protected override bool Validate()
    {
        // Check everything first, so nothing is half-saved when one field is wrong
        foreach (var (spec, _, editor) in _editors)
        {
            string text = editor switch { TextBox t => t.Text.Trim(), ComboBox c => c.Text?.Trim() ?? "", _ => null };
            if (spec.Required)
            {
                bool empty = editor switch
                {
                    DatePicker d => d.SelectedDate == null,
                    ComboBox c when spec.Kind == FieldKind.Choice => c.SelectedItem == null,
                    _ => string.IsNullOrWhiteSpace(text)
                };
                if (empty)
                {
                    MessageBox.Show(this, $"Please fill in “{spec.Label}”.", Title);
                    editor.Focus();
                    return false;
                }
            }
            if (spec.Kind == FieldKind.Int && text.Length > 0 && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                MessageBox.Show(this, $"“{spec.Label}” must be a whole number.", Title);
                editor.Focus();
                return false;
            }
            if (spec.Kind == FieldKind.Money && text.Length > 0 && !TryMoney(text, out _))
            {
                MessageBox.Show(this, $"“{spec.Label}” must be an amount, e.g. 255 or 12.50.", Title);
                editor.Focus();
                return false;
            }
            if (spec.Kind == FieldKind.Time && text.Length > 0 && !TimeSpan.TryParse(text.Replace('.', ':'), CultureInfo.InvariantCulture, out _))
            {
                MessageBox.Show(this, $"“{spec.Label}” should be a time such as 10:30.", Title);
                editor.Focus();
                return false;
            }
        }

        foreach (var (spec, prop, editor) in _editors)
        {
            object value = spec.Kind switch
            {
                FieldKind.Date => ((DatePicker)editor).SelectedDate,
                FieldKind.Check => ((CheckBox)editor).IsChecked == true,
                FieldKind.Choice => ((ComboBox)editor).SelectedItem as string ?? "",
                FieldKind.EditableChoice => ((ComboBox)editor).Text?.Trim() ?? "",
                FieldKind.Int => int.TryParse(((TextBox)editor).Text.Trim(), out var i) ? i : 0,
                FieldKind.Money => TryMoney(((TextBox)editor).Text.Trim(), out var p) ? p : 0L,
                FieldKind.Document => ((ComboBox)editor).SelectedValue is long id && id > 0 ? id : null,
                FieldKind.Time => NormaliseTime(((TextBox)editor).Text.Trim()),
                _ => ((TextBox)editor).Text.Trim()
            };
            if (value is DateTime dt && prop.PropertyType == typeof(DateTime)) prop.SetValue(_record, dt);
            else if (value == null && prop.PropertyType == typeof(DateTime)) continue;
            else prop.SetValue(_record, value);
        }
        return true;
    }

    private static bool TryMoney(string text, out long pence)
    {
        pence = 0;
        var clean = text.Replace("£", "").Replace(",", "").Trim();
        if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) return false;
        pence = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string NormaliseTime(string text)
    {
        if (text.Length == 0) return "";
        return TimeSpan.TryParse(text.Replace('.', ':'), CultureInfo.InvariantCulture, out var t) ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : text;
    }
}
