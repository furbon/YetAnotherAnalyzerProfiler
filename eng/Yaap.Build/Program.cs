using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

string root = FindRoot();
string task = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "verify";
string framework = GetOption(args, "--framework") ??
    (GetSdkMajor(root) >= 10 ? "net10.0" : "net8.0");
string? runtime = GetOption(args, "--runtime");

try
{
    switch (task.ToLowerInvariant())
    {
        case "check":
            CheckRepository(root);
            break;
        case "restore":
            await RestoreAsync(root, framework);
            break;
        case "build":
            await BuildAsync(root, framework);
            break;
        case "test":
            await TestAsync(root, framework);
            break;
        case "format":
            await FormatAsync(root);
            break;
        case "publish":
            await PublishAsync(root, framework, runtime ?? throw new InvalidOperationException(
                "publish requires --runtime <RID>."));
            break;
        case "verify":
            CheckRepository(root);
            await RestoreAsync(root, framework);
            await FormatAsync(root);
            await BuildAsync(root, framework);
            await TestAsync(root, framework);
            CheckRepository(root);
            break;
        default:
            throw new InvalidOperationException(
                "Task must be check, restore, format, build, test, publish, or verify.");
    }

    Console.WriteLine($"YAAP '{task}' completed for {framework}.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task RestoreAsync(string root, string framework)
{
    bool supportsAllTargets = GetSdkMajor(root) >= 10;
    foreach (ProjectTarget project in Projects(root, framework))
    {
        List<string> arguments = new()
        {
            "restore",
            project.Path,
        };
        if (supportsAllTargets)
        {
            arguments.Add("--locked-mode");
        }
        else
        {
            arguments.Add($"-p:TargetFrameworks={project.Framework}");
            arguments.Add("-p:RestorePackagesWithLockFile=true");
            arguments.Add("-p:RestoreLockedMode=false");
            arguments.Add("-p:NuGetLockFilePath=obj/sdk8.packages.lock.json");
            arguments.Add("--force-evaluate");
        }

        await RunAsync(root, "dotnet", arguments);
    }

    await RunAsync(root, "dotnet", new[]
    {
        "restore",
        Path.Combine(root, "tests", "assets", "Fixture.App", "Fixture.App.csproj"),
        "--locked-mode",
    });
    await RunAsync(root, "dotnet", new[]
    {
        "restore",
        Path.Combine(root, "tests", "assets", "Local.Package", "Local.Package.csproj"),
        "--locked-mode",
    });
}

static async Task BuildAsync(string root, string framework)
{
    foreach (ProjectTarget project in Projects(root, framework))
    {
        await RunAsync(root, "dotnet", new[]
        {
            "build",
            project.Path,
            "--framework",
            project.Framework,
            "--configuration",
            "Release",
            "--no-restore",
        });
    }
}

static async Task FormatAsync(string root)
{
    if (GetSdkMajor(root) < 10)
    {
        Console.WriteLine("dotnet format is covered by the .NET 10 validation lane.");
        return;
    }

    await RunAsync(root, "dotnet", new[]
    {
        "format",
        Path.Combine(root, "YetAnotherAnalyzerProfiler.slnx"),
        "--verify-no-changes",
        "--no-restore",
    });
}

static async Task TestAsync(string root, string framework)
{
    await RunAsync(root, "dotnet", new[]
    {
        "run",
        "--project",
        Path.Combine(root, "tests", "Yaap.Tests", "Yaap.Tests.csproj"),
        "--framework",
        framework,
        "--configuration",
        "Release",
        "--no-build",
    });

    if (OperatingSystem.IsWindows())
    {
        await RunAsync(root, "dotnet", new[]
        {
            "run",
            "--project",
            Path.Combine(root, "tests", "Yaap.Gui.Tests", "Yaap.Gui.Tests.csproj"),
            "--framework",
            $"{framework}-windows",
            "--configuration",
            "Release",
            "--no-build",
        });
    }

    await RunAsync(
        root,
        "dotnet",
        new[]
        {
            "run",
            "--project",
            Path.Combine(root, "tests", "Yaap.Tests", "Yaap.Tests.csproj"),
            "--framework",
            framework,
            "--configuration",
            "Release",
            "--no-build",
        },
        new Dictionary<string, string?> { ["YAAP_RUN_INTEGRATION"] = "1" });

    await TestLocalFeedAsync(root);
}

static async Task TestLocalFeedAsync(string root)
{
    string packageProject = Path.Combine(root, "tests", "assets", "Local.Package", "Local.Package.csproj");
    string consumerProject = Path.Combine(root, "tests", "local-feed", "Consumer", "Consumer.csproj");
    string feed = Path.Combine(root, "tests", "local-feed", "packages");
    Directory.CreateDirectory(feed);
    await RunAsync(root, "dotnet", new[] { "restore", packageProject, "--locked-mode" });
    await RunAsync(root, "dotnet", new[]
    {
        "pack",
        packageProject,
        "--configuration",
        "Release",
        "--no-restore",
        "--output",
        feed,
    });
    await RunAsync(root, "dotnet", new[]
    {
        "restore",
        consumerProject,
        "--locked-mode",
        "--no-cache",
    });
    await RunAsync(root, "dotnet", new[]
    {
        "build",
        consumerProject,
        "--configuration",
        "Release",
        "--no-restore",
    });
}

static async Task PublishAsync(string root, string framework, string runtime)
{
    string outputRoot = Path.Combine(root, "artifacts", "publish", runtime, framework);
    await PublishProjectAsync(
        root,
        Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"),
        framework,
        runtime,
        Path.Combine(outputRoot, "cli"));
    string executableName = runtime.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
        ? "yaap.exe"
        : "yaap";
    string executable = Path.Combine(outputRoot, "cli", executableName);
    if (!File.Exists(executable))
    {
        throw new InvalidOperationException($"Single-file CLI was not produced: {executable}");
    }

    if (runtime.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
    {
        await PublishProjectAsync(
            root,
            Path.Combine(root, "src", "Yaap.Gui", "Yaap.Gui.csproj"),
            $"{framework}-windows",
            runtime,
            Path.Combine(outputRoot, "gui"));
        if (!File.Exists(Path.Combine(outputRoot, "gui", "yaap-gui.exe")))
        {
            throw new InvalidOperationException("Single-file GUI was not produced.");
        }
    }
}

static Task PublishProjectAsync(
    string root,
    string project,
    string framework,
    string runtime,
    string output)
{
    return RunAsync(root, "dotnet", new[]
    {
        "publish",
        project,
        "--framework",
        framework,
        "--runtime",
        runtime,
        "--configuration",
        "Release",
        "--self-contained",
        "true",
        "-p:PublishSingleFile=true",
        "-p:RestoreLockedMode=false",
        "-p:RestorePackagesWithLockFile=true",
        $"-p:NuGetLockFilePath=obj/publish.{runtime}.packages.lock.json",
        "-p:RestoreForceEvaluate=true",
        "--output",
        output,
    });
}

static IReadOnlyList<ProjectTarget> Projects(string root, string framework)
{
    List<ProjectTarget> projects = new()
    {
    };
    if (OperatingSystem.IsWindows())
    {
        projects.Add(new(
            Path.Combine(root, "tests", "Yaap.Gui.Tests", "Yaap.Gui.Tests.csproj"),
            $"{framework}-windows"));
        projects.Add(new(
            Path.Combine(root, "src", "Yaap.Gui", "Yaap.Gui.csproj"),
            $"{framework}-windows"));
    }

    projects.Add(new(Path.Combine(root, "tests", "Yaap.Tests", "Yaap.Tests.csproj"), framework));
    projects.Add(new(Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"), framework));
    projects.Add(new(Path.Combine(root, "src", "Yaap.Core", "Yaap.Core.csproj"), framework));

    return projects;
}

static void CheckRepository(string root)
{
    string canonical = File.ReadAllText(Path.Combine(root, "eng", "agent-instructions.md"));
    foreach (string path in new[]
    {
        Path.Combine(root, "AGENTS.md"),
        Path.Combine(root, ".github", "copilot-instructions.md"),
    })
    {
        if (!string.Equals(canonical, File.ReadAllText(path), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Agent instructions are out of sync: {path}");
        }
    }

    string[] files = GetRepositoryFiles(root);
    foreach (string relative in files)
    {
        string path = Path.Combine(root, relative);
        if (!File.Exists(path) || !IsTextFile(relative))
        {
            continue;
        }

        byte[] bytes = File.ReadAllBytes(path);
        bool shell = relative.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
        string text = Encoding.UTF8.GetString(bytes.AsSpan(hasBom ? 3 : 0));
        if (shell)
        {
            if (hasBom || text.Contains("\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Shell script must be UTF-8 without BOM and LF: {relative}");
            }
        }
        else if (!hasBom || (text.Contains('\n') && !text.Contains("\r\n", StringComparison.Ordinal)) ||
                 text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'))
        {
            throw new InvalidOperationException($"Text must be UTF-8 BOM and CRLF: {relative}");
        }

        if (Regex.IsMatch(text, @"[A-Za-z]:\\(?:Users|Dev)\\", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(text, """(?i)(password|api[_-]?key|client[_-]?secret)\s*[:=]\s*['"]?[^\s'"]+"""))
        {
            throw new InvalidOperationException($"Possible local path or secret in {relative}.");
        }

        if (relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(text, @"<PackageReference\s+[^>]*Version=", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException($"Package version must be centralized: {relative}");
        }

        if (RequiresSpaceIndentation(relative) && Regex.IsMatch(text, "(?m)^\\t"))
        {
            throw new InvalidOperationException($"Leading indentation must use spaces: {relative}");
        }
    }

    int versionSources = files.Count(relative =>
        relative.Replace('\\', '/').Equals("eng/Version.props", StringComparison.OrdinalIgnoreCase));
    if (versionSources != 1)
    {
        throw new InvalidOperationException("eng/Version.props must be the single version source.");
    }

    string versionProps = File.ReadAllText(Path.Combine(root, "eng", "Version.props"));
    Match version = Regex.Match(versionProps, @"<VersionPrefix>(?<value>\d+\.\d+\.\d+)</VersionPrefix>");
    if (!version.Success ||
        !versionProps.Contains("<AssemblyVersion>$(VersionPrefix).0</AssemblyVersion>", StringComparison.Ordinal) ||
        !versionProps.Contains("<FileVersion>$(VersionPrefix).0</FileVersion>", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Version.props must define SemVer once and derive *.0 assembly/file versions.");
    }

    Console.WriteLine($"Repository guards passed for {files.Length} files.");
}

static string[] GetRepositoryFiles(string root)
{
    ProcessStartInfo start = new("git")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add("ls-files");
    start.ArgumentList.Add("--cached");
    start.ArgumentList.Add("--others");
    start.ArgumentList.Add("--exclude-standard");
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("git could not start.");
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException("git ls-files failed.");
    }

    return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static bool IsTextFile(string path)
{
    string extension = Path.GetExtension(path);
    return new[]
    {
        ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".xaml", ".xml",
        ".json", ".config", ".md", ".yml", ".yaml", ".ps1", ".sh", ".editorconfig", ".gitignore",
        ".gitattributes",
    }.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
}

static bool RequiresSpaceIndentation(string path)
{
    return new[]
    {
        ".cs", ".csproj", ".props", ".targets", ".xaml", ".xml", ".json", ".config",
        ".yml", ".yaml", ".ps1",
    }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

static async Task RunAsync(
    string root,
    string fileName,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string?>? environment = null)
{
    Console.WriteLine($"> {fileName} {string.Join(' ', arguments.Select(Quote))}");
    ProcessStartInfo start = new(fileName)
    {
        WorkingDirectory = root,
        UseShellExecute = false,
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    if (environment is not null)
    {
        foreach ((string key, string? value) in environment)
        {
            start.Environment[key] = value;
        }
    }

    using Process process = Process.Start(start) ?? throw new InvalidOperationException(
        $"Could not start {fileName}.");
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command failed with exit code {process.ExitCode}: {fileName}");
    }
}

static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

static string FindRoot()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "YetAnotherAnalyzerProfiler.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Repository root was not found.");
}

static string? GetOption(IReadOnlyList<string> arguments, string name)
{
    for (int index = 0; index < arguments.Count - 1; index++)
    {
        if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static int GetSdkMajor(string root)
{
    ProcessStartInfo start = new("dotnet", "--version")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("dotnet could not start.");
    string version = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return process.ExitCode == 0 && int.TryParse(version.Split('.')[0], out int major)
        ? major
        : throw new InvalidOperationException("Could not determine the .NET SDK version.");
}

internal sealed record ProjectTarget(string Path, string Framework);
