using System.Text;
using System.Text.RegularExpressions;

namespace CaseKeeper.Services.BulkImport;

/// <summary>One document as the version check sees it: its readable text and the dates that say how new it is.</summary>
public sealed record VersionInput(long DocumentId, string Title, string Text, DateTime? DocumentDate, DateTime? FileModified, string FileHash);

public enum VersionKind
{
    /// <summary>Byte-for-byte the same file.</summary>
    Exact,
    /// <summary>The same words in different files (for example a letter saved as PDF and as Word).</summary>
    SameContent,
    /// <summary>Mostly the same, with edits: drafts and later versions of one document.</summary>
    Versions
}

/// <summary>How the text of one document differs from the newest in its group.</summary>
public sealed record DiffSummary(int Added, int Removed, int Unchanged, IReadOnlyList<string> AddedSamples, IReadOnlyList<string> RemovedSamples)
{
    public bool Identical => Added == 0 && Removed == 0;
    public string Describe(string newer = "the newest") => Identical
        ? "Same wording"
        : $"{newer} has {Added} new or changed sentence{(Added == 1 ? "" : "s")}, and {Removed} from this one {(Removed == 1 ? "is" : "are")} gone or changed";
}

public sealed class VersionMember
{
    public long DocumentId { get; init; }
    public string Title { get; init; } = "";
    public bool Newest { get; init; }
    /// <summary>How alike it is to the newest (0 to 1, shared wording).</summary>
    public double Similarity { get; init; }
    public DateTime? EffectiveDate { get; init; }
    public string DateSource { get; init; } = "";
    /// <summary>Compared with the newest in the group; null for the newest itself.</summary>
    public DiffSummary Diff { get; init; }
}

public sealed class VersionGroup
{
    public VersionKind Kind { get; init; }
    /// <summary>Newest first.</summary>
    public List<VersionMember> Members { get; init; } = new();
    /// <summary>Why the first member counts as the newest, in plain words.</summary>
    public string NewestReason { get; init; } = "";
    /// <summary>Stable for the same set of documents, so a decision about the group can be remembered.</summary>
    public string Key => AnalysisSchema.Hash(string.Join(",", Members.Select(m => m.DocumentId).OrderBy(i => i)))[..16];
    public string KindText => Kind switch { VersionKind.Exact => "Exact copies", VersionKind.SameContent => "Same content, different files", _ => "Versions of one document" };
}

/// <summary>
/// Finds documents that are copies or versions of each other by comparing their words (never their bytes or names):
/// overlapping five-word phrases, narrowed quickly with MinHash so hundreds of documents take moments, then measured
/// exactly. Within each group it says which is newest, why, and what changed.
/// </summary>
public static class VersionDetector
{
    public const double VersionThreshold = 0.70, SameThreshold = 0.97;
    private const int Shingle = 5, Hashes = 64, Bands = 16, Rows = Hashes / Bands, MinWords = 12;
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<VersionGroup> Find(IReadOnlyList<VersionInput> docs, CancellationToken ct = default)
    {
        var n = docs.Count;
        var sets = new HashSet<ulong>[n];
        var sigs = new ulong[n][];
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            sets[i] = Shingles(docs[i].Text);
            sigs[i] = sets[i].Count > 0 ? MinHash(sets[i]) : null;
        }

