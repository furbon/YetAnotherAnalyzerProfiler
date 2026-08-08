namespace Yaap.Core;

public static class RunComparison
{
    public static ComparisonResult Compare(
        ProfileRun baseline,
        ProfileRun candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        List<MetricDelta> metrics = new();
        AddDeltas(
            metrics,
            "analyzer",
            ToAnalyzerDictionary(baseline.Analyzers),
            ToAnalyzerDictionary(candidate.Analyzers),
            cancellationToken);
        AddDeltas(
            metrics,
            "generator",
            ToGeneratorDictionary(baseline.Generators),
            ToGeneratorDictionary(candidate.Generators),
            cancellationToken);

        int baselineFiles = baseline.Generators.Sum(item => item.GeneratedFileCount);
        int candidateFiles = candidate.Generators.Sum(item => item.GeneratedFileCount);
        long baselineBytes = baseline.Generators.Sum(item => item.GeneratedByteCount);
        long candidateBytes = candidate.Generators.Sum(item => item.GeneratedByteCount);
        return new ComparisonResult(
            baseline.Id,
            candidate.Id,
            metrics
                .OrderByDescending(item => Math.Abs(item.DeltaMilliseconds ?? 0))
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .ToArray(),
            candidateFiles - baselineFiles,
            candidateBytes - baselineBytes,
            GetWarnings(baseline, candidate));
    }

    private static IReadOnlyDictionary<string, double> ToAnalyzerDictionary(
        IEnumerable<StatisticalMetric> metrics)
    {
        return metrics
            .GroupBy(item => $"{item.Assembly}::{item.Identity}::{item.Kind}::{item.DiagnosticId}", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.MeanMilliseconds),
                StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, double> ToGeneratorDictionary(
        IEnumerable<GeneratorMetric> metrics)
    {
        return metrics
            .GroupBy(item => $"{item.Assembly}::{item.Identity}", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.MeanMilliseconds),
                StringComparer.Ordinal);
    }

    private static void AddDeltas(
        ICollection<MetricDelta> target,
        string category,
        IReadOnlyDictionary<string, double> baseline,
        IReadOnlyDictionary<string, double> candidate,
        CancellationToken cancellationToken)
    {
        foreach (string identity in baseline.Keys.Union(candidate.Keys, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasBaseline = baseline.TryGetValue(identity, out double before);
            bool hasCandidate = candidate.TryGetValue(identity, out double after);
            double? delta = hasBaseline && hasCandidate ? after - before : null;
            double? percent = delta is not null && before != 0 ? delta / before * 100 : null;
            target.Add(new MetricDelta(
                identity,
                category,
                hasBaseline ? before : null,
                hasCandidate ? after : null,
                delta,
                percent,
                !hasBaseline,
                !hasCandidate));
        }
    }

    private static IReadOnlyList<string> GetWarnings(ProfileRun baseline, ProfileRun candidate)
    {
        List<string> warnings = new();
        AddWarningIfDifferent(warnings, "状態", baseline.Status, candidate.Status);
        AddWarningIfDifferent(warnings, "測定モード", baseline.Mode, candidate.Mode);
        AddWarningIfDifferent(warnings, "ウォームアップ回数", baseline.WarmupCount, candidate.WarmupCount);
        AddWarningIfDifferent(warnings, "測定回数", baseline.IterationCount, candidate.IterationCount);
        AddWarningIfDifferent(warnings, "clean方針", baseline.CleanBeforeEach, candidate.CleanBeforeEach);
        AddWarningIfDifferent(warnings, "restore方針", baseline.Restore, candidate.Restore);
        AddWarningIfDifferent(warnings, "分離出力", baseline.Isolated, candidate.Isolated);
        AddWarningIfDifferent(warnings, "成功測定数", baseline.Measurements.Count, candidate.Measurements.Count);
        AddWarningIfDifferent(warnings, "SDK", baseline.Environment.DotNetSdk, candidate.Environment.DotNetSdk);
        AddWarningIfDifferent(warnings, "OS", baseline.Environment.OperatingSystem, candidate.Environment.OperatingSystem);
        AddWarningIfDifferent(warnings, "アーキテクチャ", baseline.Environment.Architecture, candidate.Environment.Architecture);
        AddWarningIfDifferent(warnings, "論理プロセッサ数", baseline.Environment.ProcessorCount, candidate.Environment.ProcessorCount);
        AddWarningIfDifferent(warnings, "構成", baseline.Configuration, candidate.Configuration);
        AddWarningIfDifferent(warnings, "Gitコミット", baseline.Environment.GitCommit, candidate.Environment.GitCommit);
        AddWarningIfDifferent(warnings, "Git作業ツリー状態", baseline.Environment.GitDirty, candidate.Environment.GitDirty);
        string baselineFrameworks = string.Join(';', baseline.TargetFrameworks.Order(StringComparer.Ordinal));
        string candidateFrameworks = string.Join(';', candidate.TargetFrameworks.Order(StringComparer.Ordinal));
        AddWarningIfDifferent(warnings, "対象フレームワーク", baselineFrameworks, candidateFrameworks);
        if (baseline.Status != RunStatus.Succeeded || candidate.Status != RunStatus.Succeeded)
        {
            warnings.Add("成功以外の測定結果を含むため、差分は参考値です。");
        }

        return warnings;
    }

    private static void AddWarningIfDifferent<T>(List<string> warnings, string field, T baseline, T candidate)
    {
        if (!EqualityComparer<T>.Default.Equals(baseline, candidate))
        {
            warnings.Add($"{field}が異なります: ベースライン={baseline}, 候補={candidate}");
        }
    }
}
