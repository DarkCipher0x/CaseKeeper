using System.IO;
using System.Text.Json;

namespace CaseKeeper.Services;

/// <summary>
/// A small "in use" marker kept in the library folder while CaseKeeper has it open.
///
/// A library on a shared drive can be reached from more than one PC, but SQLite over a network
/// share isn't safe with two computers writing at once. The marker lets a second PC see that the
/// library is already open elsewhere and warn before opening it. It's refreshed every minute, so
/// one left behind by a crash or a dropped connection goes stale and stops counting.
/// </summary>
public static class LibraryLock
{
    public const string FileName = ".casekeeper-in-use.json";

    /// <summary>How long a marker counts after its last refresh.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private static string _heldIn;
    private static System.Threading.Timer _refresh;

    public sealed class Holder
    {
        public string Machine { get; set; }
        public string User { get; set; }
        public int ProcessId { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime LastSeen { get; set; }
    }

    /// <summary>
    /// Who else has this library open right now, or null if nobody (or only this PC) does.
    /// </summary>
    public static Holder FindOtherHolder(string libraryRoot)
    {
        try
        {
            var path = Path.Combine(libraryRoot, FileName);
            if (!File.Exists(path)) return null;
            var h = JsonSerializer.Deserialize<Holder>(File.ReadAllText(path));
            if (h == null) return null;
            if (string.Equals(h.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return null;
            if (DateTime.UtcNow - h.LastSeen > StaleAfter) return null;
            return h;
        }
        catch { return null; /* unreadable marker - don't block opening over it */ }
    }

    /// <summary>Marks the library as open on this PC and keeps the marker fresh until <see cref="Release"/>.</summary>
    public static void Acquire(string libraryRoot)
    {
        _heldIn = libraryRoot;
        var opened = DateTime.UtcNow;
        Write(opened);
        _refresh = new System.Threading.Timer(_ => Write(opened), null, RefreshEvery, RefreshEvery);
    }

    public static void Release()
    {
        _refresh?.Dispose();
        _refresh = null;
        if (_heldIn == null) return;
        try
        {
            var path = Path.Combine(_heldIn, FileName);
            // Only remove our own marker, never one another PC has written since
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Holder>(File.ReadAllText(path)) is { } h &&
                string.Equals(h.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }
        catch { /* drive gone or file busy - the marker will go stale on its own */ }
        _heldIn = null;
    }

    private static void Write(DateTime opened)
    {
        try
        {
            // The marker sits in plain view beside the library (even an encrypted one), so it names
            // the computer only. The Windows account name is personal and isn't needed to warn anyone.
            var holder = new Holder
            {
                Machine = Environment.MachineName,
                User = "",
                ProcessId = Environment.ProcessId,
                OpenedAt = opened,
                LastSeen = DateTime.UtcNow
            };
            File.WriteAllText(Path.Combine(_heldIn, FileName), JsonSerializer.Serialize(holder));
        }
        catch { /* a network blip shouldn't interrupt the user; we'll try again next refresh */ }
    }

    public static string Describe(Holder h)
    {
        var minutes = (int)Math.Max(0, (DateTime.UtcNow - h.OpenedAt).TotalMinutes);
        var since = minutes < 1 ? "just now" : minutes < 120 ? $"{minutes} minute(s) ago" : $"at {h.OpenedAt.ToLocalTime():g}";
        return $"{h.Machine}, opened {since}";
    }
}
