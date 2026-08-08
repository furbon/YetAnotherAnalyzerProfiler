using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Yaap.Core;
using Yaap.Gui;
using ShapePath = System.Windows.Shapes.Path;

List<(string Name, Func<Task> Body)> tests = new()
{
    ("gui.viewmodel-initialization", ViewModelInitializationAsync),
    ("gui.window-startup-smoke", WindowStartupSmokeAsync),
    ("gui.drop-and-auto-discovery", DropAndAutoDiscoveryAsync),
    ("gui.recent-target-ordering", RecentTargetOrderingAsync),
    ("gui.configuration-priority", ConfigurationPriorityAsync),
    ("gui.configuration-history", ConfigurationHistoryAsync),
    ("gui.history-load-discards-stale-selection", HistoryLoadDiscardsStaleSelectionAsync),
    ("gui.cli-feature-parity", FeatureParityAsync),
    ("gui.result-tree-filtering", ResultTreeFilteringAsync),
    ("gui.result-tree-cancellation", ResultTreeCancellationAsync),
    ("gui.discovery-discards-stale-results", DiscoveryDiscardsStaleResultsAsync),
    ("gui.measurement-state", MeasurementStateAsync),
    ("gui.failure-observability", FailureObservabilityAsync),
    ("gui.theme-framework", ThemeFrameworkAsync),
    ("gui.async-command", AsyncCommandAsync),
    ("gui.shutdown-retry-after-child-exit", ShutdownRetryAfterChildExitAsync),
    ("gui.virtualization-and-generator-disclaimer", XamlContractAsync),
};

