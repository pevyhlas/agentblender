# Executing ready work

**Inputs**: a ready change from [planning.md](planning.md), canonical specs/design, actual code and
tests, current dependency evidence and project policy.
**Writes**: code/tests, the change's Tasks/Acceptance/State, optional ignored execution detail and
the same PR's context/evidence. Spec changes are limited to approved deltas and honest enforcement
or delivery markers; new product/design decisions return to Product-Kit's existing process.
**Result**: verified PR-scoped implementation ready for review and closing in the same PR
([review.md](review.md)), or a recorded blocker/decision.

The upstream flow owns intent, architecture and design. Execution normally consumes that definition;
it does not run a second feature-specification exercise or routinely ask the owner to approve it again.

## Plan, tasks, analyze

1. Confirm the ready scope against the actual baseline. Record its commit and relevant spec/design
   IDs in Approach. Reuse approved design; choose ordinary code-level details within its bounds.
2. Make a proportional execution plan. A tiny fix needs no extra plan. For substantial work,
   use `.prdkit/templates/implementation-plan-template.md` only if useful, at the gitignored
   `.prdkit/cache/implementation/<change-id>.md`; verify the path is ignored before writing it.
   Keep paths, line ranges, commands and implementation steps in the cache or the PR, not the
   record. Keep Acceptance and State authoritative; put review-relevant context on the PR.
3. Write each task as `- [ ] T001 <outcome; dependency; check name>`, with an optional part
   prefix before the key. Tie the outcome to canonical IDs or acceptance. Use `T###` keys unique
   within the change; never reuse or renumber them. Prefer vertical increments; justify enabling
   tasks. Parallelize only independent touches allowed by project policy. Honor security, data
   integrity and other hard bounds from the first increment, not a final hardening task.
4. Analyze before execution: scope and design consistency, outcome/check coverage, valid non-cyclic
   dependencies, shared touches, and meaningful negative/cross-part checks. Use the project's real
   verification commands and testing policy; no universal TDD requirement. Do not demand future
    runtime proof at planning time or mistake task status for proof.
5. Preflight verification using the repository's documented commands, CI and actual discovery:
   confirm working directory, supported flags, tool versions, test count/selection and required
   environment. Distinguish zero tests, incomplete logs and unavailable tools from PASS. Capture
   command, revision, environment and completed result once; reuse it while its inputs stay valid.
   Routine invocation repairs follow protocol.md; they do not reset substantive attempt limits.

No routine research dossier, data-model copy or duplicate contract. If a consequential uncertainty
needs investigation, collect only what resolves it and route the finding below.

## Implement, verify, converge

Before selecting the next task or child change, reload the programme's outcome checkpoint
([planning.md](planning.md)) and check dependencies against current evidence. In State's
**Selection**, name the milestone or predeclared enabling step this task advances, why it comes
before the next critical-path candidate, and how it fits the lane and remaining allocation. For
temporary/retiring code, include its carry-forward or removal rationale from
[planning.md](planning.md). A tiny standalone fix needs only a sentence; no extra document or
routine owner approval is required.

A blocked deployment, review or owner decision blocks only its actual dependants. Check whether
source/design prerequisites or another approved lane can advance independently. If none can, name
the blocker and the decision that unlocks the outcome; do not substitute an unlimited inventory of
adjacent improvements. Broad instructions to continue preserve the outcome, lane purposes, timebox
and explicit holds unless the owner changes them.

Take the selected authorized task whose dependency outcome is evidenced. Implement and run its
checks. Local verification is not delivery: keep the task line unchecked until the PR that
delivers it deletes it. For a single-PR change, the closing PR deletes the record. Use the optional
`Drops: <change-id> T004, T005` line in the PR description for dropped tasks. Publish and review
coherent increments per [change.md](change.md) and [review.md](review.md). Converge the actual
diff against approved intent and design; check failure behavior and cross-part interactions.
Tests that merely repeat the implementation do not establish the intended outcome.

After integration, compare evidence with the programme milestone and its predeclared dependency
checks. Record the actual advance, or increment **No-progress changes** once for that integrated
child, including successful repairs and evidence-only work that advance neither. Test/review/PR
counts alone are not an advance. Reset the count only with linked evidence of an advance; naming
a new child, changing sessions or retrofitting a milestone to completed work does not reset it.
Keep the parent checkpoint current in the relevant child PR/its final status; reconcile concurrent
children before choosing more work. Preserve the latest checkpoint on the PR if record changes
would invalidate final review; reconcile it into the parent at the next authorized edit.

At the progress limit, timebox boundary or displaced lane, reassess **before starting another
adjacent change**, even if every PR passed. Compare the remaining critical path, effort spent,
replacement/containment options and the proposed next step. Within unchanged authority, record
the decision and take a bounded step that directly advances that path. If continuation needs more
investment, different scope or a lifted hold, report NEEDS_DECISION with a recommendation in the
inbox; only the owner changes those bounds.
This selection checkpoint neither waives a safety defect nor creates a new review/approval gate.

On resume/rebase, compare the actual spec/code/dependency baseline with the recorded one. Revalidate
affected tasks and evidence when a shared contract or bound changed; preserve unaffected work and
valid review coverage. Missing scratch is reconstructed from the record/PR and actual state, never
treated as a reason to restart product discovery. Local tasks and tracker assignment grant no authority.

## Exceptional findings

| Finding | Action |
|---------|--------|
| Code violates clear intent | Correct the code; do not edit the promise to match it. |
| Equivalent code-level choice within design/bounds | Decide under existing policy; no new product requirement. |
| Missing or contradictory product intent | Record evidence, impact, options and recommendation in the inbox; Product-Kit resolves it. |
| Material design/interface assumption fails | Return the affected work to Product-Kit's design/decision process; do not force a broken design. |
| Possible extra capability or technical improvement | Keep it optional; no scope growth without product/design justification and authority. |

Implementation observations are evidence about the implementation, not permission to redefine intent.
New requirements need the provenance in [planning.md](planning.md). Stop affected work at an unresolved
consequential choice; continue independent ready work. Apply the decided change to its canonical owner,
then update affected tasks/checks. Never manufacture requirements from a library, method or schema.
