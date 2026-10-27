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

Keep a compact table in the programme's Tasks (or the bounded change for a single outcome):

| Work | Outcome / canonical IDs | Design / parts | Stage / disposition | Depends on + required outcome | Completion checks |
|------|-------------------------|----------------|---------------------|-------------------------------|-------------------|

Use stable local keys while discovering work; link child change IDs when opened. Separate required
now, later, optional and unresolved work. Detail only near-ready work; do not open ten worktrees
just to describe ten candidates. No separate feature-spec hierarchy or global task-ID scheme.

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

Use the change template's five-column Acceptance table even for programmes: Item, Proves, Check,
Attempts, Result. Put coverage/dependency detail in Tasks; status helpers read the Acceptance columns.
Keep Result a bare status token as in [change.md](change.md), with evidence outside that cell.
