# Product-Kit protocol

The spec narrows the space of acceptable implementations where a wrong choice is expensive. It
is not a program and never tries to be complete; code and tests are the exact implementation.
Keep what explains the system; leave what explains the work to git, PRs and CI.

For routine `specs/` context, read through
`bash .prdkit/scripts/bash/read-spec.sh specs/<file>.md` (spec.md, Generated tracking navigation).
It preserves source IDs and line numbers while omitting only validated generated navigation.
Use `--index` to find headings and `--section '<heading-or-id>'` to read just one section.
Use raw source/Git diffs for review, integrity, link maintenance and exact-revision evidence.

## Authority

Humans own product intent and consequential choices. Read project instructions and
`.prdkit/memory/constitution.md` first; harness and user instructions keep their authority. A
generated plan never grants permission to commit, push, merge, publish, spend or deploy: use
explicit user authorization or the constitution's action policy, resolved per action, with the
source and scope of each hold. Record an owner's stop as a HOLD in `specs/records.md` with their
verbatim words, source, date and stated scope; ask the owner before applying a narrower or wider
reading. Its `until=` is a reminder: the hold stands until its issuer explicitly names and lifts it.
Missing authority is not approval; standing authorization need not be repeated.

For a long HOLD or WAIVER quote, copy `.prdkit/templates/authority-template.md` to
`specs/authority.md` if needed. Put the verbatim quote, source and date under a unique heading
such as `## H-07`. Run `bash .prdkit/scripts/bash/authority-ref.sh H-07` and paste its
`ref=specs/authority.md#H-07@<digest>` as the entire reason after ` — ` in the row. Keep the
full scope and all operative fields, including `until=` or `check=`/`expires=`, in the row.
Short inline reasons still work. A waiver renewal needs a new row from the owner; if the quote
changes, give it a new entry and digest instead of rewriting the old evidence.

The digest is the first 12 lowercase hex digits of SHA-256. It covers the original bytes from
the anchor heading through the line before the next heading at the same or a higher level
(or EOF), with trailing LF bytes normalized to exactly one LF. Only ATX (`#`–`######`)
headings outside comments and fences count; match the case-sensitive heading ID, not a rendered
slug. Use LF text. Status and doctor reject broken references; reconcile the original owner
evidence rather than refreshing a digest to silence a mismatch. A failed check never lifts a hold.

While work is held, put read-only observations and attempt classification in State. Leave held
Tasks, Acceptance and original evidence unchanged until authorized; reporting a hold is not a rerun
or a new result for its acceptance item.

Only a human extends or grants: the intent owner, or a human the owner names. That covers more
attempts or fix rounds, lifting holds, and changes to scope, investment or limits. An agent never
grants these to itself or another agent, whatever its role. Owner words relayed verbatim through
another session or tool, with date and source, are owner direction: record and act on them
without asking again. An agent's paraphrase, summary or allocation is information.

## Asking

A clear request needs no re-approval; an uncertain suggestion is not an execution request. Ask
only when an interpretation materially changes scope, behavior, investment or authority. Every
question to the owner carries a recommendation; when the choice is reversible, state the default
you will apply if no answer arrives and continue other work. A one-way door ([spec.md](spec.md))
waits for the answer. Pending owner direction and open questions go in `specs/inbox.md`
([inbox.md](inbox.md)), never only in harness memory or a session log. On an answer and on
resume, apply [spec.md](spec.md), Resolve owner answers, before asking it again.

## Where things live

| What | File |
|------|------|
| The spec: current product (L0), architecture and optional UI design (L1), parts (L2) | `specs/`, per [spec.md](spec.md), Layout |
| Facts observed about the outside world | `specs/observed.md` (`observed-template.md`), or `specs/observed/<area>.md` once split |
| Pending owner direction and open questions only | `specs/inbox.md` (`inbox-template.md`) |
| Holds, waivers, attestations, deployments | `specs/records.md` (`records-template.md`); long authority quotes in `specs/authority.md` on demand |
| Open change (deleted when it closes) | `specs/changes/<id>.md` |
| Optional execution scratch (never the sole handover) | gitignored `.prdkit/cache/implementation/<id>.md`; useful context on the same PR |
| History (tracked, never cited by the spec) | `.prdkit/history/log/<id>.md` per close, folded into `log.md`; `archive.md` indexes older closes and lessons |