        var parent = Enumerable.Range(0, n).ToArray();
        int Root(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Join(int a, int b) { a = Root(a); b = Root(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
        var pairSim = new Dictionary<(int, int), double>();

        // Exact copies (same fingerprint) group whatever their text
        foreach (var g in Enumerable.Range(0, n).Where(i => !string.IsNullOrEmpty(docs[i].FileHash)).GroupBy(i => docs[i].FileHash).Where(g => g.Count() > 1))
        {
            var list = g.ToList();
            for (int k = 1; k < list.Count; k++) { Join(list[0], list[k]); pairSim[(Math.Min(list[0], list[k]), Math.Max(list[0], list[k]))] = 1.0; }
        }

        // Candidates: documents sharing at least one MinHash band
        var buckets = new Dictionary<(int Band, ulong Hash), List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (sigs[i] == null) continue;
            for (int b = 0; b < Bands; b++)
            {
                ulong h = 1469598103934665603UL;
                for (int r = 0; r < Rows; r++) h = (h ^ sigs[i][b * Rows + r]) * 1099511628211UL;
                var key = (b, h);
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<int>();
                l.Add(i);
            }
        }
        var checkedPairs = new HashSet<(int, int)>();
        foreach (var bucket in buckets.Values.Where(b => b.Count > 1))
        {
            ct.ThrowIfCancellationRequested();
            // A huge bucket is boilerplate (the same cover sheet on everything): too common to mean "versions"
            if (bucket.Count > 200) continue;
            for (int x = 0; x < bucket.Count; x++)
                for (int y = x + 1; y < bucket.Count; y++)
                {
                    var pair = (Math.Min(bucket[x], bucket[y]), Math.Max(bucket[x], bucket[y]));
                    if (!checkedPairs.Add(pair)) continue;
                    var (j, containment) = Similarity(sets[pair.Item1], sets[pair.Item2]);
                    double small = Math.Min(sets[pair.Item1].Count, sets[pair.Item2].Count), large = Math.Max(sets[pair.Item1].Count, sets[pair.Item2].Count);
                    // Versions: mostly the same wording, or one is the other with a lot added (a fuller later draft)
                    if (j >= VersionThreshold || (containment >= 0.9 && small / large >= 0.4))
                    {
                        Join(pair.Item1, pair.Item2);
                        pairSim[pair] = Math.Max(pairSim.GetValueOrDefault(pair), j);
                    }
                }
        }

        var groups = new List<VersionGroup>();
        foreach (var g in Enumerable.Range(0, n).GroupBy(Root).Where(g => g.Count() > 1))
        {
            var idx = g.ToList();
            var ranked = idx.Select(i => (i, When(docs[i]))).OrderByDescending(t => t.Item2.Date ?? DateTime.MinValue)
                .ThenByDescending(t => sets[t.i].Count).ThenByDescending(t => docs[t.i].DocumentId).ToList();
            var newest = ranked[0].i;
            bool allSameHash = idx.Select(i => docs[i].FileHash).Distinct().Count() == 1 && !string.IsNullOrEmpty(docs[newest].FileHash);
            var sims = idx.Where(i => i != newest).Select(i => Similarity(sets[newest], sets[i]).Jaccard).ToList();
            var kind = allSameHash ? VersionKind.Exact : sims.Count > 0 && sims.Min() >= SameThreshold ? VersionKind.SameContent : VersionKind.Versions;
            var members = ranked.Select(t => new VersionMember
            {
                DocumentId = docs[t.i].DocumentId, Title = docs[t.i].Title, Newest = t.i == newest,
                Similarity = t.i == newest ? 1 : Similarity(sets[newest], sets[t.i]).Jaccard,
                EffectiveDate = t.Item2.Date, DateSource = t.Item2.Source,
                Diff = t.i == newest || kind == VersionKind.Exact ? null : Diff(docs[t.i].Text, docs[newest].Text)
            }).ToList();
            groups.Add(new VersionGroup { Kind = kind, Members = members, NewestReason = Reason(ranked, sets, kind) });
        }
        return groups.OrderBy(g => g.Kind == VersionKind.Versions ? 0 : g.Kind == VersionKind.SameContent ? 1 : 2)
            .ThenByDescending(g => g.Members.Count).ToList();
    }

    private static (DateTime? Date, string Source) When(VersionInput d) =>
        d.DocumentDate is { } dd ? (dd, "dated") : d.FileModified is { } fm ? (fm, "file last changed") : (null, "");

    private static string Reason(List<(int i, (DateTime? Date, string Source) w)> ranked, HashSet<ulong>[] sets, VersionKind kind)
    {
        if (kind == VersionKind.Exact) return "Identical files: keep any one.";
        var a = ranked[0].w;
        var b = ranked.Count > 1 ? ranked[1].w : default;
        if (a.Date is { } da && b.Date is { } db && da.Date != db.Date)
            return a.Source == b.Source
                ? $"Newest: {a.Source} {da:d MMM yyyy}, the next is {db:d MMM yyyy}."
                : $"Newest: {a.Source} {da:d MMM yyyy} (the next one: {b.Source} {db:d MMM yyyy}).";
        if (a.Date is { } only && b.Date == null) return $"Newest: {a.Source} {only:d MMM yyyy}; the others have no date.";
        return sets[ranked[0].i].Count > (ranked.Count > 1 ? sets[ranked[1].i].Count : 0)
            ? "No dates tell them apart, so the longest is taken as the latest. Check before relying on it."
            : "No dates tell them apart. Check which is the latest yourself.";
    }

    // ------------------------------------------------------------------ similarity
    private static HashSet<ulong> Shingles(string text)
    {
        var words = Word.Matches(text ?? "").Select(m => m.Value.ToLowerInvariant()).ToList();
        var set = new HashSet<ulong>();
        if (words.Count < MinWords) return set;
        for (int i = 0; i + Shingle <= words.Count; i++)
        {
            ulong h = 1469598103934665603UL;
            for (int k = 0; k < Shingle; k++)
            {
                foreach (var ch in words[i + k]) h = (h ^ ch) * 1099511628211UL;
                h = (h ^ ' ') * 1099511628211UL;
            }
            set.Add(h);
        }
        return set;
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static ulong[] MinHash(HashSet<ulong> set)
    {
        var sig = new ulong[Hashes];
        Array.Fill(sig, ulong.MaxValue);
        foreach (var s in set)
            for (int k = 0; k < Hashes; k++)
            {
                var h = Mix(s ^ (0xA5A5A5A5UL * (ulong)(k + 1)));
                if (h < sig[k]) sig[k] = h;
            }
        return sig;
    }

    /// <summary>Jaccard (shared phrases over all phrases), and containment (shared over the smaller document's phrases).</summary>
    public static (double Jaccard, double Containment) Similarity(HashSet<ulong> a, HashSet<ulong> b)
    {
        if (a.Count == 0 || b.Count == 0) return (0, 0);
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        int shared = small.Count(large.Contains);
        return (shared / (double)(a.Count + b.Count - shared), shared / (double)small.Count);
    }

    /// <summary>For tests and the review screen: how alike two texts are (0 to 1).</summary>
    public static double TextSimilarity(string a, string b) => Similarity(Shingles(a), Shingles(b)).Jaccard;

    // ------------------------------------------------------------------ what changed
    private static readonly Regex SentenceBreak = new(@"(?<=[.!?])\s+|\n+", RegexOptions.Compiled);

    public static List<string> Sentences(string text) =>
        SentenceBreak.Split(text ?? "").Select(s => Regex.Replace(s, @"\s+", " ").Trim()).Where(s => s.Length > 0).ToList();

    public enum LineChange { Same, Added, Removed }

    /// <summary>Sentence by sentence: what the newer text keeps, adds and drops (longest common subsequence).</summary>
    public static List<(LineChange Change, string Text)> Compare(string older, string newer)
    {
        var a = Sentences(older);
        var b = Sentences(newer);
        var result = new List<(LineChange, string)>();
        // Very long documents: compare as sets instead (keeps it quick; the order of changes matters less there)
        if ((long)a.Count * b.Count > 4_000_000)
        {
            var sa = a.ToHashSet(StringComparer.Ordinal);
            var sb = b.ToHashSet(StringComparer.Ordinal);
            result.AddRange(b.Select(s => (sa.Contains(s) ? LineChange.Same : LineChange.Added, s)));
            result.AddRange(a.Where(s => !sb.Contains(s)).Select(s => (LineChange.Removed, s)));
            return result;
        }
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal)) { result.Add((LineChange.Same, b[y])); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) result.Add((LineChange.Removed, a[x++]));
            else result.Add((LineChange.Added, b[y++]));
        }
        while (x < a.Count) result.Add((LineChange.Removed, a[x++]));
        while (y < b.Count) result.Add((LineChange.Added, b[y++]));
        return result;
    }

    public static DiffSummary Diff(string older, string newer)
    {
        var c = Compare(older, newer);
        static string Clip(string s) => s.Length > 140 ? s[..140] + "…" : s;
        return new DiffSummary(c.Count(t => t.Change == LineChange.Added), c.Count(t => t.Change == LineChange.Removed), c.Count(t => t.Change == LineChange.Same),
            c.Where(t => t.Change == LineChange.Added).Take(3).Select(t => Clip(t.Text)).ToList(),
            c.Where(t => t.Change == LineChange.Removed).Take(3).Select(t => Clip(t.Text)).ToList());
    }
}
