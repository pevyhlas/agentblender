# Change: Diagnose and correct installed native-worker startup timeouts

**Opened**: 2026-10-26 · **Parts**: P1, P3 · **Journeys**: J-01, J-06 · **Tier**: T2 · **Parent**: 008-agent-operability

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

Continue the owner's selected startup-reliability priority after child018 merged. Installed real
workers twice timed out in `warm`; a passing failed-worker stand-in proved logging/status only.
Establish the cause and restore bounded real-worker startup under the existing contracts.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L2 | P1 worker startup / P3.B-02 | diagnosis first; correction follows established cause and reviewed contract | no timeout or warm-bypass decision assumed |

## Bounds

<!-- From `.prdkit/scripts/bash/bounds.sh <id>`: each constraint, non-goal or quality this change
could break, with how it holds; then "Others in scope: hold". A conflict goes to the owner inbox
with both sides, never resolved here. -->

- C-01/C-02/C-08: existing Blender binary and project-owned worker only; no new dependency,
  host-side bpy or redistributed Blender.
- C-03/C-04: isolated loopback daemon, private fixture store/runtime/config, synthetic sentinel;
  no live user service/desktop, telemetry or credential changes. Retain metadata-only diagnostics.
- Q-01/Q-04: keep warm rendering, timeouts, process cleanup and session isolation meaningful.
- C-09/C-12: required hosted gates remain; no hardware qualification or advisory-lane promotion.
- Others in scope: hold.

## Approach

Public baseline38d3db8544e675dbcb0d54c90bc3a633076ed0e9 (PR14). Reuse P3.B-02 and supervisor
Spawning→Warming→Ready, framed single-request transport and failed-worker cleanup. Inspect retained
PR12/PR14 evidence before another execution. First diagnostic isolates one installed floor worker,
capturing phase/time/process-resource metadata under the unchanged60s bound. It is not a retry
claiming acceptance. No production change until the cause is supported; material changes to pool
scheduling, warm contract or process policy receive independent design review first.

## Tasks

- [ ] T001 Preserve prior attempts and establish where/why native warm stalls with one bounded
  instrumented installed run; inspect supervisor/pool/worker and private child resource facts.
- [ ] T002 Apply only a supported correction; review consequential design before code and add
  independent fault/cancellation/isolation regression proof appropriate to the cause.
- [ ] T003 Verify real installed startup and first session/render, source regressions, owning-domain
  reviews and exact-head CI. Keep unrelated service carry-overs/hardware/release outcomes open.

<!-- Tiny fixes stay inline. Optional detail: .prdkit/cache/implementation/<id>.md;
useful context/progress on the same PR. Product/programme discovery uses workflows/planning.md. -->

## Checks

Native proof must execute actual Blender through an isolated installed tool and retain first-failure
evidence. An HTTP health response alone is not worker readiness. Preserve timeout poisoning/kill,
typed stage diagnostics, source identity and ready-worker semantics. No sleep or larger timeout
may replace a diagnosed correction. Use actual loopback/private fixtures, never the user's daemon.

## Acceptance

<!-- Required PR obligations. Distinguish later non-gating runtime observations on the PR before
execution; existing required checks cannot be demoted to finish. Live-only claims need live proof.
Keep these five columns for status helpers, including on a parent programme. Result is exactly
NOT_RUN/PASS/FAIL/BLOCKED/NOT_APPLICABLE; evidence/revision/environment go in Check or below the table. -->

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P3.B-02; R-11 | observations01/02 plus private phase-instrumented03: native render dominates successful warm; timed-out cause remains unknown | inherited plus3 diagnostic observations | BLOCKED |
| A2 | P1/P3 lifecycle; Q-04 | supported correction plus independent cleanup/cancel/isolation regression or evidence-based no-code disposition | 0 corrective source attempts | NOT_RUN |
| A3 | R-11; P3.B-02 | observation01 reached ready;02 failed warm before first session/render | inherited plus2 unchanged-source observations | FAIL |
| A4 | Delivery | applicable named reviews, unit/native selections and exact-head required CI | 0 | NOT_RUN |

