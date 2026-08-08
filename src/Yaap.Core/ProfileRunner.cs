namespace Yaap.Core;

public sealed class ProfileRunner
{
    private readonly IProcessRunner _processRunner;
    private readonly IBinlogAnalyzer _binlogAnalyzer;
    private readonly EnvironmentDetector _environmentDetector;

    public ProfileRunner(
        IProcessRunner? processRunner = null,
        IBinlogAnalyzer? binlogAnalyzer = null,
        EnvironmentDetector? environmentDetector = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
        _binlogAnalyzer = binlogAnalyzer ?? new BinlogAnalyzer();
        _environmentDetector = environmentDetector ?? new EnvironmentDetector(_processRunner);
    }

    public async Task<ProfileRun> RunAsync(
        ProfileOptions options,
        IProgress<ProfileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        progress?.Report(new ProfileProgress(ProfileStage.Validating, "入力を確認しています。", 0, 1));
        TargetInfo target = await TargetDiscovery.DiscoverAsync(
            options.TargetPath,
            cancellationToken).ConfigureAwait(false);
        string targetDirectory = Path.GetDirectoryName(target.FullPath)!;
        HistoryStore history = new(options.HistoryPath);
        EnvironmentSnapshot environment = await _environmentDetector.CaptureAsync(
            targetDirectory,
            cancellationToken).ConfigureAwait(false);
        ProfileRun run = new()
        {
            TargetPath = target.FullPath,
            TargetName = Path.GetFileName(target.FullPath),
            Configuration = options.Configuration,
            Mode = options.Mode,
            WarmupCount = options.WarmupCount,
            IterationCount = options.IterationCount,
            CleanBeforeEach = options.CleanBeforeEach,
            Restore = options.Restore,
            StartedAt = DateTimeOffset.UtcNow,
            Environment = environment,
            TargetFrameworks = target.TargetFrameworks,
            Isolated = options.Isolated,
            ArtifactsPath = options.ArtifactsPath,
        };

        string runDirectory = history.GetRunDirectory(run.Id);
        using IDisposable runLease = history.AcquireRunLease(run.Id, cancellationToken);
        string workDirectory = Path.Combine(runDirectory, "work");
        string buildLoggerPath = string.Empty;
        string artifactsPath = ResolveArtifactsPath(options, targetDirectory, workDirectory);
        if (options.Isolated)
        {
            run = run with { ArtifactsPath = artifactsPath };
        }

        Directory.CreateDirectory(workDirectory);
        string effectiveTarget = target.FullPath;
        List<MeasurementResult> measurements = new();
        List<RunDiagnostic> runDiagnostics = new();
        IReadOnlyList<GeneratorOutputSnapshot> latestOutputSnapshots =
            Array.Empty<GeneratorOutputSnapshot>();
        await history.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        try
        {
            buildLoggerPath = ResolveBuildLoggerPath();
            effectiveTarget = await CreateCompatibilitySolutionIfRequiredAsync(
                target,
                environment.DotNetSdk,
                workDirectory,
                cancellationToken).ConfigureAwait(false);
            if (options.Restore)
            {
                progress?.Report(new ProfileProgress(ProfileStage.Restoring, "NuGet を復元しています。", 0, 1));
                await RunRequiredAsync(
                    "restore",
                    CreateRestoreArguments(effectiveTarget, options, artifactsPath),
                    targetDirectory,
                    cancellationToken).ConfigureAwait(false);
            }

            for (int index = 0; index < options.WarmupCount; index++)
            {
                progress?.Report(new ProfileProgress(
                    ProfileStage.WarmingUp,
                    $"ウォームアップ {index + 1}/{options.WarmupCount}",
                    index,
                    options.WarmupCount));
                await RunRequiredAsync(
                    "warm-up build",
                    CreateWarmupArguments(effectiveTarget, options, artifactsPath),
                    targetDirectory,
                    cancellationToken).ConfigureAwait(false);
            }

            for (int index = 1; index <= options.IterationCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (options.CleanBeforeEach)
                {
                    progress?.Report(new ProfileProgress(
                        ProfileStage.Cleaning,
                        $"測定 {index}/{options.IterationCount} の前に clean しています。",
                        index - 1,
                        options.IterationCount));
                    await RunRequiredAsync(
                        "clean",
                        CreateCleanArguments(effectiveTarget, options, artifactsPath),
                        targetDirectory,
                        cancellationToken).ConfigureAwait(false);
                }

                string measurementDirectory = Path.Combine(workDirectory, $"measurement-{index:D3}");
                string generatedPath = Path.Combine(measurementDirectory, "generated");
                string binlogPath = Path.Combine(measurementDirectory, "build.binlog");
                string compilerCapturePath = Path.Combine(measurementDirectory, "compiler-capture.yaap");
                Directory.CreateDirectory(measurementDirectory);
                progress?.Report(new ProfileProgress(
                    ProfileStage.Building,
                    $"測定ビルド {index}/{options.IterationCount}",
                    index - 1,
                    options.IterationCount));
                DateTimeOffset startedAt = DateTimeOffset.UtcNow;
                ProcessResult build = await _processRunner.RunAsync(
                    new ProcessInvocation(
                        "dotnet",
                        CreateMeasuredBuildArguments(
                            effectiveTarget,
                            options,
                            artifactsPath,
                            binlogPath,
                            generatedPath,
                            buildLoggerPath),
                        targetDirectory,
                        new Dictionary<string, string?>
                        {
                            [CompilerInvocationCapture.EnvironmentVariable] = compilerCapturePath,
                        }),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                List<RunDiagnostic> measurementDiagnostics = new();
                IReadOnlyList<AnalyzerSample> analyzers = Array.Empty<AnalyzerSample>();
                IReadOnlyList<GeneratorSample> generators = Array.Empty<GeneratorSample>();
                bool profilingSucceeded = true;
                if (File.Exists(compilerCapturePath) || File.Exists(binlogPath))
                {
                    progress?.Report(new ProfileProgress(
                        ProfileStage.Analyzing,
                        $"コンパイラー情報 {index}/{options.IterationCount} を逐次解析しています。",
                        index - 1,
                        options.IterationCount));
                    List<SpooledCompilerInvocation> compilerInvocations = new();
                    int compilerSpoolIndex = 0;
                    Action<CompilerInvocation> spoolInvocation = invocation =>
                    {
                        string commandLinePath = Path.Combine(
                            measurementDirectory,
                            $"compiler-{++compilerSpoolIndex:D3}.commandline");
                        File.WriteAllText(
                            commandLinePath,
                            invocation.CommandLine,
                            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        compilerInvocations.Add(new SpooledCompilerInvocation(
                            commandLinePath,
                            invocation.WorkingDirectory));
                    };
                    BinlogAnalysis analysis = new(
                        Array.Empty<AnalyzerSample>(),
                        Array.Empty<GeneratorSample>(),
                        Array.Empty<RunDiagnostic>(),
                        0,
                        Array.Empty<CompilerInvocation>());
                    if (File.Exists(compilerCapturePath))
                    {
                        await CompilerInvocationCapture.ReadAsync(
                            compilerCapturePath,
                            spoolInvocation,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        analysis = await _binlogAnalyzer.AnalyzeAsync(
                            binlogPath,
                            cancellationToken,
                            spoolInvocation).ConfigureAwait(false);
                    }

                    foreach (CompilerInvocation invocation in analysis.CompilerInvocations)
                    {
                        spoolInvocation(invocation);
                    }

                    Dictionary<(string Identity, string Assembly, MetricKind Kind, string? DiagnosticId), double>
                        analyzerTotals = new();
                    Dictionary<(string Identity, string Assembly), double> generatorTotals = new();
                    if (compilerInvocations.Count == 0)
                    {
                        AddAnalyzerSamples(analyzerTotals, analysis.Analyzers);
                        AddGeneratorSamples(generatorTotals, analysis.Generators);
                    }

                    measurementDiagnostics.AddRange(analysis.Diagnostics);
                    for (int compilerIndex = 0; compilerIndex < compilerInvocations.Count; compilerIndex++)
                    {
                        SpooledCompilerInvocation invocation = compilerInvocations[compilerIndex];
                        BinlogAnalyzer.CompilerReportAccumulator reports =
                            BinlogAnalyzer.CreateCompilerReportAccumulator();
                        try
                        {
                            CompilerCommand command = CommandLineTokenizer.ParseCompilerCommand(
                                await File.ReadAllTextAsync(
                                    invocation.CommandLinePath,
                                    cancellationToken).ConfigureAwait(false));
                            string responseFile = Path.Combine(
                                measurementDirectory,
                                $"compiler-{compilerIndex + 1:D3}.rsp");
                            try
                            {
                                await File.WriteAllTextAsync(
                                    responseFile,
                                    command.CompilerArguments,
                                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                                    cancellationToken).ConfigureAwait(false);
                                string[] compilerArguments = command.HostArguments
                                    .Append($"@{responseFile}")
                                    .ToArray();

                                ProcessResult compiler = await _processRunner.RunAsync(
                                    new ProcessInvocation(command.FileName, compilerArguments, invocation.WorkingDirectory),
                                    reports.Accept,
                                    cancellationToken).ConfigureAwait(false);
                                BinlogAnalysis report = reports.Complete();
                                AddAnalyzerSamples(analyzerTotals, report.Analyzers);
                                AddGeneratorSamples(generatorTotals, report.Generators);
                                measurementDiagnostics.AddRange(report.Diagnostics);
                                if (compiler.ExitCode != 0)
                                {
                                    profilingSucceeded = false;
                                    measurementDiagnostics.Add(YaapErrors.ProcessFailed(
                                        "compiler profiling replay",
                                        compiler.ExitCode,
                                        compiler.CombinedTail));
                                }
                            }
                            finally
                            {
                                TryDeleteFile(responseFile);
                            }
                        }
                        catch (FormatException exception)
                        {
                            profilingSucceeded = false;
                            measurementDiagnostics.Add(YaapErrors.BinlogFailed(exception.Message));
                        }
                        finally
                        {
                            TryDeleteFile(invocation.CommandLinePath);
                        }
                    }

                    analyzers = analyzerTotals.Select(pair => new AnalyzerSample(
                        pair.Key.Identity,
                        pair.Key.Assembly,
                        pair.Key.Kind,
                        pair.Key.DiagnosticId,
                        pair.Value)).ToArray();
                    generators = generatorTotals.Select(pair => new GeneratorSample(
                        pair.Key.Identity,
                        pair.Key.Assembly,
                        pair.Value)).ToArray();
                    if (analyzers.Count == 0 && generators.Count == 0)
                    {
                        profilingSucceeded = false;
                        measurementDiagnostics.Add(YaapErrors.BinlogFailed(
                            "No analyzer report or replayable C# compiler invocation was found."));
                    }
                }

                latestOutputSnapshots = await history.ReplaceGeneratedOutputsAsync(
                    run.Id,
                    GeneratedOutputInventory.InspectAsync(generatedPath, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                bool succeeded = build.ExitCode == 0 && profilingSucceeded;
                if (build.ExitCode != 0)
                {
                    measurementDiagnostics.Add(YaapErrors.ProcessFailed(
                        "build",
                        build.ExitCode,
                        build.CombinedTail));
                }

                measurements.Add(new MeasurementResult(
                    index,
                    startedAt,
                    build.Elapsed.TotalMilliseconds,
                    succeeded,
                    binlogPath,
                    analyzers,
                    generators,
                    Array.Empty<GeneratedOutput>(),
                    measurementDiagnostics));
                run.Analyzers = Statistics.AggregateAnalyzers(measurements);
                run.Generators = Statistics.AggregateGenerators(measurements, latestOutputSnapshots);
                run.Measurements = measurements.ToArray();
                run.Diagnostics = runDiagnostics.Concat(measurementDiagnostics).ToArray();
                run.Status = succeeded ? RunStatus.Running : RunStatus.Partial;
                progress?.Report(new ProfileProgress(ProfileStage.Saving, "部分結果を保存しています。", index, options.IterationCount));
                await history.SaveAsync(run, cancellationToken).ConfigureAwait(false);

                TryDeleteDirectory(generatedPath);
                TryDeleteFile(compilerCapturePath);
                if (!succeeded)
                {
                    break;
                }
            }

            run.Status = measurements.Count == 0 || measurements.All(item => !item.BuildSucceeded)
                ? RunStatus.Failed
                : measurements.Any(item => !item.BuildSucceeded)
                    ? RunStatus.Partial
                    : RunStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Canceled;
            runDiagnostics.Add(YaapErrors.Canceled());
        }
        catch (YaapException exception)
        {
            run.Status = measurements.Count > 0 ? RunStatus.Partial : RunStatus.Failed;
            runDiagnostics.Add(exception.Diagnostic);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            run.Status = measurements.Count > 0 ? RunStatus.Partial : RunStatus.Failed;
            runDiagnostics.Add(YaapErrors.ProcessFailed("profile", -1, exception.Message));
        }

        run.Measurements = measurements.ToArray();
        run.Analyzers = Statistics.AggregateAnalyzers(measurements);
        run.Generators = Statistics.AggregateGenerators(measurements, latestOutputSnapshots);
        run.Diagnostics = run.Diagnostics.Concat(runDiagnostics).Distinct().ToArray();
        run.FinishedAt = DateTimeOffset.UtcNow;
        CleanupTransientCompilerFiles(workDirectory);
        await history.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
        runLease.Dispose();
        if (run.Status != RunStatus.Canceled)
        {
            await history.ApplyRetentionAsync(options.RetentionCount, CancellationToken.None).ConfigureAwait(false);
        }
        if (options.Isolated && string.IsNullOrWhiteSpace(options.ArtifactsPath))
        {
            TryDeleteDirectory(artifactsPath);
        }

        progress?.Report(new ProfileProgress(ProfileStage.Completed, "測定が完了しました。", 1, 1));
        return run;
    }

    private static void ValidateOptions(ProfileOptions options)
    {
        if (options.IterationCount is < 1 or > 1000)
        {
            throw new YaapException(YaapErrors.InvalidInput("Iteration count must be between 1 and 1000."));
        }

        if (options.WarmupCount is < 0 or > 1000)
        {
            throw new YaapException(YaapErrors.InvalidInput("Warm-up count must be between 0 and 1000."));
        }

        if (options.RetentionCount < 1)
        {
            throw new YaapException(YaapErrors.InvalidInput("Retention count must be at least 1."));
        }

        if (!options.Isolated && !string.IsNullOrWhiteSpace(options.ArtifactsPath))
        {
            throw new YaapException(YaapErrors.InvalidInput("--artifacts-path requires isolated mode."));
        }
    }

    private async Task RunRequiredAsync(
        string phase,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await _processRunner.RunAsync(
            new ProcessInvocation("dotnet", arguments, workingDirectory),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new YaapException(YaapErrors.ProcessFailed(phase, result.ExitCode, result.CombinedTail));
        }
    }

    private static IReadOnlyList<string> CreateRestoreArguments(
        string target,
        ProfileOptions options,
        string artifactsPath)
    {
        List<string> arguments = new() { "restore", target };
        AddArtifactsPath(arguments, options, artifactsPath);
        return arguments;
    }

    private static async Task<string> CreateCompatibilitySolutionIfRequiredAsync(
        TargetInfo target,
        string sdkVersion,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        if (!target.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(sdkVersion.Split('.')[0], out int sdkMajor) ||
            sdkMajor >= 9)
        {
            return target.FullPath;
        }

        List<string> projects = new();
        System.Xml.XmlReaderSettings settings = new()
        {
            Async = true,
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            IgnoreComments = true,
        };
        await using FileStream input = new(
            target.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(input, settings))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == System.Xml.XmlNodeType.Element &&
                    reader.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase) &&
                    reader.GetAttribute("Path") is { Length: > 0 } relativePath)
                {
                    string project = Path.GetFullPath(
                        relativePath
                            .Replace('\\', Path.DirectorySeparatorChar)
                            .Replace('/', Path.DirectorySeparatorChar),
                        Path.GetDirectoryName(target.FullPath)!);
                    if (Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(project))
                    {
                        projects.Add(project);
                    }
                }
            }
        }

        if (projects.Count == 0)
        {
            throw new YaapException(YaapErrors.InvalidInput(
                "The .slnx file does not contain a buildable C# project."));
        }

        string compatibilityPath = Path.Combine(workDirectory, "sdk8-compatibility.sln");
        await using StreamWriter writer = new(
            compatibilityPath,
            append: false,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Microsoft Visual Studio Solution File, Format Version 12.00").ConfigureAwait(false);
        await writer.WriteLineAsync("# Visual Studio Version 17").ConfigureAwait(false);
        const string projectType = "{FAE04EC0-301F-11D3-BF4-00C04F79EFBC}";
        List<string> projectGuids = new();
        foreach (string project in projects.Distinct(FileSystemPath.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(project));
            string projectGuid = new Guid(hash.AsSpan(0, 16)).ToString("B").ToUpperInvariant();
            projectGuids.Add(projectGuid);
            string name = Path.GetFileNameWithoutExtension(project).Replace('"', '_');
            await writer.WriteLineAsync(
                $"Project(\"{projectType}\") = \"{name}\", \"{project}\", \"{projectGuid}\"")
                .ConfigureAwait(false);
            await writer.WriteLineAsync("EndProject").ConfigureAwait(false);
        }

        await writer.WriteLineAsync("Global").ConfigureAwait(false);
        await writer.WriteLineAsync("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution").ConfigureAwait(false);
        await writer.WriteLineAsync("\t\tDebug|Any CPU = Debug|Any CPU").ConfigureAwait(false);
        await writer.WriteLineAsync("\t\tRelease|Any CPU = Release|Any CPU").ConfigureAwait(false);
        await writer.WriteLineAsync("\tEndGlobalSection").ConfigureAwait(false);
        await writer.WriteLineAsync("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution").ConfigureAwait(false);
        foreach (string projectGuid in projectGuids)
        {
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                await writer.WriteLineAsync(
                    $"\t\t{projectGuid}.{configuration}|Any CPU.ActiveCfg = {configuration}|Any CPU")
                    .ConfigureAwait(false);
                await writer.WriteLineAsync(
                    $"\t\t{projectGuid}.{configuration}|Any CPU.Build.0 = {configuration}|Any CPU")
                    .ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync("\tEndGlobalSection").ConfigureAwait(false);
        await writer.WriteLineAsync("EndGlobal").ConfigureAwait(false);
        return compatibilityPath;
    }

    private static IReadOnlyList<string> CreateWarmupArguments(
        string target,
        ProfileOptions options,
        string artifactsPath)
    {
        List<string> arguments = new()
        {
            "build",
            target,
            "--no-restore",
            "--configuration",
            options.Configuration,
            "--verbosity",
            "minimal",
        };
        AddArtifactsPath(arguments, options, artifactsPath);
        return arguments;
    }

    private static IReadOnlyList<string> CreateCleanArguments(
        string target,
        ProfileOptions options,
        string artifactsPath)
    {
        List<string> arguments = new()
        {
            "clean",
            target,
            "--configuration",
            options.Configuration,
            "--verbosity",
            "minimal",
        };
        AddArtifactsPath(arguments, options, artifactsPath);
        return arguments;
    }

    private static IReadOnlyList<string> CreateMeasuredBuildArguments(
        string target,
        ProfileOptions options,
        string artifactsPath,
        string binlogPath,
        string generatedPath,
        string buildLoggerPath)
    {
        List<string> arguments = new()
        {
            "build",
            target,
            "--no-restore",
            "--configuration",
            options.Configuration,
            "--no-incremental",
            "--verbosity",
            "normal",
            $"-bl:{binlogPath}",
            $"-logger:{buildLoggerPath}",
            "-p:ReportAnalyzer=true",
            "-p:EmitCompilerGeneratedFiles=true",
            $"-p:CompilerGeneratedFilesOutputPath={generatedPath}",
        };
        AddArtifactsPath(arguments, options, artifactsPath);
        return arguments;
    }

    private static string ResolveBuildLoggerPath()
    {
        string fileName = "Yaap.BuildLogger.dll";
        string candidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new YaapException(YaapErrors.ProcessFailed(
            "profile logger",
            -1,
            $"Required file was not found beside YAAP: {fileName}"));
    }

    private static void AddArtifactsPath(
        ICollection<string> arguments,
        ProfileOptions options,
        string artifactsPath)
    {
        if (options.Isolated)
        {
            arguments.Add("--artifacts-path");
            arguments.Add(artifactsPath);
        }
    }

    private static string ResolveArtifactsPath(
        ProfileOptions options,
        string targetDirectory,
        string workDirectory)
    {
        if (!options.Isolated)
        {
            return string.Empty;
        }

        string path = Path.GetFullPath(options.ArtifactsPath ?? Path.Combine(workDirectory, "artifacts"));
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        string relative = Path.GetRelativePath(target, path);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            throw new YaapException(YaapErrors.InvalidInput(
                "The isolated artifacts path must be outside the analyzed target directory."));
        }

        return path;
    }

    private static void AddAnalyzerSamples(
        IDictionary<(string Identity, string Assembly, MetricKind Kind, string? DiagnosticId), double> totals,
        IEnumerable<AnalyzerSample> samples)
    {
        foreach (AnalyzerSample sample in samples)
        {
            (string Identity, string Assembly, MetricKind Kind, string? DiagnosticId) key =
                (sample.Identity, sample.Assembly, sample.Kind, sample.DiagnosticId);
            totals.TryGetValue(key, out double current);
            totals[key] = current + sample.ElapsedMilliseconds;
        }
    }

    private static void AddGeneratorSamples(
        IDictionary<(string Identity, string Assembly), double> totals,
        IEnumerable<GeneratorSample> samples)
    {
        foreach (GeneratorSample sample in samples)
        {
            (string Identity, string Assembly) key = (sample.Identity, sample.Assembly);
            totals.TryGetValue(key, out double current);
            totals[key] = current + sample.ElapsedMilliseconds;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CleanupTransientCompilerFiles(string workDirectory)
    {
        if (!Directory.Exists(workDirectory))
        {
            return;
        }

        try
        {
            foreach (string pattern in new[] { "*.commandline", "*.rsp", "compiler-capture.yaap" })
            {
                foreach (string path in Directory.EnumerateFiles(workDirectory, pattern, SearchOption.AllDirectories))
                {
                    TryDeleteFile(path);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record SpooledCompilerInvocation(
        string CommandLinePath,
        string WorkingDirectory);
}
