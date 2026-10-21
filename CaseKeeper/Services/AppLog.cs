using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace CaseKeeper.Services;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3, Fatal = 4 }

public sealed class LogEntry
{
    public DateTime At { get; init; }
    public LogLevel Level { get; init; }
    public string Area { get; init; } = "";
    public string Message { get; init; } = "";
    public string Detail { get; init; } = "";
    public string ErrorId { get; init; } = "";

    public string LevelText => Level.ToString().ToUpperInvariant();
    public string TimeText => At.ToString("dd MMM HH:mm:ss");
}

/// <summary>
/// The app's diagnostic log: what went wrong, where, and when.
///
/// Written to %LOCALAPPDATA%\CaseKeeper\Logs, one file per day, kept for a limited number of days.
/// It deliberately records <i>what the program did</i>, never the contents of your case: no document
/// text, no notes, and your Windows profile folder is replaced with %USERPROFILE% so a log can be
/// shared for support without giving away who you are. Every error gets a short reference
/// (e.g. E-7F3A2C) that is shown on screen too, so the two can be matched up.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<LogEntry> Recent = new();
    private const int RecentLimit = 600;
    private const long MaxFileBytes = 8 * 1024 * 1024;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaseKeeper", "Logs");

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public static int RetentionDays { get; set; } = 30;

    /// <summary>Raised (on any thread) whenever an entry is written, for the in-app log viewer.</summary>
    public static event Action<LogEntry> Written;

    public static IReadOnlyList<LogEntry> RecentEntries => Recent.ToArray();

    public static void Debug(string area, string message) => Write(LogLevel.Debug, area, message, null);
    public static void Info(string area, string message) => Write(LogLevel.Info, area, message, null);
    public static void Warn(string area, string message, Exception ex = null) => Write(LogLevel.Warning, area, message, ex);

    /// <summary>Records an error and returns its reference, to show to the user.</summary>
    public static string Error(string area, string message, Exception ex = null) => Write(LogLevel.Error, area, message, ex);
    public static string Fatal(string area, string message, Exception ex = null) => Write(LogLevel.Fatal, area, message, ex);

    private static string Write(LogLevel level, string area, string message, Exception ex)
    {
        if (level < MinimumLevel) return "";
        var id = level >= LogLevel.Error ? "E-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant() : "";
        var entry = new LogEntry
        {
            At = DateTime.Now, Level = level, Area = area ?? "", ErrorId = id,
            Message = Redact(message ?? ""),
            Detail = ex == null ? "" : Redact(Describe(ex))
        };

        Recent.Enqueue(entry);
        while (Recent.Count > RecentLimit && Recent.TryDequeue(out _)) { }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var path = CurrentFile();
                var sb = new StringBuilder();
                sb.Append(entry.At.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(entry.LevelText).Append("] ");
                sb.Append(entry.Area);
                if (id.Length > 0) sb.Append(" (").Append(id).Append(')');
                sb.Append(": ").Append(entry.Message);
                if (entry.Detail.Length > 0) sb.AppendLine().Append(Indent(entry.Detail));
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* logging must never take the app down */ }

        try { Written?.Invoke(entry); } catch { /* listener problems are not ours */ }
        return id;
    }

    private static string CurrentFile()
    {
        var baseName = Path.Combine(Folder, $"casekeeper-{DateTime.Now:yyyyMMdd}");
        var path = baseName + ".log";
        int n = 1;
        while (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
            path = $"{baseName}-{++n}.log";
        return path;
    }

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        int depth = 0;
        for (var e = ex; e != null && depth < 6; e = e.InnerException, depth++)
        {
            if (depth > 0) sb.AppendLine("--- caused by ---");
            sb.Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
            if (e.StackTrace != null) sb.AppendLine(e.StackTrace);
        }
        return sb.ToString().TrimEnd();
    }

    private static string Indent(string s) => "    " + s.Replace("\n", "\n    ");

    /// <summary>Removes the things in a message that would identify the person using the PC.</summary>
    public static string Redact(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            var user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length > 2)
                s = System.Text.RegularExpressions.Regex.Replace(s, $@"\b{System.Text.RegularExpressions.Regex.Escape(user)}\b", "<user>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { /* best effort */ }
        return s;
    }

    /// <summary>Deletes log files older than the retention period. Called once at startup.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, RetentionDays));
            foreach (var f in Directory.EnumerateFiles(Folder, "casekeeper-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch (Exception ex) { Warn("Log", "Couldn't tidy old log files", ex); }
    }

    public static IEnumerable<string> LogFiles() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, "casekeeper-*.log").OrderByDescending(f => f)
            : Enumerable.Empty<string>();
}
