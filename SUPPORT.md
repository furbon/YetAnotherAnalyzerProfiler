# Support Policy

[日本語](docs/ja/support.md)

Questions, bug reports, feature proposals, and pull requests may be written in English or Japanese. Support is provided on a best-effort basis without a guaranteed response time.

## Supported scope

- CLI: Windows, macOS, and Linux
- GUI: Windows WPF
- YAAP target frameworks: .NET 8 and .NET 10
- Profile inputs: `.sln`, `.slnx`, and `.csproj` containing C#
- History, comparison, and CSV, JSON, or Markdown export

Each release and the [changelog](CHANGELOG.md) are authoritative for published binary operating systems, RIDs, and TFMs. See the [testing policy](docs/testing.md) for the specific SDK, operating-system, and runner coverage.

## Questions and bug reports

Use the issue tracker of the service hosting this repository for general questions, reproducible bugs, and feature proposals. In a local copy with no configured hosting URL, use the route named by the repository maintainer. Do not include vulnerabilities or secrets in an issue; follow the [security policy](SECURITY.md).

Include:

- Output from `yaap version`
- Operating system, CPU architecture, and `dotnet --info`
- CLI or GUI, target format, configuration, profiling mode, and isolated-output setting
- Expected and actual behavior
- YAAP diagnostic codes and sanitized diagnostic content
- A minimal public reproduction project when possible

Binlogs, history JSON, and logs can contain absolute paths, feeds, user information, and compiler arguments. Inspect attachments and remove confidential, personal, and organization-specific information before sharing them.

## Out of scope or not guaranteed

- A sandbox for executing untrusted repositories safely
- Blocking communication or side effects from target MSBuild tasks, analyzers, or source generators
- Per-generated-file execution time not reported by Roslyn
- Profiling a target with no C# compiler invocation
- Configuring target-specific NuGet feeds, credential providers, or custom build environments
- Operating systems, CPU architectures, or preview SDKs outside the documented support matrix

See the [usage guide](docs/usage.md) and [troubleshooting guide](docs/troubleshooting.md) for `.slnx`, private feeds, isolated output, and diagnostic codes.

## Compatibility and updates

During 0.x development, history schemas and CLI behavior can change when required to reach a stable release. Compatibility effects, migrations, and known limitations are recorded in the changelog. Review the release notes and back up required history and exports before updating.
