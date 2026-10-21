using Microsoft.Win32;

namespace CaseKeeper.Services;

/// <summary>How capable this PC is of running a local AI model, from basic laptop to big graphics card.</summary>
public enum AiTier
{
    /// <summary>No usable graphics card and under 8 GB of memory: AI off by default.</summary>
    Minimal = 0,
    /// <summary>A basic PC or laptop, or any laptop on battery: small model, one document at a time, categories only.</summary>
    Basic = 1,
    /// <summary>A graphics card with about 6–10 GB.</summary>
    Mid = 2,
    /// <summary>A graphics card with 12 GB or more.</summary>
    High = 3,
    /// <summary>A graphics card with 24 GB or more.</summary>
    VeryHigh = 4
}

/// <summary>What CaseKeeper recommends for Bulk Case Import on this PC.</summary>
public sealed class AiRecommendation
{
    public AiTier Tier { get; init; }
    /// <summary>One line for Settings, e.g. "This PC: graphics card with 16 GB – fast AI settings".</summary>
    public string Summary { get; init; } = "";
    public bool AiRecommended { get; init; }
    /// <summary>Documents the AI reads at once.</summary>
    public int Workers { get; init; }
    /// <summary>"full" (categories, dates and records) or "categories" (much quicker on modest hardware).</summary>
    public string Depth { get; init; } = "full";
    /// <summary>Installed model used for the "Fast" preset, or null if none suitable is installed.</summary>
    public string FastModel { get; init; }
    /// <summary>Installed model used for the "Careful" preset, or null if none suitable fits this PC.</summary>
    public string CarefulModel { get; init; }
    /// <summary>Models installed but too large to run sensibly here.</summary>
    public List<string> TooLarge { get; init; } = new();
    public string Note { get; init; } = "";
}

/// <summary>
/// Looks at the graphics card, memory and power source and recommends AI settings that this PC can
/// actually cope with. Recommendations only: anything the person sets themselves always wins.
/// </summary>
public static class AiHardware
{
    /// <summary>The fast default model, built into CaseKeeper's recommendations.</summary>
    public const string DefaultModel = "gemma3:4b";

    // In order of preference. Fast: quick and reliable at structured answers. Careful: larger, slower, more accurate.
    private static readonly string[] FastModels = { "gemma3:4b", "qwen3:4b", "llama3.2:3b", "qwen2.5:3b", "phi4-mini", "gemma3:1b" };
    private static readonly string[] CarefulModels = { "qwen3:14b", "qwen2.5:14b", "gemma3:12b", "qwen3:8b", "qwen2.5:7b", "llama3.1:8b" };
    private static readonly string[] VeryLargeCarefulModels = { "qwen3:32b", "gemma3:27b", "qwen2.5:32b" };

    private static (string Name, double Gb)? _gpu;

    /// <summary>The graphics card with the most dedicated memory, read from the driver's registry entry (WMI caps it at 4 GB).</summary>
    public static (string Name, double Gb) Gpu => _gpu ??= ReadGpu();

