using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// Import review → Duplicates &amp; versions: copies and versions of the same document found by comparing their words,
/// with the newest marked and what changed. Nothing is ever deleted: older versions are tagged, and every choice is in
/// the audit trail and can be undone.
/// </summary>
public partial class ImportReviewView
{
    public const string OlderTag = "Older version", LatestTag = "Latest version", VersionTag = "Version";
    private List<VersionGroup> _versions;
    private string _versionsSignature = "";
    private bool _versionsBusy;

    private static Dictionary<string, string> VersionDecisions()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(Db.GetSetting("versions.decisions", "{}") ?? "{}") ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static void SaveDecisions(Dictionary<string, string> d) => Db.SetSetting("versions.decisions", JsonSerializer.Serialize(d));

    private int OpenVersionGroups => _versions?.Count(g => !VersionDecisions().ContainsKey(g.Key)) ?? 0;

    /// <summary>Works out the groups in the background when the library has changed since the last time.</summary>
    private async Task RefreshVersionsAsync()
    {
        if (_versionsBusy) return;
        List<CaseDocument> docs;
        try { docs = Db.GetDocuments(); }
        catch (Exception ex) { AppLog.Warn("Review", "Couldn't list documents for the version check", ex); return; }
        var signature = $"{docs.Count}|{docs.Sum(d => d.Id)}|{string.Join(",", docs.Select(d => d.FileHash.Length > 6 ? d.FileHash[..6] : d.FileHash))}".GetHashCode().ToString();
        if (_versions != null && signature == _versionsSignature) return;
        _versionsBusy = true;
        try
        {
            var modified = Db.Query<ImportFile>("DocumentId IS NOT NULL").GroupBy(f => f.DocumentId!.Value)
                .ToDictionary(g => g.Key, g => g.Max(f => f.SourceModifiedAt));
            var inputs = docs.Select(d => new VersionInput(d.Id, d.Title, string.Join("\n", Db.GetAllPageText(d.Id).OrderBy(p => p.Key).Select(p => p.Value)),
                d.DocumentDate, modified.GetValueOrDefault(d.Id) ?? d.DateReceived, d.FileHash)).ToList();
            _versions = await Task.Run(() => VersionDetector.Find(inputs));
            _versionsSignature = signature;
            AppLog.Info("Review", $"Version check: {_versions.Count} group(s) among {docs.Count} documents");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Review", "The version check failed", ex);
            _versions ??= new();
        }
        finally { _versionsBusy = false; }
        if (IsLoaded) Reload();
    }

