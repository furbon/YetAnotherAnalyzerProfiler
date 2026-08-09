using System.Diagnostics;
using System.IO.Compression;
using System.Net;
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
        case "visual":
            await CaptureGuiVisualMatrixAsync(root, GetOption(args, "--output"));
            break;
        case "publish":
            EnsureSdkVersion(root, framework);
            await PublishAsync(root, framework, runtime ?? throw new InvalidOperationException(
                "publish requires --runtime <RID>."));
            break;
        case "pack":
            EnsureSdkVersion(root, framework);
            await PackAsync(root);
            break;
        case "verify":
            {
                Dictionary<string, string> expectedPackageLocks = CapturePackageLockHashes(root);
                EnsureSdkVersion(root, framework);
                CheckRepository(root);
                await EnsurePackageLockRestoreDeterminismAsync(root, expectedPackageLocks, framework);
                await EnsurePackageLockDebugRebuildDeterminismAsync(root, expectedPackageLocks, framework);
                await EnsurePackageLockVisualStudioRebuildDeterminismAsync(root, expectedPackageLocks);
                await RestoreAsync(root, framework);
                NormalizePackageLockFiles(root);
                EnsurePackageLockHashes(root, expectedPackageLocks, "Locked restore");
                await FormatAsync(root);
                await BuildAsync(root, framework);
                await TestAsync(root, framework);
                if (framework.Equals("net10.0", StringComparison.OrdinalIgnoreCase))
                {
                    await PackAsync(root);
                }
                NormalizePackageLockFiles(root);
                EnsurePackageLockHashes(root, expectedPackageLocks, "Build, test, or pack");
                CheckRepository(root);
                break;
            }
        default:
            throw new InvalidOperationException(
                "Task must be check, restore, format, build, test, visual, pack, publish, or verify.");
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

static async Task EnsurePackageLockRestoreDeterminismAsync(
    string root,
    IReadOnlyDictionary<string, string> expectedHashes,
    string framework)
{
    // Non-Windows SDKs download WPF targeting packs that Windows SDKs provide locally,
    // so Windows owns forced regeneration of the WPF locks. Other hosts still force
    // regeneration of every cross-platform product and test project.
    bool supportsAllTargets = GetSdkMajor(root) >= 10;
    if (supportsAllTargets && OperatingSystem.IsWindows())
    {
        await RunAsync(root, "dotnet", new[]
        {
            "restore",
            Path.Combine(root, "YetAnotherAnalyzerProfiler.slnx"),
            "--force-evaluate",
            "-p:RestoreLockedMode=false",
        });
    }
    else
    {
        foreach (ProjectTarget project in Projects(root, framework))
        {
            List<string> arguments = new()
            {
                "restore",
                project.Path,
                "--force-evaluate",
                "-p:RestoreLockedMode=false",
            };
            if (!supportsAllTargets)
            {
                arguments.Add($"-p:TargetFrameworks={project.Framework}");
                arguments.Add("-p:RestorePackagesWithLockFile=true");
                arguments.Add("-p:NuGetLockFilePath=obj/sdk8.packages.lock.json");
            }

            await RunAsync(root, "dotnet", arguments);
        }
    }

    EnsureCliRestoreGraphIdentity(root);
    NormalizePackageLockFiles(root);
    EnsurePackageLockHashes(root, expectedHashes, "Forced restore");
}

static void EnsureCliRestoreGraphIdentity(string root)
{
    string graphPath = Path.Combine(
        root,
        "tests",
        "Yaap.Tests",
        "obj",
        "Yaap.Tests.csproj.nuget.dgspec.json");
    using JsonDocument graph = JsonDocument.Parse(File.ReadAllText(graphPath));
    string cliPath = Path.GetFullPath(Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"));
    JsonElement cliProject = graph.RootElement
        .GetProperty("projects")
        .EnumerateObject()
        .Single(project => Path.GetFullPath(project.Name).Equals(
            cliPath,
            StringComparison.OrdinalIgnoreCase))
        .Value;
    string? projectName = cliProject
        .GetProperty("restore")
        .GetProperty("projectName")
        .GetString();
    if (!"YetAnotherAnalyzerProfiler.Tool".Equals(projectName, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "CLI restore graph identity must match the evaluated PackageId " +
            $"YetAnotherAnalyzerProfiler.Tool, actual: {projectName ?? "<null>"}");
    }
}

static async Task EnsurePackageLockDebugRebuildDeterminismAsync(
    string root,
    IReadOnlyDictionary<string, string> expectedHashes,
    string framework)
{
    string artifactsPath = Path.Combine(
        Path.GetTempPath(),
        $"yaap-debug-rebuild-{Guid.NewGuid():N}");
    try
    {
        await RunAsync(root, "dotnet", new[]
        {
            "build",
            Path.Combine(root, "src", "Yaap.BuildLogger", "Yaap.BuildLogger.csproj"),
            "--framework",
            "netstandard2.0",
            "--configuration",
            "Debug",
            "--no-restore",
        });

        List<string> arguments = new()
        {
            "build",
            Path.Combine(root, "tests", "Yaap.Tests", "Yaap.Tests.csproj"),
            "--configuration",
            "Debug",
            "--no-incremental",
            "--force",
            "--artifacts-path",
            artifactsPath,
        };
        if (GetSdkMajor(root) < 10)
        {
            arguments.Add("--framework");
            arguments.Add(framework);
            arguments.Add($"-p:TargetFrameworks={framework}");
            arguments.Add("-p:RestorePackagesWithLockFile=true");
            arguments.Add("-p:RestoreLockedMode=false");
            arguments.Add("-p:NuGetLockFilePath=obj/sdk8.packages.lock.json");
        }

        await RunAsync(root, "dotnet", arguments);
        NormalizePackageLockFiles(root);
        EnsurePackageLockHashes(root, expectedHashes, "Debug rebuild with implicit restore");
    }
    finally
    {
        string resolvedArtifactsPath = Path.GetFullPath(artifactsPath);
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
            Path.DirectorySeparatorChar;
        if (!resolvedArtifactsPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to delete Debug rebuild path outside the temp directory: {resolvedArtifactsPath}");
        }

        if (Directory.Exists(resolvedArtifactsPath))
        {
            Directory.Delete(resolvedArtifactsPath, recursive: true);
        }
    }
}

static async Task EnsurePackageLockVisualStudioRebuildDeterminismAsync(
    string root,
    IReadOnlyDictionary<string, string> expectedHashes)
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    if (GetSdkMajor(root) < 10)
    {
        Console.WriteLine("Visual Studio rebuild lock check is covered by the .NET 10 validation lane.");
        return;
    }

    string? msbuild = FindVisualStudioMsBuild();
    if (msbuild is null)
    {
        Console.WriteLine("Visual Studio rebuild lock check skipped: Visual Studio MSBuild was not found.");
        return;
    }

    await RunAsync(root, msbuild, new[]
    {
        Path.Combine(root, "tests", "Yaap.Tests", "Yaap.Tests.csproj"),
        "-restore",
        "-target:Rebuild",
        "-property:Configuration=Debug",
        "-property:RestoreLockedMode=false",
        "-property:RestoreForceEvaluate=true",
        "-verbosity:minimal",
    });
    EnsureCliRestoreGraphIdentity(root);
    NormalizePackageLockFiles(root);
    EnsurePackageLockHashes(root, expectedHashes, "Visual Studio rebuild of the package-lock owner graph");
}

