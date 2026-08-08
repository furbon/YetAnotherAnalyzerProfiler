# YAAP engineering instructions

These rules apply to every repository change made by Codex or GitHub Copilot.

## Product invariants

- Keep `Yaap.Core` and `Yaap.Cli` cross-platform. WPF code belongs only in `Yaap.Gui`.
- Support .NET 8 and .NET 10. Do not remove either target without an approved design change.
- Keep the product offline. Only the analyzed target's ordinary `dotnet restore` may contact its configured feeds.
- Profile analyzers and source generators using compiler-reported totals. Never infer execution time for an individual generated file.
- Stream binlogs and generated outputs; do not load an entire large binlog or output tree into memory.
- Keep build, parsing, history, comparison, and export operations asynchronous and cancelable from the GUI.
- Preserve failed, partial, and canceled runs with stable error codes and actionable diagnostics.

## Repository rules

- `eng/Version.props` is the only version source. SemVer is used and the fourth assembly/file component remains zero.
- Human documentation and GUI text are Japanese. Code identifiers and agent instructions are English.
- Source text is UTF-8 with BOM, CRLF, and space indentation. Shell scripts are the executable LF exception.
- Package versions are centralized. Before adding a package, record provenance, maintenance, adoption, license, vulnerabilities, transitive dependencies, compatibility, and alternatives in the active plan.
- Do not commit machine-specific paths, credentials, local smoke-test names/results, build outputs, histories, packages, binlogs, or temporary files.
- Keep `AGENTS.md`, `.github/copilot-instructions.md`, and this canonical file byte-for-byte identical.

## Required workflow

1. Read repository instructions and inspect Git state before editing.
2. Keep changes scoped and add regression coverage for behavior changes.
3. Run `./eng/build.ps1 verify` on Windows or `./eng/build.sh verify` on macOS/Linux.
4. Confirm `dotnet format --verify-no-changes` and repository guards pass.
5. Review the full diff and Git status before committing with Conventional Commits.

Do not weaken tests, guards, lock-file behavior, warnings-as-errors, or platform coverage to make validation pass.
