# YetAnotherAnalyzerProfiler CLI

YAAP is a local tool that measures Roslyn analyzer and source-generator execution time in C# builds using compiler-reported values.

## Get started

```powershell
dotnet tool install --global YetAnotherAnalyzerProfiler.Tool
yaap profile path/to/App.slnx
yaap history list
yaap help
```

.NET 8 or .NET 10 is required. Run `yaap <command> --help` for command-specific help.

## Safety

YAAP is not a sandbox. It runs the target's restore, clean, build, MSBuild tasks, analyzers, and source generators with your permissions. Do not profile an untrusted target. Isolated output moves standard `bin` and `obj` output; it does not restrict file operations or communication.

YAAP itself has no telemetry or update checks. The profiled target can still communicate through ordinary restore or its own implementation. History, binlogs, and exports can contain absolute paths, logs, and confidential target information. Inspect them before sharing.

## Metrics

Analyzer and source-generator time is reported by the compiler per assembly/type. YAAP does not estimate execution time per generated file. Failed measurements retain diagnostics and do not enter successful-sample statistics.

For complete documentation, issues, and private security reporting, visit the [YAAP repository](https://github.com/furbon/YetAnotherAnalyzerProfiler). [Japanese documentation](https://github.com/furbon/YetAnotherAnalyzerProfiler/blob/main/README.ja.md) is also available.

This readme is embedded in each package version. It is updated from the repository when a new package is published; NuGet.org does not fetch later repository changes into an existing package.
