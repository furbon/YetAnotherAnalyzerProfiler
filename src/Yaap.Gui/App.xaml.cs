using System.Windows;
using Wpf.Ui.Appearance;

namespace Yaap.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ApplicationThemeManager.ApplySystemTheme();
    }
}
