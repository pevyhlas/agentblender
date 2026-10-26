# Change: Pin logging levels and real daemon status in operability tests

**Opened**: 2026-10-26 · **Parts**: P1, P5 · **Journeys**: J-05, J-06 · **Tier**: T1 · **Parent**: 008-agent-operability

## Intent

Complete parent008/T056 under the owner's public-roadmap continuation. Operators need reliable
quiet logs and a status response that exercises the real shared builder, including token
redaction. This is verification of already-declared behavior, not a new public capability.

## Spec edits

| Level | ID | Edit | Note |
|---|---|---|---|
| L1 | observability / quiet logs | pin level filtering and explicit framework override in tests | startup summary is Information and suppressed at Warning |
| L2 | P1.I-06 | lifecycle stand-in uses actual status builder with a configured token | no handwritten status substitute |

## Bounds

- C-03/C-04: isolated loopback test listeners and sentinel credentials; no external desktop or live daemon.
- C-05: no telemetry change or content emission; tests pin existing log categories/levels only.
- C-09/C-12: existing hosted unit lanes; no platform qualification claim.
- Q-02/Q-04: bounded log assertions and timeouts; scoped fixtures dispose processes/sockets.
- Others in scope: hold.

## Approach

Baseline `80377b7bef34ee205afcc25515c1a6d6e6de0ab3` (provenance PR12 final tree; rebase onto
its merged public commit before publishing this child). Existing `DesktopTier.ProbeAvailableAsync`
logs once on the first observation and once per availability transition. Set probe TTL to zero
in tests and use real local TCP success/refusal, proving repeated observations do not log.

Existing `DaemonHost.QuietLogs` filters Abl categories at the effective configured level and
framework logs at Warning by default; explicit `Logging__LogLevel__Microsoft.AspNetCore=Information`
overrides that. Startup summary is logged at Information, so Warning suppresses it. Exercise actual
isolated daemon subprocesses and requests, then inspect flushed log bytes after stopping them.
This resolves T056's startup-summary detail by the existing level contract, without a new exception.

Replace the handcrafted `LifecycleCoreTests.Status` seed with `DaemonStatus.Build()` from bounded
fake-worker services. Preserve fixed PID/version/time and each lifecycle scenario's deliberate
mutations. Seed a nonempty sentinel desktop token and assert literal expected redaction plus
absence of the sentinel in JSON and text. Existing real daemon REST/MCP/CLI parity tests remain.

No general feature design is introduced. If these checks expose a behavior conflict, stop that
scope for an explicit correction/design decision rather than rewriting expected results.
Independent review: code/test evidence, security (token/log checks) and ergonomics (status/log
meaning). Architecture and licence are not triggered by test-fixture-only changes.

## Tasks

- [ ] T001 `DesktopTransportTests`: literal first/repeat/change/repeat log-count and message
  assertions, including level and no token content, with bounded real loopback probes.
- [ ] T002 `DaemonLifecycleTests.Operability`: Warning removes Information/startup-summary
  lines; explicit framework Information restores request lines. Real subprocess/config paths.
- [ ] T003 `LifecycleCoreTests`: real shared status-builder seed, literal redaction and unchanged
  lifecycle text/JSON assertions; meaningful mutations remain scenario-local.
- [ ] T004 Update P1 test references; focused/full unit and installed log/status checks; required
  independent reviews and exact-head CI/review gate; finalize this same PR.

## Checks

Use handwritten expected log counts/text and redaction values. No sleeps to manufacture state
transitions; bound real listener/refusal and daemon lifecycle waits. Use command-local umask022
and a short private temp directory for existing Unix socket fixtures, as learned in PR12.
Full unit lane excludes BlenderIntegration/MetalQualification/OptixQualification per constitution.
Installed checks use clean tracked-tree local bundles and isolated paths/ports, without installing
a user service. No actual GPU or desktop launcher required.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|---|---|---|---|---|
| A1 | T056; Q-02 | repeated equal probes log once, transition logs twice; exact Information text, no token | 0 | NOT_RUN |
| A2 | observability / quiet logs | actual daemon Warning suppresses startup/request Information; explicit framework override emits request lines | 0 | NOT_RUN |
| A3 | P1.I-06; C-04 | lifecycle fixture uses real status builder and configured sentinel token; expected redaction survives CLI forms | 0 | NOT_RUN |
| A4 | Delivery | focused/full unit, installed checks, named independent reviews and exact-head required CI | 0 | NOT_RUN |

## State

**Step**: ready bounded test implementation
**Waiting on**: T001–T004 evidence.
**Authority**: owner's public-roadmap continuation and constitution continuation/feature-branch
commit/push/PR/reviewed-green-merge policy. Parent008 explicitly names T056 next. No release or
deployment authority inferred.
**Attempts**: baseline0; no review or acceptance result yet. PR12's cold concurrent native-worker
warm timeout remains unresolved there; these logging tests do not qualify startup reliability.
**Next**: implement existing logging/status expectations; carry source evidence into its own PR.
