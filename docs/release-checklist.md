# Release Checklist

Do not publish a release candidate to NuGet or GitHub Releases until every applicable condition is satisfied.

## Hosting configuration

- Set a public default branch and branch protection requiring canonical CI.
- Prevent updates/deletion of `v*.*.*` tags and configure the `release` environment with a reviewer and `NUGET_USER`.
- Restrict nuget.org Trusted Publishing to the source repository, `release.yml`, and `release` environment. Do not store a long-lived `NUGET_API_KEY` in GitHub.
- Enable GitHub Private vulnerability reporting or an equivalent verified private route.
- Maintain a verified private Code of Conduct contact that is separate from security reporting.
- Run self-managed GitLab runners in dedicated temporary workspaces and do not expose protected variables to unprotected pipelines.
- Verify that `RepositoryUrl`, package project URL, and nuspec commit match the source repository and tag commit.
- Follow the [GitHub setup and operations guide](github-setup.md) for initial and recurring settings.

## Artifacts

1. Match `eng/Version.props`, tag, dated CHANGELOG entry, `.github/release-notes/v<version>.md`, and its `.ja.md` translation.
2. Pass canonical verification on every operating system and both TFMs. GUI verification includes STA startup and required light/dark inspection.
3. Pack NuGet twice and require byte-for-byte equality.
4. Extract each archive on a runner for its CPU and start `cli/yaap` as documented.
5. Create producer attestations for the package and every archive plus `SHA256SUMS.txt`.
6. Attach verified artifacts only to a draft using the version-specific release notes. Never modify an existing public release.

The preferred publication path is **Actions → Release → Run workflow** on `main`, supplying the tag and explicit publication confirmation. Approve the environment only after verification. An existing version-tag push enters the same verification and publication path.

Users can verify provenance with GitHub CLI:

```sh
gh attestation verify yaap-linux-x64.zip --repo <owner>/<repository>
gh attestation verify YetAnotherAnalyzerProfiler.Tool.<version>.nupkg --repo <owner>/<repository>
```

Replace placeholders with the source repository and released SemVer.

## Retry and recovery

When the same NuGet version exists, continue only if its bytes have the same SHA-256 as the candidate. Download and compare it again after push; do not publish GitHub Release on mismatch. The workflow rejects an existing public release. A failed draft upload can be restored from the same workflow run's verified artifacts.

If NuGet was published but GitHub Release was not, do not invent a new build under the same version. Recover the draft from the same tag commit and identical-digest artifacts, restore producer attestations, verify, and publish. If the digest cannot be reproduced, stop publication, unlist the NuGet package, record the incident, and release a new SemVer fix.
