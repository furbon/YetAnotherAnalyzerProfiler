namespace Yaap.Core;

public static class Statistics
{
    public static IReadOnlyList<StatisticalMetric> AggregateAnalyzers(
        IEnumerable<MeasurementResult> measurements)
    {
        ProfileStatisticsAccumulator accumulator = new();
        accumulator.AddRange(measurements);
        return accumulator.CreateAnalyzerMetrics();
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
        ProfileStatisticsAccumulator accumulator = new();
        accumulator.AddRange(measurements);
        return accumulator.CreateGeneratorMetrics(outputSnapshots);
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

    public static double CompilerReportedAnalyzerTotal(IReadOnlyList<AnalyzerSample> samples)
    {
        Dictionary<string, (double Children, double? AssemblyTotal)> assemblies =
            new(StringComparer.Ordinal);
        foreach (AnalyzerSample sample in samples)
        {
            if (sample.Kind != MetricKind.Analyzer)
            {
                continue;
            }

            assemblies.TryGetValue(sample.Assembly, out (double Children, double? AssemblyTotal) current);
            if (sample.Identity.Equals(sample.Assembly, StringComparison.Ordinal))
            {
                current.AssemblyTotal = sample.ElapsedMilliseconds;
            }
            else
            {
                current.Children += sample.ElapsedMilliseconds;
            }

            assemblies[sample.Assembly] = current;
        }

        return assemblies.Values.Sum(value => value.AssemblyTotal ?? value.Children);
    }

    public static double CompilerReportedGeneratorTotal(IReadOnlyList<GeneratorSample> samples)
    {
        Dictionary<string, (double Children, double? AssemblyTotal)> assemblies =
            new(StringComparer.Ordinal);
        foreach (GeneratorSample sample in samples)
        {
            assemblies.TryGetValue(sample.Assembly, out (double Children, double? AssemblyTotal) current);
            if (sample.Identity.Equals(sample.Assembly, StringComparison.Ordinal))
            {
                current.AssemblyTotal = sample.ElapsedMilliseconds;
            }
            else
            {
                current.Children += sample.ElapsedMilliseconds;
            }

            assemblies[sample.Assembly] = current;
        }

        return assemblies.Values.Sum(value => value.AssemblyTotal ?? value.Children);
    }

}

internal sealed class ProfileStatisticsAccumulator
{
    private readonly Dictionary<
        (string Identity, string Assembly, MetricKind Kind, string? DiagnosticId),
        RunningStatistics> _analyzers = new();
    private readonly Dictionary<(string Identity, string Assembly), GeneratorAggregate> _generators = new();
    private readonly RunningStatistics _analyzerTotals = new();
    private readonly RunningStatistics _generatorTotals = new();

    public double AnalyzerTotalMeanMilliseconds => _analyzerTotals.Mean;

    public double GeneratorTotalMeanMilliseconds => _generatorTotals.Mean;

    public void AddRange(IEnumerable<MeasurementResult> measurements)
    {
        foreach (MeasurementResult measurement in measurements)
        {
            Add(measurement);
        }
    }