int failures = 0;
foreach ((string name, Func<Task> body) in tests)
{
    try
    {
        await body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

return failures == 0 ? 0 : 1;

static async Task WindowStartupSmokeAsync()
{
    string historyPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "yaap-gui-tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(historyPath);
    string recentTargetPath = System.IO.Path.Combine(historyPath, "RecentTarget.csproj");
    string longerRecentTargetPath = System.IO.Path.Combine(
        historyPath,
        "RecentTargetWithALongerName.csproj");
    await File.WriteAllTextAsync(
        recentTargetPath,
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
    await File.WriteAllTextAsync(
        longerRecentTargetPath,
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
    HistoryStore visualHistory = new(historyPath);
    ProfileRun optimizationBefore = CreateHistoricalRun(
        recentTargetPath,
        "Release",
        DateTimeOffset.UtcNow.AddDays(-1));
    optimizationBefore.Label = "最適化前";
    ProfileRun optimizationAfter = CreateHistoricalRun(
        recentTargetPath,
        "Release",
        DateTimeOffset.UtcNow.AddHours(-1));
    optimizationAfter.Label = "最適化後";
    await visualHistory.SaveAsync(optimizationBefore);
    await visualHistory.SaveAsync(optimizationAfter);
    for (int index = 0; index < 46; index++)
    {
        ProfileRun run = CreateHistoricalRun(
            recentTargetPath,
            index % 2 == 0 ? "Release" : "Debug",
            DateTimeOffset.UtcNow.AddHours(-index - 2));
        run.Label = index % 8 == 0 ? $"確認用 {index + 1}" : null;
        await visualHistory.SaveAsync(run);
    }
    TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Thread thread = new(() =>
    {
        App? app = null;
        try
        {
            using MainViewModel viewModel = new(
                targetDiscoveryDelay: TimeSpan.Zero,
                historyLoader: async (id, cancellationToken) =>
                {
                    await Task.Delay(80, cancellationToken);
                    return await visualHistory.LoadAsync(id, cancellationToken);
                })
            {
                HistoryPath = historyPath,
            };
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            SetPrivateProperty(viewModel, nameof(MainViewModel.SelectedRun), CreateVisualRun());
            viewModel.WaitForResultFilterAsync().GetAwaiter().GetResult();
            SetPrivateProperty(viewModel, nameof(MainViewModel.Comparison), CreateVisualComparison());
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            app = new App();
            app.InitializeComponent();
            MainWindow window = new(viewModel);
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            TabControl mainTabs = (TabControl)window.FindName("MainTabs");
            TabControl analyzerViewTabs = (TabControl)window.FindName("AnalyzerViewTabs");
            TabItem analyzerTableViewTab =
                analyzerViewTabs.ItemContainerGenerator.ContainerFromIndex(0) as TabItem ??
                throw new InvalidOperationException("The Analyzer table view tab was not rendered.");
            TabItem analyzerTreeViewTab =
                analyzerViewTabs.ItemContainerGenerator.ContainerFromIndex(1) as TabItem ??
                throw new InvalidOperationException("The Analyzer tree view tab was not rendered.");
            TreeView analyzerTreeView = (TreeView)window.FindName("AnalyzerTreeView");
            FrameworkElement targetCard = (FrameworkElement)window.FindName("TargetCard");
            FrameworkElement busyCard = (FrameworkElement)window.FindName("BusyCard");
            Wpf.Ui.Controls.InfoBar statusBar =
                (Wpf.Ui.Controls.InfoBar)window.FindName("StatusBar");
            Button startButton = (Button)window.FindName("StartButton");
            TextBlock busyTitle = (TextBlock)window.FindName("BusyTitle");
            TextBlock busyMessage = (TextBlock)window.FindName("BusyMessage");
            Button cancelButton = (Button)window.FindName("BusyCancelButton");
            Button inlineCancelButton = (Button)window.FindName("InlineCancelButton");
            ToggleButton recentTargetsButton =
                (ToggleButton)window.FindName("RecentTargetsButton");
            Popup recentTargetsPopup = (Popup)window.FindName("RecentTargetsPopup");
            FrameworkElement recentTargetsPopupContent = recentTargetsPopup.Child as FrameworkElement ??
                throw new InvalidOperationException("The recent-target popup content was not created.");
            ItemsControl recentTargetsItems = (ItemsControl)window.FindName("RecentTargetsItems");
            TextBlock recentTargetsEmptyMessage =
                (TextBlock)window.FindName("RecentTargetsEmptyMessage");
            ToggleButton advancedSettingsButton =
                (ToggleButton)window.FindName("AdvancedSettingsButton");
            Popup advancedSettingsPopup = (Popup)window.FindName("AdvancedSettingsPopup");
            FrameworkElement advancedSettingsPopupContent =
                advancedSettingsPopup.Child as FrameworkElement ??
                throw new InvalidOperationException("The advanced-settings popup content was not created.");
            CheckBox restoreCheckBox = (CheckBox)window.FindName("RestoreCheckBox");
            Wpf.Ui.Controls.SymbolIcon recentTargetsChevron =
                FindVisualDescendant<Wpf.Ui.Controls.SymbolIcon>(recentTargetsButton) ??
                throw new InvalidOperationException("The recent-target chevron was not rendered.");
            Wpf.Ui.Controls.SymbolIcon advancedSettingsIcon =
                FindVisualDescendant<Wpf.Ui.Controls.SymbolIcon>(advancedSettingsButton) ??
                throw new InvalidOperationException("The advanced-settings icon was not rendered.");
            DataGrid analyzerGrid = (DataGrid)window.FindName("AnalyzerGrid");
            Border analyzerTableSurface = (Border)window.FindName("AnalyzerTableSurface");
            Border analyzerTreeSurface = (Border)window.FindName("AnalyzerTreeSurface");
            Grid analyzerTreeHeader = (Grid)window.FindName("AnalyzerTreeHeader");
            TextBlock analyzerTableEmptyMessage =
                (TextBlock)window.FindName("AnalyzerTableEmptyMessage");
            TextBlock analyzerTreeEmptyMessage =
                (TextBlock)window.FindName("AnalyzerTreeEmptyMessage");
            DataGrid generatorGrid = (DataGrid)window.FindName("GeneratorGrid");
            DataGrid historyGrid = (DataGrid)window.FindName("HistoryGrid");
            DataGrid comparisonGrid = (DataGrid)window.FindName("ComparisonGrid");
            DataGrid diagnosticsGrid = (DataGrid)window.FindName("DiagnosticsGrid");
            TextBlock diagnosticActionText = (TextBlock)window.FindName("DiagnosticActionText");
            TextBox diagnosticDetailText = (TextBox)window.FindName("DiagnosticDetailText");
            DatePicker historyFromDatePicker =
                (DatePicker)window.FindName("HistoryFromDatePicker");
            DatePicker historyToDatePicker =
                (DatePicker)window.FindName("HistoryToDatePicker");
            FrameworkElement historyPeriodPanel =
                (FrameworkElement)window.FindName("HistoryPeriodPanel");
            Button historyRefreshButton =
                (Button)window.FindName("HistoryRefreshButton");
            Button historyPeriodClearButton =
                (Button)window.FindName("HistoryPeriodClearButton");
            Wpf.Ui.Controls.TextBox historyLabelTextBox =
                (Wpf.Ui.Controls.TextBox)window.FindName("HistoryLabelTextBox");
            TextBlock generatorOutputsTruncatedNotice =
                (TextBlock)window.FindName("GeneratorOutputsTruncatedNotice");
            TextBlock compareBaselineLabel = (TextBlock)window.FindName("CompareBaselineLabel");
            TextBlock exportPathLabel = (TextBlock)window.FindName("ExportPathLabel");
            TextBlock settingsTitle = (TextBlock)window.FindName("SettingsTitle");
            Ensure(
                ReferenceEquals(recentTargetsPopup.DataContext, viewModel),
                "The recent-target popup must bind to the window view model.");
            Ensure(
                ReferenceEquals(advancedSettingsPopup.DataContext, viewModel),
                "The advanced-settings popup must bind to the window view model.");
            Ensure(restoreCheckBox.IsChecked == true, "Restore must be enabled in the GUI by default.");
            viewModel.Restore = false;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Ensure(restoreCheckBox.IsChecked == false, "The restore control must update from the view model.");
            viewModel.Restore = true;
            Ensure(targetCard.ActualHeight < 80, "Collapsed advanced settings must not reserve a second row.");
            Ensure(
                recentTargetsChevron.Symbol == Wpf.Ui.Controls.SymbolRegular.ChevronDown16,
                "The recent-target button must use a Fluent down chevron.");
            Ensure(
                advancedSettingsIcon.Symbol == Wpf.Ui.Controls.SymbolRegular.Options20,
                "Advanced settings must use a compact Fluent options icon.");
            Ensure(
                analyzerTableSurface.BorderThickness == analyzerTreeSurface.BorderThickness &&
                analyzerTableSurface.BorderThickness.Left == 1,
                "Analyzer table and tree views must use the same visible result boundary.");
            Ensure(
                analyzerTableSurface.CornerRadius == analyzerTreeSurface.CornerRadius,
                "Analyzer result surfaces must use the same corner treatment.");
            Ensure(analyzerGrid.CanUserResizeColumns, "Analyzer table columns must remain resizable.");
            Ensure(analyzerGrid.SelectionUnit == DataGridSelectionUnit.FullRow, "Analyzer table selection must represent complete result items.");
            Ensure(analyzerGrid.ContextMenu is ContextMenu, "The Analyzer table must expose an item context menu.");
            Ensure(analyzerTreeView.ContextMenu is ContextMenu, "The Analyzer tree must expose an item context menu.");
            MenuItem analyzerGridCopyItem = (MenuItem)((ContextMenu)analyzerGrid.ContextMenu).Items[0];
            MenuItem analyzerTreeCopyItem = (MenuItem)((ContextMenu)analyzerTreeView.ContextMenu).Items[0];
            Ensure(
                analyzerGridCopyItem.Header?.ToString() == analyzerTreeCopyItem.Header?.ToString() &&
                analyzerGridCopyItem.Command == MainWindow.CopyAnalyzerResultCommand &&
                analyzerTreeCopyItem.Command == MainWindow.CopyAnalyzerResultCommand,
                "Analyzer table and tree context menus must expose the same copy action.");
            Ensure(
                MainWindow.CopyAnalyzerResultCommand.InputGestures.OfType<KeyGesture>().Any(
                    gesture => gesture.Key == Key.C && gesture.Modifiers == ModifierKeys.Control),
                "Analyzer copy must be discoverable through Ctrl+C.");
            DataGridCell analyzerMeanCell = GetDataGridCell(analyzerGrid, 0, 3);
            TextBlock analyzerMeanText = FindVisualDescendant<TextBlock>(analyzerMeanCell) ??
                throw new InvalidOperationException("The analyzer mean cell text was not rendered.");
            Ensure(analyzerMeanText.TextAlignment == TextAlignment.Right, "Timing values must be right-aligned.");
            Ensure(
                Typography.GetNumeralAlignment(analyzerMeanText) == FontNumeralAlignment.Tabular,
                "Timing values must use tabular numerals.");
            IReadOnlyList<DataGridColumnHeader> analyzerHeaders =
                FindVisualDescendants<DataGridColumnHeader>(analyzerGrid)
                    .Where(header => header.Column is not null)
                    .ToArray();
            Ensure(analyzerHeaders.Count == 6, "Every Analyzer table column must render a header.");
            Ensure(
                analyzerHeaders.All(header => header.BorderThickness.Right == 1 && header.BorderThickness.Bottom == 1),
                "Analyzer table headers must visibly separate every resize boundary.");
            Ensure(
                analyzerHeaders.All(header => header.FontWeight == FontWeights.SemiBold),
                "Analyzer table headers must use the same semibold emphasis as the tree headers.");
            Ensure(
                analyzerHeaders.All(header =>
                    FindVisualDescendants<Thumb>(header).Any(thumb => thumb.ActualWidth >= 8)),
                "Every Analyzer table header boundary must retain a practical resize target.");
            Ensure(
                analyzerHeaders.All(header => header.ToolTip?.ToString()?.Contains("列幅", StringComparison.Ordinal) == true),
                "Analyzer table headers must explain the drag-to-resize affordance.");
            analyzerGrid.SelectedIndex = 0;
            Ensure(
                MainWindow.CopyAnalyzerResultCommand.CanExecute(null, analyzerGrid),
                "The Analyzer table copy command must enable for a selected row.");
            analyzerMeanCell.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Right)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
            });
            Ensure(analyzerGrid.SelectedIndex == 0, "Right-clicking an Analyzer table cell must select its row.");
            analyzerGrid.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Right)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
            });
            Ensure(analyzerGrid.SelectedItem is null, "Right-clicking Analyzer table background must clear stale selection.");
            Ensure(analyzerGrid.Columns.Count == 6, "The Analyzer table must omit the low-value sample-count column.");
            Ensure(
                analyzerGrid.Columns.All(column => !string.Equals(column.Header?.ToString(), "標本", StringComparison.Ordinal)),
                "The Analyzer table must not present sample count as 標本.");
            Ensure(analyzerGrid.ActualHeight < 600, "The Analyzer table must be constrained to the tab viewport.");
            ScrollViewer analyzerScroll = FindVisualDescendants<ScrollViewer>(analyzerGrid)
                .OrderByDescending(viewer => viewer.ScrollableHeight)
                .FirstOrDefault() ??
                throw new InvalidOperationException("The Analyzer table scroll host was not rendered.");
            Ensure(analyzerScroll.ScrollableHeight > 0, "A large Analyzer table must have a vertical scroll range.");
            Ensure(
                analyzerScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                "A large Analyzer table must show its vertical scrollbar.");
            Ensure(
                analyzerScroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
                "The Analyzer table must not show a horizontal scrollbar at the normal window width.");
            EnsureAccessibleVerticalScrollBar(analyzerGrid, "Analyzer table");
            int realizedAnalyzerRows = FindVisualDescendants<DataGridRow>(analyzerGrid).Count();
            Ensure(
                realizedAnalyzerRows < analyzerGrid.Items.Count / 2,
                "The Analyzer table must virtualize rows instead of realizing the entire result set.");
            analyzerGrid.SelectedIndex = analyzerGrid.Items.Count - 1;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Ensure(analyzerScroll.VerticalOffset > 0, "Selecting an off-screen Analyzer must scroll it into view.");
            analyzerScroll.ScrollToTop();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            MouseWheelEventArgs wheel = new(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
            };
            analyzerScroll.RaiseEvent(wheel);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Ensure(analyzerScroll.VerticalOffset > 0, "The mouse wheel must scroll the Analyzer table.");
            Ensure(
                ReferenceEquals(viewModel.Analyzers, viewModel.Analyzers),
                "Unchanged Analyzer projections must be cached across tab switches.");
            mainTabs.SelectedIndex = 1;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            ScrollViewer generatorScroll = FindVisualDescendants<ScrollViewer>(generatorGrid)
                .OrderByDescending(viewer => viewer.ScrollableHeight)
                .FirstOrDefault() ??
                throw new InvalidOperationException("The Generator table scroll host was not rendered.");
            Ensure(generatorScroll.ScrollableHeight > 0, "A large Generator table must have a vertical scroll range.");
            Ensure(
                generatorScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                "A large Generator table must show its vertical scrollbar.");
            Ensure(
                FindVisualDescendants<DataGridRow>(generatorGrid).Count() < generatorGrid.Items.Count / 2,
                "The Generator table must virtualize rows.");
            mainTabs.SelectedIndex = 0;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            mainTabs.SelectedIndex = 2;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Ensure(viewModel.History.Count == 48, "The visual history fixture must exercise a large list.");
            ScrollViewer historyScroll = FindVisualDescendants<ScrollViewer>(historyGrid)
                .OrderByDescending(viewer => viewer.ScrollableHeight)
                .FirstOrDefault() ??
                throw new InvalidOperationException("The History table scroll host was not rendered.");
            Ensure(historyScroll.ScrollableHeight > 0, "A large History table must have a vertical scroll range.");
            EnsureAccessibleVerticalScrollBar(historyGrid, "History table");
            historyGrid.SelectedIndex = 24;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            historyScroll.ScrollToVerticalOffset(Math.Min(320, historyScroll.ScrollableHeight));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            double historyHeightBeforeLoad = historyGrid.ActualHeight;
            double historyOffsetBeforeLoad = historyScroll.VerticalOffset;
            object historySelectionBeforeLoad = historyGrid.SelectedItem;
            int historyCountBeforeLoad = historyGrid.Items.Count;
            historyGrid.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
            });
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Ensure(viewModel.IsOperationRunning, "History loading must remain an observable asynchronous operation.");
            Ensure(viewModel.StatusTitleText == "処理中", "Inline history loading must not retain a previous run outcome heading.");
            Ensure(viewModel.StatusSeverity == Wpf.Ui.Controls.InfoBarSeverity.Informational, "Inline history loading must use informational severity.");
            Ensure(inlineCancelButton.Visibility == Visibility.Visible, "History loading must expose an inline cancel action.");
            Ensure(inlineCancelButton.IsEnabled, "The inline cancel action must be enabled while loading history.");
            Ensure(!viewModel.IsBusySurfaceVisible, "History loading must not replace the list with the global busy surface.");
            Ensure(mainTabs.IsEnabled, "History loading must not disable or fade the tab content.");
            Ensure(busyCard.Visibility == Visibility.Collapsed, "History loading must not insert the global busy card.");
            Ensure(Math.Abs(historyGrid.ActualHeight - historyHeightBeforeLoad) < 0.5, "History loading must not resize the list.");
            Ensure(Math.Abs(historyScroll.VerticalOffset - historyOffsetBeforeLoad) < 0.5, "History loading must not move the list.");
            PumpUntil(window.Dispatcher, () => !viewModel.IsOperationRunning, TimeSpan.FromSeconds(5));
            Ensure(historyGrid.Items.Count == historyCountBeforeLoad, "History loading must not rebuild the history collection.");
            Ensure(ReferenceEquals(historyGrid.SelectedItem, historySelectionBeforeLoad), "History loading must preserve selection.");
            Ensure(Math.Abs(historyScroll.VerticalOffset - historyOffsetBeforeLoad) < 0.5, "History loading must preserve scroll position.");
            SetPrivateProperty(viewModel, nameof(MainViewModel.SelectedRun), CreateVisualRun());
            Task resultProjection = viewModel.WaitForResultFilterAsync();
            PumpUntil(window.Dispatcher, () => resultProjection.IsCompleted, TimeSpan.FromSeconds(5));
            resultProjection.GetAwaiter().GetResult();
            mainTabs.SelectedIndex = 0;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            EnsureAccessibleVerticalScrollBar(analyzerGrid, "Analyzer table after result reload");
            Ensure(startButton.FontWeight == FontWeights.SemiBold, "The primary measurement action must use emphasized text.");
            Ensure(busyCard.Visibility == Visibility.Collapsed, "The measurement progress surface must be hidden while idle.");
            Ensure(statusBar.Visibility == Visibility.Visible, "The persistent status bar must be visible while idle.");
            recentTargetsButton.IsChecked = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Ensure(recentTargetsPopup.IsOpen, "The empty recent-target popup should still open.");
            Ensure(
                recentTargetsEmptyMessage.Visibility == Visibility.Collapsed,
                "The recent-target empty state must be hidden when history provides an item.");
            recentTargetsButton.IsChecked = false;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            SetPrivateProperty(viewModel, nameof(MainViewModel.StatusText), "コンパイラー情報 1/3 を逐次解析しています。");
            SetPrivateProperty(viewModel, nameof(MainViewModel.IsRunning), true);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Ensure(!targetCard.IsEnabled, "The target controls must be disabled while measuring.");
            Ensure(!mainTabs.IsEnabled, "Result and history tabs must be disabled while measuring.");
            Ensure(busyCard.Visibility == Visibility.Visible, "The measurement progress surface must be visible.");
            Ensure(statusBar.Visibility == Visibility.Collapsed, "The persistent status bar must not duplicate running progress.");
            Ensure(
                busyTitle.Text == viewModel.BusyTitleText,
                "The busy heading must use the canonical measurement state text.");
            Ensure(busyMessage.Text == viewModel.StatusText, "The busy surface must show the current progress message.");
            Ensure(cancelButton.IsEnabled, "Cancel must remain enabled while measuring.");
            viewModel.RecentTargets.Add(new RecentTarget(
                System.IO.Path.GetFileName(longerRecentTargetPath),
                longerRecentTargetPath,
                DateTimeOffset.UtcNow.AddSeconds(-1)));

            string? captureDirectory = Environment.GetEnvironmentVariable("YAAP_GUI_CAPTURE_DIR");
            foreach (AppThemeMode mode in new[] { AppThemeMode.Light, AppThemeMode.Dark })
            {
                viewModel.SelectedTheme = MainViewModel.ThemeOptions.Single(option => option.Mode == mode);
                mainTabs.SelectedIndex = 0;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                EnsureReadableForeground(mainTabs.Foreground, mode, "MainTabs");
                CaptureWindow(window, captureDirectory, $"{mode.ToString().ToLowerInvariant()}-busy");

                SetPrivateProperty(viewModel, nameof(MainViewModel.IsRunning), false);
                viewModel.LoadSelectedCommand.Execute(null);
                PumpUntil(window.Dispatcher, () => viewModel.IsOperationRunning, TimeSpan.FromSeconds(2));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                Ensure(inlineCancelButton.Visibility == Visibility.Visible, "The inline cancel action must render in both themes.");
                CaptureWindow(window, captureDirectory, $"{mode.ToString().ToLowerInvariant()}-history-loading");
                viewModel.CancelCommand.Execute(null);
                PumpUntil(window.Dispatcher, () => !viewModel.IsOperationRunning, TimeSpan.FromSeconds(2));
                if (!viewModel.TargetPath.Equals(recentTargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.TargetPath = recentTargetPath;
                }

                Task customTargetDiscovery = viewModel.WaitForTargetDiscoveryAsync();
                PumpUntil(
                    window.Dispatcher,
                    () => customTargetDiscovery.IsCompleted,
                    TimeSpan.FromSeconds(5));
                customTargetDiscovery.GetAwaiter().GetResult();
                viewModel.Configuration = string.Empty;
                viewModel.Configuration = "CustomProfile";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Ensure(viewModel.StartCommand.CanExecute(null), "The rendered custom configuration must remain actionable.");
                Ensure(
                    viewModel.MeasurementStateText.Contains("未検出", StringComparison.Ordinal),
                    "The rendered custom configuration must explain that it was not detected.");
                Ensure(
                    viewModel.StatusTitleText.Contains("未検出", StringComparison.Ordinal),
                    "The custom-configuration warning must replace stale operation status.");
                CaptureWindow(
                    window,
                    captureDirectory,
                    $"{mode.ToString().ToLowerInvariant()}-custom-configuration");
                viewModel.Configuration = "Release";
                SetPrivateProperty(viewModel, nameof(MainViewModel.SelectedRun), CreateVisualFailureRun(RunStatus.Failed));
                Task failedProjection = viewModel.WaitForResultFilterAsync();
                PumpUntil(window.Dispatcher, () => failedProjection.IsCompleted, TimeSpan.FromSeconds(5));
                failedProjection.GetAwaiter().GetResult();
                SetPrivateProperty(
                    viewModel,
                    nameof(MainViewModel.StatusText),
                    "測定に失敗しました: sample.csproj。YAAP2001: 測定前の dotnet clean に失敗しました。原因ログと対処は「トラブルシュート」タブで確認できます。");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Ensure(viewModel.StatusTitleText == "測定失敗", "The rendered failure heading must be Japanese.");
                Ensure(
                    statusBar.Severity == Wpf.Ui.Controls.InfoBarSeverity.Error,
                    "A failed measurement must render an error status surface.");
                for (int index = 0; index < mainTabs.Items.Count; index++)
                {
                    mainTabs.SelectedIndex = index;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    TabItem selectedTab = (TabItem)mainTabs.ItemContainerGenerator.ContainerFromIndex(index);
                    Border tabBorder = (Border)selectedTab.Template.FindName("TabBorder", selectedTab);
                    ContentPresenter headerPresenter =
                        (ContentPresenter)selectedTab.Template.FindName("HeaderPresenter", selectedTab);
                    EnsureContrast(
                        TextElement.GetForeground(headerPresenter),
                        tabBorder.Background,
                        $"{mode} main tab {index + 1}");
                    if (index == 3)
                    {
                        EnsureReadableForeground(compareBaselineLabel.Foreground, mode, "CompareBaselineLabel");
                    }
                    else if (index == 4)
                    {
                        EnsureReadableForeground(exportPathLabel.Foreground, mode, "ExportPathLabel");
                    }
                    else if (index == 6)
                    {
                        EnsureReadableForeground(settingsTitle.Foreground, mode, "SettingsTitle");
                    }

                    if (index == 1)
                    {
                        generatorGrid.SelectedIndex = 0;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        Ensure(
                            generatorOutputsTruncatedNotice.Visibility == Visibility.Visible,
                            "A truncated generated-output preview must direct users to full export.");
                    }
                    else if (index == 2)
                    {
                        historyGrid.SelectedIndex = 0;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        double labelEditorLeft = historyLabelTextBox
                            .TransformToAncestor(window)
                            .Transform(new Point()).X;
                        double historyGridLeft = historyGrid
                            .TransformToAncestor(window)
                            .Transform(new Point()).X;
                        Ensure(
                            labelEditorLeft - historyGridLeft < 180,
                            "The selected-history label editor must align with the left-side label column.");
                        double periodRight = historyPeriodPanel
                            .TransformToAncestor(window)
                            .Transform(new Point(historyPeriodPanel.ActualWidth, 0)).X;
                        double refreshLeft = historyRefreshButton
                            .TransformToAncestor(window)
                            .Transform(new Point()).X;
                        Ensure(
                            refreshLeft - periodRight >= 20,
                            "History refresh must be visually separated from the period controls.");
                    }

                    if (index == 0)
                    {
                        EnsureAccessibleVerticalScrollBar(analyzerGrid, $"{mode} Analyzer table");
                        EnsureAnalyzerViewTabState(
                            analyzerTableViewTab,
                            analyzerTreeViewTab,
                            $"{mode} Analyzer table view tab");
                        analyzerGrid.SelectedIndex = 0;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        RenderTargetBitmap analyzerTableBitmap = RenderElement(window);
                        DataGridColumnHeader analyzerFirstHeader = analyzerHeaders[0];
                        Color analyzerTableHeaderFill = GetRenderedPixel(
                            analyzerTableBitmap,
                            window,
                            analyzerFirstHeader,
                            new Point(analyzerFirstHeader.ActualWidth - 24, analyzerFirstHeader.ActualHeight / 2));
                        Color analyzerTableHeaderSeparator = GetMostContrastingRenderedPixel(
                            analyzerTableBitmap,
                            window,
                            analyzerFirstHeader,
                            analyzerTableHeaderFill,
                            analyzerFirstHeader.ActualWidth - 1,
                            analyzerFirstHeader.ActualHeight / 2,
                            horizontalRadius: 2);
                        EnsureContrast(
                            analyzerFirstHeader.Foreground,
                            new SolidColorBrush(analyzerTableHeaderFill),
                            $"{mode} Analyzer table header");
                        Ensure(
                            ColorDistance(analyzerTableHeaderFill, analyzerTableHeaderSeparator) >= 28,
                            $"{mode} Analyzer table header separators must be visibly distinct " +
                            $"(fill {FormatColor(analyzerTableHeaderFill)}, separator {FormatColor(analyzerTableHeaderSeparator)}).");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-table-selected");
                        ContextMenu analyzerTableMenu = analyzerGrid.ContextMenu ??
                            throw new InvalidOperationException("The Analyzer table context menu was not created.");
                        analyzerTableMenu.PlacementTarget = analyzerGrid;
                        analyzerTableMenu.IsOpen = true;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Ensure(analyzerTableMenu.IsOpen, "The Analyzer table context menu must open for a selection.");
                        CaptureElement(
                            analyzerTableMenu,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-table-context-menu");
                        analyzerTableMenu.IsOpen = false;
                        double analyzerNormalWidth = window.Width;
                        window.Width = window.MinWidth;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Ensure(
                            analyzerScroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
                            "A narrow Analyzer table must allow horizontally scrolling resized columns.");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-table-narrow");
                        window.Width = analyzerNormalWidth;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Ensure(
                            analyzerScroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
                            "Restoring the Analyzer table width must remove unnecessary horizontal scrolling.");
                        ScrollBar analyzerBar = FindVisualDescendants<ScrollBar>(analyzerGrid)
                            .Where(item => item.Orientation == Orientation.Vertical && item.IsVisible)
                            .OrderByDescending(item => item.ActualHeight)
                            .First();
                        CaptureElement(
                            analyzerBar.Track!.Thumb,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-scroll-thumb");
                        analyzerViewTabs.SelectedIndex = 1;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        analyzerTreeView.UpdateLayout();
                        TreeViewItem analyzerAssembly =
                            analyzerTreeView.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem ??
                            throw new InvalidOperationException("The Analyzer tree root was not rendered.");
                        analyzerAssembly.IsExpanded = true;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        analyzerAssembly.UpdateLayout();
                        TreeViewItem analyzerLeaf =
                            analyzerAssembly.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem ??
                            throw new InvalidOperationException("The Analyzer tree leaf was not rendered.");
                        ResultTreeNode analyzerLeafNode = analyzerLeaf.DataContext as ResultTreeNode ??
                            throw new InvalidOperationException("The Analyzer tree leaf data was not retained.");
                        Ensure(analyzerLeafNode.IsAnalyzerMetric, "Analyzer tree leaves must carry metric columns.");
                        IReadOnlyList<TextBlock> analyzerLeafTexts =
                            FindVisualDescendants<TextBlock>(analyzerLeaf).ToArray();
                        TextBlock analyzerTreeMeanText = analyzerLeafTexts.Single(text => text.Text == "10.000");
                        TextBlock analyzerTreeMinimumText = analyzerLeafTexts.Single(text => text.Text == "9.000");
                        TextBlock analyzerTreeMaximumText = analyzerLeafTexts.Single(text => text.Text == "11.000");
                        foreach (TextBlock timingText in new[]
                                 {
                                     analyzerTreeMeanText,
                                     analyzerTreeMinimumText,
                                     analyzerTreeMaximumText,
                                 })
                        {
                            Ensure(timingText.TextAlignment == TextAlignment.Right, "Analyzer tree timings must be right-aligned.");
                            Ensure(
                                Typography.GetNumeralAlignment(timingText) == FontNumeralAlignment.Tabular,
                                "Analyzer tree timings must use tabular numerals.");
                            EnsureReadableForeground(timingText.Foreground, mode, $"{mode} Analyzer tree timing");
                        }

                        IReadOnlyList<Border> analyzerTreeHeaderCells =
                            analyzerTreeHeader.Children.OfType<Border>().ToArray();
                        Ensure(analyzerTreeHeaderCells.Count == 5, "The Analyzer tree must expose all metric headers.");
                        IReadOnlyList<TextBlock> analyzerTreeHeaderTexts = analyzerTreeHeaderCells
                            .Select(cell => FindVisualDescendant<TextBlock>(cell) ??
                                throw new InvalidOperationException("An Analyzer tree header label was not rendered."))
                            .ToArray();
                        Ensure(
                            analyzerTreeHeaderTexts.All(text => text.FontWeight == FontWeights.SemiBold),
                            "Analyzer tree headers must use the shared semibold emphasis.");
                        EnsureAnalyzerViewTabState(
                            analyzerTreeViewTab,
                            analyzerTableViewTab,
                            $"{mode} Analyzer tree view tab");
                        RenderTargetBitmap analyzerTreeBitmap = RenderElement(window);
                        Border analyzerFirstTreeHeader = analyzerTreeHeaderCells[0];
                        Color analyzerTreeHeaderFill = GetRenderedPixel(
                            analyzerTreeBitmap,
                            window,
                            analyzerFirstTreeHeader,
                            new Point(analyzerFirstTreeHeader.ActualWidth / 2, analyzerFirstTreeHeader.ActualHeight / 2));
                        Color analyzerTreeHeaderSeparator = GetMostContrastingRenderedPixel(
                            analyzerTreeBitmap,
                            window,
                            analyzerFirstTreeHeader,
                            analyzerTreeHeaderFill,
                            analyzerFirstTreeHeader.ActualWidth - 1,
                            analyzerFirstTreeHeader.ActualHeight / 2,
                            horizontalRadius: 2);
                        EnsureContrast(
                            analyzerTreeHeaderTexts[0].Foreground,
                            new SolidColorBrush(analyzerTreeHeaderFill),
                            $"{mode} Analyzer tree header");
                        Ensure(
                            ColorDistance(analyzerTableHeaderFill, analyzerTreeHeaderFill) <= 3,
                            $"{mode} Analyzer table and tree header fills must render identically " +
                            $"({FormatColor(analyzerTableHeaderFill)} vs {FormatColor(analyzerTreeHeaderFill)}).");
                        Ensure(
                            ColorDistance(analyzerTreeHeaderFill, analyzerTreeHeaderSeparator) >= 28,
                            $"{mode} Analyzer tree header separators must be visibly distinct " +
                            $"(fill {FormatColor(analyzerTreeHeaderFill)}, separator {FormatColor(analyzerTreeHeaderSeparator)}).");
                        TextBlock[] analyzerTreeTimingTexts =
                        {
                            analyzerTreeMeanText,
                            analyzerTreeMinimumText,
                            analyzerTreeMaximumText,
                        };
                        for (int timingIndex = 0; timingIndex < analyzerTreeTimingTexts.Length; timingIndex++)
                        {
                            Border headerCell = analyzerTreeHeaderCells[timingIndex + 2];
                            TextBlock timingText = analyzerTreeTimingTexts[timingIndex];
                            double headerRight = headerCell.TransformToAncestor(window)
                                .Transform(new Point(headerCell.ActualWidth, 0)).X;
                            double valueRight = timingText.TransformToAncestor(window)
                                .Transform(new Point(timingText.ActualWidth, 0)).X;
                            Ensure(
                                Math.Abs(headerRight - valueRight) < 3,
                                $"Analyzer tree timing values must align with their headers ({headerRight:F1} vs {valueRight:F1}).");
                        }

                        analyzerLeaf.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Right)
                        {
                            RoutedEvent = Mouse.PreviewMouseDownEvent,
                        });
                        Ensure(
                            ReferenceEquals(analyzerTreeView.SelectedItem, analyzerLeafNode),
                            "Right-clicking an Analyzer tree node must select that node.");
                        Ensure(
                            MainWindow.CopyAnalyzerResultCommand.CanExecute(null, analyzerTreeView),
                            "The Analyzer tree copy command must enable for a selected node.");
                        ContextMenu analyzerTreeMenu = analyzerTreeView.ContextMenu ??
                            throw new InvalidOperationException("The Analyzer tree context menu was not created.");
                        analyzerTreeMenu.PlacementTarget = analyzerTreeView;
                        analyzerTreeMenu.IsOpen = true;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Ensure(analyzerTreeMenu.IsOpen, "The Analyzer tree context menu must open for a selection.");
                        CaptureElement(
                            analyzerTreeMenu,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-tree-context-menu");
                        analyzerTreeMenu.IsOpen = false;
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-tree-selected");
                        window.Width = window.MinWidth;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-tree-narrow");
                        window.Width = analyzerNormalWidth;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        ScrollViewer treeScroll = FindVisualDescendants<ScrollViewer>(analyzerTreeView)
                            .OrderByDescending(viewer => viewer.ScrollableHeight)
                            .FirstOrDefault() ??
                            throw new InvalidOperationException("The Analyzer tree scroll host was not rendered.");
                        Ensure(treeScroll.ScrollableHeight > 0, "A large Analyzer tree must have a vertical scroll range.");
                        Ensure(
                            treeScroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
                            "The Analyzer tree must fit its metric columns without horizontal scrolling.");
                        Ensure(
                            treeScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                            "A large Analyzer tree must show its vertical scrollbar.");
                        Ensure(
                            FindVisualDescendants<TreeViewItem>(analyzerTreeView).Count() < 300,
                            "The Analyzer tree must virtualize expanded children.");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-tree");
                        analyzerTreeView.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Right)
                        {
                            RoutedEvent = Mouse.PreviewMouseDownEvent,
                        });
                        Ensure(
                            analyzerTreeView.SelectedItem is null,
                            "Right-clicking Analyzer tree background must clear stale selection.");
                        viewModel.ResultFilter = "__YAAP_NO_MATCH__";
                        Task emptyProjection = viewModel.WaitForResultFilterAsync();
                        PumpUntil(window.Dispatcher, () => emptyProjection.IsCompleted, TimeSpan.FromSeconds(5));
                        emptyProjection.GetAwaiter().GetResult();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Ensure(
                            analyzerTreeEmptyMessage.Visibility == Visibility.Visible,
                            "The Analyzer tree must explain an empty result.");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-tree-empty");
                        analyzerViewTabs.SelectedIndex = 0;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Ensure(
                            analyzerTableEmptyMessage.Visibility == Visibility.Visible,
                            "The Analyzer table must explain an empty result.");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-table-empty");
                        viewModel.ResultFilter = string.Empty;
                        Task restoredProjection = viewModel.WaitForResultFilterAsync();
                        PumpUntil(window.Dispatcher, () => restoredProjection.IsCompleted, TimeSpan.FromSeconds(5));
                        restoredProjection.GetAwaiter().GetResult();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    }
                    else if (index == 1)
                    {
                        EnsureAccessibleVerticalScrollBar(generatorGrid, $"{mode} Generator table");
                    }
                    else if (index == 2 && historyGrid.ContextMenu is ContextMenu historyMenu)
                    {
                        Ensure(
                            !historyPeriodClearButton.IsEnabled,
                            "An empty history period must start with a disabled clear action.");
                        ButtonAutomationPeer clearPeer = new(historyPeriodClearButton);
                        historyToDatePicker.SelectedDate = DateTime.Today;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        Ensure(
                            MainViewModel.TryParseHistoryDateText(viewModel.HistoryTo, out DateTime calendarOnlyTo) &&
                            calendarOnlyTo.Date == DateTime.Today,
                            "A calendar-only selection must update the history end date.");
                        Ensure(
                            historyPeriodClearButton.IsEnabled,
                            "A calendar-only selection must immediately enable period clearing.");
                        ((IInvokeProvider)clearPeer.GetPattern(PatternInterface.Invoke)).Invoke();
                        PumpUntil(
                            window.Dispatcher,
                            () => viewModel.HistoryFrom.Length == 0 && viewModel.HistoryTo.Length == 0,
                            TimeSpan.FromSeconds(2));
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        Ensure(
                            !historyPeriodClearButton.IsEnabled,
                            "Clearing a calendar-only selection must disable the action again.");

                        string typedFrom = DateTime.Today.AddDays(-2).ToString(
                            "yyyy/MM/dd",
                            System.Globalization.CultureInfo.InvariantCulture);
                        historyFromDatePicker.ApplyTemplate();
                        TextBox historyFromTextBox =
                            (TextBox)historyFromDatePicker.Template.FindName(
                                "PART_TextBox",
                                historyFromDatePicker);
                        historyFromTextBox.Text = typedFrom;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        Ensure(
                            viewModel.HistoryFrom.Equals(typedFrom, StringComparison.Ordinal),
                            "History start text must update its source before focus changes.");
                        Ensure(
                            historyPeriodClearButton.IsEnabled,
                            "Text-only history input must immediately enable period clearing.");
                        historyToDatePicker.SelectedDate = DateTime.Today;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        Ensure(
                            MainViewModel.TryParseHistoryDateText(viewModel.HistoryTo, out DateTime selectedTo) &&
                            selectedTo.Date == DateTime.Today,
                            "History calendar selection must update its source before focus changes.");
                        Task visualPeriodRefresh = viewModel.WaitForHistoryRefreshAsync();
                        PumpUntil(window.Dispatcher, () => visualPeriodRefresh.IsCompleted, TimeSpan.FromSeconds(5));
                        visualPeriodRefresh.GetAwaiter().GetResult();
                        Ensure(historyPeriodClearButton.IsEnabled, "A populated history period must expose an enabled clear action.");
                        historyPeriodClearButton.ApplyTemplate();
                        ShapePath clearIcon = FindVisualDescendant<ShapePath>(historyPeriodClearButton) ??
                            throw new InvalidOperationException("The history period clear icon was not rendered.");
                        EnsureCenteredWithin(clearIcon, historyPeriodClearButton, 1.0, "History period clear icon");
                        EnsureTemplateInteractionTriggers(
                            historyPeriodClearButton.Template,
                            "History period clear button");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-history-period-filled");
                        SetReadOnlyBooleanState(
                            historyPeriodClearButton,
                            typeof(UIElement),
                            "IsMouseOverPropertyKey",
                            true);
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-history-clear-hover");
                        SetReadOnlyBooleanState(
                            historyPeriodClearButton,
                            typeof(ButtonBase),
                            "IsPressedPropertyKey",
                            true);
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                        Border clearSurface = FindVisualDescendants<Border>(historyPeriodClearButton)
                            .First(item => item.Name.Equals("ActionSurface", StringComparison.Ordinal));
                        EnsureContrast(
                            clearIcon.Stroke,
                            clearSurface.Background,
                            $"{mode} history period clear pressed state");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-history-clear-pressed");
                        SetReadOnlyBooleanState(
                            historyPeriodClearButton,
                            typeof(ButtonBase),
                            "IsPressedPropertyKey",
                            false);
                        SetReadOnlyBooleanState(
                            historyPeriodClearButton,
                            typeof(UIElement),
                            "IsMouseOverPropertyKey",
                            false);
                        SetReadOnlyBooleanState(
                            historyPeriodClearButton,
                            typeof(UIElement),
                            "IsKeyboardFocusedPropertyKey",
                            true);
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-history-clear-focus");
                        SetReadOnlyBooleanState(
                            historyPeriodClearButton,
                            typeof(UIElement),
                            "IsKeyboardFocusedPropertyKey",
                            false);
                        ((IInvokeProvider)clearPeer.GetPattern(PatternInterface.Invoke)).Invoke();
                        PumpUntil(
                            window.Dispatcher,
                            () => viewModel.HistoryFrom.Length == 0 && viewModel.HistoryTo.Length == 0,
                            TimeSpan.FromSeconds(2));
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        Ensure(
                            historyFromDatePicker.Text.Length == 0 && historyToDatePicker.Text.Length == 0,
                            "The rendered history clear button must clear both date controls.");
                        Task visualPeriodClear = viewModel.WaitForHistoryRefreshAsync();
                        PumpUntil(window.Dispatcher, () => visualPeriodClear.IsCompleted, TimeSpan.FromSeconds(5));
                        visualPeriodClear.GetAwaiter().GetResult();
                        Ensure(!historyPeriodClearButton.IsEnabled, "An empty history period must disable its clear action.");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-history-clear-disabled");

                        historyMenu.PlacementTarget = historyGrid;
                        historyMenu.IsOpen = true;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        CaptureElement(
                            historyMenu,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-history-context-menu");
                        historyMenu.IsOpen = false;
                        historyFromDatePicker.IsDropDownOpen = true;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Popup calendarPopup =
                            (Popup)historyFromDatePicker.Template.FindName("PART_Popup", historyFromDatePicker);
                        FrameworkElement calendarPopupContent = calendarPopup.Child as FrameworkElement ??
                            throw new InvalidOperationException("The history calendar popup content was not created.");
                        Calendar calendar = calendarPopupContent as Calendar ??
                            FindVisualDescendant<Calendar>(calendarPopupContent) ??
                            throw new InvalidOperationException("The history calendar was not rendered.");
                        Ensure(
                            calendar.Background is SolidColorBrush { Color.A: byte.MaxValue },
                            "The history calendar background must be fully opaque.");
                        foreach ((CalendarMode displayMode, string suffix) in new[]
                                 {
                                     (CalendarMode.Month, "month"),
                                     (CalendarMode.Year, "year"),
                                     (CalendarMode.Decade, "decade"),
                                 })
                        {
                            calendar.DisplayMode = displayMode;
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                            Button previousButton = FindVisualDescendants<Button>(calendar)
                                .FirstOrDefault(item => item.Name.Equals("PART_PreviousButton", StringComparison.Ordinal)) ??
                                throw new InvalidOperationException("The previous calendar navigation button was not rendered.");
                            Button nextButton = FindVisualDescendants<Button>(calendar)
                                .FirstOrDefault(item => item.Name.Equals("PART_NextButton", StringComparison.Ordinal)) ??
                                throw new InvalidOperationException("The next calendar navigation button was not rendered.");
                            Button headerButton = FindVisualDescendants<Button>(calendar)
                                .FirstOrDefault(item => item.Name.Equals("PART_HeaderButton", StringComparison.Ordinal)) ??
                                throw new InvalidOperationException("The calendar header button was not rendered.");
                            foreach ((Button button, string automationName) in new[]
                                     {
                                         (previousButton, "前の期間へ移動"),
                                         (nextButton, "次の期間へ移動"),
                                     })
                            {
                                button.ApplyTemplate();
                                ShapePath glyph = FindVisualDescendants<ShapePath>(button)
                                    .FirstOrDefault(item => item.Name.Equals("CalendarNavigationGlyph", StringComparison.Ordinal)) ??
                                    throw new InvalidOperationException(
                                        "A calendar navigation glyph was not rendered. " +
                                        $"Name={button.Name}; Style={button.Style}; Template={button.Template}; " +
                                        $"LocalStyle={button.ReadLocalValue(FrameworkElement.StyleProperty)}; " +
                                        $"ExpectedStyle={ReferenceEquals(button.Style, window.FindResource("CalendarNavigationButtonStyle"))}; " +
                                        $"LocalTemplate={button.ReadLocalValue(Control.TemplateProperty)}; " +
                                        $"Content={button.Content?.GetType().FullName ?? "<null>"}.");
                                Ensure(button.ActualWidth >= 31 && button.ActualHeight >= 31,
                                    "Calendar navigation buttons must provide a practical pointer target.");
                                EnsureCenteredWithin(glyph, button, 1.0, automationName);
                                EnsureContrast(glyph.Stroke, calendar.Background, $"{mode} {suffix} {automationName}");
                                Ensure(
                                    AutomationProperties.GetName(button).Equals(automationName, StringComparison.Ordinal),
                                    $"Calendar navigation automation name was not configured: {automationName}.");
                                EnsureTemplateInteractionTriggers(button.Template, automationName);
                            }

                            Ensure(
                                AutomationProperties.GetName(headerButton).Equals("表示期間を切り替え", StringComparison.Ordinal),
                                "The calendar header must explain its display-mode action.");
                            EnsureTemplateInteractionTriggers(headerButton.Template, "Calendar header button");
                            foreach (TextBlock calendarText in
                                     FindVisualDescendants<TextBlock>(calendarPopupContent)
                                         .Where(item => item.IsVisible && !string.IsNullOrWhiteSpace(item.Text)))
                            {
                                EnsureContrast(
                                    calendarText.Foreground,
                                    GetCalendarTextBackground(calendarText, calendar),
                                    $"{mode} history {suffix} calendar text '{calendarText.Text}'");
                            }

                            CaptureElement(
                                calendarPopupContent,
                                captureDirectory,
                                $"{mode.ToString().ToLowerInvariant()}-history-calendar-{suffix}");

                            ButtonBase calendarItem = displayMode == CalendarMode.Month
                                ? FindVisualDescendants<CalendarDayButton>(calendar)
                                    .First(item => item.IsVisible && item.IsEnabled && item.IsSelected == false)
                                : FindVisualDescendants<CalendarButton>(calendar)
                                    .First(item => item.IsVisible && item.IsEnabled);
                            SetReadOnlyBooleanState(
                                calendarItem,
                                typeof(UIElement),
                                "IsMouseOverPropertyKey",
                                true);
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                            Ensure(
                                calendarItem.Background is SolidColorBrush,
                                $"{mode} {suffix} calendar items must expose visible hover feedback.");
                            Ensure(
                                calendarItem.BorderBrush is SolidColorBrush && calendarItem.BorderThickness.Left >= 2,
                                $"{mode} {suffix} calendar item hover state must expose an accent outline.");
                            CaptureElement(
                                calendarPopupContent,
                                captureDirectory,
                                $"{mode.ToString().ToLowerInvariant()}-history-calendar-{suffix}-item-hover");
                            SetReadOnlyBooleanState(
                                calendarItem,
                                typeof(UIElement),
                                "IsMouseOverPropertyKey",
                                false);

                            if (displayMode == CalendarMode.Month)
                            {
                                SetReadOnlyBooleanState(
                                    previousButton,
                                    typeof(UIElement),
                                    "IsMouseOverPropertyKey",
                                    true);
                                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                                CaptureElement(
                                    calendarPopupContent,
                                    captureDirectory,
                                    $"{mode.ToString().ToLowerInvariant()}-history-calendar-navigation-hover");
                                SetReadOnlyBooleanState(
                                    previousButton,
                                    typeof(UIElement),
                                    "IsMouseOverPropertyKey",
                                    false);
                                SetReadOnlyBooleanState(
                                    nextButton,
                                    typeof(ButtonBase),
                                    "IsPressedPropertyKey",
                                    true);
                                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                                ShapePath pressedGlyph = FindVisualDescendants<ShapePath>(nextButton)
                                    .First(item => item.Name.Equals("CalendarNavigationGlyph", StringComparison.Ordinal));
                                Border pressedSurface = FindVisualDescendants<Border>(nextButton)
                                    .First(item => item.Name.Equals("NavigationSurface", StringComparison.Ordinal));
                                EnsureContrast(
                                    pressedGlyph.Stroke,
                                    pressedSurface.Background,
                                    $"{mode} calendar navigation pressed state");
                                CaptureElement(
                                    calendarPopupContent,
                                    captureDirectory,
                                    $"{mode.ToString().ToLowerInvariant()}-history-calendar-navigation-pressed");
                                SetReadOnlyBooleanState(
                                    nextButton,
                                    typeof(ButtonBase),
                                    "IsPressedPropertyKey",
                                    false);
                                SetReadOnlyBooleanState(
                                    previousButton,
                                    typeof(UIElement),
                                    "IsKeyboardFocusedPropertyKey",
                                    true);
                                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                                CaptureElement(
                                    calendarPopupContent,
                                    captureDirectory,
                                    $"{mode.ToString().ToLowerInvariant()}-history-calendar-navigation-focus");
                                SetReadOnlyBooleanState(
                                    previousButton,
                                    typeof(UIElement),
                                    "IsKeyboardFocusedPropertyKey",
                                    false);
                                previousButton.IsEnabled = false;
                                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                                CaptureElement(
                                    calendarPopupContent,
                                    captureDirectory,
                                    $"{mode.ToString().ToLowerInvariant()}-history-calendar-navigation-disabled");
                                previousButton.IsEnabled = true;
                            }
                        }
                        historyFromDatePicker.IsDropDownOpen = false;

                        double normalWidth = window.Width;
                        window.Width = window.MinWidth;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Ensure(
                            historyRefreshButton.TransformToAncestor(window).Transform(new Point()).X >=
                            historyPeriodPanel.TransformToAncestor(window).Transform(new Point(historyPeriodPanel.ActualWidth, 0)).X + 20,
                            "History period and refresh controls must remain separated at minimum width.");
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-history-narrow");
                        window.Width = normalWidth;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    }
                    else if (index == 3)
                    {
                        Ensure(comparisonGrid.Items.Count == 336, "The visual comparison fixture must exercise a large list.");
                        EnsureAccessibleVerticalScrollBar(comparisonGrid, "Comparison table");
                    }
                    else if (index == 5)
                    {
                        Ensure(diagnosticsGrid.Items.Count == 1, "The failed run diagnostic must render in troubleshooting.");
                        Ensure(diagnosticsGrid.Columns.Count == 2, "Troubleshooting must provide a concise selectable diagnostic list.");
                        Ensure(
                            diagnosticActionText.Text.Equals(viewModel.SelectedDiagnostic?.SuggestedAction, StringComparison.Ordinal),
                            "The complete selected recovery guidance must render below the diagnostic list.");
                        Ensure(diagnosticActionText.TextWrapping == TextWrapping.Wrap, "Recovery guidance must wrap instead of clipping.");
                        Ensure(
                            diagnosticDetailText.Text.Contains("完全ログ:", StringComparison.Ordinal),
                            "The selected diagnostic detail must render its persistent log path.");
                        Ensure(diagnosticDetailText.IsReadOnly, "Diagnostic details must be copyable without being editable.");
                    }

                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    CaptureWindow(
                        window,
                        captureDirectory,
                        $"{mode.ToString().ToLowerInvariant()}-tab-{index + 1}");
                }

                SetPrivateProperty(viewModel, nameof(MainViewModel.SelectedRun), CreateVisualFailureRun(RunStatus.Partial));
                Task partialProjection = viewModel.WaitForResultFilterAsync();
                PumpUntil(window.Dispatcher, () => partialProjection.IsCompleted, TimeSpan.FromSeconds(5));
                partialProjection.GetAwaiter().GetResult();
                SetPrivateProperty(
                    viewModel,
                    nameof(MainViewModel.StatusText),
                    "測定は一部のみ完了しました: sample.csproj。YAAP2001: 測定用 dotnet build に失敗しました。原因ログと対処は「トラブルシュート」タブで確認できます。");
                mainTabs.SelectedIndex = 5;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Ensure(
                    statusBar.Severity == Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    "A partial measurement must render a warning status surface.");
                CaptureWindow(
                    window,
                    captureDirectory,
                    $"{mode.ToString().ToLowerInvariant()}-partial-troubleshooting");

                recentTargetsButton.IsChecked = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Ensure(recentTargetsPopup.IsOpen, "The recent-target popup should open in each theme.");
                Ensure(
                    recentTargetsChevron.Symbol == Wpf.Ui.Controls.SymbolRegular.ChevronUp16,
                    "The recent-target chevron should point up while open.");
                recentTargetsItems.UpdateLayout();
                ContentPresenter themedRecentTargetPresenter =
                    (ContentPresenter)recentTargetsItems.ItemContainerGenerator.ContainerFromIndex(0);
                Button themedRecentTargetItem = FindVisualDescendant<Button>(themedRecentTargetPresenter) ??
                    throw new InvalidOperationException("The themed recent-target item was not rendered.");
                EnsureReadableForeground(
                    themedRecentTargetItem.Foreground,
                    mode,
                    $"{mode} recent-target item");
                CaptureElement(
                    recentTargetsPopupContent,
                    captureDirectory,
                    $"{mode.ToString().ToLowerInvariant()}-recent-targets");
                ContentPresenter secondRecentTargetPresenter =
                    (ContentPresenter)recentTargetsItems.ItemContainerGenerator.ContainerFromIndex(1);
                Button secondRecentTargetItem = FindVisualDescendant<Button>(secondRecentTargetPresenter) ??
                    throw new InvalidOperationException("The second recent-target item was not rendered.");
                Ensure(
                    Math.Abs(themedRecentTargetItem.ActualWidth - secondRecentTargetItem.ActualWidth) < 0.5,
                    "Recent-target items must use equal widths.");
                recentTargetsButton.IsChecked = false;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                advancedSettingsButton.IsChecked = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Ensure(advancedSettingsPopup.IsOpen, "The advanced-settings popup should open in each theme.");
                CaptureElement(
                    advancedSettingsPopupContent,
                    captureDirectory,
                    $"{mode.ToString().ToLowerInvariant()}-advanced-settings");
                advancedSettingsButton.IsChecked = false;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                SetPrivateProperty(viewModel, nameof(MainViewModel.StatusText), "コンパイラー情報 1/3 を逐次解析しています。");
                SetPrivateProperty(viewModel, nameof(MainViewModel.IsRunning), true);
            }

            SetPrivateProperty(viewModel, nameof(MainViewModel.IsRunning), false);
            recentTargetsButton.IsChecked = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Ensure(recentTargetsButton.IsChecked == true, "The recent-target button should stay toggled while open.");
            Ensure(recentTargetsPopup.IsOpen, "The recent-target popup should be visible.");
            Ensure(recentTargetsItems.Items.Count == 2, "The recent-target popup should render all items.");
            recentTargetsItems.UpdateLayout();
            ContentPresenter recentTargetPresenter =
                (ContentPresenter)recentTargetsItems.ItemContainerGenerator.ContainerFromIndex(0);
            Button recentTargetItem = FindVisualDescendant<Button>(recentTargetPresenter) ??
                throw new InvalidOperationException("The recent-target item button was not rendered.");
            recentTargetItem.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Ensure(viewModel.TargetPath == recentTargetPath, "Clicking a recent target should select it.");
            Ensure(recentTargetsButton.IsChecked == false, "Selecting a recent target should reset the toggle button.");
            Ensure(!recentTargetsPopup.IsOpen, "Selecting a recent target should close the popup.");
            window.Close();
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetResult(exception);
        }
        finally
        {
            app?.Shutdown();
        }
    })
    {
        IsBackground = true,
        Name = "YAAP GUI startup smoke test",
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();

    try
    {
        Exception? exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Ensure(exception is null, $"MainWindow failed during startup: {exception}");
        Ensure(thread.Join(TimeSpan.FromSeconds(5)), "The GUI startup smoke thread did not exit.");
    }
    finally
    {
        if (Directory.Exists(historyPath))
        {
            Directory.Delete(historyPath, recursive: true);
        }
    }
}

