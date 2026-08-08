using System.Windows;

namespace Yaap.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ThemeManager.Apply(AppThemeMode.Auto);
    }
}
