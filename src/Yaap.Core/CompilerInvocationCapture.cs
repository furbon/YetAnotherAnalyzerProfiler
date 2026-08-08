using System.Text;

namespace Yaap.Core;

public static class CompilerInvocationCapture
{
    public const string EnvironmentVariable = "YAAP_COMPILER_CAPTURE_PATH";
    public const string Header = "YAAP-COMPILER-CAPTURE\t1";

    public static async Task<int> ReadAsync(
        string path,
        Action<CompilerInvocation> invocationSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocationSink);
        await using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(header, Header, StringComparison.Ordinal))
        {
            throw new YaapException(YaapErrors.BinlogFailed(
                "The compiler capture header is missing or unsupported."));
        }

        int count = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] fields = line.Split('\t');
            if (fields.Length != 3 || !fields[0].Equals("C", StringComparison.Ordinal))
            {
                throw new YaapException(YaapErrors.BinlogFailed(
                    $"Invalid compiler capture record at line {count + 2}."));
            }

            try
            {
                string commandLine = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
                string capturedWorkingDirectory = Encoding.UTF8.GetString(Convert.FromBase64String(fields[2]));
                string workingDirectory = string.IsNullOrWhiteSpace(capturedWorkingDirectory)
                    ? Path.GetDirectoryName(Path.GetFullPath(path))!
                    : Path.GetFullPath(capturedWorkingDirectory);
                invocationSink(new CompilerInvocation(commandLine, workingDirectory));
                count++;
            }
            catch (Exception exception) when (
                exception is FormatException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new YaapException(YaapErrors.BinlogFailed(
                    $"Invalid compiler capture record at line {count + 2}: {exception.Message}"), exception);
            }
        }

        return count;
    }
}
