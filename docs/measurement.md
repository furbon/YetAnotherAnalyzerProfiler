# Measurement Model

[日本語](ja/measurement.md)

The default profile uses Release, one restore, one warmup, three measured iterations, and a clean before every measured iteration. Analyzer concurrency remains enabled. YAAP stores the mean, minimum, maximum, population standard deviation, and environment information. Cold mode omits the warmup; custom mode configures counts and the clean policy.

Elapsed build time is the duration of the ordinary measured build. Analyzer and source-generator time comes from Roslyn itself: a streaming logger running inside the target build records each C# compiler invocation, then YAAP faithfully replays that invocation to obtain Roslyn reports. Replay duration is not added to build duration. Because the logger runs with the target SDK, measurement does not depend on YAAP's binlog-reader generation matching the target SDK.

Source-generator time is reported per assembly/type. Generated-file count, bytes, lines, and paths are separate metrics. YAAP never estimates, apportions, or assigns time to an individual generated file.

## Metric hierarchy and aggregation

`Analyzer` represents analyzer time, `Diagnostic` is its per-diagnostic-ID breakdown, and `Generator` is source-generator time. When the compiler returns both an assembly total and type/diagnostic detail, those rows form a hierarchy and must not be added together. History's total mean milliseconds prefer the assembly total; only assemblies without a total contribute the sum of their type rows. The resulting total is averaged across successful samples.

For example, if `A.dll` reports a 10 ms total and type details of 6 ms and 4 ms, its history value is 10 ms, not 20 ms. If `B.dll` has no total and one 3 ms type row, the overall value is 13 ms. Failed iterations retain metrics, diagnostics, and logs but do not enter statistics. Requested iterations, recorded iterations, and successful samples can differ.

The additional compiler-reporting pass runs target analyzers and source generators again. It can therefore cause side effects beyond the normal build count. Profile only trusted targets that are safe to execute repeatedly. `--isolated` does not provide security isolation. See the [security policy](../SECURITY.md#trust-boundary-for-profiled-targets).