Templates are in `.prdkit/templates/`. Raw evidence never lives in `specs/`: diffs, results,
logs and captures stay in CI, the PR or the gitignored `.prdkit/cache/evidence/`, linked by URL
or path. Every claim says whether it is observed, decided, assumed or proposed.

## Context order

1. The request and the open change it belongs to ([change.md](change.md)), plus its parent's
   outcome checkpoint: original goal, intended architecture, critical path, lane purposes and limits.
2. The constitution, then `product.md` and `architecture.md` in full; read `specs/design.md`
   when present and relevant.
3. The part files the change touches, and parts across a touched interface.
4. Their code and tests, and a `legacy` document only when a part links it. `.prdkit/history/archive.md`
   only on demand: a history question, or before re-trying a direction.

Read a document over 32 KB by section. Agent context files (`CLAUDE.md`, `AGENTS.md`) load on
every turn: they hold stable orientation and pointers, never change narrative or evidence.

On compaction or handover, preserve that checkpoint with its source revision, latest evidenced
advance, cumulative no-progress count, consumed allocation, holds and next-task rationale. Its
count and limit lines each hold a bare integer (the count may be `unknown`) that status parses; a
note there invalidates the checkpoint, so reasons and child references go in Progress, Selection
or the PR. Reload
the actual parent/PR and owner direction on resume; a summary's local task queue never replaces
the original outcome or grants a new allocation. Missing/stale checkpoint data requires
reconciliation, not an inferred zero count or restarted timebox.

## Attempts and stalls

Fix routine path, invocation and local setup errors and continue; that is no fix round and grants
no installation, shared-host change, destructive cleanup or retry of an ambiguous external effect.

Count attempts per acceptance item and per blocker (an outcome that keeps failing: a live check,
an integration, a deploy) in the change's State, across tasks, PRs and sessions. The initial baseline
run before any corrective change is retained evidence, not a repair attempt. Never relabel a retry
as a fresh baseline to reset the budget. Each real run of
that outcome after a change meant to fix it is an attempt; a merged PR whose own checks pass while
the outcome still fails is an attempt, not progress. Review allows at most three fix rounds after
the initial failing review. After three attempts, or two that produced no new information, stop
that line of work and write an alternatives note in the change: what was learned, what is still
unknown, and at least two alternatives, one managed or off-the-shelf and one relaxing or dropping
what forces the work. Report NEEDS_DECISION through the inbox and continue other work. Only the
owner grants further attempts, with a stated limit; a reset or a new task name does not.

When a PR closes, retain unresolved outcome attempts, limits and holds on its PR/follow-up. A new
fix change inherits them; deleting an execution record does not reset a failing product outcome.

Successful changes can also stall a programme without a failed check. Apply the outcome-progress
reassessment in [implementation.md](implementation.md) across child PRs and sessions. Keep its
no-progress count separate from substantive repair attempts; a green child does not prove that
the critical path moved. Readiness/correctness review and next-work priority answer different questions.

## External systems

Before code depends on an external system's data shape (an API response, a credential or config
file, a tool's output), capture a secret-safe sample or cite the upstream contract, and record it
in `specs/observed.md`. Readers accept unknown fields unless that contract forbids them. A check
that redacts errors keeps a debug path showing structure without secrets: keys, types, error
class, upstream message. Never discard an external tool's stderr.

## Review tiers

| Tier | When | Independent reviewers |
|------|------|-----------------------|
| T0 | docs only, no behavior change | none |
| T1 | inside one part, no interface change | 1 |
| T2 | several parts, an interface, security or data | 1 per applicable concern |
| T3 | L0 edit or L1 one-way door | design review plus the owner |

The constitution may require more. A branch may raise its tier, never lower it; an L0 edit
makes it T3. T0 holds only for paths the `review-t0-paths` setting lists. Rigor, the reviewer registry and that setting are
read from the trusted integration branch, so a change to them applies only after it merges. Review, merge and closing follow [review.md](review.md).

## Results

Each operation ends with COMPLETE, READY, NEEDS_DECISION or BLOCKED, the reason, evidence links
and the next action; READY is eligibility, not authorization. For the owner, answer first, in
plain language, with the title next to every ID. Merged, live (deployed) and validated (owner or
user outcome) are distinct claims. Never invent usage, approvals or test results. Continue ready
work without asking at every step; stop at scope decisions, holds, owner gates, a stall or an
action needing authority. A handover keeps the authority source, scope and holds.
