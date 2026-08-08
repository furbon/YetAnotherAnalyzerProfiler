using System.ComponentModel;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Yaap.Gui;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
        : this(new MainViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        RecentTargetsPopup.DataContext = viewModel;
        AdvancedSettingsPopup.DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += async (_, _) =>
        {
            ApplySelectedTheme();
            await viewModel.InitializeAsync();
        };
        Closing += (_, _) => SystemThemeWatcher.UnWatch(this);
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.SelectedTheme))
        {
            ApplySelectedTheme();
        }
    }

    private void ApplySelectedTheme()
    {
        if (DataContext is MainViewModel viewModel)
        {
            ThemeManager.Apply(this, viewModel.SelectedTheme.Mode);
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs eventArgs)
    {
        string[] paths = GetDroppedPaths(eventArgs);
        eventArgs.Effects = ((MainViewModel)DataContext).CanAcceptDroppedTarget(paths)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs eventArgs)
    {
        string[] paths = GetDroppedPaths(eventArgs);
        eventArgs.Effects = ((MainViewModel)DataContext).TrySetDroppedTarget(paths)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void OnRecentTargetClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { DataContext: RecentTarget recentTarget } &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedRecentTarget = recentTarget;
            RecentTargetsButton.IsChecked = false;
            RecentTargetsPopup.IsOpen = false;
        }
    }

    private static string[] GetDroppedPaths(DragEventArgs eventArgs) =>
        eventArgs.Data.GetDataPresent(DataFormats.FileDrop) &&
        eventArgs.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : Array.Empty<string>();
}