static void CaptureWindow(Window window, string? captureDirectory, string name)
{
    CaptureElement(window, captureDirectory, name);
}

static void CaptureElement(FrameworkElement element, string? captureDirectory, string name)
{
    RenderTargetBitmap bitmap = RenderElement(element);
    Ensure(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "The GUI render bitmap is invalid.");
    if (string.IsNullOrWhiteSpace(captureDirectory))
    {
        return;
    }

    Directory.CreateDirectory(captureDirectory);
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using FileStream stream = File.Create(System.IO.Path.Combine(captureDirectory, $"{name}.png"));
    encoder.Save(stream);
}

static RenderTargetBitmap RenderElement(FrameworkElement element)
{
    element.UpdateLayout();
    int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
    int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
    RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(element);
    Ensure(bitmap.PixelWidth == width && bitmap.PixelHeight == height, "The GUI render bitmap is invalid.");
    return bitmap;
}

static Color GetRenderedPixel(
    RenderTargetBitmap bitmap,
    FrameworkElement bitmapRoot,
    FrameworkElement target,
    Point pointWithinTarget)
{
    Point point = target.TransformToAncestor(bitmapRoot).Transform(pointWithinTarget);
    int x = Math.Clamp((int)Math.Round(point.X), 0, bitmap.PixelWidth - 1);
    int y = Math.Clamp((int)Math.Round(point.Y), 0, bitmap.PixelHeight - 1);
    byte[] pixel = new byte[4];
    bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
    return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
}

