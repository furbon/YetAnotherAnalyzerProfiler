using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Yaap.Core;

namespace Yaap.Gui;

public partial class MainWindow : FluentWindow
{
    public static RoutedUICommand CopyAnalyzerResultCommand { get; } = new(
        "Analyzer の詳細をコピー",
        nameof(CopyAnalyzerResultCommand),
        typeof(MainWindow));

    private readonly ConditionalWeakTable<System.Windows.Controls.Button, object> _configuredCalendarButtons = new();
    private bool _scrollBarRefreshPending;
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;

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
        Closing += OnClosingAsync;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        };
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs eventArgs)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_shutdownInProgress || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _shutdownInProgress = true;
        IsEnabled = false;
        try
        {
            await viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _shutdownCompleted = true;
            SystemThemeWatcher.UnWatch(this);
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    if (IsVisible)
                    {
                        Close();
                    }
                },
                DispatcherPriority.Normal);
        }
        catch (TimeoutException exception)
        {
            IsEnabled = true;
            _shutdownInProgress = false;
            System.Windows.MessageBox.Show(
                this,
                $"実行中の処理を安全に終了できませんでした。処理の完了後にもう一度閉じてください。\n\n{exception.Message}",
                "終了待機",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        catch (YaapException exception)
        {
            IsEnabled = true;
            _shutdownInProgress = false;
            System.Windows.MessageBox.Show(
                this,
                $"実行中の子プロセスが終了していないため、YAAPを閉じません。\n\n{exception.Diagnostic.Detail}\n{exception.Diagnostic.SuggestedAction}",
                "終了できません",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
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

    private void OnHistoryDateTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (sender is not DatePicker picker || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        string text = eventArgs.OriginalSource is System.Windows.Controls.TextBox textBox
            ? textBox.Text
            : picker.Text;
        if (ReferenceEquals(picker, HistoryFromDatePicker))
        {
            viewModel.HistoryFrom = text;
        }
        else if (ReferenceEquals(picker, HistoryToDatePicker))
        {
            viewModel.HistoryTo = text;
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
            if (IsCurrentCalendarButtonText(text, calendar))
            {
                text.Foreground = Brushes.Black;
            }
            else
            {
                text.SetResourceReference(
                    System.Windows.Controls.TextBlock.ForegroundProperty,
                    "TextFillColorPrimaryBrush");
            }
        }

        foreach (System.Windows.Controls.Button button in
                 FindVisualDescendants<System.Windows.Controls.Button>(calendar))
        {
            bool configured = _configuredCalendarButtons.TryGetValue(button, out _);
            if (configured && !force)
            {
                continue;
            }

            if (button.Name.Equals("PART_PreviousButton", StringComparison.Ordinal))
            {
                button.Tag = "Previous";
                ApplyCalendarButtonStyle(button, "CalendarNavigationButtonStyle");
                button.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
                button.Opacity = 1;
                AutomationProperties.SetName(button, "前の期間へ移動");
                button.ToolTip = "前の期間へ移動";
            }
            else if (button.Name.Equals("PART_NextButton", StringComparison.Ordinal))
            {
                button.Tag = "Next";
                ApplyCalendarButtonStyle(button, "CalendarNavigationButtonStyle");
                button.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
                button.Opacity = 1;
                AutomationProperties.SetName(button, "次の期間へ移動");
                button.ToolTip = "次の期間へ移動";
            }
            else if (button.Name.Equals("PART_HeaderButton", StringComparison.Ordinal))
            {
                ApplyCalendarButtonStyle(button, "CalendarHeaderButtonStyle");
                AutomationProperties.SetName(button, "表示期間を切り替え");
                button.ToolTip = "月、年、年代の表示を切り替え";
            }
            else
            {
                continue;
            }

            button.ApplyTemplate();
            button.InvalidateVisual();
            if (!configured)
            {
                _configuredCalendarButtons.Add(button, new object());
            }
        }
    }

    private void ApplyCalendarButtonStyle(System.Windows.Controls.Button button, string resourceKey)
    {
        Style style = (Style)FindResource(resourceKey);
        button.Style = style;
        Setter templateSetter = style.Setters
            .OfType<Setter>()
            .First(setter => setter.Property == Control.TemplateProperty);
        button.Template = (ControlTemplate)templateSetter.Value;
    }

    private static bool IsCurrentCalendarButtonText(
        System.Windows.Controls.TextBlock text,
        Calendar calendar)
    {
        CalendarButton? calendarButton = null;
        for (DependencyObject? current = VisualTreeHelper.GetParent(text);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is CalendarButton)
            {
                calendarButton = (CalendarButton)current;
                break;
            }

            if (ReferenceEquals(current, calendar))
            {
                break;
            }
        }

        if (calendarButton is null)
        {
            return false;
        }

        if (calendarButton.DataContext is DateTime date)
        {
            return calendar.DisplayMode switch
            {
                CalendarMode.Year => date.Year == calendar.DisplayDate.Year &&
                    date.Month == calendar.DisplayDate.Month,
                CalendarMode.Decade => date.Year == calendar.DisplayDate.Year,
                _ => false,
            };
        }

        return calendar.DisplayMode switch
        {
            CalendarMode.Year => text.Text.Equals($"{calendar.DisplayDate.Month}月", StringComparison.Ordinal),
            CalendarMode.Decade => text.Text.Equals(
                calendar.DisplayDate.Year.ToString(System.Globalization.CultureInfo.CurrentCulture),
                StringComparison.Ordinal),
            _ => false,
        };
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

    private void OnAnalyzerGridPreviewMouseRightButtonDown(
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
        if (row is null)
        {
            grid.UnselectAll();
            return;
        }

        grid.SelectedItem = row.Item;
        row.Focus();
    }

    private void OnAnalyzerTreePreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not TreeView tree)
        {
            return;
        }

        System.Windows.Controls.TreeViewItem? item =
            FindAncestor<System.Windows.Controls.TreeViewItem>(eventArgs.OriginalSource as DependencyObject);
        if (item is null)
        {
            System.Windows.Controls.TreeViewItem? selectedItem =
                FindVisualDescendants<System.Windows.Controls.TreeViewItem>(tree)
                .FirstOrDefault(candidate => candidate.IsSelected);
            if (selectedItem is not null)
            {
                selectedItem.IsSelected = false;
            }

            return;
        }

        item.IsSelected = true;
        item.Focus();
    }

    private void OnAnalyzerResultContextMenuOpening(object sender, ContextMenuEventArgs eventArgs)
    {
        bool hasSelection = sender switch
        {
            System.Windows.Controls.DataGrid grid => grid.SelectedItem is StatisticalMetric,
            TreeView tree => tree.SelectedItem is ResultTreeNode,
            _ => false,
        };
        eventArgs.Handled = !hasSelection;
    }

    private void OnCopyAnalyzerResultCanExecute(object sender, CanExecuteRoutedEventArgs eventArgs)
    {
        eventArgs.CanExecute = ResolveAnalyzerResultClipboardText(eventArgs.Source) is not null;
        eventArgs.Handled = true;
    }

    private void OnCopyAnalyzerResultExecuted(object sender, ExecutedRoutedEventArgs eventArgs)
    {
        string? text = ResolveAnalyzerResultClipboardText(eventArgs.Source);
        if (text is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"クリップボードを使用できませんでした。ほかのアプリが使用中でないことを確認して、もう一度お試しください。\n\n{exception.Message}",
                "コピーできません",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }

        eventArgs.Handled = true;
    }

    public static string? GetAnalyzerResultClipboardText(object? selectedItem) => selectedItem switch
    {
        StatisticalMetric metric => AnalyzerResultClipboardFormatter.Format(metric),
        ResultTreeNode { ClipboardText.Length: > 0 } node => node.ClipboardText,
        _ => null,
    };

    private string? ResolveAnalyzerResultClipboardText(object? commandSource)
    {
        if (IsSourceWithin(commandSource, AnalyzerGrid) || AnalyzerGrid.IsKeyboardFocusWithin)
        {
            return GetAnalyzerResultClipboardText(AnalyzerGrid.SelectedItem);
        }

        if (IsSourceWithin(commandSource, AnalyzerTreeView) || AnalyzerTreeView.IsKeyboardFocusWithin)
        {
            return GetAnalyzerResultClipboardText(AnalyzerTreeView.SelectedItem);
        }

        return null;
    }

    private static bool IsSourceWithin(object? source, DependencyObject ancestor) =>
        source is DependencyObject dependencyObject &&
        (ReferenceEquals(dependencyObject, ancestor) || FindAncestor<DependencyObject>(dependencyObject, ancestor));

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool FindAncestor<T>(DependencyObject source, T expectedAncestor)
        where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, expectedAncestor))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject source)
    {
        if (source is Visual)
        {
            return VisualTreeHelper.GetParent(source);
        }

        return source is FrameworkContentElement contentElement
            ? contentElement.Parent
            : LogicalTreeHelper.GetParent(source);
    }

    private static string[] GetDroppedPaths(DragEventArgs eventArgs) =>
        eventArgs.Data.GetDataPresent(DataFormats.FileDrop) &&
        eventArgs.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : Array.Empty<string>();
}

