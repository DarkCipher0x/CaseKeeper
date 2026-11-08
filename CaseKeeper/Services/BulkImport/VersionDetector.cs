using System.Text;
using System.Text.RegularExpressions;

namespace CaseKeeper.Services.BulkImport;

/// <summary>One document as the version check sees it: its readable text and the dates that say how new it is.</summary>
/// <param name="SavedAt">When the file itself says it was last saved (PDF/Office/photo metadata).</param>
/// <param name="Revision">The Office revision number, when there is one.</param>
/// <param name="AddedAt">When it was added to CaseKeeper (shown in the edit history only, never used to rank).</param>
public sealed record VersionInput(long DocumentId, string Title, string Text, DateTime? DocumentDate, DateTime? FileModified, string FileHash,
    DateTime? SavedAt = null, int? Revision = null, DateTime? AddedAt = null);

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

    // Edit history, for the review card: each piece of evidence about when this copy was last edited
    public DateTime? DocumentDate { get; init; }
    public DateTime? SavedAt { get; init; }
    public int? Revision { get; init; }
    public DateTime? FileModified { get; init; }
    public DateTime? AddedAt { get; init; }

    /// <summary>"dated 3 Oct 2026 · last saved 4 Oct 2026 14:02 · revision 7 · file changed … · added …"</summary>
    public string History
    {
        get
        {
            var parts = new List<string>();
            if (DocumentDate is { } d) parts.Add($"dated {d:d MMM yyyy}");
            if (SavedAt is { } s) parts.Add($"last saved {s:d MMM yyyy HH:mm}");
            if (Revision is { } r) parts.Add($"revision {r}");
            if (FileModified is { } f) parts.Add($"file changed {f:d MMM yyyy HH:mm}");
            if (AddedAt is { } a && a > DateTime.MinValue) parts.Add($"added {a:d MMM yyyy}");
            return string.Join(" · ", parts);
        }
    }
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
            // Latest edit: the document's own date, then when the file says it was last saved, then its Office revision,
            // then when the file was last changed, then whichever has more in it
            var ranked = idx.Select(i => (i, When(docs[i])))
                .OrderByDescending(t => docs[t.i].DocumentDate?.Date ?? DateTime.MinValue)
                .ThenByDescending(t => docs[t.i].SavedAt ?? DateTime.MinValue)
                .ThenByDescending(t => docs[t.i].Revision ?? -1)
                .ThenByDescending(t => docs[t.i].FileModified ?? DateTime.MinValue)
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
                Diff = t.i == newest || kind == VersionKind.Exact ? null : Diff(docs[t.i].Text, docs[newest].Text),
                DocumentDate = docs[t.i].DocumentDate, SavedAt = docs[t.i].SavedAt, Revision = docs[t.i].Revision,
                FileModified = docs[t.i].FileModified, AddedAt = docs[t.i].AddedAt
            }).ToList();
            groups.Add(new VersionGroup { Kind = kind, Members = members, NewestReason = Reason(ranked.Select(t => docs[t.i]).ToList(), ranked.Select(t => sets[t.i].Count).ToList(), kind) });
        }
        return groups.OrderBy(g => g.Kind == VersionKind.Versions ? 0 : g.Kind == VersionKind.SameContent ? 1 : 2)
            .ThenByDescending(g => g.Members.Count).ToList();
    }

    private static (DateTime? Date, string Source) When(VersionInput d) =>
        d.DocumentDate is { } dd ? (dd, "dated") : d.SavedAt is { } sv ? (sv, "last saved") : d.FileModified is { } fm ? (fm, "file last changed") : (null, "");

    /// <summary>Says which piece of evidence put the newest first, comparing it with the runner-up.</summary>
    public static string Reason(IReadOnlyList<VersionInput> ranked, IReadOnlyList<int> sizes, VersionKind kind)
    {
        if (kind == VersionKind.Exact) return "Identical files: keep any one.";
        var a = ranked[0];
        var b = ranked.Count > 1 ? ranked[1] : null;
        if (b == null) return "";
        if (a.DocumentDate is { } da && b.DocumentDate is { } db && da.Date != db.Date)
            return $"Newest: dated {da:d MMM yyyy}, the next is dated {db:d MMM yyyy}.";
        if (a.DocumentDate is { } only && b.DocumentDate == null && a.SavedAt == null)
            return $"Newest: dated {only:d MMM yyyy}; the others have no date.";
        var sameDay = a.DocumentDate is { } d1 && b.DocumentDate is { } d2 && d1.Date == d2.Date ? $"Both are dated {d1:d MMM yyyy}. " : "";
        if (a.SavedAt is { } sa && b.SavedAt is { } sb && sa != sb)
            return $"{sameDay}Newest: last saved {sa:d MMM yyyy HH:mm} (inside the file); the next was saved {sb:d MMM yyyy HH:mm}.";
        if (a.SavedAt is { } sOnly && b.SavedAt == null)
            return $"{sameDay}Newest: last saved {sOnly:d MMM yyyy HH:mm} (inside the file); the others don't say when they were saved.";
        if (a.Revision is { } ra && b.Revision is { } rb && ra != rb)
            return $"{sameDay}Newest: revision {ra} of the file, the next is revision {rb}.";
        if (a.FileModified is { } fa && b.FileModified is { } fb && fa != fb)
            return $"{sameDay}Newest: the file was last changed {fa:d MMM yyyy HH:mm}, the next {fb:d MMM yyyy HH:mm}. " +
                   "A file's \"changed\" time can move when it's copied, so check before relying on it.";
        return sizes[0] > (sizes.Count > 1 ? sizes[1] : 0)
            ? $"{sameDay}Nothing in the dates tells them apart, so the longest is taken as the latest. Check before relying on it."
            : $"{sameDay}Nothing in the dates tells them apart. Check which is the latest yourself.";
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
