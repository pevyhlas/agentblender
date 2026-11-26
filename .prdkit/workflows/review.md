# Review, merge and closing

**Inputs**: a change with its acceptance results, its record, PR and diff, the spec it
touches, the reviewers registered in `.prdkit/memory/agents.md`, and project policy.
**Writes**: the review package, reports and GitHub reviews on the PR, the final PR
context, the log entry (`.prdkit/history/log/<id>.md`) and the record's deletion.
**Result**: a closed change whose exact head may merge, or review pending or a recorded blocker.

Read this before dispatching review, merging or closing. [change.md](change.md) opens and builds
the change; the tier is in [protocol.md](protocol.md).

## Review

Check task deletions against the PR's delivery. A deleted task is delivered unless the PR
description lists it on a `Drops: <change-id> T004, T005` line. Do not check off, annotate
or log delivered tasks.

The reviewer is a fresh context that did not implement the change. Reviews are run by the worker,
not CI. Send `.prdkit/templates/review-prompt-template.md`, the change record, the spec sections
it touches (plus a readable `specs/` at the head, to search), the PR's context and the full diff,
never only the implementer's summary. Before dispatch, confirm the reviewer's allowed read paths,
shell/network access and report channel; without shell access, supply the captured `bounds.sh`
output with its input revision/hashes.

For focused policy context, `.prdkit/scripts/bash/role-digest.sh <role>` extracts sections that
cite the role from working-tree policy and lists the input blobs. Use it as reading evidence,
not instead of trusted policy, the full source, or review approval.

Once per round at the exact head, if the change record exists there, run
`.prdkit/scripts/bash/facts.sh <change-id>` with the package's `--base` and `--head`.
Add `--pr <number>` when the PR number and host access are available; missing host evidence
stays explicit.
Only after collection exits 0, verify the pair before attaching: `index.json` fields `change_id`,
`key.base` and `key.head` must match this round's ID and full base and head SHAs, and the SHA-256
of the raw index bytes must match the hash in `summary.md`. If they match, attach both files via
`--evidence` and recheck the copied pair before dispatch. On refusal or mismatch, omit the facts,
never reuse the old cache, and use verified fallback evidence; rebuild without the facts if the
copied pair fails the check. Reviewers use the bound summary as authoritative for its captured
answers, without re-deriving them from the spec or tree. Facts are evidence, never a gate decision.
CI may optionally recompute them with `facts.sh --verify`. See `docs/review-packages.md` in the kit
source for details and `facts.sh --help` for attachment syntax.

For committed work run `.prdkit/scripts/bash/build-review-package.sh <change-id> <base> [head]`,
adding `--context <path>` for selected tracked canon/policy/record files and `--evidence <path>`
for completed local results. The helper checks structural accessibility, not semantic
completeness: verify the index covers each review obligation, prior findings, acceptance results
and unknowns. Every result names its command, revision, environment, completion/exit status and
limits; running logs are not completed evidence. Copy required sibling/remote evidence into the
checkout first with original source provenance, then attach or place the package inside the
reviewer's actual access boundary. Verify access before a costly review round. For uncommitted
work send the tracked diff plus the untracked file contents, with their hashes. Without a
trustworthy package, review is pending.

