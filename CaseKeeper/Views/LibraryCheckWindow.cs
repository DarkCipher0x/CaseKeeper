using System.Windows;
using System.Windows.Controls;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;

namespace CaseKeeper.Views;

/// <summary>
/// Library check: choose which documents already in the library to check, whether the AI helps (or only
/// CaseKeeper's own rules), which sections to fill, and whether what's found is added to the case straight away
/// (marked to check, see TrackAutoAdd) or waits in Import review.
/// </summary>
public sealed class LibraryCheckWindow : FormDialog
{
    private static Data.Database Db => App.Library.Db;

    private readonly RadioButton _all, _uncategorised, _notChecked;
    private readonly CheckBox _useAi, _readText, _overview, _autoAdd, _autoCheck;
    private readonly Dictionary<string, CheckBox> _sections = new();
    private readonly TextBlock _scopeCount;
    private readonly List<CaseDocument> _docs;
    private readonly HashSet<long> _checkedBefore;
    private readonly long? _unknown;

    public List<CaseDocument> Documents { get; private set; } = new();
    public BulkOptions Options { get; private set; }

    /// <summary>"1": documents imported from now on are checked by themselves (see MainWindow's auto-check queue).</summary>
    public const string AutoCheckSetting = "track.autoCheckImports";

    public LibraryCheckWindow() : base("Check my library", 640, 820, "Start check")
    {
        var stats = LibraryCheck.Count(Db);
        _docs = Db.GetDocuments();
        _unknown = CategoryMatcher.Unknown(Db.GetCategories())?.Id;
        _checkedBefore = Db.Query<ImportFile>("DocumentId IS NOT NULL AND AnalysisStatus=$a", ("$a", (int)AnalysisState.Done))
            .Select(f => f.DocumentId!.Value).ToHashSet();

        AddHint("CaseKeeper reads every document you choose, works out where each belongs and what it tells you – hearings, orders, " +
                "directions, dates for the timeline, contact, complaints, people, Scott schedule items – and looks for copies and versions. " +
                "What it's confident about goes straight into your case, marked “From AI · check”; the rest waits in Import review. " +
                "Documents read before aren't read again unless they've changed.");
        Fields.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = $"{stats.Documents} document(s) · {stats.Uncategorised} not yet categorised · {stats.TextNotRead + stats.NoText} without readable text · " +
                   $"{stats.CheckedBefore} organised before · {stats.ExactCopies} exact cop{(stats.ExactCopies == 1 ? "y" : "ies")}" +
                   (stats.LastCheck is { } last ? $" · last checked {last:d MMM yyyy}" : "")
        });

        Heading("Which documents");
        _all = Radio("All documents", true);
        _uncategorised = Radio("Only those not yet categorised", false);
        _notChecked = Radio("Only those not organised before", false);
        _scopeCount = new TextBlock { Margin = new Thickness(22, 4, 0, 0), FontSize = 12 };
        _scopeCount.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Fields.Children.Add(_scopeCount);

        Heading("How");
        var provider = AiService.BulkProvider;
        string who = AiProviders.IsCloud(provider) ? $"{AiProviders.Get(provider).Name} (cloud – masked as set in Settings → AI)" : "your local AI";
        _useAi = Check(AiService.Enabled ? $"Use AI to suggest categories, dates, records and complaint details ({who})"
                                         : "Use AI (turn it on in Settings → AI first)", AiService.Enabled);
        _useAi.IsEnabled = AiService.Enabled;
        AddHint("Without AI, CaseKeeper's own rules still suggest categories and complaint records, and the copies and versions check always runs.");
        _readText = Check("Read the text of documents that don't have it yet (slower for scans)", true);
        _autoAdd = Check("Add what's found to the case straight away, marked to check (fills the Track sections, Case & hearings and Parties)", TrackAutoAdd.Enabled(Db));
        AddHint("Only when CaseKeeper is confident, there are no warnings or conflicts and nothing like it is in your case already. Each one " +
                "can be checked off or undone in Import review → Added by AI – to check, and the whole check can be undone in one go. " +
                "Filling the Track sections needs an AI (your local AI is fine); without one, only categories and complaints are suggested.");
        _autoCheck = Check("From now on, do this by itself for documents I import", AiService.Enabled && Db.GetSetting(AutoCheckSetting, "1") == "1");
        _autoCheck.IsEnabled = AiService.Enabled;
        AddHint("New documents are read once their text is ready. With a cloud AI you're always asked (and shown what will be sent) first.");

        Heading("What to fill in");
        var saved = LibraryCheck.SavedSections(Db);
        foreach (var s in LibraryCheck.Sections)
        {
            var box = Check(s.Name, saved.Contains(s.Key));
            box.ToolTip = s.Explain;
            _sections[s.Key] = box;
        }
        AddHint("A short summary of each document is always kept: the case overview and the Case map are built from it.");
        _overview = Check("Write a case overview when it finishes (uses AI; saved in AI → Reports)", AiService.Enabled && Db.GetSetting(LibraryCheck.OverviewSetting + ".want", "1") == "1");
        _overview.IsEnabled = AiService.Enabled;

        foreach (var r in new[] { _all, _uncategorised, _notChecked }) r.Checked += (_, _) => UpdateCount();
        _readText.Checked += (_, _) => UpdateCount();
        _readText.Unchecked += (_, _) => UpdateCount();
        UpdateCount();
    }

    private void Heading(string text) => Fields.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });

    private RadioButton Radio(string text, bool on)
    {
        var r = new RadioButton { Content = text, GroupName = "scope", IsChecked = on, Margin = new Thickness(0, 3, 0, 0) };
        Fields.Children.Add(r);
        return r;
    }

    private CheckBox Check(string text, bool on)
    {
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(c, text);
        Fields.Children.Add(c);
        return c;
    }

    private List<CaseDocument> Chosen()
    {
        IEnumerable<CaseDocument> q = _docs;
        if (_uncategorised.IsChecked == true) q = q.Where(d => d.CategoryId == _unknown);
        else if (_notChecked.IsChecked == true) q = q.Where(d => !_checkedBefore.Contains(d.Id));
        if (_readText.IsChecked != true) q = q.Where(d => d.OcrStatus is not (OcrStatus.Pending or OcrStatus.Running));
        return q.ToList();
    }

    private void UpdateCount()
    {
        if (_scopeCount == null) return;
        int n = Chosen().Count;
        _scopeCount.Text = n == 0 ? "No documents match." : $"{n} document(s) will be checked.";
    }

    protected override bool Validate()
    {
        Documents = Chosen();
        if (Documents.Count == 0) { MessageBox.Show(this, "No documents match what you chose.", Title); return false; }
        var sections = _sections.Where(k => k.Value.IsChecked == true).Select(k => k.Key).ToList();
        if (sections.Count == 0) { MessageBox.Show(this, "Choose at least one thing to fill in.", Title); return false; }
        Db.SetSetting(LibraryCheck.SectionsSetting, string.Join(",", sections));
        Db.SetSetting(LibraryCheck.OverviewSetting + ".want", _overview.IsChecked == true ? "1" : "0");
        Db.SetSetting(TrackAutoAdd.Setting, _autoAdd.IsChecked == true ? "1" : "0");
        if (_autoCheck.IsEnabled) Db.SetSetting(AutoCheckSetting, _autoCheck.IsChecked == true ? "1" : "0");
        Options = new BulkOptions
        {
            LibraryCheck = true, RunAi = _useAi.IsChecked == true, RunOcr = _readText.IsChecked == true,
            Sections = sections.Count == LibraryCheck.Sections.Length ? null : sections,
            WriteOverview = _overview.IsChecked == true, Remember = false, AutoAdd = _autoAdd.IsChecked == true
        };
        return true;
    }
}
