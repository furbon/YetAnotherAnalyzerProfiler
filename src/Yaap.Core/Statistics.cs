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
        return AggregateGenerators(measurements, outputSnapshots: null);
    }

    internal static IReadOnlyList<GeneratorMetric> AggregateGenerators(
        IEnumerable<MeasurementResult> measurements,
        IReadOnlyList<GeneratorOutputSnapshot>? outputSnapshots)
    {
        Dictionary<(string Identity, string Assembly), GeneratorAggregate> aggregates = new();
        foreach (MeasurementResult measurement in measurements)
        {
            foreach (GeneratorSample sample in measurement.Generators)
            {
                (string Identity, string Assembly) key = (sample.Identity, sample.Assembly);
                if (!aggregates.TryGetValue(key, out GeneratorAggregate? aggregate))
                {
                    aggregate = new GeneratorAggregate(sample.Identity, sample.Assembly);
                    aggregates.Add(key, aggregate);
                }

                aggregate.Values.Add(sample.ElapsedMilliseconds);
            }

            if (outputSnapshots is null)
            {
                foreach (GeneratedOutput output in measurement.GeneratedOutputs)
                {
                    if (output.GeneratorAssembly.Length > 0 &&
                        aggregates.TryGetValue(
                            (output.GeneratorIdentity, output.GeneratorAssembly),
                            out GeneratorAggregate? exact))
                    {
                        exact.Outputs[output.RelativePath] = output;
                        continue;
                    }

                    foreach (GeneratorAggregate aggregate in aggregates.Values.Where(item =>
                                 item.Identity.Equals(output.GeneratorIdentity, StringComparison.Ordinal)))
                    {
                        aggregate.Outputs[output.RelativePath] = output;
                    }
                }
            }
        }

        if (outputSnapshots is not null)
        {
            foreach (GeneratorOutputSnapshot snapshot in outputSnapshots)
            {
                if (snapshot.Assembly.Length > 0 &&
                    aggregates.TryGetValue(
                        (snapshot.Identity, snapshot.Assembly),
                        out GeneratorAggregate? exact))
                {
                    exact.Apply(snapshot);
                    continue;
                }

                foreach (GeneratorAggregate aggregate in aggregates.Values.Where(item =>
                             item.Identity.Equals(snapshot.Identity, StringComparison.Ordinal)))
                {
                    aggregate.Apply(snapshot);
                }
            }
        }

        return aggregates.Values.Select(aggregate =>
            {
                GeneratedOutput[] generated = aggregate.Outputs.Values
                    .OrderBy(output => output.RelativePath, StringComparer.Ordinal)
                    .ToArray();
                return new GeneratorMetric(
                    aggregate.Identity,
                    aggregate.Assembly,
                    Mean(aggregate.Values),
                    aggregate.Values.Min(),
                    aggregate.Values.Max(),
                    StandardDeviation(aggregate.Values),
                    aggregate.Values.Count,
                    aggregate.GeneratedFileCount ?? generated.Length,
                    aggregate.GeneratedByteCount ?? generated.Sum(item => item.ByteCount),
                    aggregate.GeneratedLineCount ?? generated.Sum(item => item.LineCount),
                    aggregate.Preview ?? generated)
                {
                    OutputsTruncated = aggregate.OutputsTruncated,
                };
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

    private sealed class GeneratorAggregate(string identity, string assembly)
    {
        public string Identity { get; } = identity;

        public string Assembly { get; } = assembly;

        public List<double> Values { get; } = new();

        public Dictionary<string, GeneratedOutput> Outputs { get; } = new(StringComparer.Ordinal);

        public int? GeneratedFileCount { get; private set; }

        public long? GeneratedByteCount { get; private set; }

        public long? GeneratedLineCount { get; private set; }

        public IReadOnlyList<GeneratedOutput>? Preview { get; private set; }

        public bool OutputsTruncated { get; private set; }

        public void Apply(GeneratorOutputSnapshot snapshot)
        {
            GeneratedFileCount = snapshot.FileCount;
            GeneratedByteCount = snapshot.ByteCount;
            GeneratedLineCount = snapshot.LineCount;
            Preview = snapshot.Preview;
            OutputsTruncated = snapshot.IsTruncated;
        }
    }
}
