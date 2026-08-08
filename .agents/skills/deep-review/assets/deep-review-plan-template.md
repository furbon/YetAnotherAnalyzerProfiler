# DeepReview Plan: <review target>

## Status and invocation

- State: Discovery
- Created / updated: YYYY-MM-DD
- Explicit invocation: `<quote or command that invoked DeepReview>`
- Base branch and commit: `<branch>` / `<commit>`
- Work branch: `agent/<name>`
- Approval and integration authority: `<review/edit/commit/merge/push/release boundaries>`

## Objective and completion outcome

Describe the user-visible quality outcome, why DeepReview is appropriate, and the
evidence required before completion.

## Repository and change context

Record architecture, lifecycle, target platforms, shipped artifacts, current diff,
recent history, active instructions, local-only evidence, and baseline Git state.

## Scope and boundaries

### Included

- Exact code, products, workflows, artifacts, documentation, platforms, and history.

### Excluded

- Explicit exclusions and why they cannot hide an in-scope risk.

### Authority boundaries

- External writes, destructive actions, production systems, push/release authority,
  credentials, and approvals.

## Adaptive review configuration

| Axis | Why it applies | Failure classes and boundaries | Reviewer persona | Target |
| --- | --- | --- | --- | --- |
| `<axis>` | `<evidence>` | `<owned risks>` | `<independent mandate>` | `>= 9.5` |

- Reviewer topology and parallelism: `<count, independence, fallback>`
- Cross-check assignments: `<high-risk boundaries reviewed by more than one axis>`
- Numeric threshold: `<per-axis threshold>`
- Non-numeric gate: zero unresolved blockers and unmitigated critical/high findings.
- User confirmations or justified inferred defaults: `<decisions>`

## Product and repository invariants

- List behavior, compatibility, performance, privacy, platform, documentation,
  testing, packaging, and workflow invariants that fixes must preserve.

## Baseline evidence

| Check | Command or method | Result | Evidence / limitation |
| --- | --- | --- | --- |
| Git state | `git status` / history | Pending | |
| Canonical verification | `<repository command>` | Pending | |
| Format / static guards | `<commands>` | Pending | |
| Runtime / UI / artifact checks | `<commands or inspection>` | Pending | |

## System and risk map

Map entry points, control/data flow, persistence, external processes/services,
failure and cancellation paths, user interfaces, release flow, and trust boundaries.

## Finding ledger

| ID | Severity | Axis | Evidence and impact | Required remediation | State | Verification |
| --- | --- | --- | --- | --- | --- | --- |
| `<ID>` | `<level>` | `<axis>` | `<path/line/run>` | `<testable outcome>` | Open | `<check>` |

Valid states: Open, Fixed, Verified, Rejected with evidence, Accepted residual risk.

## Remediation and re-review cycles

### Cycle 1

1. [ ] Collect independent findings without cross-contamination.
2. [ ] Reconcile duplicates and disagreements in the ledger.
3. [ ] Implement cohesive root-cause fixes and regression guards.
4. [ ] Run focused and proportional regression checks.
5. [ ] Give the current tree to independent reviewers for fresh verification.
6. [ ] Record scores, blockers, gaps, and the next cycle decision.

Add cycles until every completion gate passes. Never delete earlier evidence.

## Implementation and documentation plan

1. [ ] Ordered changes with exact paths, contracts, compatibility, and tests.
2. [ ] Documentation/help/API synchronization.
3. [ ] CI, packaging, migration, security, and release consequences.
4. [ ] Dependency assessment or explicit `No dependency changes`.

## Verification matrix

| Area | Positive, negative, failure, cancellation, concurrency, scale, or platform case | Command/method | Expected result |
| --- | --- | --- | --- |
| `<area>` | `<scenario>` | `<command>` | `<outcome>` |

Include canonical verification, format, guards, supported platforms/frameworks,
runtime UI where applicable, artifact inspection, links/docs, and repository hygiene.

## Final scorecard

| Axis | Reviewer | Initial score | Final score | Blocker status | Evidence for final score |
| --- | --- | --- | --- | --- | --- |
| `<axis>` | `<persona>` | Pending | Pending | Pending | |

Completion requires every applicable final score to meet the configured target and
all non-numeric gates to pass.

## Git and delivery plan

1. [ ] Verify the exact base and create the authorized work branch.
2. [ ] Review the full diff and status; exclude local/private artifacts.
3. [ ] Complete pre-commit canonical and proportional verification.
4. [ ] Commit intentionally under repository rules.
5. [ ] Merge, push, publish, or deploy only within explicit authority.
6. [ ] Run the required post-integration verification and branch cleanup.

## Decision log

| Date | Decision | Evidence and rationale |
| --- | --- | --- |
| YYYY-MM-DD | `<decision>` | `<reason>` |

## Completion report evidence

- Reviewed scope:
- Final scores and reviewers:
- Blocker/high finding status:
- Major remediations:
- Verification commands and results:
- Commit/integration/release state:
- Genuine residual risks or unverified boundaries:
