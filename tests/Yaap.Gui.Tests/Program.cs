using Yaap.Core;
using Yaap.Gui;

List<(string Name, Func<Task> Body)> tests = new()
{
    ("gui.viewmodel-initialization", ViewModelInitializationAsync),
    ("gui.drop-and-auto-discovery", DropAndAutoDiscoveryAsync),
    ("gui.discovery-discards-stale-results", DiscoveryDiscardsStaleResultsAsync),
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
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
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

static async Task XamlContractAsync()
{
    string root = FindRepositoryRoot();
    string xaml = await File.ReadAllTextAsync(System.IO.Path.Combine(root, "src", "Yaap.Gui", "MainWindow.xaml"));
    Ensure(xaml.Contains("VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "Virtualization is required.");
    Ensure(xaml.Contains("生成ファイル単位の実行時間", StringComparison.Ordinal), "Generator timing disclaimer is required.");
    Ensure(xaml.Contains("キャンセル", StringComparison.Ordinal), "Cancellation UI is required.");
    Ensure(xaml.Contains("ResultFilter", StringComparison.Ordinal), "Analyzer and generator filtering is required.");
    Ensure(xaml.Contains("AllowDrop=\"True\"", StringComparison.Ordinal), "File drop must be enabled.");
    Ensure(xaml.Contains("PreviewDrop=\"OnPreviewDrop\"", StringComparison.Ordinal), "File drop must be handled.");
    Ensure(!xaml.Contains("DiscoverCommand", StringComparison.Ordinal), "Manual discovery should not remain in the GUI.");
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
