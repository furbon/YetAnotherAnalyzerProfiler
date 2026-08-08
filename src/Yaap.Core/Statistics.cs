namespace Yaap.Core;

public static class Statistics
{
    public static IReadOnlyList<StatisticalMetric> AggregateAnalyzers(
        IEnumerable<MeasurementResult> measurements)
    {
        return measurements
            .SelectMany(measurement => measurement.Analyzers)
            .GroupBy(
                sample => (sample.Identity, sample.Assembly, sample.Kind, sample.DiagnosticId),
                sample => sample.ElapsedMilliseconds)
            .Select(group => CreateAnalyzerMetric(group.Key, group.ToArray()))
            .OrderByDescending(metric => metric.MeanMilliseconds)
            .ThenBy(metric => metric.Identity, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<GeneratorMetric> AggregateGenerators(
        IEnumerable<MeasurementResult> measurements)
    {
        MeasurementResult[] source = measurements.ToArray();
        Dictionary<string, IReadOnlyList<GeneratedOutput>> outputs = source
            .SelectMany(measurement => measurement.GeneratedOutputs)
            .GroupBy(output => output.GeneratorIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GeneratedOutput>)group
                    .GroupBy(output => output.RelativePath, StringComparer.Ordinal)
                    .Select(pathGroup => pathGroup.Last())
                    .OrderBy(output => output.RelativePath, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        return source
            .SelectMany(measurement => measurement.Generators)
            .GroupBy(
                sample => (sample.Identity, sample.Assembly),
                sample => sample.ElapsedMilliseconds)
            .Select(group =>
            {
                double[] values = group.ToArray();
                IReadOnlyList<GeneratedOutput> generated = outputs.GetValueOrDefault(
                    group.Key.Identity,
                    Array.Empty<GeneratedOutput>());
                return new GeneratorMetric(
                    group.Key.Identity,
                    group.Key.Assembly,
                    Mean(values),
                    values.Min(),
                    values.Max(),
                    StandardDeviation(values),
                    values.Length,
                    generated.Count,
                    generated.Sum(item => item.ByteCount),
                    generated.Sum(item => item.LineCount),
                    generated);
            })
            .OrderByDescending(metric => metric.MeanMilliseconds)
            .ThenBy(metric => metric.Identity, StringComparer.Ordinal)
            .ToArray();
    }

    public static double Mean(IReadOnlyCollection<double> values)
    {
        return values.Count == 0 ? 0 : values.Sum() / values.Count;
    }

    public static double StandardDeviation(IReadOnlyCollection<double> values)
    {
        if (values.Count <= 1)
        {
            return 0;
        }

        double mean = Mean(values);
        double variance = values.Sum(value => Math.Pow(value - mean, 2)) / values.Count;
        return Math.Sqrt(variance);
    }

    private static StatisticalMetric CreateAnalyzerMetric(
        (string Identity, string Assembly, MetricKind Kind, string? DiagnosticId) key,
        double[] values)
    {
        return new StatisticalMetric(
            key.Identity,
            key.Assembly,
            key.Kind,
            key.DiagnosticId,
            Mean(values),
            values.Min(),
            values.Max(),
            StandardDeviation(values),
            values.Length);
    }
}