static Color GetMostContrastingRenderedPixel(
    RenderTargetBitmap bitmap,
    FrameworkElement bitmapRoot,
    FrameworkElement target,
    Color reference,
    double targetX,
    double targetY,
    int horizontalRadius)
{
    Color result = reference;
    double greatestDistance = 0;
    for (int offset = -horizontalRadius; offset <= horizontalRadius; offset++)
    {
        Color candidate = GetRenderedPixel(
            bitmap,
            bitmapRoot,
            target,
            new Point(targetX + offset, targetY));
        double distance = ColorDistance(reference, candidate);
        if (distance > greatestDistance)
        {
            greatestDistance = distance;
            result = candidate;
        }
    }

    return result;
}

static double ColorDistance(Color left, Color right) => Math.Sqrt(
    Math.Pow(left.R - right.R, 2) +
    Math.Pow(left.G - right.G, 2) +
    Math.Pow(left.B - right.B, 2));

static string FormatColor(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

static void EnsureAnalyzerViewTabState(TabItem selected, TabItem unselected, string name)
{
    selected.ApplyTemplate();
    unselected.ApplyTemplate();
    Border selectedSurface = selected.Template.FindName("AnalyzerViewTabSurface", selected) as Border ??
        throw new InvalidOperationException($"{name} did not render the selected tab surface.");
    Border selectedIndicator = selected.Template.FindName("AnalyzerViewTabIndicator", selected) as Border ??
        throw new InvalidOperationException($"{name} did not render the selection indicator.");
    ContentPresenter selectedHeader =
        selected.Template.FindName("AnalyzerViewTabHeader", selected) as ContentPresenter ??
        throw new InvalidOperationException($"{name} did not render the header presenter.");
    Border unselectedIndicator = unselected.Template.FindName("AnalyzerViewTabIndicator", unselected) as Border ??
        throw new InvalidOperationException($"{name} did not render the unselected indicator.");

    Ensure(selected.IsSelected, $"{name} must be selected.");
    Ensure(!unselected.IsSelected, $"{name} must leave the alternate view unselected.");
    Ensure(selectedIndicator.Visibility == Visibility.Visible, $"{name} must show an accent indicator.");
    Ensure(selectedIndicator.ActualHeight >= 3, $"{name} accent indicator must remain clearly visible.");
    Ensure(unselectedIndicator.Visibility == Visibility.Collapsed, $"{name} must hide the inactive indicator.");
    Ensure(
        TextElement.GetFontWeight(selectedHeader) == FontWeights.SemiBold,
        $"{name} must emphasize its selected label.");
    Ensure(
        selectedSurface.Background is SolidColorBrush { Color.A: byte.MaxValue },
        $"{name} must use an opaque selected fill that remains stable across backgrounds.");
    Ensure(
        selectedSurface.BorderBrush is SolidColorBrush { Color.A: > 0 } &&
        selectedSurface.BorderThickness.Left >= 1,
        $"{name} must expose a visible selected outline.");
    EnsureContrast(
        TextElement.GetForeground(selectedHeader),
        selectedSurface.Background,
        $"{name} selected label");
}

static void EnsureReadableForeground(Brush brush, AppThemeMode mode, string name)
{
    Ensure(brush is SolidColorBrush, $"{name} must use a solid theme foreground.");
    Color color = ((SolidColorBrush)brush).Color;
    double luminance = (0.2126 * color.ScR) + (0.7152 * color.ScG) + (0.0722 * color.ScB);
    if (mode == AppThemeMode.Dark)
    {
        Ensure(luminance >= 0.5, $"{name} foreground is too dark for the dark theme.");
    }
    else
    {
        Ensure(luminance <= 0.5, $"{name} foreground is too light for the light theme.");
    }
}

static void EnsureContrast(Brush foreground, Brush background, string name)
{
    Ensure(foreground is SolidColorBrush, $"{name} foreground must be a solid brush.");
    Ensure(background is SolidColorBrush, $"{name} background must be a solid brush.");
    Color foregroundColor = ((SolidColorBrush)foreground).Color;
    Color backgroundColor = ((SolidColorBrush)background).Color;
    double foregroundLuminance = RelativeLuminance(foregroundColor);
    double backgroundLuminance = RelativeLuminance(backgroundColor);
    double ratio = (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
        (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    Ensure(
        ratio >= 4.5,
        $"{name} contrast ratio {ratio:N2} is below 4.5:1 " +
        $"(foreground #{foregroundColor.R:X2}{foregroundColor.G:X2}{foregroundColor.B:X2}, " +
        $"background #{backgroundColor.R:X2}{backgroundColor.G:X2}{backgroundColor.B:X2}).");
}

static Brush GetCalendarTextBackground(TextBlock text, Calendar calendar)
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

    bool selected = calendarButton?.DataContext is DateTime date
        ? calendar.DisplayMode switch
        {
            CalendarMode.Year => date.Year == calendar.DisplayDate.Year &&
                date.Month == calendar.DisplayDate.Month,
            CalendarMode.Decade => date.Year == calendar.DisplayDate.Year,
            _ => false,
        }
        : calendarButton is not null && calendar.DisplayMode switch
        {
            CalendarMode.Year => text.Text.Equals($"{calendar.DisplayDate.Month}月", StringComparison.Ordinal),
            CalendarMode.Decade => text.Text.Equals(
                calendar.DisplayDate.Year.ToString(System.Globalization.CultureInfo.CurrentCulture),
                StringComparison.Ordinal),
            _ => false,
        };
    if (!selected)
    {
        return calendar.Background;
    }

    for (DependencyObject? current = VisualTreeHelper.GetParent(text);
         current is not null && !ReferenceEquals(current, calendar);
         current = VisualTreeHelper.GetParent(current))
    {
        Brush? background = current switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            Control control => control.Background,
            _ => null,
        };
        if (background is SolidColorBrush { Color.A: > 0 })
        {
            return background;
        }
    }

    return calendar.Background;
}

static double RelativeLuminance(Color color)
{
    static double Linearize(byte component)
    {
        double channel = component / 255.0;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    return (0.2126 * Linearize(color.R)) +
        (0.7152 * Linearize(color.G)) +
        (0.0722 * Linearize(color.B));
}

static void EnsureCenteredWithin(
    FrameworkElement element,
    FrameworkElement container,
    double tolerance,
    string name)
{
    Point elementTopLeft = element.TransformToAncestor(container).Transform(new Point());
    double elementCenterX = elementTopLeft.X + (element.ActualWidth / 2);
    double elementCenterY = elementTopLeft.Y + (element.ActualHeight / 2);
    double containerCenterX = container.ActualWidth / 2;
    double containerCenterY = container.ActualHeight / 2;
    Ensure(
        Math.Abs(elementCenterX - containerCenterX) <= tolerance &&
        Math.Abs(elementCenterY - containerCenterY) <= tolerance,
        $"{name} must be centered within its pointer target " +
        $"(icon {elementCenterX:N2},{elementCenterY:N2}; target {containerCenterX:N2},{containerCenterY:N2}).");
}

static void EnsureTemplateInteractionTriggers(ControlTemplate template, string name)
{
    HashSet<DependencyProperty> triggerProperties = template.Triggers
        .OfType<Trigger>()
        .Select(trigger => trigger.Property)
        .ToHashSet();
    foreach (DependencyProperty required in new[]
             {
                 UIElement.IsMouseOverProperty,
                 ButtonBase.IsPressedProperty,
                 UIElement.IsKeyboardFocusedProperty,
                 UIElement.IsEnabledProperty,
             })
    {
        Ensure(triggerProperties.Contains(required), $"{name} must style {required.Name}.");
    }
}

static void SetReadOnlyBooleanState(
    DependencyObject element,
    Type ownerType,
    string propertyKeyField,
    bool value)
{
    FieldInfo field = ownerType.GetField(
        propertyKeyField,
        BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy) ??
        throw new InvalidOperationException($"WPF state key was not found: {ownerType.Name}.{propertyKeyField}");
    DependencyPropertyKey key = field.GetValue(null) as DependencyPropertyKey ??
        throw new InvalidOperationException($"WPF state key was invalid: {ownerType.Name}.{propertyKeyField}");
    element.SetValue(key, value);
}

static T? FindVisualDescendant<T>(DependencyObject root)
    where T : DependencyObject
{
    for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        DependencyObject child = VisualTreeHelper.GetChild(root, index);
        if (child is T match)
        {
            return match;
        }

        T? descendant = FindVisualDescendant<T>(child);
        if (descendant is not null)
        {
            return descendant;
        }
    }

    return null;
}

static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
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

static void EnsureAccessibleVerticalScrollBar(DependencyObject root, string name)
{
    ScrollBar scrollBar = FindVisualDescendants<ScrollBar>(root)
        .Where(item => item.Orientation == Orientation.Vertical && item.IsVisible)
        .OrderByDescending(item => item.ActualHeight)
        .FirstOrDefault() ??
        throw new InvalidOperationException($"{name} vertical scrollbar was not rendered.");
    scrollBar.ApplyTemplate();
    Thumb thumb = scrollBar.Track?.Thumb ??
        throw new InvalidOperationException($"{name} scrollbar thumb was not rendered.");
    Border thumbVisual = thumb.Template.FindName("ThumbBody", thumb) as Border ??
        throw new InvalidOperationException($"{name} scrollbar did not use the accessible thumb template.");
    Ensure(scrollBar.ActualWidth >= 19, $"{name} scrollbar must provide a practical pointer target.");
    Ensure(thumb.ActualWidth >= 15, $"{name} scrollbar thumb must provide a practical pointer target.");
    Ensure(thumb.ActualHeight >= 51, $"{name} scrollbar thumb must remain easy to grab with many items.");
    Ensure(thumbVisual.ActualWidth >= 11, $"{name} scrollbar thumb must be visibly wide enough to grab.");
    Ensure(thumbVisual.ActualHeight >= 47, $"{name} scrollbar thumb must be visibly tall enough to grab.");
}

static void PumpUntil(Dispatcher dispatcher, Func<bool> condition, TimeSpan timeout)
{
    DateTime deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException("The GUI operation did not complete while pumping the dispatcher.");
        }

        DispatcherFrame frame = new();
        dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Thread.Sleep(10);
    }
}

