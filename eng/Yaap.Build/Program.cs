using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            NormalizePackageLockFiles(root);
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
            EnsureSdkVersion(root, framework);
            await PublishAsync(root, framework, runtime ?? throw new InvalidOperationException(
                "publish requires --runtime <RID>."));
            break;
        case "verify":
            EnsureSdkVersion(root, framework);
            CheckRepository(root);
            await RestoreAsync(root, framework);
            NormalizePackageLockFiles(root);
            await FormatAsync(root);
            await BuildAsync(root, framework);
            await TestAsync(root, framework);
            NormalizePackageLockFiles(root);
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
        if (supportsAllTargets || project.Framework.Equals("netstandard2.0", StringComparison.OrdinalIgnoreCase))
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
    NormalizePackageLockFiles(root);
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
        await BuildAndRunGuiTestsAsync(root, $"{framework}-windows");
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

    if (GetSdkMajor(root) >= 10 && framework.Equals("net10.0", StringComparison.OrdinalIgnoreCase))
    {
        string tests = Path.Combine(root, "tests", "Yaap.Tests", "Yaap.Tests.csproj");
        await RunAsync(root, "dotnet", new[]
        {
            "build",
            tests,
            "--framework",
            "net8.0",
            "--configuration",
            "Release",
            "--no-restore",
        });
        await RunAsync(
            root,
            "dotnet",
            new[]
            {
                "run",
                "--project",
                tests,
                "--framework",
                "net8.0",
                "--configuration",
                "Release",
                "--no-build",
                "--",
                "--group",
                "integration",
            },
            new Dictionary<string, string?> { ["YAAP_RUN_INTEGRATION"] = "1" });

        if (OperatingSystem.IsWindows())
        {
            await BuildAndRunGuiTestsAsync(root, "net8.0-windows");
        }
    }

    await TestLocalFeedAsync(root);
}

static async Task BuildAndRunGuiTestsAsync(string root, string framework)
{
    await RunAsync(root, "dotnet", new[]
    {
        "build",
        Path.Combine(root, "tests", "Yaap.Gui.Tests", "Yaap.Gui.Tests.csproj"),
        "--framework",
        framework,
        "--configuration",
        "Debug",
        "--no-restore",
    });
    await RunGuiTestsAsync(root, framework);
}

static Task RunGuiTestsAsync(string root, string framework)
{
    return RunAsync(root, "dotnet", new[]
    {
        "run",
        "--project",
        Path.Combine(root, "tests", "Yaap.Gui.Tests", "Yaap.Gui.Tests.csproj"),
        "--framework",
        framework,
        "--configuration",
        "Debug",
        "--no-build",
    });
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
    await PublishDistributionAsync(root, framework, runtime, outputRoot, runSmokes: true);

    string reproducibilityRoot = Path.Combine(
        Path.GetTempPath(),
        $"yaap-publish-reproducibility-{Guid.NewGuid():N}");
    try
    {
        await PublishDistributionAsync(root, framework, runtime, reproducibilityRoot, runSmokes: false);
        EnsureDirectoriesHaveEqualHashes(outputRoot, reproducibilityRoot);
    }
    finally
    {
        if (Directory.Exists(reproducibilityRoot))
        {
            Directory.Delete(reproducibilityRoot, recursive: true);
        }
    }
}

