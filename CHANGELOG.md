# Changelog

This file records changes to YAAP that materially affect users. Its structure is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow Semantic Versioning.

## [0.1.1] - 2026-08-09

### Documentation and support

- Made English the authoritative language for repository documentation, NuGet package information, release notes, and contribution templates.
- Added official Japanese translations for onboarding, usage, measurement, troubleshooting, security, and support guidance.
- Explicitly accepts GitHub issue and pull-request submissions in English or Japanese.

### Compatibility

- No CLI commands, GUI behavior, history format, measurement semantics, supported operating systems, or target frameworks changed.
- Existing v0.1.0 command lines and histories remain compatible.

### Security and privacy

- Preserved the existing trust boundary: YAAP is not a sandbox, and profiled targets can communicate or cause side effects.
- Made the same trust-boundary and sensitive-data guidance available from the English canonical documents and selected Japanese translations.

### Known limitations

- Japanese documentation intentionally covers the main user and safety journeys rather than mirroring every developer and maintainer document. English prevails if translations differ.

## [0.1.0] - 2026-08-09

YAAP's first public release.

### Major capabilities

- Analyzer and source-generator profiling for `.sln`, `.slnx`, and `.csproj` inputs, plus existing-binlog analysis
- Cross-platform CLI for .NET 8 and .NET 10, .NET global-tool distribution, and a Windows WPF GUI with light and dark themes
- Warm, cold, and custom profiling modes; repeated-run statistics; restore control; and compiler-reported analyzer/generator time
- An on-disk manifest and preview with generated-file counts, bytes, lines, relative paths, and generator assembly/type
- Local history with labels, search, date filters, retention, two-run comparison, and comparability warnings
- CSV, JSON, and Markdown result export plus complete generated-output export
- Cancellation for long operations, preservation of failed and partial runs, and stable diagnostic codes

### Security and privacy

- YAAP itself has no telemetry, update checks, or external API communication.
- The target's restore, build, MSBuild tasks, analyzers, and source generators can communicate or cause side effects.
- `--isolated` separates output locations but is not a sandbox.
- History and binlogs remain local but can contain confidential target information.

### Known limitations

- The GUI is Windows-only.
- The CLI and GUI have medium-specific differences such as standard output, file pickers, and themes. See the [feature matrix](docs/index.md#cli-and-gui-feature-matrix).
- Source-generator time is reported per assembly/type. YAAP does not estimate time per generated file.
- YAAP does not isolate untrusted targets.

Release attachments are authoritative for the operating systems, RIDs, TFMs, and checksums of each archive.
