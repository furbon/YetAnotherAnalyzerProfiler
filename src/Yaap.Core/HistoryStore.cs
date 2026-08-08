using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaap.Core;

public sealed class HistoryStore
{
    public const string HistoryEnvironmentVariable = "YAAP_HISTORY_PATH";
    public const int MaximumLabelLength = 120;

    private const string RunLeasesDirectoryName = "leases";
    private const string TombstonesDirectoryName = "tombstones";
    private const string GeneratedOutputsManifestName = "generated-outputs.ndjson";
    private const int GeneratedOutputPreviewLimit = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonOptions)
    {
        WriteIndented = false,
    };

    public HistoryStore(string? rootPath = null)
    {
        RootPath = Path.GetFullPath(rootPath ?? GetDefaultRootPath());
    }

    public string RootPath { get; }

    public string GetRunDirectory(Guid id) => Path.Combine(RootPath, "runs", id.ToString("D"));

    internal IDisposable AcquireRunLease(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string path = GetRunLeasePath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileStream stream = new(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new RunLease(stream);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(
                $"Could not acquire the run lease for {id:D}: {exception.Message}"), exception);
        }
    }

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

            string summaryPath = Path.Combine(GetRunDirectory(id), "summary.json");
            if (File.Exists(summaryPath))
            {
                await using FileStream summaryStream = OpenSequential(summaryPath);
                RunSummary? summary = await JsonSerializer.DeserializeAsync<RunSummary>(
                    summaryStream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                run.Label = summary?.Label;
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

    internal async Task<IReadOnlyList<GeneratorOutputSnapshot>> ReplaceGeneratedOutputsAsync(
        Guid id,
        IAsyncEnumerable<GeneratedOutput> outputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetRunDirectory(id);
        string manifestPath = Path.Combine(directory, GeneratedOutputsManifestName);
        string temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        Dictionary<(string Identity, string Assembly), GeneratedOutputAccumulator> summaries = new();
        try
        {
            Directory.CreateDirectory(directory);
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (StreamWriter writer = new(
                stream,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                64 * 1024,
                leaveOpen: false))
            {
                await foreach (GeneratedOutput output in outputs
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    string line = JsonSerializer.Serialize(output, ManifestJsonOptions);
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    (string Identity, string Assembly) key = (
                        output.GeneratorIdentity,
                        output.GeneratorAssembly);
                    if (!summaries.TryGetValue(key, out GeneratedOutputAccumulator? summary))
                    {
                        summary = new GeneratedOutputAccumulator(
                            output.GeneratorIdentity,
                            output.GeneratorAssembly,
                            GeneratedOutputPreviewLimit);
                        summaries.Add(key, summary);
                    }

                    summary.Add(output);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, manifestPath, overwrite: true);
            return summaries.Values
                .Select(summary => summary.ToSnapshot())
                .OrderBy(summary => summary.Identity, StringComparer.Ordinal)
                .ThenBy(summary => summary.Assembly, StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public async IAsyncEnumerable<GeneratedOutput> StreamGeneratedOutputsAsync(
        Guid id,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetRunDirectory(id);
        if (!Directory.Exists(directory))
        {
            throw new YaapException(YaapErrors.HistoryFailed($"Run does not exist: {id:D}"));
        }

        string path = Path.Combine(directory, GeneratedOutputsManifestName);
        if (!File.Exists(path))
        {
            yield break;
        }

        FileStream stream;
        try
        {
            stream = OpenManifestSequential(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
        }

        await using (stream.ConfigureAwait(false))
        using (StreamReader reader = new(stream))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
                }

                if (line is null)
                {
                    yield break;
                }

                GeneratedOutput? output;
                try
                {
                    output = JsonSerializer.Deserialize<GeneratedOutput>(line, ManifestJsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
                }

                if (output is null)
                {
                    throw new YaapException(YaapErrors.HistoryFailed(
                        $"Generated-output manifest contains an empty record for run {id:D}."));
                }

                yield return output;
            }
        }
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(
        HistoryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        QueueTombstoneCleanup();
        query ??= new HistoryQuery();
        if (query.Limit is <= 0)
        {
            throw new YaapException(YaapErrors.InvalidInput("History limit must be greater than zero."));
        }

        string runsPath = Path.Combine(RootPath, "runs");
        if (!Directory.Exists(runsPath))
        {
            return Array.Empty<RunSummary>();
        }

        List<RunSummary>? summaries = query.Limit is null ? new List<RunSummary>() : null;
        PriorityQueue<RunSummary, long>? newest = query.Limit is not null
            ? new PriorityQueue<RunSummary, long>()
            : null;
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
                    if (newest is null)
                    {
                        summaries!.Add(summary);
                    }
                    else
                    {
                        newest.Enqueue(summary, summary.StartedAt.UtcDateTime.Ticks);
                        if (newest.Count > query.Limit!.Value)
                        {
                            _ = newest.Dequeue();
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // A corrupt run is isolated. Loading that id reports the actionable error.
            }
        }

        IEnumerable<RunSummary> ordered = newest is null
            ? summaries!.OrderByDescending(summary => summary.StartedAt)
            : newest.UnorderedItems
                .Select(item => item.Element)
                .OrderByDescending(summary => summary.StartedAt);

        return ordered.ToArray();
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            bool deleted = await DeleteRunDirectoryAsync(
                id,
                cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                throw new YaapException(YaapErrors.HistoryFailed(
                    $"Run {id:D} is active and cannot be deleted."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YaapException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
        }
    }

    public async Task UpdateLabelAsync(
        Guid id,
        string? label,
        CancellationToken cancellationToken = default)
    {
        string? normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (normalized?.Length > MaximumLabelLength)
        {
            throw new YaapException(YaapErrors.InvalidInput(
                $"History label must be {MaximumLabelLength} characters or fewer."));
        }

        string path = Path.Combine(GetRunDirectory(id), "summary.json");
        if (!File.Exists(path))
        {
            throw new YaapException(YaapErrors.HistoryFailed($"Run does not exist: {id:D}"));
        }

        try
        {
            using IDisposable lease = AcquireRunLease(id, cancellationToken);
            RunSummary? summary;
            await using (FileStream stream = OpenSequential(path))
            {
                summary = await JsonSerializer.DeserializeAsync<RunSummary>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            if (summary is null || summary.Id != id)
            {
                throw new JsonException($"History summary is invalid: {id:D}");
            }

            await WriteAtomicallyAsync(
                path,
                summary with { Label = normalized },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YaapException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
        }
    }

    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        string runsPath = Path.Combine(RootPath, "runs");
        if (!Directory.Exists(runsPath))
        {
            return 0;
        }

        int deleted = 0;
        foreach (string directory in Directory.EnumerateDirectories(runsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileName(directory), out Guid id))
            {
                continue;
            }

            if (!await DeleteRunDirectoryAsync(id, cancellationToken).ConfigureAwait(false))
            {
                throw new YaapException(YaapErrors.HistoryFailed(
                    $"Run {id:D} is active and cannot be deleted."));
            }

            deleted++;
        }

        return deleted;
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
        int retainedCompletedRuns = 0;
        foreach (RunSummary summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunActive(summary.Id))
            {
                continue;
            }

            if (retainedCompletedRuns++ < retainCount)
            {
                continue;
            }

            await DeleteRunDirectoryAsync(
                summary.Id,
                cancellationToken).ConfigureAwait(false);
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

    private static FileStream OpenManifestSequential(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
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
                (summary.Label?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                summary.Id.ToString("D").Contains(query.Search, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private async Task<bool> DeleteRunDirectoryAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetRunDirectory(id);
        if (!Directory.Exists(directory))
        {
            return true;
        }

        try
        {
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileStream? deletionLease = TryAcquireDeletionLease(id);
                    if (deletionLease is null)
                    {
                        return false;
                    }

                    string tombstonesPath = Path.Combine(RootPath, TombstonesDirectoryName);
                    Directory.CreateDirectory(tombstonesPath);
                    string tombstonePath = Path.Combine(
                        tombstonesPath,
                        $"{id:D}.{Guid.NewGuid():N}.deleted");
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Directory.Move(directory, tombstonePath);
                    }
                    finally
                    {
                        deletionLease.Dispose();
                    }

                    TryDeleteRunLeaseFile(id);
                    QueueTombstoneCleanup();
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new YaapException(YaapErrors.HistoryFailed(exception.Message), exception);
        }
    }

    private FileStream? TryAcquireDeletionLease(Guid id)
    {
        string path = GetRunLeasePath(id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool IsRunActive(Guid id)
    {
        using FileStream? lease = TryAcquireDeletionLease(id);
        return lease is null;
    }

    private string GetRunLeasePath(Guid id) => Path.Combine(
        RootPath,
        RunLeasesDirectoryName,
        $"{id:D}.lock");

    private void TryDeleteRunLeaseFile(Guid id)
    {
        try
        {
            File.Delete(GetRunLeasePath(id));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryCooperatively(string rootPath, CancellationToken cancellationToken)
    {
        Stack<(string Path, bool Delete)> pending = new();
        pending.Push((rootPath, false));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string path, bool delete) = pending.Pop();
            if (delete)
            {
                Directory.Delete(path, recursive: false);
                continue;
            }

            pending.Push((path, true));
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        pending.Push((entry, false));
                    }
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }
    }

    private void QueueTombstoneCleanup()
    {
        string tombstonesPath = Path.Combine(RootPath, TombstonesDirectoryName);
        if (!Directory.Exists(tombstonesPath))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                foreach (string tombstone in Directory.EnumerateDirectories(
                             tombstonesPath,
                             "*.deleted",
                             SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        DeleteDirectoryCooperatively(tombstone, CancellationToken.None);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    private static void TryDeleteFile(string path)
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

    private sealed class RunLease : IDisposable
    {
        private FileStream? _stream;

        public RunLease(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            FileStream? stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
            {
                return;
            }

            stream.Dispose();
        }
    }
}

internal sealed record GeneratorOutputSnapshot(
    string Identity,
    string Assembly,
    int FileCount,
    long ByteCount,
    long LineCount,
    IReadOnlyList<GeneratedOutput> Preview,
    bool IsTruncated);

internal sealed class GeneratedOutputAccumulator
{
    private static readonly IComparer<GeneratedOutput> OutputComparer =
        Comparer<GeneratedOutput>.Create((left, right) =>
        {
            int path = string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal);
            if (path != 0)
            {
                return path;
            }

            int bytes = left.ByteCount.CompareTo(right.ByteCount);
            return bytes != 0 ? bytes : left.LineCount.CompareTo(right.LineCount);
        });

    private readonly int _previewLimit;
    private readonly SortedSet<GeneratedOutput> _preview = new(OutputComparer);

    public GeneratedOutputAccumulator(string identity, string assembly, int previewLimit)
    {
        Identity = identity;
        Assembly = assembly;
        _previewLimit = previewLimit;
    }

    public string Identity { get; }

    public string Assembly { get; }

    public int FileCount { get; private set; }

    public long ByteCount { get; private set; }

    public long LineCount { get; private set; }

    public void Add(GeneratedOutput output)
    {
        FileCount = checked(FileCount + 1);
        ByteCount = checked(ByteCount + output.ByteCount);
        LineCount = checked(LineCount + output.LineCount);
        _preview.Add(output);
        if (_preview.Count > _previewLimit)
        {
            _preview.Remove(_preview.Max!);
        }
    }

    public GeneratorOutputSnapshot ToSnapshot()
    {
        return new GeneratorOutputSnapshot(
            Identity,
            Assembly,
            FileCount,
            ByteCount,
            LineCount,
            _preview.ToArray(),
            FileCount > _preview.Count);
    }
}
