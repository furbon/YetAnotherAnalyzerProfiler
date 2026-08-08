using System.Diagnostics;
using System.Reflection;
using System.Text;
using Yaap.Cli;
using Yaap.Core;

if (args is ["--child-wait"])
{
    await Task.Delay(TimeSpan.FromMinutes(5));
    return 0;
}

if (args is ["--child-output"])
{
    for (int index = 1; index <= 250; index++)
    {
        Console.WriteLine($"stdout-{index:D3}");
        Console.Error.WriteLine($"stderr-{index:D3}");
    }

    return 7;
}

List<TestCase> tests = new()
{
    new("unit.mode-defaults", ModeDefaultsAsync),
    new("unit.statistics", StatisticsAsync),
    new("unit.compiler-report-locales", CompilerReportLocalesAsync),
    new("comparison.deltas-and-warnings", ComparisonAsync),
    new("history.search-retention-delete", HistoryAsync),
    new("history.concurrent-retention-protects-active", ConcurrentRetentionAsync),
    new("history.atomic-delete-cancellation", AtomicHistoryDeleteAsync),
    new("export.csv-json-markdown", ExportAsync),
    new("export.json-async-cancel", JsonExportCancellationAsync),
    new("export.atomic-cancel-and-csv-safety", AtomicExportAsync),
    new("functional.target-discovery", TargetDiscoveryAsync),
    new("functional.compiler-invocation-capture", CompilerInvocationCaptureAsync),
    new("failure.malformed-binlog-diagnostic", MalformedBinlogAsync),
    new("functional.profile-prefers-sdk-capture", ProfilePrefersSdkCaptureAsync),
    new("functional.generated-output-inventory", GeneratedOutputAsync),
    new("functional.generated-output-assembly-isolation", GeneratedOutputAssemblyIsolationAsync),
    new("functional.generated-output-manifest-streaming", GeneratedOutputManifestAsync),
    new("functional.profile-isolated", ProfileIsolatedAsync),
    new("functional.profile-normal-output", ProfileNormalAsync),
    new("failure.process-output-tail-bounded", ProcessOutputTailAsync),
    new("failure.profile-warmup-build-record", ProfileWarmupFailureAsync),
    new("failure.profile-clean-record", ProfileCleanFailureAsync),
    new("failure.profile-clean-partial-after-success", ProfileCleanPartialAsync),
    new("failure.profile-partial-record", ProfileFailureAsync),
    new("failure.profile-partial-after-success", ProfilePartialAsync),
    new("cancellation.profile-record", ProfileCancellationAsync),
    new("cancellation.process-exit-bounded", ProcessCancellationAsync),
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
        await test.Body().WaitAsync(TimeSpan.FromMinutes(3));
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
        new[] { new GeneratedOutput("G", "Gen", "G/a.cs", 10, 2) });
    MeasurementResult second = Measurement(
        2,
        new[] { new AnalyzerSample("A", "Asm", MetricKind.Analyzer, null, 30) },
        new[] { new GeneratorSample("G", "Gen", 40) },
        new[] { new GeneratedOutput("G", "Gen", "G/a.cs", 12, 3) });
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
    await history.UpdateLabelAsync(first.Id, " 最適化前 ");
    ProfileRun loaded = await history.LoadAsync(first.Id);
    Assert.Equal(first.Id, loaded.Id);
    Assert.Equal("最適化前", loaded.Label);
    RunSummary labeled = Assert.Single(await history.ListAsync(new HistoryQuery(Search: "最適化前")));
    Assert.Equal(first.Id, labeled.Id);
    Assert.Equal("最適化前", labeled.Label);
    YaapException longLabel = await Assert.ThrowsAsync<YaapException>(() =>
        history.UpdateLabelAsync(first.Id, new string('x', HistoryStore.MaximumLabelLength + 1)));
    Assert.Equal("YAAP1001", longLabel.Diagnostic.Code);
    Assert.Equal(1, (await history.ListAsync(new HistoryQuery(Search: "beta"))).Count);
    Assert.Equal(second.Id, Assert.Single(await history.ListAsync(new HistoryQuery(Limit: 1))).Id);
    await Assert.ThrowsAsync<YaapException>(() => history.ListAsync(new HistoryQuery(Limit: 0)));
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
    Assert.Equal(2, await history.DeleteAllAsync());
    Assert.Equal(0, (await history.ListAsync()).Count);
    Assert.Equal(0, await history.DeleteAllAsync());
}

