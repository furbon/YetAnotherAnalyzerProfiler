using System.Diagnostics;
using System.Text;
using Yaap.Cli;
using Yaap.Core;

List<TestCase> tests = new()
{
    new("unit.mode-defaults", ModeDefaultsAsync),
    new("unit.statistics", StatisticsAsync),
    new("unit.compiler-report-locales", CompilerReportLocalesAsync),
    new("comparison.deltas-and-warnings", ComparisonAsync),
    new("history.search-retention-delete", HistoryAsync),
    new("export.csv-json-markdown", ExportAsync),
    new("functional.target-discovery", TargetDiscoveryAsync),
    new("functional.generated-output-inventory", GeneratedOutputAsync),
    new("functional.profile-isolated", ProfileIsolatedAsync),
    new("functional.profile-normal-output", ProfileNormalAsync),
    new("failure.profile-partial-record", ProfileFailureAsync),
    new("failure.profile-partial-after-success", ProfilePartialAsync),
    new("cancellation.profile-record", ProfileCancellationAsync),
    new("cli.help-and-errors", CliAsync),
    new("scale.aggregate-bounded-identities", ScaleAsync),
};

if (Environment.GetEnvironmentVariable("YAAP_RUN_INTEGRATION") == "1")
{
    tests.Add(new TestCase("integration.analyzer-generator-profile", IntegrationProfileAsync));
}

