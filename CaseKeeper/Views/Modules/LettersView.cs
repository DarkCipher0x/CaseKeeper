using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using CaseKeeper.UI;
using CaseKeeper.Views.Forms;
using Microsoft.Win32;

namespace CaseKeeper.Views.Modules;

/// <summary>Letters &amp; templates: write letters and court documents, manage templates and letterheads.</summary>
public class LettersView : UserControl
{
    private static Data.Database Db => App.Library.Db;
    private readonly TabControl _tabs;
    private readonly LetterEditor _letters;
    private readonly TemplateEditor _templates;
    private readonly LetterheadEditor _letterheads;

    public LettersView()
    {
        var root = new DockPanel { Margin = new Thickness(28, 24, 28, 18) };
        var head = new StackPanel { Margin = new Thickness(8, 0, 0, 10) };
        head.Children.Add(Ui.Text("Letters & templates", "H1"));
        head.Children.Add(Ui.Muted("Write letters, statements and position statements on your own letterhead, starting from a template. " +
                                   "Case details fill themselves in. Save as Word, PDF or a web page, print, or keep a copy in your library.", 13));
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        _letters = new LetterEditor(this);
        _templates = new TemplateEditor(this);
        _letterheads = new LetterheadEditor(this);
        _tabs = new TabControl { Padding = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        _tabs.Items.Add(new TabItem { Header = "Letters & documents", Content = _letters });
        _tabs.Items.Add(new TabItem { Header = "Templates", Content = _templates });
        _tabs.Items.Add(new TabItem { Header = "Letterheads", Content = _letterheads });
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource != _tabs) return;
            _letters.ReloadChoices();
            _templates.Reload();
            _letterheads.Reload();
        };
        root.Children.Add(_tabs);
        Content = root;
        Loaded += (_, _) =>
        {
            BuiltInTemplates.EnsureInstalled(Db);
            _letters.Reload();
            _templates.Reload();
            _letterheads.Reload();
        };
    }

    public void ShowLetters() => _tabs.SelectedIndex = 0;

    /// <summary>Starts a new letter from a template, optionally to a given recipient.</summary>
    public void NewFromTemplate(DocTemplate t, string recipient = null)
    {
        ShowLetters();
        _letters.NewLetter(t, recipient);
    }

    public static Letterhead DefaultLetterhead() => Db.All<Letterhead>().FirstOrDefault(l => l.IsDefault) ?? Db.All<Letterhead>().FirstOrDefault();

    // ================================================================== shared bits
    internal static Grid TwoPane(UIElement left, UIElement right, double leftWidth = 280)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 2);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    internal static StackPanel Labelled(string label, UIElement control, string hint = null)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Style("Label") });
        s.Children.Add(control);
        if (hint != null) s.Children.Add(new TextBlock { Text = hint, Style = Ui.Style("Muted"), FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) });
        return s;
    }

    internal static Grid Pair(UIElement a, UIElement b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(b, 2);
        g.Children.Add(a);
        g.Children.Add(b);
        return g;
    }

    /// <summary>The formatting toolbar for a body text box: bold, italic, headings, lists, fields.</summary>
    internal static WrapPanel FormatBar(TextBox box, Func<IEnumerable<object>> extraInsert = null)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        Button Tool(string glyphOrText, string tip, Action act, bool icon = true)
        {
            var b = new Button { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip };
            b.Content = icon ? Ui.Icon(glyphOrText, 13) : new TextBlock { Text = glyphOrText, FontWeight = FontWeights.SemiBold };
            b.Click += (_, _) => { act(); box.Focus(); };
            return b;
        }
        void Wrap(string marker)
        {
            int start = box.SelectionStart, len = box.SelectionLength;
            var sel = box.SelectedText;
            box.SelectedText = marker + (sel.Length > 0 ? sel : "text") + marker;
            box.Select(start + marker.Length, sel.Length > 0 ? len : 4);
        }
        void LinePrefix(string prefix)
        {
            int lineStart = box.Text.LastIndexOf('\n', Math.Max(0, box.CaretIndex - 1)) + 1;
            if (box.CaretIndex == 0) lineStart = 0;
            box.Text = box.Text.Insert(lineStart, prefix);
            box.CaretIndex = lineStart + prefix.Length;
        }
        void Insert(string text)
        {
            int at = box.CaretIndex;
            box.Text = box.Text.Insert(at, text);
            box.CaretIndex = at + text.Length;
        }

        bar.Children.Add(Tool("B", "Bold (**text**)  Ctrl+B", () => Wrap("**"), icon: false));
        bar.Children.Add(Tool("I", "Italic (*text*)  Ctrl+I", () => Wrap("*"), icon: false));
        bar.Children.Add(Tool("U", "Underline (__text__)  Ctrl+U", () => Wrap("__"), icon: false));
        bar.Children.Add(Tool("H1", "Heading (# )", () => LinePrefix("# "), icon: false));
        bar.Children.Add(Tool("H2", "Subheading (## )", () => LinePrefix("## "), icon: false));
        bar.Children.Add(Tool("", "Bullet point (- )", () => LinePrefix("- ")));
        bar.Children.Add(Tool("", "Numbered paragraph that keeps counting, as in a statement (#. )", () => LinePrefix("#. ")));
        bar.Children.Add(Tool("", "Right-align this line ([right])", () => LinePrefix("[right] ")));
        bar.Children.Add(Tool("", "Insert a table", () => Insert("\n| Heading | Heading |\n|---|---|\n| Text | Text |\n")));
        bar.Children.Add(Tool("", "Page break", () => Insert("\n[page break]\n")));

        var fields = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Icon("", 13), new TextBlock { Text = "  Insert field" } } },
            Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 4, 4), ToolTip = "Insert details that fill themselves in, such as the case number" };
        fields.Click += (_, _) =>
        {
            var items = CaseFields.All.GroupBy(f => f.Group).Select(g => (object)Ui.Sub(g.Key, null,
                g.Select(f => (object)Ui.Item($"{f.Description}", null, () => { Insert("{{" + f.Key + "}}"); box.Focus(); }, "{{" + f.Key + "}}")))).ToList();
            if (extraInsert != null) items.AddRange(extraInsert());
            var menu = Ui.Menu(items.ToArray());
            menu.PlacementTarget = fields;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        bar.Children.Add(fields);

        var help = new Button { Content = Ui.Icon("", 13), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 4), ToolTip = "Formatting help" };
        help.Click += (_, _) => MessageBox.Show(Window.GetWindow(box),
            "# Heading      ## Subheading      ### Minor heading\n" +
            "**bold**      *italic*      __underline__\n" +
            "- bullet point (indent two spaces for a sub-point)\n" +
            "1. numbered list\n" +
            "#. numbered paragraph – keeps counting through the whole document (witness statements)\n" +
            "> quotation\n" +
            "[right] text  /  [centre] text  – align a line\n" +
            "---  a line across the page        [page break]\n" +
            "| a | table | – put |---|---| under the first row to make it a heading row\n\n" +
            "{{case.number}} and other fields fill in automatically. Use 'Insert field' to pick one.\n" +
            "A single line break is kept (good for addresses); a blank line starts a new paragraph.",
            "Formatting", MessageBoxButton.OK, MessageBoxImage.Information);
        bar.Children.Add(help);

        box.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.B) { Wrap("**"); e.Handled = true; }
            else if (e.Key == Key.I) { Wrap("*"); e.Handled = true; }
            else if (e.Key == Key.U) { Wrap("__"); e.Handled = true; }
        };
        return bar;
    }

    internal static FlowDocumentScrollViewer PreviewPane()
    {
        return new FlowDocumentScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)Application.Current.FindResource("ViewerBackground"),
            Padding = new Thickness(10),
            IsToolBarVisible = false
        };
    }

    internal static TextBox SmallMulti(string text, double height) =>
        new() { Text = text ?? "", Height = height, Style = Ui.Style("MultilineBox") };

    internal static byte[] PickWordFile(Window owner, out string name)
    {
        name = null;
        var dlg = new OpenFileDialog { Filter = "Word document or template|*.docx;*.dotx|All files|*.*", Title = "Choose a Word file" };
        if (dlg.ShowDialog(owner) != true) return null;
        var bytes = File.ReadAllBytes(dlg.FileName);
        WordSafety.Check(bytes);
        name = Path.GetFileName(dlg.FileName);
        return bytes;
    }

    // ================================================================== letters
    private sealed class LetterEditor : UserControl
    {
        private readonly LettersView _host;
        private readonly ListBox _list;
        private readonly TextBox _search;
        private readonly TextBox _title, _recipient, _address, _subject, _theirRef, _ourRef, _salutation, _signoff, _body;
        private readonly ComboBox _letterhead, _status;
        private readonly DatePicker _date;
        private readonly CheckBox _isDocument;
        private readonly FlowDocumentScrollViewer _preview;
        private readonly TextBlock _savedText, _sourceText;
        private readonly StackPanel _letterOnly;
        private readonly Grid _editorArea;
        private readonly DispatcherTimer _saveTimer, _previewTimer;
        private LetterDraft _current;
        private bool _loading;
        private List<LetterDraft> _all = new();

        public LetterEditor(LettersView host)
        {
            _host = host;
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _saveTimer.Tick += (_, _) => SaveCurrent();
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };

            // ---- list
            var left = new DockPanel();
            var newBtn = Ui.Button("New…", "", null, primary: true, tooltip: "Start from a template");
            newBtn.Click += (_, _) => ShowNewMenu(newBtn);
            var leftTop = new StackPanel();
            leftTop.Children.Add(newBtn);
            leftTop.Children.Add(Ui.SearchBox(out _search, "Search letters…"));
            _search.TextChanged += (_, _) => FilterList();
            DockPanel.SetDock(leftTop, Dock.Top);
            left.Children.Add(leftTop);
            _list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _list.ItemTemplate = ListTemplate();
            _list.SelectionChanged += (_, _) => { if (!_loading && _list.SelectedItem is LetterDraft l) Load(l); };
            _list.ContextMenuOpening += (_, _) => _list.ContextMenu = ListMenu();
            _list.ContextMenu = new ContextMenu();
            _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) DeleteCurrent(); };
            left.Children.Add(_list);

            // ---- editor
            _title = new TextBox();
            _letterhead = new ComboBox { DisplayMemberPath = "Name" };
            _status = new ComboBox { ItemsSource = LetterDraft.Statuses };
            _date = new DatePicker();
            _isDocument = new CheckBox { Content = "Formal document layout (statement, position statement…): no addresses or sign-off", Margin = new Thickness(0, 10, 0, 0) };
            _recipient = new TextBox();
            _address = SmallMulti("", 70);
            _subject = new TextBox();
            _theirRef = new TextBox();
            _ourRef = new TextBox();
            _salutation = new TextBox();
            _signoff = new TextBox();
            _body = new TextBox { Style = Ui.Style("MultilineBox"), MinHeight = 320, FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5 };
            _sourceText = Ui.Muted("", 11.5);
            _savedText = Ui.Muted("", 11.5);

            var form = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            form.Children.Add(Labelled("Title (for your list)", _title));
            form.Children.Add(Pair(Labelled("Letterhead", _letterhead), Labelled("Status", _status)));
            form.Children.Add(_sourceText);
            form.Children.Add(_isDocument);
            _letterOnly = new StackPanel();
            _letterOnly.Children.Add(Pair(Labelled("To", _recipient), Labelled("Date", _date)));
            _letterOnly.Children.Add(Labelled("Their address", _address));
            _letterOnly.Children.Add(Labelled("Subject (shown as 'Re: …')", _subject));
            _letterOnly.Children.Add(Pair(Labelled("Their reference", _theirRef), Labelled("Our reference", _ourRef)));
            _letterOnly.Children.Add(Pair(Labelled("Greeting", _salutation, "e.g. Dear Sir or Madam"), Labelled("Sign-off", _signoff, "Yours faithfully / sincerely")));
            form.Children.Add(_letterOnly);
            form.Children.Add(new TextBlock { Text = "TEXT", Style = Ui.Style("Label") });
            form.Children.Add(FormatBar(_body, DocumentReferences));
            form.Children.Add(_body);
            form.Children.Add(_savedText);

            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            actions.Children.Add(Ui.Button("Export…", "", async (_, _) => await ExportCurrentAsync(), primary: true, tooltip: "Save as PDF, Word or a web page"));
            actions.Children.Add(Ui.Button("Print…", "", (_, _) => PrintCurrent()));
            actions.Children.Add(Ui.Button("Save to library", "", async (_, _) => await SaveToLibraryAsync(), tooltip: "Adds a PDF of this letter to your documents"));
            var ai = Ui.Button("Improve with AI", "", async (_, _) => await ImproveAsync(), tooltip: "Uses your local Ollama to suggest clearer wording");
            actions.Children.Add(ai);
            actions.Children.Add(Ui.Button(null, "", (_, _) => DuplicateCurrent(), tooltip: "Duplicate"));
            var del = Ui.Button(null, "", (_, _) => DeleteCurrent(), tooltip: "Delete");
            del.Style = Ui.Style("DangerButton");
            actions.Children.Add(del);

            var editorScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var editorDock = new DockPanel();
            DockPanel.SetDock(actions, Dock.Top);
            editorDock.Children.Add(actions);
            editorDock.Children.Add(editorScroll);

            _preview = PreviewPane();
            var previewBox = new DockPanel();
            var ph = Ui.Text("Preview", "H2");
            DockPanel.SetDock(ph, Dock.Top);
            previewBox.Children.Add(ph);
            previewBox.Children.Add(new Border { Child = _preview, CornerRadius = new CornerRadius(8), ClipToBounds = true });

            _editorArea = new Grid();
            _editorArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _editorArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            _editorArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(previewBox, 2);
            _editorArea.Children.Add(editorDock);
            _editorArea.Children.Add(previewBox);

            var empty = Ui.Muted("Choose a letter on the left, or click New… to start one from a template.", 14);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            var right = new Grid();
            right.Children.Add(empty);
            right.Children.Add(_editorArea);
            _editorArea.Visibility = Visibility.Collapsed;

            Content = TwoPane(left, right, 270);

            foreach (var tb in new[] { _title, _recipient, _address, _subject, _theirRef, _ourRef, _salutation, _signoff, _body })
                tb.TextChanged += (_, _) => Touched();
            _letterhead.SelectionChanged += (_, _) => Touched();
            _status.SelectionChanged += (_, _) => Touched();
            _date.SelectedDateChanged += (_, _) => Touched();
            _isDocument.Checked += (_, _) => { _letterOnly.Visibility = Visibility.Collapsed; Touched(); };
            _isDocument.Unchecked += (_, _) => { _letterOnly.Visibility = Visibility.Visible; Touched(); };
            Unloaded += (_, _) => SaveCurrent();
        }

        private static DataTemplate ListTemplate()
        {
            var t = new DataTemplate();
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(MarginProperty, new Thickness(2, 5, 2, 5));
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            var sub = new FrameworkElementFactory(typeof(TextBlock));
            sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
            {
                StringFormat = "{0} · {1}",
                Bindings = { new System.Windows.Data.Binding("Status"), new System.Windows.Data.Binding("UpdatedText") }
            });
            sub.SetValue(TextBlock.FontSizeProperty, 11.5);
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.AppendChild(title);
            stack.AppendChild(sub);
            t.VisualTree = stack;
            return t;
        }

        public void Reload()
        {
            ReloadChoices();
            _all = Db.All<LetterDraft>().OrderByDescending(l => l.UpdatedAt).ToList();
            FilterList();
        }

        public void ReloadChoices()
        {
            var selected = (_letterhead.SelectedItem as Letterhead)?.Id;
            var heads = new List<Letterhead> { new() { Id = 0, Name = "No letterhead" } };
            heads.AddRange(Db.All<Letterhead>());
            _loading = true;
            _letterhead.ItemsSource = heads;
            _letterhead.SelectedItem = heads.FirstOrDefault(h => h.Id == (selected ?? _current?.LetterheadId ?? 0)) ?? heads[0];
            _loading = false;
        }

        private void FilterList()
        {
            var q = _search.Text.Trim();
            _loading = true;
            _list.ItemsSource = _all.Where(l => q.Length == 0 || $"{l.Title} {l.RecipientName} {l.Subject} {l.Status}".Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            _list.SelectedItem = _all.FirstOrDefault(l => l.Id == _current?.Id);
            _loading = false;
        }

        private void ShowNewMenu(Button anchor)
        {
            var templates = Db.All<DocTemplate>().OrderBy(t => t.Kind).ThenBy(t => t.Name, NaturalComparer.Instance).ToList();
            var items = templates.GroupBy(t => t.Kind).Select(g => (object)Ui.Sub(g.Key, null,
                g.Select(t => (object)Ui.Item(t.Name, t.HasWordTemplate ? "" : null, () => NewLetter(t))))).ToList();
            items.Insert(0, Ui.Item("Blank letter", "", () => NewLetter(null), bold: true));
            items.Insert(1, new Separator());
            var menu = Ui.Menu(items.ToArray());
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        public void NewLetter(DocTemplate t, string recipient = null)
        {
            SaveCurrent();
            var lh = DefaultLetterhead();
            var l = new LetterDraft
            {
                Title = t == null ? "New letter" : t.Name,
                TemplateId = t?.Id,
                LetterheadId = lh?.Id,
                Body = t?.Body ?? "",
                Subject = t?.DefaultSubject ?? "",
                Salutation = t?.DefaultSalutation ?? "Dear Sir or Madam",
                SignOff = t?.DefaultSignOff ?? "Yours faithfully",
                IsDocument = t?.IsDocument ?? false,
                RecipientName = recipient ?? ""
            };
            Db.Save(l);
            Reload();
            Load(l);
            _title.Focus();
            _title.SelectAll();
        }

        private void Load(LetterDraft l)
        {
            SaveCurrent();
            _loading = true;
            _current = Db.Find<LetterDraft>(l.Id) ?? l;
            _title.Text = _current.Title;
            ReloadChoices();
            _letterhead.SelectedItem = ((IEnumerable<Letterhead>)_letterhead.ItemsSource).FirstOrDefault(h => h.Id == (_current.LetterheadId ?? 0));
            _status.SelectedItem = _current.Status;
            _date.SelectedDate = _current.LetterDate;
            _isDocument.IsChecked = _current.IsDocument;
            _letterOnly.Visibility = _current.IsDocument ? Visibility.Collapsed : Visibility.Visible;
            _recipient.Text = _current.RecipientName;
            _address.Text = _current.RecipientAddress;
            _subject.Text = _current.Subject;
            _theirRef.Text = _current.TheirReference;
            _ourRef.Text = _current.OurReference;
            _salutation.Text = _current.Salutation;
            _signoff.Text = _current.SignOff;
            _body.Text = _current.Body;
            var template = _current.TemplateId is { } tid ? Db.Find<DocTemplate>(tid) : null;
            _sourceText.Text = template == null ? "" : $"Started from: {template.Name}" + (template.HasWordTemplate ? " (Word layout – exported files use your Word design)" : "");
            _savedText.Text = $"Last saved {_current.UpdatedText}";
            _loading = false;
            _editorArea.Visibility = Visibility.Visible;
            _list.SelectedItem = _all.FirstOrDefault(x => x.Id == _current.Id);
            UpdatePreview();
        }

        private void Touched()
        {
            if (_loading || _current == null) return;
            _savedText.Text = "Editing…";
            _saveTimer.Stop();
            _saveTimer.Start();
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void Pull()
        {
            if (_current == null) return;
            _current.Title = string.IsNullOrWhiteSpace(_title.Text) ? "Untitled" : _title.Text.Trim();
            _current.LetterheadId = (_letterhead.SelectedItem as Letterhead) is { Id: > 0 } lh ? lh.Id : null;
            _current.Status = _status.SelectedItem as string ?? "Draft";
            _current.LetterDate = _date.SelectedDate;
            _current.IsDocument = _isDocument.IsChecked == true;
            _current.RecipientName = _recipient.Text;
            _current.RecipientAddress = _address.Text;
            _current.Subject = _subject.Text;
            _current.TheirReference = _theirRef.Text;
            _current.OurReference = _ourRef.Text;
            _current.Salutation = _salutation.Text;
            _current.SignOff = _signoff.Text;
            _current.Body = _body.Text;
            if (_current.Status == "Sent" && _current.SentDate == null) _current.SentDate = DateTime.Today;
        }

        private void SaveCurrent()
        {
            _saveTimer.Stop();
            if (_current == null || _loading) return;
            Pull();
            var stored = Db.Find<LetterDraft>(_current.Id);
            if (stored != null && Same(stored, _current)) return;
            _current.UpdatedAt = DateTime.Now;
            try
            {
                Db.Save(_current);
                _savedText.Text = "✓ Saved " + DateTime.Now.ToString("HH:mm");
                var inList = _all.FirstOrDefault(x => x.Id == _current.Id);
                if (inList != null && (inList.Title != _current.Title || inList.Status != _current.Status))
                {
                    _all = Db.All<LetterDraft>().OrderByDescending(l => l.UpdatedAt).ToList();
                    FilterList();
                }
            }
            catch (Exception ex)
            {
                var id = AppLog.Error("Letters", "Couldn't save a letter", ex);
                _savedText.Text = $"Couldn't save ({id})";
            }
        }

        private static bool Same(LetterDraft a, LetterDraft b) =>
            a.Title == b.Title && a.LetterheadId == b.LetterheadId && a.Status == b.Status && a.LetterDate == b.LetterDate &&
            a.IsDocument == b.IsDocument && a.RecipientName == b.RecipientName && a.RecipientAddress == b.RecipientAddress &&
            a.Subject == b.Subject && a.TheirReference == b.TheirReference && a.OurReference == b.OurReference &&
            a.Salutation == b.Salutation && a.SignOff == b.SignOff && a.Body == b.Body;

        private (DocModel Doc, Letterhead Lh, byte[] Word, Dictionary<string, string> Fields, List<string> Missing) Compose()
        {
            Pull();
            var lh = _current.LetterheadId is { } id ? Db.Find<Letterhead>(id) : null;
            var fields = CaseFields.Build(Db, _current, lh);
            var missing = new List<string>();
            var doc = LetterComposer.Compose(_current, fields, missing);
            var template = _current.TemplateId is { } tid ? Db.Find<DocTemplate>(tid) : null;
            var word = template?.HasWordTemplate == true ? template.WordTemplate : lh?.HasWordTemplate == true ? lh.WordTemplate : null;
            return (doc, lh, word, fields, missing);
        }

        private void UpdatePreview()
        {
            if (_current == null) return;
            try
            {
                var (doc, lh, _, _, missing) = Compose();
                _preview.Document = FlowDocBuilder.Build(doc, lh);
                if (missing.Count > 0)
                    _savedText.Text = $"Fields without a value yet: {string.Join(", ", missing.Distinct().Select(m => "{{" + m + "}}"))} – fill in Case details or Parties.";
            }
            catch (Exception ex) { AppLog.Warn("Letters", "Preview failed", ex); }
        }

        private async Task ExportCurrentAsync()
        {
            if (_current == null) return;
            SaveCurrent();
            var (doc, lh, word, fields, _) = Compose();
            var path = await ExportHelper.ExportDocumentAsync(Window.GetWindow(this), doc, lh, word, fields);
            if (path != null && _current.Status == "Draft")
            {
                _status.SelectedItem = "Final";
                SaveCurrent();
            }
        }

        private void PrintCurrent()
        {
            if (_current == null || !MainWindow.ConfirmConfidentiality(Window.GetWindow(this))) return;
            var (doc, lh, _, _, _) = Compose();
            FlowDocBuilder.Print(doc, lh, Window.GetWindow(this));
            Db.Audit("export.print", "letter", _current.Id, $"Printed “{_current.Title}”");
        }

        private async Task SaveToLibraryAsync()
        {
            if (_current == null) return;
            SaveCurrent();
            var (doc, lh, word, fields, _) = Compose();
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var bytes = await Sta.RunAsync(() => DocumentExporter.Render(doc, lh, ExportFormat.Pdf, word, fields));
                Mouse.OverrideCursor = null;
                MainWindow.Shell?.ImportGenerated(bytes, $"{_current.Title}.pdf", _current.Title, "Letters I have written", _current.LetterDate);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                var id = AppLog.Error("Letters", "Couldn't add the letter to the library", ex);
                MessageBox.Show(Window.GetWindow(this), $"Couldn't add it to the library: {ex.Message}\nReference: {id}", "Letters");
            }
        }

        private async Task ImproveAsync()
        {
            if (_current == null) return;
            var owner = Window.GetWindow(this);
            if (!AiService.EnsureEnabled(owner)) return;
            var text = string.IsNullOrWhiteSpace(_body.SelectedText) ? _body.Text : _body.SelectedText;
            if (string.IsNullOrWhiteSpace(text)) return;
            var result = await AiAssistWindow.RunAsync(owner, "Improve wording",
                AiService.Prompt("prompt.rewrite"), text);
            if (result == null) return;
            if (string.IsNullOrWhiteSpace(_body.SelectedText)) _body.Text = result;
            else _body.SelectedText = result;
        }

        private IEnumerable<object> DocumentReferences()
        {
            var docs = MainWindow.Shell?.Documents ?? new List<CaseDocument>();
            var exhibits = docs.Where(d => !string.IsNullOrWhiteSpace(d.ExhibitRef)).OrderBy(d => d.ExhibitRef, NaturalComparer.Instance).ToList();
            yield return new Separator();
            yield return Ui.Sub("Exhibit reference", "", exhibits.Select(d => (object)Ui.Item($"{d.ExhibitRef} – {d.Title}", null,
                () => InsertAtCaret($"exhibit {d.ExhibitRef}"))));
            yield return Ui.Sub("Document by title", "", docs.OrderByDescending(d => d.DocumentDate).Take(40).Select(d => (object)Ui.Item(
                $"{d.Title} ({d.DocumentDateText})", null, () => InsertAtCaret($"{d.Title} dated {d.DocumentDate:d MMMM yyyy}"))));
        }

        private void InsertAtCaret(string text)
        {
            int at = _body.CaretIndex;
            _body.Text = _body.Text.Insert(at, text);
            _body.CaretIndex = at + text.Length;
            _body.Focus();
        }

        private void DuplicateCurrent()
        {
            if (_current == null) return;
            SaveCurrent();
            var copy = Ui.Clone(_current);
            copy.Id = 0;
            copy.Title += " (copy)";
            copy.Status = "Draft";
            copy.CreatedAt = copy.UpdatedAt = DateTime.Now;
            Db.Save(copy);
            Reload();
            Load(copy);
        }

        private void DeleteCurrent()
        {
            if (_current == null) return;
            if (MessageBox.Show(Window.GetWindow(this), $"Delete “{_current.Title}”?\n\nFiles you already exported are not affected.", "Delete letter",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Db.Remove<LetterDraft>(_current.Id);
            _current = null;
            _editorArea.Visibility = Visibility.Collapsed;
            Reload();
        }

        private ContextMenu ListMenu()
        {
            bool has = _list.SelectedItem is LetterDraft;
            return Ui.Menu(
                Ui.Item("Open", "", () => { if (_list.SelectedItem is LetterDraft l) Load(l); }, enabled: has, bold: true),
                Ui.Item("Export…", "", async () => await ExportCurrentAsync(), enabled: has),
                Ui.Item("Print…", "", PrintCurrent, enabled: has),
                Ui.Item("Save to library", "", async () => await SaveToLibraryAsync(), enabled: has),
                Ui.Sub("Set status", "", LetterDraft.Statuses.Select(s => (object)Ui.Item(s, null, () => { _status.SelectedItem = s; SaveCurrent(); }))),
                Ui.Item("Duplicate", "", DuplicateCurrent, enabled: has),
                Ui.Item("History…", "", () => MainWindow.Shell?.ShowAuditFor(typeof(LetterDraft), _current?.Id ?? 0), enabled: has),
                new Separator(),
                Ui.Item("Delete…", "", DeleteCurrent, "Del", has));
        }
    }

    // ================================================================== templates
    private sealed class TemplateEditor : UserControl
    {
        private readonly LettersView _host;
        private readonly ListBox _list;
        private readonly TextBox _name, _desc, _subject, _salutation, _signoff, _body;
        private readonly ComboBox _kind;
        private readonly CheckBox _isDocument;
        private readonly TextBlock _wordInfo;
        private readonly StackPanel _editor;
        private DocTemplate _current;

        public TemplateEditor(LettersView host)
        {
            _host = host;
            var left = new DockPanel();
            var top = new WrapPanel();
            top.Children.Add(Ui.Button("New", "", (_, _) => New(), primary: true));
            top.Children.Add(Ui.Button("Import Word…", "", (_, _) => ImportWord(), tooltip: "Use a .docx or .dotx as a template"));
            DockPanel.SetDock(top, Dock.Top);
            left.Children.Add(top);
            _list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
            _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is DocTemplate t) Load(t); };
            _list.ContextMenuOpening += (_, _) => _list.ContextMenu = Menu();
            _list.ContextMenu = new ContextMenu();
            left.Children.Add(_list);

            _name = new TextBox();
            _kind = new ComboBox { ItemsSource = DocTemplate.Kinds, IsEditable = true };
            _desc = new TextBox();
            _subject = new TextBox();
            _salutation = new TextBox();
            _signoff = new TextBox();
            _isDocument = new CheckBox { Content = "Formal document (statement etc.) rather than a letter", Margin = new Thickness(0, 10, 0, 0) };
            _body = new TextBox { Style = Ui.Style("MultilineBox"), MinHeight = 320 };
            _wordInfo = Ui.Muted("", 12.5);

            _editor = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            var actions = new WrapPanel();
            actions.Children.Add(Ui.Button("Save", "", (_, _) => Save(), primary: true));
            actions.Children.Add(Ui.Button("Use this template", "", (_, _) => { Save(); if (_current != null) _host.NewFromTemplate(_current); }));
            actions.Children.Add(Ui.Button("Duplicate", "", (_, _) => Duplicate()));
            actions.Children.Add(Ui.Button("Restore original", "", (_, _) => RestoreBuiltIn(), tooltip: "Put a built-in template back as it was"));
            var del = Ui.Button("Delete", "", (_, _) => Delete());
            del.Style = Ui.Style("DangerButton");
            actions.Children.Add(del);
            _editor.Children.Add(actions);
            _editor.Children.Add(Pair(Labelled("Name", _name), Labelled("Kind", _kind)));
            _editor.Children.Add(Labelled("Description", _desc));
            _editor.Children.Add(_isDocument);
            _editor.Children.Add(Labelled("Default subject", _subject));
            _editor.Children.Add(Pair(Labelled("Default greeting", _salutation), Labelled("Default sign-off", _signoff)));

            var word = new StackPanel();
            word.Children.Add(Ui.Text("Word layout (optional)", "H2"));
            word.Children.Add(Ui.Muted("Attach a Word file (.docx or .dotx) and its exact design is used for Word exports, and for PDFs " +
                                       "when Microsoft Word or LibreOffice is installed. Put {{fields}} anywhere in it, and {{body}} " +
                                       "on its own line where the letter text should go.", 12.5));
            word.Children.Add(_wordInfo);
            var wb = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            wb.Children.Add(Ui.Button("Attach Word file…", "", (_, _) => AttachWord()));
            wb.Children.Add(Ui.Button("Convert it to editable text", "", (_, _) => ConvertWord()));
            wb.Children.Add(Ui.Button("Save Word file…", "", (_, _) => SaveWordFile()));
            wb.Children.Add(Ui.Button("Remove", "", (_, _) => { if (_current != null) { _current.WordTemplate = null; _current.WordTemplateName = ""; Save(); } }));
            word.Children.Add(wb);
            _editor.Children.Add(Ui.Card(word, new Thickness(0, 14, 0, 6)));

            _editor.Children.Add(new TextBlock { Text = "TEMPLATE TEXT", Style = Ui.Style("Label") });
            _editor.Children.Add(FormatBar(_body));
            _editor.Children.Add(_body);

            var scroll = new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
            _editor.Tag = scroll;
            Content = TwoPane(left, scroll, 270);
        }

        public void Reload()
        {
            var id = _current?.Id;
            var list = Db.All<DocTemplate>().OrderBy(t => t.Kind).ThenBy(t => t.Name, NaturalComparer.Instance).ToList();
            _list.ItemsSource = list;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(list);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Kind"));
            if (_list.GroupStyle.Count == 0)
            {
                var header = new DataTemplate();
                var f = new FrameworkElementFactory(typeof(TextBlock));
                f.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
                f.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
                f.SetValue(TextBlock.MarginProperty, new Thickness(4, 10, 0, 2));
                f.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                header.VisualTree = f;
                _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = header });
            }
            _list.SelectedItem = list.FirstOrDefault(t => t.Id == id);
        }

        private void Load(DocTemplate t)
        {
            _current = Db.Find<DocTemplate>(t.Id) ?? t;
            _name.Text = _current.Name;
            _kind.Text = _current.Kind;
            _desc.Text = _current.Description;
            _subject.Text = _current.DefaultSubject;
            _salutation.Text = _current.DefaultSalutation;
            _signoff.Text = _current.DefaultSignOff;
            _isDocument.IsChecked = _current.IsDocument;
            _body.Text = _current.Body;
            _wordInfo.Text = _current.HasWordTemplate
                ? $"Attached: {_current.WordTemplateName}. Fields used: {string.Join(", ", SafeFields(_current.WordTemplate))}"
                : "No Word file attached – exports use CaseKeeper's own layout and your letterhead.";
            ((ScrollViewer)_editor.Tag).Visibility = Visibility.Visible;
        }

        private static IEnumerable<string> SafeFields(byte[] docx)
        {
            try { return DocxDocWriter.FieldsInTemplate(docx).Select(f => "{{" + f + "}}").DefaultIfEmpty("none"); }
            catch { return new[] { "(couldn't read)" }; }
        }

        private void Save()
        {
            if (_current == null) return;
            _current.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled template" : _name.Text.Trim();
            _current.Kind = string.IsNullOrWhiteSpace(_kind.Text) ? "Letter" : _kind.Text.Trim();
            _current.Description = _desc.Text.Trim();
            _current.DefaultSubject = _subject.Text;
            _current.DefaultSalutation = _salutation.Text;
            _current.DefaultSignOff = _signoff.Text;
            _current.IsDocument = _isDocument.IsChecked == true;
            _current.Body = _body.Text;
            _current.UpdatedAt = DateTime.Now;
            Db.Save(_current);
            Reload();
            Load(_current);
        }

        private void New()
        {
            _current = new DocTemplate { Name = "New template", Kind = "Letter", Body = "Dear {{recipient.name}},\n\n[Your text]" };
            Db.Save(_current);
            Reload();
            Load(_current);
        }

        private void ImportWord()
        {
            var owner = Window.GetWindow(this);
            byte[] bytes;
            string name;
            try { bytes = PickWordFile(owner, out name); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Import Word template"); return; }
            if (bytes == null) return;
            var answer = MessageBox.Show(owner,
                $"How should “{name}” be used?\n\n" +
                "Yes – keep its Word layout exactly. Exports fill in any {{fields}} in it and put the letter text where {{body}} is.\n\n" +
                "No – convert it into editable text in CaseKeeper (formatting is simplified).",
                "Import Word template", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            var t = new DocTemplate { Name = Path.GetFileNameWithoutExtension(name), Kind = "Letter", UpdatedAt = DateTime.Now };
            try
            {
                if (answer == MessageBoxResult.Yes)
                {
                    t.WordTemplate = ScrubbedCopy(bytes);
                    t.WordTemplateName = name;
                    t.Body = "[Write the letter text here. It goes where {{body}} is in your Word file.]";
                }
                else t.Body = DocxDocWriter.ToMarkup(bytes);
                Db.Save(t);
                Db.Audit("template.import", "template", t.Id, $"Imported Word template “{name}”", answer == MessageBoxResult.Yes ? "Kept Word layout" : "Converted to text");
            }
            catch (Exception ex)
            {
                var id = AppLog.Error("Templates", "Word template import failed", ex);
                MessageBox.Show(owner, $"That Word file couldn't be imported: {ex.Message}\nReference: {id}", "Import Word template");
                return;
            }
            _current = t;
            Reload();
            Load(t);
        }

        /// <summary>Strips author details from an imported Word file before it is stored.</summary>
        internal static byte[] ScrubbedCopy(byte[] docx)
        {
            using var ms = new MemoryStream();
            ms.Write(docx, 0, docx.Length);
            ms.Position = 0;
            using (var w = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, true)) DocxDocWriter.Scrub(w);
            return ms.ToArray();
        }

        private void AttachWord()
        {
            if (_current == null) return;
            var owner = Window.GetWindow(this);
            try
            {
                var bytes = PickWordFile(owner, out var name);
                if (bytes == null) return;
                _current.WordTemplate = ScrubbedCopy(bytes);
                _current.WordTemplateName = name;
                Save();
            }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Attach Word file"); }
        }

        private void ConvertWord()
        {
            if (_current?.HasWordTemplate != true) return;
            if (!string.IsNullOrWhiteSpace(_body.Text) && MessageBox.Show(Window.GetWindow(this),
                    "Replace the template text with the text of the Word file?", "Convert", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { _body.Text = DocxDocWriter.ToMarkup(_current.WordTemplate); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Convert"); }
        }

        private void SaveWordFile()
        {
            if (_current == null) return;
            var dlg = new SaveFileDialog { Filter = "Word document|*.docx", FileName = LibraryService.SafeFileName(_current.Name) + ".docx" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            var bytes = _current.HasWordTemplate
                ? _current.WordTemplate
                : DocxDocWriter.Render(new DocModel { Title = _current.Name, ShowTitle = false, Blocks = Markup.Parse(_current.Body) }, DefaultLetterhead());
            File.WriteAllBytes(dlg.FileName, bytes);
            Db.Audit("template.export", "template", _current.Id, $"Saved template “{_current.Name}” as a Word file", DocumentExporter.DescribeLocation(dlg.FileName));
        }

        private void Duplicate()
        {
            if (_current == null) return;
            var copy = Ui.Clone(_current);
            copy.Id = 0;
            copy.Name += " (copy)";
            copy.IsBuiltIn = false;
            copy.BuiltInKey = "";
            Db.Save(copy);
            _current = copy;
            Reload();
            Load(copy);
        }

        private void RestoreBuiltIn()
        {
            if (_current is not { IsBuiltIn: true } || BuiltInTemplates.Find(_current.BuiltInKey) is not { } def)
            {
                MessageBox.Show(Window.GetWindow(this), "This isn't one of the built-in templates.", "Restore");
                return;
            }
            var fresh = BuiltInTemplates.ToTemplate(def);
            fresh.Id = _current.Id;
            Db.Save(fresh);
            Load(fresh);
        }

        private void Delete()
        {
            if (_current == null) return;
            if (MessageBox.Show(Window.GetWindow(this), $"Delete the template “{_current.Name}”?\n\nLetters already written from it are kept.",
                    "Delete template", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Db.Remove<DocTemplate>(_current.Id);
            _current = null;
            ((ScrollViewer)_editor.Tag).Visibility = Visibility.Collapsed;
            Reload();
        }

        private ContextMenu Menu() => Ui.Menu(
            Ui.Item("Use this template", "", () => { if (_current != null) _host.NewFromTemplate(_current); }, enabled: _current != null, bold: true),
            Ui.Item("Duplicate", "", Duplicate, enabled: _current != null),
            Ui.Item("Save as Word file…", "", SaveWordFile, enabled: _current != null),
            Ui.Item("Restore original", "", RestoreBuiltIn, enabled: _current?.IsBuiltIn == true),
            new Separator(),
            Ui.Item("Delete…", "", Delete, enabled: _current != null));
    }

    // ================================================================== letterheads
    private sealed class LetterheadEditor : UserControl
    {
        private readonly LettersView _host;
        private readonly ListBox _list;
        private readonly TextBox _name, _sender, _address, _phone, _email, _web, _ref, _footer, _colour;
        private readonly ComboBox _font, _layout, _align;
        private readonly CheckBox _default;
        private readonly Image _logo;
        private readonly TextBlock _wordInfo;
        private readonly FlowDocumentScrollViewer _preview;
        private readonly Grid _editorGrid;
        private Letterhead _current;
        private bool _loading;

        private static readonly string[] Swatches = { "#0B7A75", "#1E40AF", "#14213D", "#7C2D12", "#4C1D95", "#166534", "#374151", "#9F1239" };

        public LetterheadEditor(LettersView host)
        {
            _host = host;
            var left = new DockPanel();
            var newBtn = Ui.Button("New letterhead", "", (_, _) => New(), primary: true);
            DockPanel.SetDock(newBtn, Dock.Top);
            left.Children.Add(newBtn);
            _list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
            _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is Letterhead l) Load(l); };
            left.Children.Add(_list);

            _name = new TextBox();
            _sender = new TextBox();
            _address = SmallMulti("", 70);
            _phone = new TextBox();
            _email = new TextBox();
            _web = new TextBox();
            _ref = new TextBox();
            _footer = new TextBox();
            _colour = new TextBox { Width = 110 };
            _font = new ComboBox { ItemsSource = Letterhead.Fonts, IsEditable = true };
            _layout = new ComboBox { ItemsSource = Letterhead.Layouts };
            _align = new ComboBox { ItemsSource = Letterhead.Alignments };
            _default = new CheckBox { Content = "Use this letterhead for new letters", Margin = new Thickness(0, 10, 0, 0) };
            _logo = new Image { Height = 54, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            _wordInfo = Ui.Muted("", 12);

            var form = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            var actions = new WrapPanel();
            actions.Children.Add(Ui.Button("Save", "", (_, _) => Save(), primary: true));
            var del = Ui.Button("Delete", "", (_, _) => Delete());
            del.Style = Ui.Style("DangerButton");
            actions.Children.Add(del);
            form.Children.Add(actions);
            form.Children.Add(Labelled("Letterhead name (for you)", _name));
            form.Children.Add(Labelled("Name shown at the top", _sender, "Your name, or e.g. 'J. Smith – Litigant in person'"));
            form.Children.Add(Labelled("Address", _address, "Leave out an address you need to keep confidential (C8)."));
            form.Children.Add(Pair(Labelled("Phone", _phone), Labelled("E-mail", _email)));
            form.Children.Add(Pair(Labelled("Website", _web), Labelled("Reference line", _ref)));
            form.Children.Add(Labelled("Footer text", _footer, "e.g. Private and confidential"));
            form.Children.Add(Pair(Labelled("Style", _layout), Labelled("Font", _font)));

            var colourRow = new WrapPanel();
            colourRow.Children.Add(_colour);
            foreach (var s in Swatches)
            {
                var sw = new Button { Width = 24, Height = 24, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0), ToolTip = s,
                    Background = (Brush)new BrushConverter().ConvertFromString(s), BorderThickness = new Thickness(0) };
                sw.Click += (_, _) => _colour.Text = s;
                colourRow.Children.Add(sw);
            }
            form.Children.Add(Labelled("Accent colour", colourRow, "A hex colour like #0B7A75, or pick one."));

            var logoPanel = new StackPanel();
            var logoButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            logoButtons.Children.Add(Ui.Button("Choose logo…", "", (_, _) => ChooseLogo()));
            logoButtons.Children.Add(Ui.Button("Remove logo", "", (_, _) => { if (_current != null) { _current.LogoPng = null; _logo.Source = null; Touched(); } }));
            logoPanel.Children.Add(logoButtons);
            logoPanel.Children.Add(_logo);
            form.Children.Add(Pair(Labelled("Logo", logoPanel, "Re-saved as PNG, so photo location and camera details are removed."), Labelled("Logo position", _align)));
            form.Children.Add(_default);

            var word = new StackPanel();
            word.Children.Add(Ui.Text("Use your own Word letterhead (optional)", "H2"));
            word.Children.Add(Ui.Muted("Already have letterhead paper designed in Word? Attach the .docx or .dotx. Word exports will be built on it " +
                                       "(put {{body}} where the letter should go, or it's added at the end). Fields like {{sender.name}} work too.", 12.5));
            word.Children.Add(_wordInfo);
            var wb = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            wb.Children.Add(Ui.Button("Attach Word letterhead…", "", (_, _) => AttachWord()));
            wb.Children.Add(Ui.Button("Remove", "", (_, _) => { if (_current != null) { _current.WordTemplate = null; _current.WordTemplateName = ""; Save(); } }));
            word.Children.Add(wb);
            form.Children.Add(Ui.Card(word, new Thickness(0, 14, 0, 0)));

            _preview = PreviewPane();
            var previewDock = new DockPanel();
            var ph = Ui.Text("Preview", "H2");
            DockPanel.SetDock(ph, Dock.Top);
            previewDock.Children.Add(ph);
            previewDock.Children.Add(new Border { Child = _preview, CornerRadius = new CornerRadius(8), ClipToBounds = true });

            _editorGrid = new Grid { Visibility = Visibility.Collapsed };
            _editorGrid.ColumnDefinitions.Add(new ColumnDefinition());
            _editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            _editorGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(previewDock, 2);
            _editorGrid.Children.Add(scroll);
            _editorGrid.Children.Add(previewDock);

            var right = new Grid();
            var empty = Ui.Muted("Create a letterhead for your letters: your name and contact details, an optional logo and your colours.", 14);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(empty);
            right.Children.Add(_editorGrid);
            Content = TwoPane(left, right, 240);

            foreach (var tb in new[] { _sender, _address, _phone, _email, _web, _ref, _footer, _colour }) tb.TextChanged += (_, _) => Touched();
            foreach (var cb in new[] { _font, _layout, _align }) cb.SelectionChanged += (_, _) => Touched();
            _font.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Touched()));
        }

        public void Reload()
        {
            var id = _current?.Id;
            var list = Db.All<Letterhead>();
            _list.ItemsSource = list;
            _list.SelectedItem = list.FirstOrDefault(l => l.Id == id);
        }

        private void New()
        {
            var me = MainWindow.Shell?.Parties.FirstOrDefault(p => p.IsMe);
            var l = new Letterhead
            {
                Name = "My letterhead", SenderName = me?.Name ?? "", IsDefault = !Db.All<Letterhead>().Any()
            };
            Db.Save(l);
            Reload();
            _list.SelectedItem = ((IEnumerable<Letterhead>)_list.ItemsSource).FirstOrDefault(x => x.Id == l.Id);
        }

        private void Load(Letterhead l)
        {
            _loading = true;
            _current = Db.Find<Letterhead>(l.Id) ?? l;
            _name.Text = _current.Name;
            _sender.Text = _current.SenderName;
            _address.Text = _current.AddressLines;
            _phone.Text = _current.Phone;
            _email.Text = _current.Email;
            _web.Text = _current.Website;
            _ref.Text = _current.ReferenceLine;
            _footer.Text = _current.FooterText;
            _colour.Text = _current.AccentColor;
            _font.Text = _current.FontFamily;
            _layout.SelectedItem = _current.Layout;
            _align.SelectedItem = _current.LogoAlign;
            _default.IsChecked = _current.IsDefault;
            _logo.Source = ToImage(_current.LogoPng);
            _wordInfo.Text = _current.HasWordTemplate ? "Attached: " + _current.WordTemplateName : "No Word letterhead attached.";
            _loading = false;
            _editorGrid.Visibility = Visibility.Visible;
            UpdatePreview();
        }

        private void Pull()
        {
            _current.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Letterhead" : _name.Text.Trim();
            _current.SenderName = _sender.Text.Trim();
            _current.AddressLines = _address.Text.Trim();
            _current.Phone = _phone.Text.Trim();
            _current.Email = _email.Text.Trim();
            _current.Website = _web.Text.Trim();
            _current.ReferenceLine = _ref.Text.Trim();
            _current.FooterText = _footer.Text.Trim();
            _current.AccentColor = System.Text.RegularExpressions.Regex.IsMatch(_colour.Text.Trim(), "^#?[0-9A-Fa-f]{6}$")
                ? "#" + _colour.Text.Trim().TrimStart('#').ToUpperInvariant() : "#0B7A75";
            _current.FontFamily = string.IsNullOrWhiteSpace(_font.Text) ? "Segoe UI" : _font.Text.Trim();
            _current.Layout = _layout.SelectedItem as string ?? "Classic";
            _current.LogoAlign = _align.SelectedItem as string ?? "Right";
            _current.IsDefault = _default.IsChecked == true;
        }

        private void Touched()
        {
            if (_loading || _current == null) return;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_current == null) return;
            Pull();
            var sample = new LetterDraft
            {
                RecipientName = "The Court Manager", RecipientAddress = "The Family Court\n[Address]", Subject = "Case number {{case.number}}",
                Body = "This is how your letters will look. The text of each letter goes here.\n\nA second paragraph, with **bold** and *italic* text.",
                Salutation = "Dear Sir or Madam", SignOff = "Yours faithfully"
            };
            try { _preview.Document = FlowDocBuilder.Build(LetterComposer.Compose(sample, CaseFields.Build(Db, sample, _current)), _current); }
            catch (Exception ex) { AppLog.Warn("Letterheads", "Preview failed", ex); }
        }

        private void Save()
        {
            if (_current == null) return;
            Pull();
            _current.UpdatedAt = DateTime.Now;
            if (_current.IsDefault)
                foreach (var other in Db.All<Letterhead>().Where(l => l.Id != _current.Id && l.IsDefault))
                {
                    other.IsDefault = false;
                    Db.Save(other);
                }
            Db.Save(_current);
            Reload();
        }

        private void Delete()
        {
            if (_current == null) return;
            if (MessageBox.Show(Window.GetWindow(this), $"Delete the letterhead “{_current.Name}”?", "Delete letterhead",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Db.Remove<Letterhead>(_current.Id);
            _current = null;
            _editorGrid.Visibility = Visibility.Collapsed;
            Reload();
        }

        private void ChooseLogo()
        {
            if (_current == null) return;
            var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Title = "Choose a logo" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                if (new FileInfo(dlg.FileName).Length > 15 * 1024 * 1024) throw new InvalidDataException("That image is too large (15 MB limit).");
                _current.LogoPng = ToCleanPng(File.ReadAllBytes(dlg.FileName), 800);
                _logo.Source = ToImage(_current.LogoPng);
                Save();
                UpdatePreview();
            }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "Couldn't use that image: " + ex.Message, "Logo"); }
        }

        /// <summary>Re-encodes an image as a fresh PNG no wider than <paramref name="maxWidth"/>, dropping all metadata (EXIF, GPS…).</summary>
        private static byte[] ToCleanPng(byte[] input, int maxWidth)
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(input), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth > maxWidth)
            {
                double s = (double)maxWidth / frame.PixelWidth;
                frame = new TransformedBitmap(frame, new ScaleTransform(s, s));
            }
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(converted));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static BitmapImage ToImage(byte[] png)
        {
            if (png is not { Length: > 0 }) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(png);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        private void AttachWord()
        {
            if (_current == null) return;
            var owner = Window.GetWindow(this);
            try
            {
                var bytes = PickWordFile(owner, out var name);
                if (bytes == null) return;
                _current.WordTemplate = TemplateEditor.ScrubbedCopy(bytes);
                _current.WordTemplateName = name;
                Save();
                _wordInfo.Text = "Attached: " + name;
            }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Attach Word letterhead"); }
        }
    }
}
