# YetAnotherAnalyzerProfiler (YAAP)

English | [日本語](README.ja.md)

[![CI](https://github.com/furbon/YetAnotherAnalyzerProfiler/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/furbon/YetAnotherAnalyzerProfiler/actions/workflows/ci.yml?query=branch%3Amain)
[![NuGet](https://img.shields.io/nuget/v/YetAnotherAnalyzerProfiler.Tool?logo=nuget)](https://www.nuget.org/packages/YetAnotherAnalyzerProfiler.Tool)
[![GitHub Release](https://img.shields.io/github/v/release/furbon/YetAnotherAnalyzerProfiler?display_name=tag&sort=semver)](https://github.com/furbon/YetAnotherAnalyzerProfiler/releases/latest)

YAAP is a local profiler that measures the cost of Roslyn analyzers and source generators in C# builds using compiler-reported values. It stores measurements as local history and can compare two runs or export results as CSV, JSON, or Markdown.

- Non-interactive CLI for Windows, macOS, and Linux
- WPF GUI for Windows
- `.sln`, `.slnx`, and `.csproj` inputs with .NET 8 and .NET 10 SDKs
- Separate analyzer and source-generator totals
- Generated-file counts, sizes, line counts, and relative paths
- Local history, search, comparison, and export

> [!WARNING]
> YAAP is not a sandbox. Profiling runs the target's restore, clean, and build operations and replays compiler invocations that include its analyzers and source generators. Target code can perform file operations, start processes, communicate over the network, or cause other side effects with the same user permissions as YAAP. Do not run untrusted repositories. `--isolated` separates `bin` and `obj` outputs; it is not a security boundary. See the [security policy](SECURITY.md#trust-boundary-for-profiled-targets).

## Quick start

Install the latest stable CLI as a .NET global tool after reviewing its NuGet package page and provenance-backed release:

```powershell
dotnet tool install --global YetAnotherAnalyzerProfiler.Tool
yaap version
```

Run the CLI from the repository:

```powershell
dotnet run --project src/Yaap.Cli --framework net10.0 -- profile path/to/App.slnx
dotnet run --project src/Yaap.Cli --framework net10.0 -- history list
```

On Windows, run the GUI:

```powershell
dotnet run --project src/Yaap.Gui --framework net10.0-windows
```

Isolated output is enabled by default. YAAP passes .NET's `--artifacts-path` to restore, clean, and build, but cannot prevent arbitrary writes by custom MSBuild targets. Use `--no-isolated` only when the target must use its normal `bin` and `obj` directories. YAAP itself has no telemetry or update checks. The target's restore, build, analyzers, and source generators may still communicate according to the target's configuration and implementation.

## Distribution

Create self-contained binaries locally:

```powershell
./eng/build.ps1 publish --runtime win-x64 --framework net10.0
```

```sh
./eng/build.sh publish --runtime linux-x64 --framework net10.0
```

Outputs are written under `artifacts/publish/<RID>/<TFM>/` in `cli` and, on Windows, `gui`. Keep `Yaap.BuildLogger.dll` beside the executable because profiling requires it. Official release archives include the applicable executables plus `LICENSE`, `THIRD-PARTY-NOTICES.txt`, README, and CHANGELOG.

Build and locally verify the CLI NuGet package with:

```powershell
./eng/build.ps1 pack --framework net10.0
```

The verified package is written to `artifacts/packages/YetAnotherAnalyzerProfiler.Tool.<version>.nupkg`.

After extracting a release archive, run YAAP without opening an SDK project. On Windows:

```powershell
.\cli\yaap.exe version
.\cli\yaap.exe profile C:\path\to\App.slnx
.\gui\yaap-gui.exe
```

On Linux or macOS, confirm the CLI executable bit after extraction:

```sh
chmod +x ./cli/yaap
./cli/yaap version
./cli/yaap profile /path/to/App.slnx
```

Source builds target .NET 8 and .NET 10. Consult each [release](https://github.com/furbon/YetAnotherAnalyzerProfiler/releases) and the [changelog](CHANGELOG.md) for the operating systems, CPU architectures, and TFMs provided as self-contained binaries.

## CLI and GUI

The CLI and GUI use the same Core and support the same profiling conditions, history filters, existing-binlog analysis, comparison, and complete export. Medium-specific differences such as standard output, file pickers, and themes are documented in the [feature matrix](docs/index.md#cli-and-gui-feature-matrix).

## Data and privacy

History can contain measurements, absolute target paths, SDK and OS information, Git information, diagnostics, complete logs from failed or canceled child processes, and binlogs. Binlogs and restore, clean, or build output may contain confidential information from the target project.

History remains local and YAAP does not transmit it. Inspect content before sharing it and delete history that is no longer needed.

## Documentation

Start with the [documentation guide](docs/index.md).

- [Usage guide](docs/usage.md) — CLI, GUI, history, and comparison
- [Measurement model](docs/measurement.md) — metric meaning and limitations
- [Troubleshooting](docs/troubleshooting.md) — diagnostic codes and remedies
- [Architecture](docs/architecture.md) — components and data flow
- [Development guide](docs/development.md) and [testing policy](docs/testing.md)
- [GitHub setup and operations](docs/github-setup.md) — repository settings, Actions, and release operation
- [Release checklist](docs/release-checklist.md) — publication gates, provenance, and recovery
- [DeepReview guide](docs/deep-review.md) — explicitly invoked high-rigor repository review
- [Changelog](CHANGELOG.md), [contribution guide](CONTRIBUTING.md), [security policy](SECURITY.md), and [support policy](SUPPORT.md)

## License

YAAP is available under the [MIT License](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for software distributed with YAAP.
