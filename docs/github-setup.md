# GitHub Setup and Operations

This guide records the authoritative GitHub configuration and recurring release procedure for YAAP. It is not a task log. Re-check settings against this document after repository transfer, workflow changes, or a new release series.

## Workflows

| Workflow | Trigger | Purpose |
| --- | --- | --- |
| CI | Push to `main`, `develop/**`, `agent/**`; pull request; manual | Repository verification on .NET 8/10 and supported hosts plus release-package smoke |
| Dependency Review | Pull request | Reject newly introduced dependencies with known moderate-or-higher vulnerabilities |
| Release | `v*.*.*` tag or confirmed manual run from `main` | Re-verify, build, attest, publish NuGet and GitHub Release |

Dependabot checks NuGet and GitHub Actions weekly and targets the active `develop/v<version>` branch. `./eng/build.ps1 start-version --version <version>` updates those targets together with the canonical product version.

## 1. Local and remote preparation

Enable the tracked commit guard in every clone:

```powershell
./eng/install-git-hooks.ps1
```

For a new remote only:

```powershell
git remote add origin https://github.com/<owner>/<repository>.git
git push -u origin develop/v<version>
git push -u origin main
```

The repository default branch is `main`. The initial bootstrap of a repository with no Actions history may temporarily require the current `develop/v<version>` as default so required check names become selectable. Return the default to `main` immediately after the first successful CI run.

## 2. Repository settings

Under **Settings → General**:

- Visibility: Public
- Default branch: `main`
- Issues: enabled
- Wiki: disabled; tracked `docs/` is authoritative
- Discussions: disabled until support volume justifies a separate channel
- Merge commits: enabled
- Squash and rebase merges: disabled
- Automatically delete head branches: optional; do not enable if a versioned develop branch must remain after its release PR

The About description should explain that YAAP profiles Roslyn analyzers and source generators from compiler-reported metrics. Maintain focused topics such as `dotnet`, `roslyn`, `analyzer`, `source-generator`, `profiler`, and `wpf`.

## 3. Actions policy

Under **Settings → Actions → General**:

- Allow GitHub-owned actions and the explicitly selected `NuGet/login@*`.
- Require actions to be pinned to a full-length commit SHA.
- Set default workflow token permission to read repository contents.
- Do not allow Actions to create or approve pull requests.
- Do not expose release credentials to pull-request workflows.

Every external action reference in tracked workflows uses a reviewed full commit SHA with a version comment. Update the SHA through a normal dependency pull request and review upstream provenance, maintenance, license, advisories, runtime requirements, and changes before merge.

CI jobs set timeouts and concurrency cancellation. Release jobs use non-canceling version-specific concurrency because publication must not race.

## 4. Rulesets

### Protect main

Target `refs/heads/main` and enable:

- Prevent deletion
- Block force pushes
- Require a pull request
- Require all review conversations to be resolved
- Allow merge commits only
- Require branches to be up to date
- Require these status checks:
  - `Verify Linux / net8.0`
  - `Verify Linux / net10.0`
  - `Verify Windows / net8.0`
  - `Verify Windows / net10.0`
  - `Verify macOS / net10.0`
  - `Package / linux-x64`
  - `Package / win-x64`
  - `Package / osx-x64`
  - `Package / osx-arm64`
  - `Dependency Review`

A solo maintainer cannot satisfy an independent approval requirement, so the required approving-review count remains zero. Raise it to one when another active maintainer can review without bypassing the rule.

### Protect develop branches

Target `refs/heads/develop/**` and prevent deletion and force pushes. Tracked work is committed on `agent/*` and integrated with a non-fast-forward merge.

### Protect release tags

Target `refs/tags/v*.*.*` and prevent deletion and force pushes. A version tag must never be moved or reused.

## 5. Security and dependencies

Enable:

- Dependency graph
- Dependabot alerts
- Dependabot security updates
- Secret scanning
- Secret scanning push protection
- CodeQL default setup for C# and GitHub Actions
- Private vulnerability reporting

Review dependency changes through the pull-request dependency view and the required Dependency Review check. Dependabot reports existing vulnerable dependencies; Dependency Review blocks vulnerable additions before merge. License and maintenance review remains manual and is recorded in the active task plan.

