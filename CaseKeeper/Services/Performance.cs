using System.Runtime.InteropServices;

namespace CaseKeeper.Services;

public enum PerformanceMode { Auto = 0, PowerSaver = 1, Balanced = 2, Maximum = 3 }

/// <summary>
/// Decides how hard CaseKeeper works the computer. On a desktop it can scan several documents at
/// once and hand the local AI every core; on a laptop running on battery it does one thing at a
/// time at lower priority so the fan stays quiet and the battery lasts.
/// </summary>
public static class Performance
{
    public static PerformanceMode Mode { get; set; } = PerformanceMode.Auto;

    public static int LogicalCores => Environment.ProcessorCount;

    /// <summary>The mode actually in force: Auto resolves to Power saver on battery, otherwise Balanced or Maximum.</summary>
    public static PerformanceMode Effective
    {
        get
        {
            if (Mode != PerformanceMode.Auto) return Mode;
            if (OnBattery) return PerformanceMode.PowerSaver;
            return LogicalCores >= 12 && TotalMemoryGb >= 16 ? PerformanceMode.Maximum : PerformanceMode.Balanced;
        }
    }

    /// <summary>How many documents are scanned for text at the same time.</summary>
    public static int OcrWorkers => Effective switch
    {
        PerformanceMode.PowerSaver => 1,
        PerformanceMode.Maximum => Math.Clamp(LogicalCores / 2, 2, 8),
        _ => Math.Clamp(LogicalCores / 4, 1, 3)
    };

    /// <summary>Resolution PDF pages are rendered at for text recognition (1.0 = 96 dpi).</summary>
    public static double OcrRenderScale => Effective switch
    {
        PerformanceMode.PowerSaver => 1.6,
        PerformanceMode.Maximum => 2.4,
        _ => 2.0
    };

    /// <summary>CPU threads suggested to Ollama when it runs on this PC (0 = let Ollama decide).</summary>
    public static int AiThreads => Effective switch
    {
        PerformanceMode.PowerSaver => Math.Max(2, LogicalCores / 4),
        PerformanceMode.Maximum => 0,
        _ => Math.Max(2, LogicalCores / 2)
    };

    public static ThreadPriority BackgroundPriority =>
        Effective == PerformanceMode.Maximum ? ThreadPriority.Normal : ThreadPriority.BelowNormal;

    // ------------------------------------------------------------------ hardware facts
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    public static bool OnBattery
    {
        get
        {
            try { return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0; }
            catch { return false; }
        }
    }

    public static bool HasBattery
    {
        get
        {
            try { return GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255; }
            catch { return false; }
        }
    }

    public static double TotalMemoryGb
    {
        get
        {
            try
            {
                var m = new MemoryStatusEx();
                return GlobalMemoryStatusEx(m) ? Math.Round(m.ullTotalPhys / 1024d / 1024d / 1024d, 1) : 0;
            }
            catch { return 0; }
        }
    }

    public static string Describe()
    {
        var power = HasBattery ? (OnBattery ? "laptop on battery" : "laptop on mains power") : "mains powered";
        var eff = Effective switch
        {
            PerformanceMode.PowerSaver => "Power saver",
            PerformanceMode.Maximum => "Maximum",
            _ => "Balanced"
        };
        return $"{LogicalCores} logical cores · {TotalMemoryGb:0.#} GB memory · {power}\n" +
               $"In use now: {eff} — {OcrWorkers} document(s) scanned at once, " +
               $"AI threads {(AiThreads == 0 ? "chosen by Ollama" : AiThreads.ToString())}.";
    }
}