## State

**Step**: NEEDS_DECISION — bounded diagnostic sequence finished; no supported production repair
**Waiting on**: owner selection of a controlled profiling increment or a changed warm requirement.
**Authority**: owner accepted the proposed AGD input/AgentBlender startup priorities on2026-10-26;
constitution permits bounded feature commit/push/PR and reviewed green merge. No live deployment.
**Inherited outcome history**: PR12 installed baseline failed warm with overlapping cold workers;
harness correction1 waited for a ready floor after the source suite and passed delivery, but did
not clear concurrent-start reliability. PR14's separate installed run failed a single floor warm
at60s before opening a session. Stand-in logging proof passed, not native health. Source repairs0.
No failed evidence or limits reset. Prior logs: retained016 `verification-attempts.md`,018
`installed-01.log`/`installed-proof-01` and PR12/14. The solo-floor failure weakens the earlier
contention-only hypothesis; native stderr/output was empty, so the cause remains unknown.
**Diagnostic observations**: unchanged installed package from child018 (source64e4cde; relevant
worker/supervisor source unchanged on public baseline). Observation01 reached ready at20.202s
(supervisor18.485s), but its sampler inspected only the main thread's child list and missed the
worker spawned by a .NET thread. It proves readiness only, not process-resource or cleanup facts.
Observation02 corrected that sampler to enumerate every task's children; the single floor worker
timed out at the unchanged60s warm bound. It held30 threads and accrued approximately117s user
CPU time, rather than sleeping for the whole timeout; the final sample confirms the child exited.
No session/render was attempted after the warm failure. Native output remained empty. This
refutes a permanently blocked/no-CPU explanation but does not locate shader compilation, driver,
render or journal work. The intermittent20s success does not clear prior failures. No product
source change or corrective native attempt has occurred in019; diagnostic runs2, one failing.
**Independent assessment**: architecture diagnostic-design PASS with1 advisory medium: warm lacks
phase visibility. [Report](https://github.com/pevyhlas/agentblender/pull/15#issuecomment-6428443935).
Adopted its one-run metadata-only instrumentation in a private copy of the installed worker;
installed host/package and tracked product source are unchanged. Original/instrumented module
hashes and exact diff are retained. Private TMPDIR is an explicit isolation difference; no
timeout, engine, sample count, journal, protocol or scheduling policy changes.
**Observation03**: ready at39.980s. Native64px/one-sample EEVEE render/write took35.984s wall and
91.538s CPU; restoration, journal and reply then completed within0.001s. All10 flushed markers
were retained. The observed child exited. Cgroup counters report no quota throttling or OOM;
host scheduling pressure was present, not established as the cause. No first session/render was
attempted. This intermittent success identifies the dominant phase in this run, not the cause of
the earlier timeout; it cannot pass A1/A3. Original package SHA256dc1b171f…18d5; private worker
SHA2563bad8654…b98. Full identities/raw results live in ignored019 evidence and the PR summary.
**Attempts and stop**: inherited outcome history remains above;019 has3 diagnostic observations,
one reproduced timeout and two readiness observations (one instrumented), zero production repairs.
Stop native reruns here: another uninstrumented success/failure would not settle the cause. No
further execution allocation is inferred from a reviewer recommendation.
**Alternatives**:
1. Recommended: one controlled official-Blender profiling increment, using its built-in render
   diagnostics and existing standard host observations, capped at2 executions. Identify the actual
   EEVEE graphics implementation and failing native subphase before any repair; preserve60s warm.
2. Relax the eager-render readiness requirement: design lazy first-render preparation or an
   explicitly longer configured cold-start allowance. This changes the warm/latency promise and
   needs owner intent plus architecture review; it is not a tactical fix.
3. Defer native investigation and continue independent service-lifecycle hardening while this
   failing outcome remains open. Default until an owner selection; no qualification or release.
**Next**: owner inbox decision; draft PR15 preserves source availability, observations and blockers.
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
