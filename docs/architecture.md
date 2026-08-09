# Architecture

YAAP consists of `Yaap.Core`, `Yaap.Cli`, `Yaap.Gui`, and `Yaap.BuildLogger`, which runs inside the MSBuild process that builds the target. Core owns target discovery, child processes, streaming binlog parsing, statistics, history, comparison, and export. The CLI and GUI use the same API.

## Profiling pipeline

1. Discover inputs and configurations with streaming XML or text parsing.
2. Record Git, SDK, operating system, CPU, and target frameworks.
3. Run the target's ordinary restore once when restore is enabled.
4. After required warmups, repeat clean and a non-incremental build with `/reportanalyzer`.
5. MSBuild records each C# compiler invocation to a line-oriented sidecar. This avoids a binlog-generation dependency when YAAP runs on .NET 8 and the target uses a newer SDK.
6. Replay each recorded compiler invocation through a response file and collect Roslyn analyzer/generator reports. This pass contributes only compiler-reported analyzer/generator metrics, not measured build duration.
7. For standalone binlog analysis and the legacy measurement path, consume `BinaryLogReplayEventSource` one event at a time without expanding the whole log.
8. Scan external `EmitCompilerGeneratedFiles` output in 64 KiB chunks.
9. Aggregate generated output by generator assembly and type for each iteration, then retain the final aggregate once in history.
10. Atomically store mean, minimum, maximum, population standard deviation, and partial results.

Roslyn reports analyzer and diagnostic-type time plus generator assembly/type time. It does not report execution time for individual generated files, so YAAP does not infer it. A report value of `<0.001 seconds` is conservatively stored as the reporting resolution's upper bound of 1 ms.

## Large inputs

Binlogs and compiler sidecars are processed as event or line streams. Child-process stdout and stderr stream to per-command logs under the history run; memory retains at most the final 200 lines of each stream. Temporary logs for successful commands are removed. Failed or canceled commands retain complete logs, the command, working directory, truncation status, and diagnostics.

History lists load only small summaries and load full runs on selection. Generated files are read one at a time in 64 KiB chunks without retaining content. Complete generated-output metadata is written one line at a time to `generated-outputs.ndjson` and atomically replaces its temporary file on completion. Each generator in run JSON retains a deterministic first-100 preview and `OutputsTruncated`; counts, bytes, and lines still describe all output.

History and export consume the manifest as a cancelable asynchronous line stream. History exports all records to CSV, JSON, or Markdown; the GUI virtualizes a bounded preview. Memory therefore does not grow with total binlog bytes, generated-file bytes, or generated-output record count. Disk use grows with the complete manifest.

Each measured iteration is atomically checkpointed as one file under `measurements/`; `run.json` holds aggregate state. Avoiding serialization of all earlier samples on every iteration keeps total writes linear in iteration count and data size. History detail and complete CLI JSON replay checkpoints in order, preserving successful and failed recorded iterations and diagnostics after failure or cancellation.

## GUI state and themes

Target discovery uses generation numbers and cancellation tokens so stale results never reach the UI. After replacing the configuration list, selection is explicitly chosen in this order: latest history for the same target, Release, Debug, then alphabetical. Command availability and status text derive from the same state.

The GUI uses WPF UI's FluentWindow, theme dictionaries, and control dictionaries together. YAAP does not maintain a separate palette or base control templates. Auto follows Windows theme changes through WPF UI; explicit light or dark selection uses the same theme system for windows, popups, inputs, tables, and status displays.

## Offline operation and data

YAAP itself has no telemetry, update check, or external API client. The target's child `dotnet restore` can contact configured feeds; its build, MSBuild tasks, analyzers, and source generators can communicate according to their implementation. YAAP does not sandbox them.

History is local JSON and YAAP does not transmit it, but it can contain absolute paths, Git information, diagnostics, complete child-process logs, binlogs, and other confidential information. See the [security policy](../SECURITY.md).