static async Task PublishDistributionAsync(
    string root,
    string framework,
    string runtime,
    string outputRoot,
    bool runSmokes)
{
    if (Directory.Exists(outputRoot))
    {
        Directory.Delete(outputRoot, recursive: true);
    }

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

    EnsureBuildLoggerPublished(Path.Combine(outputRoot, "cli"));
    CopyReleaseDocuments(root, Path.Combine(outputRoot, "cli"));
    CopyRuntimePackNotices(
        Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"),
        framework,
        runtime,
        Path.Combine(outputRoot, "cli"),
        includeWindowsDesktopLicense: false,
        expectedVersion: GetRuntimePackVersion(root, framework));
    EnsurePublishedContents(
        Path.Combine(outputRoot, "cli"),
        executableName,
        "Yaap.BuildLogger.dll",
        "Yaap.Core.xml",
        "LICENSE",
        "README.md",
        "CHANGELOG.md",
        "THIRD-PARTY-NOTICES.txt",
        "CODE_OF_CONDUCT.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "SUPPORT.md",
        "docs",
        "DOTNET-RUNTIME-LICENSE.txt",
        "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt");
    if (runSmokes)
    {
        await SmokePublishedCliAsync(root, executable, runtime);
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

        EnsureBuildLoggerPublished(Path.Combine(outputRoot, "gui"));
        CopyReleaseDocuments(root, Path.Combine(outputRoot, "gui"));
        CopyRuntimePackNotices(
            Path.Combine(root, "src", "Yaap.Gui", "Yaap.Gui.csproj"),
            $"{framework}-windows",
            runtime,
            Path.Combine(outputRoot, "gui"),
            includeWindowsDesktopLicense: true,
            expectedVersion: GetRuntimePackVersion(root, framework));
        EnsurePublishedContents(
            Path.Combine(outputRoot, "gui"),
            "yaap-gui.exe",
            "Yaap.BuildLogger.dll",
            "Yaap.Core.xml",
            "LICENSE",
            "README.md",
            "CHANGELOG.md",
            "THIRD-PARTY-NOTICES.txt",
            "CODE_OF_CONDUCT.md",
            "CONTRIBUTING.md",
            "SECURITY.md",
            "SUPPORT.md",
            "docs",
            "DOTNET-RUNTIME-LICENSE.txt",
            "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt",
            "DOTNET-WINDOWSDESKTOP-LICENSE.txt");
        if (runSmokes)
        {
            await SmokePublishedGuiAsync(
                Path.Combine(outputRoot, "gui", "yaap-gui.exe"),
                runtime);
        }
    }

    EnsurePublishedContents(
        outputRoot,
        runtime.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
            ? new[] { "cli", "gui" }
            : new[] { "cli" });
    ValidateArtifactMarkdownLinks(outputRoot);
}

