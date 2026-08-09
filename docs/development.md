# Development Guide

## Prerequisites

- .NET SDK 8.0.423 and 10.0.302; verification uses both
- Windows to build and run the WPF GUI
- Git; the product itself does not require Git

`global.json` and `eng/toolchain.json` pin verification SDKs. Core and CLI target `net8.0;net10.0`; GUI targets `net8.0-windows;net10.0-windows`. `eng/Version.props` is the only product-version source, and assembly/file version component four remains zero.

To start a release version, first create `develop/v<version>` from the intended `main`, create an `agent/*` branch from it, and run:

```powershell
./eng/build.ps1 start-version --version <major.minor.patch>
```

```sh
./eng/build.sh start-version --version <major.minor.patch>
```

The command validates the branch and clean worktree, then updates `eng/Version.props` and both Dependabot target branches from one input. Maintainers still curate the matching CHANGELOG entry, `.github/release-notes/v<version>.md`, its `.ja.md` translation, and the supported series in `SECURITY.md` when a minor series changes. Historical version references are not rewritten.

## Common commands

```powershell
./eng/build.ps1 verify
./eng/build.ps1 pack --framework net10.0
./eng/build.ps1 publish --runtime win-x64 --framework net10.0
```

```sh
./eng/build.sh verify
./eng/build.sh pack --framework net10.0
./eng/build.sh publish --runtime linux-x64 --framework net10.0
```

`verify` runs repository guards, locked restore, formatting, warnings-as-errors builds, Core/CLI/GUI tests, real analyzer/generator integration, and local NuGet feed and lock-file tests. GUI tests run in Debug on Windows for both target frameworks and include the STA startup smoke.

`pack` builds the CLI as the `YetAnotherAnalyzerProfiler.Tool` .NET tool, checks an exact content allowlist, installs it into a temporary tool path, and runs `version` and `help`. Net10 verification includes this check. `publish` performs locked publish with an RID-specific temporary lock, validates the content allowlist and notices, starts the CLI, and on Windows shows and closes the GUI.

Centralize every new NuGet package. Record provenance, maintenance, adoption, license, vulnerabilities, transitive dependencies, target-framework compatibility, and alternatives in the task plan. Every production lock-file package/version must appear in `THIRD-PARTY-NOTICES.txt`; repository guards enforce synchronization.

## GitHub releases

Pull requests are expected to pass GitHub Actions verification across all supported operating systems and TFMs before branch protection permits a merge. New pushes cancel obsolete runs, and every job has a timeout. Each setup job installs only its requested SDK below `RUNNER_TEMP`. The .NET 8 lane uses `obj/sdk8.packages.lock.json` to prevent SDK-specific lock-graph representation from modifying tracked locks; .NET 10 verifies tracked locks and Visual Studio rebuild reproducibility.

For a release, update `eng/Version.props` through `start-version` and complete normal PR verification. The preferred release path manually starts the GitHub Release workflow from `main` with explicit confirmation. After verification, the workflow creates the matching `vX.Y.Z` tag on the verified commit. Pushing an existing version tag enters the same path.

`.github/workflows/release.yml` checks the tag against the single version source, re-verifies .NET 8/10 on Windows, Linux, and macOS, and builds the NuGet tool plus four self-contained archives. The publication job uses the protected `release` environment. NuGet Trusted Publishing is restricted to this repository, workflow, and environment and exchanges GitHub OIDC for a short-lived credential; no long-lived API key is stored. NuGet and GitHub Release publication wait for all validation. Existing public releases are not modified, existing NuGet bytes must match a candidate, producer provenance attestations are created, and the draft contains the same verified artifacts before publication. See the [release checklist](release-checklist.md) and [GitHub operations guide](github-setup.md).

GitLab CI verifies both TFMs on Linux and uses self-managed runners tagged `windows` and `macos` for platform coverage. Runners need isolated temporary workspaces, required SDKs, PowerShell, and an interactive WPF session. GitHub's release workflow is authoritative. Publishing from GitLab requires a separately approved design with equivalent protected tags, environment approval, digest verification, and producer provenance.

## Files and quality

Source text is UTF-8 with BOM, CRLF, and spaces. Executable `.sh` files and files below `.agents/skills/` are UTF-8 without BOM and LF. NuGet-owned `packages.lock.json` files are UTF-8 without BOM and CRLF. `AGENTS.md`, `.github/copilot-instructions.md`, and `eng/agent-instructions.md` must be identical. The shared harness checks these rules plus local paths, secrets, artifacts, duplicate version sources, documentation localization contracts, and release configuration.
