using System.Windows;
using Microsoft.Win32;

namespace CaseKeeper.UI;

public enum AppTheme { System = 0, Light = 1, Dark = 2 }

/// <summary>
/// Swaps the colour palette at runtime. Every style refers to its colours with DynamicResource,
/// so replacing the merged dictionary re-colours every open window immediately.
/// </summary>
public static class ThemeManager
{
    private const string LightUri = "Themes/Light.xaml";
    private const string DarkUri = "Themes/Dark.xaml";

    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>True when the app is actually painting dark right now.</summary>
    public static bool IsDark => Current == AppTheme.Dark || (Current == AppTheme.System && WindowsPrefersDark());

    public static event Action ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var uri = new Uri(IsDark ? DarkUri : LightUri, UriKind.Relative);
        var dict = (ResourceDictionary)Application.LoadComponent(uri);

        var merged = Application.Current.Resources.MergedDictionaries;
        // Slot 0 is always the palette, so styles defined after it keep resolving against the new one
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        ThemeChanged?.Invoke();
    }

    /// <summary>Re-applies the palette if Windows switched between light and dark while we were running.</summary>
    public static void RefreshIfFollowingWindows()
    {
        if (Current == AppTheme.System) Apply(AppTheme.System);
    }

    private static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = light, 0 = dark. Missing value means light.
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static AppTheme Parse(string s) =>
        Enum.TryParse<AppTheme>(s, true, out var t) ? t : AppTheme.System;
}
