# Change: agent operability

**Opened**: 2026-10-20 · **Parts**: P1, P3, P5 · **Journeys**: J-06, J-05 · **Tier**: T2 · **Parent**: none

<!-- Continues 0.x feature 008-agent-operability, kept open at the Product-Kit 1.1 restructure.
Its feature folder is summarized in specs/archive.md; details are in the private history. -->

## Intent

A fresh agent sets AgentBlender up alone (R-11, J-06), including on an Apple silicon Mac
(R-12). Built and merged: the Host/Origin guard (P1.B-02), the configuration resolver, Blender
discovery and version policy, runtime paths and the worker environment allowlist, the daemon
lifecycle and user services, `daemon_status`, `abl setup`, `abl doctor`, `abl mcp config`, agent
guidance, and the `agentblender` tool package with the release bundle and SDK policy
(P1 operability, D-16, D-18). What remains is hardening carried from reviews, one provenance
field, and the macOS qualification (an owner task). Inbox: "macOS qualification" and "First public
release" are this change's owner items.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L0 | C-12 | drop "qualification open" once T074 passes | R-12's marker goes with change 009-metal-gpu |
| L2 | P1.B-10, P1.B-13 | one path-trust primitive behind both | T021 |
| L2 | P1 delivery | provenance records `blender.allowUnsupported` use | T040 |

## Bounds

- C-03: holds: no new listener; the guard is unchanged.
- C-04: holds: qualification records redact host and chip details.
- C-12: holds: the macOS lane stays advisory until T074 passes.
- Others in scope: hold.

## Approach

Decided in the original design reviews (D-16). The T021 security design review in child 013
decided to refuse macOS allow ACLs for private path guarantees and apply the runtime final-directory
rule to state. Shared facts/caller migration landed in013; log-write/rotation hardening and native
acceptance are completed by015 (public PR #11).

## Tasks

- [x] T021 (child013 and child015) One path-trust primitive: add the owner uid and, on macOS, the `allow`-ACL flag to
  `PathFacts`; derive ownership from the uid and retire the `chmod` proof; discovery and
  delivery's system-alias predicate read mode, owner and ACL through the shared facts; update
  `FakeConfigFileSystem` and the trust tests. Fold `RuntimePaths.StateRunDir` onto
  `DaemonPaths.StateDir`; decide whether the state directory gets the runtime directory's
  final-component rule; open the daemon log only as a regular file the user owns (the reader
  half is built); the store-root gate moves onto the shared facts for every daemon verb.
  Log transaction in child015-log-safety from child013 design review: require private existing logs before rotation;
  protect directory identity against replacement by another uid, open without following final
  links and verify the opened object; validate every selected tail path, including derived and
  startup-failure paths. Native Linux/macOS behavior passed at9bf90fc in public PR #11.
- [x] T040 (child016-delivery-provenance, PR #12) Delivery provenance records `blender.allowUnsupported` use (the producing Blender's
  version and support) as an additive descriptor field, reviewed with the descriptor schema.
- [x] T056 (child018-logging-status-tests, PR #14) Tests: a `DesktopTier` unit test that two equal probes log one line and a changed
  result a second; an integration case that `ABL_LOG_LEVEL=Warning` prints no `info:` lines
  (and decides whether the startup summary is suppressed); request lines return with
  `Logging__LogLevel__Microsoft.AspNetCore=Information`; the unit-lane `daemon_status`
  stand-in builds from a real `DaemonStatus.Build()` with a redacted token.
- [ ] Service carry-overs from the lifecycle reviews: `IServiceManager` gains `Install`,
  `Uninstall` and `UnitPath`; a distinct refusal exit code with `RestartPreventExitStatus=` so
  a login job that refuses before the lock does not restart forever, doctor reporting a stuck
  job, and `daemon.out` rotation (tail only this start's bytes); one `desktop.tcpAvailable`
  encoding for `/healthz` and `daemon_status`; doctor `service.state` reports a job this shell
  cannot identify; a post-install systemd `LoadState` check with roll-back and a launchd
  definition comparison on reinstall (qualified with T074); tests that `uninstall-service`
  refuses an active job and that `stop` falls back to SIGTERM; stop the unit when
  `systemctl --user start` fails, with a journal hint; one "as the service sees it" resolver
  for `install-service` and `start`; write units 0600 or record why 0644; move the manager-kind
  output branch behind `IServiceManager`.
- [ ] T074 (owner) macOS arm64 qualification by an agent through its harness: every
  fresh-setup step passes, with the environment-scrub proof (variable names only),
  `abl doctor --json`, the session transcript, the harness tool call returning and whether the
  harness sandboxed the daemon; the MCP clients in use send no `Origin`; a later 10.0 SDK
  feature band builds the checkout without downloading an SDK; and the default `TMPDIR` passes
  the delivery guard's system-alias rule (P1.B-27), so the `TMPDIR` workaround in the
  constitution, `CONTRIBUTING.md` and the macOS CI lane can go. Inbox: "macOS qualification".
- [ ] After T074: make the macOS CI lane required (drop its `continue-on-error` in
  `.github/workflows/ci.yml`).
- [ ] T075 (owner) the go for the first release. Inbox: "First public release".

## Checks

Every new behavior names its test; T021's reviewed ACL and private-state decisions become P1
guarantees. Shared-facts migration preserves discovery trust anchors and store target-link semantics.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | R-12, C-12 | macOS arm64 qualification run (T074) | 0 | NOT_RUN |
| A2 | P1.B-10, P1.B-13 | trust tests over the shared facts and log transactions (T021); at9bf90fc: Linux863pass/1skip and macOS864pass/0skip, [run40240250983](https://github.com/pevyhlas/agentblender/actions/runs/40240250983), children013/015 | Linux4, native4 | PASS |
| A3 | R-08 | descriptor test for the provenance field (T040) | 0 | PASS — child016 source/native/installed proof on [PR12](https://github.com/pevyhlas/agentblender/pull/12#issuecomment-6413279391) |
| A4 | P1 operability | T056 source/installed logging-status checks PASS in PR #14; service carry-over tests remain | 0 | NOT_RUN |

## State

**Step**: build
**Waiting on**: the owner for T074 and T075
**Next**: bounded service carry-overs follow as separate reviewed increments. T056 source and
installed logging/status verification passed in PR #14, with code/security/ergonomics review.
The native worker warm observation from PR #12 remains unresolved: child018's auxiliary installed
run also timed out warming one worker (60s). No corrective native retry was made; the passing
failed-worker stand-in proves log/status semantics only. Keep that outcome's history across follow-ups.
Child019/PR15 retained that history and reproduced the single-floor timeout. Its third, phase-marked
observation reached readiness with36s inside native render/write; this does not establish the cause
of the failed run or first-session health. Three diagnostic observations are retained, no product
repair is selected, and the owner inbox holds the next bounded profiling/requirement decision.
T021 shared facts landed in [PR #9](https://github.com/pevyhlas/agentblender/pull/9); log transactions
and native acceptance are complete in [PR #11](https://github.com/pevyhlas/agentblender/pull/11).
