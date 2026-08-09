# DeepReview Guide

DeepReview is an explicitly invoked repository audit and remediation protocol, separate from ordinary code and design review. It repeats independent adversarial review, remediation, verification, and re-review to improve OSS quality, latent risk, ambiguity, tests, documentation, and release processes together.

It is expensive in time and model use, so it never starts automatically or implicitly. Requests such as “review this diff,” “check this PR,” or “review the design” remain ordinary scoped reviews.

## Invocation

For Codex, invoke the repository skill explicitly:

```text
$deep-review audit and remediate the complete repository before the v0.2.0 release
```

For another agent, name the protocol:

```text
Run DeepReview against this authentication change and its release process.
```

These requests do not invoke DeepReview:

```text
Review this diff.
Check the design.
Review the code before release.
```

## Adaptive configuration

The agent examines scope, architecture, product lifecycle, platforms, release timing, tests, CI, and documentation, then selects:

- Scope: a change, subsystem, release, incident, design decision, or repository
- Axes: runtime, correctness, concurrency, performance, API, compatibility, security, UX, OSS, testing, portability, CI, packaging, reproducibility, or other applicable risks
- Personas: independent adversarial reviewers intended to falsify each selected axis
- Parallelism: based on breadth, risk, and available subagents
- Gates: per-axis scores, blockers, required tests, and integration scope

For a repository or release audit, defaults are at least three independent reviewers, at least 9.5/10 on every applicable axis, zero unresolved blockers or unmitigated critical/high findings, independent re-review of the remediated tree, and green canonical checks. A high average cannot hide a weak axis.

Axes and reviewer counts are adaptive. “Runtime, OSS, quality” is an example, not a fixed configuration.

## When the agent asks

The agent asks focused questions when these decisions materially change cost, time, scope, or the completion gate:

- Change-only scope versus the whole repository
- Axes and independent reviewer count
- A score threshold other than 9.5 or release-specific mandatory gates
- Commit, develop integration, push, or publication authority
- External services, credentials, production environments, or destructive operations

When scope is clear and the user says “fully autonomous,” “GO,” or equivalent, the agent can infer reasonable defaults and record them in the plan.

## Execution cycle

1. Inspect instructions, Git state, scope, canonical commands, and release boundaries.
2. Create a dedicated plan with axes, personas, scores, authority, tests, and stop conditions.
3. Establish pre-change build, test, format, guard, UI, and artifact baselines.
4. Run independent adversarial reviewers in parallel when available and consolidate evidence-backed findings.
5. Remediate root causes and add failure, cancellation, concurrency, cleanup, scale, and compatibility regression coverage as applicable.
6. Return the current remediated tree to independent reviewers and include newly introduced defects.
7. Repeat remediation and re-review until every quantitative and non-quantitative gate passes.
8. Finish canonical verification, complete diff review, authorized commit/integration, and post-integration checks.

Without subagents, execute the same personas as separate evidence reviews and disclose the limitation.

## Artifacts and authority

The following development assets are referenced from the source repository and are not included in executable-only distributions:

- Protocol: `.agents/skills/deep-review/SKILL.md`
- Axes, personas, scoring: `.agents/skills/deep-review/references/review-design.md`
- Plan template: `.agents/skills/deep-review/assets/deep-review-plan-template.md`
- Actual plan: `.docs_agent/plans/YYYY-MM-DD-<task>.md` (local and untracked)

The plan records explicit invocation, review configuration, baselines, finding ledger, remediation cycles, verification, final score, and Git/release state. DeepReview does not claim mathematical absence of defects. It improves the inspected scope until no known blocker remains and discloses unverified boundaries or residual risk.
