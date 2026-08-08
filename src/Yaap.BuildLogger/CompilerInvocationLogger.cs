using System.Text;
using Microsoft.Build.Framework;

namespace Yaap.BuildLogger;

public sealed class CompilerInvocationLogger : ILogger
{
    public const string CapturePathEnvironmentVariable = "YAAP_COMPILER_CAPTURE_PATH";

    private readonly object _sync = new();
    private StreamWriter? _writer;

    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Minimal;

    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        string? path = Environment.GetEnvironmentVariable(CapturePathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LoggerException(
                $"{CapturePathEnvironmentVariable} is required by the YAAP compiler logger.");
        }

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            64 * 1024);
        _writer.WriteLine(CompilerCaptureProtocol.Header);
        eventSource.AnyEventRaised += OnAnyEventRaised;
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void OnAnyEventRaised(object sender, BuildEventArgs eventArgs)
    {
        if (eventArgs is not TaskCommandLineEventArgs command ||
            !command.TaskName.Equals("Csc", StringComparison.OrdinalIgnoreCase) ||
            command.CommandLine.IndexOf("reportanalyzer", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        string workingDirectory = GetWorkingDirectory(command.ProjectFile);
        lock (_sync)
        {
            if (_writer is null)
            {
                return;
            }

            _writer.Write(CompilerCaptureProtocol.CommandRecord);
            _writer.Write('\t');
            _writer.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(command.CommandLine)));
            _writer.Write('\t');
            _writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory)));
        }
    }

    private static string GetWorkingDirectory(string? projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile))
        {
            return Environment.CurrentDirectory;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? Environment.CurrentDirectory;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Environment.CurrentDirectory;
        }
    }

    private static class CompilerCaptureProtocol
    {
        public const string Header = "YAAP-COMPILER-CAPTURE\t1";
        public const char CommandRecord = 'C';
    }
}
