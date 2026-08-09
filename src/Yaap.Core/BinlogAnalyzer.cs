using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace Yaap.Core;

public sealed record BinlogAnalysis(
    IReadOnlyList<AnalyzerSample> Analyzers,
    IReadOnlyList<GeneratorSample> Generators,
    IReadOnlyList<RunDiagnostic> Diagnostics,
    long EventCount,
    IReadOnlyList<CompilerInvocation> CompilerInvocations)
{
    public double? CompilerReportedAnalyzerTotalMilliseconds { get; init; }

    public double? CompilerReportedGeneratorTotalMilliseconds { get; init; }
}

public sealed record CompilerInvocation(string CommandLine, string WorkingDirectory);

public interface IBinlogAnalyzer
{
    Task<BinlogAnalysis> AnalyzeAsync(
        string binlogPath,
        CancellationToken cancellationToken = default,
        Action<CompilerInvocation>? compilerInvocationSink = null);
}

public sealed partial class BinlogAnalyzer : IBinlogAnalyzer
{
    private const int MaximumUnrecognizedLines = 20;

    public Task<BinlogAnalysis> AnalyzeAsync(
        string binlogPath,
        CancellationToken cancellationToken = default,
        Action<CompilerInvocation>? compilerInvocationSink = null)
    {
        if (!File.Exists(binlogPath))
        {
            throw new YaapException(YaapErrors.BinlogFailed($"File not found: {binlogPath}"));
        }

        return Task.Run(
            () => Replay(binlogPath, cancellationToken, compilerInvocationSink),
            cancellationToken);
    }

    private static BinlogAnalysis Replay(
        string binlogPath,
        CancellationToken cancellationToken,
        Action<CompilerInvocation>? compilerInvocationSink)
    {
        List<AnalyzerSample> analyzers = new();
        List<GeneratorSample> generators = new();
        List<RunDiagnostic> diagnostics = new();
        List<CompilerInvocation> compilerInvocations = new();
        ReportParser parser = new(analyzers, generators, diagnostics);
        long eventCount = 0;
        BinaryLogReplayEventSource source = new()
        {
            AllowForwardCompatibility = true,
        };
        source.AnyEventRaised += (_, eventArgs) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventCount++;
            if (eventArgs is TaskCommandLineEventArgs commandLine &&
                commandLine.TaskName.Equals("Csc", StringComparison.OrdinalIgnoreCase) &&
                commandLine.CommandLine.Contains("reportanalyzer", StringComparison.OrdinalIgnoreCase))
            {
                string directory = string.IsNullOrWhiteSpace(commandLine.ProjectFile)
                    ? Path.GetDirectoryName(binlogPath)!
                    : Path.GetDirectoryName(commandLine.ProjectFile)!;
                CompilerInvocation invocation = new(commandLine.CommandLine, directory);
                if (compilerInvocationSink is null)
                {
                    compilerInvocations.Add(invocation);
                }
                else
                {
                    compilerInvocationSink(invocation);
                }
            }

            if (eventArgs is BuildMessageEventArgs message && !string.IsNullOrWhiteSpace(message.Message))
            {
                parser.Accept(message.Message);
            }
        };
        source.RecoverableReadError += error =>
        {
            if (diagnostics.Count < MaximumUnrecognizedLines)
            {
                diagnostics.Add(YaapErrors.BinlogFailed(error.ToString() ?? "Recoverable binlog read error."));
            }
        };

