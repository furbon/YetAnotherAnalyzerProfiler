using System.Diagnostics;

namespace Yaap.Core;

public sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null);

public sealed record ProcessResult(
    int ExitCode,
    TimeSpan Elapsed,
    IReadOnlyList<string> StandardOutputTail,
    IReadOnlyList<string> StandardErrorTail)
{
    public string CombinedTail => string.Join(
        Environment.NewLine,
        StandardOutputTail.Concat(StandardErrorTail));
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        Action<string, bool>? onLine = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int TailCapacity = 200;
    private static readonly TimeSpan CancellationExitTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        Action<string, bool>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = invocation.FileName,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (invocation.Environment is not null)
        {
            foreach ((string key, string? value) in invocation.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using Process process = new() { StartInfo = startInfo };
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start process: {invocation.FileName}");
        }

        BoundedLineBuffer stdout = new(TailCapacity);
        BoundedLineBuffer stderr = new(TailCapacity);
        Task stdoutTask = DrainAsync(process.StandardOutput, stdout, false, onLine);
        Task stderrTask = DrainAsync(process.StandardError, stderr, true, onLine);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => QueueKill((Process)state!),
            process);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WaitForCancellationExitAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);

            throw;
        }
        finally
        {
            stopwatch.Stop();
        }

        return new ProcessResult(
            process.ExitCode,
            stopwatch.Elapsed,
            stdout.Snapshot(),
            stderr.Snapshot());
    }

    private static async Task WaitForCancellationExitAsync(
        Process process,
        Task stdoutTask,
        Task stderrTask)
    {
        Task completion;
        try
        {
            completion = Task.WhenAll(
                process.WaitForExitAsync(CancellationToken.None),
                stdoutTask,
                stderrTask);
        }
        catch (InvalidOperationException)
        {
            ObserveFault(stdoutTask);
            ObserveFault(stderrTask);
            return;
        }

        if (await Task.WhenAny(
                completion,
                Task.Delay(CancellationExitTimeout)).ConfigureAwait(false) == completion)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch
            {
            }

            return;
        }

        QueueKill(process);
        ObserveFault(completion);
    }

    private static void QueueKill(Process process)
    {
        try
        {
            ThreadPool.QueueUserWorkItem(
                static state => TryKill((Process)state!),
                process,
                preferLocal: false);
        }
        catch
        {
            // A cancellation callback must never throw, including while the process is racing to exit.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The caller is already canceling; a failed best-effort retry must not mask it.
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedLineBuffer buffer,
        bool isError,
        Action<string, bool>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            buffer.Add(line);
            onLine?.Invoke(line, isError);
        }
    }

    private sealed class BoundedLineBuffer
    {
        private readonly int _capacity;
        private readonly Queue<string> _lines;
        private readonly object _sync = new();

        public BoundedLineBuffer(int capacity)
        {
            _capacity = capacity;
            _lines = new Queue<string>(capacity);
        }

        public void Add(string line)
        {
            lock (_sync)
            {
                if (_lines.Count == _capacity)
                {
                    _lines.Dequeue();
                }

                _lines.Enqueue(line);
            }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_sync)
            {
                return _lines.ToArray();
            }
        }
    }
}
