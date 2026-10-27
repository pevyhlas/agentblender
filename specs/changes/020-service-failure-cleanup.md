# Change: Harden failed service startup cleanup and protect stop/uninstall behavior

**Opened**: 2026-10-27 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: 008-agent-operability

## Intent

The owner selected independent service hardening while native startup investigation is paused.
Close the parent008 failed-service-start cleanup gap and protect its stop/uninstall safety rules.
Fold that direction here; native reliability remains open on019/PR15 with its exhausted allocation.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L2 | P1.B-14, P1.B-16 | failed service starts request manager cleanup and retain actionable failure diagnostics | same host-only verbs/exit codes |
| L2 | P1.B-15, P1.B-16 | regression proof for SIGTERM fallback and active-uninstall refusal | existing guarantees |

## Bounds

- C-03/C-04: existing loopback checks and private log readers; no new listener or environment
  capture, no real user-service mutation in verification.
- Q-04: selected-service cleanup only, no new direct signals on failed start; retain the
  confirmed-daemon/store/port and active-session protections.
- C-01/C-02/C-08: .NET host changes only, existing dependencies; no worker/Blender changes.
- C-09/C-12: hosted required CI stays; macOS and real service-manager qualification remain
  separate observations. Native warm profiling stays paused by owner.
- Others in scope: hold.

## Approach

Baseline38d3db8544e675dbcb0d54c90bc3a633076ed0e9; D-16, P1.B-14–16 and parent008 service carry-overs.
`StartServiceAsync` currently returns immediately when `IServiceManager.Start` fails; its later
readiness-failure branch stops the manager but discards a stop failure. Reuse one cleanup/reporting
path for both outcomes, retaining the primary error/check and manager output/journal hint. A
secondary stop failure is reported, never treated as successful cleanup. Cleanup uses only the
selected manager's Stop, not PID signals, uninstall, restart, or a new retry.

Before cleanup on an immediate manager rejection, perform a bounded existing-daemon observation
using the existing lock/record/health identity checks. A confirmed ready daemon or responsive
holder on another port is left running (including a concurrent start); report the manager failure
and that disposition. Existing post-start mismatch protection remains. No widened handoff rule.

Add an internal injectable-manager overload for `uninstall-service` to exercise its existing
active-job refusal without reaching the user's manager. Tests use actual isolated store locks,
loopback fixture responses and fake process/manager side effects. SIGTERM fallback already exists;
prove exact ordering, verified exit and preserved manager warning, with sessions refusing first.

Out of scope: refusal exit/restart policy, installation rollback, manager interface unification,
doctor status changes, launchd definition/rotation changes, live installation/deployment and
native warm correction. These remain parent008 carry-overs, not satisfied by this increment.

## Tasks

- [x] T001 Review the bounded cleanup design and add immediate-failure/secondary-failure regressions.
- [x] T002 Implement shared cleanup/reporting and the uninstall test seam; preserve healthy-holder
  protection and prove active-uninstall refusal plus SIGTERM fallback.
- [ ] T003 Build/unit verification, applicable reviews, exact-head CI and same-PR finalization.

## Checks

Tests must observe effects, not just generated text: one cleanup Stop after a rejected Start,
original error plus secondary cleanup failure retained, manager-specific diagnostic hint, no
stop/signal for protected healthy holders or refused active sessions/uninstall. Existing
service-handoff and worker-cleanup regressions remain. Source/unit proofs use no native Blender
or actual service manager, and do not claim native or live-service acceptance.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P1.B-14, P1.B-16 | failed manager start requests cleanup, preserves primary and secondary errors plus journal/output guidance | 0 | NOT_RUN |
| A2 | P1.B-15, P1.B-16; Q-04 | protected holder, active uninstall/session and SIGTERM fallback regression proofs | 0 | NOT_RUN |
| A3 | C-03/C-04; delivery | owning reviews, zero-warning build, full unit lane, exact-head required CI and Product-Kit gates | 0 | NOT_RUN |

## State

**Step**: verification
**Waiting on**: full unit lane and implementation reviews
**Authority**: owner selected pause-profiling/service-hardening on2026-10-27; constitution permits
bounded branch commit/push/PR and reviewed green merge. No release/deployment or live user-service
mutation. Native019 has5 diagnostics,1 reproduced timeout,0 product corrections; extra2/2 used,
A1 BLOCKED/A3 FAIL and first-session/render unproved. This change does not reset those outcomes.
**Evidence**: `.prdkit/cache/evidence/020-service-failure-cleanup/`: design-review PASS0 before
implementation; regression-before6 FAIL on uncorrected source (expected). Focused run116 PASS/1 FAIL
at the existing private-log fixture: parent `state/` inherited group-write under umask0002. Diagnostic
run confirmed the refused parent; fixture now creates both levels privately, without weakening the
reader. These failed outputs are retained. Build/unit verification follows that fixture correction.
**Next**: finish verification, publish the checkpoint/PR, and obtain applicable exact-source reviews.
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
