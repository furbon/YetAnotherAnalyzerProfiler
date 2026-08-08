using System.Windows;

namespace Yaap.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += async (_, _) => await ((MainViewModel)DataContext).InitializeAsync();
    }
}