static async Task ConcurrentRetentionAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    TaskCompletionSource firstBuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource releaseFirstBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
    RecordingProcessRunner firstProcess = new(async (invocation, cancellationToken) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "build")
        {
            firstBuildStarted.TrySetResult();
            await releaseFirstBuild.Task.WaitAsync(cancellationToken);
            CreateFakeBuildOutputs(invocation);
        }

        return SuccessfulProcess(invocation);
    });
    ProfileOptions options = new()
    {
        TargetPath = project,
        WarmupCount = 0,
        IterationCount = 1,
        CleanBeforeEach = false,
        Restore = false,
        HistoryPath = historyPath.Path,
        RetentionCount = 1,
    };
    Task<ProfileRun> firstTask = new ProfileRunner(
        firstProcess,
        new FakeBinlogAnalyzer(),
        new EnvironmentDetector(firstProcess)).RunAsync(options);
    await firstBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

    HistoryStore history = new(historyPath.Path);
    RunSummary active = Assert.Single(await history.ListAsync());
    YaapException activeDelete = await Assert.ThrowsAsync<YaapException>(
        () => history.DeleteAsync(active.Id));
    Assert.Equal("YAAP4001", activeDelete.Diagnostic.Code);
    YaapException activeLabel = await Assert.ThrowsAsync<YaapException>(
        () => history.UpdateLabelAsync(active.Id, "実行中"));
    Assert.Equal("YAAP4001", activeLabel.Diagnostic.Code);
    YaapException activeDeleteAll = await Assert.ThrowsAsync<YaapException>(
        () => history.DeleteAllAsync());
    Assert.Equal("YAAP4001", activeDeleteAll.Diagnostic.Code);
    Assert.Equal(active.Id, Assert.Single(await history.ListAsync()).Id);

    RecordingProcessRunner secondProcess = new((invocation, _) =>
    {
        CreateFakeBuildOutputs(invocation);
        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun second = await new ProfileRunner(
        secondProcess,
        new FakeBinlogAnalyzer(),
        new EnvironmentDetector(secondProcess)).RunAsync(options);
    IReadOnlyList<RunSummary> whileActive = await history.ListAsync();
    Assert.Equal(2, whileActive.Count);
    Assert.True(whileActive.Any(summary => summary.Id == active.Id));
    Assert.True(whileActive.Any(summary => summary.Id == second.Id));

    releaseFirstBuild.TrySetResult();
    await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
    IReadOnlyList<RunSummary> retained = await history.ListAsync();
    Assert.Equal(1, retained.Count);
    Assert.Equal(second.Id, retained[0].Id);
}

static async Task AtomicHistoryDeleteAsync()
{
    using TemporaryDirectory temporary = new();
    HistoryStore history = new(temporary.Path);
    for (int attempt = 0; attempt < 5; attempt++)
    {
        ProfileRun run = Run(target: $"atomic-{attempt}.csproj");
        await history.SaveAsync(run);
        string runDirectory = history.GetRunDirectory(run.Id);
        string payload = System.IO.Path.Combine(runDirectory, "payload");
        Directory.CreateDirectory(payload);
        for (int index = 0; index < 200; index++)
        {
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(payload, $"{index:D3}.txt"),
                index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using CancellationTokenSource cancellation = new();
        Task deletion = history.DeleteAsync(run.Id, cancellation.Token);
        cancellation.Cancel();
        try
        {
            await deletion;
            Assert.False(Directory.Exists(runDirectory));
        }
        catch (OperationCanceledException)
        {
            Assert.True(Directory.Exists(runDirectory));
            Assert.True(File.Exists(System.IO.Path.Combine(runDirectory, "run.json")));
            Assert.Equal(200, Directory.EnumerateFiles(payload).Count());
            ProfileRun loaded = await history.LoadAsync(run.Id);
            Assert.Equal(run.Id, loaded.Id);
            await history.DeleteAsync(run.Id);
        }
    }

    string tombstones = System.IO.Path.Combine(temporary.Path, "tombstones");
    for (int attempt = 0; attempt < 100 &&
         Directory.Exists(tombstones) &&
         Directory.EnumerateDirectories(tombstones).Any(); attempt++)
    {
        await Task.Delay(20);
    }

    Assert.False(Directory.Exists(tombstones) && Directory.EnumerateDirectories(tombstones).Any());
}

static async Task ExportAsync()
{
    ProfileRun run = Run(
        analyzers: new[] { Metric("A,quoted", 1.25) },
        generators: new[]
        {
            new GeneratorMetric("G", "Gen", 2, 1, 3, 0.5, 2, 1, 10, 2,
                new[] { new GeneratedOutput("G", "Gen", "G/a.cs", 10, 2) }),
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

static async Task AtomicExportAsync()
{
    using TemporaryDirectory temporary = new();
    string outputPath = System.IO.Path.Combine(temporary.Path, "result.csv");
    await File.WriteAllTextAsync(outputPath, "existing-content");
    using CancellationTokenSource cancellation = new();
    cancellation.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(() => RunExporter.ExportAsync(
        Run(analyzers: new[] { Metric("=dangerous", 1) }),
        ExportFormat.Csv,
        outputPath,
        cancellation.Token));
    Assert.Equal("existing-content", await File.ReadAllTextAsync(outputPath));
    Assert.False(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly).Any());

    await RunExporter.ExportAsync(
        Run(analyzers: new[] { Metric("=dangerous", 1) }),
        ExportFormat.Csv,
        outputPath);
    Assert.Contains("'=dangerous", await File.ReadAllTextAsync(outputPath));
}

static async Task JsonExportCancellationAsync()
{
    using CancellationTokenSource cancellation = new();
    await using BlockingWriteStream output = new();
    Task export = RunExporter.ExportAsync(
        Run(analyzers: new[] { Metric("async", 1) }),
        ExportFormat.Json,
        output,
        EmptyGeneratedOutputs(),
        cancellation.Token);
    await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.False(export.IsCompleted, "JSON export must yield before serializing the run payload.");
    cancellation.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(() => export);
}

static async IAsyncEnumerable<GeneratedOutput> EmptyGeneratedOutputs()
{
    await Task.CompletedTask;
    yield break;
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
    string customSolutionPath = System.IO.Path.Combine(temporary.Path, "Custom.sln");
    await File.WriteAllTextAsync(
        customSolutionPath,
        "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
        "Global\n" +
        "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n" +
        "\t\tProfile|x64 = Profile|x64\n" +
        "\t\tShip|ARM64 = Ship|ARM64\n" +
        "\tEndGlobalSection\n" +
        "EndGlobal\n");
    TargetInfo customSolution = await TargetDiscovery.DiscoverAsync(customSolutionPath);
    Assert.Equal(2, customSolution.Configurations.Count);
    Assert.True(customSolution.Configurations.Contains("Profile"));
    Assert.True(customSolution.Configurations.Contains("Ship"));
    Assert.False(customSolution.Configurations.Contains("Debug"));
    await Assert.ThrowsAsync<YaapException>(() => TargetDiscovery.DiscoverAsync(System.IO.Path.Combine(temporary.Path, "missing.sln")));
    Assert.True(TargetDiscovery.IsSupportedPath(project));
    Assert.True(TargetDiscovery.HasSupportedExtension("sample.slnx"));
    Assert.False(TargetDiscovery.IsSupportedPath(System.IO.Path.Combine(temporary.Path, "missing.csproj")));
    Assert.False(TargetDiscovery.HasSupportedExtension("sample.txt"));
    Assert.False(TargetDiscovery.IsSupportedPath("invalid.txt"));
}

static async Task CompilerInvocationCaptureAsync()
{
    using TemporaryDirectory temporary = new();
    string project = System.IO.Path.Combine(temporary.Path, "Sample.csproj");
    await File.WriteAllTextAsync(project, "<Project />");
    string capture = System.IO.Path.Combine(temporary.Path, "capture.yaap");
    const string commandLine = "dotnet csc.dll /reportanalyzer Program.cs";
    string record = string.Join(
        '\t',
        "C",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(commandLine)),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(temporary.Path)));
    await File.WriteAllTextAsync(
        capture,
        CompilerInvocationCapture.Header + Environment.NewLine + record + Environment.NewLine,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    List<CompilerInvocation> invocations = new();
    int count = await CompilerInvocationCapture.ReadAsync(capture, invocations.Add);
    Assert.Equal(1, count);
    CompilerInvocation invocation = Assert.Single(invocations);
    Assert.Equal(commandLine, invocation.CommandLine);
    Assert.Equal(temporary.Path, invocation.WorkingDirectory);

    await File.WriteAllTextAsync(capture, "invalid");
    YaapException exception = await Assert.ThrowsAsync<YaapException>(
        () => CompilerInvocationCapture.ReadAsync(capture, _ => { }));
    Assert.Equal("YAAP3001", exception.Diagnostic.Code);
}

static async Task MalformedBinlogAsync()
{
    using TemporaryDirectory temporary = new();
    string binlog = System.IO.Path.Combine(temporary.Path, "malformed.binlog");
    await File.WriteAllBytesAsync(binlog, new byte[] { 1, 2, 3 });
    YaapException exception = await Assert.ThrowsAsync<YaapException>(
        () => new BinlogAnalyzer().AnalyzeAsync(binlog));
    Assert.Equal("YAAP3001", exception.Diagnostic.Code);
    Assert.True(exception.InnerException is not null);
}

static async Task GeneratedOutputAsync()
{
    using TemporaryDirectory temporary = new();
    string directory = System.IO.Path.Combine(temporary.Path, "Assembly", "Generator");
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "a.cs"), "one\ntwo\nthree");
    GeneratedOutput output = Assert.Single(await CollectAsync(
        GeneratedOutputInventory.InspectAsync(temporary.Path)));
    Assert.Equal("Generator", output.GeneratorIdentity);
    Assert.Equal("Assembly", output.GeneratorAssembly);
    Assert.Equal(3L, output.LineCount);
    Assert.True(output.ByteCount > 3);
}

static async Task GeneratedOutputAssemblyIsolationAsync()
{
    using TemporaryDirectory temporary = new();
    string first = System.IO.Path.Combine(temporary.Path, "Assembly.A", "SharedGenerator", "Nested");
    string second = System.IO.Path.Combine(temporary.Path, "Assembly.B", "SharedGenerator");
    Directory.CreateDirectory(first);
    Directory.CreateDirectory(second);
    await File.WriteAllTextAsync(System.IO.Path.Combine(first, "a.cs"), "a");
    await File.WriteAllTextAsync(System.IO.Path.Combine(second, "b.cs"), "bb");
    IReadOnlyList<GeneratedOutput> outputs = await CollectAsync(
        GeneratedOutputInventory.InspectAsync(temporary.Path));
    Assert.Equal(2, outputs.Count);
    Assert.True(outputs.Any(output =>
        output.GeneratorAssembly == "Assembly.A" && output.GeneratorIdentity == "SharedGenerator"));
    Assert.True(outputs.Any(output =>
        output.GeneratorAssembly == "Assembly.B" && output.GeneratorIdentity == "SharedGenerator"));

    MeasurementResult measurement = Measurement(
        1,
        Array.Empty<AnalyzerSample>(),
        new[]
        {
            new GeneratorSample("SharedGenerator", "Assembly.A", 1),
            new GeneratorSample("SharedGenerator", "Assembly.B", 2),
        },
        outputs);
    IReadOnlyList<GeneratorMetric> metrics = Statistics.AggregateGenerators(new[] { measurement });
    Assert.Equal(2, metrics.Count);
    Assert.Equal(1, metrics.Single(metric => metric.Assembly == "Assembly.A").GeneratedFileCount);
    Assert.Equal(1, metrics.Single(metric => metric.Assembly == "Assembly.B").GeneratedFileCount);
}

static async Task GeneratedOutputManifestAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    RecordingProcessRunner process = new((invocation, _) =>
    {
        CreateFakeBuildOutputs(invocation);
        if (invocation.Arguments.FirstOrDefault() == "build" &&
            invocation.Arguments.Any(item => item.StartsWith("-bl:", StringComparison.Ordinal)))
        {
            string property = invocation.Arguments.Single(item =>
                item.StartsWith("-p:CompilerGeneratedFilesOutputPath=", StringComparison.Ordinal));
            string root = property["-p:CompilerGeneratedFilesOutputPath=".Length..]
                .Replace("$(MSBuildProjectName)", "Fixture.App", StringComparison.Ordinal);
            string directory = System.IO.Path.Combine(
                root,
                "Fixture.Analyzers",
                "Fixture.Analyzers.FixtureGenerator");
            for (int index = 0; index < 150; index++)
            {
                File.WriteAllText(
                    System.IO.Path.Combine(directory, $"Preview-{index:D3}.g.cs"),
                    $"line {index}\n");
            }
        }

        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun run = await new ProfileRunner(
        process,
        new FakeBinlogAnalyzer(),
        new EnvironmentDetector(process)).RunAsync(new ProfileOptions
        {
            TargetPath = project,
            Mode = ProfileMode.Custom,
            WarmupCount = 0,
            IterationCount = 1,
            CleanBeforeEach = false,
            Restore = false,
            HistoryPath = historyPath.Path,
        });

    GeneratorMetric metric = Assert.Single(run.Generators);
    Assert.Equal(151, metric.GeneratedFileCount);
    Assert.Equal(100, metric.Outputs.Count);
    Assert.True(metric.OutputsTruncated);
    HistoryStore history = new(historyPath.Path);
    ProfileRun loaded = await history.LoadAsync(run.Id);
    GeneratorMetric loadedMetric = Assert.Single(loaded.Generators);
    Assert.Equal(100, loadedMetric.Outputs.Count);
    Assert.True(loadedMetric.OutputsTruncated);
    string runJson = await File.ReadAllTextAsync(System.IO.Path.Combine(
        history.GetRunDirectory(run.Id),
        "run.json"));
    Assert.False(runJson.Contains("Preview-149.g.cs", StringComparison.Ordinal));

    IReadOnlyList<GeneratedOutput> allOutputs = await CollectAsync(
        history.StreamGeneratedOutputsAsync(run.Id));
    Assert.Equal(151, allOutputs.Count);
    Assert.True(allOutputs.Any(output => output.RelativePath.EndsWith(
        "Preview-149.g.cs",
        StringComparison.Ordinal)));

    foreach ((ExportFormat Format, string Marker) previewExpectation in new[]
             {
                 (ExportFormat.Csv, ",true"),
                 (ExportFormat.Json, "\"outputsTruncated\": true"),
                 (ExportFormat.Markdown, "プレビュー"),
             })
    {
        await using MemoryStream previewOutput = new();
        await RunExporter.ExportAsync(loaded, previewExpectation.Format, previewOutput);
        Assert.Contains(
            previewExpectation.Marker,
            Encoding.UTF8.GetString(previewOutput.ToArray()));
    }

    foreach (ExportFormat format in Enum.GetValues<ExportFormat>())
    {
        await using MemoryStream output = new();
        await RunExporter.ExportAsync(
            loaded,
            format,
            output,
            history.StreamGeneratedOutputsAsync(run.Id));
        string exported = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Preview-149.g.cs", exported);
        if (format == ExportFormat.Json)
        {
            Assert.Contains("\"generatedOutputs\"", exported);
        }
    }

    using StringWriter cliOutput = new();
    using StringWriter cliError = new();
    int cliResult = await CliApplication.RunAsync(
        new[]
        {
            "history",
            "show",
            run.Id.ToString("D"),
            "--history",
            historyPath.Path,
        },
        cliOutput,
        cliError);
    Assert.Equal(CliApplication.Success, cliResult);
    Assert.Equal(string.Empty, cliError.ToString());
    Assert.Contains("\"generatedOutputs\"", cliOutput.ToString());
    Assert.Contains("Preview-149.g.cs", cliOutput.ToString());

    using CancellationTokenSource cancellation = new();
    await using IAsyncEnumerator<GeneratedOutput> enumerator = history
        .StreamGeneratedOutputsAsync(run.Id, cancellation.Token)
        .GetAsyncEnumerator();
    Assert.True(await enumerator.MoveNextAsync());
    cancellation.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
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
    Assert.True(buildCommands.Where(item => item.Arguments.FirstOrDefault() == "build").All(item =>
        item.Arguments.Contains("--no-incremental")));
    Assert.True(buildCommands.Where(item => item.Arguments.FirstOrDefault() == "build").All(item =>
        item.Arguments.Any(argument => argument.StartsWith("-logger:", StringComparison.Ordinal))));
    Assert.False(Directory.Exists(System.IO.Path.Combine(target.Path, "bin")));
    Assert.False(Directory.Exists(System.IO.Path.Combine(target.Path, "obj")));
}

static async Task ProfilePrefersSdkCaptureAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    CountingBinlogAnalyzer binlog = new();
    ProfileRun run = await new ProfileRunner(
        new CaptureProcessRunner(),
        binlog,
        new EnvironmentDetector(new CaptureProcessRunner()))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            Mode = ProfileMode.Custom,
            WarmupCount = 0,
            IterationCount = 1,
            CleanBeforeEach = true,
            Isolated = true,
            HistoryPath = historyPath.Path,
        });
    Assert.Equal(RunStatus.Succeeded, run.Status);
    Assert.Equal(0, binlog.CallCount);
    Assert.True(run.Analyzers.Any(item => item.Identity.Contains("FixtureAnalyzer", StringComparison.Ordinal)));
    Assert.True(run.Generators.Any(item => item.Identity.Contains("FixtureGenerator", StringComparison.Ordinal)));
    Assert.False(Directory.EnumerateFiles(
        historyPath.Path,
        "compiler-capture.yaap",
        SearchOption.AllDirectories).Any());
    Assert.False(Directory.EnumerateFiles(
        historyPath.Path,
        "*.rsp",
        SearchOption.AllDirectories).Any());
}