static DataGridCell GetDataGridCell(DataGrid grid, int rowIndex, int columnIndex)
{
    grid.UpdateLayout();
    DataGridRow row = grid.ItemContainerGenerator.ContainerFromIndex(rowIndex) as DataGridRow ??
        throw new InvalidOperationException($"DataGrid row {rowIndex} was not rendered.");
    grid.ScrollIntoView(row.Item, grid.Columns[columnIndex]);
    row.UpdateLayout();
    DataGridCellsPresenter presenter = FindVisualDescendant<DataGridCellsPresenter>(row) ??
        throw new InvalidOperationException("The DataGrid cells presenter was not rendered.");
    return presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex) as DataGridCell ??
        throw new InvalidOperationException($"DataGrid cell {rowIndex},{columnIndex} was not rendered.");
}

static void SetPrivateProperty<T>(object target, string propertyName, T value)
{
    PropertyInfo property = target.GetType().GetProperty(propertyName) ??
        throw new InvalidOperationException($"Property was not found: {propertyName}");
    MethodInfo setter = property.GetSetMethod(nonPublic: true) ??
        throw new InvalidOperationException($"Property setter was not found: {propertyName}");
    setter.Invoke(target, new object?[] { value });
}

static void SetPrivateField<T>(object target, string fieldName, T value)
{
    FieldInfo field = target.GetType().GetField(
        fieldName,
        BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Field was not found: {fieldName}");
    field.SetValue(target, value);
}

static ProfileRun CreateVisualRun()
{
    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    StatisticalMetric[] analyzers = Enumerable.Range(0, 600)
        .Select(index => new StatisticalMetric(
            $"Sample.Analyzers.PerformanceAnalyzer{index:D4}",
            "Sample.Analyzers",
            MetricKind.Analyzer,
            null,
            10 + index,
            9 + index,
            11 + index,
            1.2,
            3))
        .ToArray();
    GeneratorMetric[] generators = Enumerable.Range(0, 240)
        .Select(index => new GeneratorMetric(
            $"Sample.Generators.ModelGenerator{index:D4}",
            "Sample.Generators",
            8.5 + index,
            8 + index,
            9 + index,
            0.5,
            3,
            1,
            256,
            12,
            new[]
            {
                new GeneratedOutput(
                    $"Sample.Generators.ModelGenerator{index:D4}",
                    "Sample.Generators",
                    $"Generated/Model{index:D4}.g.cs",
                    256,
                    12),
            }))
        .ToArray();
    generators[0] = generators[0] with { OutputsTruncated = true };
    return new ProfileRun
    {
        TargetPath = "sample.csproj",
        TargetName = "sample.csproj",
        Configuration = "Release",
        Mode = ProfileMode.Warm,
        StartedAt = startedAt,
        FinishedAt = startedAt.AddSeconds(1),
        Status = RunStatus.Succeeded,
        Environment = new EnvironmentSnapshot(
            "Windows",
            "x64",
            8,
            ".NET",
            "10.0.100",
            null,
            null,
            false),
        Analyzers = analyzers,
        Generators = generators,
        Diagnostics = Enumerable.Range(0, 180)
            .Select(index => new RunDiagnostic(
                $"YAAP{index:D4}",
                "確認用診断",
                $"テーマ描画確認 {index:D3}",
                "操作は不要です。"))
            .ToArray(),
        Isolated = true,
    };
}

static ProfileRun CreateVisualFailureRun(RunStatus status)
{
    ProfileRun run = CreateVisualRun();
    run.Status = status;
    ProcessOperation operation = status == RunStatus.Partial
        ? ProcessOperation.MeasuredBuild
        : ProcessOperation.Clean;
    string command = status == RunStatus.Partial ? "build" : "clean";
    run.Diagnostics = new[]
    {
        YaapErrors.ProcessFailed(
            operation,
            17,
            $"実行コマンド: dotnet {command} sample.csproj\n作業ディレクトリ: fixture\n完全ログ: history/runs/sample/logs/{command}-001.log\n標準出力末尾（前方の行は完全ログにのみ記録）:\n  Build started.\n標準エラー出力末尾:\n  ファイルが別のプロセスで使用されています。"),
    };
    return run;
}

static ComparisonResult CreateVisualComparison()
{
    MetricDelta[] metrics = Enumerable.Range(0, 336)
        .Select(index => new MetricDelta(
            $"Sample.Analyzers.PerformanceAnalyzer{index:D4}",
            index % 7 == 0 ? "generator" : "analyzer",
            10 + index,
            11.5 + index,
            1.5,
            15,
            Added: false,
            Removed: false))
        .ToArray();
    return new ComparisonResult(Guid.NewGuid(), Guid.NewGuid(), metrics, 0, 0, Array.Empty<string>());
}

static async Task ViewModelInitializationAsync()
{
    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yaap-gui-tests", Guid.NewGuid().ToString("N"));
    try
    {
        using MainViewModel viewModel = new()
        {
            HistoryPath = path,
        };
        await viewModel.InitializeAsync();
        Ensure(viewModel.History.Count == 0, "History should start empty.");
        Ensure(viewModel.Modes.Contains(ProfileMode.Warm), "Warm mode should be available.");
        Ensure(viewModel.HistoryStatuses.Contains("部分結果"), "History status filtering should be available.");
        Ensure(viewModel.Isolated, "The GUI should default to isolated output.");
        Ensure(viewModel.Restore, "The GUI should restore by default.");
        Ensure(viewModel.RetentionCount == 50, "Default history retention should be available.");
        Ensure(viewModel.HistoryLimit == "500", "GUI history display must have a bounded default.");
        Ensure(viewModel.CancelCommand.CanExecute(null) == false, "Cancel should be disabled while idle.");
        Ensure(viewModel.SelectedTheme.Mode == AppThemeMode.Auto, "The system theme should be the default.");
        Ensure(viewModel.MeasurementStateText.Contains("測定対象", StringComparison.Ordinal), "The disabled-start reason should be visible.");
        Ensure(MainViewModel.TryParseHistoryDateText("2026/01/31", out _), "Slash-separated dates should be accepted.");
        Ensure(MainViewModel.TryParseHistoryDateText("2026-01-31", out _), "ISO dates should be accepted.");
        Ensure(MainViewModel.TryParseHistoryDateText("31/Jan/2026", out _), "Invariant month-name dates should be accepted.");
        Ensure(MainViewModel.GetExportFormat("result.json") == ExportFormat.Json, "JSON should be inferred from its extension.");
        Ensure(MainViewModel.GetExportFormat("result.CSV") == ExportFormat.Csv, "Export extensions should be case-insensitive.");
        Ensure(MainViewModel.GetExportFormat("result.md") == ExportFormat.Markdown, "Markdown .md should be supported.");
        Ensure(MainViewModel.GetExportFormat("result.markdown") == ExportFormat.Markdown, "Markdown .markdown should be supported.");
        foreach (string invalidPath in new[] { "result", "result.txt" })
        {
            try
            {
                _ = MainViewModel.GetExportFormat(invalidPath);
                throw new InvalidOperationException($"Unsupported export path was accepted: {invalidPath}");
            }
            catch (YaapException exception)
            {
                Ensure(exception.Diagnostic.Code == "YAAP1002", "Invalid export extensions must use the stable option error code.");
            }
        }
    }
    finally
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

static async Task DropAndAutoDiscoveryAsync()
{
    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yaap-gui-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try
    {
        string project = System.IO.Path.Combine(path, "Sample.csproj");
        await File.WriteAllTextAsync(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Configurations>Debug;Profile;Release</Configurations><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        using MainViewModel viewModel = new(targetDiscoveryDelay: TimeSpan.Zero)
        {
            HistoryPath = System.IO.Path.Combine(path, "history"),
        };
        Ensure(!viewModel.TrySetDroppedTarget(Array.Empty<string>()), "An empty drop should be rejected.");
        Ensure(!viewModel.TrySetDroppedTarget(new[] { project, project }), "A multi-file drop should be rejected.");
        Ensure(!viewModel.TrySetDroppedTarget(new[] { System.IO.Path.Combine(path, "Sample.txt") }), "An unsupported extension should be rejected.");
        Ensure(!viewModel.TrySetDroppedTarget(new[] { System.IO.Path.Combine(path, "Missing.csproj") }), "A missing project drop should be rejected.");
        Ensure(viewModel.TrySetDroppedTarget(new[] { project }), "A supported project drop should be accepted.");
        Ensure(viewModel.TargetPath == System.IO.Path.GetFullPath(project), "The dropped target was not selected.");
        Ensure(
            viewModel.RecentTargets.Count == 1,
            $"A selected valid target should be added to recent targets immediately. Status: {viewModel.StatusText}");
        Ensure(viewModel.RecentTargets[0].Path == System.IO.Path.GetFullPath(project), "The selected target should be the newest recent item.");
        await viewModel.WaitForTargetDiscoveryAsync();
        Ensure(viewModel.Configurations.SequenceEqual(new[] { "Debug", "Profile", "Release" }), "Configurations were not discovered.");
        Ensure(viewModel.Configuration == "Release", "Release should be preferred when the previous configuration is invalid.");
        Ensure(viewModel.StartCommand.CanExecute(null), "Start should be enabled after discovery.");
        Ensure(viewModel.MeasurementStateText.Contains("測定可能", StringComparison.Ordinal), "The ready state should be visible.");
        viewModel.Configuration = string.Empty;
        Ensure(!viewModel.StartCommand.CanExecute(null), "A blank configuration must disable Start.");
        Ensure(viewModel.MeasurementStateText.Contains("ビルド構成を選択", StringComparison.Ordinal), "A blank configuration should have actionable guidance.");
        viewModel.Configuration = "Unknown";
        Ensure(viewModel.StartCommand.CanExecute(null), "A custom configuration absent from discovery should remain usable.");
        Ensure(viewModel.MeasurementStateText.Contains("未検出", StringComparison.Ordinal), "A custom configuration should show a warning.");
        viewModel.Configuration = "Release";
        Ensure(viewModel.StartCommand.CanExecute(null), "A discovered configuration should re-enable Start.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static async Task RecentTargetOrderingAsync()
{
    string path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "yaap-gui-tests",
        Guid.NewGuid().ToString("N"));
    string historyPath = System.IO.Path.Combine(path, "history");
    Directory.CreateDirectory(path);
    try
    {
        string historicalProject = System.IO.Path.Combine(path, "Historical.csproj");
        await File.WriteAllTextAsync(historicalProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        HistoryStore store = new(historyPath);
        await store.SaveAsync(CreateHistoricalRun(
            historicalProject,
            "Release",
            DateTimeOffset.UtcNow.AddDays(-1)));

        using MainViewModel viewModel = new(
            targetDiscoverer: (target, _) => Task.FromResult(new TargetInfo(
                System.IO.Path.GetFullPath(target),
                System.IO.Path.GetExtension(target),
                new[] { "Release" },
                new[] { "net8.0" })),
            targetDiscoveryDelay: TimeSpan.Zero)
        {
            HistoryPath = historyPath,
        };
        await viewModel.InitializeAsync();
        Ensure(viewModel.RecentTargets.Count == 1, "Persisted history should seed recent targets.");

        List<string> projects = new();
        for (int index = 0; index < 12; index++)
        {
            string project = System.IO.Path.Combine(path, $"Project{index:D2}.csproj");
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            projects.Add(project);
            viewModel.TargetPath = project;
            await viewModel.WaitForTargetDiscoveryAsync();
        }

        Ensure(viewModel.RecentTargets.Count == 10, "Recent targets should remain bounded to ten items.");
        Ensure(viewModel.RecentTargets[0].Path == projects[^1], "The newest selected target should be first.");
        Ensure(!viewModel.RecentTargets.Any(item => item.Path == projects[0]), "The oldest target should be evicted.");

        viewModel.TargetPath = projects[5];
        await viewModel.WaitForTargetDiscoveryAsync();
        Ensure(viewModel.RecentTargets.Count == 10, "Re-selecting a target must not duplicate it.");
        Ensure(viewModel.RecentTargets[0].Path == projects[5], "Re-selecting a target should promote it to first.");

        await viewModel.InitializeAsync();
        Ensure(
            viewModel.RecentTargets.Any(item => item.Path == projects[5]),
            "Refreshing persisted history must not discard current-session targets.");
        Ensure(viewModel.RecentTargets[0].Path == projects[5], "History refresh should preserve session recency.");

        viewModel.TargetPath = System.IO.Path.Combine(path, "Unsupported.txt");
        await viewModel.WaitForTargetDiscoveryAsync();
        Ensure(viewModel.RecentTargets.Count == 10, "Invalid targets must not be added to recent targets.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static async Task ConfigurationPriorityAsync()
{
    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yaap-gui-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try
    {
        async Task<string> ProjectAsync(string name, string configurations)
        {
            string project = System.IO.Path.Combine(path, $"{name}.csproj");
            await File.WriteAllTextAsync(
                project,
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Configurations>{configurations}</Configurations><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            return project;
        }

        string debugProject = await ProjectAsync("DebugOnly", "Profile;Debug");
        string alphabeticalProject = await ProjectAsync("Alphabetical", "Zulu;Alpha;Profile");
        using MainViewModel viewModel = new(targetDiscoveryDelay: TimeSpan.Zero);
        viewModel.TargetPath = debugProject;
        await viewModel.WaitForTargetDiscoveryAsync();
        Ensure(viewModel.Configuration == "Debug", "Debug should be preferred when Release is unavailable.");
        Ensure(viewModel.StartCommand.CanExecute(null), "Debug fallback should be ready to start.");

        viewModel.TargetPath = alphabeticalProject;
        await viewModel.WaitForTargetDiscoveryAsync();
        Ensure(viewModel.Configurations.SequenceEqual(new[] { "Alpha", "Profile", "Zulu" }), "Configurations should be sorted.");
        Ensure(viewModel.Configuration == "Alpha", "The alphabetical first configuration should be selected as the final fallback.");
        Ensure(viewModel.StartCommand.CanExecute(null), "Alphabetical fallback should be ready to start.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static async Task ConfigurationHistoryAsync()
{
    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yaap-gui-tests", Guid.NewGuid().ToString("N"));
    string historyPath = System.IO.Path.Combine(path, "history");
    Directory.CreateDirectory(path);
    try
    {
        string project = System.IO.Path.Combine(path, "Historical.csproj");
        await File.WriteAllTextAsync(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Configurations>Release;Debug;Profile</Configurations><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        HistoryStore history = new(historyPath);
        await history.SaveAsync(CreateHistoricalRun(project, "Debug", DateTimeOffset.UtcNow.AddMinutes(-2)));
        await history.SaveAsync(CreateHistoricalRun(project, "Profile", DateTimeOffset.UtcNow.AddMinutes(-1)));

        using MainViewModel discoveryFirst = new(targetDiscoveryDelay: TimeSpan.Zero)
        {
            HistoryPath = historyPath,
            TargetPath = project,
        };
        await discoveryFirst.WaitForTargetDiscoveryAsync();
        Ensure(discoveryFirst.Configuration == "Profile", "Discovery should load and use the newest historical configuration.");
        await discoveryFirst.InitializeAsync();
        Ensure(discoveryFirst.Configuration == "Profile", "Refreshing history should preserve the newest historical configuration.");

        using MainViewModel historyFirst = new(targetDiscoveryDelay: TimeSpan.Zero)
        {
            HistoryPath = historyPath,
        };
        await historyFirst.InitializeAsync();
        Ensure(historyFirst.RecentTargets.Count == 1, "Recent targets should be deduplicated by path.");
        Ensure(historyFirst.RecentTargets[0].Path == System.IO.Path.GetFullPath(project), "The historical target path should be selectable.");
        historyFirst.SelectedRecentTarget = historyFirst.RecentTargets[0];
        await historyFirst.WaitForTargetDiscoveryAsync();
        Ensure(historyFirst.TargetPath == System.IO.Path.GetFullPath(project), "Selecting a recent target should update the target path.");
        Ensure(historyFirst.Configuration == "Profile", "The newest same-target history should win when history loads first.");
        Ensure(historyFirst.StartCommand.CanExecute(null), "A historical configuration should be ready to start.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static async Task HistoryLoadDiscardsStaleSelectionAsync()
{
    ProfileRun first = CreateHistoricalRun("first.csproj", "Release", DateTimeOffset.UtcNow.AddMinutes(-1));
    ProfileRun second = CreateHistoricalRun("second.csproj", "Release", DateTimeOffset.UtcNow);
    async Task<ProfileRun> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == first.Id)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return id == first.Id ? first : second;
    }

    using MainViewModel viewModel = new(historyLoader: LoadAsync);
    RunSummary firstSummary = first.ToSummary();
    RunSummary secondSummary = second.ToSummary();
    viewModel.History.Add(firstSummary);
    viewModel.History.Add(secondSummary);
    viewModel.SelectedHistory = firstSummary;
    viewModel.LoadSelectedCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.IsOperationRunning, TimeSpan.FromSeconds(2));
    viewModel.SelectedHistory = secondSummary;
    await WaitUntilAsync(() => !viewModel.IsOperationRunning, TimeSpan.FromSeconds(2));
    Ensure(viewModel.SelectedRun is null, "A canceled stale load must not install its result.");

    viewModel.LoadSelectedCommand.Execute(null);
    await WaitUntilAsync(() => !viewModel.IsOperationRunning, TimeSpan.FromSeconds(2));
    Ensure(viewModel.SelectedRun?.Id == second.Id, "The newly selected history result must win.");
}

static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    while (!predicate())
    {
        if (stopwatch.Elapsed >= timeout)
        {
            throw new TimeoutException("The expected GUI state was not reached.");
        }

        await Task.Delay(10);
    }
}

static async Task FeatureParityAsync()
{
    string path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "yaap-gui-tests",
        Guid.NewGuid().ToString("N"));
    string historyPath = System.IO.Path.Combine(path, "history");
    Directory.CreateDirectory(path);
    try
    {
        string project = System.IO.Path.Combine(path, "Parity.csproj");
        string binlog = System.IO.Path.Combine(path, "existing.binlog");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllBytesAsync(binlog, new byte[] { 1 });
        HistoryStore history = new(historyPath);
        await history.SaveAsync(CreateHistoricalRun(project, "Debug", DateTimeOffset.UtcNow.AddDays(-2)));
        await history.SaveAsync(CreateHistoricalRun(project, "Release", DateTimeOffset.UtcNow));

        using MainViewModel viewModel = new(
            targetDiscoveryDelay: TimeSpan.Zero,
            binlogAnalyzer: (_, _) => Task.FromResult(new BinlogAnalysis(
                new[] { new AnalyzerSample("ParityAnalyzer", "Parity", MetricKind.Analyzer, null, 12) },
                new[] { new GeneratorSample("ParityGenerator", "Parity", 8) },
                Array.Empty<RunDiagnostic>(),
                42,
                Array.Empty<CompilerInvocation>())))
        {
            HistoryPath = historyPath,
        };
        await viewModel.InitializeAsync();
        Ensure(viewModel.History.Count == 2, "History should initially contain both runs.");
        Ensure(viewModel.ComparisonChoices.Count == 2, "Readable comparison choices should follow history.");
        Ensure(viewModel.SelectedBaseline is not null && viewModel.SelectedCandidate is not null, "Comparison should select sensible defaults.");
        Ensure(viewModel.CompareCommand.CanExecute(null), "Two different history choices should enable comparison.");

        viewModel.SelectedHistory = viewModel.History[0];
        viewModel.SelectedHistoryLabel = "最適化後";
        await viewModel.WaitForLabelSaveAsync();
        RunSummary labeled = AssertSingle(await history.ListAsync(new HistoryQuery(Search: "最適化後")));
        Ensure(labeled.Id == viewModel.SelectedHistory.Id, "The edited history label was not persisted.");
        Ensure(viewModel.UndoLabelCommand.CanExecute(null), "A saved label edit should enable Undo.");
        viewModel.UndoLabelCommand.Execute(null);
        await WaitForOperationAsync(viewModel);
        Ensure((await history.ListAsync()).Single(item => item.Id == labeled.Id).Label is null, "Undo did not restore the previous label.");
        Ensure(viewModel.RedoLabelCommand.CanExecute(null), "Undo should enable Redo.");
        viewModel.RedoLabelCommand.Execute(null);
        await WaitForOperationAsync(viewModel);
        Ensure((await history.ListAsync()).Single(item => item.Id == labeled.Id).Label == "最適化後", "Redo did not restore the label.");

        viewModel.HistoryFrom = "31/Jan/2026";
        viewModel.HistoryTo = "2026-12-31";
        viewModel.RefreshHistoryCommand.Execute(null);
        await WaitForOperationAsync(viewModel);
        Ensure(viewModel.History.Count == 2, "Common fuzzy date formats should be accepted.");
        Ensure(viewModel.ClearHistoryPeriodCommand.CanExecute(null), "A populated history period should enable clearing.");
        viewModel.ClearHistoryPeriodCommand.Execute(null);
        Ensure(viewModel.HistoryFrom.Length == 0 && viewModel.HistoryTo.Length == 0, "History period clear must empty both fields atomically.");
        Ensure(!viewModel.ClearHistoryPeriodCommand.CanExecute(null), "An empty history period should disable clearing.");
        await viewModel.WaitForHistoryRefreshAsync();
        Ensure(viewModel.History.Count == 2, "Clearing the history period should refresh the unbounded list.");

        viewModel.HistoryFrom = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        viewModel.HistoryTo = string.Empty;
        viewModel.HistoryLimit = "1";
        viewModel.RefreshHistoryCommand.Execute(null);
        await WaitForOperationAsync(viewModel);
        Ensure(viewModel.History.Count == 1, "GUI history date and limit filters must match CLI capability.");
        Ensure(viewModel.History[0].Configuration == "Release", "History filtering returned the wrong run.");

        viewModel.BinlogPath = binlog;
        Ensure(viewModel.AnalyzeBinlogCommand.CanExecute(null), "A binlog path should enable analysis.");
        viewModel.AnalyzeBinlogCommand.Execute(null);
        await WaitForOperationAsync(viewModel);
        Ensure(viewModel.SelectedRun?.TargetPath == binlog, "GUI binlog analysis did not select its result.");
        Ensure(viewModel.SelectedRun?.Analyzers.Single().Identity == "ParityAnalyzer", "Analyzer results were not projected from the binlog.");
        Ensure(viewModel.SelectedRun?.Generators.Single().Identity == "ParityGenerator", "Generator results were not projected from the binlog.");
        Ensure(viewModel.StatusText.Contains("42", StringComparison.Ordinal), "Binlog event count should be visible.");

        viewModel.ExportPath = System.IO.Path.Combine(path, "result.txt");
        viewModel.ExportCommand.Execute(null);
        await WaitForOperationAsync(viewModel);
        Ensure(
            viewModel.StatusText.Contains("YAAP1002", StringComparison.Ordinal) &&
            viewModel.StatusText.Contains(".json", StringComparison.Ordinal),
            "GUI export must reject unsupported extensions with an actionable stable error.");
        viewModel.ExportPath = System.IO.Path.Combine(path, "result.markdown");
        viewModel.ExportCommand.Execute(null);
        await WaitForOperationAsync(viewModel);
        Ensure(File.Exists(viewModel.ExportPath), "GUI export must derive Markdown from the selected file extension.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static T AssertSingle<T>(IReadOnlyList<T> values)
{
    Ensure(values.Count == 1, $"Expected one item, actual {values.Count}.");
    return values[0];
}

static async Task WaitForOperationAsync(MainViewModel viewModel)
{
    for (int attempt = 0; attempt < 500; attempt++)
    {
        if (!viewModel.IsOperationRunning)
        {
            await Task.Delay(10);
            if (!viewModel.IsOperationRunning)
            {
                return;
            }
        }

        await Task.Delay(10);
    }

    throw new TimeoutException("The GUI operation did not complete.");
}

static Task ResultTreeFilteringAsync()
{
    StatisticalMetric analyzer = new(
        "SampleAnalyzer",
        "Sample.Assembly",
        MetricKind.Analyzer,
        null,
        12,
        10,
        14,
        1,
        3);
    StatisticalMetric diagnostic = new(
        "SampleAnalyzer",
        "Sample.Assembly",
        MetricKind.Diagnostic,
        "YAAP001",
        4,
        3,
        5,
        1,
        3);
    IReadOnlyList<ResultTreeNode> analyzerTree = ResultTreeBuilder.BuildAnalyzers(
        new[] { analyzer, diagnostic },
        "YAAP001");
    Ensure(analyzerTree.Count == 1, "The analyzer tree should retain the matching assembly branch.");
    Ensure(analyzerTree[0].Children.Count == 1, "The analyzer tree should filter nonmatching metrics.");
    Ensure(analyzerTree[0].Children[0].Name.Contains("YAAP001", StringComparison.Ordinal), "Diagnostic IDs should be visible in the tree.");
    Ensure(!analyzerTree[0].Children[0].Detail.Contains("標本", StringComparison.Ordinal), "Analyzer tree details must omit sample count.");
    ResultTreeNode analyzerLeaf = analyzerTree[0].Children[0];
    Ensure(analyzerTree[0].Assembly == diagnostic.Assembly, "The analyzer tree root must retain its assembly value.");
    Ensure(!analyzerTree[0].IsAnalyzerMetric, "An assembly root must not render as an analyzer metric.");
    Ensure(analyzerLeaf.IsAnalyzerMetric, "An analyzer tree leaf must expose metric columns.");
    Ensure(analyzerLeaf.Kind == diagnostic.Kind, "The analyzer tree must retain the metric kind.");
    Ensure(analyzerLeaf.MeanMilliseconds == diagnostic.MeanMilliseconds, "The analyzer tree must retain mean time.");
    Ensure(analyzerLeaf.MinimumMilliseconds == diagnostic.MinimumMilliseconds, "The analyzer tree must retain minimum time.");
    Ensure(analyzerLeaf.MaximumMilliseconds == diagnostic.MaximumMilliseconds, "The analyzer tree must retain maximum time.");
    Ensure(
        analyzerLeaf.Detail.Contains("最小 3.000 ms", StringComparison.Ordinal) &&
        analyzerLeaf.Detail.Contains("最大 5.000 ms", StringComparison.Ordinal),
        "Analyzer tree details must carry the complete table timing range.");
    Ensure(
        analyzerLeaf.ClipboardText.Equals(
            AnalyzerResultClipboardFormatter.Format(diagnostic),
            StringComparison.Ordinal),
        "Analyzer tree and table rows must use the same clipboard representation.");
    Ensure(
        MainWindow.GetAnalyzerResultClipboardText(diagnostic) == analyzerLeaf.ClipboardText,
        "Analyzer table and tree clipboard commands must resolve equivalent details.");
    Ensure(
        MainWindow.GetAnalyzerResultClipboardText(analyzerTree[0])?.Contains("項目数: 1", StringComparison.Ordinal) == true,
        "Assembly roots must provide a useful clipboard summary.");
    Ensure(
        MainWindow.GetAnalyzerResultClipboardText(new object()) is null,
        "Unrelated selections must not produce Analyzer clipboard text.");

    GeneratorMetric generator = new(
        "SampleGenerator",
        "Sample.Assembly",
        8,
        7,
        9,
        1,
        3,
        2,
        300,
        20,
        new[]
        {
            new GeneratedOutput("SampleGenerator", "Sample.Generators", "Generated/First.g.cs", 100, 8),
            new GeneratedOutput("SampleGenerator", "Sample.Generators", "Generated/Second.g.cs", 200, 12),
        })
    {
        OutputsTruncated = true,
    };
    IReadOnlyList<ResultTreeNode> generatorTree = ResultTreeBuilder.BuildGenerators(
        new[] { generator },
        "Second");
    Ensure(generatorTree.Count == 1, "A generated-file match should retain its generator branch.");
    Ensure(generatorTree[0].Children[0].Children.Count == 1, "Only matching generated files should remain in a filtered tree.");
    Ensure(generatorTree[0].Children[0].Children[0].Name.EndsWith("Second.g.cs", StringComparison.Ordinal), "The matching generated file should be visible.");
    Ensure(
        generatorTree[0].Children[0].Detail.Contains("先頭100件を表示、全件はexport", StringComparison.Ordinal),
        "A truncated generator tree node must direct users to full export.");
    Ensure(ResultTreeBuilder.BuildGenerators(new[] { generator }, "Missing").Count == 0, "Nonmatching generator branches should be removed.");
    return Task.CompletedTask;
}

static ProfileRun CreateHistoricalRun(string targetPath, string configuration, DateTimeOffset startedAt)
{
    return new ProfileRun
    {
        TargetPath = targetPath,
        TargetName = System.IO.Path.GetFileNameWithoutExtension(targetPath),
        Configuration = configuration,
        Mode = ProfileMode.Warm,
        StartedAt = startedAt,
        FinishedAt = startedAt.AddSeconds(1),
        Status = RunStatus.Succeeded,
        Environment = new EnvironmentSnapshot(
            "Windows",
            "x64",
            1,
            ".NET",
            "10.0.100",
            null,
            null,
            false),
        Isolated = true,
    };
}

static async Task DiscoveryDiscardsStaleResultsAsync()
{
    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yaap-gui-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try
    {
        string first = System.IO.Path.Combine(path, "First.csproj");
        string second = System.IO.Path.Combine(path, "Second.csproj");
        await File.WriteAllTextAsync(first, "<Project />");
        await File.WriteAllTextAsync(second, "<Project />");
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        static TargetInfo Result(string target, string configuration) => new(
            target,
            ".csproj",
            new[] { configuration },
            new[] { "net8.0" });
        using MainViewModel viewModel = new(
            targetDiscoverer: async (target, _) =>
            {
                if (target == first)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                    return Result(target, "Stale");
                }

                return Result(target, "Current");
            },
            targetDiscoveryDelay: TimeSpan.Zero);
        viewModel.TargetPath = first;
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.TargetPath = second;
        Task current = viewModel.WaitForTargetDiscoveryAsync();
        await current.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirst.SetResult();
        await Task.Delay(50);
        Ensure(viewModel.Configurations.SequenceEqual(new[] { "Current" }), "A stale discovery result replaced the current target.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static async Task AsyncCommandAsync()
{
    TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool executed = false;
    AsyncRelayCommand command = new(
        async _ =>
        {
            await Task.Yield();
            executed = true;
            completion.SetResult();
        });
    command.Execute(null);
    await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Ensure(executed, "Async command did not execute.");

    TaskCompletionSource cancellationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? cancellationError = null;
    AsyncRelayCommand cancelable = new(
        async cancellationToken =>
        {
            cancellationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        },
        onError: exception => cancellationError = exception);
    cancelable.Execute(null);
    await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Ensure(cancelable.IsExecuting, "Cancelable command did not enter the executing state.");
    cancelable.Cancel();
    for (int attempt = 0; attempt < 100 && cancelable.IsExecuting; attempt++)
    {
        await Task.Delay(10);
    }

    Ensure(!cancelable.IsExecuting, "Cancelable command did not finish after cancellation.");
    Ensure(cancellationError is null, "Normal command cancellation must not be reported as an error.");
}

static Task MeasurementStateAsync()
{
    string[] configurations = new[] { "Debug", "Release" };
    MeasurementStatePresentation running = MeasurementStatePresentation.Create(
        isRunning: true,
        isDiscovering: false,
        hasValidTarget: true,
        "sample.csproj",
        "Release",
        configurations);
    Ensure(!running.CanStart, "Start must be disabled while a measurement runs.");
    Ensure(running.Text == "測定中: Release 構成", "Running text should describe progress without an error-like prefix.");
    Ensure(!running.Text.Contains("開始できません", StringComparison.Ordinal), "Running text must not claim that measurement could not start.");

    MeasurementStatePresentation discovering = MeasurementStatePresentation.Create(
        isRunning: false,
        isDiscovering: true,
        hasValidTarget: false,
        "sample.csproj",
        string.Empty,
        configurations);
    Ensure(discovering.Text.Contains("確認中", StringComparison.Ordinal), "Discovery should be presented as progress.");

    MeasurementStatePresentation missing = MeasurementStatePresentation.Create(
        false,
        false,
        true,
        "sample.csproj",
        string.Empty,
        configurations);
    Ensure(!missing.CanStart && missing.Text.Contains("ビルド構成を選択", StringComparison.Ordinal), "Blank selection should be blocked.");

    MeasurementStatePresentation unknown = MeasurementStatePresentation.Create(
        false,
        false,
        true,
        "sample.csproj",
        "Profile",
        configurations);
    Ensure(unknown.CanStart && unknown.Text.Contains("未検出", StringComparison.Ordinal), "A custom configuration should be allowed with an explicit warning.");

    MeasurementStatePresentation ready = MeasurementStatePresentation.Create(
        false,
        false,
        true,
        "sample.csproj",
        "Release",
        configurations);
    Ensure(ready.CanStart && ready.Text == "測定可能: Release 構成", "A discovered selection should be ready.");
    return Task.CompletedTask;
}

static Task ResultTreeCancellationAsync()
{
    StatisticalMetric[] metrics = Enumerable.Range(0, 100_000)
        .Select(index => new StatisticalMetric(
            $"Analyzer{index}",
            "Assembly",
            MetricKind.Analyzer,
            null,
            index,
            index,
            index,
            0,
            1))
        .ToArray();
    using CancellationTokenSource preCanceled = new();
    preCanceled.Cancel();
    EnsureCanceled(
        () => ResultTreeBuilder.BuildAnalyzers(metrics, null, preCanceled.Token),
        "Result tree construction ignored a pre-canceled token.");

    using CancellationTokenSource sortingCancellation = new();
    EnsureCanceled(
        () => ResultTreeBuilder.BuildAnalyzers(
            CancelAfterEnumeration(metrics, sortingCancellation),
            null,
            sortingCancellation.Token),
        "Result tree sorting ignored cancellation after enumeration.");
    return Task.CompletedTask;
}

static IEnumerable<T> CancelAfterEnumeration<T>(
    IEnumerable<T> values,
    CancellationTokenSource cancellation)
{
    foreach (T value in values)
    {
        yield return value;
    }

    cancellation.Cancel();
}

static void EnsureCanceled(Action action, string message)
{
    try
    {
        action();
    }
    catch (OperationCanceledException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static async Task ShutdownRetryAfterChildExitAsync()
{
    using Process child = Process.Start(new ProcessStartInfo(
        "pwsh",
        "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
    {
        CreateNoWindow = true,
        UseShellExecute = false,
    }) ?? throw new InvalidOperationException("Shutdown test child process did not start.");
    using MainViewModel viewModel = new(targetDiscoveryDelay: TimeSpan.Zero);
    SetPrivateField(
        viewModel,
        "_shutdownBlocker",
        new ProcessDidNotTerminateException(child.Id));
    try
    {
        await viewModel.ShutdownAsync();
        throw new InvalidOperationException("Shutdown should refuse while the child process is alive.");
    }
    catch (ProcessDidNotTerminateException exception)
    {
        Ensure(exception.ProcessId == child.Id, "Shutdown reported the wrong child PID.");
    }

    child.Kill(entireProcessTree: true);
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
}

static async Task FailureObservabilityAsync()
{
    string path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "yaap-gui-tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try
    {
        string project = System.IO.Path.Combine(path, "Failure.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        ProfileRun failedRun = CreateHistoricalRun(project, "Release", DateTimeOffset.UtcNow);
        failedRun.Status = RunStatus.Failed;
        failedRun.Diagnostics = new[]
        {
            YaapErrors.ProcessFailed(
                ProcessOperation.Clean,
                17,
                $"実行コマンド: dotnet clean {project}\n作業ディレクトリ: {path}\n完全ログ: {path}/logs/clean-001.log"),
        };
        StubGuiProfileRunner runner = new((_, _, _) => Task.FromResult(failedRun));
        using MainViewModel viewModel = new(
            runner,
            targetDiscoverer: (target, _) => Task.FromResult(new TargetInfo(
                System.IO.Path.GetFullPath(target),
                ".csproj",
                new[] { "Release" },
                new[] { "net8.0" })),
            targetDiscoveryDelay: TimeSpan.Zero)
        {
            HistoryPath = System.IO.Path.Combine(path, "history"),
        };
        viewModel.TargetPath = project;
        await viewModel.WaitForTargetDiscoveryAsync();
        Ensure(viewModel.StartCommand.CanExecute(null), "The failure fixture must be ready to measure.");
        viewModel.StartCommand.Execute(null);
        AsyncRelayCommand start = (AsyncRelayCommand)viewModel.StartCommand;
        for (int attempt = 0; attempt < 200 && start.IsExecuting; attempt++)
        {
            await Task.Delay(10);
        }

        Ensure(!start.IsExecuting, "The failed measurement command did not complete.");
        Ensure(ReferenceEquals(failedRun, viewModel.SelectedRun), "The failed run must remain selected.");
        Ensure(viewModel.StatusTitleText == "測定失敗", "A failed run must have a Japanese failure heading.");
        Ensure(viewModel.StatusText.Contains("YAAP2001", StringComparison.Ordinal), "The failure code must be immediately visible.");
        Ensure(viewModel.StatusText.Contains("dotnet clean", StringComparison.Ordinal), "The failed operation must be immediately visible.");
        Ensure(viewModel.StatusText.Contains("トラブルシュート", StringComparison.Ordinal), "The GUI must direct users to detailed diagnostics.");
        Ensure(!viewModel.StatusText.Contains("Failed", StringComparison.Ordinal), "The GUI must not expose an English enum as the failure explanation.");
        RunDiagnostic visible = viewModel.Diagnostics.Single();
        Ensure(ReferenceEquals(visible, viewModel.SelectedDiagnostic), "The primary failure diagnostic must be selected automatically.");
        Ensure(visible.Detail.Contains("完全ログ", StringComparison.Ordinal), "The retained log path must be available in the GUI diagnostics.");
        Ensure(visible.SuggestedAction.Contains("Clean target", StringComparison.Ordinal), "The GUI diagnostics must expose clean-specific recovery guidance.");

        ProfileRun partialRun = CreateHistoricalRun(project, "Release", DateTimeOffset.UtcNow);
        partialRun.Status = RunStatus.Partial;
        partialRun.Diagnostics = failedRun.Diagnostics;
        SetPrivateProperty(viewModel, nameof(MainViewModel.SelectedRun), partialRun);
        Ensure(viewModel.StatusTitleText == "部分結果", "A partial run must have a Japanese warning heading.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static Task ThemeFrameworkAsync()
{
    Ensure(ThemeManager.ToApplicationTheme(AppThemeMode.Auto) == ApplicationTheme.Unknown, "Auto should delegate to the system theme watcher.");
    Ensure(ThemeManager.ToApplicationTheme(AppThemeMode.Light) == ApplicationTheme.Light, "Light should map to WPF UI light.");
    Ensure(ThemeManager.ToApplicationTheme(AppThemeMode.Dark) == ApplicationTheme.Dark, "Dark should map to WPF UI dark.");
    return Task.CompletedTask;
}

static async Task XamlContractAsync()
{
    string root = FindRepositoryRoot();
    string xaml = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "src", "Yaap.Gui", "MainWindow.xaml"));
    string viewModel = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "src", "Yaap.Gui", "MainViewModel.cs"));
    string appXaml = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "src", "Yaap.Gui", "App.xaml"));
    string notices = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
    Ensure(xaml.Contains("x:Key=\"ScrollableDataGridStyle\"", StringComparison.Ordinal), "Large grids must share a scrolling contract.");
    Ensure(xaml.Contains("x:Key=\"VirtualizedTreeViewStyle\"", StringComparison.Ordinal), "Large trees must share a scrolling contract.");
    Ensure(xaml.Contains("VerticalScrollBarVisibility\" Value=\"Auto\"", StringComparison.Ordinal), "Large result controls must expose vertical scrollbars.");
    Ensure(xaml.Contains("VirtualizationMode\" Value=\"Recycling\"", StringComparison.Ordinal), "Recycling virtualization is required.");
    Ensure(xaml.Contains("Grid.Row=\"2\"", StringComparison.Ordinal), "The Analyzer result view must occupy the bounded star row.");
    Ensure(xaml.Contains("生成ファイル単位の実行時間", StringComparison.Ordinal), "Generator timing disclaimer is required.");
    Ensure(xaml.Contains("先頭100件を表示しています。全件はexportで確認できます。", StringComparison.Ordinal), "Truncated generated-output previews must explain full export.");
    Ensure(xaml.Contains("SelectedItem.OutputsTruncated", StringComparison.Ordinal), "The truncated-preview notice must be conditional.");
    Ensure(viewModel.Contains("StreamGeneratedOutputsAsync(SelectedRun.Id, cancellationToken)", StringComparison.Ordinal), "GUI export must stream the complete generated-output manifest.");
    Ensure(xaml.Contains("キャンセル", StringComparison.Ordinal), "Cancellation UI is required.");
    Ensure(xaml.Contains("ResultFilter", StringComparison.Ordinal), "Analyzer and generator filtering is required.");
    Ensure(xaml.Contains("PlaceholderText=\"*.csproj; *.slnx; *.sln\"", StringComparison.Ordinal), "The target placeholder is required.");
    Ensure(xaml.Contains("RecentTargets", StringComparison.Ordinal), "Recent targets must be selectable from history.");
    Ensure(xaml.Contains("PlaceholderText=\"Analyzer、診断ID、アセンブリを検索\"", StringComparison.Ordinal), "The analyzer search placeholder is required.");
    Ensure(xaml.Contains("PlaceholderText=\"Generator、アセンブリ、生成ファイルを検索\"", StringComparison.Ordinal), "The generator search placeholder is required.");
    Ensure(xaml.Contains("ItemsSource=\"{Binding AnalyzerTree}\"", StringComparison.Ordinal), "The analyzer tree view is required.");
    Ensure(xaml.Contains("x:Key=\"AnalyzerResultSurfaceStyle\"", StringComparison.Ordinal), "Analyzer views must share one result-surface contract.");
    Ensure(xaml.Contains("x:Key=\"ResultColumnHeaderStyle\"", StringComparison.Ordinal), "Analyzer table headers must expose resize boundaries.");
    Ensure(xaml.Contains("Header=\"詳細をコピー\"", StringComparison.Ordinal), "Analyzer table and tree items must expose a shared copy action.");
    Ensure(xaml.Contains("OnAnalyzerGridPreviewMouseRightButtonDown", StringComparison.Ordinal), "Analyzer table right-click must select its target row.");
    Ensure(xaml.Contains("OnAnalyzerTreePreviewMouseRightButtonDown", StringComparison.Ordinal), "Analyzer tree right-click must select its target node.");
    Ensure(xaml.Contains("表示する Analyzer 結果がありません", StringComparison.Ordinal), "Both Analyzer views must explain empty results.");
    Ensure(xaml.Contains("MinimumMilliseconds", StringComparison.Ordinal) && xaml.Contains("MaximumMilliseconds", StringComparison.Ordinal), "Analyzer tree leaves must expose the complete timing range.");
    Ensure(xaml.Contains("ItemsSource=\"{Binding GeneratorTree}\"", StringComparison.Ordinal), "The generator tree view is required.");
    Ensure(xaml.Contains("Header=\"設定\"", StringComparison.Ordinal), "The settings tab is required.");
    Ensure(xaml.Contains("AccentFillColorDefaultBrush", StringComparison.Ordinal), "Selected main tabs should have a visible accent.");
    Ensure(xaml.Contains("TextOnAccentFillColorPrimaryBrush", StringComparison.Ordinal), "Selected tab text must contrast with the accent.");
    Ensure(xaml.Contains("x:Name=\"RecentTargetsButton\"", StringComparison.Ordinal), "Recent targets must use a compact button.");
    Ensure(xaml.Contains("x:Name=\"RecentTargetsPopup\"", StringComparison.Ordinal), "Recent targets must use a reliable popup.");
    Ensure(!xaml.Contains("最近使用 ▼", StringComparison.Ordinal), "Recent targets must not use a text triangle.");
    Ensure(xaml.Contains("ChevronDown16", StringComparison.Ordinal), "Recent targets must use a Fluent chevron.");
    Ensure(xaml.Contains("HorizontalAlignment=\"Stretch\"", StringComparison.Ordinal), "Recent-target items must stretch to a uniform width.");
    Ensure(xaml.Contains("x:Name=\"AdvancedSettingsPopup\"", StringComparison.Ordinal), "Advanced settings must use a compact popup.");
    Ensure(xaml.Contains("x:Name=\"RestoreCheckBox\"", StringComparison.Ordinal), "Advanced settings must expose restore control.");
    Ensure(xaml.Contains("IsChecked=\"{Binding Restore}\"", StringComparison.Ordinal), "The restore control must bind to the view model.");
    Ensure(viewModel.Contains("Restore = Restore", StringComparison.Ordinal), "The GUI restore setting must reach ProfileOptions.");
    Ensure(xaml.Contains("<DatePicker", StringComparison.Ordinal), "History date filters must provide calendar pickers.");
    Ensure(xaml.Contains("x:Key=\"OpaqueCalendarStyle\"", StringComparison.Ordinal), "History calendars must use an opaque theme-aware style.");
    Ensure(xaml.Contains("CalendarStyle=\"{StaticResource OpaqueCalendarStyle}\"", StringComparison.Ordinal), "Both history date pickers must use the opaque calendar style.");
    Ensure(xaml.Contains("Text=\"{Binding HistoryFrom, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal), "The history start date must update before focus changes.");
    Ensure(xaml.Contains("Text=\"{Binding HistoryTo, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal), "The history end date must update before focus changes.");
    Ensure(xaml.Contains("SelectedDate=\"{Binding HistoryFromDate, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal), "The history start calendar selection must stay synchronized.");
    Ensure(xaml.Contains("SelectedDate=\"{Binding HistoryToDate, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal), "The history end calendar selection must stay synchronized.");
    Ensure(xaml.Contains("Command=\"{Binding ClearHistoryPeriodCommand}\"", StringComparison.Ordinal), "History period filters must provide one-step clearing.");
    Ensure(xaml.Contains("x:Key=\"CalendarNavigationButtonStyle\"", StringComparison.Ordinal), "Calendar navigation must use an explicit theme-aware style.");
    Ensure(xaml.Contains("x:Name=\"CalendarNavigationGlyph\"", StringComparison.Ordinal), "Calendar navigation must use centered vector glyphs.");
    Ensure(xaml.Contains("x:Key=\"IconActionButtonStyle\"", StringComparison.Ordinal), "Compact icon actions must expose complete interaction states.");
    Ensure(xaml.Contains("x:Name=\"HistoryPeriodClearIcon\"", StringComparison.Ordinal), "History period clearing must use a centered vector icon.");
    Ensure(!xaml.Contains("Content=\"×\"", StringComparison.Ordinal), "History period clearing must not depend on a font multiplication glyph.");
    Ensure(xaml.Contains("x:Name=\"HistoryPeriodPanel\"", StringComparison.Ordinal) && xaml.Contains("x:Name=\"HistoryRefreshButton\"", StringComparison.Ordinal), "History period and refresh layout must remain testable.");
    Ensure(xaml.Contains("HistoryLimit, UpdateSourceTrigger=LostFocus", StringComparison.Ordinal), "The history display limit must live in Settings.");
    Ensure(xaml.Contains("Header=\"ラベル\"", StringComparison.Ordinal), "History labels must be visible.");
    Ensure(!xaml.Contains("Binding Id}\" Header=\"ID\"", StringComparison.Ordinal), "Internal history IDs must not be shown.");
    Ensure(!xaml.Contains("詳細を遅延読込", StringComparison.Ordinal), "Implementation-oriented history wording must not be shown.");
    Ensure(!xaml.Contains("選択履歴を削除", StringComparison.Ordinal), "History delete must not occupy a primary toolbar button.");
    Ensure(xaml.Contains("<ContextMenu>", StringComparison.Ordinal) && xaml.Contains("Header=\"削除\"", StringComparison.Ordinal), "History delete must be available from a context menu.");
    Ensure(xaml.Contains("Header=\"読み込み\"", StringComparison.Ordinal), "History load must be available from the context menu.");
    Ensure(!xaml.Contains("Content=\"読み込み\"", StringComparison.Ordinal), "History load must not occupy a toolbar button.");
    Ensure(!xaml.Contains("Symbol=\"ArrowUndo20\"", StringComparison.Ordinal) && !xaml.Contains("Symbol=\"ArrowRedo20\"", StringComparison.Ordinal), "History label Undo/Redo must not use misleading visible buttons.");
    Ensure(xaml.Contains("<ui:TextBox.InputBindings>", StringComparison.Ordinal) && xaml.Contains("Modifiers=\"Control\"", StringComparison.Ordinal), "History label Undo/Redo must remain available from focused keyboard shortcuts.");
    Ensure(xaml.Contains("DisplayMemberPath=\"DisplayText\"", StringComparison.Ordinal), "Comparison must use readable history selectors.");
    Ensure(!xaml.Contains("Header=\"出力・トラブルシュート\"", StringComparison.Ordinal), "Export and troubleshooting must be separate tabs.");
    Ensure(xaml.Contains("Header=\"出力\"", StringComparison.Ordinal) && xaml.Contains("Header=\"トラブルシュート\"", StringComparison.Ordinal), "Export and troubleshooting tabs are required.");
    Ensure(xaml.Contains("Command=\"{Binding BrowseExportCommand}\"", StringComparison.Ordinal), "Export must provide a save-file picker.");
    Ensure(!xaml.Contains("SelectedValue=\"{Binding ExportFormat}\"", StringComparison.Ordinal), "Export must not duplicate format in a separate selector.");
    Ensure(xaml.Contains("現在読み込んでいる測定結果を保存します。参照ダイアログでJSON、CSV、Markdownの形式を選択できます。", StringComparison.Ordinal), "Export guidance must list every supported format.");
    Ensure(
        xaml.Split("PlaceholderText=\"パスを入力、または参照\"", StringSplitOptions.None).Length - 1 == 2,
        "Export and binlog inputs must accurately guide both typing and browsing.");
    Ensure(viewModel.Contains("GetExportFormat(ExportPath)", StringComparison.Ordinal), "GUI export must infer its format from the selected file.");
    Ensure(viewModel.Contains("IsBusySurfaceVisible", StringComparison.Ordinal), "Lightweight history loading must not disturb the tab layout.");
    Ensure(xaml.Contains("Handler=\"OnScrollableControlLoaded\"", StringComparison.Ordinal), "Large controls must enlarge scrollbar pointer targets at runtime.");
    Ensure(xaml.Contains("Handler=\"OnAccessibleScrollBarLoaded\"", StringComparison.Ordinal), "Dynamically created scrollbars must receive accessible sizing.");
    Ensure(xaml.Contains("Handler=\"OnAccessibleScrollBarUnloaded\"", StringComparison.Ordinal), "Scrollbar layout monitoring must detach cleanly on unload.");
    Ensure(xaml.Contains("Command=\"{Binding BrowseHistoryDirectoryCommand}\"", StringComparison.Ordinal), "History paths must provide a folder picker.");
    Ensure(xaml.Contains("Command=\"{Binding BrowseArtifactsDirectoryCommand}\"", StringComparison.Ordinal), "Artifact paths must provide a folder picker.");
    Ensure(xaml.Contains("Command=\"{Binding AnalyzeBinlogCommand}\"", StringComparison.Ordinal), "GUI must expose existing-binlog analysis.");
    Ensure(xaml.Contains("SelectedItem=\"{Binding SelectedDiagnostic}\"", StringComparison.Ordinal), "Troubleshooting must select a diagnostic for detailed inspection.");
    Ensure(xaml.Contains("x:Name=\"DiagnosticActionText\"", StringComparison.Ordinal), "Troubleshooting must expose complete recovery guidance.");
    Ensure(xaml.Contains("x:Name=\"DiagnosticDetailText\"", StringComparison.Ordinal), "Troubleshooting must expose a copyable detail and log surface.");
    Ensure(xaml.Contains("TextWrapping=\"NoWrap\"", StringComparison.Ordinal) && xaml.Contains("HorizontalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal), "Long process logs must remain scrollable without reflow.");
    Ensure(xaml.Contains("Symbol=\"Options20\"", StringComparison.Ordinal), "Advanced settings must use a Fluent options icon.");
    Ensure(!xaml.Contains("<Expander", StringComparison.Ordinal), "Advanced settings must not reserve an expander row.");
    Ensure(xaml.Contains("NumericCellTextStyle", StringComparison.Ordinal), "Numeric cells must share an alignment style.");
    Ensure(xaml.Contains("Typography.NumeralAlignment", StringComparison.Ordinal), "Numeric cells must use tabular numerals.");
    Ensure(xaml.Contains("NumericColumnHeaderStyle", StringComparison.Ordinal), "Numeric headers must align with values.");
    Ensure(xaml.Contains("ui:ProgressRing", StringComparison.Ordinal), "Measurement progress must be visually prominent.");
    Ensure(xaml.Contains("x:Name=\"BusyCancelButton\"", StringComparison.Ordinal), "Cancel must remain available on the busy surface.");
    Ensure(xaml.Contains("x:Name=\"StatusBar\"", StringComparison.Ordinal), "The idle status surface must have a testable identity.");
    Ensure(xaml.Contains("Severity=\"{Binding StatusSeverity}\"", StringComparison.Ordinal), "Failed and partial measurements must expose status severity.");
    Ensure(xaml.Contains("Title=\"{Binding StatusTitleText}\"", StringComparison.Ordinal), "Measurement outcomes must expose a localized status heading.");
    Ensure(xaml.Contains("Text=\"{Binding BusyTitleText}\"", StringComparison.Ordinal), "The busy surface must describe measurement and secondary operations.");
    Ensure(xaml.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "Busy and status changes must be announced to assistive technology.");
    Ensure(!xaml.Contains("Text=\"測定を実行しています\"", StringComparison.Ordinal), "The busy surface must not duplicate measurement-state wording.");
    Ensure(!xaml.Contains("Header=\"標本\"", StringComparison.Ordinal), "The Analyzer table must not expose sample count.");
    Ensure(xaml.Contains("x:Name=\"StartButton\"", StringComparison.Ordinal), "The primary measurement action must be testable.");
    Ensure(xaml.Contains("AllowDrop=\"True\"", StringComparison.Ordinal), "File drop must be enabled.");
    Ensure(xaml.Contains("PreviewDrop=\"OnPreviewDrop\"", StringComparison.Ordinal), "File drop must be handled.");
    Ensure(!xaml.Contains("DiscoverCommand", StringComparison.Ordinal), "Manual discovery should not remain in the GUI.");
    Ensure(xaml.Contains("IsEditable=\"True\"", StringComparison.Ordinal), "Imported or custom configurations must remain enterable.");
    Ensure(xaml.Contains("Text=\"{Binding Configuration, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal), "Editable configuration text must update the selected configuration.");
    Ensure(xaml.Contains("MeasurementStateText", StringComparison.Ordinal), "Measurement state must always be visible.");
    Ensure(xaml.Contains("ElementName=AdvancedSettingsButton", StringComparison.Ordinal), "Advanced settings should be closed until its icon button is toggled.");
    Ensure(xaml.Contains("SelectedTheme", StringComparison.Ordinal), "The theme selector is required.");
    Ensure(xaml.Contains("ui:FluentWindow", StringComparison.Ordinal), "The window must use the WPF UI Fluent foundation.");
    Ensure(xaml.Contains("ui:TitleBar", StringComparison.Ordinal), "The Fluent window must retain visible window controls and a draggable title bar.");
    Ensure(xaml.Contains("ui:InfoBar", StringComparison.Ordinal), "Measurement status should use a coherent themed component.");
    Ensure(xaml.Contains("ui:DataGrid", StringComparison.Ordinal), "Result grids must use the theme-aware WPF UI control.");
    Ensure(xaml.Contains("x:Name=\"ConfigurationSelector\"", StringComparison.Ordinal), "The configuration selector contract is required.");
    Ensure(appXaml.Contains("ui:ThemesDictionary", StringComparison.Ordinal), "WPF UI theme resources are required.");
    Ensure(appXaml.Contains("ui:ControlsDictionary", StringComparison.Ordinal), "WPF UI control resources are required.");
    Ensure(!appXaml.Contains("WindowBackgroundBrush", StringComparison.Ordinal), "The removed hand-authored palette must not return.");
    Ensure(!appXaml.Contains("ControlTemplate", StringComparison.Ordinal), "Base control templates must come from the UI framework.");
    Ensure(notices.Contains("WPF UI LICENSE", StringComparison.Ordinal), "The WPF UI license must accompany distributions.");
}

static string FindRepositoryRoot()
{
    DirectoryInfo? current = new(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(System.IO.Path.Combine(current.FullName, "YetAnotherAnalyzerProfiler.slnx")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException("Repository root was not found.");
}

static void Ensure(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class StubGuiProfileRunner : IProfileRunner
{
    private readonly Func<ProfileOptions, IProgress<ProfileProgress>?, CancellationToken, Task<ProfileRun>> _run;

    public StubGuiProfileRunner(
        Func<ProfileOptions, IProgress<ProfileProgress>?, CancellationToken, Task<ProfileRun>> run)
    {
        _run = run;
    }

    public Task<ProfileRun> RunAsync(
        ProfileOptions options,
        IProgress<ProfileProgress>? progress = null,
        CancellationToken cancellationToken = default) => _run(options, progress, cancellationToken);
}
