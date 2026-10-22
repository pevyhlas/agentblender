# Running a change

**Inputs**: a bounded request or owner direction, open inbox items, the spec, code and policy.
**Writes**: `specs/changes/<id>.md`, spec edits with the code that realizes them, a log line.
**Result**: a closed change whose acceptance passed, or a recorded blocker or decision.

A change is a transaction on the spec and the code. Open one only for bounded work; discuss a
vague request (and ask its consequential questions) first. Create it with
`.prdkit/scripts/bash/new-change.sh --json --short-name <name> "<intent>"`: it numbers the change,
copies `.prdkit/templates/change-template.md` and creates a worktree unless `--no-worktree`. It
never hands out a number a `planned:` marker names; to open that planned change, pass its number
and slug (`--number 12 --short-name export` for `planned: 012-export`). The worktree starts from
committed HEAD: it lists uncommitted `specs/` paths left behind (`UNCARRIED_SPECS`); record the
owner direction and spec context the change needs in the worktree.

## The record

Sections of one file; skip a section that has nothing real.

1. **Intent**: why now, for whom (the journeys it serves), and one sentence of disposition for each inbox item it
   addresses (folded here, left open and why, or proposed for decline).
2. **Spec edits**: IDs added, changed or retired per level ([spec.md](spec.md)), with the owner's
   quote for L0. List conflicts with existing lines or tests; conflicting owner directions go to
   the owner with both quotes, never resolved by you.
3. **Bounds**: before the approach, run `.prdkit/scripts/bash/bounds.sh <id>`. It lists the
   constraints, non-goals and qualities in scope for the header's parts and journeys. Name each
   one this change could break with how it holds, then `Others in scope: hold`. A conflict goes
   to the owner inbox with both sides; never resolve it, and never build past it. Build for the
   current stage: an item it lists for a later stage is built only on the owner's direction.
4. **Approach**: only real choices: options, the pick, why. For a new external dependency, record
   a sample in `specs/observed.md` first.
5. **Tasks**: a short checklist for resuming.
6. **Checks**: before building, a few lines: every edit passes the line test, touched IDs agree
   with L0 and L1, every new Q and I is enforced.
7. **Acceptance**: each changed R, Q, B or I maps to the check that proves it (test, CI job, live
   run, owner trial), live when the behavior exists only in a real environment. A PASS names the
   revision and environment and links its output.
8. **State**: step, attempts per item and blocker, what blockers wait on, holds with their source,
   branch and PR, authority per action, next action. Update it before every stop; it is the only
   status prose an agent writes.

## While building

- Spec edits land in the PR whose code and tests realize them.
- A PR that edits or deletes a test citing an ID also edits that ID's line, retires it, or says
  in the PR why the line still holds.
- A behavior change without a spec edit says `Spec impact: none — <reason>`. Unaffected changes
  and parts get no note at all.
- Evidence stays in CI, the PR or `.prdkit/cache/evidence/`; Acceptance links it. The cache is
  local to one machine: link a CI run or PR comment for evidence anyone else needs. A green PR
  does not pass an acceptance item.
- Emit the ledger events `.prdkit/memory/task-ledger.md` lists, at their boundaries.

## Review

Pick the tier ([protocol.md](protocol.md)). The reviewer is a fresh context that did not implement
the change. Send `.prdkit/templates/review-prompt-template.md`, the change record, the spec
sections it touches and the full diff, never only the implementer's summary. The reviewer runs
`bounds.sh` itself and checks the diff against every item it lists. For committed work
run `.prdkit/scripts/bash/build-review-package.sh <change-id> <base>`: it captures BASE..HEAD into
`.prdkit/cache/evidence/<change-id>/reviews/round-N/` and the reviewer writes its report there.
For uncommitted work send the tracked diff plus the untracked file contents, with their hashes.
Without a trustworthy package, review is pending. Critical and high findings block, medium blocks
under `Rigor: full`, low is advisory. A failed tool run is not a product FAIL and an absent
reviewer is not approval. Post each report on the PR once one exists: it is the durable review
record, since the record is deleted on close and the cache is local. Append one State line per
verdict, never editing an earlier one except to correct a range to the printed hashes; a later
line supersedes the same reviewer's earlier one, whatever its round. The range is the `BASE_SHA..HEAD_SHA` the package printed, never a name that
moves (`HEAD`, a branch); the first round's base is where the change leaves the default branch:

```text
**Review**: round 2 · <base sha>..<reviewed head sha> · <reviewer or concern> · PASS · 0 critical, 0 high, 1 medium, 0 low · <report link>
```

A scoped re-review's range starts at or before the head the earlier round reviewed, so the
rounds chain. A T3 change also records the owner's verdict, reviewer `owner`, with the head
the owner approved and a link to their words in place of counts; a later L0 or L1 edit needs
the owner again.

## Delivery

Authority is per action ([protocol.md](protocol.md)); a hold wins until its issuer lifts it. At
verified increments and before a stop, commit only intended work, publish when authorized (a
draft PR while acceptance is open) and run
`.prdkit/scripts/bash/check-delivery.sh --json` before claiming a remote checkpoint: a matching
SHA proves availability, not acceptance. Without publish authority, record LOCAL_ONLY with the
preserved work. Merge only when the required checks pass and
`.prdkit/scripts/bash/check-review.sh --head <sha> <change-id>` passes on the exact head being
merged, then merge that head (`gh pr merge --match-head-commit <sha>` on GitHub). It passes when
every reviewer's latest verdict is PASS with nothing blocking, no blocking line stands (a FAIL
or blocking counts stand until a later clean PASS from the same reviewer reaches their head),
the recorded ranges chain from the change's start and from each reviewer's head to the merged
head (after which only the record and `specs/log.md` changed), and the tier's minimum is
recorded: one independent verdict from T1, the owner's for T3. It does not know which concerns
a T2 change needed; the reviewer list is the worker's call. It needs no network or model
credentials, so CI can run it too. It checks recorded verdicts, never grants merge
authority, and is the gate where the plan has no branch protection.

## Closing

A change closes when every acceptance item passes, live ones included, or the owner closes it.
Before the final review round: remove `planned` markers now built and promote lasting choices from
Approach into L1 or L2 (alternatives, assumptions, revisit when). After its PASS, only the record
and the log change: commit the final Review line, then in a later commit append
`- <date> <id>: <intent> — <IDs> — <PR>` to `specs/log.md` and delete the record (the check reads
the record's last committed version). A change whose code merged before its live acceptance
closes in a pull request that carries only the record and the log. After merge run
`.prdkit/scripts/bash/cleanup-change.sh <id>` from the main checkout: it copies the worktree's
evidence, retrospectives and ledger events into this checkout, verifies them, and only then
removes the worktree.

## Bigger work

A programme is a parent change: Intent names the journeys or R/Q it advances, Tasks list child
changes with order, dependencies and owner gates, State tracks them. It closes when its children
close and its acceptance is met. Edit it in place; no amendment journals.
