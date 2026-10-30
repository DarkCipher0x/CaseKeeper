using System.IO;

namespace CaseKeeper.Services;

/// <summary>
/// Where CaseKeeper keeps things on disk, for each of the two ways it can be installed.
///
/// Website installer (unpackaged):
///   library   %LOCALAPPDATA%\CaseKeeper           (as it always has been)
///   settings  %APPDATA%\CaseKeeper                (library-location.json, recent-libraries.json)
///   logs      %LOCALAPPDATA%\CaseKeeper\Logs
///
/// Microsoft Store (MSIX package):
///   library   %USERPROFILE%\CaseKeeper
///   settings  the package's own LocalState folder
///   logs      the package's own LocalState\Logs folder
///
/// A packaged app's new files under AppData are redirected by Windows into a hidden per-package
/// folder that is deleted when the app is uninstalled. That is right for settings and logs, so
/// the Store copy keeps them in its own package folder and Windows removes them on uninstall.
/// It would be wrong for someone's case, so the Store copy keeps the library outside AppData,
/// where it is an ordinary folder they can see, back up and delete, and where it survives an
/// uninstall or reinstall. Settings → Factory reset → "Remove all CaseKeeper data" deletes it.
/// </summary>
public static class AppPaths
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The library folder used when nobody has chosen another one.</summary>
    public static string DefaultLibrary => AppPackage.IsPackaged
        ? Path.Combine(UserProfile, "CaseKeeper")
        : WebsiteDefaultLibrary;

    /// <summary>The website installer's default library. A Store copy offers to copy it on first run.</summary>
    public static string WebsiteDefaultLibrary => Path.Combine(LocalAppData, "CaseKeeper");

    /// <summary>The small files that remember where the library is and which libraries were opened recently.</summary>
    public static string SettingsFolder => AppPackage.IsPackaged
        ? PackageLocalState
        : Path.Combine(RoamingAppData, "CaseKeeper");

    public static string LogsFolder => AppPackage.IsPackaged
        ? Path.Combine(PackageLocalState, "Logs")
        : Path.Combine(LocalAppData, "CaseKeeper", "Logs");

    /// <summary>
    /// The Store copy's private data folder (%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalState). It is a
    /// real folder, so File Explorer can open it, and Windows deletes it when the app is uninstalled.
    /// </summary>
    private static string PackageLocalState =>
        Path.Combine(LocalAppData, "Packages", AppPackage.FamilyName, "LocalState");

    /// <summary>
    /// True for a path inside %LOCALAPPDATA% or %APPDATA%. A Store copy must never keep a library
    /// there: new files would be hidden in the package folder and deleted on uninstall.
    /// </summary>
    public static bool IsInsideAppData(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            return new[] { LocalAppData, RoamingAppData }
                .Where(p => !string.IsNullOrEmpty(p))
                .Any(p => full.StartsWith(Path.GetFullPath(p).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Why a Store copy can't use a folder, or null when it can (always null for the website copy).</summary>
    public static string PackagedLocationProblem(string path) =>
        AppPackage.IsPackaged && IsInsideAppData(path)
            ? "The Microsoft Store version of CaseKeeper can't keep a library inside the hidden AppData folder: Windows would " +
              "delete it when the app is uninstalled. Choose a folder somewhere else, for example in your user folder or on another drive."
            : null;
}
