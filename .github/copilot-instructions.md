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
- Keep `CHANGELOG.md` as a curated user-facing release record. Include material
  capabilities, compatibility, security/privacy facts, and known limitations;
  exclude task-level fixes, refactorings, tests, CI work, and commit-log detail.
- Whenever a version's `CHANGELOG.md` content changes, update its Japanese,
  GitHub-ready `.github/release-notes/v<version>.md` in the same task and keep the
  two documents consistent. Git commits, pull requests, and local task plans are
  the sources for detailed engineering history; do not maintain a separate
  task-by-task developer changelog unless an approved design explicitly requires it.
- Human documentation and GUI text are Japanese. Code identifiers and agent instructions are English.
- Source text is UTF-8 with BOM, CRLF, and space indentation. Shell scripts and
  open-agent skill files under `.agents/skills/` are portable UTF-8 without BOM and
  LF exceptions. NuGet-owned `packages.lock.json` files are UTF-8 without BOM and
  CRLF so an ordinary restore never dirties the worktree.
- Package versions are centralized. Before adding a package, record provenance, maintenance, adoption, license, vulnerabilities, transitive dependencies, compatibility, and alternatives in the active plan.
- Do not commit machine-specific paths, credentials, local smoke-test names/results, build outputs, histories, packages, binlogs, or temporary files.
- Before concrete tracked work, read `.docs_agent/WORKFLOW.md` when present and create its required local task plan. Critical rules remain binding even when their source is Git-ignored.
- Agents work only on `agent/<feature-or-change-name>` branches created from the current user-prepared `develop/v...` branch. Carry authorized dirty work to the agent branch immediately; never make agent commits directly on `main`, `master`, or `develop/*`, and never push unless explicitly requested.
- Keep the repository pre-commit guard enabled with `./eng/install-git-hooks.ps1`. It rejects direct commits outside `agent/*`, permits the required merge commit into `develop/*`, and requires explicit override for an authorized main-branch merge.
- Treat successful WPF compilation as insufficient runtime validation. GUI changes must retain the STA startup smoke test that creates, shows, lays out, and closes `MainWindow`.
- WPF visual changes require rendered light- and dark-theme inspection of every affected tab and state. Compilation, source-string assertions, and a startup-only smoke test are not visual validation.
- Keep `AGENTS.md`, `.github/copilot-instructions.md`, and this canonical file byte-for-byte identical.

## Explicit DeepReview workflow

- DeepReview is the repository's expensive, highest-rigor adversarial review and
  remediation workflow. Start it only when the user explicitly invokes
  `$deep-review`, says `DeepReview`, or unmistakably requests this named workflow.
- Never infer DeepReview from a generic code, design, change, PR, quality, or release
  review request. Ordinary reviews remain ordinary unless the user explicitly opts in.
- When explicitly invoked, read and follow `.agents/skills/deep-review/SKILL.md` and
  its required reference. Adapt axes, independent reviewer personas, parallelism,
  score threshold, and verification to the inspected risk; ask focused questions
  only when a choice materially changes cost or coverage.
- The default completion gate is at least 9.5/10 on every applicable axis, no
  unresolved blocker or unmitigated critical/high finding, green canonical checks,
  and independent re-review of the remediated tree. See `docs/deep-review.md`.

## Required workflow

1. Read tracked and local repository instructions, create/update the required task plan, and inspect Git state before editing.
2. Run `./eng/install-git-hooks.ps1`, then create or switch to the required `agent/*` branch from the intended `develop/v...` base before tracked edits; record the base branch and commit.
3. Keep changes scoped and add regression coverage for behavior changes.
4. For `Yaap.Gui` changes, run `Yaap.Gui.Tests` in Debug on Windows, confirm the `MainWindow` startup smoke, and inspect rendered light/dark output for every affected tab and state.
5. Run `./eng/build.ps1 verify` on Windows or `./eng/build.sh verify` on macOS/Linux.
6. Confirm `dotnet format --verify-no-changes` and repository guards pass. Windows verification must cover GUI startup on both target frameworks.
7. Review the full diff and Git status before committing with Conventional Commits on the agent branch.

Do not weaken tests, guards, lock-file behavior, warnings-as-errors, or platform coverage to make validation pass.
