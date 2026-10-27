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
- [x] T003 Build/unit verification and applicable reviews; required reviewed-head CI passed.
  Same-PR finalization retains the exact-final-head CI/review gate before merge.

## Checks

Tests must observe effects, not just generated text: one cleanup Stop after a rejected Start,
original error plus secondary cleanup failure retained, manager-specific diagnostic hint, no
stop/signal for protected healthy holders or refused active sessions/uninstall. Existing
service-handoff and worker-cleanup regressions remain. Source/unit proofs use no native Blender
or actual service manager, and do not claim native or live-service acceptance.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P1.B-14, P1.B-16 | failed-start and log-tail tests pass in unit-01 at5ff8713; primary/secondary causes and journal/output guidance | 1 | PASS |
| A2 | P1.B-15, P1.B-16; Q-04 | protected-holder, active-uninstall/session and SIGTERM fallback tests pass in unit-01 at5ff8713 | 1 | PASS |
| A3 | C-03/C-04; delivery | build0 warnings; unit-03 passes1029/0fail/1existing skip; four reviews PASS0; required CI at reviewed headf017c45 in run40487604518 PASS; final metadata head must pass again before merge | 1 source, 2 environment corrections | PASS |

## State

**Step**: finalization
**Waiting on**: final metadata-head CI and review gate before merge
**Authority**: owner selected pause-profiling/service-hardening on2026-10-27; constitution permits
bounded branch commit/push/PR and reviewed green merge. No release/deployment or live user-service
mutation. Native019 has5 diagnostics,1 reproduced timeout,0 product corrections; extra2/2 used,
A1 BLOCKED/A3 FAIL and first-session/render unproved. This change does not reset those outcomes.
**Evidence**: `.prdkit/cache/evidence/020-service-failure-cleanup/`: design-review PASS0 before
implementation; regression-before6 FAIL on uncorrected source (expected). Focused run137 PASS/1 FAIL
at the existing private-log fixture: parent `state/` inherited group-write under umask0002. Diagnostic
run confirmed the refused parent; fixture now creates both levels privately, without weakening the
reader. These failed outputs are retained. Unit-01 at5ff8713: Core890 PASS/34 FAIL/1 SKIP,
Integration96 PASS/9 FAIL (stand-ins only); all changed tests pass. The failures cluster in discovery,
doctor/setup and process fixtures that inherit writable directories under umask0002. Unit-02 uses
the CI-like umask0022, same source: Core891 PASS/33 FAIL/1 SKIP, Integration105 PASS/0 FAIL.
The remaining cause was proved with isolated fixture helpers: long temp paths selected the shared
runtime fallback, whose existing0775 mode correctly failed the0700 rule. No shared path was changed.
Unit-03 used a short private temp root plus umask0022: Core924 PASS/0 FAIL/1 existing skip,
Integration105 PASS/0 FAIL. No product source changed after5ff8713 and no native diagnostic ran.
These are verification-environment corrections, not native/product repair attempts. The diagnostic
helper's initial analyzer refusal and corrected isolated report are retained too.
Build0 warnings/errors; adoption7 groups, doctor (advisories), CI policy and diff checks PASS.
Public hygiene and CI-policy negative tests PASS. Required Validate and Build/unit CI passed at
reviewed headf017c45 in [run40487604518](https://github.com/pevyhlas/agentblender/actions/runs/40487604518);
macOS26 arm64 advisory Build/unit also passed. Hosted Blender was still running at that observation
and is not native019 repair evidence. All four independent reviews PASS0, with their earlier
verification caveats retained in the reports below. First check-review invocation atf017c45 failed
because verdicts were not yet committed; the final gate must read the committed metadata.
**Delivery**: [PR16](https://github.com/pevyhlas/agentblender/pull/16) OPEN/DRAFT; checkpoint5ff8713
pushed and delivery helper confirmed matching remote SHA; reviewed checkpointf017c45 also pushed.
**Next**: commit results/review metadata, finalize on PR16, and merge only the exact final green head.
**Review**: round 1 · 38d3db8544e675dbcb0d54c90bc3a633076ed0e9..f017c4504a8e29f9e91ac770f368b3954079ce45 · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/16#issuecomment-6439352057
**Review**: round 1 · 38d3db8544e675dbcb0d54c90bc3a633076ed0e9..f017c4504a8e29f9e91ac770f368b3954079ce45 · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/16#issuecomment-6439352421
**Review**: round 1 · 38d3db8544e675dbcb0d54c90bc3a633076ed0e9..f017c4504a8e29f9e91ac770f368b3954079ce45 · eng-ergonomics · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/16#issuecomment-6439352814
**Review**: round 1 · 38d3db8544e675dbcb0d54c90bc3a633076ed0e9..f017c4504a8e29f9e91ac770f368b3954079ce45 · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/16#issuecomment-6439353337