static string? FindVisualStudioMsBuild()
{
    string[] roots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    };
    foreach (string root in roots.Where(Directory.Exists))
    {
        string visualStudioRoot = Path.Combine(root, "Microsoft Visual Studio");
        if (!Directory.Exists(visualStudioRoot))
        {
            continue;
        }

        foreach (string versionDirectory in Directory
                     .EnumerateDirectories(visualStudioRoot)
                     .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (string editionDirectory in Directory
                         .EnumerateDirectories(versionDirectory)
                         .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(
                    editionDirectory,
                    "MSBuild",
                    "Current",
                    "Bin",
                    "MSBuild.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    return null;
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

    // WPF workspace loading requires Windows targeting packs. Windows verifies the
    // whole solution; other hosts verify every cross-platform workspace explicitly.
    string[] workspaces = OperatingSystem.IsWindows()
        ? new[] { Path.Combine(root, "YetAnotherAnalyzerProfiler.slnx") }
        : new[]
        {
            Path.Combine(root, "eng", "Yaap.Build", "Yaap.Build.csproj"),
            Path.Combine(root, "src", "Yaap.BuildLogger", "Yaap.BuildLogger.csproj"),
            Path.Combine(root, "src", "Yaap.Core", "Yaap.Core.csproj"),
            Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"),
            Path.Combine(root, "tests", "Yaap.Tests", "Yaap.Tests.csproj"),
            Path.Combine(root, "tests", "assets", "Fixture.Analyzers", "Fixture.Analyzers.csproj"),
            Path.Combine(root, "tests", "assets", "Fixture.App", "Fixture.App.csproj"),
            Path.Combine(root, "tests", "assets", "Local.Package", "Local.Package.csproj"),
        };
    foreach (string workspace in workspaces)
    {
        await RunAsync(root, "dotnet", new[]
        {
            "format",
            workspace,
            "--verify-no-changes",
            "--no-restore",
        });
    }

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
            new Dictionary<string, string?>
            {
                ["YAAP_RUN_INTEGRATION"] = "1",
                ["DOTNET_ROLL_FORWARD"] = "Major",
            });

        if (OperatingSystem.IsWindows())
        {
            await BuildAndRunGuiTestsAsync(root, "net8.0-windows");
        }
    }

    await TestLocalFeedAsync(root);
}

static async Task BuildAndRunGuiTestsAsync(
    string root,
    string framework,
    string? captureDirectory = null)
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
    await RunGuiTestsAsync(root, framework, captureDirectory);
}

static Task RunGuiTestsAsync(string root, string framework, string? captureDirectory = null)
{
    Dictionary<string, string?> environment = new();
    if (captureDirectory is not null)
    {
        environment["YAAP_GUI_CAPTURE_DIR"] = captureDirectory;
    }

    if (GetSdkMajor(root) >= 10 && framework.StartsWith("net8.0", StringComparison.OrdinalIgnoreCase))
    {
        environment["DOTNET_ROLL_FORWARD"] = "Major";
    }

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
    }, environment.Count == 0 ? null : environment);
}

static async Task CaptureGuiVisualMatrixAsync(string root, string? requestedOutput)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("GUI visual capture requires Windows and WPF.");
    }

    EnsureSdkVersion(root, "net10.0");
    string output = Path.GetFullPath(requestedOutput ??
        Path.Combine(root, "artifacts", "gui-visuals"));
    Directory.CreateDirectory(output);
    await RunAsync(root, "dotnet", new[]
    {
        "restore",
        Path.Combine(root, "tests", "Yaap.Gui.Tests", "Yaap.Gui.Tests.csproj"),
        "--locked-mode",
    });

    foreach (string framework in new[] { "net8.0-windows", "net10.0-windows" })
    {
        string frameworkOutput = Path.Combine(output, framework);
        Directory.CreateDirectory(frameworkOutput);
        DateTime captureStartedAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await BuildAndRunGuiTestsAsync(root, framework, frameworkOutput);
        ValidateVisualCaptureMatrix(frameworkOutput, captureStartedAtUtc);
        WriteVisualContactSheet(frameworkOutput, framework);
    }

    string rootIndex = "<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\">" +
        "<title>YAAP GUI visual matrix</title><body><h1>YAAP GUI visual matrix</h1>" +
        "<ul><li><a href=\"net8.0-windows/index.html\">.NET 8</a></li>" +
        "<li><a href=\"net10.0-windows/index.html\">.NET 10</a></li></ul></body></html>";
    File.WriteAllText(Path.Combine(output, "index.html"), rootIndex, new UTF8Encoding(false));
    Console.WriteLine($"GUI visual matrix: {Path.Combine(output, "index.html")}");
}