string? filter = args.Length >= 2 && args[0] == "--group" ? args[1] : null;
IReadOnlyList<TestCase> selected = tests
    .Where(test => filter is null || test.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
    .ToArray();
if (selected.Count == 0)
{
    Console.Error.WriteLine($"No tests matched group '{filter}'.");
    return 2;
}

int failures = 0;
Stopwatch suite = Stopwatch.StartNew();
foreach (TestCase test in selected)
{
    Stopwatch timer = Stopwatch.StartNew();
    try
    {
        await test.Body();
        Console.WriteLine($"PASS {test.Name} ({timer.ElapsedMilliseconds} ms)");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"Executed {selected.Count} tests in {suite.ElapsedMilliseconds} ms; failures: {failures}.");
return failures == 0 ? 0 : 1;

static Task ModeDefaultsAsync()
{
    ProfileOptions warm = ProfileOptions.ForMode("target.csproj", ProfileMode.Warm);
    ProfileOptions cold = ProfileOptions.ForMode("target.csproj", ProfileMode.Cold);
    Assert.Equal(1, warm.WarmupCount);
    Assert.Equal(3, warm.IterationCount);
    Assert.True(warm.CleanBeforeEach);
    Assert.Equal(0, cold.WarmupCount);
    Assert.True(cold.CleanBeforeEach);
    return Task.CompletedTask;
}

static Task StatisticsAsync()
{
    MeasurementResult first = Measurement(
        1,
        new[] { new AnalyzerSample("A", "Asm", MetricKind.Analyzer, null, 10) },
        new[] { new GeneratorSample("G", "Gen", 20) },
        new[] { new GeneratedOutput("G", "G/a.cs", 10, 2) });
    MeasurementResult second = Measurement(
        2,
        new[] { new AnalyzerSample("A", "Asm", MetricKind.Analyzer, null, 30) },
        new[] { new GeneratorSample("G", "Gen", 40) },
        new[] { new GeneratedOutput("G", "G/a.cs", 12, 3) });
    StatisticalMetric analyzer = Assert.Single(Statistics.AggregateAnalyzers(new[] { first, second }));
    GeneratorMetric generator = Assert.Single(Statistics.AggregateGenerators(new[] { first, second }));
    Assert.Equal(20d, analyzer.MeanMilliseconds);
    Assert.Equal(10d, analyzer.StandardDeviationMilliseconds);
    Assert.Equal(30d, generator.MeanMilliseconds);
    Assert.Equal(1, generator.GeneratedFileCount);
    Assert.Equal(12L, generator.GeneratedByteCount);
    return Task.CompletedTask;
}

static Task CompilerReportLocalesAsync()
{
    BinlogAnalyzer.CompilerReportAccumulator report = BinlogAnalyzer.CreateCompilerReportAccumulator();
    report.Accept("アナライザー実行の合計時間: 0.010 秒。");
    report.Accept("  0.006  60 Fixture.Analyzers, Version=1.0.0.0, Culture=neutral");
    report.Accept(" <0.001  <1 Fixture.Analyzers.FixtureAnalyzer (YAAPF001)");
    report.Accept("Total generator execution time: 0.020 seconds.");
    report.Accept("  0.012  60 Fixture.Analyzers, Version=1.0.0.0, Culture=neutral");
    report.Accept("  0.004  20 Fixture.Analyzers.FixtureGenerator");
    BinlogAnalysis result = report.Complete();
    AnalyzerSample analyzer = result.Analyzers.Single(item =>
        item.Identity.Contains("FixtureAnalyzer", StringComparison.Ordinal));
    GeneratorSample generator = result.Generators.Single(item =>
        item.Identity.Contains("FixtureGenerator", StringComparison.Ordinal));
    Assert.Equal("Fixture.Analyzers", analyzer.Assembly);
    Assert.Equal("YAAPF001", analyzer.DiagnosticId);
    Assert.Equal(1d, analyzer.ElapsedMilliseconds);
    Assert.Equal("Fixture.Analyzers", generator.Assembly);
    Assert.Equal(4d, generator.ElapsedMilliseconds);
    return Task.CompletedTask;
}

static Task ComparisonAsync()
{
    ProfileRun baseline = Run(
        analyzers: new[] { Metric("A", 10), Metric("Removed", 5) },
        sdk: "8.0.100");
    ProfileRun candidate = Run(
        analyzers: new[] { Metric("A", 15), Metric("Added", 4) },
        sdk: "10.0.100");
    ComparisonResult comparison = RunComparison.Compare(baseline, candidate);
    MetricDelta changed = comparison.Metrics.Single(item => item.Identity.Contains("::A::", StringComparison.Ordinal));
    Assert.Equal(5d, changed.DeltaMilliseconds);
    Assert.Equal(50d, changed.DeltaPercent);
    Assert.True(comparison.Metrics.Single(item => item.Identity.Contains("::Added::", StringComparison.Ordinal)).Added);
    Assert.True(comparison.Metrics.Single(item => item.Identity.Contains("::Removed::", StringComparison.Ordinal)).Removed);
    Assert.True(comparison.Warnings.Any(item => item.Contains("SDK", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static async Task HistoryAsync()
{
    using TemporaryDirectory temporary = new();
    HistoryStore history = new(temporary.Path);
    ProfileRun first = Run(target: "alpha.csproj", startedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
    ProfileRun second = Run(target: "beta.csproj", startedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
    await history.SaveAsync(first);
    await history.SaveAsync(second);
    ProfileRun loaded = await history.LoadAsync(first.Id);
    Assert.Equal(first.Id, loaded.Id);
    Assert.Equal(1, (await history.ListAsync(new HistoryQuery(Search: "beta"))).Count);
    Assert.Equal(1, (await history.ListAsync(new HistoryQuery(
        Status: RunStatus.Succeeded,
        From: first.StartedAt.AddSeconds(1),
        To: DateTimeOffset.UtcNow.AddMinutes(1)))).Count);
    string corruptDirectory = history.GetRunDirectory(Guid.NewGuid());
    Directory.CreateDirectory(corruptDirectory);
    await File.WriteAllTextAsync(System.IO.Path.Combine(corruptDirectory, "summary.json"), "not-json");
    Assert.Equal(2, (await history.ListAsync()).Count);
    await history.ApplyRetentionAsync(1);
    IReadOnlyList<RunSummary> retained = await history.ListAsync();
    Assert.Equal(1, retained.Count);
    Assert.Equal(second.Id, retained[0].Id);
    await history.DeleteAsync(second.Id);
    Assert.Equal(0, (await history.ListAsync()).Count);
}

static async Task ExportAsync()
{
    ProfileRun run = Run(
        analyzers: new[] { Metric("A,quoted", 1.25) },
        generators: new[]
        {
            new GeneratorMetric("G", "Gen", 2, 1, 3, 0.5, 2, 1, 10, 2,
                new[] { new GeneratedOutput("G", "G/a.cs", 10, 2) }),
        });
    foreach (ExportFormat format in Enum.GetValues<ExportFormat>())
    {
        await using MemoryStream stream = new();
        await RunExporter.ExportAsync(run, format, stream);
        Assert.True(stream.Length > 20);
        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string content = await reader.ReadToEndAsync();
        Assert.Contains(format == ExportFormat.Markdown ? "生成ファイル単位の時間ではありません" : "A", content);
        Assert.Contains("G/a.cs", content);
    }
}

static async Task TargetDiscoveryAsync()
{
    using TemporaryDirectory temporary = new();
    string project = System.IO.Path.Combine(temporary.Path, "Sample.csproj");
    await File.WriteAllTextAsync(
        project,
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Configurations>Debug;Profile</Configurations><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>");
    TargetInfo info = await TargetDiscovery.DiscoverAsync(project);
    Assert.True(info.Configurations.Contains("Profile"));
    Assert.Equal(2, info.TargetFrameworks.Count);
    string root = FindRepositoryRoot();
    TargetInfo solution = await TargetDiscovery.DiscoverAsync(
        System.IO.Path.Combine(root, "tests", "assets", "Fixture.Solution", "Fixture.sln"));
    TargetInfo solutionXml = await TargetDiscovery.DiscoverAsync(
        System.IO.Path.Combine(root, "tests", "assets", "Fixture.Solution", "Fixture.slnx"));
    Assert.True(solution.Configurations.Contains("Release"));
    Assert.Equal(".slnx", solutionXml.Extension);
    Assert.True(solution.TargetFrameworks.Contains("net8.0"));
    Assert.True(solutionXml.TargetFrameworks.Contains("net8.0"));
    await Assert.ThrowsAsync<YaapException>(() => TargetDiscovery.DiscoverAsync(System.IO.Path.Combine(temporary.Path, "missing.sln")));
}

static async Task GeneratedOutputAsync()
{
    using TemporaryDirectory temporary = new();
    string directory = System.IO.Path.Combine(temporary.Path, "Assembly", "Generator");
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "a.cs"), "one\ntwo\nthree");
    GeneratedOutput output = Assert.Single(await GeneratedOutputInventory.InspectAsync(temporary.Path));
    Assert.Equal("Generator", output.GeneratorIdentity);
    Assert.Equal(3L, output.LineCount);
    Assert.True(output.ByteCount > 3);
}

static async Task ProfileIsolatedAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    RecordingProcessRunner process = new((invocation, _) =>
    {
        CreateFakeBuildOutputs(invocation);
        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRunner runner = new(
        process,
        new FakeBinlogAnalyzer(),
        new EnvironmentDetector(process));
    ProfileRun run = await runner.RunAsync(new ProfileOptions
    {
        TargetPath = project,
        Mode = ProfileMode.Custom,
        WarmupCount = 0,
        IterationCount = 2,
        CleanBeforeEach = true,
        Isolated = true,
        HistoryPath = historyPath.Path,
    });
    Assert.Equal(RunStatus.Succeeded, run.Status);
    Assert.Equal(2, run.Measurements.Count);
    Assert.Equal(1, run.Analyzers.Count);
    Assert.Equal(1, run.Generators.Count);
    Assert.Equal(1, run.Generators[0].GeneratedFileCount);
    IReadOnlyList<ProcessInvocation> buildCommands = process.Invocations
        .Where(item => item.Arguments.FirstOrDefault() is "restore" or "clean" or "build")
        .ToArray();
    Assert.True(buildCommands.All(item => item.Arguments.Contains("--artifacts-path")));
    Assert.False(Directory.Exists(System.IO.Path.Combine(target.Path, "bin")));
    Assert.False(Directory.Exists(System.IO.Path.Combine(target.Path, "obj")));
}

static async Task ProfileFailureAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    RecordingProcessRunner process = new((invocation, _) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "build")
        {
            CreateFakeBuildOutputs(invocation);
            return Task.FromResult(new ProcessResult(1, TimeSpan.FromMilliseconds(1), Array.Empty<string>(), new[] { "fixture failure" }));
        }

        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun run = await new ProfileRunner(process, new FakeBinlogAnalyzer(), new EnvironmentDetector(process))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            WarmupCount = 0,
            IterationCount = 3,
            CleanBeforeEach = false,
            HistoryPath = historyPath.Path,
        });
    Assert.Equal(RunStatus.Failed, run.Status);
    Assert.Equal(1, run.Measurements.Count);
    Assert.True(run.Diagnostics.Any(item => item.Code == "YAAP2001"));
    ProfileRun retained = await new HistoryStore(historyPath.Path).LoadAsync(run.Id);
    Assert.Equal(RunStatus.Failed, retained.Status);
}

static async Task ProfileNormalAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    RecordingProcessRunner process = new((invocation, _) =>
    {
        CreateFakeBuildOutputs(invocation);
        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun run = await new ProfileRunner(process, new FakeBinlogAnalyzer(), new EnvironmentDetector(process))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            WarmupCount = 0,
            IterationCount = 1,
            CleanBeforeEach = true,
            Isolated = false,
            HistoryPath = historyPath.Path,
        });
    Assert.Equal(RunStatus.Succeeded, run.Status);
    Assert.False(process.Invocations.Any(item => item.Arguments.Contains("--artifacts-path")));
}

static async Task ProfilePartialAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    int measuredBuilds = 0;
    RecordingProcessRunner process = new((invocation, _) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "build" &&
            invocation.Arguments.Any(item => item.StartsWith("-bl:", StringComparison.Ordinal)))
        {
            measuredBuilds++;
            CreateFakeBuildOutputs(invocation);
            if (measuredBuilds == 2)
            {
                return Task.FromResult(new ProcessResult(
                    1,
                    TimeSpan.FromMilliseconds(1),
                    Array.Empty<string>(),
                    new[] { "second measurement failed" }));
            }
        }

        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun run = await new ProfileRunner(process, new FakeBinlogAnalyzer(), new EnvironmentDetector(process))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            WarmupCount = 0,
            IterationCount = 3,
            CleanBeforeEach = false,
            HistoryPath = historyPath.Path,
        });
    Assert.Equal(RunStatus.Partial, run.Status);
    Assert.Equal(2, run.Measurements.Count);
    Assert.True(run.Analyzers.Count > 0);
    Assert.True(run.Diagnostics.Any(item => item.Code == "YAAP2001"));
}

static async Task ProfileCancellationAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    RecordingProcessRunner process = new(async (invocation, cancellationToken) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "restore")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return SuccessfulProcess(invocation);
    });
    using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
    ProfileRun run = await new ProfileRunner(process, new FakeBinlogAnalyzer(), new EnvironmentDetector(process))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            WarmupCount = 0,
            IterationCount = 1,
            HistoryPath = historyPath.Path,
        }, cancellationToken: cancellation.Token);
    Assert.Equal(RunStatus.Canceled, run.Status);
    Assert.True(run.Diagnostics.Any(item => item.Code == "YAAP5001"));
}