public sealed class WidthAdjustmentConverter : System.Windows.Data.IValueConverter
{
    public double Adjustment { get; set; }

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        System.Globalization.CultureInfo culture) =>
        value is double width ? Math.Max(0, width - Adjustment) : 0d;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

public sealed class WholeRowDataGridHeightConverter : System.Windows.Data.IValueConverter
{
    public double HeaderHeight { get; set; }

    public double RowHeight { get; set; }

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is not double availableHeight || !double.IsFinite(availableHeight))
        {
            return 0d;
        }

        if (
            HeaderHeight < 0 ||
            RowHeight <= 0 ||
            availableHeight <= HeaderHeight)
        {
            return Math.Max(0, availableHeight);
        }

        double visibleRows = Math.Floor((availableHeight - HeaderHeight) / RowHeight);
        return HeaderHeight + (visibleRows * RowHeight);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

public sealed class HorizontalScrollCompensatedHeightConverter : System.Windows.Data.IValueConverter
{
    public double BaseHeight { get; set; }

    public double RequiredWidth { get; set; }

    public double ScrollbarHeight { get; set; }

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        System.Globalization.CultureInfo culture) =>
        value is double availableWidth && availableWidth < RequiredWidth
            ? BaseHeight + ScrollbarHeight
            : BaseHeight;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
