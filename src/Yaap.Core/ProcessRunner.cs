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
            static state =>
            {
                Process child = (Process)state!;
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            },
            process);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

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
