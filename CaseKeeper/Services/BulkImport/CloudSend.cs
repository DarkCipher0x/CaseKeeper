using System.Text.Json.Nodes;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>An AI that runs on someone else's computers (Anthropic, OpenAI, Microsoft Foundry): documents leave this PC.</summary>
public interface ICloudAiClient : IAiJsonClient
{
    /// <summary>For example "anthropic".</summary>
    string ProviderId { get; }
    /// <summary>For example "Anthropic (Claude)".</summary>
    string ProviderName { get; }
    /// <summary>The host requests go to, for example api.anthropic.com.</summary>
    string Host { get; }
    /// <summary>The masker used for this run, or null when masking is off.</summary>
    Masker Masker { get; }
}

/// <summary>One document as the pre-send review sees it.</summary>
public sealed class CloudSendItem
{
    public ImportFile File { get; init; }
    public CaseDocument Document { get; init; }
    public int Pages { get; init; }
    public int Characters { get; init; }
    /// <summary>Kinds of personal detail found (and masked, when masking is on), with how many.</summary>
    public Dictionary<string, int> Found { get; init; } = new();
    /// <summary>Sensitive subjects spotted by keyword: health, children, money, criminal matters…</summary>
    public List<string> Topics { get; init; } = new();
    /// <summary>Masking may have missed names, or the text is too garbled to mask reliably.</summary>
    public bool PoorlyMasked { get; init; }
    public string PoorReason { get; init; } = "";
}

/// <summary>What the person decided after the pre-send review.</summary>
public sealed class CloudSendDecision
{
    public bool Proceed { get; init; }
    /// <summary>What to do with the poorly masked documents: "send", "local" (use the local AI) or "skip".</summary>
    public string PoorlyMasked { get; init; } = "send";
    public static readonly CloudSendDecision Cancel = new() { Proceed = false };
}

/// <summary>Everything the pre-send review shows.</summary>
public sealed class CloudSendRequest
{
    public string ProviderId { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Host { get; init; } = "";
    public string Model { get; init; } = "";
    public bool Masking { get; init; }
    public List<CloudSendItem> Items { get; init; } = new();
    public long EstimatedTokens => Items.Sum(i => (long)i.Characters) / 4 + Items.Count * 1500L;
}

/// <summary>Builds the pre-send review from the documents about to be read: what's in them, and how well they mask.</summary>
public static class CloudSendReview
{
    private static readonly (string Topic, string[] Words)[] Topics =
    {
        ("health or medical", new[] { "diagnos", "medication", "mental health", "hospital", "gp ", "psychiatr", "therap", "nhs number", "prescri" }),
        ("children", new[] { "child", "school", "nursery", "cafcass", "son ", "daughter" }),
        ("money", new[] { "bank", "account", "salary", "benefit", "universal credit", "mortgage", "debt", "payslip" }),
        ("police or criminal", new[] { "police", "arrest", "crime number", "conviction", "caution", "bail", "probation" }),
        ("abuse or safeguarding", new[] { "abuse", "safeguarding", "violence", "assault", "harass", "coercive", "non-molestation" })
    };

    public static CloudSendItem Assess(ImportFile f, CaseDocument doc, IReadOnlyDictionary<int, string> pages, Masker masker)
    {
        var text = string.Join("\n", pages.OrderBy(p => p.Key).Select(p => p.Value));
        var found = new Dictionary<string, int>();
        string masked = text;
        if (masker != null)
        {
            // A masker of its own, so the counts are for this document only (the run's masker gives the same placeholders)
            var probe = masker.CloneKnown();
            masked = probe.Mask(text);
            foreach (var (k, v) in probe.Counts) found[k] = v;
        }
        var lower = text.ToLowerInvariant();
        var topics = Topics.Where(t => t.Words.Any(w => lower.Contains(w, StringComparison.Ordinal))).Select(t => t.Topic).ToList();
        var a = Masker.Assess(masked);
        return new CloudSendItem
        {
            File = f, Document = doc, Pages = pages.Count, Characters = text.Length, Found = found, Topics = topics,
            PoorlyMasked = masker != null && a.Poor,
            PoorReason = !a.Poor ? "" : a.LowQualityText ? "the text looks like a poor scan" : $"{a.LikelyNamesLeft} possible names weren't recognised"
        };
    }
}
