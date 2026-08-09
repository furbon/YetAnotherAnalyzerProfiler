# YAAP Documentation Guide

[日本語](ja/index.md)

This page is the entry point for using, operating, and developing YAAP. The implementation and CLI help define product behavior; documentation is updated to match each release.

## Choose a path

### Use YAAP

1. Read the [README](../README.md) for safety guidance and startup instructions.
2. Use the [usage guide](usage.md) for CLI and GUI operation.
3. Read the [measurement model](measurement.md) before interpreting or comparing metrics.
4. Consult [troubleshooting](troubleshooting.md) when a run fails.

### Operate YAAP

- [Security policy](../SECURITY.md) — trust boundaries, history and binlog handling, vulnerability reporting
- [Support policy](../SUPPORT.md) — supported scope and information needed in reports
- [Changelog](../CHANGELOG.md) — releases, compatibility, and known limitations
- [Release checklist](release-checklist.md) — publication gates, provenance, and recovery

### Develop YAAP

- [Architecture](architecture.md) — components, profiling pipeline, and storage
- [Development guide](development.md) — SDKs, commands, and repository rules
- [Testing policy](testing.md) — verification scope and CI
- [GitHub setup and operations](github-setup.md) — repository settings, Actions, rulesets, and release operation
- [DeepReview](deep-review.md) — explicitly invoked multi-axis adversarial repository review
- [Contribution guide](../CONTRIBUTING.md) — proposals, implementation, and review
- [Code of Conduct](../CODE_OF_CONDUCT.md)

## Supported environment

| Item | Supported scope |
| --- | --- |
| CLI | Windows, macOS, Linux |
| GUI | Windows WPF |
| Source target frameworks | .NET 8, .NET 10 |
| Profile inputs | `.sln`, `.slnx`, and `.csproj` containing C# |
| Release binaries | The attachments and changelog for each release are authoritative |

## CLI and GUI feature matrix

“Not available” means that the shared Core supports the operation but that interface does not expose it.

| Capability | CLI | GUI |
| --- | --- | --- |
| Profile a target | `profile` | Available |
| Detect configurations | Explicit `configurations` command | Automatic after target input |
| Warm, cold, custom modes | Available | Available |
| Warmups, iterations, clean | Options | Advanced settings |
| Restore control | `--restore` or `--no-restore` | Advanced settings |
| Isolated output | Enabled by default; `--no-isolated` disables it | Enabled by default |
| Explicit artifacts path | `--artifacts-path` | Advanced settings |
| Cancel current operation | Ctrl+C | Cancel button |
| Search history by text/status | Available | Available |
| Limit history by date/count | `--from`, `--to`, `--limit` | History dates and settings limit |
| Load a history result | `history show` | Double-click or Load |
| History labels | Not available | Autosave with Undo/Redo |
| Delete history | Requires `--force` | Context-menu selection or delete all in settings |
| Compare two runs | Select IDs | Select by label, time, target, configuration |
| CSV, JSON, Markdown export | Available | Available |
| Complete profile result as JSON | `--json` to stdout | JSON file from Export |
| Analyze one existing binlog | `analyze` | Troubleshooting tab |
| Light/dark theme | Not applicable | Available |

The GUI cancel button requests cancellation of the current profile, history I/O, binlog analysis, comparison, or export operation. Consult its diagnostics for preserved partial results and output-file handling.

## Key defaults

| Setting | CLI | GUI |
| --- | --- | --- |
| Profiling mode | warm | warm |
| Warmups | 1 | 1 |
| Measured iterations | 3 | 3 |
| Clean before each measurement | enabled | enabled |
| Restore | enabled | enabled |
| Isolated output | enabled | enabled |
| History retention | 50 | 50 |
| History display limit | unlimited without `--limit` | 500 |
| Configuration | Release | Latest matching history, Release, Debug, then name order |

## Published layout

A publish created by the `eng` harness has the following principal files. Runtime files required for self-contained execution and `docs/` are also included. The allowlist and Markdown-link validation are authoritative.

```text
artifacts/publish/<RID>/<TFM>/
├── cli/
│   ├── yaap or yaap.exe
│   ├── Yaap.BuildLogger.dll
│   ├── Yaap.Core.xml
│   └── LICENSE, README.md, CHANGELOG.md, THIRD-PARTY-NOTICES.txt
└── gui/                    # Windows only
    ├── yaap-gui.exe
    ├── Yaap.BuildLogger.dll
    ├── Yaap.Core.xml
    └── LICENSE, README.md, CHANGELOG.md, THIRD-PARTY-NOTICES.txt
```

The publish harness fails on files outside its allowlist. Profiling cannot run if `Yaap.BuildLogger.dll` is separated from the executable.

## History and confidential information

The default history location is `YAAP` under the local application-data directory returned by .NET. Change it with `--history`, the GUI history setting, or `YAAP_HISTORY_PATH`.

History can contain absolute paths, Git commit/branch/dirty state, SDK and OS data, diagnostics, complete logs from failed or canceled child processes, a binlog per measurement, and relative generated-file paths. Target binlogs and logs can contain secrets. Inspect and sanitize them before attaching them to an issue, chat, or public storage.

## Documentation consistency

When changing CLI options, update `yaap help`, the [usage guide](usage.md), and this feature matrix together. When changing profiling or storage, update the [measurement model](measurement.md), [architecture](architecture.md), and changelog. When changing support or safety assumptions, update README, SECURITY, and SUPPORT. Update every selected Japanese translation in the same change as its English source.
