# Discovering implementation work

**Inputs**: approved product intent, journeys, requirements, qualities and constraints; architecture,
design decisions, part contracts and invariants; current code/evidence, stage and action policy.
**Writes**: the existing change or programme's Intent, Approach, Tasks, Acceptance and State; unresolved
decisions in `specs/inbox.md`. Canonical edits follow [spec.md](spec.md) and existing authority.
**Result**: justified, bounded work with coverage and dependency outcomes, or a focused decision.

Product-Kit owns product discovery, architecture and design. Use this operation when turning a
product or programme into features, or when a bounded request lacks its upstream definition.
Already-ready work goes straight to [implementation.md](implementation.md): do not rediscover it.

## Discover across levels

1. Read the relevant L0/L1/L2 together, including the parts across touched interfaces, current
   implementation and evidence. Separate existing guarantees, approved planned intent and hypotheses.
2. Walk the journeys and their meaningful states, failures and recovery paths. Apply current-stage
   qualities, constraints and non-goals. Trace these through the design's responsibilities, contracts,
   data ownership, invariants and cross-part interactions; inspect gaps in both behavior and design.
   For touched persistent state, follow creation, reading, update, deletion/forgetting and recovery
   across every relevant reader/writer. Record which part owns each transition and how old/new
   versions coexist; inspect current guarantees rather than inventing new lifecycle requirements.
3. Group the missing work into useful, verifiable outcomes, preferably vertical across parts.
   Include necessary integration, migration or other enabling work justified by those outcomes and
   the approved design. A part is not a feature list; neither a quota of features nor every possible
   technical improvement establishes coverage. Do not turn later-stage intent into prerequisites.
4. Check both directions: what approved behavior has no work or existing proof, and what work has
   no product/design justification? Explain omissions, shared work and safe intermediate states.
   Coverage is a reasoned assessment, not proof the product definition is complete.

Keep a compact discovery table in the programme's Tasks:

| Work | Outcome / canonical IDs | Design / parts | Stage / disposition | Depends on + required outcome | Completion checks |
|------|-------------------------|----------------|---------------------|-------------------------------|-------------------|

Link child change IDs when opened. Separate required now, later, optional and unresolved work.
Detail only near-ready work; do not open ten worktrees just to describe ten candidates. In a bounded
change, use outcome-level task lines per [implementation.md](implementation.md).
No separate feature-spec hierarchy or global task-ID scheme.

## Programmes

A programme is a parent change: Intent names outcomes; Tasks map coverage, dependency outcomes and
owner gates with one row per child (id, outcome, status, PR, carry-forward of at most three lines);
State tracks them. A child names its parent in its **Parent** header; status and the
merged-without-closing check read that field. Child plans and execution live in the child record
and its PR. A closed child does not prove the parent's integrated outcome. Edit the parent in
place; no amendment journals. Children and the parent close per [review.md](review.md).

## Protect the outcome

For a programme or a sequence of follow-ups, keep an **outcome checkpoint** in the existing
parent's State, using the format in `.prdkit/scripts/bash/outcome-checkpoint.md`.
Children link it rather than copying a second tracker; a standalone small change can keep its
outcome and selection in prose. Define the check for each enabling step in Tasks before starting
it: a reviewed interface, working source path or deployed service can be a real intermediate
advance without claiming the final user outcome.

Scope linked progress evidence to what it proves: source, deployed or user-validated. Record
`none` only if the history shows no advance. Count integrated children since the latest evidenced
advance as a nonnegative integer. Keep the scaffold's positive progress limit unless the
owner/project sets another (cite its source in Allocation). Reassess at that count or an existing
timebox/investment boundary, whichever comes first.

On adoption/resume, reconstruct missing fields from actual records and owner direction before
selecting more work. If history or consumption cannot be reconciled, record `unknown`, not zero;
do not invent a new baseline. Ordinary State prose such as an `**Outcome**:`
result is not a programme goal. Record programme checkpoint fields only under
`### Outcome checkpoint` in State. Count/reset/reassessment rules are in
[implementation.md](implementation.md). These fields report recorded claims, not machine-verified
progress or action permission: the helper's count comparison cannot evaluate prose deadlines,
effort, lane drift, evidence validity or criticality, so the operational lead checks those too.
Keep scope, limits and original failures through splitting or closing child changes; a new
record is not a fresh investment allowance.

Preserve each lane's purpose as well as the concurrency cap. A spare slot does not authorize
diverting the value or foundation lane to an adjacent cleanup queue.

Before investing in an implementation scheduled for replacement, identify the affected supported
path, present defect and minimal safe remedy; compare repair with permitted containment, disabling
the affected path, or advancing the replacement. Say what code/contracts/tests carry forward and
what will be discarded, with a removal trigger for temporary work. Existing hard bounds and
supported behavior still bind: containment or disabling needs the same applicable authority as any
behavior change. Completeness of a retiring implementation is not automatically a prerequisite for
its replacement. A new omission is a candidate to assess, not an automatic extension of the queue.

## Resolve upstream gaps

A missing product promise needs user/outcome evidence, an external obligation or explicit owner
direction. Architecture may reveal necessary enabling work; that does not mint a new L0 requirement.
A library limitation, current method behavior or convenient implementation is never sufficient
requirement provenance. Apply the line test and [protocol.md](protocol.md)'s authority; preserve
owner quotes for L0. Material design gaps use the existing design/decision process, with T3 for
one-way doors. Record alternatives and a recommendation; stop only affected work. Do not silently
choose between contradictory guarantees or weaken them to fit the code.

## Hand off ready work

Use the existing change record, not another readiness document. Its scope is ready when it names:

- The intended outcome and exclusions, canonical requirement/design references, affected parts and
  interfaces, and the spec/code baseline it was checked against.
- The applicable bounds, a settled design with ordinary implementation choices left open, and no
  consequential unresolved decision blocking this work.
- Concrete completion checks and dependencies with release conditions: a reviewed contract, merged
  code, deployed interface or validated behavior are different outcomes. Required PR gates are
  distinct from later runtime observations; existing required checks cannot be silently demoted.

Readiness is not authorization. Continue within standing policy without re-approval; holds and
required capabilities still apply. Check dependencies against actual evidence, not a checkbox or
tracker status. Note shared interface/data writers and sequence overlapping work until compatible.
Check for circular proof: if A needs B deployed but B's merge needs A, name the permissible premerge
proof and the separate integration/live gate with its owner and trigger. Use an approved intermediate
state or change sequencing; changing an existing required gate needs owner authority. An unresolved
cycle blocks that dependency, not independent ready work. Resolve it before implementation review.
For several PRs, give each a bounded outcome and its own change; use a programme only when useful
for integrated outcomes or coordination, not as a cleanup vehicle. Changes can ship intermediate
behavior only when it preserves all applicable hard bounds and states remaining intent honestly.
