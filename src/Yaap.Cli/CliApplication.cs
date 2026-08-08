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

            if (TryGetCommandHelp(arguments, out string? commandHelp))
            {
                await output.WriteLineAsync(commandHelp).ConfigureAwait(false);
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
                "version" or "--version" => await VersionAsync(arguments.Skip(1).ToArray(), output).ConfigureAwait(false),
                _ => throw new CliUsageException($"不明なコマンドです: {arguments[0]}"),
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
            await error.WriteLineAsync("使用方法は 'yaap help' で確認できます。").ConfigureAwait(false);
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
        string target = parsed.RequirePositional(0, "profile には測定対象のパスが必要です。");
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
        if (options.WarmupCount is < 0 or > 1000 ||
            options.IterationCount is < 1 or > 1000 ||
            options.RetentionCount < 1)
        {
            throw new CliUsageException("--warmups は 0～1000、--iterations は 1～1000、--retention は 1 以上を指定してください。");
        }

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
            HistoryStore history = new(options.HistoryPath);
            run = await history.LoadAsync(run.Id, cancellationToken).ConfigureAwait(false);
            await WriteRunJsonAsync(
                output,
                run,
                history.StreamGeneratedOutputsAsync(
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
            string outputPath = parsed.Get("output") ?? throw new CliUsageException("--export には --output が必要です。");
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
            parsed.RequirePositional(0, "configurations には測定対象のパスが必要です。"),
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
            throw new CliUsageException("history には list、show、delete のいずれかが必要です。");
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
                int? limit = parsed.GetNullableInt("limit");
                if (limit is <= 0 or > 10000)
                {
                    throw new CliUsageException("--limit は 1～10000 を指定してください。");
                }

                IReadOnlyList<RunSummary> summaries = await history.ListAsync(
                    new HistoryQuery(
                        parsed.Get("search"),
                        status,
                        parsed.GetDateTime("from"),
                        parsed.GetDateTime("to"),
                        limit),
                    cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(output, summaries, cancellationToken).ConfigureAwait(false);
                return Success;
            case "show":
                parsed.Validate(new[] { "history" }, Array.Empty<string>(), 1, 1);
                ProfileRun run = await history.LoadAsync(
                    parsed.RequireGuid(0, "history show には測定IDが必要です。"),
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
                    throw new CliUsageException("history delete は非対話操作のため --force が必要です。");
                }

                Guid id = parsed.RequireGuid(0, "history delete には測定IDが必要です。");
                await history.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(id.ToString("D")).ConfigureAwait(false);
                return Success;
            default:
                throw new CliUsageException($"不明な history コマンドです: {arguments[0]}");
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
            parsed.RequireGuid(0, "compare には基準と比較対象の測定IDが必要です。"),
            cancellationToken).ConfigureAwait(false);
        ProfileRun candidate = await history.LoadAsync(
            parsed.RequireGuid(1, "compare には基準と比較対象の測定IDが必要です。"),
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
        Guid id = parsed.RequireGuid(0, "export には測定IDが必要です。");
        string formatText = parsed.Get("format") ?? throw new CliUsageException("export には --format が必要です。");
        string outputPath = parsed.Get("output") ?? throw new CliUsageException("export には --output が必要です。");
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
            parsed.RequirePositional(0, "analyze には binlog のパスが必要です。"),
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(output, result, cancellationToken).ConfigureAwait(false);
        return Success;
    }

    private static async Task<int> VersionAsync(IReadOnlyList<string> arguments, TextWriter output)
    {
        if (arguments.Count != 0)
        {
            throw new CliUsageException("version に引数は指定できません。");
        }

        string version = typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        await output.WriteLineAsync(version).ConfigureAwait(false);
        return Success;
    }

    private static async Task WriteRunSummaryAsync(TextWriter output, ProfileRun run)
    {
        await output.WriteLineAsync($"測定ID: {run.Id:D}").ConfigureAwait(false);
        await output.WriteLineAsync($"状態: {LocalizedStatus(run.Status)}").ConfigureAwait(false);
        await output.WriteLineAsync($"Analyzer指標数: {run.Analyzers.Count}").ConfigureAwait(false);
        await output.WriteLineAsync($"Generator指標数: {run.Generators.Count}").ConfigureAwait(false);
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
            _ => throw new CliUsageException($"未対応の出力形式です: {value}"),
        };
    }

    private static string LocalizedStatus(RunStatus status) => status switch
    {
        RunStatus.Running => "測定中",
        RunStatus.Succeeded => "成功",
        RunStatus.Partial => "部分結果",
        RunStatus.Failed => "失敗",
        RunStatus.Canceled => "キャンセル",
        _ => status.ToString(),
    };

    private static T ParseEnum<T>(string value, string option)
        where T : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: true, out T result)
            ? result
            : throw new CliUsageException($"--{option} の値が無効です: {value}");
    }

    private static string HelpText() => """
        YetAnotherAnalyzerProfiler (YAAP)

        使用方法:
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

        profile の主なオプション:
          --configuration <name>   ビルド構成（既定: Release）
          --mode warm|cold|custom  測定プリセット（既定: warm）
          --warmups <0..1000>      集計しない事前ビルド（既定: 1）
          --iterations <1..1000>   集計するビルド（既定: 3）
          --no-clean               各測定前の clean を省略
          --clean <true|false>     custom の clean 方針
          --no-restore             最初の restore を省略
          --restore <true|false>   restore 方針
          --isolated               分離出力を使用（既定）
          --no-isolated            対象の通常の bin／obj を使用
          --artifacts-path <path>  分離出力先
          --history <path>         履歴ディレクトリ
          --retention <count>      履歴保持件数（既定: 50）
          --json                   完全な測定結果を JSON で出力
          --export <format> --output <path>

        詳細: yaap <command> --help
        終了コード: 0=成功、2=使用方法、3=失敗、4=部分結果、130=キャンセル
        """;

    private static bool TryGetCommandHelp(IReadOnlyList<string> arguments, out string? help)
    {
        bool requested = arguments.Skip(1).Any(argument => argument is "--help" or "-h");
        if (!requested)
        {
            help = null;
            return false;
        }

        string command = arguments[0].ToLowerInvariant();
        string? historyCommand = command == "history" && arguments.Count >= 2
            ? arguments[1].ToLowerInvariant()
            : null;
        help = (command, historyCommand) switch
        {
            ("profile", _) => "使用方法: yaap profile <target.sln|target.slnx|target.csproj> [options]\n範囲: --warmups 0..1000、--iterations 1..1000、--retention 1以上。詳細は yaap help を参照してください。",
            ("configurations", _) => "使用方法: yaap configurations <target>\n対象から利用可能なビルド構成を JSON で出力します。",
            ("history", "list") => "使用方法: yaap history list [--search text] [--status Succeeded|Partial|Failed|Canceled] [--from ISO-8601] [--to ISO-8601] [--limit count] [--history path]",
            ("history", "show") => "使用方法: yaap history show <run-id> [--history path]",
            ("history", "delete") => "使用方法: yaap history delete <run-id> --force [--history path]\n非対話で削除するため --force が必要です。",
            ("history", _) => "使用方法: yaap history <list|show|delete> [options]",
            ("compare", _) => "使用方法: yaap compare <baseline-id> <candidate-id> [--history path]",
            ("export", _) => "使用方法: yaap export <run-id> --format csv|json|markdown --output <path> [--history path]",
            ("analyze", _) => "使用方法: yaap analyze <build.binlog>\n既存 binlog のコンパイラー報告値を JSON で出力します。",
            ("version", _) or ("--version", _) => "使用方法: yaap version",
            _ => null,
        };
        return help is not null;
    }

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
                    throw new CliUsageException("空のオプションは指定できません。");
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
                        throw new CliUsageException($"--{name} に値は指定できません。");
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
                    throw new CliUsageException($"オプション名が無効です: --{originalName}");
                }

                if (!options.TryAdd(name, value))
                {
                    throw new CliUsageException($"オプションが重複しています: --{name}");
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
                    throw new CliUsageException($"不明なオプションです: --{name}");
                }

                if (allowedFlags.Contains(name) && value is not null)
                {
                    throw new CliUsageException($"--{name} に値は指定できません。");
                }

                if (allowedValues.Contains(name) && value is null)
                {
                    throw new CliUsageException($"--{name} には値が必要です。");
                }
            }

            if (Positionals.Count < minimumPositionals)
            {
                throw new CliUsageException("必須の位置引数がありません。");
            }

            if (Positionals.Count > maximumPositionals)
            {
                throw new CliUsageException($"余分な位置引数です: {Positionals[maximumPositionals]}");
            }
        }

        public void RejectConflict(string option, string conflictingOption)
        {
            if (_options.ContainsKey(option) && _options.ContainsKey(conflictingOption))
            {
                throw new CliUsageException($"--{option} と --{conflictingOption} は同時に指定できません。");
            }
        }

        public string? Get(string name)
        {
            if (!_options.TryGetValue(name, out string? value))
            {
                return null;
            }

            return value ?? throw new CliUsageException($"--{name} には値が必要です。");
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
                : throw new CliUsageException($"--{name} には true または false を指定してください。");
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
                : throw new CliUsageException($"--{name} には整数を指定してください。");
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
                : throw new CliUsageException($"--{name} には ISO-8601 形式の日時を指定してください。");
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
                : throw new CliUsageException($"測定IDが無効です: {value}");
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
