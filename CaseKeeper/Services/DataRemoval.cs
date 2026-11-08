using System.Diagnostics;
using System.IO;
using CaseKeeper.Data;

namespace CaseKeeper.Services;

/// <summary>
/// Removes everything CaseKeeper has stored on this PC: the libraries it knows about, the files
/// that remember where they are (and the update settings), the logs, any decrypted copies left for other
/// programs, and a downloaded update that hasn't been installed.
///
/// Used in two places:
///   • Settings → Factory reset → "Remove all CaseKeeper data from this PC…"
///   • the website installer's uninstaller, which runs  CaseKeeper.exe --remove-data  when the
///     person says yes to "also delete your library?"
/// The Microsoft Store copy has no uninstaller of its own, so the Settings button is how its
/// library is removed (Windows removes its settings and logs with the app).
///
/// Only CaseKeeper's own files are deleted. A library can live in a folder that holds other
/// things (someone may have chosen Documents itself), so each library folder loses just the
/// database, vault, Files, Exports, Snapshots and restore folders, and the folder itself goes
/// only if that leaves it empty. Libraries on network drives are never touched from here: other
/// people or PCs may rely on them.
/// </summary>
public static class DataRemoval
{
    private static readonly string[] LibraryFiles =
    {
        "casekeeper.db", "casekeeper.db-wal", "casekeeper.db-shm", "casekeeper.db-journal", "casekeeper.db.backup-copy",
        Vault.FileName, Vault.FileName + ".tmp", LibraryLock.FileName, ".casekeeper-write-test"
    };

    private static readonly string[] LibraryFolders = { "Files", "Exports", "Snapshots", "_restore", "_import" };

    public sealed class Plan
    {
        /// <summary>Libraries on this PC that will be deleted.</summary>
        public List<string> Libraries { get; } = new();
        /// <summary>Libraries on network drives, which are left alone.</summary>
        public List<string> NetworkLibraries { get; } = new();
        /// <summary>Settings, logs and temporary copies.</summary>
        public List<string> OtherFolders { get; } = new();
        public bool IsEmpty => Libraries.Count == 0 && OtherFolders.Count == 0;
    }

    /// <summary>Works out what would be deleted, without deleting anything.</summary>
    public static Plan Build()
    {
        var plan = new Plan();
        var candidates = new List<string> { LibraryService.ResolveRoot(), AppPaths.DefaultLibrary };
        candidates.AddRange(LibraryService.RecentLibraries());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            string full;
            try { full = Path.GetFullPath(candidate).TrimEnd('\\'); }
            catch { continue; }
            if (!seen.Add(full)) continue;
            if (Database.IsNetworkPath(full))
            {
                if (Directory.Exists(full)) plan.NetworkLibraries.Add(full);
                continue;
            }
            if (LooksLikeLibrary(full)) plan.Libraries.Add(full);
        }

        foreach (var folder in new[] { AppPaths.SettingsFolder, AppPaths.LogsFolder, LibraryService.TempCopiesFolder, AppPaths.UpdateWorkFolder })
            if (Directory.Exists(folder) && !plan.OtherFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                plan.OtherFolders.Add(folder);
        return plan;
    }

    private static bool LooksLikeLibrary(string folder) =>
        File.Exists(Path.Combine(folder, "casekeeper.db")) || File.Exists(Path.Combine(folder, Vault.FileName)) ||
        Directory.Exists(Path.Combine(folder, "Files"));

    /// <summary>Another copy of CaseKeeper running on this PC, which must be closed first.</summary>
    public static bool OtherCopyRunning()
    {
        var me = Environment.ProcessId;
        return Process.GetProcessesByName("CaseKeeper").Any(p => { using (p) return p.Id != me; });
    }

    /// <summary>
    /// Deletes everything in the plan. Carries on past anything it can't delete and returns a
    /// description of each problem (empty when everything went).
    /// The caller must have closed the library first.
    /// </summary>
    public static List<string> Execute(Plan plan)
    {
        var problems = new List<string>();
        AppLog.Disabled = true;          // writing a log now would only recreate a folder being deleted
        Database.Retired = true;         // and opening the database would create a new, empty one

        foreach (var library in plan.Libraries)
        {
            foreach (var name in LibraryFiles)
                Try(() => { var f = Path.Combine(library, name); if (File.Exists(f)) File.Delete(f); }, Path.Combine(library, name), problems);
            foreach (var name in LibraryFolders)
                Try(() => { var d = Path.Combine(library, name); if (Directory.Exists(d)) Directory.Delete(d, true); }, Path.Combine(library, name), problems);
            if (Directory.Exists(library))
            {
                foreach (var d in Directory.EnumerateDirectories(library, "_before_restore_*").ToList())
                    Try(() => Directory.Delete(d, true), d, problems);
                foreach (var f in Directory.EnumerateFiles(library, "*.part").ToList())
                    Try(() => File.Delete(f), f, problems);
            }
        }

        foreach (var folder in plan.OtherFolders)
            Try(() => { if (Directory.Exists(folder)) Directory.Delete(folder, true); }, folder, problems);

        // Folders left empty go too: each library folder, and the website version's
        // %LOCALAPPDATA%\CaseKeeper once its Logs folder has gone
        foreach (var folder in plan.Libraries.Append(AppPaths.WebsiteDefaultLibrary))
            try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch { /* not empty after all, or in use: leaving an empty folder is harmless */ }

        return problems;
    }

    private static void Try(Action delete, string what, List<string> problems)
    {
        try { delete(); }
        catch (Exception ex) { problems.Add($"{what}\n    {ex.Message}"); }
    }
}
