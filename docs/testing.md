# Testing Policy

`tests/Yaap.Tests` is an executable harness with no external test framework. It covers statistics, history, search, retention, deletion, comparison, export, target discovery, isolated output, failure, partial results, cancellation, CLI behavior, and aggregation of 100,000 records.

`tests/Yaap.Gui.Tests` covers WPF initialization, drag and drop, automatic configuration discovery races/cancellation, history/Release/Debug/alphabetical selection, rejection of empty or unknown configurations, running-state descriptions, WPF UI theme/FluentWindow integration, advanced settings including restore, debounced history search, flexible dates, label autosave and Undo/Redo, readable comparison selection, existing-binlog analysis, asynchronous commands, source-generator display limits, and complete-export guidance after preview truncation.

The GUI harness renders 600 analyzers and 240 generators in real windows and verifies bounded viewports, scroll ranges, visible bars, mouse-wheel behavior, selection following, substantially fewer realized rows/tree nodes than total items, and usable scrollbar tracks/thumbs. It also checks that history loading preserves list height, selection, and scroll position; opaque calendar month/year/decade views; date clearing; minimum width; and post-reload scrollbars. Real light/dark windows verify contrast and hit areas. Calendar navigation and clear actions render normal, hover, pressed, keyboard-focus, and disabled states and verify icon contrast, centering, at least 32-pixel hit areas, and accessible names.

Failure regressions verify that nonzero clean prevents build and stores a failed run; a measured-build failure and an interrupted iteration preserve partial results; and diagnostics contain command, working directory, stdout/stderr tail, truncation, complete log, and stage-specific advice. A real process emitting 250 lines verifies the in-memory final-200 limit while complete logs retain every line. CLI normal/JSON output and GUI failure/partial displays use the same diagnostics. GUI tests render all tabs in failure state and the partial-result Troubleshooting state in both themes.

Generated-output manifest tests cover each generator's deterministic first 100, complete totals, `OutputsTruncated`, all-record NDJSON streaming, read cancellation, and complete CSV/JSON/Markdown export.

Iteration-checkpoint tests cover reconstruction of all raw samples, failed-iteration diagnostics, statistics from successful samples only, final successful generated output, and YAAP4001 for corruption. Scale tests require aggregation memory to grow with unique metrics rather than iteration count and total history writes to remain linear in recorded data.

Integration tests restore and build the analyzer/source-generator fixtures in `tests/assets` and exercise binlog parsing, compiler reports, and generated files. `tests/local-feed` builds a local package and verifies `<clear />`, a relative private-feed equivalent, locked restore, and fully offline restore. These hermetic fixtures are CI-authoritative.

The .NET 10 lane also starts the YAAP test executable as `net8.0`. This verifies that SDK-hosted logging completes a profile when an older YAAP runtime cannot directly read a newer SDK's binlog.

GitHub Actions and GitLab CI invoke the same `eng` harness on .NET 8/10 and Windows, Linux, and macOS. WPF is Windows-only; self-contained single-file CLI output is verified on its target OS. Windows runs GUI tests in Debug for both TFMs and creates, shows, lays out, and closes `MainWindow` on STA. GUI changes additionally require the [GUI visual regression command](gui-visual-testing.md) and manual comparison of every affected state in both themes.

Publish verification enforces an exact artifact allowlist, bundled documentation, CLI version/help, and Windows GUI startup/close. NuGet pack checks both TFM payloads, BuildLogger, README, and notices, installs from a local feed, and runs `version` and `help`. The release workflow rejects a tag that differs from `eng/Version.props`.

Before locked restore, `verify` runs ordinary forced restore, Debug rebuild with implicit restore, and—when available on Windows—Visual Studio MSBuild rebuild of every lock-owning project and its reference graph. Every `packages.lock.json` SHA-256 must remain unchanged. Lock files follow NuGet's UTF-8-without-BOM/CRLF output.

The CLI restore identity is the evaluation-time `PackageId`, `YetAnotherAnalyzerProfiler.Tool`; executable, assembly, deps, and runtimeconfig use `yaap`. Do not mutate `PackageId` during a restore target because Visual Studio design-time restore will not observe it and lock files will churn.
