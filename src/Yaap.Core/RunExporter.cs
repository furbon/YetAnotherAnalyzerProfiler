using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Yaap.Core;

public static class RunExporter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static async Task ExportAsync(
        ProfileRun run,
        ExportFormat format,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using FileStream stream = new(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        await ExportAsync(run, format, stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ExportAsync(
        ProfileRun run,
        ExportFormat format,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        switch (format)
        {
            case ExportFormat.Json:
                await JsonSerializer.SerializeAsync(
                    output,
                    run,
                    HistoryStore.GetJsonOptions(),
                    cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Csv:
                await WriteCsvAsync(run, output, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Markdown:
                await WriteMarkdownAsync(run, output, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.");
        }
    }

    private static async Task WriteCsvAsync(
        ProfileRun run,
        Stream output,
        CancellationToken cancellationToken)
    {
        await using StreamWriter writer = new(output, Utf8WithBom, 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync("category,identity,assembly,meanMilliseconds,minMilliseconds,maxMilliseconds,stdDevMilliseconds,samples,generatedFiles,generatedBytes,generatedLines,relativePath").ConfigureAwait(false);
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
                string.Empty)).ConfigureAwait(false);
            foreach (GeneratedOutput generated in metric.Outputs)
            {
                await writer.WriteLineAsync(string.Join(
                    ',',
                    "generated-output",
                    Csv(metric.Identity),
                    Csv(metric.Assembly),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    generated.ByteCount.ToString(CultureInfo.InvariantCulture),
                    generated.LineCount.ToString(CultureInfo.InvariantCulture),
                    Csv(generated.RelativePath))).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteMarkdownAsync(
        ProfileRun run,
        Stream output,
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
        await writer.WriteLineAsync("| Generator | 相対パス | バイト数 | 行数 |").ConfigureAwait(false);
        await writer.WriteLineAsync("| --- | --- | ---: | ---: |").ConfigureAwait(false);
        foreach (GeneratorMetric metric in run.Generators)
        {
            foreach (GeneratedOutput generated in metric.Outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(
                    $"| {EscapeMarkdown(metric.Identity)} | {EscapeMarkdown(generated.RelativePath)} | {generated.ByteCount} | {generated.LineCount} |")
                    .ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Csv(string value)
    {
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|");
}
