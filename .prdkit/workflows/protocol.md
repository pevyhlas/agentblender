# Product-Kit protocol

The spec narrows the space of acceptable implementations where a wrong choice is expensive. It
is not a program and never tries to be complete; code and tests are the exact implementation.
Keep what explains the system; leave what explains the work to git, PRs and CI.

## Authority

Humans own product intent and consequential choices. Read project instructions and
`.prdkit/memory/constitution.md` first; harness and user instructions keep their authority. A
generated plan never grants permission to commit, push, merge, publish, spend or deploy: use
explicit user authorization or the constitution's action policy, resolved per action, with the
source and scope of each hold. Missing authority is not approval; standing authorization need
not be repeated.

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
waits for the answer. Pending owner direction and open questions go in `specs/inbox.md`, never
only in harness memory or a session log. On an answer, apply [spec.md](spec.md), Resolve owner
answers: incorporate and record it, transfer obligations, then remove the resolved inbox item.

## Where things live

| What | File |
|------|------|
| Product summary (L0): pains, personas, core loop, journey index, the bet | `specs/product.md`; all pains and personas in `specs/product/users.md` once they outgrow it |
| Journeys (L0): who, intent, situation, scenario, variations, success | `specs/product/journeys/j-NN-<slug>.md` |
| Requirements, qualities, constraints, non-goals (L0) | `specs/product/<kind>.md`, or `specs/product/<kind>/<area>.md` once split |
| Lifecycle (L0): the stage now and next, each stage's goal, audience and exit criteria | `specs/product/lifecycle.md`, stages in `specs/product/lifecycle/<stage>.md` once split |
| Architecture overview (L1): diagram, parts and why, core loop path, key decisions, key areas, risks | `specs/architecture.md` |
| Architecture topics (L1): security, observability, data, interfaces, why it works, environments, operations | `specs/architecture/<topic>.md`, areas in `specs/architecture/<topic>/` once split |
| Retired parts and moved IDs (L1) | `specs/architecture/retired.md`, linked from `architecture.md` |
| Decisions (L1) | `specs/architecture/decisions.md`, or `specs/architecture/decisions/<area>.md` once split; a long one in `specs/decisions/D-NN.md` |
| Parts (L2): why each exists, components, behaviors, invariants | `specs/parts/<part>.md`, components in `specs/parts/<part>/` once split |
| Facts observed about the outside world | `specs/observed.md`, or `specs/observed/<area>.md` once split |
| Pending owner direction and open questions only | `specs/inbox.md` |
| Holds, waivers, attestations, deployments | `specs/records.md` |
| Open change (deleted when it closes) | `specs/changes/<id>.md` |
| Optional execution scratch (never the sole handover) | gitignored `.prdkit/cache/implementation/<id>.md`; useful context on the same PR |
| Closed changes, one line each | `specs/log.md` |
| Archive: directions tried and why they stopped, lessons (read on demand, never by default) | `specs/archive.md` |

Create each file from its template in `.prdkit/templates/` (`product-template.md`,
`journey-template.md`, `requirements-template.md`, `qualities-template.md`,
`constraints-template.md`, `non-goals-template.md`, `lifecycle-template.md`, `architecture-template.md`,
`architecture-topics-template.md`,
`part-template.md`, `observed-template.md`, `inbox-template.md`, `records-template.md`,
`decision-template.md`, `archive-template.md`); `users.md` and `retired.md` follow the comments in the product and
architecture templates. A change that deletes its record adds its `specs/log.md` line in the
same PR. A small product may keep L0, L1 and parts in one
`specs/SPEC.md` (`spec-template.md`). Raw evidence never lives in `specs/`: diffs, results,
logs and captures stay in CI, the PR or the gitignored `.prdkit/cache/evidence/`, linked by URL
or path. Every claim says whether it is observed, decided, assumed or proposed.

## Context order

1. The request and the open change it belongs to ([change.md](change.md)).
2. The constitution, then `product.md` and `architecture.md` in full.
3. The part files the change touches, and parts across a touched interface.
4. Their code and tests, and a `legacy` document only when a part links it. `specs/archive.md`
   only on demand: a history question, or before re-trying a direction.

Read a document over 32 KB by section. Agent context files (`CLAUDE.md`, `AGENTS.md`) load on
every turn: they hold stable orientation and pointers, never change narrative or evidence.

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

The constitution may require more. Re-review only changed source, scoped to the fix, with a
range that starts at or before the head the earlier verdicts covered; never dispatch a round
when no source changed since the last verdict. Reviews are run by the worker, not CI;
`check-review.sh` checks the recorded verdicts before merge, not which concerns a tier needed
([change.md](change.md)).

## Results

Each operation ends with COMPLETE, READY, NEEDS_DECISION or BLOCKED, the reason, evidence links
and the next action; READY is eligibility, not authorization. For the owner, answer first, in
plain language, with the title next to every ID. Merged, live (deployed) and validated (owner or
user outcome) are distinct claims. Never invent usage, approvals or test results. Continue ready
work without asking at every step; stop at scope decisions, holds, owner gates, a stall or an
action needing authority. A handover keeps the authority source, scope and holds.
