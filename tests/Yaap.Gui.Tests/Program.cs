using Yaap.Core;
using Yaap.Gui;

List<(string Name, Func<Task> Body)> tests = new()
{
    ("gui.viewmodel-initialization", ViewModelInitializationAsync),
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
        MainViewModel viewModel = new()
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
