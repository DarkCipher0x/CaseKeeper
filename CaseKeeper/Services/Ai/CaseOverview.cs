using System.Globalization;
using System.Text;
using CaseKeeper.Data;
using CaseKeeper.Models;
using CaseKeeper.Services.BulkImport;

namespace CaseKeeper.Services;

/// <summary>
/// The case overview: an AI-written summary of the whole case, written from each document's short summary (from its
/// analysis, or its description) and the records already approved – never the documents' full text. Saved as an
/// AI report and shown on the Case map. The usual AI consent and masking apply.
/// </summary>
public static class CaseOverview
{
    // ------------------------------------------------------------------ case overview
    public const string ReportType = "Case overview";

    /// <summary>
    /// What the overview is written from: each document's one-paragraph summary (from its analysis, or its description),
    /// and the records already approved. Never the documents' full text.
    /// </summary>
    public static List<AiService.SourceText> Sources(Database db)
    {
        var sources = new List<AiService.SourceText>();
        var categories = db.GetCategories().ToDictionary(c => c.Id, c => c.DisplayName);
        var summaries = db.Query<AiFinding>("Kind=$k AND Status<>$s", ("$k", FindingKinds.Summary), ("$s", (int)FindingStatus.Superseded))
            .GroupBy(f => f.DocumentId).ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.CreatedAt).First());
        var lines = new StringBuilder();
        foreach (var d in db.GetDocuments().OrderBy(d => d.DocumentDate ?? d.DateReceived ?? d.ImportedAt))
        {
            var summary = summaries.TryGetValue(d.Id, out var f) && !string.IsNullOrWhiteSpace(f.Detail) ? f.Detail : d.Description;
            var when = d.DocumentDate is { } dd ? dd.ToString("d MMM yyyy") : "undated";
            lines.AppendLine($"- {d.Title} [{categories.GetValueOrDefault(d.CategoryId, "Uncategorised")}, {when}]" +
                             (string.IsNullOrWhiteSpace(summary) ? "" : ": " + Clip(summary, 500)));
        }
        sources.Add(new AiService.SourceText { Label = "Documents in the library (one line each)", Text = lines.ToString() });

        var records = new StringBuilder();
        foreach (var p in db.GetParties()) records.AppendLine($"- Party: {p.Role}{(string.IsNullOrWhiteSpace(p.Name) ? "" : " – " + p.Name)}{(p.IsMe ? " (the person using this app)" : "")}");
        foreach (var h in db.All<Hearing>().OrderBy(h => h.HearingDate)) records.AppendLine($"- Hearing: {h.HearingDate:d MMM yyyy} {h.HearingType} ({h.Status}){(string.IsNullOrWhiteSpace(h.Court) ? "" : " at " + h.Court)}");
        foreach (var o in db.All<CourtOrder>().OrderBy(o => o.OrderDate)) records.AppendLine($"- Order: {o.OrderDate:d MMM yyyy} {o.OrderType}{(string.IsNullOrWhiteSpace(o.MadeBy) ? "" : ", " + o.MadeBy)}");
        foreach (var a in db.All<CaseAction>().Where(a => a.Status is not ("Done" or "Complete" or "Completed" or "Cancelled")).OrderBy(a => a.DueDate))
            records.AppendLine($"- Outstanding: {a.Title}{(a.DueDate is { } due ? $" (due {due:d MMM yyyy})" : "")}, {a.Responsible}");
        foreach (var c in db.GetComplaints()) records.AppendLine($"- Complaint to {c.Recipient}: {c.Subject} ({c.Status}{(c.ResponseDue is { } r ? $", reply due {r:d MMM yyyy}" : "")})");
        foreach (var s in db.All<ScottItem>().OrderBy(s => s.ItemNo)) records.AppendLine($"- Scott item {s.ItemNo} ({s.AllegedBy}, {s.WhenText}): {Clip(s.Allegation, 200)} [{s.Status}]");
        if (records.Length > 0) sources.Add(new AiService.SourceText { Label = "Records in the case", Text = records.ToString() });
        return sources;
    }

    public static string Prompt => AiService.ReportPrompt("overview");

    /// <summary>Writes the case overview with the AI chosen in Settings → AI and saves it to AI → Reports.</summary>
    public static async Task<AiReport> WriteAsync(Database db, IProgress<string> status, CancellationToken ct)
    {
        var sources = Sources(db);
        var result = await AiService.AnalyseAsync(Prompt, sources, granular: false, status, null, ct);
        var report = new AiReport
        {
            Title = $"{ReportType} – {DateTime.Now:d MMM yyyy}", ReportType = ReportType, Scope = "Whole library (document summaries and records)",
            Model = AiService.ActiveModel, Endpoint = AiService.ActiveHost,
            Output = "> AI-generated overview from the documents' summaries and your records – check it against the documents. Not legal advice.\n\n" + result.Output,
            DurationMs = result.DurationMs, ChunkCount = result.Chunks
        };
        db.Save(report);
        db.SetSetting(LibraryCheck.OverviewSetting, report.Id.ToString(CultureInfo.InvariantCulture));
        db.Audit("ai.overview", "ai", report.Id, "Wrote a case overview", $"From {sources.Sum(s => s.Text.Length):N0} characters of summaries and records ({AiService.ActiveModel}).");
        return report;
    }

    /// <summary>The newest case overview, or null.</summary>
    public static AiReport Latest(Database db) =>
        db.All<AiReport>().Where(r => r.ReportType == ReportType).OrderByDescending(r => r.CreatedAt).FirstOrDefault();

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