static async Task ProcessOutputTailAsync()
{
    string assembly = Assembly.GetExecutingAssembly().Location;
    ProcessResult result = await new ProcessRunner().RunAsync(new ProcessInvocation(
        "dotnet",
        new[] { assembly, "--child-output" },
        FindRepositoryRoot()));
    Assert.Equal(7, result.ExitCode);
    Assert.Equal(200, result.StandardOutputTail.Count);
    Assert.Equal(200, result.StandardErrorTail.Count);
    Assert.Equal("stdout-051", result.StandardOutputTail[0]);
    Assert.Equal("stdout-250", result.StandardOutputTail[^1]);
    Assert.Equal("stderr-051", result.StandardErrorTail[0]);
    Assert.Equal("stderr-250", result.StandardErrorTail[^1]);
    Assert.True(result.StandardOutputTruncated);
    Assert.True(result.StandardErrorTruncated);
}

static async Task ProfileWarmupFailureAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    RecordingProcessRunner process = new((invocation, onLine, _) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "build")
        {
            onLine?.Invoke("warm-up build fixture failure", true);
            return Task.FromResult(new ProcessResult(
                9,
                TimeSpan.FromMilliseconds(1),
                Array.Empty<string>(),
                new[] { "warm-up build fixture failure" }));
        }

        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun run = await new ProfileRunner(process, new FakeBinlogAnalyzer(), new EnvironmentDetector(process))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            WarmupCount = 1,
            IterationCount = 2,
            CleanBeforeEach = true,
            HistoryPath = historyPath.Path,
        });
    Assert.Equal(RunStatus.Failed, run.Status);
    Assert.Equal(0, run.Measurements.Count);
    Assert.False(process.Invocations.Any(invocation =>
        invocation.Arguments.FirstOrDefault() == "clean"));
    RunDiagnostic diagnostic = Assert.Single(run.Diagnostics);
    Assert.Contains("ウォームアップ用 dotnet build", diagnostic.Message);
    Assert.Contains("実行コマンド: dotnet build", diagnostic.Detail);
    Assert.Contains("warm-up build fixture failure", diagnostic.Detail);
    Assert.Contains("ビルド構成", diagnostic.SuggestedAction);
    string log = Assert.Single(Directory.EnumerateFiles(
        System.IO.Path.Combine(new HistoryStore(historyPath.Path).GetRunDirectory(run.Id), "logs"),
        "warm-up-*.log").ToArray());
    Assert.Contains("[stderr] warm-up build fixture failure", await File.ReadAllTextAsync(log));
}

