using Yaap.Core;
using Yaap.Gui;

List<(string Name, Func<Task> Body)> tests = new()
{
    ("gui.viewmodel-initialization", ViewModelInitializationAsync),
    ("gui.drop-and-auto-discovery", DropAndAutoDiscoveryAsync),
    ("gui.configuration-priority", ConfigurationPriorityAsync),
    ("gui.configuration-history", ConfigurationHistoryAsync),
    ("gui.discovery-discards-stale-results", DiscoveryDiscardsStaleResultsAsync),
    ("gui.theme-palettes", ThemePalettesAsync),
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
        Ensure(viewModel.StartAvailabilityText.Contains("測定対象", StringComparison.Ordinal), "The disabled-start reason should be visible.");
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
        using MainViewModel viewModel = new(targetDiscoveryDelay: TimeSpan.Zero);
        Ensure(!viewModel.TrySetDroppedTarget(Array.Empty<string>()), "An empty drop should be rejected.");
        Ensure(!viewModel.TrySetDroppedTarget(new[] { project, project }), "A multi-file drop should be rejected.");
        Ensure(!viewModel.TrySetDroppedTarget(new[] { System.IO.Path.Combine(path, "Sample.txt") }), "An unsupported extension should be rejected.");
        Ensure(!viewModel.TrySetDroppedTarget(new[] { System.IO.Path.Combine(path, "Missing.csproj") }), "A missing project drop should be rejected.");
        Ensure(viewModel.TrySetDroppedTarget(new[] { project }), "A supported project drop should be accepted.");
        await viewModel.WaitForTargetDiscoveryAsync();
        Ensure(viewModel.TargetPath == System.IO.Path.GetFullPath(project), "The dropped target was not selected.");
        Ensure(viewModel.Configurations.SequenceEqual(new[] { "Debug", "Profile", "Release" }), "Configurations were not discovered.");
        Ensure(viewModel.Configuration == "Release", "Release should be preferred when the previous configuration is invalid.");
        Ensure(viewModel.StartCommand.CanExecute(null), "Start should be enabled after discovery.");
        Ensure(viewModel.StartAvailabilityText.Contains("測定可能", StringComparison.Ordinal), "The ready state should be visible.");
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
        historyFirst.TargetPath = project;
        await historyFirst.WaitForTargetDiscoveryAsync();
        Ensure(historyFirst.Configuration == "Profile", "The newest same-target history should win when history loads first.");
        Ensure(historyFirst.StartCommand.CanExecute(null), "A historical configuration should be ready to start.");
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
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

static Task ThemePalettesAsync()
{
    Ensure(ThemeManager.ResolveEffectiveMode(AppThemeMode.Auto, () => false) == AppThemeMode.Dark, "Auto should resolve to the system dark theme.");
    Ensure(ThemeManager.ResolveEffectiveMode(AppThemeMode.Auto, () => true) == AppThemeMode.Light, "Auto should resolve to the system light theme.");
    Ensure(ThemeManager.ResolveEffectiveMode(AppThemeMode.Dark, () => true) == AppThemeMode.Dark, "An explicit dark override should win.");
    ThemePalette light = ThemeManager.GetPalette(AppThemeMode.Light);
    ThemePalette dark = ThemeManager.GetPalette(AppThemeMode.Dark);
    Ensure(light.WindowBackground != dark.WindowBackground, "Light and dark palettes should differ.");
    Ensure(light.Foreground != dark.Foreground, "Theme foreground colors should differ.");
    return Task.CompletedTask;
}

static async Task XamlContractAsync()
{
    string root = FindRepositoryRoot();
    string xaml = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "src", "Yaap.Gui", "MainWindow.xaml"));
    string appXaml = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "src", "Yaap.Gui", "App.xaml"));
    Ensure(xaml.Contains("VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "Virtualization is required.");
    Ensure(xaml.Contains("生成ファイル単位の実行時間", StringComparison.Ordinal), "Generator timing disclaimer is required.");
    Ensure(xaml.Contains("キャンセル", StringComparison.Ordinal), "Cancellation UI is required.");
    Ensure(xaml.Contains("ResultFilter", StringComparison.Ordinal), "Analyzer and generator filtering is required.");
    Ensure(xaml.Contains("AllowDrop=\"True\"", StringComparison.Ordinal), "File drop must be enabled.");
    Ensure(xaml.Contains("PreviewDrop=\"OnPreviewDrop\"", StringComparison.Ordinal), "File drop must be handled.");
    Ensure(!xaml.Contains("DiscoverCommand", StringComparison.Ordinal), "Manual discovery should not remain in the GUI.");
    Ensure(xaml.Contains("SelectedItem=\"{Binding Configuration, Mode=TwoWay}\"", StringComparison.Ordinal), "Configuration selection must not use editable text binding.");
    Ensure(xaml.Contains("StartAvailabilityText", StringComparison.Ordinal), "Start readiness must always be visible.");
    Ensure(xaml.Contains("IsExpanded=\"False\"", StringComparison.Ordinal), "Advanced settings should be collapsed initially.");
    Ensure(xaml.Contains("SelectedTheme", StringComparison.Ordinal), "The theme selector is required.");
    Ensure(appXaml.Contains("WindowBackgroundBrush", StringComparison.Ordinal), "Theme resources are required.");
    Ensure(appXaml.Contains("PrimaryButtonStyle", StringComparison.Ordinal), "The modern primary action style is required.");
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
