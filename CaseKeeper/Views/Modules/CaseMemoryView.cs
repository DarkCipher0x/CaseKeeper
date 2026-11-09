using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// Case memory: exactly what the AI is told about the case – compiled from the case's records, kept in the library
/// database (encrypted when the library is protected) – plus what CaseKeeper has learnt from the person's decisions and
/// the notes they write for the AI. Everything here can be rebuilt, forgotten or cleared.
/// </summary>
public sealed class CaseMemoryView : DockPanel
{
    private static Data.Database Db => App.Library.Db;
    private readonly StackPanel _body = new();
    private TextBox _note;

    public CaseMemoryView()
    {
        Margin = new Thickness(28, 22, 28, 18);
        var head = new StackPanel();
        head.Children.Add(Ui.Text("What CaseKeeper knows", "H1", 21));
        head.Children.Add(Ui.Muted("The case memory the AI reads when it goes through your documents, writes the case overview and answers " +
                                   "questions about your case. It's built from your own records and kept in your library on this PC."));
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        bar.Children.Add(Ui.Button("Ask about my case…", "", (_, _) => MainWindow.Shell?.AskAboutCase(null), primary: true));
        bar.Children.Add(Ui.Button("Rebuild now", "", (_, _) => { Rebuild(); Reload(); }, tooltip: "Builds the memory again from your records"));
        bar.Children.Add(Ui.Button("Check my library…", "", (_, _) => MainWindow.Shell?.OpenLibraryCheck()));
        head.Children.Add(bar);
        SetDock(head, Dock.Top);
        Children.Add(head);
        Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += (_, _) => Reload();
    }

    private static void Rebuild()
    {
        try { CaseMemory.Rebuild(Db); }
        catch (Exception ex) { AppLog.Warn("CaseMemory", "Couldn't rebuild the case memory", ex); }
    }

    public void Reload()
    {
        _body.Children.Clear();
        try
        {
            var entries = CaseMemory.Load(Db).ToDictionary(e => e.Section);
            bool encrypted = App.Library.Vault.IsEncrypted;
            var where = new StackPanel();
            where.Children.Add(Ui.Text(encrypted ? "Stored in your library, encrypted" : "Stored in your library – not encrypted yet", "H2"));
            where.Children.Add(Ui.Muted(encrypted
                ? "The memory is in your library's database, which is protected with your password. It's in your backups and deleted with the library."
                : "The memory is in your library's database on this PC. Protect the library with a password to encrypt it, with everything else.", 12));
            where.Children.Add(Ui.Muted((CaseMemory.UpdatedAt(Db) is { } at ? $"Last updated {at:d MMM yyyy HH:mm}. " : "") +
                                        (AiService.UsingCloud || AiProviders.IsCloud(AiService.BulkProvider)
                                            ? "A cloud AI is selected: the parts the AI needs are sent with each request, with names and personal details masked if masking is on."
                                            : "Only your local AI reads it; nothing is uploaded."), 12));
            if (!encrypted)
                where.Children.Add(Ui.Button("Protect the library…", "", (_, _) => MainWindow.Shell?.GoToSettings("security")));
            var use = new CheckBox
            {
                Content = new TextBlock { Text = "Give the AI a short background from this memory with each document it reads (people, case details, hearings, orders, complaints, your notes)", TextWrapping = TextWrapping.Wrap },
                IsChecked = Db.GetSetting(CaseMemory.UseSetting, "1") != "0", Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetName(use, "Give the AI the case background with each document");
            use.Click += (_, _) =>
            {
                Db.SetSetting(CaseMemory.UseSetting, use.IsChecked == true ? "1" : "0");
                Db.Audit("settings.change", "settings", null, "Changed whether the AI gets the case background", use.IsChecked == true ? "On" : "Off");
            };
            where.Children.Add(use);
            _body.Children.Add(Ui.Card(where, new Thickness(0, 0, 0, 12)));

            // The person's own notes for the AI
            var notes = new StackPanel();
            notes.Children.Add(Ui.Text("Things the AI should know", "H2"));
            notes.Children.Add(Ui.Muted("Anything that helps it read your documents correctly – for example who “Mum” or “Dad” means in messages, " +
                                        "a nickname, or that a document set is from an earlier case. It's given to the AI with every document.", 12));
            _note = new TextBox { Text = CaseMemory.Note(Db), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 4000, Margin = new Thickness(0, 6, 0, 6) };
            AutomationProperties.SetName(_note, "Things the AI should know");
            notes.Children.Add(_note);
            notes.Children.Add(Ui.Button("Save notes", "", (_, _) => { CaseMemory.SetNote(Db, _note.Text); Rebuild(); Reload(); }, primary: true));
            _body.Children.Add(Ui.Card(notes, new Thickness(0, 0, 0, 12)));

            // What the AI is told, section by section
            foreach (var (key, title) in CaseMemory.Sections)
            {
                if (key == "notes") continue;
                if (!entries.TryGetValue(key, out var e) || string.IsNullOrWhiteSpace(e.Text)) continue;
                var sp = new StackPanel();
                sp.Children.Add(Ui.Text(title, "H2"));
                var text = new TextBox
                {
                    Text = e.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = null,
                    FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0)
                };
                text.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                AutomationProperties.SetName(text, title);
                sp.Children.Add(text);
                _body.Children.Add(Ui.Card(sp, new Thickness(0, 0, 0, 10)));
            }

            // What's been learnt from the person's decisions
            var learnt = CaseMemory.Learnt(Db);
            var ls = new StackPanel();
            ls.Children.Add(Ui.Text("What CaseKeeper has learnt from you", "H2"));
            ls.Children.Add(Ui.Muted("When you file a document somewhere other than where the AI suggested, similar documents go there next time. " +
                                     "When you reject a suggestion, the same one isn't suggested again.", 12));
            if (learnt.Count == 0) ls.Children.Add(Ui.Muted("Nothing yet.", 12));
            foreach (var l in learnt.OrderByDescending(l => l.CreatedAt).Take(200))
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var forget = Ui.Button("Forget", "", (_, _) => { CaseMemory.Forget(Db, l); Reload(); });
                AutomationProperties.SetName(forget, "Forget: " + l.Note);
                SetDock(forget, Dock.Right);
                row.Children.Add(forget);
                row.Children.Add(Ui.Text($"{l.KindText} · {l.Note}", null, 12.5));
                ls.Children.Add(row);
            }
            if (learnt.Count > 0)
                ls.Children.Add(Ui.Button("Forget everything learnt…", "", (_, _) =>
                {
                    if (MessageBox.Show(Ui.Owner(this), $"Forget all {learnt.Count} things CaseKeeper has learnt from your decisions?\n\nYour notes for the AI and your case records are kept.",
                            "Forget", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    CaseMemory.ForgetAll(Db);
                    Reload();
                }));
            _body.Children.Add(Ui.Card(ls, new Thickness(0, 0, 0, 12)));
        }
        catch (Exception ex)
        {
            var id = AppLog.Error("UI", "Case memory screen couldn't be built", ex);
            _body.Children.Add(Ui.Muted($"The case memory couldn't be shown (ref {id})."));
        }
    }
}
