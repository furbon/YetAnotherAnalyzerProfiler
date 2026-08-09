# Contributing to YAAP

Bug reports, documentation improvements, tests, and implementation proposals are welcome. Submissions may be written in English or Japanese. Do not disclose security issues in public issues; follow the [security policy](SECURITY.md).

## Before starting a change

1. Read the [Code of Conduct](CODE_OF_CONDUCT.md).
2. Search existing issues and the [changelog](CHANGELOG.md).
3. Discuss large specification changes, compatibility changes, and new dependencies in an issue before implementation, including the goal and alternatives.
4. Read the repository's `AGENTS.md` and, when present, the local `.docs_agent/WORKFLOW.md`.

When a local copy does not identify its hosting URL, do not invent an issue tracker or contact. Use the route specified by the repository maintainer.

## Development environment

- .NET SDK 8.0.423 and 10.0.302, pinned by `global.json` and CI
- Git
- Windows for GUI builds, tests, and visual inspection

```powershell
./eng/install-git-hooks.ps1
./eng/build.ps1 verify
```

```sh
sh ./eng/build.sh verify
```

See the [development guide](docs/development.md) and [testing policy](docs/testing.md) for details.

## Branches and scope

- Create `agent/<change-name>` from the latest maintainer-prepared `develop/v...` branch. The `agent` prefix is the work-branch prefix accepted by the pre-commit guard; it does not mean that only automation may contribute.
- Do not commit directly to `main`, `master`, or `develop/*`.
- Do not include unrelated formatting, generated output, local paths, binlogs, history, or secrets.
- Use Conventional Commits.
- Follow the maintainer's authorization and procedure for pushes and merges.

## Implementation and documentation

- Keep `Yaap.Core` and `Yaap.Cli` cross-platform and keep WPF dependencies in `Yaap.Gui`.
- Preserve both .NET 8 and .NET 10.
- Keep build, parsing, history, comparison, and export operations asynchronous and cancelable.
- Do not load large binlogs or generated-output trees completely into memory.
- Update measurement documentation, CLI help, GUI text, README, usage guidance, and CHANGELOG with the behavior they describe.
- English documentation is authoritative. Update an official Japanese translation in the same change whenever its English source changes. GUI text remains Japanese; code identifiers and agent instructions are English.
- Follow `.editorconfig` and `AGENTS.md` for source formatting.

Before proposing a new NuGet package, record its source, maintenance, adoption, license, known vulnerabilities, transitive dependencies, target-framework support, and alternatives in the task plan. Package versions are centralized in `Directory.Packages.props`.

## Tests

Add regression coverage proportional to the change. Test invalid input, failure, partial results, cancellation, cleanup, scale, and applicable operating systems and TFMs, not only the success path. GUI changes require `Yaap.Gui.Tests` on Windows, the STA startup smoke test, and rendered inspection of every affected state in light and dark themes.

Before submitting:

```powershell
./eng/build.ps1 verify
dotnet format --verify-no-changes
git diff --check
git status --short
```

Use `./eng/build.sh verify` on macOS or Linux.

## Review information

Invoke [DeepReview](docs/deep-review.md) explicitly only when the change needs repeated independent adversarial reviews beyond an ordinary review. A normal review request does not start DeepReview.

Include:

- The purpose and user impact
- Compatibility, data-format, security, and performance effects
- Tests run and their results
- Light and dark visual inspection for GUI changes
- Any remaining limitation, its reason, and where it is documented
