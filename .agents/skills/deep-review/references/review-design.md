# DeepReview design reference

Use this reference to configure a DeepReview from repository evidence. Keep the
review broad enough to expose cross-boundary failures while avoiding axes that add
only duplicate opinions.

## Scope and timing

Classify the request before selecting reviewers:

| Situation | Default scope | Typical reviewer count |
| --- | --- | --- |
| Focused risky change | Changed subsystem plus callers, tests, docs, operational boundary | 2-3 |
| Cross-cutting feature or refactor | Full affected data/control flow and release surfaces | 3-4 |
| Release readiness or OSS audit | Entire repository, artifacts, automation, docs, user workflows | 3-6 |
| Incident or regression | Failure path, adjacent invariants, observability, prevention controls | 3-4 |
| Architecture decision | Current implementation, alternatives, migration, compatibility, operations | 3-5 |

Run the strongest review after the implementation is testable and before irreversible
release or integration. For a large effort, use an early discovery pass to shape the
plan and a fresh final pass to validate the completed tree.

## Axis selection

Choose axes from the actual risk surface. Combine tightly coupled topics when one
reviewer can cover them without losing depth; split an axis when it needs different
expertise or evidence.

| Evidence in scope | Candidate axes |
| --- | --- |
| Async, threads, processes, large data | Correctness; concurrency/cancellation; memory/performance; resource lifetime |
| Public APIs, persistence, schemas | Architecture/API; compatibility/migration; data integrity |
| CLI, GUI, web, accessibility | UX/accessibility; interface parity; responsiveness; error recovery |
| OSS release | Documentation/community; licensing/supply chain; packaging/reproducibility; support/security disclosure |
| CI, build, tooling, agents | Test reliability; portability; automation/guardrails; reproducibility |
| Auth, secrets, untrusted inputs | Security/privacy; threat model; dependency/supply-chain risk |
| Domain-specific algorithms | Domain correctness; numerical/measurement validity; scale behavior |

Repository-wide reviews should normally include these families, adapted to the
product: runtime/correctness, user/OSS contract, and quality/release assurance. They
are starting families, not mandatory fixed labels.

For each axis, define:

- The distinct failure classes it owns.
- Files, workflows, artifacts, and platforms it must inspect.
- Invariants and adversarial scenarios it must challenge.
- Evidence needed for a 9.5 score.
- Boundaries it must cross-check with another reviewer.

Drop an axis if it is inapplicable and record why. Add an axis whenever a material
risk would otherwise have no owner.

## Persona construction and independence

Give each reviewer a concrete adversarial mandate, for example:

- Runtime skeptic: races, deadlocks, cancellation loss, leaks, unbounded memory,
  partial failure, corrupt state, process lifetime, or misleading measurements.
- OSS maintainer: first-use clarity, platform promises, licensing, contribution and
  security paths, CLI/GUI/API contract parity, and documentation drift.
- Release quality engineer: deterministic builds, lock files, matrix coverage,
  flaky or shallow tests, artifact content, provenance, and rollback readiness.
- Security/privacy reviewer: trust boundaries, secrets, unsafe inputs, dependency
  provenance, disclosure, and data retention.
- Architecture adversary: coupling, extension points, compatibility, ownership,
  error contracts, and migration complexity.
- UX/accessibility adversary: state transitions, responsiveness, cancellation,
  keyboard/screen-reader behavior, visual states, and actionable diagnostics.

Do not ask reviewers to endorse the plan. Ask them to falsify readiness. Keep them
read-only and isolated from earlier scores until they have produced their own
findings. When practical, change reviewer persona or context between the initial and
final pass to reduce confirmation bias.

Every reviewer response must contain:

1. Findings ordered by severity with exact evidence.
2. Areas inspected and important boundaries not inspected.
3. Required verification for each proposed fix.
4. A score with explicit reasons preventing a higher score.
5. A clear blocker/no-blocker conclusion.

## User confirmation policy

Ask one compact question set before expensive execution when any of these is true:

- The named target could mean either a change set or the whole repository.
- Reviewer count changes expected cost materially.
- Multiple plausible axes would produce different coverage.
- The requested threshold, release gate, or integration authority is unclear.
- Required external systems, production data, credentials, paid services, or
  destructive actions are outside existing authority.

Present a recommended configuration first. Do not ask the user to choose facts that
repository inspection can establish. Skip confirmation when the user already chose
the configuration or explicitly requested autonomous execution with an unambiguous
scope.

## Finding and remediation rules

Use stable finding IDs. Severity is based on plausible impact and evidence:

- Blocker: unsafe to ship or complete; data loss, critical security, core incorrect
  result, unrecoverable failure, or missing required release path.
- High: serious correctness, reliability, compatibility, privacy, or operational
  defect likely enough to require remediation before completion.
- Medium: material quality gap with bounded impact and a concrete fix.
- Low: worthwhile hardening or clarity improvement that is not a release gate.

Track each finding as open, fixed, verified, rejected-with-evidence, or explicitly
accepted residual risk. Scores never close findings. Fix root causes and add a guard
or regression test whenever the invariant can be automated.

## Scoring rubric

Score each applicable axis independently; do not average away a weak axis.

| Score | Evidence standard |
| --- | --- |
| 10.0 | Exceptional evidence, no material known gap, strong prevention controls, and only negligible bounded uncertainty |
| 9.5-9.9 | Release-ready, no blocker/high issue, comprehensive relevant evidence, minor non-material uncertainty only |
| 9.0-9.4 | Strong but at least one material gap, incomplete boundary, or insufficient prevention evidence remains |
| 8.0-8.9 | Multiple material weaknesses or one high-risk uncertainty remains |
| Below 8 | Significant correctness, security, reliability, operability, or release weakness |

The default gate is `score >= 9.5` for every applicable axis, zero unresolved blockers,
and zero unmitigated critical/high findings. An explicit user threshold may
change the numeric gate but cannot make a known blocker disappear.

## Evidence and stopping rules

Prefer primary evidence: source, tests, actual command output, rendered UI, produced
artifacts, dependency metadata, and platform execution. Static assertions do not
replace runtime validation where runtime behavior is at risk.

Stop only when:

- Findings and cross-axis disagreements are resolved.
- Final reviewers inspected the remediated tree rather than an earlier diff.
- Every axis meets its threshold with traceable evidence.
- Canonical and proportional verification is green.
- The final status/diff and delivery state match the plan.

If a necessary check cannot run, do not silently discount it. Record the exact
boundary, pursue safe alternatives, and ask the user when the remaining uncertainty
materially affects completion.
