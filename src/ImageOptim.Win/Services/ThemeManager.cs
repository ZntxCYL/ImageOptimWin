using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ImageOptim.Win.Services;

/// <summary>
/// WPF palette controlled by Windows Settings > Personalization > Colors >
/// Choose your mode (AppsUseLightTheme). Brushes are updated in place so every
/// DynamicResource consumer, including an already-open settings window, repaints.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizationKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static string Mode { get; private set; } = "System";
    public static bool IsDark { get; private set; }

    public static void Apply(string? mode)
    {
        Mode = mode is "Light" or "Dark" ? mode : "System";
        IsDark = Mode == "Dark" || Mode == "System" && WindowsAppsUseDarkTheme();
        var palette = IsDark ? DarkPalette : LightPalette;
        // WPF can freeze brushes loaded from compiled XAML resources. Mutating
        // brush.Color in place may then throw InvalidOperationException before
        // the first window is shown. Replace each resource instead: existing
        // DynamicResource references automatically resolve the new brush.
        if (Application.Current is not { } app) return;
        foreach (var (key, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            app.Resources[key] = new SolidColorBrush(color);
        }
        foreach (Window window in app.Windows) ApplyWindowChrome(window);
    }

    /// <summary>Use matching DWM caption colors for native preference windows.</summary>
    public static void ApplyWindowChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var isDark = IsDark ? 1 : 0;
        try
        {
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int)) != 0)
            {
                // Older Windows 10 builds used the previous enum value.
                const int LEGACY_DARK_MODE = 19;
                DwmSetWindowAttribute(hwnd, LEGACY_DARK_MODE, ref isDark, sizeof(int));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static void RefreshSystemTheme()
    {
        if (Mode == "System") Apply("System");
    }

    private static bool WindowsAppsUseDarkTheme()
    {
        try
        {
            // Missing key defaults to Windows' traditional light app theme.
            var value = Registry.GetValue(PersonalizationKey, "AppsUseLightTheme", 1);
            return value is int setting && setting == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["ThemeWindow"] = "#FFFFFF", ["ThemeTitleBar"] = "#F2F2F3",
        ["ThemeHeader"] = "#F7F7F8", ["ThemeFooter"] = "#F5F5F6",
        ["ThemeBorder"] = "#D7D8DC", ["ThemeText"] = "#24252A",
        ["ThemeSecondaryText"] = "#676970", ["ThemeMutedText"] = "#96989D",
        ["ThemeStripe"] = "#F6F6F7", ["ThemeSelected"] = "#1463CF",
        ["ThemeSelectedText"] = "#FFFFFF", ["ThemeButton"] = "#F8F8F9",
        ["ThemeButtonHover"] = "#E8E9EC", ["ThemeProgressTrack"] = "#DADDE1",
        ["ThemeProgressFill"] = "#1680F2", ["ThemeInput"] = "#FFFFFF",
        ["ThemeDialog"] = "#F7F7F8", ["ThemeStatusDone"] = "#16A34A",
        ["ThemeStatusRunning"] = "#2B7CD6", ["ThemeStatusFailed"] = "#D53D45"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["ThemeWindow"] = "#1F1F20", ["ThemeTitleBar"] = "#39393A",
        ["ThemeHeader"] = "#242425", ["ThemeFooter"] = "#2A2A2B",
        ["ThemeBorder"] = "#464648", ["ThemeText"] = "#F2F2F3",
        ["ThemeSecondaryText"] = "#C0C0C3", ["ThemeMutedText"] = "#919196",
        ["ThemeStripe"] = "#29292B", ["ThemeSelected"] = "#075BC8",
        ["ThemeSelectedText"] = "#FFFFFF", ["ThemeButton"] = "#363638",
        ["ThemeButtonHover"] = "#505054", ["ThemeProgressTrack"] = "#4D4D50",
        ["ThemeProgressFill"] = "#5DA6FF", ["ThemeInput"] = "#303032",
        ["ThemeDialog"] = "#2C2C2E", ["ThemeStatusDone"] = "#24CA65",
        ["ThemeStatusRunning"] = "#61ACFF", ["ThemeStatusFailed"] = "#FF6B6E"
    };
}
