using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Yaap.Gui;

public enum AppThemeMode
{
    Auto,
    Light,
    Dark,
}

public sealed record ThemeOption(AppThemeMode Mode, string Label);

public sealed record ThemePalette(
    AppThemeMode EffectiveMode,
    string WindowBackground,
    string Surface,
    string ElevatedSurface,
    string Foreground,
    string MutedForeground,
    string Border,
    string InputBackground,
    string ControlBackground,
    string ControlHover,
    string Accent,
    string AccentHover,
    string AccentForeground,
    string Selection,
    string StatusBackground);

public static class ThemeManager
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppThemeMode ResolveEffectiveMode(
        AppThemeMode requested,
        Func<bool>? systemUsesLightTheme = null)
    {
        if (requested != AppThemeMode.Auto)
        {
            return requested;
        }

        bool light = (systemUsesLightTheme ?? SystemUsesLightTheme)();
        return light ? AppThemeMode.Light : AppThemeMode.Dark;
    }

    public static ThemePalette GetPalette(
        AppThemeMode requested,
        Func<bool>? systemUsesLightTheme = null)
    {
        AppThemeMode effective = ResolveEffectiveMode(requested, systemUsesLightTheme);
        return effective == AppThemeMode.Dark
            ? new ThemePalette(
                effective,
                "#111418",
                "#191E24",
                "#212830",
                "#F1F5F9",
                "#A8B3C1",
                "#36404C",
                "#232A33",
                "#29323C",
                "#35404C",
                "#5B8DEF",
                "#78A4F5",
                "#FFFFFF",
                "#29476E",
                "#172A44")
            : new ThemePalette(
                effective,
                "#F3F5F7",
                "#FFFFFF",
                "#F8FAFC",
                "#1C222B",
                "#5D6673",
                "#D5DAE1",
                "#FFFFFF",
                "#EEF1F5",
                "#E2E7ED",
                "#2563EB",
                "#1D4ED8",
                "#FFFFFF",
                "#DCE8FF",
                "#EAF2FF");
    }

    public static AppThemeMode Apply(AppThemeMode requested)
    {
        ThemePalette palette = GetPalette(requested);
        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            return palette.EffectiveMode;
        }

        SetBrush(resources, "WindowBackgroundBrush", palette.WindowBackground);
        SetBrush(resources, "SurfaceBrush", palette.Surface);
        SetBrush(resources, "ElevatedSurfaceBrush", palette.ElevatedSurface);
        SetBrush(resources, "ForegroundBrush", palette.Foreground);
        SetBrush(resources, "MutedForegroundBrush", palette.MutedForeground);
        SetBrush(resources, "BorderBrush", palette.Border);
        SetBrush(resources, "InputBackgroundBrush", palette.InputBackground);
        SetBrush(resources, "ControlBackgroundBrush", palette.ControlBackground);
        SetBrush(resources, "ControlHoverBrush", palette.ControlHover);
        SetBrush(resources, "AccentBrush", palette.Accent);
        SetBrush(resources, "AccentHoverBrush", palette.AccentHover);
        SetBrush(resources, "AccentForegroundBrush", palette.AccentForeground);
        SetBrush(resources, "SelectionBrush", palette.Selection);
        SetBrush(resources, "StatusBackgroundBrush", palette.StatusBackground);
        return palette.EffectiveMode;
    }

    public static void Apply(Window window, AppThemeMode requested)
    {
        ArgumentNullException.ThrowIfNull(window);
        AppThemeMode effective = Apply(requested);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        int dark = effective == AppThemeMode.Dark ? 1 : 0;
        int result = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        if (result != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
    }

    private static bool SystemUsesLightTheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            object? value = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1);
            return value is not int integer || integer != 0;
        }
        catch (Exception exception) when (
            exception is SecurityException or System.IO.IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return true;
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int value,
        int valueSize);
}
