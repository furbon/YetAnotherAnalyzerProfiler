using System.Xml;

namespace Yaap.Core;

public sealed record TargetInfo(
    string FullPath,
    string Extension,
    IReadOnlyList<string> Configurations,
    IReadOnlyList<string> TargetFrameworks);

public static class TargetDiscovery
{
    private static readonly HashSet<string> SupportedExtensions = new(
        new[] { ".sln", ".slnx", ".csproj" },
        StringComparer.OrdinalIgnoreCase);

    public static async Task<TargetInfo> DiscoverAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new YaapException(YaapErrors.InvalidInput("Target path is empty."));
        }

        string fullPath = Path.GetFullPath(path);
        string extension = Path.GetExtension(fullPath);
        if (!File.Exists(fullPath) || !SupportedExtensions.Contains(extension))
        {
            throw new YaapException(YaapErrors.InvalidInput(fullPath));
        }

        return extension.ToLowerInvariant() switch
        {
            ".sln" => await DiscoverSolutionAsync(fullPath, cancellationToken).ConfigureAwait(false),
            ".slnx" => await DiscoverSolutionXmlAsync(fullPath, cancellationToken).ConfigureAwait(false),
            _ => await DiscoverProjectAsync(fullPath, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async Task<TargetInfo> DiscoverSolutionAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        HashSet<string> configurations = new(StringComparer.OrdinalIgnoreCase);
        List<string> projects = new();
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.Contains("|Any CPU =", StringComparison.OrdinalIgnoreCase))
            {
                System.Text.RegularExpressions.Match projectMatch =
                    System.Text.RegularExpressions.Regex.Match(
                        line,
                        @"= ""[^""]+"", ""(?<path>[^""]+\.csproj)""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                if (projectMatch.Success)
                {
                    projects.Add(Path.GetFullPath(
                        NormalizeRelativePath(projectMatch.Groups["path"].Value),
                        Path.GetDirectoryName(fullPath)!));
                }

                continue;
            }

            string value = line.Trim().Split('|')[0];
            if (!string.IsNullOrWhiteSpace(value))
            {
                configurations.Add(value);
            }
        }

        return new TargetInfo(
            fullPath,
            ".sln",
            configurations.Count == 0 ? new[] { "Release", "Debug" } : configurations.ToArray(),
            await DiscoverProjectFrameworksAsync(projects, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<TargetInfo> DiscoverSolutionXmlAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        List<string> configurations = new();
        List<string> projects = new();
        XmlReaderSettings settings = new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
        };
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using XmlReader reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element &&
                reader.LocalName.Equals("BuildType", StringComparison.OrdinalIgnoreCase))
            {
                string? name = reader.GetAttribute("Name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    configurations.Add(name);
                }
            }

            if (reader.NodeType == XmlNodeType.Element &&
                reader.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase) &&
                reader.GetAttribute("Path") is { Length: > 0 } projectPath)
            {
                projects.Add(Path.GetFullPath(
                    NormalizeRelativePath(projectPath),
                    Path.GetDirectoryName(fullPath)!));
            }
        }

        return new TargetInfo(
            fullPath,
            ".slnx",
            configurations.Count == 0
                ? new[] { "Release", "Debug" }
                : configurations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            await DiscoverProjectFrameworksAsync(projects, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<TargetInfo> DiscoverProjectAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        List<string> configurations = new();
        List<string> frameworks = new();
        XmlReaderSettings settings = new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
        };
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using XmlReader reader = XmlReader.Create(stream, settings);
        bool advance = true;
        while (!reader.EOF)
        {
            if (advance && !await reader.ReadAsync().ConfigureAwait(false))
            {
                break;
            }

            advance = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName is "Configurations" or "TargetFramework" or "TargetFrameworks")
            {
                string element = reader.LocalName;
                string value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                advance = false;
                string[] items = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (element == "Configurations")
                {
                    configurations.AddRange(items);
                }
                else
                {
                    frameworks.AddRange(items);
                }
            }
        }

        return new TargetInfo(
            fullPath,
            ".csproj",
            configurations.Count == 0
                ? new[] { "Release", "Debug" }
                : configurations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            frameworks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<IReadOnlyList<string>> DiscoverProjectFrameworksAsync(
        IEnumerable<string> projectPaths,
        CancellationToken cancellationToken)
    {
        HashSet<string> frameworks = new(StringComparer.OrdinalIgnoreCase);
        foreach (string projectPath in projectPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(projectPath))
            {
                continue;
            }

            TargetInfo project = await DiscoverProjectAsync(projectPath, cancellationToken).ConfigureAwait(false);
            frameworks.UnionWith(project.TargetFrameworks);
        }

        return frameworks.ToArray();
    }

    private static string NormalizeRelativePath(string path)
    {
        return path
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
    }
}