static async Task CliAsync()
{
    using StringWriter output = new();
    using StringWriter error = new();
    int help = await CliApplication.RunAsync(new[] { "help" }, output, error);
    Assert.Equal(0, help);
    Assert.Contains("profile", output.ToString());
    output.GetStringBuilder().Clear();
    int invalid = await CliApplication.RunAsync(new[] { "unknown" }, output, error);
    Assert.Equal(CliApplication.UsageError, invalid);
    Assert.Contains("Unknown command", error.ToString());
    output.GetStringBuilder().Clear();
    error.GetStringBuilder().Clear();
    int optionBeforeTarget = await CliApplication.RunAsync(
        new[] { "configurations", "--json", FindRepositoryRoot() + "/tests/assets/Fixture.App/Fixture.App.csproj" },
        output,
        error);
    Assert.Equal(0, optionBeforeTarget);
}

static Task ScaleAsync()
{
    const int sampleCount = 100_000;
    List<MeasurementResult> measurements = new(sampleCount);
    for (int index = 0; index < sampleCount; index++)
    {
        measurements.Add(Measurement(
            index,
            new[] { new AnalyzerSample($"A{index % 8}", "Asm", MetricKind.Analyzer, null, index % 10) },
            Array.Empty<GeneratorSample>(),
            Array.Empty<GeneratedOutput>()));
    }

    Stopwatch stopwatch = Stopwatch.StartNew();
    IReadOnlyList<StatisticalMetric> result = Statistics.AggregateAnalyzers(measurements);
    Assert.Equal(8, result.Count);
    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
    return Task.CompletedTask;
}