For spec edits, judge each line at its edited level: [Levels](spec.md#levels),
[What earns a line](spec.md#what-earns-a-line) and Left open ([Elements](spec.md#elements)).
Send those rules with the prompt. Snapshot the installed prompt and `.prdkit/workflows/spec.md`
with `--context` if the trusted copies are tracked at the reviewed head; otherwise attach both
at dispatch.
Use the `level` category for detail the edit adds at the wrong level or over budget.

In a spec-only change, apply the existing severities to contradictions with constraints, decisions
or non-goals; wrong or owner-unauthorized one-way doors; unobservable acceptance outcomes; missing
bounds; and facts given a second home. Requests for step logic, field-level schemas or test
procedures in L0–L2 or the change record are low (advisory); tag them `defer: implementing PR`.

Critical and high findings block, medium blocks under `Rigor: full`, low is advisory. A failed
tool run is not a product FAIL and an absent reviewer is not approval. Preserve each report
verbatim on the PR; the change record will be deleted on close and the local cache is not its
durable public home. When the reviewer is configured in the tier map, prepare the header with
`.prdkit/scripts/bash/review-report.sh scaffold`. Keep the original scaffold and send a copy
and the format from `review-report.sh --help` with the prompt. Before publication, use
`review-report.sh lint --strict` with the trusted policy's `--rigor` and `--scaffold` pointing to
the saved original. If there is no tier map, send the format and lint without `--scaffold`.
Only the opening scaffold header supplies authoritative stamps; treat everything after it as
reviewer content, not as stamp metadata. Neither an optional standalone header nor a `lint` pass
without `--scaffold` verifies stamp values; only `--scaffold` against the coordinator's saved
original does.
Report lint is formatting evidence, not approval.

For GitHub evidence, `.prdkit/scripts/bash/pr-state.sh` snapshots the PR and
`.prdkit/scripts/bash/ci-wait.sh <pr-number> --head <sha>` waits for that head's required
checks. Neither substitutes for the trusted review gate or confers merge authority. Reruns
are off by default; only opt in with operator-approved failure signatures.

Submit a normal GitHub pull request review under the reviewer’s own identity. Approve the
commit actually inspected, or request changes with the findings. When using the API, bind
`commit_id` to the reviewed SHA. Never pass credentials through the kit. For a focused
re-review, use `--since <previously-reviewed-head>` with the current trusted merge base;
inspect the fixes, prior findings and affected obligations with the full diff available.
Never dispatch a round with no source change just to regenerate a package. For a metadata-only
closing commit the reviewer can inspect the closing delta and approve the new SHA.

## Merge

Apply the same tier minimums as 2.x to the exact PR head. T0 needs no positive review;
the live gate must still pass. At T1–T3 obtain an `APPROVED` review from at least one
independent `review-publishers` account. At T3 also obtain `APPROVED` from a `review-owner`.
An account in both lists counts only as owner, and the PR author counts for neither role.
These are minimums; cover every applicable concern in the review. Only the latest submitted
review by that account on the current SHA counts toward approval. As in 2.x, a push does
not clear objections: stop at every tier for an outstanding `CHANGES_REQUESTED` on any
commit of this PR. Address the finding and ask the same reviewer to approve the current
head. A comment or someone else’s approval cannot clear it. If an owner/maintainer
dismisses the objection on GitHub, they must dismiss that specific review. Use `blocking`
to find its review ID and commit.

If trusted policy sets `review-lenses`, dispatch each applicable concern to an account
mapped to its registered reviewer name. One account can cover several concerns with
one approval. Check `areas` in the live gate JSON for the account and review that covered
each concern. T1 and T2 need at least one mapped approval; the worker still verifies
all applicable T2 concerns. T3 needs a mapped `design` approval and the separate owner.

Run `.prdkit/scripts/bash/check-review.sh --pr <url> --head <sha> <change-id>` and the
required checks. With merge authority, merge only the checked SHA (`--match-head-commit`).
Without `--pr` a successful structural preview exits 3 and cannot authorize merge.

## Closing

A PR-scoped change closes in that same PR when its required acceptance and review pass, or by
explicit owner disposition. For a single-PR change, deleting the record delivers its tasks with
the change, except tasks marked as dropped in the PR description. Whatever deletes a record,
an owner-disposed cancellation included, adds its change-level log entry in the same PR.
Required live gates still block. Non-gating runtime observations need no cleanup PR.

Before final review: reconcile the approved spec delta with actual behavior. Remove only built
`planned` markers; preserve unbuilt intent and hypotheses. Known unmet required work needs an
explicit disposition and durable follow-up reference; never erase it with the plan. Cancellation
withdraws, defers or rebinds unfinished planned intent instead of making it look built. If the
project uses `specs/design.md`, fold durable owner UI decisions and corrections into **Decisions**
as dated, sourced one-liners. Post
scope/design references, useful implementation context, actual verification/review evidence and
unknowns on the same PR using `.prdkit/templates/pr-summary-template.md`; do not depend on
intermediate commits surviving squash. Remove any tracked auxiliary execution files before this
review; ordinary review coverage applies to their changes/deletions. Ignored scratch may be
discarded after useful context is durable. A child PR updates only its row and the checkpoint in
its parent, and never copies its plan, findings or evidence there.

After the current PR head passes, run `.prdkit/scripts/bash/close-change.sh --pr <url> <id> "<intent> — <IDs> — <disposition;
runtime unknown if relevant> — <PR>"`, which writes the log entry `.prdkit/history/log/<id>.md` and deletes
the record once `check-review.sh` passes. Keep the full row within 200 characters, including
date, change ID and any tags. Carried findings and follow-ups go to the parent's row or the
spec; evidence stays on the PR. One file per closed
change means two branches that each close a change never conflict in the log; compaction folds
the entries into `.prdkit/history/log.md` ([compact.md](compact.md)).

The closing commit uses the checkout's ambient Git identity. To use an agent identity, set
`GIT_AUTHOR_NAME`, `GIT_AUTHOR_EMAIL`, `GIT_COMMITTER_NAME` and `GIT_COMMITTER_EMAIL` for the
helper invocation:

```bash
GIT_AUTHOR_NAME='Agent' GIT_AUTHOR_EMAIL=agent@example.com GIT_COMMITTER_NAME='Agent' GIT_COMMITTER_EMAIL=agent@example.com bash .prdkit/scripts/bash/close-change.sh --pr https://github.com/owner/repo/pull/1 001-example 'Update behavior — R-01 — complete — #1'
```

Push the closing commit. Obtain the approvals required for its tier on that SHA and rerun the gate.
The PR, not the record,
carries any handoff to whoever merges. Do not change scope,
acceptance or policy after review without reviewing the affected changes. Later defects use
ordinary linked fix changes, which inherit the failing outcome's attempts, limits and holds
([protocol.md](protocol.md)); they never retroactively turn an unknown into a PASS or erase a
known unmet obligation. Never reopen a completed transaction merely to clear working files. A
change merged with its record still open (status flags it) closes in the next relevant PR, whose
record is its review carrier; never fabricate acceptance or open a cleanup-only PR. After merge
run `.prdkit/scripts/bash/cleanup-change.sh <id>` from the main checkout; it keeps the worktree's
evidence before moving the checkout and its Git registration into the reported quarantine.
A clean worktree with supported submodules needs no `--force`. Inspect and recover any needed
work before manually purging its retention refs and quarantine slot.
`cleanup-change.sh --closed` sweeps every `.worktrees/<id>` on branch `<id>` whose change is closed
on the trusted integration branch (record gone, log entry present), skipping dirty or locked ones
and leaving branches alone; run it after merges so finished worktrees do not pile up.

A `--no-branch` record closes the same way, through the project's authorized delivery route; its
shared checkout stays, so skip `cleanup-change.sh`. A detached HEAD can hold a record, but
publishing needs an authorized named branch.

## Review carrier

Finalize a programme parent in the last relevant PR when its required outcomes pass or the owner
disposes of it, keeping non-gating observations explicit; no separate cleanup PR. When one PR also
finalizes a parent or older change, its implementation record is the **review carrier**. Before
final review, bring the other record's remaining required acceptance, unresolved findings, attempts,
holds and disposition into the carrier, labelled with their origin. Preserve verdict/report
history; unresolved FAILs need a clean later verdict from the same registered reviewer, not
disappearance during transfer. The carrier takes the combined scope's highest applicable tier and
all required reviewer concerns, including owner approval where required. Post the other record's
useful context/evidence on this PR, add its log disposition and delete it **before** the carrier's
final review. That review covers the combined diff and integrated outcomes, including these
deletions. Push the carrier’s closing commit for its tier’s required approvals. Run
`check-review.sh --pr <url>` for the carrier at the exact final head; do not start mutually dependent
post-review updates to two records. An inherited approval is not fresh coverage for changed work.