Keep the conduct-report address separate from vulnerability reporting. Test the private advisory link from `SECURITY.md` while signed out of privileged maintainer views.

## 6. Release environment and NuGet

Create the GitHub Environment `release`:

- Restrict deployment branches/tags to the release policy.
- Require the designated maintainer's approval.
- Store only the nuget.org profile name as `NUGET_USER`.
- Do not store a long-lived NuGet API key.

Configure nuget.org Trusted Publishing for:

- Repository owner and repository name
- Workflow `.github/workflows/release.yml`
- Environment `release`

The `NuGet/login` action exchanges GitHub OIDC for a short-lived credential. Package metadata must contain the public repository URL and release commit.

## 7. Start the next version

From a clean, synchronized `main`:

1. Create and push `develop/v<version>`.
2. Create `agent/<change-name>` from that branch.
3. Run:

```powershell
./eng/build.ps1 start-version --version <major.minor.patch>
```

4. Curate the matching CHANGELOG entry, `.github/release-notes/v<version>.md`, and `.github/release-notes/v<version>.ja.md`.
5. Update the supported series in `SECURITY.md` only when the major/minor series changes.
6. Confirm `eng/Version.props` remains the only product-version source and the fourth assembly/file component remains zero.

Evergreen installation examples omit `--version`. Historical changelog and release-note versions are intentionally retained.

## 8. Release candidate

Before the pull request to `main`:

- Complete `./eng/build.ps1 verify` on Windows.
- Confirm required CI jobs pass on Windows, Linux, and macOS.
- Confirm Dependency Review passes.
- Review all user-visible English documents and selected Japanese translations.
- Verify package metadata/readme, archive layout, checksums, and provenance configuration.
- Confirm the release notes start with `# YAAP v<version>` and the CHANGELOG has a dated matching entry.
- Merge the agent branch into `develop/v<version>` with `--no-ff` and run the short post-merge verification.
- Push the develop branch and open a pull request to `main`.
- Merge only after every required check is green and review threads are resolved.

## 9. Publication

Preferred operation:

1. Open **Actions → Release → Run workflow**.
2. Select `main`.
3. Enter `v<version>` as `release_tag`.
4. Set publication confirmation exactly as requested by the workflow.
5. Leave `recovery_run_id` empty for a normal release.
6. Wait for validation and producer jobs.
7. Inspect the pending `release` environment deployment and approve only the verified commit/version.
8. Let the workflow create or verify the tag, publish NuGet through Trusted Publishing, verify published bytes, upload checksums/assets, and publish the draft GitHub Release.

Pushing a `v<version>` tag to the verified `main` commit enters the same workflow, but the confirmed manual path is preferred because it creates the tag after validation.

## 10. Post-publication verification

- Confirm the GitHub Release is public and has the expected archives, nupkg, and `SHA256SUMS.txt`.
- Confirm every release asset digest matches the checksum file.
- Verify producer provenance:

```powershell
gh attestation verify YetAnotherAnalyzerProfiler.Tool.<version>.nupkg --repo <owner>/<repository>
gh attestation verify yaap-win-x64.zip --repo <owner>/<repository>
```

- Download the package from the public NuGet flat-container endpoint and compare it with the validated candidate after removing NuGet repository signatures through the repository verifier.
- Install from nuget.org into a temporary tool path and run `yaap version`, `yaap help`, and a minimal trusted profile.
- Confirm NuGet shows the English description, embedded readme, license, repository, and expected framework assets.
- Confirm badges resolve to the new release/version after service caches update.
- Close the version branch only after the release and post-publication checks are complete.

## Recovery

A recovery run must reference a completed failed manual Release run for the same tag and `main` commit. It may reuse only that run's validated artifacts. The workflow rejects a public release, mismatched tag, mismatched artifact set, or NuGet bytes that differ from the candidate.

If NuGet is public but GitHub Release is not, recover the draft from identical artifacts and publish after verification. If identical bytes cannot be reproduced, stop, unlist the package, record the incident, and publish a new SemVer fix. Never overwrite or reuse a published version.
