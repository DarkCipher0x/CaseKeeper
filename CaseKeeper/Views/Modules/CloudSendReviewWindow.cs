using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;

namespace CaseKeeper.Views.Modules;

/// <summary>
/// Shown before a cloud AI reads anything in Bulk Case Import: exactly how much is going, where to, whether personal
/// details are masked, what sensitive material is in the batch, and which documents couldn't be masked well (the person
/// decides what happens to those). Nothing is sent unless the person clicks Send.
/// </summary>
public class CloudSendReviewWindow : FormDialog
{
    private static Data.Database Db => App.Library.Db;
    private readonly CloudSendRequest _req;
    private readonly RadioButton _send, _local, _skip;
    private readonly CheckBox _remember;

    public string PoorChoice { get; private set; } = "send";

    private CloudSendReviewWindow(CloudSendRequest req) : base("Before anything is sent", 680, 720, $"Send to {req.Provider}")
    {
        _req = req;
        int docs = req.Items.Count, pages = req.Items.Sum(i => i.Pages);
        var poor = req.Items.Where(i => i.PoorlyMasked).ToList();

        var lead = new TextBlock
        {
            Text = $"{docs} document{(docs == 1 ? "" : "s")} ({pages:N0} page{(pages == 1 ? "" : "s")}) will be sent to {req.Provider} to be read by the AI model {req.Model.Split('/').Last()}.",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0)
        };
        AutomationProperties.SetLiveSetting(lead, AutomationLiveSetting.Polite);
        Fields.Children.Add(lead);

        Section("WHERE IT GOES");
        Line($"Address: {req.Host} (encrypted connection, HTTPS). It leaves this PC.");
        Line(req.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft Foundry: it's processed in your own Azure subscription, in the region your resource is in, under your Azure terms."
            : $"{req.Provider} handles it under its API terms, which can include keeping requests for a limited time for safety checks. It isn't used to train models under those terms, but check them if that matters to you.");
        Line($"Roughly {req.EstimatedTokens:N0} tokens of text in total. {req.Provider} charges your account for this.");
        if (App.Library.Db.GetSetting(Services.BulkImport.CaseMemory.UseSetting, "1") != "0")
            Line("With each document, a short background from your case memory is sent too (people's names and roles, case details, hearings, orders, " +
                 "complaints and your notes for the AI – never contact details), masked the same way, so the AI can link to what's already known. " +
                 "You can switch this off on the Case memory screen.");

        Section("PERSONAL DETAILS");
        if (req.Masking)
        {
            var totals = new Dictionary<string, int>();
            foreach (var i in req.Items) foreach (var (k, v) in i.Found) totals[k] = totals.GetValueOrDefault(k) + v;
            Line(totals.Count == 0
                ? "Masking is on. CaseKeeper didn't recognise any names, contact details or numbers to replace."
                : "Masking is on. These are replaced with placeholders such as [PERSON-1] before sending, and put back in the answers here: " +
                  string.Join(", ", totals.OrderByDescending(t => t.Value).Select(t => $"{t.Value:N0} {Label(t.Key)}")) + ".");
            Line("Masking is best effort. Names CaseKeeper doesn't know (people not in your list of parties, and without Mr, Ms or similar) can be missed. Add people to Parties to make it more reliable.");
        }
        else Warn("Masking is OFF: names, addresses, phone numbers and other details are sent as they are. Turn it on in Settings → AI.");

        var topics = req.Items.SelectMany(i => i.Topics).GroupBy(t => t).OrderByDescending(g => g.Count()).ToList();
        if (topics.Count > 0)
            Line("Sensitive subjects in this batch: " + string.Join(", ", topics.Select(g => $"{g.Key} ({g.Count()} document{(g.Count() == 1 ? "" : "s")})")) + ".");

        if (poor.Count > 0)
        {
            Section($"{poor.Count} DOCUMENT{(poor.Count == 1 ? "" : "S")} COULDN'T BE MASKED WELL");
            var list = new ListBox { MaxHeight = 150, Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetName(list, "Documents that couldn't be masked well");
            foreach (var i in poor) list.Items.Add($"{i.Document.Title} – {i.PoorReason}");
            Fields.Children.Add(list);
            var group = "poor";
            _local = new RadioButton { GroupName = group, Content = "Read these with the local AI on this PC instead (they aren't sent)", Margin = new Thickness(0, 2, 0, 2) };
            _skip = new RadioButton { GroupName = group, Content = "Skip them for now (they wait in Import review)", Margin = new Thickness(0, 2, 0, 2) };
            _send = new RadioButton { GroupName = group, Content = "Send them anyway", Margin = new Thickness(0, 2, 0, 2) };
            Fields.Children.Add(_local);
            Fields.Children.Add(_skip);
            Fields.Children.Add(_send);
            bool localReady = AiService.Enabled && !string.IsNullOrWhiteSpace(AiService.Model);
            (localReady ? _local : _skip).IsChecked = true;
            if (!localReady) _local.Content += " – set up the local AI in Settings → AI first";
            _local.IsEnabled = localReady;
        }

        _remember = new CheckBox
        {
            Content = $"Don't show this again for {req.Provider} (reset it in Settings → AI)",
            Margin = new Thickness(0, 14, 0, 0)
        };
        Fields.Children.Add(_remember);
        AddHint("CaseKeeper records in the audit trail that you agreed, where to, and how many documents. Never their content.");
    }

    private void Section(string text) => Fields.Children.Add(new TextBlock { Text = text, Style = Ui.Style("Label"), Margin = new Thickness(0, 16, 0, 2) });
    private void Line(string text) => Fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0) });
    private void Warn(string text)
    {
        var t = new TextBlock { Text = "⚠ " + text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        Fields.Children.Add(t);
    }

    private static string Label(string tag) => tag switch
    {
        "PERSON" => "names", "EMAIL" => "e-mail addresses", "PHONE" => "phone numbers", "POSTCODE" => "postcodes", "NINO" => "National Insurance numbers",
        "CASE" => "case numbers", "ACCOUNT" => "bank or card details", "DOB" => "dates of birth", "ADDRESS" => "street addresses", _ => tag.ToLowerInvariant()
    };

    protected override bool Validate()
    {
        PoorChoice = _local?.IsChecked == true ? "local" : _skip?.IsChecked == true ? "skip" : "send";
        return true;
    }

    /// <summary>Asks the person (or uses the answer they told CaseKeeper to remember for this provider).</summary>
    public static CloudSendDecision Ask(Window owner, CloudSendRequest req)
    {
        var rememberKey = $"bulk.cloudReview.skip.{req.ProviderId}";
        var remembered = Db.GetSetting(rememberKey, "") ?? "";
        if (remembered is "send" or "local" or "skip") return new CloudSendDecision { Proceed = true, PoorlyMasked = remembered };
        var w = new CloudSendReviewWindow(req) { Owner = owner };
        if (w.ShowDialog() != true) return CloudSendDecision.Cancel;
        if (w._remember.IsChecked == true)
        {
            Db.SetSetting(rememberKey, w.PoorChoice);
            Db.Audit("settings.change", "settings", null, $"Bulk import will no longer ask before sending to {req.Provider}", $"Poorly masked documents: {w.PoorChoice}.");
        }
        return new CloudSendDecision { Proceed = true, PoorlyMasked = w.PoorChoice };
    }
}
