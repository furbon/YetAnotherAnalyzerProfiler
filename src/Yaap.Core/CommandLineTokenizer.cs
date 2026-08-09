namespace Yaap.Core;

public sealed record CompilerCommand(
    string FileName,
    IReadOnlyList<string> HostArguments,
    string CompilerArguments);

public static class CommandLineTokenizer
{
    public static CompilerCommand ParseCompilerCommand(string commandLine)
    {
        ArgumentToken executable = ReadExecutable(commandLine);
        if (string.IsNullOrWhiteSpace(executable.Value))
        {
            throw new FormatException("The compiler command line does not contain an executable.");
        }

        int compilerArgumentsStart = SkipWhitespace(commandLine, executable.End);
        List<string> hostArguments = new();
        if (Path.GetFileNameWithoutExtension(executable.Value)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            int position = compilerArgumentsStart;
            while (position < commandLine.Length)
            {
                ArgumentToken token = ReadToken(commandLine, position);
                if (token.Value.Length == 0)
                {
                    break;
                }

                hostArguments.Add(token.Value);
                position = SkipWhitespace(commandLine, token.End);
                if (Path.GetFileName(token.Value).Equals("csc.dll", StringComparison.OrdinalIgnoreCase))
                {
                    compilerArgumentsStart = position;
                    break;
                }
            }

            if (hostArguments.Count == 0 ||
                !Path.GetFileName(hostArguments[^1]).Equals("csc.dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("A dotnet-hosted csc.dll command was expected.");
            }
        }

        return new CompilerCommand(
            executable.Value,
            hostArguments,
            commandLine[compilerArgumentsStart..]);
    }

    private static ArgumentToken ReadExecutable(string commandLine)
    {
        int start = SkipWhitespace(commandLine, 0);
        if (start < commandLine.Length && commandLine[start] != '"')
        {
            int executableEnd = commandLine.IndexOf(".exe", start, StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                executableEnd += ".exe".Length;
                if (executableEnd == commandLine.Length || char.IsWhiteSpace(commandLine[executableEnd]))
                {
                    return new ArgumentToken(commandLine[start..executableEnd], executableEnd);
                }
            }
        }

        return ReadToken(commandLine, start);
    }

    private static ArgumentToken ReadToken(string commandLine, int start)
    {
        int index = SkipWhitespace(commandLine, start);
        if (index >= commandLine.Length)
        {
            return new ArgumentToken(string.Empty, commandLine.Length);
        }

        System.Text.StringBuilder value = new();
        bool quoted = false;
        while (index < commandLine.Length)
        {
            int backslashes = 0;
            while (index < commandLine.Length && commandLine[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index < commandLine.Length && commandLine[index] == '"')
            {
                value.Append('\\', backslashes / 2);
                if (backslashes % 2 == 0)
                {
                    quoted = !quoted;
                }
                else
                {
                    value.Append('"');
                }

                index++;
                continue;
            }

            value.Append('\\', backslashes);
            if (index >= commandLine.Length || (!quoted && char.IsWhiteSpace(commandLine[index])))
            {
                break;
            }

            value.Append(commandLine[index]);
            index++;
        }

        return new ArgumentToken(value.ToString(), index);
    }

    private static int SkipWhitespace(string value, int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return index;
    }

    private readonly record struct ArgumentToken(string Value, int End);
}
