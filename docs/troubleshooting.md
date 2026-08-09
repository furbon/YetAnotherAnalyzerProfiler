# Troubleshooting

[日本語](ja/troubleshooting.md)

## Start profiling is disabled

Read the bottom status. It distinguishes a missing target, target/configuration discovery, invalid path, empty configuration, and an active profile, and states the next required action. Successful discovery selects a configuration automatically and reports that profiling is ready.

If configuration discovery fails, confirm that the input exists, has a `.sln`, `.slnx`, or `.csproj` extension, and contains valid solution syntax or XML. Expand Advanced settings to inspect history and isolated-output paths.

## YAAP1001: Invalid input

Confirm that the target exists and is a `.sln`, `.slnx`, or `.csproj`. An isolated-output path must be outside the target directory.

## YAAP1002: Invalid conditions

Check iteration and retention ranges, date ordering, comparison selections, and output extensions. YAAP1001 means the target file cannot be opened; YAAP1002 means a selection, value, or combination is invalid after opening it.

## YAAP2001: restore, clean, build, or profile failed

The CLI prints the diagnostic code and summary followed by the command, working directory, complete log, stdout/stderr tail, and suggested action. The GUI shows failure in red and partial results in yellow, including the failed `dotnet clean` or `dotnet build` stage. Select the diagnostic in Troubleshooting to read complete suggested action and copy details or logs.

Complete logs stream under `logs` in the history run as `restore.log`, `clean-001.log`, `build-001.log`, and similar names. Failed and canceled commands retain complete logs. Diagnostic stdout and stderr are limited to their final 200 lines. When earlier lines are available only in the complete log, inspect it for the first error. A log-write failure is itself retained in diagnostics.

First rerun the recorded command from its recorded working directory. For restore failures, check private-feed credential providers, `NuGet.Config`, package sources, lock files, network access, and selected SDK. YAAP does not replace them; it preserves ordinary dotnet behavior.

For clean failures, check whether an IDE, test runner, or another build holds `bin`, `obj`, or the isolated path. Check read-only attributes, delete permissions, free space, and custom `Clean` targets. Remove the cause before rerunning the recorded `dotnet clean`.

For build failures, locate the first error or MSBuild code in the complete log. Verify the recorded configuration and target framework, `global.json` and installed SDK, restored references, packages, and custom targets. Measured builds use `--no-restore`; when packages are the cause, first complete restore under the recorded conditions.

If only isolated mode fails, check custom targets for fixed `bin` or `obj` assumptions. Make the target support `--artifacts-path` or use normal output only in an environment where target writes are acceptable.

## YAAP2002: Child process remained after cancellation

YAAP terminates the process tree and waits. If repeated termination cannot stop it, YAAP stores YAAP2002 and the GUI does not falsely report a safe exit or close. Use operating-system tools to inspect and terminate the recorded process ID, then remove the target behavior that keeps files or processes alive.

## YAAP3001: Could not parse binlog or report

Confirm that C# compilation occurred and the SDK supports `/reportanalyzer`. Recreate a damaged binlog. Unknown report rows produce a partial result and retain the affected line.

Normal profiling obtains compiler data through a logger hosted by the target SDK, so a YAAP process on .NET 8 can measure a target built with the .NET 10 SDK without parsing that SDK's binlog. Direct `yaap analyze` of an existing binlog should use the same or a newer YAAP generation than the SDK that created it.

## YAAP5001: Canceled

Cancellation terminates the child process tree and preserves collected measurements in history. A later profile uses a new run ID.

## YAAP4001: Could not read or write history

An active run cannot be deleted. Retry after it completes or is canceled. Check history-directory permissions, free space, and locks held by other processes. Retention cleanup excludes active runs and counts completed runs.

## YAAP6001: Could not export

Check that the output directory exists and has permissions and free space, and that another application does not lock the destination. Export writes a temporary file in the same directory and replaces the destination, so failure or cancellation does not overwrite an existing file with partial content.

## Comparison warnings

Results with different SDK, operating system, CPU, configuration, or TFM are advisory. Reprofile on the same machine with the same SDK, configuration, and iteration conditions to reduce variation from concurrent analyzer execution.
