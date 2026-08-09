using System.Runtime.InteropServices;

namespace Yaap.Core;

public sealed class EnvironmentDetector
{
    private readonly IProcessRunner _processRunner;

    public EnvironmentDetector(IProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public async Task<EnvironmentSnapshot> CaptureAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        string sdk = await ReadFirstLineAsync(
            "dotnet",
            new[] { "--version" },
            targetDirectory,
            cancellationToken).ConfigureAwait(false) ?? "unknown";

        string? commit = await ReadFirstLineAsync(
            "git",
            new[] { "rev-parse", "HEAD" },
            targetDirectory,
            cancellationToken).ConfigureAwait(false);
        string? branch = commit is null
            ? null
            : await ReadFirstLineAsync(
                "git",
                new[] { "branch", "--show-current" },
                targetDirectory,
                cancellationToken).ConfigureAwait(false);
        bool dirty = false;
        if (commit is not null)
        {
            string? status = await ReadFirstLineAsync(
                "git",
                new[] { "status", "--porcelain" },
                targetDirectory,
                cancellationToken).ConfigureAwait(false);
            dirty = !string.IsNullOrWhiteSpace(status);
        }

        return new EnvironmentSnapshot(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription,
            sdk,
            commit,
            branch,
            dirty);
    }

    private async Task<string?> ReadFirstLineAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await _processRunner.RunAsync(
                new ProcessInvocation(fileName, arguments, workingDirectory),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0
                ? result.StandardOutputTail.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim()
                : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