static void ValidateVisualCaptureMatrix(string directory, DateTime captureStartedAtUtc)
{
    List<string> expected = new();
    foreach (string theme in new[] { "light", "dark" })
    {
        expected.Add($"{theme}-target-toolbar-disabled.png");
        expected.Add($"{theme}-target-toolbar-enabled.png");
        for (int tab = 1; tab <= 7; tab++)
        {
            expected.Add($"{theme}-tab-{tab}.png");
            expected.Add($"{theme}-tab-{tab}-narrow.png");
        }

        expected.AddRange(new[]
        {
            $"{theme}-analyzer-table-selected.png",
            $"{theme}-analyzer-table-focused.png",
            $"{theme}-analyzer-table-narrow.png",
            $"{theme}-analyzer-table-context-menu.png",
            $"{theme}-analyzer-tree-selected.png",
            $"{theme}-analyzer-tree-focused.png",
            $"{theme}-analyzer-tree-narrow.png",
            $"{theme}-analyzer-tree-context-menu.png",
            $"{theme}-analyzer-table-empty.png",
            $"{theme}-analyzer-tree-empty.png",
            $"{theme}-generator-table-selected.png",
            $"{theme}-generator-table-focused.png",
            $"{theme}-generator-tree-selected.png",
            $"{theme}-generator-tree-focused.png",
            $"{theme}-generator-table-empty.png",
            $"{theme}-generator-tree-empty.png",
            $"{theme}-advanced-settings.png",
            $"{theme}-busy.png",
            $"{theme}-custom-configuration.png",
            $"{theme}-history-context-menu.png",
            $"{theme}-history-loading.png",
            $"{theme}-history-narrow.png",
            $"{theme}-partial-troubleshooting.png",
            $"{theme}-recent-targets.png",
        });
    }

    string[] missing = expected
        .Where(file =>
        {
            FileInfo capture = new(Path.Combine(directory, file));
            return !capture.Exists ||
                capture.Length == 0 ||
                capture.LastWriteTimeUtc < captureStartedAtUtc;
        })
        .ToArray();
    if (missing.Length > 0)
    {
        throw new InvalidOperationException(
            "GUI visual capture matrix is incomplete: " + string.Join(", ", missing));
    }
}

static void WriteVisualContactSheet(string directory, string framework)
{
    string[] images = Directory.GetFiles(directory, "*.png")
        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        .ToArray();
    StringBuilder html = new();
    html.Append("<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><title>")
        .Append(WebUtility.HtmlEncode($"YAAP GUI visual matrix {framework}"))
        .Append("</title><style>body{font-family:Segoe UI,sans-serif;background:#181818;color:#eee;margin:20px}")
        .Append("main{display:grid;grid-template-columns:repeat(auto-fit,minmax(440px,1fr));gap:16px}")
        .Append("figure{margin:0;padding:12px;background:#252525;border:1px solid #444;border-radius:8px}")
        .Append("img{display:block;width:100%;height:auto;background:#111}figcaption{margin-top:8px}</style><body><h1>")
        .Append(WebUtility.HtmlEncode($"YAAP GUI visual matrix — {framework}"))
        .Append("</h1><main>");
    foreach (string image in images)
    {
        string file = Path.GetFileName(image);
        html.Append("<figure><img loading=\"lazy\" src=\"")
            .Append(WebUtility.HtmlEncode(file))
            .Append("\" alt=\"")
            .Append(WebUtility.HtmlEncode(file))
            .Append("\"><figcaption>")
            .Append(WebUtility.HtmlEncode(file))
            .Append("</figcaption></figure>");
    }

    html.Append("</main></body></html>");
    File.WriteAllText(
        Path.Combine(directory, "index.html"),
        html.ToString(),
        new UTF8Encoding(false));
}

static async Task TestLocalFeedAsync(string root)
{
    string packageProject = Path.Combine(root, "tests", "assets", "Local.Package", "Local.Package.csproj");
    string consumerProject = Path.Combine(root, "tests", "local-feed", "Consumer", "Consumer.csproj");
    string feed = Path.Combine(root, "tests", "local-feed", "packages");
    string consumerObj = Path.Combine(root, "tests", "local-feed", "Consumer", "obj");
    string consumerPackages = Path.Combine(consumerObj, "local-feed-packages");
    string consumerLock = Path.Combine(consumerObj, "local-feed.packages.lock.json");
    string consumerFramework = GetSdkMajor(root) >= 10 ? "net10.0" : "net8.0";
    Directory.CreateDirectory(feed);
    if (Directory.Exists(consumerPackages))
    {
        Directory.Delete(consumerPackages, recursive: true);
    }

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
        "--force-evaluate",
        "--no-cache",
        "-p:RestoreLockedMode=false",
        "-p:RestorePackagesWithLockFile=true",
        $"-p:NuGetLockFilePath={consumerLock}",
        $"-p:RestorePackagesPath={consumerPackages}",
        $"-p:TargetFramework={consumerFramework}",
    });
    // A freshly packed archive has a host-specific content hash. Replay its disposable
    // lock in locked mode to test reproducibility without weakening tracked product locks.
    await RunAsync(root, "dotnet", new[]
    {
        "restore",
        consumerProject,
        "--locked-mode",
        "--no-cache",
        $"-p:NuGetLockFilePath={consumerLock}",
        $"-p:RestorePackagesPath={consumerPackages}",
        $"-p:TargetFramework={consumerFramework}",
    });
    await RunAsync(root, "dotnet", new[]
    {
        "build",
        consumerProject,
        "--configuration",
        "Release",
        "--no-restore",
        $"-p:RestorePackagesPath={consumerPackages}",
        $"-p:TargetFramework={consumerFramework}",
    });
}

