---
name: deep-review
description: Run the repository's highest-rigor, broad adversarial review and remediation workflow only when the user explicitly invokes `$deep-review`, says `DeepReview`, or unmistakably requests the named DeepReview workflow. Never use it for a generic code review, design review, PR review, or an inferred quality need.
---

# DeepReview

Treat DeepReview as an expensive, explicit-only repository improvement program,
not as a synonym for ordinary review. Raise the repository to an evidence-backed,
clean, all-green state through independent review, remediation, and re-review.

## Establish the execution contract

1. Confirm that the current request explicitly invokes `$deep-review`, names
   `DeepReview`, or unmistakably requests this repository's named workflow. If it
   does not, do not apply this skill; perform only the ordinary requested review.
2. Read all repository and local agent instructions before taking task actions.
3. Read [references/review-design.md](references/review-design.md) completely before
   choosing scope, axes, reviewers, thresholds, or questions.
4. Inspect the repository, current change, active plan, Git state, recent history,
   product/release phase, and available verification commands.
5. Create the repository-required task plan. When no stricter template applies,
   copy [assets/deep-review-plan-template.md](assets/deep-review-plan-template.md)
   into the repository's local plan location and complete every section.
6. Record the explicit invocation, review scope, chosen axes, reviewer topology,
   target score, integration authority, and verification boundary in the plan.

Do not start tracked changes before satisfying the repository's approval, branch,
and planning rules. An explicit DeepReview invocation authorizes the review work the
user requested; it does not grant unrelated deployment, push, release, destructive,
or production authority.

## Configure adaptively

Infer a recommended configuration from the inspected evidence. Select axes because
they can expose materially different failure classes, not to meet a fixed count.
Prefer independent reviewers with non-overlapping primary mandates and deliberate
cross-checks at high-risk boundaries.

Use these defaults unless repository evidence or the user changes them:

- Review the user-named target; for a release-readiness request, cover the full
  repository, shipped artifacts, documentation, automation, and operator workflow.
- Use at least three independent reviewer personas for a repository-wide review.
- Require every applicable axis to reach at least 9.5/10.
- Require zero unresolved release blockers and zero unmitigated critical/high
  findings regardless of score.
- Repeat remediation and independent re-review until all gates pass.

Ask focused questions only when an answer materially changes cost, scope, coverage,
or integration. Provide a recommended default for review axes, reviewer count,
score threshold, and delivery boundary. If the user already requested full autonomy
and supplied a clear scope, record justified defaults and proceed without ceremony.

## Execute the review program

1. Run a baseline before edits: canonical build/test/format/guard commands, relevant
   release or UI checks, and repository hygiene inspection. Preserve exact evidence.
2. Map the system and change surface across code, tests, data, APIs, UI/CLI,
   documentation, dependencies, CI, packaging, deployment, and support boundaries.
3. Dispatch independent adversarial reviewers in parallel when the host supports
   subagents. Give each reviewer the same raw scope and repository evidence, its own
   mandate, and a read-only finding contract. Do not leak another reviewer's
   conclusions or the desired score.
4. When parallel agents are unavailable, run the same personas sequentially with
   fresh evidence passes and state this limitation in the final report.
5. Require every finding to include severity, exact evidence, impact, reproduction or
   reasoning, affected invariant, and a verifiable remediation criterion. Reject
   vague quality wishes and score-only opinions.
6. Reconcile overlaps and disagreements. Maintain one finding ledger in the task
   plan; never lose a finding merely because another reviewer did not reproduce it.
7. Implement cohesive fixes on the authorized work branch. Add proportional
   regression coverage for behavior, invalid input, failure, cancellation,
   concurrency, cleanup, scale, compatibility, portability, and UI runtime behavior.
8. Run focused tests after each remediation wave and keep documentation, contracts,
   help text, tests, and implementation synchronized.
9. Give the remediated state back to independent reviewers. Ask them to verify prior
   findings, search for regressions and new risks, and rescore from current evidence.
10. Repeat steps 6-9 until the blocker gate, per-axis threshold, and all repository
    verification gates pass. Never round a lower score up to the target.

The main agent owns synthesis and edits. Reviewer agents remain independent and
read-only unless the plan explicitly separates implementation agents from reviewers.

## Apply completion gates

Do not report DeepReview complete until all of the following are true:

- Every accepted finding is fixed and verified, or is explicitly rejected with
  evidence. A user-approved residual risk must remain visible and may still prevent
  a release-ready conclusion.
- Every applicable axis meets the configured score with a written evidence basis.
- No material blocker is hidden by an average score.
- Canonical build, tests, formatting, repository guards, and proportional platform,
  package, release, UI, security, performance, and documentation checks pass.
- Full diff and Git status contain no unrelated changes, secrets, local paths,
  generated output, private observations, or temporary artifacts.
- Required commit, merge, post-merge verification, and branch cleanup are complete
  only when authorized by repository instructions and the user.

Use precise language: state that no known blocker remains under the executed scope
and evidence. Do not claim mathematical bug-freedom, literal zero risk, or coverage
that was not run.

## Report the result

Lead with the outcome. Include the reviewed scope, final axes and independent scores,
blocker status, major improvements, verification actually run, delivery state, and
any genuine residual risk or unverified boundary. Do not close with optional work
that should have been completed inside the accepted DeepReview scope.