    private static (string, double) ReadGpu()
    {
        string best = "";
        double bestGb = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return ("", 0);
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k == null) continue;
                    double gb = 0;
                    var q = k.GetValue("HardwareInformation.qwMemorySize");
                    if (q is long l) gb = l / 1024d / 1024d / 1024d;
                    else if (q is byte[] qb && qb.Length >= 8) gb = BitConverter.ToInt64(qb, 0) / 1024d / 1024d / 1024d;
                    if (gb == 0)
                    {
                        var m = k.GetValue("HardwareInformation.MemorySize");
                        if (m is int i) gb = (uint)i / 1024d / 1024d / 1024d;
                        else if (m is byte[] mb && mb.Length >= 4) gb = BitConverter.ToUInt32(mb, 0) / 1024d / 1024d / 1024d;
                    }
                    if (gb > bestGb)
                    {
                        bestGb = gb;
                        best = k.GetValue("DriverDesc") as string ?? "";
                    }
                }
                catch { /* an adapter we can't read */ }
            }
        }
        catch (Exception ex) { AppLog.Warn("AI", "Couldn't read the graphics card details", ex); }
        return (best, Math.Round(bestGb, 1));
    }

    /// <summary>This PC as the recommendations see it: graphics memory, system memory and whether it's on battery or power saver.</summary>
    public readonly record struct Machine(double GpuGb, double RamGb, bool Gentle);

    public static Machine ThisPc => new(Gpu.Gb, Performance.TotalMemoryGb,
        Performance.OnBattery || Performance.Effective == PerformanceMode.PowerSaver);

    public static AiTier Tier => TierFor(ThisPc);

    public static AiTier TierFor(Machine pc)
    {
        if (pc.Gentle) return pc.GpuGb < 2 && pc.RamGb < 8 ? AiTier.Minimal : AiTier.Basic;
        if (pc.GpuGb >= 23) return AiTier.VeryHigh;
        if (pc.GpuGb >= 11.5) return AiTier.High;
        if (pc.GpuGb >= 5.5) return AiTier.Mid;
        return pc.RamGb < 8 ? AiTier.Minimal : AiTier.Basic;
    }

    /// <summary>
    /// Whether a model of this size can run here: on the graphics card, slowly on the processor, or not sensibly at all.
    /// Size is the download size reported by Ollama, which is close to what it needs in memory.
    /// </summary>
    public static string Fit(long sizeBytes) => Fit(sizeBytes, ThisPc);

    public static string Fit(long sizeBytes, Machine pc)
    {
        double gb = sizeBytes / 1024d / 1024d / 1024d;
        if (pc.GpuGb >= 2 && gb <= pc.GpuGb * 0.85) return "gpu";
        if (gb <= pc.RamGb * 0.45) return "cpu";
        return "too large";
    }

    private static string Pick(IEnumerable<string> preferred, IReadOnlyList<(string Name, long Size)> installed, Func<long, bool> fits) =>
        preferred.Select(p => installed.FirstOrDefault(m => Same(m.Name, p)))
            .FirstOrDefault(m => m.Name != null && fits(m.Size)).Name;

    public static bool Same(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string m) => m == null ? "" : m.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? m[..^7] : m;

    /// <summary>Recommended settings for this PC, given the models installed in Ollama.</summary>
    public static AiRecommendation Recommend(IReadOnlyList<(string Name, long Size)> installed) => Recommend(installed, ThisPc);

    public static AiRecommendation Recommend(IReadOnlyList<(string Name, long Size)> installed, Machine pc)
    {
        installed ??= Array.Empty<(string, long)>();
        var tier = TierFor(pc);
        bool OnGpu(long s) => Fit(s, pc) == "gpu";
        bool Usable(long s) => Fit(s, pc) != "too large";
        var tooLarge = installed.Where(m => !Usable(m.Size)).Select(m => m.Name).ToList();

        string fast = Pick(FastModels, installed, tier >= AiTier.Mid ? OnGpu : Usable)
                      ?? installed.Where(m => Usable(m.Size)).OrderBy(m => m.Size).Select(m => m.Name).FirstOrDefault();
        string careful = tier >= AiTier.High
            ? Pick(tier >= AiTier.VeryHigh ? VeryLargeCarefulModels.Concat(CarefulModels) : CarefulModels, installed, OnGpu)
            : tier == AiTier.Mid ? Pick(CarefulModels.Skip(3), installed, OnGpu) : null;

        var gpu = pc.GpuGb >= 2 ? $"graphics card with {pc.GpuGb:0} GB" : "no graphics card the AI can use";
        var power = pc.Gentle ? " · on battery or power saver" : "";
        var (summary, workers, depth, recommended) = tier switch
        {
            AiTier.Minimal => ($"This PC: {gpu}, {pc.RamGb:0} GB memory{power} – AI off is recommended", 1, "categories", false),
            AiTier.Basic => ($"This PC: {gpu}{power} – gentle AI settings (one document at a time, categories only)", 1, "categories", true),
            AiTier.Mid => ($"This PC: {gpu} – balanced AI settings (two documents at once)", 2, "full", true),
            AiTier.High => ($"This PC: {gpu} – fast AI settings (three documents at once)", 3, "full", true),
            _ => ($"This PC: {gpu} – fast AI settings (four documents at once)", 4, "full", true)
        };
        var note = tier == AiTier.Minimal
            ? "Local AI would be very slow here. CaseKeeper's own rules still suggest categories from form codes and titles."
            : fast == null ? $"No suitable model is installed. Install the recommended one with the Download button, or by running: ollama pull {DefaultModel}"
            : workers > 1 ? "Reading several documents at once only helps if Ollama allows it: set the Windows environment variable OLLAMA_NUM_PARALLEL to 4, then restart Ollama."
            : "";
        return new AiRecommendation
        {
            Tier = tier, Summary = summary, AiRecommended = recommended, Workers = workers, Depth = depth,
            FastModel = fast, CarefulModel = careful, TooLarge = tooLarge, Note = note
        };
    }

    /// <summary>The next lighter model to fall back to when <paramref name="current"/> is too much for this PC, or null.</summary>
    public static string LighterThan(string current, IReadOnlyList<(string Name, long Size)> installed, Machine pc)
    {
        var size = installed.FirstOrDefault(m => Same(m.Name, current)).Size;
        if (size <= 0) return null;
        return FastModels.Select(p => installed.FirstOrDefault(m => Same(m.Name, p)))
            .Where(m => m.Name != null && m.Size < size * 0.8 && Fit(m.Size, pc) != "too large")
            .Select(m => m.Name).FirstOrDefault();
    }
    /// <summary>Models that think aloud before answering. Their thinking is switched off for bulk analysis, which needs only the answer.</summary>
    public static object ThinkSetting(string model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.StartsWith("gpt-oss")) return "low";          // can't be switched off entirely, only turned down
        if (m.StartsWith("qwen3") || m.StartsWith("deepseek-r1") || m.StartsWith("magistral") || m.StartsWith("qwq")) return false;
        return null;
    }
}
