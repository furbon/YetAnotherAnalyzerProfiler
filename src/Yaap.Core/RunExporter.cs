using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Yaap.Core;

public static class RunExporter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static Task ExportAsync(
        ProfileRun run,
        ExportFormat format,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return ExportToPathAsync(run, format, outputPath, generatedOutputs: null, cancellationToken);
    }

    public static Task ExportAsync(
        ProfileRun run,
        ExportFormat format,
        string outputPath,
        IAsyncEnumerable<GeneratedOutput> generatedOutputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generatedOutputs);
        return ExportToPathAsync(run, format, outputPath, generatedOutputs, cancellationToken);
    }

    private static async Task ExportToPathAsync(
        ProfileRun run,
        ExportFormat format,
        string outputPath,
        IAsyncEnumerable<GeneratedOutput>? generatedOutputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(outputPath);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await ExportToStreamAsync(
                    run,
                    format,
                    stream,
                    generatedOutputs,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new YaapException(YaapErrors.ExportFailed(exception.Message), exception);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    public static Task ExportAsync(
        ProfileRun run,
        ExportFormat format,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        return ExportToStreamAsync(run, format, output, generatedOutputs: null, cancellationToken);
    }

    public static Task ExportAsync(
        ProfileRun run,
        ExportFormat format,
        Stream output,
        IAsyncEnumerable<GeneratedOutput> generatedOutputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generatedOutputs);
        return ExportToStreamAsync(run, format, output, generatedOutputs, cancellationToken);
    }

    private static async Task ExportToStreamAsync(
        ProfileRun run,
        ExportFormat format,
        Stream output,
        IAsyncEnumerable<GeneratedOutput>? generatedOutputs,
        CancellationToken cancellationToken)
    {
        switch (format)
        {
            case ExportFormat.Json:
                await WriteJsonAsync(run, output, generatedOutputs, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Csv:
                await WriteCsvAsync(run, output, generatedOutputs, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Markdown:
                await WriteMarkdownAsync(run, output, generatedOutputs, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.");
        }
    }

    private static async Task WriteJsonAsync(
        ProfileRun run,
        Stream output,
        IAsyncEnumerable<GeneratedOutput>? generatedOutputs,
        CancellationToken cancellationToken)
    {
        if (generatedOutputs is null)
        {
            await JsonSerializer.SerializeAsync(
                output,
                run,
                HistoryStore.GetJsonOptions(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteUtf8Async(output, "{\n  \"run\": ", cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(
            output,
            run,
            HistoryStore.GetJsonOptions(),
            cancellationToken).ConfigureAwait(false);
        await WriteUtf8Async(
            output,
            ",\n  \"generatedOutputs\": [",
            cancellationToken).ConfigureAwait(false);
        bool first = true;
        await foreach (GeneratedOutput generated in generatedOutputs
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!first)
            {
                await WriteUtf8Async(output, ",", cancellationToken).ConfigureAwait(false);
            }

            await WriteUtf8Async(output, "\n    ", cancellationToken).ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(
                output,
                generated,
                HistoryStore.GetJsonOptions(),
                cancellationToken).ConfigureAwait(false);
            first = false;
        }

        await WriteUtf8Async(
            output,
            first ? "]\n}" : "\n  ]\n}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCsvAsync(
        ProfileRun run,
        Stream output,
        IAsyncEnumerable<GeneratedOutput>? generatedOutputs,
        CancellationToken cancellationToken)
    {
        await using StreamWriter writer = new(output, Utf8WithBom, 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync("category,identity,assembly,meanMilliseconds,minMilliseconds,maxMilliseconds,stdDevMilliseconds,samples,generatedFiles,generatedBytes,generatedLines,relativePath,generatedOutputsTruncated").ConfigureAwait(false);
        foreach (StatisticalMetric metric in run.Analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(
                ',',
                Csv(metric.Kind.ToString().ToLowerInvariant()),
                Csv(metric.Identity),
                Csv(metric.Assembly),
                Number(metric.MeanMilliseconds),
                Number(metric.MinimumMilliseconds),
                Number(metric.MaximumMilliseconds),
                Number(metric.StandardDeviationMilliseconds),
                 metric.SampleCount.ToString(CultureInfo.InvariantCulture),
                 string.Empty,
                 string.Empty,
                 string.Empty,
                 string.Empty,
                 string.Empty)).ConfigureAwait(false);
        }

        foreach (GeneratorMetric metric in run.Generators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(
                ',',
                "generator",
                Csv(metric.Identity),
                Csv(metric.Assembly),
                Number(metric.MeanMilliseconds),
                Number(metric.MinimumMilliseconds),
                Number(metric.MaximumMilliseconds),
                Number(metric.StandardDeviationMilliseconds),
                metric.SampleCount.ToString(CultureInfo.InvariantCulture),
                metric.GeneratedFileCount.ToString(CultureInfo.InvariantCulture),
                metric.GeneratedByteCount.ToString(CultureInfo.InvariantCulture),
                metric.GeneratedLineCount.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                (generatedOutputs is null && metric.OutputsTruncated).ToString().ToLowerInvariant())).ConfigureAwait(false);
            if (generatedOutputs is null)
            {
                foreach (GeneratedOutput generated in metric.Outputs)
                {
                    await WriteGeneratedOutputCsvAsync(writer, generated, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (generatedOutputs is not null)
        {
            await foreach (GeneratedOutput generated in generatedOutputs
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                await WriteGeneratedOutputCsvAsync(writer, generated, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteGeneratedOutputCsvAsync(
        StreamWriter writer,
        GeneratedOutput generated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string line = string.Join(
            ',',
            "generated-output",
            Csv(generated.GeneratorIdentity),
            Csv(generated.GeneratorAssembly),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            generated.ByteCount.ToString(CultureInfo.InvariantCulture),
            generated.LineCount.ToString(CultureInfo.InvariantCulture),
            Csv(generated.RelativePath),
            string.Empty);
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteMarkdownAsync(
        ProfileRun run,
        Stream output,
        IAsyncEnumerable<GeneratedOutput>? generatedOutputs,
        CancellationToken cancellationToken)
    {
        await using StreamWriter writer = new(output, Utf8WithBom, 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync($"# YAAP 測定結果: {EscapeMarkdown(run.TargetName)}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync($"- ID: `{run.Id:D}`").ConfigureAwait(false);
        await writer.WriteLineAsync($"- 状態: `{run.Status}`").ConfigureAwait(false);
        await writer.WriteLineAsync($"- 構成: `{EscapeMarkdown(run.Configuration)}`").ConfigureAwait(false);
        await writer.WriteLineAsync($"- SDK: `{EscapeMarkdown(run.Environment.DotNetSdk)}`").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("## Analyzer").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("| 種別 | ID | アセンブリ | 平均 ms | 最小 ms | 最大 ms |").ConfigureAwait(false);
        await writer.WriteLineAsync("| --- | --- | --- | ---: | ---: | ---: |").ConfigureAwait(false);
        foreach (StatisticalMetric metric in run.Analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                $"| {metric.Kind} | {EscapeMarkdown(metric.Identity)} | {EscapeMarkdown(metric.Assembly)} | {Number(metric.MeanMilliseconds)} | {Number(metric.MinimumMilliseconds)} | {Number(metric.MaximumMilliseconds)} |").ConfigureAwait(false);
        }

        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("## Source Generator").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("Generator 時間は型単位の合計です。生成ファイル単位の時間ではありません。").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("| Generator | アセンブリ | 平均 ms | ファイル数 | バイト数 | 行数 |").ConfigureAwait(false);
        await writer.WriteLineAsync("| --- | --- | ---: | ---: | ---: | ---: |").ConfigureAwait(false);
        foreach (GeneratorMetric metric in run.Generators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                $"| {EscapeMarkdown(metric.Identity)} | {EscapeMarkdown(metric.Assembly)} | {Number(metric.MeanMilliseconds)} | {metric.GeneratedFileCount} | {metric.GeneratedByteCount} | {metric.GeneratedLineCount} |").ConfigureAwait(false);
        }

        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("### 生成ファイル一覧（時間指標ではありません）").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        if (generatedOutputs is null && run.Generators.Any(metric => metric.OutputsTruncated))
        {
            await writer.WriteLineAsync(
                "注: 生成ファイル一覧は各Generator最大100件のプレビューです。集計値は全件を表します。")
                .ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        await writer.WriteLineAsync("| Generator | 相対パス | バイト数 | 行数 |").ConfigureAwait(false);
        await writer.WriteLineAsync("| --- | --- | ---: | ---: |").ConfigureAwait(false);
        if (generatedOutputs is null)
        {
            foreach (GeneratorMetric metric in run.Generators)
            {
                foreach (GeneratedOutput generated in metric.Outputs)
                {
                    await WriteGeneratedOutputMarkdownAsync(writer, generated, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        else
        {
            await foreach (GeneratedOutput generated in generatedOutputs
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                await WriteGeneratedOutputMarkdownAsync(writer, generated, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteGeneratedOutputMarkdownAsync(
        StreamWriter writer,
        GeneratedOutput generated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(
            $"| {EscapeMarkdown(generated.GeneratorIdentity)} | {EscapeMarkdown(generated.RelativePath)} | {generated.ByteCount} | {generated.LineCount} |")
            .ConfigureAwait(false);
    }

    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = $"'{value}";
        }

        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static ValueTask WriteUtf8Async(
        Stream output,
        string value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return output.WriteAsync(bytes, cancellationToken);
    }

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|");

    private static void TryDeleteTemporaryFile(string path)
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
}
