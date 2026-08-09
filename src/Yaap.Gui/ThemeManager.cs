using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Yaap.Gui;

public enum AppThemeMode
{
    Auto,
    Light,
    Dark,
}

public sealed record ThemeOption(AppThemeMode Mode, string Label);

public static class ThemeManager
{
    public static void Apply(Window window, AppThemeMode requested)
    {
        ArgumentNullException.ThrowIfNull(window);
        SystemThemeWatcher.UnWatch(window);

        if (requested == AppThemeMode.Auto)
        {
            ApplicationThemeManager.ApplySystemTheme();
            SystemThemeWatcher.Watch(window, WindowBackdropType.None, updateAccents: true);
            return;
        }

        ApplicationTheme theme = ToApplicationTheme(requested);
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
        WindowBackgroundManager.UpdateBackground(window, theme, WindowBackdropType.None);
    }

    public static ApplicationTheme ToApplicationTheme(AppThemeMode requested)
    {
        return requested switch
        {
            AppThemeMode.Light => ApplicationTheme.Light,
            AppThemeMode.Dark => ApplicationTheme.Dark,
            _ => ApplicationTheme.Unknown,
        };
    }
}
