using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly ConditionalWeakTable<System.Windows.Controls.TextBlock, object> _configuredCalendarText = new();
    private bool _scrollBarRefreshPending;

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
        else if (eventArgs.PropertyName is nameof(MainViewModel.IsRunning) or
                 nameof(MainViewModel.IsOperationRunning))
        {
            ScheduleAccessibleScrollBarRefresh();
        }
    }

    private void ApplySelectedTheme()
    {
        if (DataContext is MainViewModel viewModel)
        {
            ThemeManager.Apply(this, viewModel.SelectedTheme.Mode);
            ScheduleAccessibleScrollBarRefresh();
        }
    }

    private void ScheduleAccessibleScrollBarRefresh()
    {
        if (_scrollBarRefreshPending)
        {
            return;
        }

        _scrollBarRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _scrollBarRefreshPending = false;
                UpdateLayout();
                ConfigureAccessibleScrollBars(this);
                UpdateLayout();
            },
            DispatcherPriority.ContextIdle);
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

    private void OnAccessibleScrollBarLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is ScrollBar scrollBar)
        {
            scrollBar.LayoutUpdated -= OnAccessibleScrollBarLayoutUpdated;
            scrollBar.LayoutUpdated += OnAccessibleScrollBarLayoutUpdated;
            ConfigureAccessibleScrollBar(scrollBar);
        }
    }

    private void OnAccessibleScrollBarUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is ScrollBar scrollBar)
        {
            scrollBar.LayoutUpdated -= OnAccessibleScrollBarLayoutUpdated;
        }
    }

    private void OnAccessibleScrollBarLayoutUpdated(object? sender, EventArgs eventArgs)
    {
        if (sender is ScrollBar scrollBar)
        {
            ConfigureAccessibleScrollBar(scrollBar);
        }
    }

    private void ConfigureAccessibleScrollBars(DependencyObject root)
    {
        foreach (ScrollBar scrollBar in FindVisualDescendants<ScrollBar>(root))
        {
            scrollBar.LayoutUpdated -= OnAccessibleScrollBarLayoutUpdated;
            scrollBar.LayoutUpdated += OnAccessibleScrollBarLayoutUpdated;
            ConfigureAccessibleScrollBar(scrollBar);
        }
    }

    private void ConfigureAccessibleScrollBar(ScrollBar scrollBar)
    {
        scrollBar.ApplyTemplate();
        Track? track = scrollBar.Track ??
            scrollBar.Template.FindName("PART_Track", scrollBar) as Track;
        if (scrollBar.Orientation == Orientation.Vertical)
        {
            if (scrollBar.MinWidth < 20)
            {
                scrollBar.MinWidth = 20;
            }
        }
        else if (scrollBar.MinHeight < 20)
        {
            scrollBar.MinHeight = 20;
        }

        if (track?.Thumb is not Thumb thumb)
        {
            return;
        }

        ControlTemplate? thumbTemplate = TryFindResource(
            scrollBar.Orientation == Orientation.Vertical
                ? "AccessibleVerticalScrollThumbTemplate"
                : "AccessibleHorizontalScrollThumbTemplate") as ControlTemplate;
        if (thumbTemplate is not null && !ReferenceEquals(thumb.Template, thumbTemplate))
        {
            thumb.Template = thumbTemplate;
            thumb.ApplyTemplate();
        }

        if (!thumb.RenderTransform.Value.IsIdentity)
        {
            thumb.RenderTransform = Transform.Identity;
        }

        if (!thumb.LayoutTransform.Value.IsIdentity)
        {
            thumb.LayoutTransform = Transform.Identity;
        }

        if (thumb.HorizontalContentAlignment != HorizontalAlignment.Stretch)
        {
            thumb.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }

        if (thumb.VerticalContentAlignment != VerticalAlignment.Stretch)
        {
            thumb.VerticalContentAlignment = VerticalAlignment.Stretch;
        }

        if (thumb.Clip is not null)
        {
            thumb.Clip = null;
        }

        if (thumb.ClipToBounds)
        {
            thumb.ClipToBounds = false;
        }

        if (thumb.OpacityMask is not null)
        {
            thumb.OpacityMask = null;
        }

        if (Math.Abs(thumb.Opacity - 1) > double.Epsilon)
        {
            thumb.Opacity = 1;
        }

        if (scrollBar.Orientation == Orientation.Vertical)
        {
            if (thumb.MinWidth < 16)
            {
                thumb.MinWidth = 16;
            }

            if (thumb.MinHeight < 52)
            {
                thumb.MinHeight = 52;
            }
        }
        else
        {
            if (thumb.MinWidth < 52)
            {
                thumb.MinWidth = 52;
            }

            if (thumb.MinHeight < 16)
            {
                thumb.MinHeight = 16;
            }
        }

        ConfigureAccessibleTrackViewport(scrollBar, track);
    }

    private static void ConfigureAccessibleTrackViewport(ScrollBar scrollBar, Track track)
    {
        const double minimumThumbLength = 52;
        double trackLength = scrollBar.Orientation == Orientation.Vertical
            ? track.ActualHeight
            : track.ActualWidth;
        double range = scrollBar.Maximum - scrollBar.Minimum;
        if (range <= 0 || trackLength <= minimumThumbLength)
        {
            return;
        }

        double minimumViewport = minimumThumbLength * range / (trackLength - minimumThumbLength);
        double accessibleViewport = Math.Max(scrollBar.ViewportSize, minimumViewport);
        if (double.IsFinite(accessibleViewport) &&
            Math.Abs(track.ViewportSize - accessibleViewport) > 0.1)
        {
            track.ViewportSize = accessibleViewport;
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
            () => AttachCalendarAccessibility(picker),
            DispatcherPriority.Loaded);
    }

    private void AttachCalendarAccessibility(DatePicker picker)
    {
        if (picker.Template.FindName("PART_Popup", picker) is not Popup { Child: DependencyObject popup })
        {
            return;
        }

        Calendar? calendar = popup as Calendar ?? FindVisualDescendants<Calendar>(popup).FirstOrDefault();
        if (calendar is null)
        {
            return;
        }

        calendar.LayoutUpdated -= OnHistoryCalendarLayoutUpdated;
        calendar.LayoutUpdated += OnHistoryCalendarLayoutUpdated;
        calendar.DisplayModeChanged -= OnHistoryCalendarDisplayModeChanged;
        calendar.DisplayModeChanged += OnHistoryCalendarDisplayModeChanged;
        calendar.Unloaded -= OnHistoryCalendarUnloaded;
        calendar.Unloaded += OnHistoryCalendarUnloaded;
        ApplyCalendarForeground(calendar, force: true);
    }

    private void OnHistoryCalendarLayoutUpdated(object? sender, EventArgs eventArgs)
    {
        if (sender is Calendar calendar)
        {
            ApplyCalendarForeground(calendar);
        }
    }

    private void OnHistoryCalendarDisplayModeChanged(
        object? sender,
        CalendarModeChangedEventArgs eventArgs)
    {
        if (sender is Calendar calendar)
        {
            calendar.Dispatcher.BeginInvoke(
                () => ApplyCalendarForeground(calendar, force: true),
                DispatcherPriority.Loaded);
        }
    }

    private void OnHistoryCalendarUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Calendar calendar)
        {
            calendar.LayoutUpdated -= OnHistoryCalendarLayoutUpdated;
            calendar.DisplayModeChanged -= OnHistoryCalendarDisplayModeChanged;
            calendar.Unloaded -= OnHistoryCalendarUnloaded;
        }
    }

    private void ApplyCalendarForeground(Calendar calendar, bool force = false)
    {
        foreach (System.Windows.Controls.TextBlock text in
                 FindVisualDescendants<System.Windows.Controls.TextBlock>(calendar))
        {
            bool configured = _configuredCalendarText.TryGetValue(text, out _);
            if (configured && !force)
            {
                continue;
            }

            text.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                "TextFillColorPrimaryBrush");
            if (!configured)
            {
                _configuredCalendarText.Add(text, new object());
            }
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
