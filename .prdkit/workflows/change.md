# Running a change

**Inputs**: a bounded request or owner direction, open inbox items, the spec, code and policy.
**Writes**: `specs/changes/<id>.md`, approved spec edits with the code that realizes them, PR context
and evidence, a log line. Optional execution detail is ignored working material.
**Result**: a finalized PR-scoped change, or a recorded blocker or decision; runtime and product
validation claims remain separate.

A change is a transaction on the spec and the code. Open one only for bounded work; discuss a
vague request (and ask its consequential questions) first. Create it with
`.prdkit/scripts/bash/new-change.sh --json --short-name <name> "<intent>"`: it numbers the change,
copies `.prdkit/templates/change-template.md` and creates a worktree unless `--no-worktree`. It
never hands out a number a `planned:` marker names; to open that planned change, pass its number
and slug (`--number 12 --short-name export` for `planned: 012-export`). The worktree starts from
committed HEAD: it lists uncommitted `specs/` paths left behind (`UNCARRIED_SPECS`); record the
owner direction and spec context the change needs in the worktree.

For a product/programme, use [planning.md](planning.md) to discover work across intent and design.
For ready work, use [implementation.md](implementation.md); do not repeat upstream specification
or design. One implementation change normally belongs to one PR; larger outcomes use bounded
children when useful. The PR is the delivery transaction, not proof of every later product outcome.

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
4. **Approach**: approved design references and spec/code baseline, then only real execution choices:
   options, the pick, why. Material design decisions use Product-Kit's existing authority. For a new
   external dependency, record a sample in `specs/observed.md` first.
5. **Tasks**: a short checklist for resuming, or stable local tasks with contribution, dependencies
   and checks per [implementation.md](implementation.md). No extra plan for a tiny fix.
6. **Checks**: before building, a few lines: every edit passes the line test, touched IDs agree
   with L0 and L1, every new Q and I is enforced.
7. **Acceptance**: the bounded PR obligations map to canonical R, Q, B or I and the check that proves
   them (test, CI job, live run, owner trial). Keep the template's five columns. Result is exactly
   `NOT_RUN`, `PASS`, `FAIL`, `BLOCKED` or `NOT_APPLICABLE`; put revision/environment and output links
   in Check or below the table, not after the status token. A PASS needs that evidence.
   Distinguish required PR gates from later runtime observations before execution, using agreed
   scope/policy. Live-only claims need live proof; without it they stay unverified. Existing acceptance
   rows and policy-required checks remain gates unless the authorized owner explicitly changes them;
   never demote a failing check to finish. Put non-gating observations and their next check on the PR.
8. **State**: step, attempts per item and blocker, what blockers wait on, holds with their source,
   branch and PR, authority per action, next action. Update it before every stop; it is the only
   status prose an agent writes.

## While building

- Spec edits land in the PR whose code and tests realize them. Only approved meaning changes;
  implementation findings follow [implementation.md](implementation.md), never manufacture intent.
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
sections it touches, the PR's context and the full diff, never only the implementer's summary. The reviewer runs
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

A PR-scoped change closes in that same PR when its required acceptance and review pass, or by
explicit owner disposition. Required live gates still block; non-gating runtime observations do not
require a cleanup PR. Merge is neither deployment nor runtime/product validation.

Before final review: reconcile the approved spec delta with actual behavior. Remove only built
`planned` markers; preserve unbuilt intent and hypotheses. Durable design decisions belong in L1/L2
when decided, not just at close. Known unmet required work needs an explicit disposition and durable
follow-up reference; never erase it with the plan. Cancellation withdraws, defers or rebinds unfinished
planned intent instead of making it look built. Post scope/design references, useful implementation
context, actual verification/review evidence and unknowns on the same PR using
`.prdkit/templates/pr-summary-template.md`. Do not depend on intermediate commits surviving squash.
Remove any tracked auxiliary execution files before this review; ordinary review coverage applies
to their changes/deletions. Ignored scratch may be discarded after useful context is durable.

After PASS, only verdict metadata, record deletion and the log change: commit the final Review line,
then in a later commit in the same PR append
`- <date> <id>: <intent> — <IDs> — <disposition; runtime unknown if relevant> — <PR>` to
`specs/log.md` and delete the record (the check reads its last committed version). Do not change
scope, acceptance or policy after review without reviewing the affected changes. Later defects use
ordinary linked fix changes; carry forward the failing outcome's attempts, limits and holds. Never
reopen a completed transaction merely to clear working files. For an already-merged legacy open
change, resolve its outstanding obligations/disposition in the next relevant PR; do not fabricate
acceptance or require a new cleanup-only PR. After merge run
`.prdkit/scripts/bash/cleanup-change.sh <id>` from the main checkout: it copies the worktree's
evidence, retrospectives and ledger events into this checkout, verifies them, and only then
removes the worktree.

## Bigger work

A programme is a parent change planned with [planning.md](planning.md): Intent names outcomes,
Tasks map coverage and children with dependency outcomes and owner gates, State tracks them. A child
PR closing does not prove the parent's integrated outcome. Update the parent in relevant child PRs;
finalize it in the last relevant PR when its required outcomes pass or the owner disposes of it,
keeping non-gating observations explicit. No separate cleanup PR. Edit it in place; no amendment journals.

When one PR also finalizes a parent or older change, its implementation record is the **review
carrier**. Before final review, bring the other record's remaining required acceptance, unresolved
findings, attempts, holds and disposition into that carrier, labelled with their origin. Preserve
verdict/report history; unresolved FAILs need a clean later verdict from the same reviewer/concern,
not disappearance during transfer. The carrier takes the combined scope's highest applicable tier
and all required reviewer concerns, including owner approval where required. Post the other record's
useful context/evidence on this PR, add its log disposition and delete it **before** the carrier's
final review. That review covers the combined diff and integrated outcomes, including these deletions.
Only the carrier's verdict metadata/deletion and log may change after PASS. Run `check-review.sh`
for the carrier at the exact final head; do not start mutually dependent post-review updates to two
records. An inherited approval is not fresh coverage for changed work. This uses one PR review
transaction without exempting arbitrary change-record paths or dropping the parent's obligations.
