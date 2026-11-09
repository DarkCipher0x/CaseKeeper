using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.Services.Documents;
using CaseKeeper.Services.Guide;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// The help assistant, in two modes. "Help with CaseKeeper": ask how to do something and get step-by-step
/// directions with buttons that go straight to the right screen, grounded in a map of the app and the user guide
/// (it's told counts about the library, never titles, names or content). "My case": ask about your own case; the
/// answer comes from the case memory and the matching pages of your documents, with buttons that open each source
/// page. Either way it never changes anything. Without AI it still finds the matching help.
/// </summary>
public class HelpAssistantView : UserControl
{
    public const string AppMode = "app", CaseMode = "case";
    private string _mode = App.Library.Db.GetSetting("assistant.mode", AppMode) == CaseMode ? CaseMode : AppMode;
    private readonly RadioButton _appMode, _caseMode;
    private readonly TextBlock _hint, _intro;
    private readonly StackPanel _messages;
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input;
    private readonly Button _send, _stop;
    private readonly TextBlock _status;
    private readonly WrapPanel _suggestions;
    private readonly List<(string Role, string Content)> _history = new();
    private CancellationTokenSource _cts;

    public HelpAssistantView()
    {
        var root = new DockPanel { Margin = new Thickness(36, 28, 36, 20) };

        // ---- header
        var head = new StackPanel();
        var titleRow = new DockPanel();
        var newChat = Ui.Button("New conversation", "", (_, _) => Reset(), tooltip: "Start again");
        DockPanel.SetDock(newChat, Dock.Right);
        titleRow.Children.Add(newChat);
        titleRow.Children.Add(Ui.Text("Help assistant", "H1"));
        head.Children.Add(titleRow);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        _appMode = new RadioButton { Content = "Help with CaseKeeper", GroupName = "assistantMode", IsChecked = _mode == AppMode, Margin = new Thickness(0, 0, 18, 0) };
        _caseMode = new RadioButton { Content = "My case", GroupName = "assistantMode", IsChecked = _mode == CaseMode };
        _appMode.Checked += (_, _) => SetMode(AppMode);
        _caseMode.Checked += (_, _) => SetMode(CaseMode);
        modes.Children.Add(_appMode);
        modes.Children.Add(_caseMode);
        head.Children.Add(modes);
        _intro = Ui.Muted("", 13);
        head.Children.Add(_intro);
        _status = Ui.Muted("", 12.5);
        _status.Margin = new Thickness(0, 8, 0, 6);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ---- input
        var inputArea = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        _send = Ui.Button("Ask", "", (_, _) => _ = AskAsync(_input.Text), primary: true, tooltip: "Enter to send · Shift+Enter for a new line");
        _send.Margin = new Thickness(8, 0, 0, 0);
        _stop = Ui.Button("Stop", "", (_, _) => _cts?.Cancel());
        _stop.Margin = new Thickness(6, 0, 0, 0);
        _stop.Visibility = Visibility.Collapsed;
        buttons.Children.Add(_send);
        buttons.Children.Add(_stop);
        DockPanel.SetDock(buttons, Dock.Right);
        inputArea.Children.Add(buttons);
        var inputGrid = new Grid();
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 140, FontSize = 14,
            Padding = new Thickness(12, 10, 12, 10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top
        };
        System.Windows.Automation.AutomationProperties.SetName(_input, "Ask the help assistant");
        var hint = _hint = new TextBlock { Text = "e.g. How do I put my case number on every letter?", Margin = new Thickness(14, 12, 0, 0), IsHitTestVisible = false, FontSize = 14 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        hint.SetBinding(VisibilityProperty, new System.Windows.Data.Binding("Text")
            { Source = _input, Converter = (System.Windows.Data.IValueConverter)Application.Current.FindResource("PlaceholderVis") });
        inputGrid.Children.Add(_input);
        inputGrid.Children.Add(hint);
        inputArea.Children.Add(inputGrid);
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                _ = AskAsync(_input.Text);
            }
        };
        DockPanel.SetDock(inputArea, Dock.Bottom);
        root.Children.Add(inputArea);

