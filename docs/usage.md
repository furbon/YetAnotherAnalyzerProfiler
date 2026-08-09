# Usage Guide

[日本語](ja/usage.md)

> [!WARNING]
> YAAP is not a sandbox. It runs target MSBuild tasks, analyzers, and source generators and replays compiler invocations to obtain reports. Do not profile an untrusted target. `--isolated` separates standard output locations but cannot prevent arbitrary writes or communication. See the [security policy](../SECURITY.md#trust-boundary-for-profiled-targets).

## Supported environment

The CLI runs on .NET 8 or .NET 10 on Windows, macOS, and Linux. The GUI is a Windows WPF application. Inputs are `.sln`, `.slnx`, and `.csproj` files containing ordinary C# applications.

The .NET 8 SDK cannot build `.slnx` directly. YAAP creates a temporary compatibility `.sln` in the history workspace containing the same C# projects. It does not write this compatibility solution into the target repository.

## CLI

```text
yaap profile <target> [--configuration Release] [--mode warm|cold|custom]
    [--warmups N] [--iterations N] [--clean true|false]
    [--restore true|false] [--isolated|--no-isolated] [--artifacts-path PATH]
    [--history PATH] [--retention N]
yaap configurations <target>
yaap history list|show|delete [options]
yaap compare <baseline-id> <candidate-id> [--history PATH]
yaap export <run-id> --format csv|json|markdown --output PATH
yaap analyze <binlog>
yaap version
```

Warm mode defaults to one warmup, three measured iterations, and a clean before every measurement. Cold mode uses no warmup, three measurements, and a clean before each. Custom mode configures the counts. When restore is enabled, it runs once before profiling and uses the target's normal `NuGet.Config` hierarchy, credential providers, private feeds, and lock files.

Isolated output is enabled by default. `--isolated` enables it explicitly; `--no-isolated` disables it. YAAP passes .NET's `--artifacts-path` to restore, clean, and build. The output must be outside the target directory. A custom target that assumes fixed `bin` or `obj` paths fails explicitly; YAAP does not silently fall back to normal output.

Exit codes are success `0`, usage `2`, failure `3`, partial result `4`, and cancellation `130`. For a failure or partial result, normal output includes each diagnostic's code, summary, command, working directory, complete-log path, output tail, and suggested action. `--json` includes the same information in the run's `diagnostics`. Complete logs remain under the configured history run and are available through `history show`.

## History and comparison

History is stored under the user's local application-data directory by default and can be changed with `--history`. `history list` filters by text, status, and start/end time; `history show` loads details lazily. Deletion requires `--force`.

Comparison reports analyzer/generator changes, additions/removals, and generated-file count/byte differences. It warns when SDK, operating system, CPU, configuration, or target frameworks differ.

CSV, JSON, and Markdown export of stored history streams the on-disk generated-output manifest and includes every generated file. In-memory run data limits each generator's file list to a deterministic first 100, while its count, bytes, and line totals always cover all files.

## GUI

The GUI provides target selection, automatic configuration discovery, profiling mode, isolated output, progress, cancellation, history search/load, comparison, deletion, and CSV/JSON/Markdown export. Row, column, and tree virtualization use recycling for large data. Tables and trees support the mouse wheel, scrollbars, arrow keys, and Page Up/Down. Build, parsing, and history I/O run outside the UI thread.

Drop one `.sln`, `.slnx`, or `.csproj` onto the target field, choose Browse, or type a path. Configuration discovery starts automatically after a short debounce and cancels obsolete discovery when the input changes.

If available, selection prefers the configuration used by the latest history for the same target, then Release, Debug, and alphabetical order. A custom configuration not in the discovered list can be typed and is built by that exact name after an explicit warning. An empty configuration cannot start. The bottom status always shows Ready to profile, the required preparation, or Profiling plus discovery/profile progress.

The GUI uses WPF UI's Fluent theme. Auto follows the system theme at startup and on Windows changes; Light and Dark can be selected at any time and apply consistently to the complete window and popups. History location, retention, restore, clean, isolated output, and artifacts path appear only when Advanced settings is expanded.

Source-generator time represents the complete generator assembly/type. Generated files display only count, size, lines, and relative path because Roslyn does not report per-file time. The first 100 generated files form the preview; complete CSV/JSON/Markdown export is identified when more exist.

The History tab debounces text/status filters. Start and end dates can be selected from an opaque calendar or entered in forms such as `2026/01/31`, `2026-01-31`, and `31/Jan/2026`. Double-click a run or choose Load without disturbing list position and selection. Delete is available from the context menu.

Optional labels save automatically. Ctrl+Z/Ctrl+Y undo or redo only label edits made in the current application session. If another YAAP instance edits the same history, refresh before editing. Internal IDs are hidden; Comparison selects two runs by label, time, target, and configuration.

The history display limit is 1–10000 in Settings and defaults to 500. Settings can also open the history directory or, after confirmation, delete all history. Export chooses JSON, CSV, or Markdown and the destination in one standard dialog; manually typed destinations require `.json`, `.csv`, `.md`, or `.markdown`.

Existing-binlog analysis and diagnostics live in the Troubleshooting tab. A failed operation displays its stage and diagnostic code in red at the bottom; a partial result uses yellow. Selecting a diagnostic shows wrapped suggested action plus scrollable, selectable, copyable details and logs.

The GUI Cancel button can stop the active profile, history I/O, binlog analysis, comparison, or export. See the [feature matrix](index.md#cli-and-gui-feature-matrix) for medium-specific differences.
