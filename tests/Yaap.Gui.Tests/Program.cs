using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Yaap.Core;
using Yaap.Gui;

List<(string Name, Func<Task> Body)> tests = new()
{
    ("gui.viewmodel-initialization", ViewModelInitializationAsync),
    ("gui.window-startup-smoke", WindowStartupSmokeAsync),
    ("gui.drop-and-auto-discovery", DropAndAutoDiscoveryAsync),
    ("gui.recent-target-ordering", RecentTargetOrderingAsync),
    ("gui.configuration-priority", ConfigurationPriorityAsync),
    ("gui.configuration-history", ConfigurationHistoryAsync),
    ("gui.result-tree-filtering", ResultTreeFilteringAsync),
    ("gui.discovery-discards-stale-results", DiscoveryDiscardsStaleResultsAsync),
    ("gui.measurement-state", MeasurementStateAsync),
    ("gui.theme-framework", ThemeFrameworkAsync),
    ("gui.async-command", AsyncCommandAsync),
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
    TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Thread thread = new(() =>
    {
        App? app = null;
        try
        {
            app = new App();
            app.InitializeComponent();
            using MainViewModel viewModel = new(targetDiscoveryDelay: TimeSpan.Zero)
            {
                HistoryPath = historyPath,
            };
            SetPrivateProperty(viewModel, nameof(MainViewModel.SelectedRun), CreateVisualRun());
            MainWindow window = new(viewModel);
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            TabControl mainTabs = (TabControl)window.FindName("MainTabs");
            TabControl analyzerViewTabs = (TabControl)window.FindName("AnalyzerViewTabs");
            TreeView analyzerTreeView = (TreeView)window.FindName("AnalyzerTreeView");
            FrameworkElement targetCard = (FrameworkElement)window.FindName("TargetCard");
            FrameworkElement busyCard = (FrameworkElement)window.FindName("BusyCard");
            Wpf.Ui.Controls.InfoBar statusBar =
                (Wpf.Ui.Controls.InfoBar)window.FindName("StatusBar");
            Button startButton = (Button)window.FindName("StartButton");
            TextBlock busyTitle = (TextBlock)window.FindName("BusyTitle");
            TextBlock busyMessage = (TextBlock)window.FindName("BusyMessage");
            Button cancelButton = (Button)window.FindName("BusyCancelButton");
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
            Wpf.Ui.Controls.SymbolIcon recentTargetsChevron =
                FindVisualDescendant<Wpf.Ui.Controls.SymbolIcon>(recentTargetsButton) ??
                throw new InvalidOperationException("The recent-target chevron was not rendered.");
            Wpf.Ui.Controls.SymbolIcon advancedSettingsIcon =
                FindVisualDescendant<Wpf.Ui.Controls.SymbolIcon>(advancedSettingsButton) ??
                throw new InvalidOperationException("The advanced-settings icon was not rendered.");
            DataGrid analyzerGrid = (DataGrid)window.FindName("AnalyzerGrid");
            TextBlock compareBaselineLabel = (TextBlock)window.FindName("CompareBaselineLabel");
            TextBlock exportFormatLabel = (TextBlock)window.FindName("ExportFormatLabel");
            TextBlock settingsTitle = (TextBlock)window.FindName("SettingsTitle");
            Ensure(
                ReferenceEquals(recentTargetsPopup.DataContext, viewModel),
                "The recent-target popup must bind to the window view model.");
            Ensure(
                ReferenceEquals(advancedSettingsPopup.DataContext, viewModel),
                "The advanced-settings popup must bind to the window view model.");
            Ensure(targetCard.ActualHeight < 80, "Collapsed advanced settings must not reserve a second row.");
            Ensure(
                recentTargetsChevron.Symbol == Wpf.Ui.Controls.SymbolRegular.ChevronDown16,
                "The recent-target button must use a Fluent down chevron.");
            Ensure(
                advancedSettingsIcon.Symbol == Wpf.Ui.Controls.SymbolRegular.Options20,
                "Advanced settings must use a compact Fluent options icon.");
            DataGridCell analyzerMeanCell = GetDataGridCell(analyzerGrid, 0, 3);
            TextBlock analyzerMeanText = FindVisualDescendant<TextBlock>(analyzerMeanCell) ??
                throw new InvalidOperationException("The analyzer mean cell text was not rendered.");
            Ensure(analyzerMeanText.TextAlignment == TextAlignment.Right, "Timing values must be right-aligned.");
            Ensure(
                Typography.GetNumeralAlignment(analyzerMeanText) == FontNumeralAlignment.Tabular,
                "Timing values must use tabular numerals.");
            Ensure(analyzerGrid.Columns.Count == 6, "The Analyzer table must omit the low-value sample-count column.");
            Ensure(
                analyzerGrid.Columns.All(column => !string.Equals(column.Header?.ToString(), "標本", StringComparison.Ordinal)),
                "The Analyzer table must not present sample count as 標本.");
            Ensure(startButton.FontWeight == FontWeights.SemiBold, "The primary measurement action must use emphasized text.");
            Ensure(busyCard.Visibility == Visibility.Collapsed, "The measurement progress surface must be hidden while idle.");
            Ensure(statusBar.Visibility == Visibility.Visible, "The persistent status bar must be visible while idle.");
            recentTargetsButton.IsChecked = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Ensure(recentTargetsPopup.IsOpen, "The empty recent-target popup should still open.");
            Ensure(
                recentTargetsEmptyMessage.Visibility == Visibility.Visible,
                "The empty recent-target popup should explain that there are no items.");
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
                busyTitle.Text == viewModel.MeasurementStateText,
                "The busy heading must use the canonical measurement state text.");
            Ensure(busyMessage.Text == viewModel.StatusText, "The busy surface must show the current progress message.");
            Ensure(cancelButton.IsEnabled, "Cancel must remain enabled while measuring.");
            viewModel.RecentTargets.Add(new RecentTarget(
                System.IO.Path.GetFileName(recentTargetPath),
                recentTargetPath,
                DateTimeOffset.UtcNow));
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
                SetPrivateProperty(viewModel, nameof(MainViewModel.StatusText), "直近の測定結果を表示しています。");
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
                        EnsureReadableForeground(exportFormatLabel.Foreground, mode, "ExportFormatLabel");
                    }
                    else if (index == 5)
                    {
                        EnsureReadableForeground(settingsTitle.Foreground, mode, "SettingsTitle");
                    }

                    CaptureWindow(
                        window,
                        captureDirectory,
                        $"{mode.ToString().ToLowerInvariant()}-tab-{index + 1}");

                    if (index == 0)
                    {
                        analyzerViewTabs.SelectedIndex = 1;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        analyzerTreeView.UpdateLayout();
                        TreeViewItem analyzerAssembly =
                            analyzerTreeView.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem ??
                            throw new InvalidOperationException("The Analyzer tree root was not rendered.");
                        analyzerAssembly.IsExpanded = true;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        CaptureWindow(
                            window,
                            captureDirectory,
                            $"{mode.ToString().ToLowerInvariant()}-analyzer-tree");
                        analyzerViewTabs.SelectedIndex = 0;
                    }
                }

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
        Exception? exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
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
    element.UpdateLayout();
    int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
    int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
    RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(element);
    Ensure(bitmap.PixelWidth == width && bitmap.PixelHeight == height, "The GUI render bitmap is invalid.");
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
    double foregroundLuminance = RelativeLuminance(((SolidColorBrush)foreground).Color);
    double backgroundLuminance = RelativeLuminance(((SolidColorBrush)background).Color);
    double ratio = (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
        (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    Ensure(ratio >= 4.5, $"{name} contrast ratio {ratio:N2} is below 4.5:1.");
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

static ProfileRun CreateVisualRun()
{
    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
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
        Analyzers = new[]
        {
            new StatisticalMetric(
                "Sample.Analyzers.PerformanceAnalyzer",
                "Sample.Analyzers",
                MetricKind.Analyzer,
                null,
                12.5,
                11,
                14,
                1.2,
                3),
            new StatisticalMetric(
                "Sample.Analyzers.CompilationAnalyzer",
                "Sample.Analyzers",
                MetricKind.Analyzer,
                null,
                1986,
                1831,
                2138,
                125.4,
                3),
            new StatisticalMetric(
                "Sample.Analyzers.SyntaxAnalyzer",
                "Sample.Analyzers",
                MetricKind.Analyzer,
                null,
                422.333,
                388,
                484,
                32.7,
                3),
        },
        Generators = new[]
        {
            new GeneratorMetric(
                "Sample.Generators.ModelGenerator",
                "Sample.Generators",
                8.5,
                8,
                9,
                0.5,
                3,
                1,
                256,
                12,
                new[] { new GeneratedOutput("Sample.Generators.ModelGenerator", "Generated/Model.g.cs", 256, 12) }),
        },
        Diagnostics = new[]
        {
            new RunDiagnostic("YAAP0000", "確認用診断", "テーマ描画確認", "操作は不要です。"),
        },
        Isolated = true,
    };
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
        Ensure(viewModel.RetentionCount == 50, "Default history retention should be available.");
        Ensure(viewModel.CancelCommand.CanExecute(null) == false, "Cancel should be disabled while idle.");
        Ensure(viewModel.SelectedTheme.Mode == AppThemeMode.Auto, "The system theme should be the default.");
        Ensure(viewModel.MeasurementStateText.Contains("測定対象", StringComparison.Ordinal), "The disabled-start reason should be visible.");
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
        Ensure(!viewModel.StartCommand.CanExecute(null), "A configuration absent from discovery must disable Start.");
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
            new GeneratedOutput("SampleGenerator", "Generated/First.g.cs", 100, 8),
            new GeneratedOutput("SampleGenerator", "Generated/Second.g.cs", 200, 12),
        });
    IReadOnlyList<ResultTreeNode> generatorTree = ResultTreeBuilder.BuildGenerators(
        new[] { generator },
        "Second");
    Ensure(generatorTree.Count == 1, "A generated-file match should retain its generator branch.");
    Ensure(generatorTree[0].Children[0].Children.Count == 1, "Only matching generated files should remain in a filtered tree.");
    Ensure(generatorTree[0].Children[0].Children[0].Name.EndsWith("Second.g.cs", StringComparison.Ordinal), "The matching generated file should be visible.");
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
    Ensure(!unknown.CanStart && unknown.Text.Contains("ビルド構成を選択", StringComparison.Ordinal), "Unknown selection should be blocked.");

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
    string appXaml = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "src", "Yaap.Gui", "App.xaml"));
    string notices = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
    Ensure(xaml.Contains("VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "Virtualization is required.");
    Ensure(xaml.Contains("生成ファイル単位の実行時間", StringComparison.Ordinal), "Generator timing disclaimer is required.");
    Ensure(xaml.Contains("キャンセル", StringComparison.Ordinal), "Cancellation UI is required.");
    Ensure(xaml.Contains("ResultFilter", StringComparison.Ordinal), "Analyzer and generator filtering is required.");
    Ensure(xaml.Contains("PlaceholderText=\"*.csproj; *.slnx; *.sln\"", StringComparison.Ordinal), "The target placeholder is required.");
    Ensure(xaml.Contains("RecentTargets", StringComparison.Ordinal), "Recent targets must be selectable from history.");
    Ensure(xaml.Contains("PlaceholderText=\"Analyzer、診断ID、アセンブリを検索\"", StringComparison.Ordinal), "The analyzer search placeholder is required.");
    Ensure(xaml.Contains("PlaceholderText=\"Generator、アセンブリ、生成ファイルを検索\"", StringComparison.Ordinal), "The generator search placeholder is required.");
    Ensure(xaml.Contains("ItemsSource=\"{Binding AnalyzerTree}\"", StringComparison.Ordinal), "The analyzer tree view is required.");
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
    Ensure(xaml.Contains("Symbol=\"Options20\"", StringComparison.Ordinal), "Advanced settings must use a Fluent options icon.");
    Ensure(!xaml.Contains("<Expander", StringComparison.Ordinal), "Advanced settings must not reserve an expander row.");
    Ensure(xaml.Contains("NumericCellTextStyle", StringComparison.Ordinal), "Numeric cells must share an alignment style.");
    Ensure(xaml.Contains("Typography.NumeralAlignment", StringComparison.Ordinal), "Numeric cells must use tabular numerals.");
    Ensure(xaml.Contains("NumericColumnHeaderStyle", StringComparison.Ordinal), "Numeric headers must align with values.");
    Ensure(xaml.Contains("ui:ProgressRing", StringComparison.Ordinal), "Measurement progress must be visually prominent.");
    Ensure(xaml.Contains("x:Name=\"BusyCancelButton\"", StringComparison.Ordinal), "Cancel must remain available on the busy surface.");
    Ensure(xaml.Contains("x:Name=\"StatusBar\"", StringComparison.Ordinal), "The idle status surface must have a testable identity.");
    Ensure(xaml.Contains("Text=\"{Binding MeasurementStateText}\"", StringComparison.Ordinal), "The busy surface must use the canonical measurement state.");
    Ensure(!xaml.Contains("Text=\"測定を実行しています\"", StringComparison.Ordinal), "The busy surface must not duplicate measurement-state wording.");
    Ensure(!xaml.Contains("Header=\"標本\"", StringComparison.Ordinal), "The Analyzer table must not expose sample count.");
    Ensure(xaml.Contains("x:Name=\"StartButton\"", StringComparison.Ordinal), "The primary measurement action must be testable.");
    Ensure(xaml.Contains("AllowDrop=\"True\"", StringComparison.Ordinal), "File drop must be enabled.");
    Ensure(xaml.Contains("PreviewDrop=\"OnPreviewDrop\"", StringComparison.Ordinal), "File drop must be handled.");
    Ensure(!xaml.Contains("DiscoverCommand", StringComparison.Ordinal), "Manual discovery should not remain in the GUI.");
    Ensure(xaml.Contains("SelectedItem=\"{Binding Configuration, Mode=TwoWay}\"", StringComparison.Ordinal), "Configuration selection must not use editable text binding.");
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