        _suggestions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_suggestions, Dock.Bottom);
        root.Children.Add(_suggestions);

        // ---- conversation
        _messages = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        _scroller = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var frame = new Border { Child = _scroller, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 6, 12) };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        root.Children.Add(frame);

        Content = root;
        ApplyMode();
        Loaded += (_, _) => { RefreshStatus(); if (_messages.Children.Count == 0) Reset(); _input.Focus(); };
    }

    /// <summary>Switches between help with the app and questions about the case (remembered).</summary>
    public void SetMode(string mode)
    {
        mode = mode == CaseMode ? CaseMode : AppMode;
        if (mode == _mode && _messages.Children.Count > 0) { ApplyMode(); return; }
        _mode = mode;
        App.Library.Db.SetSetting("assistant.mode", mode);
        ApplyMode();
        Reset();
    }

    private void ApplyMode()
    {
        if (_appMode.IsChecked != (_mode == AppMode)) _appMode.IsChecked = _mode == AppMode;
        if (_caseMode.IsChecked != (_mode == CaseMode)) _caseMode.IsChecked = _mode == CaseMode;
        _intro.Text = _mode == CaseMode
            ? "Ask about your own case – when something happened, what an order said, what's still outstanding. Answers come from your records and " +
              "documents, with a button to open each source page. " +
              (AiService.UsingCloud ? $"Your question, the case memory and the matching extracts are sent to {AiProviders.Get(AiService.Provider).Name}" +
                                      (AiService.MaskCloud ? ", with names and personal details masked." : " (masking is off).")
                                    : "Everything stays on this PC with your local AI.") + " Organising help only, not legal advice."
            : "Ask how to do anything in CaseKeeper, in your own words. You'll get step-by-step directions and buttons that take " +
              "you straight there. Only your question (and counts about your library) is sent to your AI – nothing about your case.";
        _hint.Text = _mode == CaseMode ? "e.g. When is the next hearing, and what do I need to do before it?" : "e.g. How do I put my case number on every letter?";
    }

    private void RefreshStatus()
    {
        _status.Text = AiService.Enabled && !string.IsNullOrWhiteSpace(AiService.ActiveModel)
            ? $"Answering with {AiService.ActiveDescription}."
            : "AI is off, so answers come straight from the help pages. Turn on AI in Settings → AI for step-by-step answers in your own words.";
    }

    private void Reset()
    {
        _cts?.Cancel();
        _history.Clear();
        _messages.Children.Clear();
        if (_mode == CaseMode)
        {
            AddAssistant("Ask me anything about your case and I'll answer from your records and documents, showing where each fact comes from. " +
                         "The more you've organised (Check my library), the more I know.",
                new List<HelpKnowledge.Link> { new("go", "memory", "What CaseKeeper knows"), new("go", "librarycheck", "Check my library") });
            _suggestions.Children.Clear();
            foreach (var s in CaseSuggestions)
            {
                var chip = new Button { Content = s, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6), FontSize = 12.5 };
                chip.Click += (_, _) => _ = AskAsync(s);
                _suggestions.Children.Add(chip);
            }
            _suggestions.Visibility = Visibility.Visible;
            return;
        }
        AddAssistant("Hi! I can show you how to do anything in CaseKeeper – importing papers, finding a form, writing a statement, building a " +
                     "bundle, setting a password, using the AI and more. What would you like to do?",
            new List<HelpKnowledge.Link> { new("guide", "quickstart", "Quick start guide"), new("go", "guide", "Open Guide & FAQ") });
        _suggestions.Children.Clear();
        foreach (var s in HelpKnowledge.Suggestions)
        {
            var chip = new Button { Content = s, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6), FontSize = 12.5 };
            chip.Click += (_, _) => _ = AskAsync(s);
            _suggestions.Children.Add(chip);
        }
        _suggestions.Visibility = Visibility.Visible;
    }

    /// <summary>Asks a question from elsewhere in the app (e.g. the command palette).</summary>
    public void Ask(string question)
    {
        if (!string.IsNullOrWhiteSpace(question)) _ = AskAsync(question);
    }

    // ------------------------------------------------------------------ asking
    private async Task AskAsync(string question)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0 || _cts != null) return;
        _input.Clear();
        _suggestions.Visibility = Visibility.Collapsed;
        AddUser(question);
        RefreshStatus();

        var screen = MainWindow.Shell?.LastScreenName ?? "";
        if (!AiService.Enabled || string.IsNullOrWhiteSpace(AiService.ActiveModel)
            || (AiService.UsingCloud && !AiService.EnsureCloudConsent(Ui.Owner(this))))
        {
            if (_mode == CaseMode) AnswerCaseWithoutAi(question);
            else AnswerWithoutAi(question, null);
            return;
        }
        if (_mode == CaseMode) { await AskCaseAsync(question); return; }

        var (bubble, body) = AddStreamingAssistant();
        _cts = new CancellationTokenSource();
        _send.IsEnabled = false;
        _stop.Visibility = Visibility.Visible;
        var answer = "";
        try
        {
            var system = BuildSystemPrompt(question, screen);
            var turns = _history.TakeLast(8).ToList();
            turns.Add(("user", question));
            App.Library.Db.Audit("ai.help", "ai", null, $"Help assistant question ({AiService.ActiveModel})", $"{question.Length} characters, plus library counts (no titles, names or document content sent)");
            answer = await AiService.ChatAsync(system, turns, piece => Dispatcher.BeginInvoke(() =>
            {
                body.Text += piece;
                _scroller.ScrollToEnd();
            }), _cts.Token);
            _history.Add(("user", question));
            _history.Add(("assistant", answer));
            _messages.Children.Remove(bubble);
            var (text, links) = HelpKnowledge.Finish(answer, question);
            AddAssistant(text, links);
        }
        catch (OperationCanceledException)
        {
            _messages.Children.Remove(bubble);
            var (text, links) = HelpKnowledge.ExtractLinks(body.Text);
            AddAssistant(text + (text.Length > 0 ? "\n\n" : "") + "*(stopped)*", links);
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "Help assistant couldn't reach the AI", ex);
            _messages.Children.Remove(bubble);
            AnswerWithoutAi(question, AiService.UsingCloud
                ? $"I couldn't reach {AiService.ActiveDescription} ({ex.Message}). Here's what the help pages say instead:"
                : $"I couldn't reach your AI ({ex.Message}). Is Ollama running? Here's what the help pages say instead:");
        }
        finally
        {
            _cts = null;
            _send.IsEnabled = true;
            _stop.Visibility = Visibility.Collapsed;
            _input.Focus();
        }
    }

    private static string BuildSystemPrompt(string question, string screen)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(AiService.Prompt("prompt.helpdesk"));
        sb.AppendLine();
        sb.AppendLine($"The person is using CaseKeeper {MainWindow.AppVersion} in {(MainWindow.Shell?.ProfessionalMode == true ? "Professional" : "Simple")} mode" +
                      (string.IsNullOrWhiteSpace(screen) ? "." : $", and was last on the '{screen}' screen."));
        sb.AppendLine($"AI features are {(AiService.Enabled ? "on" : "off")}. Password protection is {(App.Library.Vault.IsEncrypted ? "on" : "off")}.");
        // What's in the library, as numbers only (no titles, names or content), so the assistant can suggest the next step
        try { sb.AppendLine().Append(Services.BulkImport.LibraryCheck.DescribeForAssistant(Services.BulkImport.LibraryCheck.Count(App.Library.Db))); }
        catch (Exception ex) { AppLog.Warn("AI", "Couldn't count the library for the assistant", ex); }
        sb.AppendLine("When the person wants help organising, use these counts to suggest the most useful next step (for example (go:librarycheck) when " +
                      "documents are uncategorised or a check has never run, (go:review) when suggestions are waiting, (go:casemap) to see the case laid out), " +
                      "with a link. You never change anything yourself: everything happens in the app's screens and through Import review approval.");
        sb.AppendLine();
        sb.Append(HelpKnowledge.BuildContext(question, Math.Max(2500, AiService.ChunkChars / 3)));
        return sb.ToString();
    }

    public static readonly string[] CaseSuggestions =
    {
        "Where does my case stand right now?",
        "When is the next hearing, and what do I need to do before it?",
        "What directions are overdue or due soon?",
        "Which complaints are still waiting for a reply?",
        "What did the last order say?",
        "What documents am I missing?"
    };

    // ------------------------------------------------------------------ questions about the case
    private async Task AskCaseAsync(string question)
    {
        var (bubble, body) = AddStreamingAssistant();
        _cts = new CancellationTokenSource();
        _send.IsEnabled = false;
        _stop.Visibility = Visibility.Visible;
        var db = App.Library.Db;
        try
        {
            var (context, sources) = await Task.Run(() => CaseQuestion.Build(db, question, Math.Clamp(AiService.ChunkChars, 6000, 24000)));
            // The case details go in the message, not the instructions: messages are what a cloud AI's masking covers
            var system = AiService.Prompt("prompt.case.answer") + $"\n\nTODAY'S DATE: {DateTime.Today:d MMMM yyyy}";
            var turns = _history.TakeLast(6).ToList();
            turns.Add(("user", context + "\n\nQUESTION: " + question));
            db.Audit("ai.case.ask", "ai", null, $"Question about the case ({AiService.ActiveModel})",
                $"{question.Length} characters; sent with the case memory and {sources.Count} matching extract(s){(AiService.UsingCloud ? $" to {AiProviders.Get(AiService.Provider).Name}{(AiService.MaskCloud ? ", masked" : ", not masked")}" : " to the local AI")}.");
            var answer = await AiService.ChatAsync(system, turns, piece => Dispatcher.BeginInvoke(() =>
            {
                body.Text += piece;
                _scroller.ScrollToEnd();
            }), _cts.Token);
            _history.Add(("user", question));
            _history.Add(("assistant", answer));
            _messages.Children.Remove(bubble);
            AddCaseAnswer(answer);
        }
        catch (OperationCanceledException)
        {
            _messages.Children.Remove(bubble);
            AddCaseAnswer(body.Text + "\n\n*(stopped)*");
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "Case question couldn't reach the AI", ex);
            _messages.Children.Remove(bubble);
            AddAssistant($"I couldn't reach your AI ({ex.Message}). You can still look things up in **Case memory**, the **Case map** and **Search**.",
                new List<HelpKnowledge.Link> { new("go", "memory", "What CaseKeeper knows"), new("go", "search", "Open Search") });
        }
        finally
        {
            _cts = null;
            _send.IsEnabled = true;
            _stop.Visibility = Visibility.Collapsed;
            _input.Focus();
        }
    }

    private void AddCaseAnswer(string answer)
    {
        var (withoutDocs, docs) = CaseQuestion.ExtractDocLinks(App.Library.Db, answer);
        var (text, links) = HelpKnowledge.ExtractLinks(withoutDocs);
        var all = docs.Take(6).Select(d => new HelpKnowledge.Link("doc", $"{d.DocumentId}:{d.Page}", $"Open “{(d.Title.Length > 40 ? d.Title[..40] + "…" : d.Title)}” p. {d.Page}"))
            .Concat(links.Take(2)).ToList();
        AddAssistant(text, all);
    }

    /// <summary>Without AI: the matching pages from the search index, each with a button to open it.</summary>
    private void AnswerCaseWithoutAi(string question)
    {
        var (_, sources) = CaseQuestion.Build(App.Library.Db, question, 8000);
        var links = sources.Select(s => new HelpKnowledge.Link("doc", $"{s.DocumentId}:{s.Page}", $"Open “{(s.Title.Length > 40 ? s.Title[..40] + "…" : s.Title)}” p. {s.Page}")).ToList();
        AddAssistant(sources.Count == 0
                ? "AI is off, and I couldn't find pages matching those words. Try **Search** with a name, date or reference, or look at **Case memory**."
                : "AI is off, so I can't write an answer, but these pages match your question:",
            links.Count > 0 ? links : new List<HelpKnowledge.Link> { new("go", "search", "Open Search"), new("go", "memory", "What CaseKeeper knows") },
            showSetup: true);
    }

    /// <summary>Without AI: ready-made directions if one matches, else the best help sections, with buttons.</summary>
    private void AnswerWithoutAi(string question, string preface)
    {
        var recipes = HelpKnowledge.MatchRecipes(question, 1);
        var sections = recipes.Count > 0 ? new() : HelpKnowledge.Relevant(question, 4000).Take(2).ToList();
        var articles = GuideView.Search(question).Take(3).Select(x => x.Article).ToList();
        var words = HelpKnowledge.Words(question);
        var links = new List<HelpKnowledge.Link>();
        foreach (var r in recipes)
            foreach (var l in r.Links) HelpKnowledge.AddLink(links, l);
        foreach (var (key, label) in HelpKnowledge.Screens)
            if (words.Any(w => w.Length > 3 && label.Contains(w, StringComparison.OrdinalIgnoreCase)))
                HelpKnowledge.AddLink(links, "go:" + key);
        foreach (var a in articles) links.Add(new HelpKnowledge.Link("guide", a.Id, "Guide: " + a.Title));

        var sb = new System.Text.StringBuilder();
        if (preface != null) sb.AppendLine(preface).AppendLine();
        foreach (var r in recipes) sb.AppendLine("**" + r.Title + "**").AppendLine(r.Steps).AppendLine();
        if (recipes.Count == 0 && sections.Count == 0 && articles.Count == 0)
            sb.Append("I couldn't find help on that. Try different words – for example the name of a screen, a form (C100) or what you want to make (letter, bundle, backup).");
        foreach (var (title, text) in sections)
        {
            sb.AppendLine("**" + title + "**");
            var excerpt = Regex.Replace(text, @"\n{3,}", "\n\n");
            sb.AppendLine(excerpt.Length > 900 ? excerpt[..900].TrimEnd() + "…" : excerpt).AppendLine();
        }
        AddAssistant(sb.ToString().Trim(), links.Take(6).ToList(),
            showSetup: !AiService.Enabled || string.IsNullOrWhiteSpace(AiService.ActiveModel));
    }

    // ------------------------------------------------------------------ bubbles
    private Border Bubble(bool user, UIElement content)
    {
        var b = new Border
        {
            Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(user ? 80 : 0, 6, user ? 0 : 80, 6),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left, MaxWidth = 860
        };
        b.SetResourceReference(Border.BackgroundProperty, user ? "PrimarySoft" : "Surface");
        if (!user)
        {
            b.BorderThickness = new Thickness(1);
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        }
        _messages.Children.Add(b);
        Dispatcher.BeginInvoke(() => _scroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
        return b;
    }

    private void AddUser(string text) =>
        Bubble(true, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 });

    private (Border Bubble, TextBlock Body) AddStreamingAssistant()
    {
        var panel = new StackPanel();
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var thinking = Ui.Muted("Thinking…", 12);
        panel.Children.Add(thinking);
        panel.Children.Add(body);
        body.SizeChanged += (_, _) => thinking.Visibility = body.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        return (Bubble(false, panel), body);
    }

    private void AddAssistant(string text, List<HelpKnowledge.Link> links, bool showSetup = false)
    {
        var panel = new StackPanel();
        foreach (var block in Render(text)) panel.Children.Add(block);

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var l in links)
        {
            var glyph = l.Kind switch { "settings" => "", "guide" => "", _ => "" };
            var b = Ui.Button(l.Label, glyph, (_, _) => MainWindow.Shell?.Navigate(l.Kind, l.Target), primary: actions.Children.Count == 0);
            actions.Children.Add(b);
        }
        if (showSetup)
            actions.Children.Add(Ui.Button("Set up the AI", "", (_, _) => MainWindow.Shell?.GoToSettings("ai"),
                tooltip: "Turn on step-by-step answers in your own words"));
        var copy = Ui.Button(null, "", (_, _) => Ui.CopyToClipboard(text), tooltip: "Copy this answer");
        actions.Children.Add(copy);
        panel.Children.Add(actions);
        Bubble(false, panel);
    }

    /// <summary>Simple rendering of the AI's Markdown: headings, bullets, numbered steps and bold text.</summary>
    private static IEnumerable<UIElement> Render(string text)
    {
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { yield return new Border { Height = 6 }; continue; }
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 1, 0, 1) };
            var trimmed = line.TrimStart();
            var heading = Regex.Match(trimmed, @"^#{1,4}\s+(.*)$");
            if (heading.Success)
            {
                trimmed = heading.Groups[1].Value;
                tb.FontWeight = FontWeights.SemiBold;
                tb.FontSize = 15;
                tb.Margin = new Thickness(0, 6, 0, 2);
            }
            else if (Regex.IsMatch(trimmed, @"^[-*•]\s+"))
            {
                trimmed = "•  " + Regex.Replace(trimmed, @"^[-*•]\s+", "");
                tb.Margin = new Thickness(line.Length - line.TrimStart().Length > 1 ? 30 : 12, 1, 0, 1);
            }
            else if (Regex.IsMatch(trimmed, @"^\d+[.)]\s+"))
                tb.Margin = new Thickness(6, 2, 0, 2);
            foreach (var inline in Markup.ParseInline(trimmed.Replace("`", "")))
            {
                var run = new Run(inline.Text);
                if (inline.Bold) { run.FontWeight = FontWeights.SemiBold; run.SetResourceReference(TextElement.ForegroundProperty, "AccentText"); }
                if (inline.Italic) run.FontStyle = FontStyles.Italic;
                tb.Inlines.Add(run);
            }
            yield return tb;
        }
    }
}