static async Task PackAsync(string root)
{
    bool rollForwardNet8 = GetSdkMajor(root) >= 10;
    foreach (string sourceFramework in new[] { "net8.0", "net10.0" })
    {
        IReadOnlyDictionary<string, string?>? environment =
            rollForwardNet8 && sourceFramework.Equals("net8.0", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, string?> { ["DOTNET_ROLL_FORWARD"] = "Major" }
                : null;
        await RunAsync(
            root,
            "dotnet",
            new[]
            {
                "run",
                "--project",
                Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"),
                "--framework",
                sourceFramework,
                "--no-restore",
                "--",
                "version",
            },
            environment);
    }

    string versionProps = File.ReadAllText(Path.Combine(root, "eng", "Version.props"));
    Match versionMatch = Regex.Match(
        versionProps,
        "<VersionPrefix>(?<version>[^<]+)</VersionPrefix>",
        RegexOptions.CultureInvariant);
    if (!versionMatch.Success)
    {
        throw new InvalidOperationException("eng/Version.props does not contain VersionPrefix.");
    }

    string version = versionMatch.Groups["version"].Value;
    string output = Path.Combine(root, "artifacts", "packages");
    Directory.CreateDirectory(output);
    foreach (string existing in Directory.EnumerateFiles(
                 output,
                 "YetAnotherAnalyzerProfiler.Tool.*.nupkg",
                 SearchOption.TopDirectoryOnly))
    {
        File.Delete(existing);
    }

    await RunAsync(root, "dotnet", new[]
    {
        "pack",
        Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"),
        "--configuration",
        "Release",
        "--no-restore",
        "--output",
        output,
    });

    string package = Path.Combine(
        output,
        $"YetAnotherAnalyzerProfiler.Tool.{version}.nupkg");
    if (!File.Exists(package))
    {
        throw new InvalidOperationException($"NuGet tool package was not produced: {package}");
    }

    NormalizeNuGetPackage(package);

    string repeatOutput = Path.Combine(root, "artifacts", "packages-repeat");
    Directory.CreateDirectory(repeatOutput);
    await RunAsync(root, "dotnet", new[]
    {
        "pack",
        Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"),
        "--configuration",
        "Release",
        "--no-restore",
        "--output",
        repeatOutput,
    });
    string repeatedPackage = Path.Combine(
        repeatOutput,
        $"YetAnotherAnalyzerProfiler.Tool.{version}.nupkg");
    if (!File.Exists(repeatedPackage))
    {
        throw new InvalidOperationException("Repeated NuGet pack was not produced.");
    }

    NormalizeNuGetPackage(repeatedPackage);

    byte[] packageHash;
    byte[] repeatedPackageHash;
    using (FileStream stream = File.OpenRead(package))
    {
        packageHash = SHA256.HashData(stream);
    }

    using (FileStream stream = File.OpenRead(repeatedPackage))
    {
        repeatedPackageHash = SHA256.HashData(stream);
    }

    if (!packageHash.SequenceEqual(repeatedPackageHash))
    {
        throw new InvalidOperationException("Repeated NuGet packs must be byte-for-byte deterministic.");
    }

    using (ZipArchive archive = ZipFile.OpenRead(package))
    {
        HashSet<string> entries = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string required in new[]
                 {
                     "PACKAGE-README.md",
                     "LICENSE",
                     "THIRD-PARTY-NOTICES.txt",
                     "tools/net8.0/any/DotnetToolSettings.xml",
                     "tools/net8.0/any/yaap.deps.json",
                     "tools/net8.0/any/yaap.dll",
                     "tools/net8.0/any/yaap.runtimeconfig.json",
                     "tools/net8.0/any/Yaap.BuildLogger.dll",
                     "tools/net10.0/any/DotnetToolSettings.xml",
                     "tools/net10.0/any/yaap.deps.json",
                     "tools/net10.0/any/yaap.dll",
                     "tools/net10.0/any/yaap.runtimeconfig.json",
                     "tools/net10.0/any/Yaap.BuildLogger.dll",
                 })
        {
            if (!entries.Contains(required))
            {
                throw new InvalidOperationException($"NuGet tool package is missing {required}.");
            }
        }

        if (entries.Any(entry => entry.StartsWith("content/", StringComparison.OrdinalIgnoreCase) ||
            entry.StartsWith("contentFiles/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "NuGet tool package must not leak build logger assemblies as project content.");
        }

        if (entries.Any(entry =>
                entry.EndsWith("/YetAnotherAnalyzerProfiler.Tool.deps.json", StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith("/YetAnotherAnalyzerProfiler.Tool.runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "NuGet tool runtime metadata must use the yaap command name.");
        }

        ZipArchiveEntry packageReadme = archive.GetEntry("PACKAGE-README.md") ??
            throw new InvalidOperationException("NuGet tool package README is missing.");
        using (StreamReader reader = new(packageReadme.Open()))
        {
            string text = reader.ReadToEnd();
            if (text.Contains("](../", StringComparison.Ordinal) ||
                text.Contains("](docs/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("NuGet package README contains a repository-relative link.");
            }
        }

        string? repositoryUrl = Environment.GetEnvironmentVariable("RepositoryUrl");
        if (!string.IsNullOrWhiteSpace(repositoryUrl))
        {
            ZipArchiveEntry nuspec = archive.Entries.Single(entry =>
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            using StreamReader reader = new(nuspec.Open());
            string text = reader.ReadToEnd();
            if (!text.Contains(repositoryUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("NuGet repository metadata does not match RepositoryUrl.");
            }

            string? revision = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (!string.IsNullOrWhiteSpace(revision) &&
                !text.Contains(revision, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("NuGet repository commit does not match GITHUB_SHA.");
            }
        }
    }

    string smokeRoot = Path.Combine(
        root,
        "artifacts",
        "tool-smoke",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(smokeRoot);
    try
    {
        string nugetConfig = Path.Combine(smokeRoot, "NuGet.Config");
        string escapedOutput = System.Security.SecurityElement.Escape(output) ?? output;
        File.WriteAllText(
            nugetConfig,
            $"<configuration><packageSources><clear /><add key=\"local\" value=\"{escapedOutput}\" /></packageSources></configuration>",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await RunAsync(root, "dotnet", new[]
        {
            "tool",
            "install",
            "--tool-path",
            smokeRoot,
            "--configfile",
            nugetConfig,
            "YetAnotherAnalyzerProfiler.Tool",
            "--version",
            version,
        });
        string executable = Path.Combine(
            smokeRoot,
            OperatingSystem.IsWindows() ? "yaap.exe" : "yaap");
        await RunAsync(root, executable, new[] { "--version" });
        await RunAsync(root, executable, new[] { "--help" });
        foreach (string[] command in new[]
                 {
                     new[] { "profile", "--help" },
                     new[] { "configurations", "--help" },
                     new[] { "history", "list", "--help" },
                     new[] { "history", "show", "--help" },
                     new[] { "history", "delete", "--help" },
                     new[] { "compare", "--help" },
                     new[] { "export", "--help" },
                     new[] { "analyze", "--help" },
                     new[] { "version", "--help" },
                 })
        {
            await RunAsync(root, executable, command);
        }
    }
    finally
    {
        string artifactsRoot = Path.GetFullPath(Path.Combine(root, "artifacts")) +
            Path.DirectorySeparatorChar;
        string resolvedSmokeRoot = Path.GetFullPath(smokeRoot);
        if (!resolvedSmokeRoot.StartsWith(artifactsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to delete tool smoke path outside artifacts: {resolvedSmokeRoot}");
        }

        if (Directory.Exists(resolvedSmokeRoot))
        {
            Directory.Delete(resolvedSmokeRoot, recursive: true);
        }
    }
}

static void NormalizeNuGetPackage(string packagePath)
{
    const string corePrefix = "package/services/metadata/core-properties/";
    const string corePath = corePrefix + "yaap.psmdcp";
    DateTimeOffset timestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    string temporaryPath = packagePath + ".deterministic";
    using (ZipArchive source = ZipFile.OpenRead(packagePath))
    using (FileStream output = new(
               temporaryPath,
               FileMode.Create,
               FileAccess.Write,
               FileShare.None,
               64 * 1024,
               FileOptions.SequentialScan))
    using (ZipArchive destination = new(output, ZipArchiveMode.Create, leaveOpen: false))
    {
        foreach (ZipArchiveEntry sourceEntry in source.Entries.OrderBy(
                     entry => entry.FullName.StartsWith(corePrefix, StringComparison.Ordinal)
                         ? corePath
                         : entry.FullName,
                     StringComparer.Ordinal))
        {
            string name = sourceEntry.FullName.StartsWith(corePrefix, StringComparison.Ordinal)
                ? corePath
                : sourceEntry.FullName;
            ZipArchiveEntry destinationEntry = destination.CreateEntry(name, CompressionLevel.Optimal);
            destinationEntry.LastWriteTime = timestamp;
            destinationEntry.ExternalAttributes = sourceEntry.ExternalAttributes;
            using Stream destinationStream = destinationEntry.Open();
            if (name == "_rels/.rels")
            {
                byte[] relationships = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n" +
                    "  <Relationship Type=\"http://schemas.microsoft.com/packaging/2010/07/manifest\" Target=\"/YetAnotherAnalyzerProfiler.Tool.nuspec\" Id=\"Rmanifest\" />\n" +
                    $"  <Relationship Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"/{corePath}\" Id=\"Rmetadata\" />\n" +
                    "</Relationships>");
                destinationStream.Write(relationships);
                continue;
            }

            using Stream sourceStream = sourceEntry.Open();
            sourceStream.CopyTo(destinationStream);
        }
    }

    File.Move(temporaryPath, packagePath, overwrite: true);
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
    EnsureReleaseWorkflow(root);
    EnsureCliRestoreIdentity(root);

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

    EnsureDeepReviewHarness(root, canonical);
    EnsureGuiStartupSmokeGuard(root);
    EnsureGuiVisualRegressionHarness(root);
    EnsureThirdPartyNoticeSync(root);
    EnsureCheckoutLineEndingPolicy(root);

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
        bool agentSkill = relative.Replace('\\', '/').StartsWith(
            ".agents/skills/",
            StringComparison.OrdinalIgnoreCase);
        bool nugetLock = Path.GetFileName(relative).Equals(
            "packages.lock.json",
            StringComparison.OrdinalIgnoreCase);
        bool portableLf = shell || agentSkill;
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
        if (portableLf)
        {
            if (hasBom || text.Contains("\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Shell scripts and agent skills must be UTF-8 without BOM and LF: {relative}");
            }
        }
        else if (nugetLock)
        {
            if (hasBom || (text.Contains('\n') && !text.Contains("\r\n", StringComparison.Ordinal)) ||
                text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'))
            {
                throw new InvalidOperationException(
                    $"NuGet lock files must be UTF-8 without BOM and CRLF: {relative}");
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

static void EnsureCliRestoreIdentity(string root)
{
    string project = File.ReadAllText(Path.Combine(root, "src", "Yaap.Cli", "Yaap.Cli.csproj"));
    foreach (string contract in new[]
             {
                 "<AssemblyName>yaap</AssemblyName>",
                 "<PackageId>YetAnotherAnalyzerProfiler.Tool</PackageId>",
                 "<TargetName>yaap</TargetName>",
                 "<Description>C#のRoslyn Analyzer／Source Generatorをコンパイラー報告値で測定するクロスプラットフォームCLI。</Description>",
                 "<ProjectDepsFileName>yaap.deps.json</ProjectDepsFileName>",
                 "<ProjectRuntimeConfigFileName>yaap.runtimeconfig.json</ProjectRuntimeConfigFileName>",
                 "<ToolCommandName>yaap</ToolCommandName>",
             })
    {
        if (!project.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CLI restore/package/output identities must remain deterministic: {contract}");
        }
    }

    if (project.Contains("AlignRestoreProjectIdentity", StringComparison.Ordinal) ||
        project.Contains("BeforeTargets=\"_GenerateRestoreProjectSpec\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "CLI restore identity must be defined during project evaluation; target-time PackageId mutation " +
            "is not observed by Visual Studio design-time restore and makes packages.lock.json unstable.");
    }
}

static void EnsureDeepReviewHarness(string root, string canonicalAgentInstructions)
{
    string skillRoot = Path.Combine(root, ".agents", "skills", "deep-review");
    string skillPath = Path.Combine(skillRoot, "SKILL.md");
    string metadataPath = Path.Combine(skillRoot, "agents", "openai.yaml");
    string designPath = Path.Combine(skillRoot, "references", "review-design.md");
    string templatePath = Path.Combine(skillRoot, "assets", "deep-review-plan-template.md");
    string humanGuidePath = Path.Combine(root, "docs", "deep-review.md");

    foreach (string path in new[]
    {
        skillPath,
        metadataPath,
        designPath,
        templatePath,
        humanGuidePath,
    })
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"DeepReview harness file is required: {path}");
        }
    }

    string skill = File.ReadAllText(skillPath);
    foreach (string contract in new[]
    {
        "name: deep-review",
        "only when the user explicitly invokes `$deep-review`",
        "Never use it for a generic code review",
        "references/review-design.md",
        "assets/deep-review-plan-template.md",
        "at least 9.5/10",
        "Repeat remediation and independent re-review",
    })
    {
        if (!skill.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DeepReview skill is missing contract: {contract}");
        }
    }

    string metadata = File.ReadAllText(metadataPath);
    foreach (string contract in new[]
    {
        "display_name: \"DeepReview\"",
        "default_prompt: \"$deep-review ",
        "allow_implicit_invocation: false",
    })
    {
        if (!metadata.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DeepReview metadata is missing contract: {contract}");
        }
    }

    string design = File.ReadAllText(designPath);
    foreach (string contract in new[]
    {
        "## Axis selection",
        "## Persona construction and independence",
        "## User confirmation policy",
        "## Scoring rubric",
        "score >= 9.5",
        "zero unresolved blockers",
    })
    {
        if (!design.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DeepReview design is missing contract: {contract}");
        }
    }

    string template = File.ReadAllText(templatePath);
    foreach (string contract in new[]
    {
        "## Status and invocation",
        "## Adaptive review configuration",
        "## Finding ledger",
        "## Remediation and re-review cycles",
        "## Verification matrix",
        "## Final scorecard",
        "## Git and delivery plan",
    })
    {
        if (!template.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DeepReview plan template is missing section: {contract}");
        }
    }

    if (!canonicalAgentInstructions.Contains(".agents/skills/deep-review/SKILL.md", StringComparison.Ordinal) ||
        !canonicalAgentInstructions.Contains("Never infer DeepReview from a generic", StringComparison.Ordinal) ||
        !canonicalAgentInstructions.Contains("docs/deep-review.md", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Canonical agent instructions must index explicit-only DeepReview.");
    }

    string humanGuide = File.ReadAllText(humanGuidePath);
    if (!humanGuide.Contains("自動・暗黙には起動しません", StringComparison.Ordinal) ||
        !humanGuide.Contains("$deep-review", StringComparison.Ordinal) ||
        !humanGuide.Contains("deep-review-plan-template.md", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The Japanese DeepReview guide must explain invocation and resources.");
    }

    foreach ((string path, string link) in new[]
    {
        (Path.Combine(root, "README.md"), "docs/deep-review.md"),
        (Path.Combine(root, "docs", "index.md"), "deep-review.md"),
    })
    {
        if (!File.ReadAllText(path).Contains(link, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DeepReview documentation is not indexed from {path}.");
        }
    }
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

static void EnsureCheckoutLineEndingPolicy(string root)
{
    string[] attributes = File.ReadAllLines(Path.Combine(root, ".gitattributes"));
    if (!attributes.Contains("* text=auto eol=crlf", StringComparer.Ordinal) ||
        !attributes.Contains("*.sh text eol=lf", StringComparer.Ordinal) ||
        !attributes.Contains(".githooks/* text eol=lf", StringComparer.Ordinal) ||
        !attributes.Contains(".agents/skills/** text eol=lf", StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            ".gitattributes must enforce CRLF for repository text and retain the portable LF exceptions.");
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

static void EnsureGuiVisualRegressionHarness(string root)
{
    string guidePath = Path.Combine(root, "docs", "gui-visual-testing.md");
    if (!File.Exists(guidePath))
    {
        throw new InvalidOperationException("The tracked GUI visual review guide is missing.");
    }

    string guide = File.ReadAllText(guidePath);
    foreach (string contract in new[]
    {
        "./eng/build.ps1 visual --output artifacts/gui-visuals",
        "ライトとダーク",
        "通常幅と最小幅",
        "全7メインタブ",
        "変更箇所だけでなく画面全体",
    })
    {
        if (!guide.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"GUI visual review guide is missing contract: {contract}");
        }
    }

    string buildSource = File.ReadAllText(Path.Combine(root, "eng", "Yaap.Build", "Program.cs"));
    foreach (string contract in new[]
    {
        "CaptureGuiVisualMatrixAsync",
        "ValidateVisualCaptureMatrix",
        "WriteVisualContactSheet",
        "net8.0-windows",
        "net10.0-windows",
    })
    {
        if (!buildSource.Contains(contract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"GUI visual capture harness is missing contract: {contract}");
        }
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
    UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false);
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

static Dictionary<string, string> CapturePackageLockHashes(string root)
{
    return GetRepositoryFiles(root)
        .Where(relative => Path.GetFileName(relative).Equals(
            "packages.lock.json",
            StringComparison.OrdinalIgnoreCase))
        .Where(relative => File.Exists(Path.Combine(root, relative)))
        .ToDictionary(
            relative => relative.Replace('\\', '/'),
            relative => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative)))),
            StringComparer.OrdinalIgnoreCase);
}

static void EnsurePackageLockHashes(
    string root,
    IReadOnlyDictionary<string, string> expectedHashes,
    string operation)
{
    Dictionary<string, string> actualHashes = CapturePackageLockHashes(root);
    string[] changed = expectedHashes.Keys
        .Union(actualHashes.Keys, StringComparer.OrdinalIgnoreCase)
        .Where(relative =>
            !expectedHashes.TryGetValue(relative, out string? expected) ||
            !actualHashes.TryGetValue(relative, out string? actual) ||
            !expected.Equals(actual, StringComparison.Ordinal))
        .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (changed.Length > 0)
    {
        throw new InvalidOperationException(
            $"{operation} changed tracked NuGet lock files: " + string.Join(", ", changed));
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
    string release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
    string gitlab = File.ReadAllText(Path.Combine(root, ".gitlab-ci.yml"));
    foreach (string required in new[] { sdk8, sdk10 })
    {
        if (!github.Contains(required, StringComparison.Ordinal) ||
            !gitlab.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"CI toolchains are out of sync with eng/toolchain.json: {required}");
        }
    }

    foreach (string required in new[] { image8, image10 })
    {
        if (!gitlab.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GitLab CI toolchains are out of sync with eng/toolchain.json: {required}");
        }
    }

    foreach (string required in new[] { sdk8, sdk10 })
    {
        if (!release.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release toolchains are out of sync with eng/toolchain.json: {required}");
        }
    }
}

static void EnsureReleaseWorkflow(string root)
{
    string ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
    foreach (string required in new[]
             {
                 "pull_request:",
                 "workflow_dispatch:",
                 "concurrency:",
                 "cancel-in-progress: true",
                 "timeout-minutes:",
                 "name: Verify Linux / ${{ matrix.framework }}",
                 "name: Verify Windows / ${{ matrix.framework }}",
                 "name: Verify macOS / net10.0",
                 "name: Package / ${{ matrix.runtime }}",
             })
    {
        if (!ci.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"GitHub PR verification is missing: {required}");
        }
    }

    if (Regex.Matches(
            ci,
            Regex.Escape("DOTNET_INSTALL_DIR="),
            RegexOptions.CultureInvariant).Count != 4)
    {
        throw new InvalidOperationException(
            "Each host-based GitHub CI job must isolate its requested .NET SDK installation.");
    }

    if (Regex.Matches(
            ci,
            Regex.Escape("./eng/normalize-checkout.ps1"),
            RegexOptions.CultureInvariant).Count != 3)
    {
        throw new InvalidOperationException(
            "Each non-Windows GitHub CI job must normalize its checkout line endings.");
    }

    if (Regex.Matches(
            ci,
            Regex.Escape("git switch -c agent/ci-verification"),
            RegexOptions.CultureInvariant).Count != 2)
    {
        throw new InvalidOperationException(
            "Each non-Windows GitHub verify job must isolate normalized files on an agent branch.");
    }

    string release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
    foreach (string required in new[]
             {
                 "tags: [\"v*.*.*\"]",
                 "workflow_dispatch:",
                 "release_tag:",
                 "confirm_publish:",
                 "ReleaseTag:",
                 "group: release-${{ github.event_name == 'workflow_dispatch' && inputs.release_tag || github.ref_name }}",
                 "cancel-in-progress: false",
                 "./eng/validate-release.ps1",
                 "./eng/build.ps1 verify",
                 "./eng/build.ps1 publish",
                 "environment: release",
                 "NuGet/login@ebc737b6fc418a6ca0073cf116ec8dc156d8b81e",
                 "secrets.NUGET_USER",
                 "steps.nuget-login.outputs.NUGET_API_KEY",
                 "dotnet nuget push",
                 "--skip-duplicate",
                 "gh release create",
                 "gh release edit",
                 "--notes-file",
                 ".github/release-notes/",
                 "RepositoryUrl:",
                 "attestations: write",
                 "id-token: write",
                 "actions/attest@",
                 "subject-path:",
                 "isDraft",
                 "different SHA-256 digest",
                 "Verify documented archive layout",
                 "archive-smoke/cli/yaap",
                 "macos-15-intel",
                 "Attest the package at its producer",
                 "Attest the archive at its producer",
                 "Published NuGet digest differs",
                 "Draft release asset set differs from the validated allowlist",
                 "Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames",
                 "Draft asset digest differs from validated asset",
                 "Failed to upload one or more validated release assets",
                 "Failed to read back draft release assets",
                 "Failed to publish release",
                 "Manual releases must run from the main branch.",
                 "Manual release publication was not explicitly confirmed.",
                 "The release commit must be reachable from main.",
                 "git tag --list -- \"$env:ReleaseTag\"",
                 "Failed to inspect release tag",
                 "already points to a different commit",
                 "Create or verify the immutable release tag",
                 "repos/$env:GITHUB_REPOSITORY/git/refs",
             })
    {
        if (!release.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"GitHub release workflow is missing: {required}");
        }
    }

    if (release.Contains("pull_request:", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The release workflow must never publish from pull requests.");
    }

    if (release.Contains("--generate-notes", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The release workflow must use the curated versioned release notes.");
    }

    if (release.Contains("secrets.NUGET_API_KEY", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The release workflow must use NuGet Trusted Publishing instead of a long-lived API key.");
    }

    if (Regex.Matches(
            release,
            Regex.Escape("DOTNET_INSTALL_DIR="),
            RegexOptions.CultureInvariant).Count != 3)
    {
        throw new InvalidOperationException(
            "Each host-based GitHub release job must isolate its requested .NET SDK installation.");
    }

    if (Regex.Matches(
            release,
            Regex.Escape("./eng/normalize-checkout.ps1"),
            RegexOptions.CultureInvariant).Count != 2)
    {
        throw new InvalidOperationException(
            "Release validation and binary jobs must normalize non-Windows checkouts.");
    }

    if (Regex.Matches(
            release,
            Regex.Escape("git switch -c agent/release-verification"),
            RegexOptions.CultureInvariant).Count != 1)
    {
        throw new InvalidOperationException(
            "The non-Windows release verification matrix must use an agent branch.");
    }

    string githubSetup = File.ReadAllText(Path.Combine(root, "docs", "github-setup.md"));
    foreach (string required in new[]
             {
                 "git remote add origin",
                 "git push -u origin develop/v0.1.0",
                 "git push -u origin main",
                 "一時default",
                 "Require actions to be pinned to a full-length commit SHA",
                 "Verify Linux / net8.0",
                 "release` Environment",
                 "Trusted Publishing",
                 "NUGET_USER",
                 "## 7. v0.1.0公開前",
                 "## 8. 公開操作",
                 "## 9. 公開後",
             })
    {
        if (!githubSetup.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"GitHub setup documentation is missing: {required}");
        }
    }

    string validator = File.ReadAllText(Path.Combine(root, "eng", "validate-release.ps1"));
    foreach (string required in new[]
             {
                 "eng/Version.props",
                 "expectedTag",
                 "README.md",
                 "SECURITY.md",
                 "CHANGELOG.md",
                 "releaseNotesPath",
                 ".github/release-notes/",
                 "# YAAP",
                 "はまだ公開されていません。",
                 "公開済みバージョン | なし",
                 "supportedSeries",
             })
    {
        if (!validator.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release validation is missing a publication-state guard: {required}");
        }
    }

    string dependabot = File.ReadAllText(Path.Combine(root, ".github", "dependabot.yml"));
    Match version = Regex.Match(
        File.ReadAllText(Path.Combine(root, "eng", "Version.props")),
        @"<VersionPrefix>(?<value>\d+\.\d+\.\d+)</VersionPrefix>",
        RegexOptions.CultureInvariant);
    string releaseVersion = version.Groups["value"].Value;
    string releaseNotesPath = Path.Combine(
        root,
        ".github",
        "release-notes",
        $"v{releaseVersion}.md");
    if (!version.Success ||
        !File.Exists(releaseNotesPath) ||
        !File.ReadAllText(releaseNotesPath).StartsWith(
            $"# YAAP v{releaseVersion}",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The current version must have curated GitHub Release notes: {releaseNotesPath}");
    }

    string expectedTargetBranch = $"target-branch: develop/v{version.Groups["value"].Value}";
    if (!version.Success || Regex.Matches(
            dependabot,
            Regex.Escape(expectedTargetBranch),
            RegexOptions.CultureInvariant).Count != 2)
    {
        throw new InvalidOperationException(
            $"Dependabot updates must target the active development branch twice: {expectedTargetBranch}");
    }

    foreach (string template in new[] { "bug.yml", "feature.yml", "question.yml", "config.yml" })
    {
        if (!File.Exists(Path.Combine(root, ".github", "ISSUE_TEMPLATE", template)))
        {
            throw new InvalidOperationException($"GitHub issue template is missing: {template}");
        }
    }

    string gitlab = File.ReadAllText(Path.Combine(root, ".gitlab-ci.yml"));
    foreach (string required in new[]
             {
                 "$CI_COMMIT_BRANCH && $CI_OPEN_MERGE_REQUESTS",
                 "$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH",
                 "resource_group: publish-$CI_COMMIT_REF_SLUG",
             })
    {
        if (!gitlab.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"GitLab pipeline guard is missing: {required}");
        }
    }

    if (Regex.Matches(
            gitlab,
            Regex.Escape("resource_group: publish-$CI_COMMIT_REF_SLUG"),
            RegexOptions.CultureInvariant).Count != 3)
    {
        throw new InvalidOperationException(
            "Each GitLab publish job must serialize publication through its resource group.");
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