static async Task IntegrationProfileAsync()
{
    string root = FindRepositoryRoot();
    string[] targets =
    {
        System.IO.Path.Combine(root, "tests", "assets", "Fixture.App", "Fixture.App.csproj"),
        System.IO.Path.Combine(root, "tests", "assets", "Fixture.Solution", "Fixture.sln"),
        System.IO.Path.Combine(root, "tests", "assets", "Fixture.Solution", "Fixture.slnx"),
    };
    foreach (string target in targets)
    {
        using TemporaryDirectory history = new();
        ProfileRun run = await new ProfileRunner().RunAsync(new ProfileOptions
        {
            TargetPath = target,
            Mode = ProfileMode.Custom,
            WarmupCount = 0,
            IterationCount = 1,
            CleanBeforeEach = true,
            Isolated = true,
            HistoryPath = history.Path,
        });
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(run.Analyzers.Any(item => item.Identity.Contains("FixtureAnalyzer", StringComparison.Ordinal)));
        Assert.True(run.Generators.Any(item => item.Identity.Contains("FixtureGenerator", StringComparison.Ordinal)));
        Assert.True(run.Generators.SelectMany(item => item.Outputs).Any(item =>
            item.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)));
    }


    using TemporaryDirectory cliHistory = new();
    using StringWriter cliOutput = new();
    using StringWriter cliError = new();
    int exitCode = await CliApplication.RunAsync(
        new[]
        {
            "profile",
            targets[0],
            "--mode", "custom",
            "--warmups", "0",
            "--iterations", "1",
            "--isolated",
            "--json",
            "--history", cliHistory.Path,
        },
        cliOutput,
        cliError);
    Assert.Equal(CliApplication.Success, exitCode);
    Assert.Contains("\"status\": \"succeeded\"", cliOutput.ToString());
    Assert.Equal(string.Empty, cliError.ToString());
}

