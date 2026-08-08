namespace Yaap.Core;

public enum ProfileMode
{
    Warm,
    Cold,
    Custom,
}

public enum RunStatus
{
    Running,
    Succeeded,
    Partial,
    Failed,
    Canceled,
}

public enum MetricKind
{
    Analyzer,
    Diagnostic,
}

public enum ExportFormat
{
    Csv,
    Json,
    Markdown,
}

public enum ProfileStage
{
    Validating,
    Restoring,
    WarmingUp,
    Cleaning,
    Building,
    Analyzing,
    Saving,
    Completed,
}

public sealed record ProfileOptions
{
    public required string TargetPath { get; init; }

    public string Configuration { get; init; } = "Release";

    public ProfileMode Mode { get; init; } = ProfileMode.Warm;

    public int WarmupCount { get; init; } = 1;

    public int IterationCount { get; init; } = 3;

    public bool CleanBeforeEach { get; init; } = true;

    public bool Restore { get; init; } = true;

    public bool Isolated { get; init; }

    public string? ArtifactsPath { get; init; }

    public string? HistoryPath { get; init; }

    public int RetentionCount { get; init; } = 50;

    public static ProfileOptions ForMode(string targetPath, ProfileMode mode)
    {
        return mode switch
        {
            ProfileMode.Cold => new ProfileOptions
            {
                TargetPath = targetPath,
                Mode = mode,
                WarmupCount = 0,
                IterationCount = 3,
                CleanBeforeEach = true,
            },
            ProfileMode.Warm => new ProfileOptions
            {
                TargetPath = targetPath,
                Mode = mode,
                WarmupCount = 1,
                IterationCount = 3,
                CleanBeforeEach = true,
            },
            _ => new ProfileOptions
            {
                TargetPath = targetPath,
                Mode = mode,
            },
        };
    }
}

public sealed record ProfileProgress(
    ProfileStage Stage,
    string Message,
    int Completed,
    int Total);

public sealed record RunDiagnostic(
    string Code,
    string Message,
    string Detail,
    string SuggestedAction);

public sealed record EnvironmentSnapshot(
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    string Framework,
    string DotNetSdk,
    string? GitCommit,
    string? GitBranch,
    bool GitDirty);

public sealed record AnalyzerSample(
    string Identity,
    string Assembly,
    MetricKind Kind,
    string? DiagnosticId,
    double ElapsedMilliseconds);

public sealed record GeneratorSample(
    string Identity,
    string Assembly,
    double ElapsedMilliseconds);

public sealed record GeneratedOutput(
    string GeneratorIdentity,
    string RelativePath,
    long ByteCount,
    long LineCount);

public sealed record StatisticalMetric(
    string Identity,
    string Assembly,
    MetricKind Kind,
    string? DiagnosticId,
    double MeanMilliseconds,
    double MinimumMilliseconds,
    double MaximumMilliseconds,
    double StandardDeviationMilliseconds,
    int SampleCount);

public sealed record GeneratorMetric(
    string Identity,
    string Assembly,
    double MeanMilliseconds,
    double MinimumMilliseconds,
    double MaximumMilliseconds,
    double StandardDeviationMilliseconds,
    int SampleCount,
    int GeneratedFileCount,
    long GeneratedByteCount,
    long GeneratedLineCount,
    IReadOnlyList<GeneratedOutput> Outputs);

public sealed record MeasurementResult(
    int Index,
    DateTimeOffset StartedAt,
    double BuildElapsedMilliseconds,
    bool BuildSucceeded,
    string BinlogPath,
    IReadOnlyList<AnalyzerSample> Analyzers,
    IReadOnlyList<GeneratorSample> Generators,
    IReadOnlyList<GeneratedOutput> GeneratedOutputs,
    IReadOnlyList<RunDiagnostic> Diagnostics);

