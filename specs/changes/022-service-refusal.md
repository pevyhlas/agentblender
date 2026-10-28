# Change: prevent service restart loops after intentional startup refusal

**Opened**: 2026-10-28 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: 008-agent-operability

## Intent

Continue the owner's pause-and-service-hardening direction and subsequent request to continue
both projects. A login service must stop retrying an intentional configuration, private-directory
or store-lock refusal, while genuine host/worker failures retain automatic recovery. This is the
refusal-policy carry-over from008, following020. Native019 remains paused (extra allocation2/2
exhausted, A1 BLOCKED/A3 FAIL); no native Blender execution is part of this change.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L2 | P1.B-14, P1.B-16 | distinguish intentional run refusal and manager restart policy | source/stand-in proof; live qualification stays008 |

## Bounds

- C-03: existing resolver, trust gates and loopback binding remain load-bearing.
- C-04, C-05: retain existing redacted diagnostics, scrubbed service environment and telemetry defaults.
- C-12: source/plist tests do not qualify macOS or a live service manager.
- Q-01, Q-04: preserve60s warm contract, worker isolation and failed-start cleanup.
- Others in scope: hold.

## Approach

Baseline2704470aad6c111652a8eddbc3be3536191a730e; D-16, P1 operability and008 service carry-overs.
Proposed design for independent review:

- `daemon run` returns78 for the three explicit pre-host refusals: `ConfigException`,
  `PrepareDirs` problem, and occupied store lock. Existing error/check/hint text is preserved.
  Usage remains2; ordinary failures (including detach/log IO, worker warm and bind failure)
  remain nonzero retryable failures. Do not catch arbitrary exceptions as refusal.
- Generated systemd units keep `Restart=on-failure`, adding `RestartPreventExitStatus=78`.
  Status78 stays a failed unit, never a successful startup.
- launchd only distinguishes successful versus failed exit. Its generated argv adds
  `--launchd`: for these same three refusals only, the process emits the original diagnostic
  plus an explicit stopped/refused explanation and exits0 to suppress `SuccessfulExit=false`
  KeepAlive. This manager adapter is explicit, mutually exclusive with `--detached`, requires
  `--log-file`, and never changes ordinary foreground/detached exits. `daemon start` still
  requires warm health and returns failure on a stopped job; manager success is not readiness.
  Source tests may exercise the adapter on Linux; only generated launchd plists select it.
- Alternatives: disable restarts entirely (loses crash recovery); add a supervisor wrapper or
  durable sentinel (adds lifecycle/state machinery). The explicit adapter is bounded and keeps
  one process under the manager. Live manager qualification remains parent008.
- A confirmed launchd `state = not running` after exit0 participates in the existing two-observation
  pre-lock fail-fast check. Pending/unobserved states do not. No manager exit establishes readiness.
- Existing launchd jobs retain loaded argv: safe upgrade stops the daemon (respecting active-session
  refusal), uninstalls and explicitly confirms absence in the applicable launchd domains before
  installing the replacement and starting it. Existing uninstall ignores failed bootout, so exit0
  alone is not confirmation; failure propagation remains008. A rewritten plist does not activate
  policy. No automatic installation or loaded-definition reconciliation.

## Tasks

- [x] T001 Independent design review; record external manager contracts and review disposition.
- [x] T002 Implement exit classification and generated policies; update P1 and agent guidance.
- [x] T003 Unit/golden and built-CLI stand-in proof for refusal, ordinary failure, diagnostic,
  health/state/lock safety, and existing cleanup. Run required source gates.
- [ ] T004 Independent code, security, architecture/operations and ergonomics review; publish and
  merge only after exact-head required CI and review gate pass.

## Checks

The delta implements008's existing restart-refusal requirement, adding no new L0 decision.
Every refusal category has process-level proof; retryable failures have a negative control.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P1.B-14, P1.B-16 | built-CLI refusal exits/diagnostics; generated policies, source25c0967; evidence summary below | 1 fixture correction | PASS |
| A2 | P1.B-14, Q-04 | retryable log failure, no worker/listener on refusal, lock and cleanup regressions; source25c0967 | 0 | PASS |
| A3 | P1 operability | source25c0967: warning-free build, non-Blender1048PASS/1existing skip, Product-Kit and required [CI40668049514](https://github.com/pevyhlas/agentblender/actions/runs/40668049514) PASS; final-head CI still gates merge | 1 old-assertion correction | PASS |

Evidence: `.prdkit/cache/evidence/022-service-refusal/verification/summary.md` and adjacent logs.

Live systemd/launchd behavior and macOS qualification stay unverified under008/T074. No019
acceptance is claimed or rerun.

## State

**Step**: source and required CI verified; implementation reviews pending
**Branch**: `022-service-refusal`; [PR18](https://github.com/pevyhlas/agentblender/pull/18), draft.
**Authority**: owner continuation; constitution permits commit, branch push/PR and green reviewed
merge. Release, deployment and live qualification require their separate authority.
**Design review**: architecture PASS (0 critical/high,2 advisory medium), security PASS0 at306343e;
  fail-fast stopped-zero observation and safe unload/reload instructions incorporate both advisories.
**Attempts**: baseline107PASS; focused86PASS/4fixture-expectation FAIL; full1045PASS/3old-exit-assertion
  FAIL/1skip; corrected full1048PASS/0FAIL/1skip. No production correction after initial implementation.
**Implementation review**: all four lenses PASS at efc7ff9; architecture/ergonomics each retained
  one advisory medium about uninstall discarding bootout failure. Docs now require actual unload
  confirmation; failure propagation is explicitly carried by008. Scoped review follows.
**Next**: required CI, four owning implementation reviews, exact-head review gate and authorized merge.

**Review**: round 2 · 2704470aad6c111652a8eddbc3be3536191a730e..efc7ff902b7766f05e572efece059c80017651a2 · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/18#issuecomment-6466130069
**Review**: round 2 · 2704470aad6c111652a8eddbc3be3536191a730e..efc7ff902b7766f05e572efece059c80017651a2 · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/18#issuecomment-6466130577
**Review**: round 2 · 2704470aad6c111652a8eddbc3be3536191a730e..efc7ff902b7766f05e572efece059c80017651a2 · eng-arch · PASS · 0 critical, 0 high, 1 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/18#issuecomment-6466131420
**Review**: round 2 · 2704470aad6c111652a8eddbc3be3536191a730e..efc7ff902b7766f05e572efece059c80017651a2 · eng-ergonomics · PASS · 0 critical, 0 high, 1 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/18#issuecomment-6466132106
