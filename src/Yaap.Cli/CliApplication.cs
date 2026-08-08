using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Yaap.Core;

namespace Yaap.Cli;

public static class CliApplication
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int ProfileFailed = 3;
    public const int PartialResult = 4;
    public const int Canceled = 130;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default,
        IProfileRunner? profileRunner = null)
    {
        try
        {
            if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
            {
                await output.WriteLineAsync(HelpText()).ConfigureAwait(false);
                return Success;
            }

            return arguments[0].ToLowerInvariant() switch
            {
                "profile" => await ProfileAsync(
                    arguments.Skip(1).ToArray(),
                    output,
                    profileRunner,
                    cancellationToken).ConfigureAwait(false),
                "configurations" => await ConfigurationsAsync(arguments.Skip(1).ToArray(), output, cancellationToken).ConfigureAwait(false),
                "history" => await HistoryAsync(arguments.Skip(1).ToArray(), output, cancellationToken).ConfigureAwait(false),
                "compare" => await CompareAsync(arguments.Skip(1).ToArray(), output, cancellationToken).ConfigureAwait(false),
                "export" => await ExportAsync(arguments.Skip(1).ToArray(), output, cancellationToken).ConfigureAwait(false),
                "analyze" => await AnalyzeAsync(arguments.Skip(1).ToArray(), output, cancellationToken).ConfigureAwait(false),
                "version" or "--version" => await VersionAsync(output).ConfigureAwait(false),
                _ => throw new CliUsageException($"Unknown command: {arguments[0]}"),
            };
        }
        catch (OperationCanceledException)
        {
            await WriteDiagnosticAsync(error, YaapErrors.Canceled()).ConfigureAwait(false);
            return Canceled;
        }
        catch (CliUsageException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await error.WriteLineAsync("Run 'yaap help' for usage.").ConfigureAwait(false);
            return UsageError;
        }
        catch (YaapException exception)
        {
            await WriteDiagnosticAsync(error, exception.Diagnostic).ConfigureAwait(false);
            return ProfileFailed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            await WriteDiagnosticAsync(error, YaapErrors.HistoryFailed(exception.Message)).ConfigureAwait(false);
            return ProfileFailed;
        }
    }

    private static async Task<int> ProfileAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        IProfileRunner? profileRunner,
        CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(arguments);
        parsed.Validate(
            new[]
            {
                "configuration", "mode", "warmups", "iterations", "clean", "restore",
                "artifacts-path", "history", "retention", "export", "output",
            },
            new[] { "isolated", "no-isolated", "json", "no-clean", "no-restore" },
            minimumPositionals: 1,
            maximumPositionals: 1);
        parsed.RejectConflict("clean", "no-clean");
        parsed.RejectConflict("restore", "no-restore");
        parsed.RejectConflict("isolated", "no-isolated");
        string target = parsed.RequirePositional(0, "profile requires a target path.");
        ProfileMode mode = parsed.GetEnum("mode", ProfileMode.Warm);
        ProfileOptions defaults = ProfileOptions.ForMode(target, mode);
        ProfileOptions options = defaults with
        {
            Configuration = parsed.Get("configuration") ?? defaults.Configuration,
            WarmupCount = parsed.GetInt("warmups", defaults.WarmupCount),
            IterationCount = parsed.GetInt("iterations", defaults.IterationCount),
            CleanBeforeEach = parsed.GetBool("clean", !parsed.HasFlag("no-clean")),
            Restore = parsed.GetBool("restore", !parsed.HasFlag("no-restore")),
            Isolated = !parsed.HasFlag("no-isolated"),
            ArtifactsPath = parsed.Get("artifacts-path"),
            HistoryPath = parsed.Get("history"),
            RetentionCount = parsed.GetInt("retention", defaults.RetentionCount),
        };

        bool json = parsed.HasFlag("json");
        IProgress<ProfileProgress>? progress = json
            ? null
            : new InlineProgress<ProfileProgress>(item =>
                output.WriteLine($"[{item.Stage}] {item.Message}"));
        ProfileRun run = await (profileRunner ?? new ProfileRunner()).RunAsync(
            options,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (json)
        {
            await WriteRunJsonAsync(
                output,
                run,
                new HistoryStore(options.HistoryPath).StreamGeneratedOutputsAsync(
                    run.Id,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteRunSummaryAsync(output, run).ConfigureAwait(false);
        }

        string? exportFormat = parsed.Get("export");
        if (exportFormat is not null)
        {
            string outputPath = parsed.Get("output") ?? throw new CliUsageException("--export requires --output.");
            await RunExporter.ExportAsync(
                run,
                ParseExportFormat(exportFormat),
                outputPath,
                new HistoryStore(options.HistoryPath).StreamGeneratedOutputsAsync(
                    run.Id,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        return run.Status switch
        {
            RunStatus.Succeeded => Success,
            RunStatus.Canceled => Canceled,
            RunStatus.Partial => PartialResult,
            _ => ProfileFailed,
        };
    }

    private static async Task<int> ConfigurationsAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(arguments);
        parsed.Validate(Array.Empty<string>(), Array.Empty<string>(), 1, 1);
        TargetInfo target = await TargetDiscovery.DiscoverAsync(
            parsed.RequirePositional(0, "configurations requires a target path."),
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(output, target, cancellationToken).ConfigureAwait(false);
        return Success;
    }

    private static async Task<int> HistoryAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
        {
            throw new CliUsageException("history requires list, show, or delete.");
        }

        ParsedArguments parsed = ParsedArguments.Parse(arguments.Skip(1).ToArray());
        HistoryStore history = new(parsed.Get("history"));
        switch (arguments[0].ToLowerInvariant())
        {
            case "list":
                parsed.Validate(
                    new[] { "search", "status", "from", "to", "limit", "history" },
                    Array.Empty<string>(),
                    0,
                    0);
                RunStatus? status = parsed.Get("status") is { } statusText
                    ? ParseEnum<RunStatus>(statusText, "status")
                    : null;
                IReadOnlyList<RunSummary> summaries = await history.ListAsync(
                    new HistoryQuery(
                        parsed.Get("search"),
                        status,
                        parsed.GetDateTime("from"),
                        parsed.GetDateTime("to"),
                        parsed.GetNullableInt("limit")),
                    cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(output, summaries, cancellationToken).ConfigureAwait(false);
                return Success;
            case "show":
                parsed.Validate(new[] { "history" }, Array.Empty<string>(), 1, 1);
                ProfileRun run = await history.LoadAsync(
                    parsed.RequireGuid(0, "history show requires a run id."),
                    cancellationToken).ConfigureAwait(false);
                await WriteRunJsonAsync(
                    output,
                    run,
                    history.StreamGeneratedOutputsAsync(run.Id, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return Success;
            case "delete":
                parsed.Validate(new[] { "history" }, new[] { "force" }, 1, 1);
                if (!parsed.HasFlag("force"))
                {
                    throw new CliUsageException("history delete is non-interactive and requires --force.");
                }

                Guid id = parsed.RequireGuid(0, "history delete requires a run id.");
                await history.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(id.ToString("D")).ConfigureAwait(false);
                return Success;
            default:
                throw new CliUsageException($"Unknown history command: {arguments[0]}");
        }
    }

    private static async Task<int> CompareAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(arguments);
        parsed.Validate(new[] { "history" }, Array.Empty<string>(), 2, 2);
        HistoryStore history = new(parsed.Get("history"));
        ProfileRun baseline = await history.LoadAsync(
            parsed.RequireGuid(0, "compare requires baseline and candidate run ids."),
            cancellationToken).ConfigureAwait(false);
        ProfileRun candidate = await history.LoadAsync(
            parsed.RequireGuid(1, "compare requires baseline and candidate run ids."),
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            output,
            RunComparison.Compare(baseline, candidate, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return Success;
    }

    private static async Task<int> ExportAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(arguments);
        parsed.Validate(new[] { "format", "output", "history" }, Array.Empty<string>(), 1, 1);
        Guid id = parsed.RequireGuid(0, "export requires a run id.");
        string formatText = parsed.Get("format") ?? throw new CliUsageException("export requires --format.");
        string outputPath = parsed.Get("output") ?? throw new CliUsageException("export requires --output.");
        HistoryStore history = new(parsed.Get("history"));
        ProfileRun run = await history.LoadAsync(
            id,
            cancellationToken).ConfigureAwait(false);
        await RunExporter.ExportAsync(
            run,
            ParseExportFormat(formatText),
            outputPath,
            history.StreamGeneratedOutputsAsync(id, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(Path.GetFullPath(outputPath)).ConfigureAwait(false);
        return Success;
    }

    private static async Task<int> AnalyzeAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(arguments);
        parsed.Validate(Array.Empty<string>(), Array.Empty<string>(), 1, 1);
        BinlogAnalysis result = await new BinlogAnalyzer().AnalyzeAsync(
            parsed.RequirePositional(0, "analyze requires a binlog path."),
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(output, result, cancellationToken).ConfigureAwait(false);
        return Success;
    }

    private static async Task<int> VersionAsync(TextWriter output)
    {
        string version = typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        await output.WriteLineAsync(version).ConfigureAwait(false);
        return Success;
    }

    private static async Task WriteRunSummaryAsync(TextWriter output, ProfileRun run)
    {
        await output.WriteLineAsync($"Run: {run.Id:D}").ConfigureAwait(false);
        await output.WriteLineAsync($"Status: {run.Status}").ConfigureAwait(false);
        await output.WriteLineAsync($"Analyzer metrics: {run.Analyzers.Count}").ConfigureAwait(false);
        await output.WriteLineAsync($"Generator metrics: {run.Generators.Count}").ConfigureAwait(false);
        foreach (RunDiagnostic diagnostic in run.Diagnostics)
        {
            await WriteDiagnosticAsync(output, diagnostic).ConfigureAwait(false);
        }
    }

    private static Task WriteDiagnosticAsync(TextWriter writer, RunDiagnostic diagnostic)
    {
        return writer.WriteLineAsync(
            $"{diagnostic.Code}: {diagnostic.Message}{Environment.NewLine}" +
            $"詳細:{Environment.NewLine}{diagnostic.Detail}{Environment.NewLine}" +
            $"対処:{Environment.NewLine}{diagnostic.SuggestedAction}");
    }

    private static async Task WriteJsonAsync<T>(
        TextWriter output,
        T value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using TextWriterJsonBuffer buffer = new(output, cancellationToken);
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, HistoryStore.GetJsonOptions());
        writer.Flush();
        buffer.FlushDecoder();
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync().ConfigureAwait(false);
    }

    private static async Task WriteRunJsonAsync(
        TextWriter output,
        ProfileRun run,
        IAsyncEnumerable<GeneratedOutput> generatedOutputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using TextWriterJsonBuffer buffer = new(output, cancellationToken);
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WritePropertyName("run");
        JsonSerializer.Serialize(writer, run, HistoryStore.GetJsonOptions());
        writer.WritePropertyName("generatedOutputs");
        writer.WriteStartArray();
        int pendingFlush = 0;
        await foreach (GeneratedOutput generated in generatedOutputs
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, generated, HistoryStore.GetJsonOptions());
            if (++pendingFlush == 128)
            {
                writer.Flush();
                pendingFlush = 0;
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        buffer.FlushDecoder();
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync().ConfigureAwait(false);
    }

    private static ExportFormat ParseExportFormat(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "csv" => ExportFormat.Csv,
            "json" => ExportFormat.Json,
            "md" or "markdown" => ExportFormat.Markdown,
            _ => throw new CliUsageException($"Unsupported export format: {value}"),
        };
    }

    private static T ParseEnum<T>(string value, string option)
        where T : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: true, out T result)
            ? result
            : throw new CliUsageException($"Invalid --{option} value: {value}");
    }

    private static string HelpText() => """
        YetAnotherAnalyzerProfiler (YAAP)

        Usage:
          yaap profile <target.sln|target.slnx|target.csproj> [options]
          yaap configurations <target>
          yaap history list [--search text] [--status status] [--from ISO-8601]
              [--to ISO-8601] [--limit count] [--history path]
          yaap history show <id> [--history path]
          yaap history delete <id> --force [--history path]
          yaap compare <baseline-id> <candidate-id> [--history path]
          yaap export <id> --format csv|json|markdown --output path [--history path]
          yaap analyze <build.binlog>
          yaap version

        Profile options:
          --configuration <name>   Build configuration (default: Release)
          --mode warm|cold|custom  Measurement preset (default: warm)
          --warmups <count>        Unmeasured builds (default: 1)
          --iterations <count>     Measured builds (default: 3)
          --no-clean               Do not clean before each measured build
          --clean <true|false>     Explicit clean policy for custom mode
          --no-restore             Skip the single restore
          --restore <true|false>   Explicit restore policy
          --isolated               Use isolated output (default)
          --no-isolated            Allow the target's ordinary bin/obj output
          --artifacts-path <path>  Explicit isolated artifacts directory
          --history <path>         History directory
          --retention <count>      Number of runs to retain (default: 50)
          --json                   Emit the complete run as JSON
          --export <format> --output <path>
        """;

    private sealed class CliUsageException : Exception
    {
        public CliUsageException(string message)
            : base(message)
        {
        }
    }

    private sealed class ParsedArguments
    {
        private static readonly HashSet<string> FlagOptions = new(
            new[] { "force", "isolated", "no-isolated", "json", "no-clean", "no-restore" },
            StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string?> _options;

        private ParsedArguments(IReadOnlyList<string> positionals, Dictionary<string, string?> options)
        {
            Positionals = positionals;
            _options = options;
        }

        public IReadOnlyList<string> Positionals { get; }

        public static ParsedArguments Parse(IReadOnlyList<string> arguments)
        {
            List<string> positionals = new();
            Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
            bool positionalOnly = false;
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index];
                if (!positionalOnly && argument.Equals("--", StringComparison.Ordinal))
                {
                    positionalOnly = true;
                    continue;
                }

                if (positionalOnly || !argument.StartsWith("--", StringComparison.Ordinal))
                {
                    positionals.Add(argument);
                    continue;
                }

                string name = argument[2..];
                if (name.Length == 0)
                {
                    throw new CliUsageException("Invalid empty option.");
                }

                string originalName = name;

                string? value = null;
                int equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                    if (FlagOptions.Contains(name))
                    {
                        throw new CliUsageException($"--{name} does not accept a value.");
                    }
                }
                else if (!FlagOptions.Contains(name) &&
                         index + 1 < arguments.Count &&
                         !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = arguments[++index];
                }

                if (name.Length == 0)
                {
                    throw new CliUsageException($"Invalid option: --{originalName}");
                }

                if (!options.TryAdd(name, value))
                {
                    throw new CliUsageException($"Duplicate option: --{name}");
                }
            }

            return new ParsedArguments(positionals, options);
        }

        public bool HasFlag(string name) => _options.TryGetValue(name, out string? value) && value is null;

        public void Validate(
            IReadOnlyCollection<string> valueOptions,
            IReadOnlyCollection<string> flagOptions,
            int minimumPositionals,
            int maximumPositionals)
        {
            HashSet<string> allowedValues = new(valueOptions, StringComparer.OrdinalIgnoreCase);
            HashSet<string> allowedFlags = new(flagOptions, StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string? value) in _options)
            {
                if (!allowedValues.Contains(name) && !allowedFlags.Contains(name))
                {
                    throw new CliUsageException($"Unknown option: --{name}");
                }

                if (allowedFlags.Contains(name) && value is not null)
                {
                    throw new CliUsageException($"--{name} does not accept a value.");
                }

                if (allowedValues.Contains(name) && value is null)
                {
                    throw new CliUsageException($"--{name} requires a value.");
                }
            }

            if (Positionals.Count < minimumPositionals)
            {
                throw new CliUsageException("A required positional argument is missing.");
            }

            if (Positionals.Count > maximumPositionals)
            {
                throw new CliUsageException($"Unexpected positional argument: {Positionals[maximumPositionals]}");
            }
        }

        public void RejectConflict(string option, string conflictingOption)
        {
            if (_options.ContainsKey(option) && _options.ContainsKey(conflictingOption))
            {
                throw new CliUsageException($"--{option} cannot be combined with --{conflictingOption}.");
            }
        }

        public string? Get(string name)
        {
            if (!_options.TryGetValue(name, out string? value))
            {
                return null;
            }

            return value ?? throw new CliUsageException($"--{name} requires a value.");
        }

        public int GetInt(string name, int defaultValue) => GetNullableInt(name) ?? defaultValue;

        public bool GetBool(string name, bool defaultValue)
        {
            string? value = Get(name);
            if (value is null)
            {
                return defaultValue;
            }

            return bool.TryParse(value, out bool result)
                ? result
                : throw new CliUsageException($"--{name} requires true or false.");
        }

        public int? GetNullableInt(string name)
        {
            string? value = Get(name);
            if (value is null)
            {
                return null;
            }

            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result)
                ? result
                : throw new CliUsageException($"--{name} requires an integer.");
        }

        public DateTimeOffset? GetDateTime(string name)
        {
            string? value = Get(name);
            if (value is null)
            {
                return null;
            }

            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result)
                ? result
                : throw new CliUsageException($"--{name} requires an ISO-8601 date/time.");
        }

        public T GetEnum<T>(string name, T defaultValue)
            where T : struct, Enum
        {
            string? value = Get(name);
            return value is null ? defaultValue : ParseEnum<T>(value, name);
        }

        public string RequirePositional(int index, string message)
        {
            return index < Positionals.Count ? Positionals[index] : throw new CliUsageException(message);
        }

        public Guid RequireGuid(int index, string message)
        {
            string value = RequirePositional(index, message);
            return Guid.TryParse(value, out Guid result)
                ? result
                : throw new CliUsageException($"Invalid run id: {value}");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TextWriterJsonBuffer : IBufferWriter<byte>, IDisposable
    {
        private readonly TextWriter _writer;
        private readonly CancellationToken _cancellationToken;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        private bool _disposed;

        public TextWriterJsonBuffer(TextWriter writer, CancellationToken cancellationToken)
        {
            _writer = writer;
            _cancellationToken = cancellationToken;
        }

        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _cancellationToken.ThrowIfCancellationRequested();
            char[] characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(count));
            try
            {
                _decoder.Convert(
                    _buffer.AsSpan(0, count),
                    characters,
                    flush: false,
                    out _,
                    out int charactersUsed,
                    out _);
                _writer.Write(characters, 0, charactersUsed);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(characters);
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer;
        }

        public void FlushDecoder()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Span<char> characters = stackalloc char[2];
            _decoder.Convert(
                ReadOnlySpan<byte>.Empty,
                characters,
                flush: true,
                out _,
                out int charactersUsed,
                out _);
            if (charactersUsed > 0)
            {
                _writer.Write(characters[..charactersUsed]);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            byte[] buffer = _buffer;
            _buffer = Array.Empty<byte>();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        private void EnsureBuffer(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cancellationToken.ThrowIfCancellationRequested();
            int required = Math.Max(sizeHint, 1);
            if (required <= _buffer.Length)
            {
                return;
            }

            byte[] replacement = ArrayPool<byte>.Shared.Rent(required);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = replacement;
        }
    }
}