public sealed record ProfileRun
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required string TargetPath { get; init; }

    public required string TargetName { get; init; }

    public required string Configuration { get; init; }

    public required ProfileMode Mode { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    public required EnvironmentSnapshot Environment { get; init; }

    public IReadOnlyList<string> TargetFrameworks { get; set; } = Array.Empty<string>();

    public IReadOnlyList<MeasurementResult> Measurements { get; set; } = Array.Empty<MeasurementResult>();

    public IReadOnlyList<StatisticalMetric> Analyzers { get; set; } = Array.Empty<StatisticalMetric>();

    public IReadOnlyList<GeneratorMetric> Generators { get; set; } = Array.Empty<GeneratorMetric>();

    public IReadOnlyList<RunDiagnostic> Diagnostics { get; set; } = Array.Empty<RunDiagnostic>();

    public bool Isolated { get; init; }

    public string? ArtifactsPath { get; init; }

    public RunSummary ToSummary()
    {
        return new RunSummary(
            Id,
            TargetName,
            TargetPath,
            Configuration,
            Status,
            StartedAt,
            FinishedAt,
            Analyzers.Count,
            Generators.Count,
            Analyzers.Sum(item => item.MeanMilliseconds),
            Generators.Sum(item => item.MeanMilliseconds));
    }
}

public sealed record RunSummary(
    Guid Id,
    string TargetName,
    string TargetPath,
    string Configuration,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int AnalyzerCount,
    int GeneratorCount,
    double AnalyzerMilliseconds,
    double GeneratorMilliseconds);

public sealed record HistoryQuery(
    string? Search = null,
    RunStatus? Status = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? Limit = null);

public sealed record MetricDelta(
    string Identity,
    string Category,
    double? BaselineMilliseconds,
    double? CandidateMilliseconds,
    double? DeltaMilliseconds,
    double? DeltaPercent,
    bool Added,
    bool Removed);

public sealed record ComparisonResult(
    Guid BaselineId,
    Guid CandidateId,
    IReadOnlyList<MetricDelta> Metrics,
    int GeneratedFileCountDelta,
    long GeneratedByteCountDelta,
    IReadOnlyList<string> Warnings);

public sealed class YaapException : Exception
{
    public YaapException(RunDiagnostic diagnostic, Exception? innerException = null)
        : base(diagnostic.Message, innerException)
    {
        Diagnostic = diagnostic;
    }

    public RunDiagnostic Diagnostic { get; }
}

public static class YaapErrors
{
    public static RunDiagnostic InvalidInput(string detail) => new(
        "YAAP1001",
        "入力を開けません。",
        detail,
        "存在する .sln、.slnx、または .csproj を指定してください。");

    public static RunDiagnostic ProcessFailed(string phase, int exitCode, string detail) => new(
        "YAAP2001",
        $"{phase} に失敗しました (終了コード {exitCode})。",
        detail,
        "対象を dotnet コマンドで直接ビルドし、NuGet 設定とエラーを確認してください。");

    public static RunDiagnostic BinlogFailed(string detail) => new(
        "YAAP3001",
        "binlog の解析に失敗しました。",
        detail,
        "測定元と同じか新しい .NET 世代の YAAP で再実行してください。");

    public static RunDiagnostic HistoryFailed(string detail) => new(
        "YAAP4001",
        "履歴の読み書きに失敗しました。",
        detail,
        "履歴ディレクトリのアクセス権、空き容量、破損ファイルを確認してください。");

    public static RunDiagnostic Canceled(string detail = "Operation canceled by the user.") => new(
        "YAAP5001",
        "処理をキャンセルしました。",
        detail,
        "部分結果は履歴で確認できます。必要なら条件を変更して再実行してください。");

    public static RunDiagnostic UnrecognizedReport(string detail) => new(
        "YAAP3002",
        "認識できない Analyzer レポート行があります。",
        detail,
        "SDK と YAAP を更新するか、binlog を保持して問題を報告してください。");
}
