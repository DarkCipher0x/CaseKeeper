using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CaseKeeper.Services;

/// <summary>
/// Whether this copy of CaseKeeper was installed from the Microsoft Store (an MSIX package) or by
/// the ordinary installer, and how to restart it in each case.
///
/// A Store copy runs with a "package identity". Starting its exe straight from the install folder
/// would start it without that identity, so a Store copy restarts through its Start-menu entry
/// instead. Both kinds keep the library in the same place (%LOCALAPPDATA%\CaseKeeper): the Store
/// package opts that folder out of Windows' per-app redirection, so switching between the two
/// installs, or uninstalling either, never loses or hides anyone's documents.
/// </summary>
public static class AppPackage
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    // The Application Id in packaging\store\AppxManifest.xml. Keep the two the same.
    private const string ApplicationId = "CaseKeeper";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder name);

    private static readonly Lazy<string> Family = new(ReadFamilyName);

    /// <summary>The package family name when installed from the Store, otherwise null.</summary>
    public static string FamilyName => Family.Value;

    public static bool IsPackaged => FamilyName != null;

    private static string ReadFamilyName()
    {
        try
        {
            int length = 0;
            int rc = GetCurrentPackageFamilyName(ref length, null);
            if (rc == AppModelErrorNoPackage || rc != ErrorInsufficientBuffer) return null;
            var sb = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }   // very old Windows without the API: certainly not packaged
    }

    /// <summary>Starts a fresh copy of CaseKeeper. The caller then shuts this one down.</summary>
    public static void StartNewCopy()
    {
        if (IsPackaged)
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{FamilyName}!{ApplicationId}") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
    }
}