static async Task ProfileCleanFailureAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string targetDirectory = System.IO.Path.Combine(target.Path, "target with spaces");
    Directory.CreateDirectory(targetDirectory);
    string project = await WriteProjectAsync(targetDirectory);
    RecordingProcessRunner process = new((invocation, onLine, _) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "clean")
        {
            for (int index = 1; index <= 230; index++)
            {
                onLine?.Invoke($"clean stdout {index:D3}", false);
            }

            onLine?.Invoke("clean stderr fixture failure", true);
            return Task.FromResult(new ProcessResult(
                17,
                TimeSpan.FromMilliseconds(1),
                Enumerable.Range(31, 200).Select(index => $"clean stdout {index:D3}").ToArray(),
                new[] { "clean stderr fixture failure" })
            {
                StandardOutputTruncated = true,
            });
        }

        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun run = await new ProfileRunner(process, new FakeBinlogAnalyzer(), new EnvironmentDetector(process))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            WarmupCount = 0,
            IterationCount = 3,
            CleanBeforeEach = true,
            HistoryPath = historyPath.Path,
        });
    Assert.Equal(RunStatus.Failed, run.Status);
    Assert.Equal(0, run.Measurements.Count);
    Assert.False(process.Invocations.Any(invocation =>
        invocation.Arguments.FirstOrDefault() == "build"));
    RunDiagnostic diagnostic = Assert.Single(run.Diagnostics);
    Assert.Equal("YAAP2001", diagnostic.Code);
    Assert.Contains("測定前の dotnet clean", diagnostic.Message);
    Assert.Contains("終了コード 17", diagnostic.Message);
    Assert.Contains("実行コマンド: dotnet clean", diagnostic.Detail);
    Assert.Contains($"\"{project}\"", diagnostic.Detail);
    Assert.Contains($"作業ディレクトリ: {targetDirectory}", diagnostic.Detail);
    Assert.Contains("完全ログ:", diagnostic.Detail);
    Assert.Contains("前方の行は完全ログにのみ記録", diagnostic.Detail);
    Assert.Contains("clean stdout 230", diagnostic.Detail);
    Assert.Contains("clean stderr fixture failure", diagnostic.Detail);
    Assert.Contains("bin／obj", diagnostic.SuggestedAction);
    Assert.Contains("Clean target", diagnostic.SuggestedAction);

    string log = Assert.Single(Directory.EnumerateFiles(
        System.IO.Path.Combine(new HistoryStore(historyPath.Path).GetRunDirectory(run.Id), "logs"),
        "*.log").ToArray());
    string logText = await File.ReadAllTextAsync(log);
    Assert.Contains("実行コマンド: dotnet clean", logText);
    Assert.Contains("[stdout] clean stdout 001", logText);
    Assert.Contains("[stdout] clean stdout 230", logText);
    Assert.Contains("[stderr] clean stderr fixture failure", logText);

    ProfileRun retained = await new HistoryStore(historyPath.Path).LoadAsync(run.Id);
    Assert.Equal(RunStatus.Failed, retained.Status);
    Assert.Equal(diagnostic, Assert.Single(retained.Diagnostics));
}

