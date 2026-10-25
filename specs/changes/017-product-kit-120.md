# Change: Adopt Product-Kit 1.2.0 and refresh roadmap execution readiness

**Opened**: 2026-10-26 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: none

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

Owner direction: “please uptake the latest product kit and then see what are the next steps”.
Adopt the latest public release, 1.2.0, and reassess ready roadmap work against actual source/PR
state. The kit's new execution guidance must preserve this project's existing acceptance, review,
attempt limits and action policy. Public-release and hardware-qualification inbox items stay open.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L1 | operations | record Product-Kit 1.2.0 vendored source and manifest | tooling update, no architecture decision |
| Change | 008/A2 | put existing evidence in Check and retain a bare PASS token | status-helper compatibility; same evidence/attempts |

## Bounds

<!-- From `.prdkit/scripts/bash/bounds.sh <id>`: each constraint, non-goal or quality this change
could break, with how it holds; then "Others in scope: hold". A conflict goes to the owner inbox
with both sides, never resolved here. -->

- C-01: pin public MIT source and preserve verbatim Product-Kit/Spec Kit notices; licence review.
- C-04/C-09: no credential, live-host or CI-policy change; no machine paths in tracked evidence.
- C-08: developer workflow files only; no runtime dependency or worker/GPL change.
- Q-04: installer check before upgrade; preserve customized memory/reviewers and open obligations;
  verify manifest, source equality, idempotence, helper regressions and exact-head CI.
- Others in scope: hold.

## Approach

Baseline: public main `8bd52bb8df6a8edeb9feaed0f915b9d5c1d1902b`. Observed upstream release
[v1.2.0](https://github.com/pevyhlas/product-kit/releases/tag/v1.2.0), source
dff688c51d1cf2a09d456be7c5135404f2390eef. Retain copy-mode adoption; run the pinned source's
`init.sh --check` then `--upgrade --copy`. The precheck found no customized kit-owned files or
templates. Memory and reviewer definitions remain project-owned. Refresh release/manifest
identity and notices; new planning/implementation workflows and optional templates are upstream
files, not another project specification. Required checks are not reclassified as observations.

Independent concerns: eng-code (workflow/helper correctness and verification), eng-security
(installer/path and authority boundaries), eng-license (vendored inbound code) and eng-ergonomics
(agent-facing workflow guidance). Architecture/runtime seams are unaffected.

## Tasks

- [x] T001 Pin and install upstream 1.2.0; preserve project-owned policy and notices.
- [x] T002 Verify installed bytes, idempotence, adoption/doctor/status and upstream helper regressions.
- [ ] T003 Review the full uptake diff; publish/merge only with exact-head CI and review gates.
- [x] T004 Assess next work against main and the active provenance draft; publish this assessment on the uptake PR. Updating draft #12 to the merged kit follows this PR.

Next-work assessment (same product intent; dependency outcomes, not new requirements):

| Work | Outcome / canonical IDs | Design / parts | Stage / disposition | Depends on + required outcome | Completion checks |
|------|-------------------------|----------------|---------------------|-------------------------------|-------------------|
| 016 provenance, draft #12 | producing Blender policy retained, 008/T040, P1.B-25/26 | P1 delivery/operability | required now; design review next | independent architecture/security design PASS | receipt/snapshot integrity, real export and installed offline checks |
| 008/T056 | logging and status regressions | P1 operability | required now; subsequent bounded increment | provenance scope stable; independent test plan | deduplicated probes, warning filter, real status object |
| Service carry-overs | reliable user-service failure/recovery | P1 lifecycle | required; split by observable outcome | bounded design per service outcome | Linux/macOS service tests and retained failures |
| 008/T074 and 009 native GPU checks | full Mac/Metal qualification | P1/P3/P5 | required gate; owner environment needed | Apple silicon with reference Blender and qualification access | actual host/Metal evidence, not hosted unit CI |

## Checks

Run the upstream installer/adoption/review/finalization/status/workflow/safety suites against the
pinned source; prove installed managed files match it. Run project adoption, doctor, status and
public hygiene; preserve the exact project policy/reviewer bytes and existing open acceptance.
No new product tests: source/runtime behavior is unchanged; required CI still runs before merge.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | C-01, operations | `source-preservation.json`, `post-upgrade.log`, `adoption.log`: 72 managed files equal pinned source, both MIT notices verbatim | baseline | PASS |
| A2 | Q-04, action policy | `idempotence-upgrade.log`, eight `upstream-*.log` suites, `doctor.log`, `status.log`, `public-hygiene.log`; policy/reviewers unchanged | baseline | PASS |
| A3 | J-06 | next-work table checked against main and draft #12; independent review and exact-head CI remain required delivery gates | baseline | PASS |

## State

**Step**: uptake verification
**Waiting on**: independent reviews and exact-head CI.
**Authority**: owner's 2026-10-26 request above; constitution branch commit/push/PR and reviewed
exact-head green merge policy. Existing release/deployment and qualification gates remain.
**Attempts**: baseline only; repair 0. Eight selected upstream regression suites pass.
**Next**: verify/publish the uptake, finish applicable reviews and merge; refresh draft #12 and
report its design-review prerequisite. Raw evidence: `.prdkit/cache/evidence/017-product-kit-120/`.
**Review**: round 1 · 8bd52bb8df6a8edeb9feaed0f915b9d5c1d1902b..2ccc103879961b89b1c0a35da84242bf4149618e · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/13#issuecomment-6405013426
**Review**: round 1 · 8bd52bb8df6a8edeb9feaed0f915b9d5c1d1902b..2ccc103879961b89b1c0a35da84242bf4149618e · eng-license · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/13#issuecomment-6405013625
**Review**: round 1 · 8bd52bb8df6a8edeb9feaed0f915b9d5c1d1902b..2ccc103879961b89b1c0a35da84242bf4149618e · eng-ergonomics · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/13#issuecomment-6405013814
**Review**: round 1 · 8bd52bb8df6a8edeb9feaed0f915b9d5c1d1902b..2ccc103879961b89b1c0a35da84242bf4149618e · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/13#issuecomment-6405025761
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
