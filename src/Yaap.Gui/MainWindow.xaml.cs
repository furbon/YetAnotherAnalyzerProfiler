using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            viewModel.LoadSelectedCommand.CanExecute(null))
        {
            viewModel.LoadSelectedCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }

    private void OnScrollableSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is System.Windows.Controls.DataGrid { SelectedItem: not null } grid)
        {
            grid.Dispatcher.BeginInvoke(() => grid.ScrollIntoView(grid.SelectedItem));
        }
    }

    private void OnScrollableControlLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not DependencyObject root)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => ConfigureAccessibleScrollBars(root),
            DispatcherPriority.Loaded);
    }

    private void ConfigureAccessibleScrollBars(DependencyObject root)
    {
        ControlTemplate? thumbTemplate =
            TryFindResource("AccessibleScrollThumbTemplate") as ControlTemplate;
        foreach (ScrollBar scrollBar in FindVisualDescendants<ScrollBar>(root))
        {
            scrollBar.ApplyTemplate();
            Track? track = scrollBar.Track ??
                scrollBar.Template.FindName("PART_Track", scrollBar) as Track;
            if (scrollBar.Orientation == Orientation.Vertical)
            {
                scrollBar.MinWidth = 16;
                if (track?.Thumb is Thumb verticalThumb)
                {
                    if (thumbTemplate is not null)
                    {
                        verticalThumb.Template = thumbTemplate;
                    }

                    verticalThumb.MinWidth = 12;
                    verticalThumb.MinHeight = 32;
                }
            }
            else
            {
                scrollBar.MinHeight = 16;
                if (track?.Thumb is Thumb horizontalThumb)
                {
                    if (thumbTemplate is not null)
                    {
                        horizontalThumb.Template = thumbTemplate;
                    }

                    horizontalThumb.MinWidth = 32;
                    horizontalThumb.MinHeight = 12;
                }
            }
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void OnHistoryDateValidationError(
        object sender,
        DatePickerDateValidationErrorEventArgs eventArgs)
    {
        eventArgs.ThrowException = false;
        if (sender is DatePicker picker &&
            MainViewModel.TryParseHistoryDateText(eventArgs.Text, out DateTime date))
        {
            picker.SelectedDate = date.Date;
        }
    }

    private void OnHistoryCalendarOpened(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not DatePicker picker)
        {
            return;
        }

        picker.Dispatcher.BeginInvoke(
            () => ApplyCalendarForeground(picker),
            DispatcherPriority.Loaded);
    }

    private static void ApplyCalendarForeground(DatePicker picker)
    {
        if (picker.Template.FindName("PART_Popup", picker) is not Popup { Child: DependencyObject popup })
        {
            return;
        }

        foreach (System.Windows.Controls.TextBlock text in
                 FindVisualDescendants<System.Windows.Controls.TextBlock>(popup))
        {
            text.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                "TextFillColorPrimaryBrush");
        }
    }

    private void OnHistoryPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not System.Windows.Controls.DataGrid grid)
        {
            return;
        }

        DependencyObject? source = eventArgs.OriginalSource as DependencyObject;
        DataGridRow? row = source is null
            ? null
            : ItemsControl.ContainerFromElement(grid, source) as DataGridRow;
        if (row is not null)
        {
            grid.SelectedItem = row.Item;
            row.Focus();
        }
    }

    private static string[] GetDroppedPaths(DragEventArgs eventArgs) =>
        eventArgs.Data.GetDataPresent(DataFormats.FileDrop) &&
        eventArgs.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : Array.Empty<string>();
}