        try
        {
            using FileStream input = new(
                binlogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            source.Replay(input, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            throw new YaapException(YaapErrors.BinlogFailed(exception.Message), exception);
        }

        return new BinlogAnalysis(analyzers, generators, diagnostics, eventCount, compilerInvocations)
        {
            CompilerReportedAnalyzerTotalMilliseconds = parser.AnalyzerTotalMilliseconds,
            CompilerReportedGeneratorTotalMilliseconds = parser.GeneratorTotalMilliseconds,
        };
    }

    public static CompilerReportAccumulator CreateCompilerReportAccumulator() => new();

    public sealed class CompilerReportAccumulator
    {
        private readonly object _sync = new();
        private readonly List<AnalyzerSample> _analyzers = new();
        private readonly List<GeneratorSample> _generators = new();
        private readonly List<RunDiagnostic> _diagnostics = new();
        private readonly ReportParser _parser;

        internal CompilerReportAccumulator()
        {
            _parser = new ReportParser(_analyzers, _generators, _diagnostics);
        }

        public void Accept(string line, bool isError = false)
        {
            lock (_sync)
            {
                _parser.Accept(line);
            }
        }

        public BinlogAnalysis Complete()
        {
            lock (_sync)
            {
                return new BinlogAnalysis(
                    _analyzers.ToArray(),
                    _generators.ToArray(),
                    _diagnostics.ToArray(),
                    0,
                    Array.Empty<CompilerInvocation>())
                {
                    CompilerReportedAnalyzerTotalMilliseconds = _parser.AnalyzerTotalMilliseconds,
                    CompilerReportedGeneratorTotalMilliseconds = _parser.GeneratorTotalMilliseconds,
                };
            }
        }
    }

    private sealed class ReportParser
    {
        private readonly List<AnalyzerSample> _analyzers;
        private readonly List<GeneratorSample> _generators;
        private readonly List<RunDiagnostic> _diagnostics;
        private ReportSection _section;
        private bool _sawReport;
        private bool _sawAnalyzerReport;
        private bool _sawGeneratorReport;
        private string? _currentAssembly;
        private List<AnalyzerSample>? _currentAnalyzerReport;
        private List<GeneratorSample>? _currentGeneratorReport;
        private double _completedAnalyzerTotalMilliseconds;
        private double _completedGeneratorTotalMilliseconds;

        public ReportParser(
            List<AnalyzerSample> analyzers,
            List<GeneratorSample> generators,
            List<RunDiagnostic> diagnostics)
        {
            _analyzers = analyzers;
            _generators = generators;
            _diagnostics = diagnostics;
        }

        public double? AnalyzerTotalMilliseconds => _sawAnalyzerReport
            ? _completedAnalyzerTotalMilliseconds +
              Statistics.CompilerReportedAnalyzerTotal(
                  _currentAnalyzerReport is null
                      ? Array.Empty<AnalyzerSample>()
                      : _currentAnalyzerReport)
            : null;

        public double? GeneratorTotalMilliseconds => _sawGeneratorReport
            ? _completedGeneratorTotalMilliseconds +
              Statistics.CompilerReportedGeneratorTotal(
                  _currentGeneratorReport is null
                      ? Array.Empty<GeneratorSample>()
                      : _currentGeneratorReport)
            : null;

        public void Accept(string message)
        {
            foreach (string originalLine in message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = originalLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (IsGeneratorHeading(line))
                {
                    if (_currentGeneratorReport is not null)
                    {
                        _completedGeneratorTotalMilliseconds +=
                            Statistics.CompilerReportedGeneratorTotal(_currentGeneratorReport);
                    }

                    _currentGeneratorReport = new List<GeneratorSample>();
                    _sawGeneratorReport = true;
                    _section = ReportSection.Generator;
                    _currentAssembly = null;
                    _sawReport = true;
                    continue;
                }

                if (IsAnalyzerHeading(line))
                {
                    if (_currentAnalyzerReport is not null)
                    {
                        _completedAnalyzerTotalMilliseconds +=
                            Statistics.CompilerReportedAnalyzerTotal(_currentAnalyzerReport);
                    }

                    _currentAnalyzerReport = new List<AnalyzerSample>();
                    _sawAnalyzerReport = true;
                    _section = ReportSection.Analyzer;
                    _currentAssembly = null;
                    _sawReport = true;
                    continue;
                }

                if (_section == ReportSection.Analyzer &&
                    (line.Contains("Rule", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase)) &&
                    line.Contains("Time", StringComparison.OrdinalIgnoreCase))
                {
                    _section = ReportSection.Diagnostic;
                    continue;
                }

                if (_section != ReportSection.None && LooksLikeUnknownTimedRowRegex().IsMatch(line))
                {
                    AddUnrecognized(line);
                    continue;
                }

                Match match = TimedRowRegex().Match(line);
                if (!match.Success || _section == ReportSection.None)
                {
                    continue;
                }

                if (!TryParseSeconds(match.Groups["seconds"].Value, out double milliseconds))
                {
                    AddUnrecognized(line);
                    continue;
                }

                string value = match.Groups["identity"].Value.Trim();
                if (value.StartsWith('|'))
                {
                    AddUnrecognized(line);
                    continue;
                }

                if (value.Equals("Analyzer", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Generator", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool assemblyRow = TryGetAssemblyName(value, out string? assemblyName);
                if (assemblyRow)
                {
                    _currentAssembly = assemblyName;
                }

                (string identity, string assembly) = assemblyRow
                    ? (assemblyName!, assemblyName!)
                    : SplitIdentity(value, _currentAssembly);
                switch (_section)
                {
                    case ReportSection.Generator:
                        GeneratorSample generator = new(identity, assembly, milliseconds);
                        _generators.Add(generator);
                        _currentGeneratorReport?.Add(generator);
                        break;
                    case ReportSection.Diagnostic:
                        Match diagnosticMatch = DiagnosticIdRegex().Match(identity);
                        string? diagnosticId = diagnosticMatch.Success ? diagnosticMatch.Value : null;
                        AnalyzerSample diagnostic = new(
                            identity,
                            assembly,
                            MetricKind.Diagnostic,
                            diagnosticId,
                            milliseconds);
                        _analyzers.Add(diagnostic);
                        _currentAnalyzerReport?.Add(diagnostic);
                        break;
                    default:
                        Match analyzerDiagnostic = DiagnosticIdRegex().Match(identity);
                        AnalyzerSample analyzer = new(
                            identity,
                            assembly,
                            assemblyRow || !analyzerDiagnostic.Success
                                ? MetricKind.Analyzer
                                : MetricKind.Diagnostic,
                            analyzerDiagnostic.Success ? analyzerDiagnostic.Value : null,
                            milliseconds);
                        _analyzers.Add(analyzer);
                        _currentAnalyzerReport?.Add(analyzer);
                        break;
                }
            }
        }

        private static bool IsAnalyzerHeading(string line)
        {
            return (line.Contains("analyzer", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("execution time", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("total", StringComparison.OrdinalIgnoreCase)) ||
                line.Contains("アナライザー実行の合計時間", StringComparison.Ordinal);
        }

        private static bool IsGeneratorHeading(string line)
        {
            return (line.Contains("generator", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("execution time", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("total", StringComparison.OrdinalIgnoreCase)) ||
                line.Contains("ジェネレーターの合計実行時間", StringComparison.Ordinal);
        }

        private static bool TryParseSeconds(string value, out double milliseconds)
        {
            string normalized = value.Replace(',', '.');
            if (double.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double seconds))
            {
                milliseconds = seconds * 1000;
                return true;
            }

            milliseconds = 0;
            return false;
        }

        private static bool TryGetAssemblyName(string value, out string? assembly)
        {
            int version = value.IndexOf(", Version=", StringComparison.OrdinalIgnoreCase);
            if (version > 0)
            {
                assembly = value[..version].Trim();
                return true;
            }

            assembly = null;
            return false;
        }

        private static (string Identity, string Assembly) SplitIdentity(
            string value,
            string? currentAssembly)
        {
            if (!string.IsNullOrWhiteSpace(currentAssembly))
            {
                return (value, currentAssembly);
            }

            Match parenthesized = ParenthesizedAssemblyRegex().Match(value);
            if (parenthesized.Success)
            {
                return (
                    parenthesized.Groups["identity"].Value.Trim(),
                    parenthesized.Groups["assembly"].Value.Trim());
            }

            int separator = value.IndexOf("::", StringComparison.Ordinal);
            if (separator > 0)
            {
                return (value[(separator + 2)..].Trim(), value[..separator].Trim());
            }

            string[] parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
            string assembly = currentAssembly ?? (parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : value);
            return (value, assembly);
        }

        private void AddUnrecognized(string line)
        {
            if (_sawReport && _diagnostics.Count < MaximumUnrecognizedLines)
            {
                _diagnostics.Add(YaapErrors.UnrecognizedReport(line));
            }
        }

        private enum ReportSection
        {
            None,
            Analyzer,
            Diagnostic,
            Generator,
        }
    }

    [GeneratedRegex(
        @"^\s*<?(?<seconds>\d+(?:[\.,]\d+)?)\s+(?:<?\d+(?:[\.,]\d+)?\s*%?\s+)?(?<identity>.+?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimedRowRegex();

    [GeneratedRegex(
        @"^\s*<?\d+(?:[\.,]\d+)?\s*(?:nanoseconds?|nsecs?|ns|microseconds?|usecs?|us|μs|µs|milliseconds?|msecs?|ms|seconds?|secs?|s|ticks?|ミリ秒|マイクロ秒|ナノ秒|秒)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LooksLikeUnknownTimedRowRegex();

    [GeneratedRegex(
        @"^(?<identity>.+?)\s+\((?<assembly>[^()]+)\)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedAssemblyRegex();

    [GeneratedRegex(@"\b(?:[A-Z]{2,6}\d{3,6})\b", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticIdRegex();
}
