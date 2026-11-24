# Change: Adopt the agent-library tier baseline (Fable 5.2 planning tier)

**Opened**: 2026-11-24 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T1 · **Parent**: none

## Intent

Owner direction 2026-11-24: switch the planning tier to Fable 5.2 and update every estate repo
whose agent model pins are out of date. AgentBlender's tier map was a hand-written literal table
with superseded ids (`claude-fable-5-1`, `openai/gpt-6-astra`, `openai/gpt-6.1-sol`). Adopt the
shared agent-library baseline (tiers.json@9c1aea7b, agent-library PR #14) the way the other
library consumers do, so later model moves are a snapshot refresh. No inbox item addressed.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|

None: developer-tooling configuration only (`.prdkit/memory/agents.md`, vendored snapshot,
generated projections). Spec impact: none — no product behavior, contract or operations line changes.

## Bounds

- C-01/C-02: holds: no dependency, inbound code or worker change; the vendored tiers.json is an
  estate-owned configuration snapshot with provenance in `tiers.json.ref`.
- C-04: holds: no secrets, paths or telemetry change.
- C-09: holds: required CI unchanged; `check-adoption.sh` and `doctor.sh` still run.
- Others in scope: hold.

## Approach

Baseline origin/main af6e3be. Product-Kit 1.2.1's `render-agents.sh --refresh-baseline` vendors
`.prdkit/agent-library/tiers.json` and `.ref` from the library commit; agents.md gains the
`Tier baseline:` line and a `| Local tier | Library tier |` map (frontier→verydeep, high→deep,
standard→standard, fast→light), and drops the literal id table. The documented `eng-license`
tier override (`tier: high`, with its Reason) is kept. Re-render; only the five OpenCode
projections change (Claude ids were already current).

## Tasks

- [x] T001 Vendor snapshot, rewrite tier map, re-render, run agent/adoption checks.
- [ ] T002 Independent review, required CI, finalize and merge.

## Checks

No new Q or I. `render-agents.sh --check` enforces the resolved projections against the baseline.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | agents.md tier policy | `render-agents.sh --check` PASS; `--resolve` shows every projection from `baseline <tier>`; sync.py model findings cleared | 1 | PASS |
| A2 | C-09; constitution | `check-adoption.sh` (7 groups) and `doctor.sh` PASS locally; required CI on the merge head | 1 | NOT_RUN |

## State

**Step**: review
**Waiting on**: independent eng-code review
**Authority**: owner direction 2026-11-24 (estate tier refresh, merge=yes); constitution branch/PR/green-reviewed merge.
**Next**: review, then finalize record/log and matched-head merge.