    public void Add(MeasurementResult measurement)
    {
        if (!measurement.BuildSucceeded)
        {
            return;
        }

        _analyzerTotals.Add(
            measurement.CompilerReportedAnalyzerTotalMilliseconds ??
            Statistics.CompilerReportedAnalyzerTotal(measurement.Analyzers));
        _generatorTotals.Add(
            measurement.CompilerReportedGeneratorTotalMilliseconds ??
            Statistics.CompilerReportedGeneratorTotal(measurement.Generators));

        foreach (AnalyzerSample sample in measurement.Analyzers)
        {
            (string Identity, string Assembly, MetricKind Kind, string? DiagnosticId) key =
                (sample.Identity, sample.Assembly, sample.Kind, sample.DiagnosticId);
            if (!_analyzers.TryGetValue(key, out RunningStatistics? statistics))
            {
                statistics = new RunningStatistics();
                _analyzers.Add(key, statistics);
            }

            statistics.Add(sample.ElapsedMilliseconds);
        }

        foreach (GeneratorSample sample in measurement.Generators)
        {
            (string Identity, string Assembly) key = (sample.Identity, sample.Assembly);
            if (!_generators.TryGetValue(key, out GeneratorAggregate? aggregate))
            {
                aggregate = new GeneratorAggregate(sample.Identity, sample.Assembly);
                _generators.Add(key, aggregate);
            }

            aggregate.Statistics.Add(sample.ElapsedMilliseconds);
        }

        foreach (GeneratedOutput output in measurement.GeneratedOutputs)
        {
            if (output.GeneratorAssembly.Length > 0 &&
                _generators.TryGetValue(
                    (output.GeneratorIdentity, output.GeneratorAssembly),
                    out GeneratorAggregate? exact))
            {
                exact.Outputs[output.RelativePath] = output;
                continue;
            }

            foreach (GeneratorAggregate aggregate in _generators.Values.Where(item =>
                         item.Identity.Equals(output.GeneratorIdentity, StringComparison.Ordinal)))
            {
                aggregate.Outputs[output.RelativePath] = output;
            }
        }
    }

    public IReadOnlyList<StatisticalMetric> CreateAnalyzerMetrics()
    {
        return _analyzers
            .Select(pair => new StatisticalMetric(
                pair.Key.Identity,
                pair.Key.Assembly,
                pair.Key.Kind,
                pair.Key.DiagnosticId,
                pair.Value.Mean,
                pair.Value.Minimum,
                pair.Value.Maximum,
                pair.Value.StandardDeviation,
                pair.Value.Count))
            .OrderByDescending(metric => metric.MeanMilliseconds)
            .ThenBy(metric => metric.Identity, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<GeneratorMetric> CreateGeneratorMetrics(
        IReadOnlyList<GeneratorOutputSnapshot>? outputSnapshots)
    {
        if (outputSnapshots is not null)
        {
            foreach (GeneratorOutputSnapshot snapshot in outputSnapshots)
            {
                if (snapshot.Assembly.Length > 0 &&
                    _generators.TryGetValue(
                        (snapshot.Identity, snapshot.Assembly),
                        out GeneratorAggregate? exact))
                {
                    exact.Apply(snapshot);
                    continue;
                }

                foreach (GeneratorAggregate aggregate in _generators.Values.Where(item =>
                             item.Identity.Equals(snapshot.Identity, StringComparison.Ordinal)))
                {
                    aggregate.Apply(snapshot);
                }
            }
        }

        return _generators.Values.Select(aggregate =>
            {
                GeneratedOutput[] generated = aggregate.Outputs.Values
                    .OrderBy(output => output.RelativePath, StringComparer.Ordinal)
                    .ToArray();
                return new GeneratorMetric(
                    aggregate.Identity,
                    aggregate.Assembly,
                    aggregate.Statistics.Mean,
                    aggregate.Statistics.Minimum,
                    aggregate.Statistics.Maximum,
                    aggregate.Statistics.StandardDeviation,
                    aggregate.Statistics.Count,
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

    private sealed class GeneratorAggregate(string identity, string assembly)
    {
        public string Identity { get; } = identity;

        public string Assembly { get; } = assembly;

        public RunningStatistics Statistics { get; } = new();

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

    private sealed class RunningStatistics
    {
        private double _mean;
        private double _sumOfSquaredDifferences;

        public int Count { get; private set; }

        public double Mean => Count == 0 ? 0 : _mean;

        public double Minimum { get; private set; } = double.PositiveInfinity;

        public double Maximum { get; private set; } = double.NegativeInfinity;

        public double StandardDeviation => Count <= 1
            ? 0
            : Math.Sqrt(_sumOfSquaredDifferences / Count);

        public void Add(double value)
        {
            Count++;
            Minimum = Math.Min(Minimum, value);
            Maximum = Math.Max(Maximum, value);
            double delta = value - _mean;
            _mean += delta / Count;
            double deltaFromUpdatedMean = value - _mean;
            _sumOfSquaredDifferences += delta * deltaFromUpdatedMean;
        }
    }
}
