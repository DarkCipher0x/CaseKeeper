using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using CaseKeeper.Services.Guide;
using CaseKeeper.UI;
using CaseKeeper.Views.Forms;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// The built-in UK family law guide, FAQ and CaseKeeper help: searchable as you type, browsable
/// by topic, with optional AI explanations from your local Ollama.
/// </summary>
public class GuideView : UserControl
{
    private readonly TextBox _search;
    private readonly ListBox _topics, _results;
    private readonly FlowDocumentScrollViewer _reader;
    private readonly TextBlock _articleTitle, _articleTopic;
    private readonly TextBox _question;
    private Article _current;

    public GuideView()
    {
        var root = new Grid { Margin = new Thickness(28, 24, 28, 18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(Ui.Text("Guide & FAQ", "H1"));
        head.Children.Add(Ui.Muted("Plain-English information about family court cases in England & Wales, answers to common questions, " +
                                   "and how to use every part of CaseKeeper. General information, not legal advice.", 13));
        var searchRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var ask = Ui.Button("Ask the AI", "", async (_, _) => await AskAsync(), tooltip: "Answers from the guide's own articles using your local AI");
        DockPanel.SetDock(ask, Dock.Right);
        searchRow.Children.Add(ask);
        var searchGrid = Ui.SearchBox(out _search, "Search the guide: a word, a form (C100) or a question…");
        _search.FontSize = 15;
        _search.TextChanged += (_, _) => RunSearch();
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter && _search.Text.Trim().EndsWith('?')) await AskAsync(); };
        searchRow.Children.Add(searchGrid);
        head.Children.Add(searchRow);
        Grid.SetColumnSpan(head, 5);
        root.Children.Add(head);

        _topics = new ListBox { ItemsSource = new[] { "All topics" }.Concat(GuideContent.Topics).ToList(), SelectedIndex = 0 };
        _topics.SelectionChanged += (_, _) => RunSearch();
        Grid.SetRow(_topics, 1);
        root.Children.Add(_topics);

        _results = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Title" };
        _results.SelectionChanged += (_, _) => { if (_results.SelectedItem is Article a) Show(a); };
        Grid.SetRow(_results, 1);
        Grid.SetColumn(_results, 2);
        root.Children.Add(_results);

        var readerDock = new DockPanel();
        var readerHead = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _articleTopic = new TextBlock { Style = Ui.Style("Eyebrow") };
        _articleTitle = Ui.Text("", "H2", 18);
        readerHead.Children.Add(_articleTopic);
        readerHead.Children.Add(_articleTitle);
        var aiRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        aiRow.Children.Add(Ui.Button("Explain simply", "", async (_, _) => await ExplainAsync(), tooltip: "Your local AI rewrites this article in plainer words"));
        aiRow.Children.Add(Ui.Button("How does this apply to me?", "", async (_, _) => await ApplyAsync(),
            tooltip: "Your local AI relates this article to the case details you've entered. Nothing leaves your AI server."));
        aiRow.Children.Add(Ui.Button("Save / print…", "", async (_, _) => await SaveArticleAsync()));
        readerHead.Children.Add(aiRow);
        DockPanel.SetDock(readerHead, Dock.Top);
        readerDock.Children.Add(readerHead);

        var askBox = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var askBtn = Ui.Button("Ask", "", async (_, _) => await AskAsync(_question.Text));
        DockPanel.SetDock(askBtn, Dock.Right);
        askBox.Children.Add(askBtn);
        var qGrid = Ui.SearchBox(out _question, "Or ask your own question, e.g. \"What happens at a DRA?\"");
        _question.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await AskAsync(_question.Text); };
        askBox.Children.Add(qGrid);
        DockPanel.SetDock(askBox, Dock.Bottom);
        readerDock.Children.Add(askBox);

        _reader = new FlowDocumentScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
        _reader.SetResourceReference(BackgroundProperty, "ViewerBackground");
        readerDock.Children.Add(new Border { Child = _reader, CornerRadius = new CornerRadius(8), ClipToBounds = true });
        Grid.SetRow(readerDock, 1);
        Grid.SetColumn(readerDock, 4);
        root.Children.Add(readerDock);