static ProfileRun Run(
    string target = "target.csproj",
    IReadOnlyList<StatisticalMetric>? analyzers = null,
    IReadOnlyList<GeneratorMetric>? generators = null,
    string sdk = "10.0.100",
    DateTimeOffset? startedAt = null)
{
    return new ProfileRun
    {
        TargetPath = target,
        TargetName = System.IO.Path.GetFileName(target),
        Configuration = "Release",
        Mode = ProfileMode.Warm,
        StartedAt = startedAt ?? DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
        Status = RunStatus.Succeeded,
        Environment = new EnvironmentSnapshot("TestOS", "x64", 8, ".NET", sdk, null, null, false),
        Analyzers = analyzers ?? Array.Empty<StatisticalMetric>(),
        Generators = generators ?? Array.Empty<GeneratorMetric>(),
    };
}

static StatisticalMetric Metric(string identity, double milliseconds)
{
    return new StatisticalMetric(identity, "Asm", MetricKind.Analyzer, null, milliseconds, milliseconds, milliseconds, 0, 1);
}

static MeasurementResult Measurement(
    int index,
    IReadOnlyList<AnalyzerSample> analyzers,
    IReadOnlyList<GeneratorSample> generators,
    IReadOnlyList<GeneratedOutput> outputs)
{
    return new MeasurementResult(
        index,
        DateTimeOffset.UtcNow,
        1,
        true,
        "build.binlog",
        analyzers,
        generators,
        outputs,
        Array.Empty<RunDiagnostic>());
}

