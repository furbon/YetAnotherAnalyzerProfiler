# Security Policy

[日本語](docs/ja/security.md)

## Supported versions

| Version | Security fixes |
| --- | --- |
| 0.1.x | Supported |
| Earlier than 0.1.x | Not supported |

Consult the latest [changelog](CHANGELOG.md) for current support information.

## Reporting a vulnerability

Do not disclose vulnerability details, exposed secrets, expanded arbitrary-code execution, or release tampering in a public issue. For GitHub, use this repository's [Private vulnerability reporting](https://github.com/furbon/YetAnotherAnalyzerProfiler/security/advisories/new). If an authorized mirror is hosted elsewhere, use that host's verified private security-reporting route.

If no private route can be found, open a public issue containing only a request for a private security contact. Do not include reproduction steps, secrets, or vulnerability details. This document does not name an unverified email address or URL as a security route.

Repository maintainers must enable and verify a private reporting route before publication and ensure that this policy and package metadata reach it. A release must not be published without that route.

Include as much of the following as practical:

- Affected YAAP version, operating system, TFM, and RID
- Vulnerability category and expected impact
- Minimal reproduction steps or a sanitized fixture
- Known mitigations
- Information that must remain private until coordinated disclosure

Maintainers will assess impact, reproducibility, remediation, and disclosure timing. Please keep the vulnerability and its fix private until coordination is complete.

## Trust boundary for profiled targets

YAAP is not a security sandbox. Profiling runs:

- `dotnet restore`, `dotnet clean`, and `dotnet build`
- MSBuild tasks and targets defined by the target
- Analyzers and source generators referenced by the target
- Replayed C# compiler invocations used to obtain compiler-reported metrics

These processes run with the same user permissions as YAAP. They can read and write arbitrary files, start processes, access environment variables or credentials, communicate over the network, and cause other side effects. Replaying analyzers and source generators can cause more side effects than an ordinary build. Do not profile an untrusted repository with YAAP.

`--isolated` and the GUI's isolated-output option use .NET's `--artifacts-path` to move standard build outputs away from the target. They do not restrict target code, custom-target writes, communication, or credential access. When isolation is required, create an external security boundary such as a disposable VM or container, a low-privilege account, and network controls.

## Meaning of offline operation

YAAP itself implements no telemetry, update check, or external API client. The target's restore can still contact configured feeds and credential providers. Its build, MSBuild tasks, analyzers, and source generators can also communicate according to their implementation. “Offline” does not guarantee that target code is disconnected.

To prohibit communication, enforce network isolation at the operating-system, VM, container, or firewall layer and provision required SDKs and packages in advance.

## History, binlogs, and exports

YAAP stores history locally and does not transmit it. History can contain:

- Absolute target paths and relative generated-file paths
- Git commit, branch, and dirty state
- Operating system, CPU count, SDK, and target frameworks
- Compiler arguments, MSBuild data, diagnostics, and target-originated logs
- Complete standard-output and standard-error logs from failed or canceled restore, clean, and build processes
- Binlogs that can expose private-feed names, user names, environment data, and other confidential information

Inspect history directories, binlogs, and JSON, CSV, or Markdown exports before sharing them. Remove confidential and personal information. Store history in a location that other users cannot read, and review retention and obsolete data regularly.

## Requirements for security fixes

A security fix must cover reproduction, failure and cancellation cleanup, affected TFMs and operating systems, release artifacts, and documentation. Do not weaken safeguards, warnings, validation, lock files, warnings-as-errors, or platform coverage to make a change pass.