        Content = root;
        Loaded += (_, _) => { if (_current == null) { RunSearch(); Open("start"); } };
    }

    /// <summary>Opens an article by id, e.g. from a help button elsewhere in the app.</summary>
    public void Open(string id)
    {
        var a = GuideContent.All.FirstOrDefault(x => x.Id == id);
        if (a == null) return;
        if (_results.ItemsSource is IEnumerable<Article> shown && !shown.Contains(a))
        {
            _search.Text = "";
            _topics.SelectedIndex = 0;
        }
        _results.SelectedItem = a;
        Show(a);
    }

    public void SearchFor(string text)
    {
        _search.Text = text;
        _search.Focus();
        _search.CaretIndex = _search.Text.Length;
    }

    public static IEnumerable<(Article Article, int Score)> Search(string query)
    {
        var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ', ',', '?', '.', '!', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !StopWords.Contains(w)).Distinct().ToList();
        if (words.Count == 0) return GuideContent.All.Select(a => (a, 0));
        return GuideContent.All.Select(a =>
        {
            string title = a.Title.ToLowerInvariant(), keys = a.Keywords.ToLowerInvariant(), body = a.Body.ToLowerInvariant();
            int score = 0;
            foreach (var w in words)
            {
                if (title.Contains(w)) score += 6;
                if (keys.Contains(w)) score += 4;
                int n = 0, i = 0;
                while ((i = body.IndexOf(w, i, StringComparison.Ordinal)) >= 0 && n < 5) { n++; i += w.Length; }
                score += n;
            }
            if (words.All(w => title.Contains(w) || keys.Contains(w) || body.Contains(w))) score += 5;
            return (a, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score);
    }

    private static readonly HashSet<string> StopWords = new()
    {
        "the", "and", "for", "can", "how", "what", "who", "does", "is", "are", "my", "me", "do", "to", "of", "in", "on", "a", "an", "it",
        "if", "or", "be", "with", "about", "when", "will", "i", "should", "there", "this", "that", "at", "by", "from"
    };

    private void RunSearch()
    {
        var topic = _topics.SelectedIndex > 0 ? _topics.SelectedItem as string : null;
        var hits = Search(_search.Text).Select(x => x.Article).Where(a => topic == null || a.Topic == topic).ToList();
        if (_search.Text.Trim().Length == 0)
            hits = hits.OrderBy(a => Array.IndexOf(GuideContent.Topics, a.Topic)).ToList();
        _results.ItemsSource = hits;
        if (_current == null || !hits.Contains(_current)) _results.SelectedItem = hits.FirstOrDefault();
    }

    private void Show(Article a)
    {
        _current = a;
        _articleTopic.Text = a.Topic.ToUpperInvariant();
        _articleTitle.Text = a.Title;
        var doc = new DocModel { Title = a.Title, ShowTitle = false };
        doc.AddRange(Markup.Parse(a.Body));
        _reader.Document = FlowDocBuilder.Build(doc, null);
    }

    private async Task ExplainAsync()
    {
        var owner = Window.GetWindow(this);
        if (_current == null || !AiService.EnsureEnabled(owner)) return;
        await AiAssistWindow.RunAsync(owner, "Explained simply: " + _current.Title, AiService.Prompt("prompt.explain"), _current.Body, acceptLabel: null);
    }

    private async Task ApplyAsync()
    {
        var owner = Window.GetWindow(this);
        if (_current == null || !AiService.EnsureEnabled(owner)) return;
        await AiAssistWindow.RunAsync(owner, "How this may apply: " + _current.Title, AiService.Prompt("prompt.guide.apply"),
            _current.Body, acceptLabel: null, withCaseBackground: true);
    }

    private async Task AskAsync(string question = null)
    {
        var owner = Window.GetWindow(this);
        question = (question ?? _search.Text).Trim();
        if (question.Length == 0) { _question.Focus(); return; }
        if (!AiService.EnsureEnabled(owner)) return;
        var context = Search(question).Take(4).Select(x => x.Article).ToList();
        if (context.Count == 0) context = GuideContent.All.Where(a => a.Id is "start" or "help").ToList();
        var material = string.Join("\n\n", context.Select(a => $"ARTICLE: {a.Title}\n{a.Body}"));
        await AiAssistWindow.RunAsync(owner, "Q: " + question,
            AiService.Prompt("prompt.guide.answer") + "\n\nQUESTION: " + question, material, acceptLabel: null);
    }

    private async Task SaveArticleAsync()
    {
        if (_current == null) return;
        var doc = new DocModel { Title = _current.Title, Subtitle = "CaseKeeper guide · " + _current.Topic };
        doc.AddRange(Markup.Parse(_current.Body));
        await ExportHelper.ExportDocumentAsync(Window.GetWindow(this), doc, null);
    }
}
