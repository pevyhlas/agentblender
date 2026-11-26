# Running a change

**Inputs**: a bounded request or owner direction, open inbox items, the spec, code and policy.
**Writes**: `specs/changes/<id>.md`, approved spec edits, any realizing code, PR context
and evidence. Optional execution detail is ignored working material.
**Result**: a change ready for [review.md](review.md), or a recorded blocker or decision.

A change is a transaction on the spec, the code or both. Open one only for bounded work; discuss a
vague request (and ask its consequential questions) first. Create it with
`.prdkit/scripts/bash/new-change.sh --json --short-name <name> "<intent>"`: it numbers the change,
renders `.prdkit/templates/change-template.md` and creates a worktree unless `--no-worktree` (a new
branch in the current checkout; existing work carries over). It never hands out a number a
`planned:` marker names. To open that planned change, pass its number and slug (`--number 12
--short-name export` for `planned: 012-export`) if it exceeds the largest allocated number.
Otherwise, open the change with a fresh number and update the marker. The worktree starts from committed HEAD: it lists
uncommitted `specs/` paths left behind (`UNCARRIED_SPECS`); record the owner direction and spec
context the change needs in the worktree. Use `--no-branch` for project-authorized
existing-branch delivery: it creates only the numbered record in the current checkout. Pass the
change ID explicitly to later helpers when it differs from the branch. Add `--programme` only
when opening a programme parent or continuing follow-up queue; it includes the commented outcome
checkpoint scaffold even if a customized change template omits it.

For a product/programme, use [planning.md](planning.md) to discover work across intent and design.
For ready work, use [implementation.md](implementation.md). One implementation change normally
belongs to one PR; larger outcomes use bounded children when useful.

## The record

The record holds work in progress, not truth: whatever stays true after merge lives in the spec,
edited in the same PR. Sections of one file; skip a section that has nothing real.

1. **Intent**: why now, for whom (the journeys it serves), and one sentence of disposition for each inbox item it
   addresses (folded to its home, still open and why, or proposed for decline).
2. **Spec edits**: IDs added, changed or retired per level ([spec.md](spec.md)), with the owner's
   quote for L0. List conflicts with existing lines or tests; conflicting owner directions go to
   the owner with both quotes, never resolved by you.
3. **Bounds**: before the approach, run `.prdkit/scripts/bash/bounds.sh <id>`. Name each bound
   this change could break with how it holds, then `Others in scope: hold`. A conflict goes to the
   owner inbox with both sides; never resolve it, and never build past it. An item it lists for a
   later stage is built only on the owner's direction.
4. **Approach**: approved design references and spec/code baseline, then only real execution choices:
   options, the pick, why; one that binds later work goes to its L1/L2 home.
5. **Tasks**: outcome, dependency and check name only; no paths, line ranges or commands. Use
   `T###` keys unique within the change; never reuse or renumber them. Optionally prefix the key
   with a part from the change header; see [spec.md](spec.md#optional-change-metadata).
   Keep execution detail in the gitignored cache or the PR per [implementation.md](implementation.md).
6. **Checks**: before building, a few lines: every edit passes the line test, touched IDs agree
   with L0 and L1, every new Q and I is enforced.
7. **Acceptance**: state bounded PR obligations as observable outcomes and map each to a canonical
   R, Q, B or I and the check that proves it (test, CI job, live run, owner trial). A spec-only PR
   proves its spec obligations, not the future runtime outcome. Check that the intended outcome can
   be observed; proof design (oracles, fixtures, controls) belongs in the implementing PR's tests
   and is reviewed there against real code.
   Keep the template's five columns, on a programme parent too: status helpers read them.
   Result is exactly `NOT_RUN`, `PASS`, `FAIL`, `BLOCKED` or `NOT_APPLICABLE`; put revision/environment
   and output links in Check or below the table, not after the status token. A PASS needs that evidence.
   Distinguish required PR gates from later runtime observations before execution, using agreed
   scope/policy. Live-only claims need live proof; without it they stay unverified. Existing acceptance
   rows and policy-required checks remain gates unless the authorized owner explicitly changes them;
   never demote a failing check to finish. Put non-gating observations and their next check on the PR.
8. **State**: step, attempts per item and blocker, what blockers wait on, holds with their source,
   branch and PR, authority per action, next action, and for a programme or continuing follow-up
   queue its outcome checkpoint ([planning.md](planning.md), Protect the outcome). Update it before
   every stop; it is the only status source an agent maintains; the PR's Current status is a
   concise projection, refreshed after a material gate, hold, limit or next-action change and
   before handover.

## While building

- Spec edits that claim built behavior land in the PR whose code and tests realize them.
  Owner-approved, unbuilt intent may land in a spec-only PR under the [`planned:` rule](spec.md#elements).
  Only approved meaning changes; follow [implementation.md](implementation.md) for findings, never
  manufacture intent.
- A PR that edits or deletes a test citing an ID also edits that ID's line, retires it, or says
  in the PR why the line still holds.
- A behavior change without a spec edit says `Spec impact: none — <reason>`. Unaffected changes
  and parts get no note at all.
- The PR that delivers a task deletes its line from the change record. No `[x]`, delivered note
  or history entry for the task. A deleted line means delivered unless the PR description lists it
  on a `Drops: <change-id> T004, T005` line. In a single-PR change, the closing PR delivers the
  tasks with the change by deleting the record.
- Acceptance links its evidence. The cache is local to one machine: link a CI run or PR comment
  for evidence anyone else needs. A green PR does not pass an acceptance item.
- At verified increments and before a stop, commit only intended work, publish when authorized (a
  draft PR while acceptance is open) and run `.prdkit/scripts/bash/check-delivery.sh --json`
  before claiming a remote checkpoint: a matching SHA proves availability, not acceptance. Without
  publish authority, record LOCAL_ONLY with the preserved work.

## Review, merge and closing

Before dispatching review, merging, closing, or finalizing a parent or another record in this PR,
read [review.md](review.md).
