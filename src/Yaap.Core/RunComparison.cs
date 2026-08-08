namespace Yaap.Core;

public static class RunComparison
{
    public static ComparisonResult Compare(ProfileRun baseline, ProfileRun candidate)
    {
        List<MetricDelta> metrics = new();
        AddDeltas(
            metrics,
            "analyzer",
            ToAnalyzerDictionary(baseline.Analyzers),
            ToAnalyzerDictionary(candidate.Analyzers));
        AddDeltas(
            metrics,
            "generator",
            ToGeneratorDictionary(baseline.Generators),
            ToGeneratorDictionary(candidate.Generators));

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
        IReadOnlyDictionary<string, double> candidate)
    {
        foreach (string identity in baseline.Keys.Union(candidate.Keys, StringComparer.Ordinal))
        {
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
        AddWarningIfDifferent(warnings, "SDK", baseline.Environment.DotNetSdk, candidate.Environment.DotNetSdk);
        AddWarningIfDifferent(warnings, "OS", baseline.Environment.OperatingSystem, candidate.Environment.OperatingSystem);
        AddWarningIfDifferent(warnings, "architecture", baseline.Environment.Architecture, candidate.Environment.Architecture);
        AddWarningIfDifferent(warnings, "CPU count", baseline.Environment.ProcessorCount, candidate.Environment.ProcessorCount);
        AddWarningIfDifferent(warnings, "configuration", baseline.Configuration, candidate.Configuration);
        string baselineFrameworks = string.Join(';', baseline.TargetFrameworks.Order(StringComparer.Ordinal));
        string candidateFrameworks = string.Join(';', candidate.TargetFrameworks.Order(StringComparer.Ordinal));
        AddWarningIfDifferent(warnings, "target frameworks", baselineFrameworks, candidateFrameworks);
        return warnings;
    }

    private static void AddWarningIfDifferent<T>(List<string> warnings, string field, T baseline, T candidate)
    {
        if (!EqualityComparer<T>.Default.Equals(baseline, candidate))
        {
            warnings.Add($"Different {field}: baseline={baseline}, candidate={candidate}");
        }
    }
}