static async Task<string> WriteProjectAsync(string directory)
{
    string path = System.IO.Path.Combine(directory, "Sample.csproj");
    await File.WriteAllTextAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
    return path;
}

static ProcessResult SuccessfulProcess(ProcessInvocation invocation)
{
    string? first = invocation.Arguments.FirstOrDefault();
    IReadOnlyList<string> output = first == "--version" ? new[] { "10.0.100" } : Array.Empty<string>();
    return new ProcessResult(0, TimeSpan.FromMilliseconds(1), output, Array.Empty<string>());
}

static void CreateFakeBuildOutputs(ProcessInvocation invocation)
{
    if (invocation.Arguments.FirstOrDefault() != "build" ||
        !invocation.Arguments.Any(item => item.StartsWith("-bl:", StringComparison.Ordinal)))
    {
        return;
    }

    string binlog = invocation.Arguments.Single(item => item.StartsWith("-bl:", StringComparison.Ordinal))[4..];
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(binlog)!);
    File.WriteAllBytes(binlog, new byte[] { 1, 2, 3 });
    string property = invocation.Arguments.Single(item => item.StartsWith("-p:CompilerGeneratedFilesOutputPath=", StringComparison.Ordinal));
    string root = property["-p:CompilerGeneratedFilesOutputPath=".Length..]
        .Replace("$(MSBuildProjectName)", "Fixture.App", StringComparison.Ordinal);
    string directory = System.IO.Path.Combine(root, "Fixture.Analyzers", "Fixture.Analyzers.FixtureGenerator");
    Directory.CreateDirectory(directory);
    File.WriteAllText(System.IO.Path.Combine(directory, "Generated.g.cs"), "line1\nline2\n");
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

internal sealed record TestCase(string Name, Func<Task> Body);

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yaap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly Func<ProcessInvocation, CancellationToken, Task<ProcessResult>> _behavior;

    public RecordingProcessRunner(Func<ProcessInvocation, CancellationToken, Task<ProcessResult>> behavior)
    {
        _behavior = behavior;
    }

    public List<ProcessInvocation> Invocations { get; } = new();

    public Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        Action<string, bool>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add(invocation);
        return _behavior(invocation, cancellationToken);
    }
}

internal sealed class FakeBinlogAnalyzer : IBinlogAnalyzer
{
    public Task<BinlogAnalysis> AnalyzeAsync(
        string binlogPath,
        CancellationToken cancellationToken = default,
        Action<CompilerInvocation>? compilerInvocationSink = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new BinlogAnalysis(
            new[] { new AnalyzerSample("Fixture.Analyzers.FixtureAnalyzer", "Fixture.Analyzers", MetricKind.Analyzer, null, 2) },
            new[] { new GeneratorSample("Fixture.Analyzers.FixtureGenerator", "Fixture.Analyzers", 3) },
            Array.Empty<RunDiagnostic>(),
            10,
            Array.Empty<CompilerInvocation>()));
    }
}

internal static class Assert
{
    public static void True(bool value, string? message = null)
    {
        if (!value)
        {
            throw new InvalidOperationException(message ?? "Expected true.");
        }
    }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    public static T Single<T>(IReadOnlyList<T> values)
    {
        Equal(1, values.Count);
        return values[0];
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text '{expected}' was not found.");
        }
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
