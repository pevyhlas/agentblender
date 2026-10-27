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
| A1 | P3.B-02; R-11 | observations01–05: successful warm dominated by native render/preparation;05 confirms NVIDIA OpenGL; timed-out cause remains unknown | inherited plus5 diagnostic observations | BLOCKED |
| A2 | P1/P3 lifecycle; Q-04 | supported correction plus independent cleanup/cancel/isolation regression or evidence-based no-code disposition | 0 corrective source attempts | NOT_RUN |
| A3 | R-11; P3.B-02 | 01 reached ready;02 failed warm;03–05 instrumented readiness does not clear failure or prove first session/render | inherited plus2 unchanged-source and3 instrumented observations | FAIL |
| A4 | Delivery | applicable named reviews, unit/native selections and exact-head required CI | 0 | NOT_RUN |

## State

**Step**: NEEDS_DECISION — approved2-execution profiling increment complete; no supported repair
**Waiting on**: next scope selection; further native execution is stopped at the allocated limit.
**Authority**: owner accepted the proposed AGD input/AgentBlender startup priorities on2026-10-26;
constitution permits bounded feature commit/push/PR and reviewed green merge. No live deployment.
The owner's subsequent approval on2026-10-27 selects the original profiling proposal: at most2 controlled
profiling executions, unchanged60s warm contract. This extends diagnostics only; inherited failures
and the original three observations remain. No production repair or runtime acceptance is implied.
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
**Approved profiling outcome**: observations04/05 consumed both additional executions. Private
exec wrappers added official native logging/GPU profiling to the installed binary; product source,
installed package,60s warm,64px/one-sample EEVEE, pool and environment policy stayed unchanged.
04 reached ready58.315s; render/write53.458s wall/123.665s CPU. 05 reached ready20.664s;
render/write18.363s wall/55.476s CPU. Both retained75 shader-start log messages. The largest gaps
between default-surface dependency and world-shader markers were28.960s and10.362s; these are
logged intervals, not measured individual shader durations or proof of a compiler defect.
05 used supported read-only getters inside its private worker to establish EEVEE's actual
OpenGL/NVIDIA implementation (driver595.71.05), excluding software rendering for that observation
only. 04's logs establish OpenGL but not its vendor/device. Both GPU traces are incomplete;
their recoverable CPU spans cover less than1s, not the full warm operation.
**Environment and cleanup**: both isolated workers exited, their private process groups were empty,
and daemons exited0. Desktop/telemetry remained off; no sessions opened. Shared-host pressure was
present. Separate long-lived workers were idle at preflight but activity occurred before04's
post-run sample; temporal overlap was not measured. 05's per-sample ambient Blender CPU counters
showed no increase. Visible cgroup counters showed no quota throttling/OOM. These are controlled
private workloads on a shared host, not an idle-host benchmark or causal load comparison.
**Profiling reviews**: initial architecture design FAIL (1 high/1 medium) exposed the false
assumption that daemon shutdown flushes Blender's profiler and incomplete helper cleanup scope.
One revision made CLOG/flushed phases primary, GPU traces opportunistic, and descendant/group
cleanup identity-bound; architecture re-review PASS (0 findings). Run05's getter/sampling delta
then received architecture and license/provenance PASS (0 findings each). These verdicts cover
private diagnostic design/source only, not a production correction or native acceptance.
**Attempts and stop**:019 now has5 diagnostics: one reproduced timeout and four readiness
observations (three instrumented); zero production repairs. The owner-approved extra2/2 are used.
No additional native run, changed deadline/readiness policy or source correction is inferred.
Failure evidence from02 and PR12/14 remains. A1/A3 stay blocked/failed: successful native
initialization intervals narrow the investigation, but no failed native subphase was captured.
Raw evidence, private source hashes/diffs, reviews and analysis are retained under ignored
`.prdkit/cache/evidence/019-worker-startup/`; scrubbed findings stay on draft PR15.
**Alternatives**:
1. Recommended: pause native investigation and continue independent service-lifecycle hardening
   from parent008. Keep019 open and native readiness/first-session acceptance unresolved.
2. Owner-allocated quiet-host investigation using off-the-shelf Blender/vendor diagnostics,
   explicitly controlled cold/warm caches and timestamped ambient load, capped at2 new executions.
   It could distinguish compiler/cache/scheduling contributions; it cannot guarantee reproduction.
   No shared driver/cache/service changes are authorized by the completed profiling increment.
3. Relax the eager-render readiness requirement: design lazy first-render preparation or an
   explicitly longer configured cold-start allowance. This changes the warm/latency promise and
   needs owner intent plus architecture review; it is not a tactical fix.
**Next**: owner inbox selection; no further native execution by default. Draft PR15 preserves
source availability, observations and blockers; no qualification, release or merge readiness.
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