static async Task ProfileCleanPartialAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    int cleanCount = 0;
    RecordingProcessRunner process = new((invocation, onLine, _) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "clean" && ++cleanCount == 2)
        {
            onLine?.Invoke("second clean failed", true);
            return Task.FromResult(new ProcessResult(
                5,
                TimeSpan.FromMilliseconds(1),
                Array.Empty<string>(),
                new[] { "second clean failed" }));
        }

        CreateFakeBuildOutputs(invocation);
        return Task.FromResult(SuccessfulProcess(invocation));
    });
    ProfileRun run = await new ProfileRunner(process, new FakeBinlogAnalyzer(), new EnvironmentDetector(process))
        .RunAsync(new ProfileOptions
        {
            TargetPath = project,
            WarmupCount = 0,
            IterationCount = 3,
            CleanBeforeEach = true,
            HistoryPath = historyPath.Path,
        });
    Assert.Equal(RunStatus.Partial, run.Status);
    Assert.Equal(1, run.Measurements.Count);
    Assert.True(run.Analyzers.Count > 0);
    RunDiagnostic diagnostic = Assert.Single(run.Diagnostics);
    Assert.Contains("測定前の dotnet clean", diagnostic.Message);
    Assert.Contains("second clean failed", diagnostic.Detail);
    string log = Assert.Single(Directory.EnumerateFiles(
        System.IO.Path.Combine(new HistoryStore(historyPath.Path).GetRunDirectory(run.Id), "logs"),
        "clean-002.log").ToArray());
    Assert.Contains("[stderr] second clean failed", await File.ReadAllTextAsync(log));
    ProfileRun retained = await new HistoryStore(historyPath.Path).LoadAsync(run.Id);
    Assert.Equal(RunStatus.Partial, retained.Status);
    Assert.Equal(1, retained.Measurements.Count);
}

