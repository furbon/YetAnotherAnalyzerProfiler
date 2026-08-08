using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaap.Core;

public sealed class HistoryStore
{
    public const string HistoryEnvironmentVariable = "YAAP_HISTORY_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public HistoryStore(string? rootPath = null)
    {
        RootPath = Path.GetFullPath(rootPath ?? GetDefaultRootPath());
    }

    public string RootPath { get; }

    public string GetRunDirectory(Guid id) => Path.Combine(RootPath, "runs", id.ToString("D"));

    public async Task SaveAsync(ProfileRun run, CancellationToken cancellationToken = default)
    {
        try
        {
            string directory = GetRunDirectory(run.Id);
            Directory.CreateDirectory(directory);
            await WriteAtomicallyAsync(
                Path.Combine(directory, "run.json"),
                run,
                cancellationToken).ConfigureAwait(false);
            await WriteAtomicallyAsync(
                Path.Combine(directory, "summary.json"),
                run.ToSummary(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
        }
    }

    public async Task<ProfileRun> LoadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(GetRunDirectory(id), "run.json");
        if (!File.Exists(path))
        {
            throw new YaapException(YaapErrors.HistoryFailed($"Run does not exist: {id:D}"));
        }

        try
        {
            await using FileStream stream = OpenSequential(path);
            ProfileRun? run = await JsonSerializer.DeserializeAsync<ProfileRun>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (run is null || run.SchemaVersion != ProfileRun.CurrentSchemaVersion)
            {
                throw new JsonException("Unsupported or empty history schema.");
            }

            return run;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
        }
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(
        HistoryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new HistoryQuery();
        string runsPath = Path.Combine(RootPath, "runs");
        if (!Directory.Exists(runsPath))
        {
            return Array.Empty<RunSummary>();
        }

        List<RunSummary> summaries = new();
        foreach (string path in Directory.EnumerateFiles(runsPath, "summary.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using FileStream stream = OpenSequential(path);
                RunSummary? summary = await JsonSerializer.DeserializeAsync<RunSummary>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (summary is not null && Matches(summary, query))
                {
                    summaries.Add(summary);
                }
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // A corrupt run is isolated. Loading that id reports the actionable error.
            }
        }

        IEnumerable<RunSummary> ordered = summaries.OrderByDescending(summary => summary.StartedAt);
        if (query.Limit is > 0)
        {
            ordered = ordered.Take(query.Limit.Value);
        }

        return ordered.ToArray();
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetRunDirectory(id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public async Task ApplyRetentionAsync(
        int retainCount,
        CancellationToken cancellationToken = default)
    {
        if (retainCount <= 0)
        {
            return;
        }

        IReadOnlyList<RunSummary> summaries = await ListAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (RunSummary expired in summaries.Skip(retainCount))
        {
            await DeleteAsync(expired.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public static JsonSerializerOptions GetJsonOptions() => new(JsonOptions);

    private static string GetDefaultRootPath()
    {
        string? configured = Environment.GetEnvironmentVariable(HistoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YAAP");
    }

    private static FileStream OpenSequential(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static bool Matches(RunSummary summary, HistoryQuery query)
    {
        if (query.Status is not null && summary.Status != query.Status)
        {
            return false;
        }

        if (query.From is not null && summary.StartedAt < query.From)
        {
            return false;
        }

        if (query.To is not null && summary.StartedAt > query.To)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            return summary.TargetName.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                summary.TargetPath.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                summary.Id.ToString("D").Contains(query.Search, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static async Task WriteAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