    private void ShowVersions()
    {
        _subtitle.Text = "Copies and versions of the same document, found by comparing their words. The newest is marked ★. Nothing is deleted: " +
                         "you can tag the older ones, link them, or say they aren't related, and undo it later.";
        _detail.Children.Clear();
        _detail.Children.Add(Ui.Muted("How the newest is decided: the document's own date first (from Document details), then when the file was last " +
                                      "changed, then which has more in it. Always open both and check before relying on one.\n\n" +
                                      "“Same content” means the same words in different files, for example a PDF and a Word copy of one letter.", 12.5));
        var sp = new StackPanel();
        if (_versions == null)
        {
            sp.Children.Add(Ui.Muted("Comparing your documents…", 13));
            _ = RefreshVersionsAsync();
            _middle.Content = sp;
            return;
        }
        var decisions = VersionDecisions();
        var open = _versions.Where(g => !decisions.ContainsKey(g.Key)).ToList();
        var done = _versions.Where(g => decisions.ContainsKey(g.Key)).ToList();
        if (open.Count == 0) sp.Children.Add(Ui.Muted(_versions.Count == 0 ? "No copies or versions found among your documents." : "Nothing left to decide.", 13));
        foreach (var g in open) sp.Children.Add(GroupCard(g, null));
        if (done.Count > 0)
        {
            sp.Children.Add(new TextBlock { Text = "ALREADY DECIDED", Style = Ui.Style("Label"), Margin = new Thickness(0, 18, 0, 4) });
            foreach (var g in done) sp.Children.Add(GroupCard(g, decisions[g.Key]));
        }
        _middle.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement GroupCard(VersionGroup g, string decided)
    {
        var card = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        var sp = new StackPanel();
        var head = new WrapPanel();
        var badge = new Border { Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, g.Kind == VersionKind.Versions ? "WarningSoft" : "PrimarySoft");
        badge.Child = new TextBlock { Text = g.KindText, FontSize = 12, FontWeight = FontWeights.SemiBold };
        head.Children.Add(badge);
        head.Children.Add(new TextBlock { Text = $"{g.Members.Count} documents", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted(g.NewestReason, 12));
        var newest = g.Members[0];

        foreach (var m in g.Members)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);
            var id = m.DocumentId;
            buttons.Children.Add(Ui.Button("Open", null, (_, _) => Shell.OpenDocumentById(id)));
            if (!m.Newest && g.Kind != VersionKind.Exact)
                buttons.Children.Add(Ui.Button("What changed", null, (_, _) => ShowChanges(m, newest), tooltip: "Compare this with the newest, sentence by sentence"));
            row.Children.Add(buttons);
            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = (m.Newest ? "★ " : "") + m.Title + (m.Newest ? "  (newest)" : ""), FontWeight = m.Newest ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var info = new List<string>();
            if (m.EffectiveDate is { } d) info.Add($"{m.DateSource} {d:d MMM yyyy}");
            if (!m.Newest) info.Add($"{m.Similarity:P0} the same wording");
            if (m.Diff != null) info.Add(m.Diff.Describe());
            if (info.Count > 0) text.Children.Add(Ui.Muted(string.Join(" · ", info), 11.5));
            row.Children.Add(text);
            sp.Children.Add(row);
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (decided == null)
        {
            actions.Children.Add(Ui.Button("Keep newest, tag the others as older", "", (_, _) => DecideVersions(g, "older"), primary: true,
                tooltip: $"Tags the newest “{LatestTag}” and the others “{OlderTag}”. Nothing is deleted."));
            actions.Children.Add(Ui.Button("Keep all, link as versions", "", (_, _) => DecideVersions(g, "linked"), tooltip: $"Tags them all “{VersionTag}”"));
            actions.Children.Add(Ui.Button("Not related", "", (_, _) => DecideVersions(g, "dismissed"), tooltip: "Stop showing this group"));
        }
        else
        {
            actions.Children.Add(Ui.Muted(decided switch { "older" => "Older ones tagged.", "linked" => "Linked as versions.", _ => "Marked as not related." }, 12));
            actions.Children.Add(Ui.Button("Undo", "", (_, _) => UndoVersions(g, decided)));
        }
        sp.Children.Add(actions);
        card.Child = sp;
        AutomationProperties.SetName(card, $"{g.KindText}: {newest.Title} and {g.Members.Count - 1} more");
        return card;
    }

    private static void Retag(long docId, Func<List<string>, List<string>> change)
    {
        var d = Db.GetDocument(docId);
        if (d == null || d.DeletedAt != null) return;
        var tags = (d.Tags ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var next = change(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (next.SequenceEqual(tags)) return;
        d.Tags = string.Join(", ", next);
        Db.UpdateDocument(d);
    }

    private void DecideVersions(VersionGroup g, string decision)
    {
        var newest = g.Members[0];
        if (decision == "older")
        {
            Retag(newest.DocumentId, t => t.Append(LatestTag).ToList());
            foreach (var m in g.Members.Skip(1)) Retag(m.DocumentId, t => t.Append(OlderTag).ToList());
        }
        else if (decision == "linked")
            foreach (var m in g.Members) Retag(m.DocumentId, t => t.Append(VersionTag).ToList());
        var all = VersionDecisions();
        all[g.Key] = decision;
        SaveDecisions(all);
        Db.Audit("document.versions", "document", newest.DocumentId, decision switch
        {
            "older" => $"Marked “{newest.Title}” as the latest version, {g.Members.Count - 1} older",
            "linked" => $"Linked {g.Members.Count} documents as versions",
            _ => $"Marked {g.Members.Count} similar documents as not related"
        }, string.Join("\n", g.Members.Select(m => $"#{m.DocumentId} {m.Title}")));
        Shell.RefreshAll();
        Reload();
    }

    private void UndoVersions(VersionGroup g, string decision)
    {
        foreach (var m in g.Members) Retag(m.DocumentId, t => t.Where(x => !x.Equals(OlderTag, StringComparison.OrdinalIgnoreCase)
            && !x.Equals(LatestTag, StringComparison.OrdinalIgnoreCase) && !(decision == "linked" && x.Equals(VersionTag, StringComparison.OrdinalIgnoreCase))).ToList());
        var all = VersionDecisions();
        all.Remove(g.Key);
        SaveDecisions(all);
        Db.Audit("document.versions", "document", g.Members[0].DocumentId, $"Undid the version decision for {g.Members.Count} documents", "");
        Shell.RefreshAll();
        Reload();
    }

    /// <summary>The older document's text against the newest, sentence by sentence: added in green, gone in red.</summary>
    private void ShowChanges(VersionMember older, VersionMember newest)
    {
        string Text(long id) => string.Join("\n", Db.GetAllPageText(id).OrderBy(p => p.Key).Select(p => p.Value));
        var changes = VersionDetector.Compare(Text(older.DocumentId), Text(newest.DocumentId));
        var doc = new FlowDocument { PagePadding = new Thickness(16), FontSize = 13 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        var legend = new Paragraph(new Run($"From “{older.Title}” to the newest, “{newest.Title}”. ")) { FontSize = 12 };
        legend.Inlines.Add(Mark(new Run("Added in the newest"), "SuccessSoft"));
        legend.Inlines.Add(new Run("  "));
        legend.Inlines.Add(Mark(new Run("Only in the older one") { TextDecorations = TextDecorations.Strikethrough }, "DangerSoft"));
        doc.Blocks.Add(legend);
        int unchangedRun = 0;
        foreach (var (change, text) in changes)
        {
            if (change == VersionDetector.LineChange.Same)
            {
                // Long unchanged stretches are folded so the changes stand out
                if (++unchangedRun > 2) continue;
                doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 2, 0, 2) });
                continue;
            }
            if (unchangedRun > 2) doc.Blocks.Add(new Paragraph(new Run($"… {unchangedRun - 2} unchanged sentence(s) …")) { FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 2) });
            unchangedRun = 0;
            var run = new Run((change == VersionDetector.LineChange.Added ? "+ " : "− ") + text);
            if (change == VersionDetector.LineChange.Removed) run.TextDecorations = TextDecorations.Strikethrough;
            doc.Blocks.Add(new Paragraph(Mark(run, change == VersionDetector.LineChange.Added ? "SuccessSoft" : "DangerSoft")) { Margin = new Thickness(0, 2, 0, 2) });
        }
        var w = new Window
        {
            Title = "What changed", Width = 820, Height = 680, Owner = Ui.Owner(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)Application.Current.FindResource("AppWindow"),
            Content = new FlowDocumentScrollViewer { Document = doc, IsToolBarVisible = false }
        };
        w.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        w.Show();
    }

    private static Inline Mark(Inline i, string brush)
    {
        i.SetResourceReference(TextElement.BackgroundProperty, brush);
        return i;
    }
}