static async Task ProfileFailureAsync()
{
    using TemporaryDirectory target = new();
    using TemporaryDirectory historyPath = new();
    string project = await WriteProjectAsync(target.Path);
    RecordingProcessRunner process = new((invocation, onLine, _) =>
    {
        if (invocation.Arguments.FirstOrDefault() == "build")
        {
            onLine?.Invoke("build stdout context", false);
            onLine?.Invoke("build stderr fixture failure", true);
            return Task.FromResult(new ProcessResult(
                1,
                TimeSpan.FromMilliseconds(1),
                new[] { "build stdout context" },
                new[] { "build stderr fixture failure" }));
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
    RunDiagnostic diagnostic = Assert.Single(run.Diagnostics.Where(item => item.Code == "YAAP2001").ToArray());
    Assert.Contains("測定用 dotnet build", diagnostic.Message);
    Assert.Contains("実行コマンド: dotnet build", diagnostic.Detail);
    Assert.Contains($"作業ディレクトリ: {target.Path}", diagnostic.Detail);
    Assert.Contains("build stdout context", diagnostic.Detail);
    Assert.Contains("build stderr fixture failure", diagnostic.Detail);
    Assert.Contains("対象フレームワーク", diagnostic.SuggestedAction);
    string log = Assert.Single(Directory.EnumerateFiles(
        System.IO.Path.Combine(new HistoryStore(historyPath.Path).GetRunDirectory(run.Id), "logs"),
        "build-*.log").ToArray());
    string logText = await File.ReadAllTextAsync(log);
    Assert.Contains("[stdout] build stdout context", logText);
    Assert.Contains("[stderr] build stderr fixture failure", logText);
    ProfileRun retained = await new HistoryStore(historyPath.Path).LoadAsync(run.Id);
    Assert.Equal(RunStatus.Failed, retained.Status);
    Assert.Equal(diagnostic, retained.Diagnostics.Single(item => item.Code == "YAAP2001"));
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

static async Task ProcessCancellationAsync()
{
    string assembly = Assembly.GetExecutingAssembly().Location;
    using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));
    Stopwatch stopwatch = Stopwatch.StartNew();
    await Assert.ThrowsAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
        new ProcessInvocation(
            "dotnet",
            new[] { assembly, "--child-wait" },
            FindRepositoryRoot()),
        cancellationToken: cancellation.Token));
    Assert.True(
        stopwatch.Elapsed < TimeSpan.FromSeconds(7),
        $"Canceled child process exceeded the bounded exit time: {stopwatch.Elapsed}.");
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
    string target = FindRepositoryRoot() + "/tests/assets/Fixture.App/Fixture.App.csproj";
    int configurations = await CliApplication.RunAsync(
        new[] { "configurations", target },
        output,
        error);
    Assert.Equal(0, configurations);
    Assert.Contains("targetFrameworks", output.ToString());

    foreach (string[] invalidArguments in new[]
             {
                 new[] { "configurations", "--json", target },
                 new[] { "configurations", target, "extra" },
                 new[] { "profile", target, "--iteratons", "1" },
                 new[] { "profile", target, "--clean", "true", "--no-clean" },
                 new[] { "history", "list", "--status", "failed", "--status", "succeeded" },
             })
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        int result = await CliApplication.RunAsync(invalidArguments, output, error);
        Assert.Equal(CliApplication.UsageError, result);
        Assert.True(error.ToString().Length > 0);
    }

    ProfileRun failedRun = Run("Sample.csproj");
    failedRun.Status = RunStatus.Failed;
    failedRun.Diagnostics = new[]
    {
        YaapErrors.ProcessFailed(
            ProcessOperation.Clean,
            17,
            "実行コマンド: dotnet clean Sample.csproj\n作業ディレクトリ: fixture\n完全ログ: history/logs/clean-001.log\n標準エラー出力末尾:\n  fixture failure"),
    };
    StubProfileRunner failedRunner = new((_, _, _) => Task.FromResult(failedRun));
    output.GetStringBuilder().Clear();
    error.GetStringBuilder().Clear();
    int failed = await CliApplication.RunAsync(
        new[] { "profile", "Sample.csproj", "--mode", "custom", "--warmups", "0", "--iterations", "1" },
        output,
        error,
        profileRunner: failedRunner);
    Assert.Equal(CliApplication.ProfileFailed, failed);
    Assert.Contains("YAAP2001: 測定前の dotnet clean", output.ToString());
    Assert.Contains("詳細:", output.ToString());
    Assert.Contains("実行コマンド: dotnet clean Sample.csproj", output.ToString());
    Assert.Contains("完全ログ: history/logs/clean-001.log", output.ToString());
    Assert.Contains("対処:", output.ToString());
    Assert.Contains("カスタム Clean target", output.ToString());
    Assert.Equal(string.Empty, error.ToString());

    using TemporaryDirectory failedHistory = new();
    await new HistoryStore(failedHistory.Path).SaveAsync(failedRun);
    output.GetStringBuilder().Clear();
    int failedJson = await CliApplication.RunAsync(
        new[]
        {
            "profile", "Sample.csproj", "--mode", "custom", "--warmups", "0", "--iterations", "1",
            "--json", "--history", failedHistory.Path,
        },
        output,
        error,
        profileRunner: failedRunner);
    Assert.Equal(CliApplication.ProfileFailed, failedJson);
    using System.Text.Json.JsonDocument failedDocument =
        System.Text.Json.JsonDocument.Parse(output.ToString());
    System.Text.Json.JsonElement failedJsonRun = failedDocument.RootElement.GetProperty("run");
    Assert.Equal("failed", failedJsonRun.GetProperty("status").GetString());
    Assert.Contains(
        "実行コマンド: dotnet clean",
        failedJsonRun.GetProperty("diagnostics")[0].GetProperty("detail").GetString()!);
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
        if (target.EndsWith("Fixture.App.csproj", StringComparison.OrdinalIgnoreCase))
        {
            string binlog = Assert.Single(run.Measurements).BinlogPath;
            int sdkMajor = int.Parse(run.Environment.DotNetSdk.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            if (Environment.Version.Major >= sdkMajor)
            {
                BinlogAnalysis direct = await new BinlogAnalyzer().AnalyzeAsync(binlog);
                Assert.True(direct.EventCount > 0);
                Assert.True(direct.CompilerInvocations.Count > 0 || direct.Analyzers.Count > 0 || direct.Generators.Count > 0);
            }
            else
            {
                try
                {
                    BinlogAnalysis forwardCompatible = await new BinlogAnalyzer().AnalyzeAsync(binlog);
                    Assert.True(forwardCompatible.EventCount > 0);
                }
                catch (YaapException incompatible)
                {
                    Assert.Equal("YAAP3001", incompatible.Diagnostic.Code);
                }
            }
        }
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

static async Task<IReadOnlyList<T>> CollectAsync<T>(
    IAsyncEnumerable<T> source,
    CancellationToken cancellationToken = default)
{
    List<T> values = new();
    await foreach (T value in source.WithCancellation(cancellationToken))
    {
        values.Add(value);
    }

    return values;
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
    TestBuildOutputs.Create(invocation);
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

internal static class TestBuildOutputs
{
    public static void Create(ProcessInvocation invocation)
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
    private readonly Func<ProcessInvocation, Action<string, bool>?, CancellationToken, Task<ProcessResult>> _behavior;

    public RecordingProcessRunner(Func<ProcessInvocation, CancellationToken, Task<ProcessResult>> behavior)
    {
        _behavior = (invocation, _, cancellationToken) => behavior(invocation, cancellationToken);
    }

    public RecordingProcessRunner(
        Func<ProcessInvocation, Action<string, bool>?, CancellationToken, Task<ProcessResult>> behavior)
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
        return _behavior(invocation, onLine, cancellationToken);
    }
}

internal sealed class StubProfileRunner : IProfileRunner
{
    private readonly Func<ProfileOptions, IProgress<ProfileProgress>?, CancellationToken, Task<ProfileRun>> _run;

    public StubProfileRunner(
        Func<ProfileOptions, IProgress<ProfileProgress>?, CancellationToken, Task<ProfileRun>> run)
    {
        _run = run;
    }

    public Task<ProfileRun> RunAsync(
        ProfileOptions options,
        IProgress<ProfileProgress>? progress = null,
        CancellationToken cancellationToken = default) => _run(options, progress, cancellationToken);
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

internal sealed class CountingBinlogAnalyzer : IBinlogAnalyzer
{
    public int CallCount { get; private set; }

    public Task<BinlogAnalysis> AnalyzeAsync(
        string binlogPath,
        CancellationToken cancellationToken = default,
        Action<CompilerInvocation>? compilerInvocationSink = null)
    {
        CallCount++;
        throw new InvalidOperationException("The SDK capture should be preferred over binlog replay.");
    }
}

internal sealed class CaptureProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        Action<string, bool>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.FileName.Equals("csc.exe", StringComparison.OrdinalIgnoreCase))
        {
            onLine?.Invoke("アナライザー実行の合計時間: 0.010 秒。", false);
            onLine?.Invoke("  0.004  40 Fixture.Analyzers.FixtureAnalyzer (YAAPF001)", false);
            onLine?.Invoke("Total generator execution time: 0.020 seconds.", false);
            onLine?.Invoke("  0.006  30 Fixture.Analyzers.FixtureGenerator", false);
            return Task.FromResult(new ProcessResult(
                0,
                TimeSpan.FromMilliseconds(1),
                Array.Empty<string>(),
                Array.Empty<string>()));
        }

        TestBuildOutputs.Create(invocation);
        if (invocation.Arguments.FirstOrDefault() == "build" &&
            invocation.Environment?.TryGetValue(CompilerInvocationCapture.EnvironmentVariable, out string? capture) == true &&
            !string.IsNullOrWhiteSpace(capture))
        {
            string project = invocation.Arguments[1];
            string record = string.Join(
                '\t',
                "C",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("csc.exe /reportanalyzer Program.cs")),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(System.IO.Path.GetDirectoryName(project)!)));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(capture)!);
            File.WriteAllText(
                capture,
                CompilerInvocationCapture.Header + Environment.NewLine + record + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        IReadOnlyList<string> output = invocation.Arguments.FirstOrDefault() == "--version"
            ? new[] { "10.0.100" }
            : Array.Empty<string>();
        return Task.FromResult(new ProcessResult(
            0,
            TimeSpan.FromMilliseconds(1),
            output,
            Array.Empty<string>()));
    }
}

internal sealed class BlockingWriteStream : Stream
{
    public TaskCompletionSource WriteStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new InvalidOperationException("Synchronous JSON writes are not allowed.");

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        WriteStarted.TrySetResult();
        return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
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