static void CopyReleaseDocuments(string root, string output)
{
    foreach (string name in new[]
    {
        "LICENSE",
        "README.md",
        "CHANGELOG.md",
        "THIRD-PARTY-NOTICES.txt",
        "CODE_OF_CONDUCT.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "SUPPORT.md",
    })
    {
        string source = Path.Combine(root, name);
        if (!File.Exists(source))
        {
            throw new InvalidOperationException($"Required release document was not found: {source}");
        }

        File.Copy(source, Path.Combine(output, name), overwrite: true);
    }

    string docsSource = Path.Combine(root, "docs");
    string docsOutput = Path.Combine(output, "docs");
    foreach (string source in Directory.EnumerateFiles(docsSource, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(docsSource, source);
        string destination = Path.Combine(docsOutput, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}

static void CopyRuntimePackNotices(
    string project,
    string framework,
    string runtime,
    string output,
    bool includeWindowsDesktopLicense,
    string expectedVersion)
{
    string assetsPath = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
    using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
    JsonElement frameworkAssets = assets.RootElement
        .GetProperty("project")
        .GetProperty("frameworks")
        .GetProperty(framework);
    string runtimePackage = $"Microsoft.NETCore.App.Runtime.{runtime}";
    EnsureDownloadDependencyVersion(frameworkAssets, runtimePackage, expectedVersion);
    string runtimePackageRoot = FindPackageRoot(assets.RootElement, runtimePackage, expectedVersion);
    CopyPackageFile(
        runtimePackageRoot,
        "LICENSE.TXT",
        Path.Combine(output, "DOTNET-RUNTIME-LICENSE.txt"));
    CopyPackageFile(
        runtimePackageRoot,
        "THIRD-PARTY-NOTICES.TXT",
        Path.Combine(output, "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt"));

    if (includeWindowsDesktopLicense)
    {
        string windowsDesktopPackage = $"Microsoft.WindowsDesktop.App.Runtime.{runtime}";
        EnsureDownloadDependencyVersion(frameworkAssets, windowsDesktopPackage, expectedVersion);
        string windowsDesktopRoot = FindPackageRoot(
            assets.RootElement,
            windowsDesktopPackage,
            expectedVersion);
        CopyPackageFile(
            windowsDesktopRoot,
            "LICENSE",
            Path.Combine(output, "DOTNET-WINDOWSDESKTOP-LICENSE.txt"));
    }
}

static void EnsureDownloadDependencyVersion(
    JsonElement frameworkAssets,
    string package,
    string expectedVersion)
{
    JsonElement dependency = frameworkAssets.GetProperty("downloadDependencies")
        .EnumerateArray()
        .SingleOrDefault(item => item.GetProperty("name").GetString()?.Equals(
            package,
            StringComparison.OrdinalIgnoreCase) == true);
    if (dependency.ValueKind == JsonValueKind.Undefined)
    {
        throw new InvalidOperationException($"Runtime pack was not resolved in project.assets.json: {package}");
    }

    string range = dependency.GetProperty("version").GetString() ?? string.Empty;
    string[] bounds = range.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries);
    if (bounds.Length != 2 ||
        !bounds[0].Equals(expectedVersion, StringComparison.Ordinal) ||
        !bounds[1].Equals(expectedVersion, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Runtime pack {package} must resolve exactly to {expectedVersion}, actual: {range}");
    }
}

static string FindPackageRoot(JsonElement assets, string package, string version)
{
    foreach (JsonProperty folder in assets.GetProperty("packageFolders").EnumerateObject())
    {
        string candidate = Path.Combine(folder.Name, package.ToLowerInvariant(), version.ToLowerInvariant());
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    throw new InvalidOperationException($"Resolved runtime pack was not found: {package} {version}");
}

static void CopyPackageFile(string packageRoot, string name, string destination)
{
    string? source = Directory.EnumerateFiles(packageRoot)
        .SingleOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
    if (source is null)
    {
        throw new InvalidOperationException($"Runtime pack file was not found: {packageRoot}/{name}");
    }

    File.Copy(source, destination, overwrite: true);
}

static void ValidateArtifactMarkdownLinks(string outputRoot)
{
    Regex markdownLink = new(@"!?\[[^\]]*\]\((?<target>[^)]+)\)", RegexOptions.CultureInvariant);
    foreach (string markdown in Directory.EnumerateFiles(outputRoot, "*.md", SearchOption.AllDirectories))
    {
        string content = File.ReadAllText(markdown);
        foreach (Match match in markdownLink.Matches(content))
        {
            string target = match.Groups["target"].Value.Trim().Trim('<', '>');
            bool external = Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));
            if (target.Length == 0 || target.StartsWith('#') || external)
            {
                continue;
            }

            string pathPart = target.Split('#', 2)[0];
            string resolved = Path.GetFullPath(
                Uri.UnescapeDataString(pathPart.Replace('/', Path.DirectorySeparatorChar)),
                Path.GetDirectoryName(markdown)!);
            string distributionRoot = Path.GetFullPath(outputRoot) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(distributionRoot, StringComparison.OrdinalIgnoreCase) ||
                (!File.Exists(resolved) && !Directory.Exists(resolved)))
            {
                throw new InvalidOperationException(
                    $"Artifact Markdown link is broken or escapes the distribution: {markdown} -> {target}");
            }
        }
    }
}

static void EnsureDirectoriesHaveEqualHashes(string expectedRoot, string actualRoot)
{
    IReadOnlyDictionary<string, string> expected = HashDirectory(expectedRoot);
    IReadOnlyDictionary<string, string> actual = HashDirectory(actualRoot);
    if (!expected.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .SequenceEqual(actual.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
    {
        string[] differences = expected.Keys.Concat(actual.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(path => !expected.TryGetValue(path, out string? expectedHash) ||
                !actual.TryGetValue(path, out string? actualHash) ||
                !expectedHash.Equals(actualHash, StringComparison.Ordinal))
            .Select(path =>
                $"{path}: {expected.GetValueOrDefault(path, "missing")} != " +
                actual.GetValueOrDefault(path, "missing"))
            .ToArray();
        throw new InvalidOperationException(
            $"Publish output is not reproducible: {string.Join(", ", differences)}.");
    }
}

static IReadOnlyDictionary<string, string> HashDirectory(string root)
{
    Dictionary<string, string> hashes = new(StringComparer.Ordinal);
    foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        using FileStream stream = File.OpenRead(path);
        hashes.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
    }

    return hashes;
}

static void EnsurePublishedContents(string output, params string[] expectedNames)
{
    string[] actualNames = Directory.EnumerateFileSystemEntries(output)
        .Select(Path.GetFileName)
        .Order(StringComparer.Ordinal)
        .ToArray()!;
    string[] expected = expectedNames.Order(StringComparer.Ordinal).ToArray();
    if (!actualNames.SequenceEqual(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"Unexpected publish contents in {output}. " +
            $"Expected: {string.Join(", ", expected)}. Actual: {string.Join(", ", actualNames)}.");
    }
}

static async Task SmokePublishedCliAsync(string root, string executable, string runtime)
{
    if (!CanRunPublishedRuntime(runtime))
    {
        Console.WriteLine($"Published CLI smoke was skipped because {runtime} does not match the current host.");
        return;
    }

    await RunAsync(root, executable, new[] { "version" });
    await RunAsync(root, executable, new[] { "help" });
}

static async Task SmokePublishedGuiAsync(string executable, string runtime)
{
    if (!OperatingSystem.IsWindows() || !CanRunPublishedRuntime(runtime))
    {
        Console.WriteLine($"Published GUI smoke was skipped because {runtime} does not match the current host.");
        return;
    }

    ProcessStartInfo start = new(executable)
    {
        WorkingDirectory = Path.GetDirectoryName(executable)!,
        UseShellExecute = false,
    };
    using Process process = Process.Start(start) ?? throw new InvalidOperationException(
        $"Could not start published GUI: {executable}");
    try
    {
        bool inputIdle = await Task.Run(() => process.WaitForInputIdle(15_000));
        if (!inputIdle)
        {
            throw new InvalidOperationException("Published GUI did not reach an idle STA window.");
        }

        Stopwatch timeout = Stopwatch.StartNew();
        while (process.MainWindowHandle == IntPtr.Zero && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(100);
            process.Refresh();
        }

        if (process.MainWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Published GUI did not create its main window.");
        }

        if (!process.CloseMainWindow())
        {
            throw new InvalidOperationException("Published GUI main window could not be closed.");
        }

        using CancellationTokenSource exitTimeout = new(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(exitTimeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Published GUI exited with code {process.ExitCode}.");
        }
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}

static bool CanRunPublishedRuntime(string runtime)
{
    bool matchingOperatingSystem =
        (OperatingSystem.IsWindows() && runtime.StartsWith("win-", StringComparison.OrdinalIgnoreCase)) ||
        (OperatingSystem.IsLinux() && runtime.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)) ||
        (OperatingSystem.IsMacOS() && runtime.StartsWith("osx-", StringComparison.OrdinalIgnoreCase));
    if (!matchingOperatingSystem)
    {
        return false;
    }

    return (RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            runtime.EndsWith("-x64", StringComparison.OrdinalIgnoreCase)) ||
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 &&
         runtime.EndsWith("-arm64", StringComparison.OrdinalIgnoreCase));
}

static void EnsureBuildLoggerPublished(string output)
{
    string logger = Path.Combine(output, "Yaap.BuildLogger.dll");
    if (!File.Exists(logger))
    {
        throw new InvalidOperationException($"SDK-hosted build logger was not published beside YAAP: {logger}");
    }
}

static Task PublishProjectAsync(
    string root,
    string project,
    string framework,
    string runtime,
    string output)
{
    return PublishProjectCoreAsync(root, project, framework, runtime, output);
}

static async Task PublishProjectCoreAsync(
    string root,
    string project,
    string framework,
    string runtime,
    string output)
{
    // Validate all project dependencies against the tracked, RID-independent lock first.
    // Self-contained runtime packs are SDK/RID-specific, so the following restore uses a
    // disposable lock under obj rather than mutating or weakening the tracked lock file.
    await RunAsync(root, "dotnet", new[]
    {
        "restore",
        project,
        "--locked-mode",
    });
    await RunAsync(root, "dotnet", new[]
    {
        "restore",
        project,
        "--runtime",
        runtime,
        "-p:RestoreLockedMode=false",
        "-p:RestorePackagesWithLockFile=true",
        $"-p:NuGetLockFilePath=obj/publish.{runtime}.packages.lock.json",
        "-p:RestoreForceEvaluate=true",
    });

    if (Directory.Exists(output))
    {
        Directory.Delete(output, recursive: true);
    }

    await RunAsync(root, "dotnet", new[]
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
        "--no-restore",
        "-p:PublishSingleFile=true",
        "--output",
        output,
    });
}

static IReadOnlyList<ProjectTarget> Projects(string root, string framework)
{
    List<ProjectTarget> projects = new()
    {
        new(Path.Combine(root, "src", "Yaap.BuildLogger", "Yaap.BuildLogger.csproj"), "netstandard2.0"),
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
    EnsureAgentBranchPolicy(root);
    EnsureAgentBranchGuardrailFiles(root);
    EnsureToolchainManifest(root);

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

    EnsureGuiStartupSmokeGuard(root);
    EnsureThirdPartyNoticeSync(root);

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
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes.AsSpan(hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException($"Text must contain valid UTF-8: {relative}", exception);
        }
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

static void EnsureAgentBranchPolicy(string root)
{
    string branch = GetGitOutput(root, "branch", "--show-current").Trim();
    string status = GetGitOutput(root, "status", "--porcelain=v1", "--untracked-files=all");
    if (!string.IsNullOrWhiteSpace(status) &&
        !branch.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Tracked worktree changes must be made on agent/*, not '{branch}'. " +
            "Create the work branch from the current develop/v... branch before editing.");
    }
}

static void EnsureAgentBranchGuardrailFiles(string root)
{
    string hookPath = Path.Combine(root, ".githooks", "pre-commit");
    string installerPath = Path.Combine(root, "eng", "install-git-hooks.ps1");
    if (!File.Exists(hookPath) || !File.Exists(installerPath))
    {
        throw new InvalidOperationException(
            "The tracked Git pre-commit guard and installer are required.");
    }

    string configuredHookPath;
    try
    {
        configuredHookPath = GetGitOutput(
            root,
            "config",
            "--local",
            "--get",
            "core.hooksPath").Trim();
    }
    catch (InvalidOperationException exception)
    {
        throw new InvalidOperationException(
            "core.hooksPath must be .githooks. Run ./eng/install-git-hooks.ps1.",
            exception);
    }

    if (!configuredHookPath.Equals(".githooks", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"core.hooksPath must be .githooks, actual: {configuredHookPath}. " +
            "Run ./eng/install-git-hooks.ps1.");
    }

    string hook = File.ReadAllText(hookPath);
    if (!hook.Contains("agent/*", StringComparison.Ordinal) ||
        !hook.Contains("develop/*", StringComparison.Ordinal) ||
        !hook.Contains("MERGE_HEAD", StringComparison.Ordinal) ||
        !hook.Contains("YAAP_ALLOW_MAIN_MERGE", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The Git pre-commit guard must protect agent, develop, and main workflows.");
    }

    string installer = File.ReadAllText(installerPath);
    if (!installer.Contains("core.hooksPath .githooks", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The Git hook installer must configure the tracked .githooks directory.");
    }
}

static void EnsureGuiStartupSmokeGuard(string root)
{
    string testProject = File.ReadAllText(Path.Combine(
        root,
        "tests",
        "Yaap.Gui.Tests",
        "Yaap.Gui.Tests.csproj"));
    if (!testProject.Contains(
            "<TargetFrameworks>net8.0-windows;net10.0-windows</TargetFrameworks>",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("GUI tests must target both net8.0-windows and net10.0-windows.");
    }

    string testSource = File.ReadAllText(Path.Combine(
        root,
        "tests",
        "Yaap.Gui.Tests",
        "Program.cs"));
    foreach (string contract in new[]
    {
        "(\"gui.window-startup-smoke\", WindowStartupSmokeAsync)",
        "window.Show();",
        "window.UpdateLayout();",
        "window.Close();",
    })
    {
        if (!testSource.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"GUI startup smoke contract is missing: {contract}");
        }
    }

    string buildSource = File.ReadAllText(Path.Combine(root, "eng", "Yaap.Build", "Program.cs"));
    if (!buildSource.Contains(
            "BuildAndRunGuiTestsAsync(root, \"net8.0-windows\")",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Default .NET 10 verification must also run .NET 8 GUI tests.");
    }
}

static void EnsureThirdPartyNoticeSync(string root)
{
    string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
    foreach (string lockFile in new[]
    {
        Path.Combine(root, "src", "Yaap.BuildLogger", "packages.lock.json"),
        Path.Combine(root, "src", "Yaap.Core", "packages.lock.json"),
        Path.Combine(root, "src", "Yaap.Cli", "packages.lock.json"),
        Path.Combine(root, "src", "Yaap.Gui", "packages.lock.json"),
    })
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockFile));
        foreach (JsonProperty framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (JsonProperty package in framework.Value.EnumerateObject())
            {
                JsonElement metadata = package.Value;
                if (metadata.TryGetProperty("type", out JsonElement type) &&
                    type.GetString()?.Equals("Project", StringComparison.OrdinalIgnoreCase) == true)
                {
                    continue;
                }

                if (!metadata.TryGetProperty("resolved", out JsonElement resolved))
                {
                    continue;
                }

                string version = resolved.GetString() ?? string.Empty;
                string? inventoryLine = notices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.StartsWith($"- {package.Name} ", StringComparison.Ordinal));
                if (inventoryLine is null || !inventoryLine.Contains(version, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"THIRD-PARTY-NOTICES.txt is missing {package.Name} {version} from {framework.Name}.");
                }
            }
        }
    }
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

static string GetGitOutput(string root, params string[] arguments)
{
    ProcessStartInfo start = new("git")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(start) ?? throw new InvalidOperationException("git could not start.");
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Trim()}");
    }

    return output;
}

static void NormalizePackageLockFiles(string root)
{
    UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: true);
    foreach (string relative in GetRepositoryFiles(root).Where(relative =>
                 Path.GetFileName(relative).Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)))
    {
        string path = Path.Combine(root, relative);
        if (!File.Exists(path))
        {
            continue;
        }

        string content = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(path, content, encoding);
    }
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
        new[] { "LICENSE", ".editorconfig", ".gitignore", ".gitattributes" }
            .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
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
    string version = GetDotnetSdkVersion(root);
    return int.TryParse(version.Split('.')[0], out int major)
        ? major
        : throw new InvalidOperationException("Could not determine the .NET SDK version.");
}

static void EnsureSdkVersion(string root, string framework)
{
    string expected = GetToolchainValue(root, "sdks", NormalizeToolchainFramework(framework));
    string actual = GetDotnetSdkVersion(root);
    if (!actual.Equals(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The {framework} lane requires .NET SDK {expected}, actual: {actual}.");
    }
}

static string GetRuntimePackVersion(string root, string framework)
{
    return GetToolchainValue(root, "runtimePacks", NormalizeToolchainFramework(framework));
}

static string GetToolchainValue(string root, string section, string framework)
{
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(root, "eng", "toolchain.json")));
    return manifest.RootElement.GetProperty(section).GetProperty(framework).GetString() ??
        throw new InvalidOperationException($"eng/toolchain.json has no {section} value for {framework}.");
}

static string NormalizeToolchainFramework(string framework)
{
    if (framework.StartsWith("net8.0", StringComparison.OrdinalIgnoreCase))
    {
        return "net8.0";
    }

    if (framework.StartsWith("net10.0", StringComparison.OrdinalIgnoreCase))
    {
        return "net10.0";
    }

    throw new InvalidOperationException($"Unsupported toolchain framework: {framework}");
}

static void EnsureToolchainManifest(string root)
{
    string sdk8 = GetToolchainValue(root, "sdks", "net8.0");
    string sdk10 = GetToolchainValue(root, "sdks", "net10.0");
    _ = GetToolchainValue(root, "runtimePacks", "net10.0");
    string image8 = GetToolchainValue(root, "linuxSdkImages", "net8.0");
    string image10 = GetToolchainValue(root, "linuxSdkImages", "net10.0");

    using JsonDocument global = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
    string globalSdk = global.RootElement.GetProperty("sdk").GetProperty("version").GetString() ?? string.Empty;
    if (!globalSdk.Equals(sdk8, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"global.json SDK must match the net8.0 toolchain manifest: {sdk8}.");
    }

    string github = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
    string gitlab = File.ReadAllText(Path.Combine(root, ".gitlab-ci.yml"));
    foreach (string required in new[] { sdk8, sdk10, image8, image10 })
    {
        if (!github.Contains(required, StringComparison.Ordinal) ||
            !gitlab.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"CI toolchains are out of sync with eng/toolchain.json: {required}");
        }
    }
}

static string GetDotnetSdkVersion(string root)
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
    return process.ExitCode == 0 && version.Length > 0
        ? version
        : throw new InvalidOperationException("Could not determine the .NET SDK version.");
}

internal sealed record ProjectTarget(string Path, string Framework);
