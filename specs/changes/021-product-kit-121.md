# Change: Adopt Product-Kit 1.2.1 and reconcile resolved inbox direction

**Opened**: 2026-10-27 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: none

## Intent

Owner request2026-10-27: adopt Product-Kit1.2.1 for AgentBlender and AgentGraphicDesign.
This transaction upgrades AB's existing vendored copy; AGD has its own change024. Reconcile
answered inbox entries into their canonical destinations with provenance and outstanding holds.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L1 | operations | vendored1.2.1, observed source and notices; open-only inbox adoption | project authority preserved |
| L1 | existing delivery/desktop deferrals | retain meaning and provenance at canonical destinations | no new product decision |

## Bounds

- C-01/C-02: pinned MIT developer-tooling source, Product-Kit/Spec Kit notices retained; no worker changes.
- C-04/C-05: project ledger remains backend:none, no credentials/paths in committed evidence or new
  telemetry enablement; review package evidence stays ignored and bounded.
- C-09/C-12: required hosted CI retained; advisory/native qualification limits unchanged.
- Others in scope: hold.

## Approach

Baseline379be62568a28355a12fe6cb3fcf2661a1be1d7f. Annotated upstream tagv1.2.1 peels to
589589aeae2791d7e509214b9fb82b014bd59f82. Use its installer --check, then --upgrade --copy;
preserve customized constitution, agents, backend configuration and harness projections. Update
manifest hash/source/notice references. Review unmodified template upgrades rather than force
overwriting project memory. Apply release adoption guidance: remove resolved inbox entries only
after checking canonical meaning/provenance and transferring remaining work/holds.

No application changes, dependency graph changes, hardware execution, release or deployment.
This is a reviewed upstream maintenance uptake, not a newly designed product capability.

## Tasks

- [x] T001 Upgrade pinned copy, provenance and notices; prove customized policy/projections preserved.
- [x] T002 Reconcile resolved inbox entries with a destination map, preserving open questions and limits.
- [x] T003 Exercise changed kit helpers and adoption/doctor/status, independent code/security/license
  reviews and exact-head required CI; finalize on this PR.

## Checks

Use upstream regression suites for changed review-package, ledger, inbox and worktree-numbering
behavior, plus consumer adoption gates and a real revision-bound review package. No new tests for
unchanged application behavior; required application CI still gates merge. An unavailable reviewer
shell/report path is recorded and does not require an impossible action from the reviewer.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | C-01; operations | source589589ae, manifestc26db47e;72 installed files byte-identical, both MIT notices verbatim, install/adoption8groups and policy hashes PASS | 1 | PASS |
| A2 | owner authority; C-04/C-05 | destination map, upstream804assertions, actual package preflight and7synchronized race scenarios PASS; fixture failure preserved | 1 | PASS |
| A3 | C-09; constitution | code/security/license PASS0; source1f2c8d2 requiredCI PASS in40570680960; final metadata-head CI remains a merge condition | 1 | PASS |

## State

**Step**: finalization
**Waiting on**: final metadata-head required CI and matched-head merge
**Review**: round 1 · 379be62568a28355a12fe6cb3fcf2661a1be1d7f..1f2c8d26fa7642523d4e061ec3338d38d2a85362 · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/17#issuecomment-6453388933
**Review**: round 1 · 379be62568a28355a12fe6cb3fcf2661a1be1d7f..1f2c8d26fa7642523d4e061ec3338d38d2a85362 · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/17#issuecomment-6451862027
**Review**: round 1 · 379be62568a28355a12fe6cb3fcf2661a1be1d7f..1f2c8d26fa7642523d4e061ec3338d38d2a85362 · eng-license · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/17#issuecomment-6451862779
**Authority**: explicit owner uptake request; constitution branch/PR/green-reviewed merge authority.
Native019 stays paused, five diagnostics, extra2/2 allocation exhausted, A1 BLOCKED/A3 FAIL; this
workflow change grants no additional diagnostic or warm-contract change. Desktop/release owner gates hold.
**Inbox reconciliation**: baseline `379be625:specs/inbox.md` is the provenance source. Public baseline
findings map to P5.I-01/P5.I-05, operations/Delivery and PR7; public-workflow/macOS/kit directions map
to operations with PRs6–10/13 and the current uptake. Pause-and-service-hardening maps to parent008
State and PRs15/16. Delivery-lane and desktop deferrals map to operations with D-12/D-15 triggers;
their original dates were not recorded and are not invented. All14 unanswered questions remain.
**Evidence**: `.prdkit/cache/evidence/021-product-kit-121/`: initial install check reports expected1.2.0
drift; upgrade/check-after, adoption8groups, doctor/status, policy hashes and upstream9suites PASS.
Doctor retains only existing archive/open-question advisories. Application source/CI/projections are
unchanged. Backend:none is unchanged; no external telemetry enabled. Actual round1 package preflight
passed inventory byte/hash and accessible input checks. Supplemental7synchronized race scenarios
include2guard-bypass negative controls, unchanged helper/driver hashes and environment. The initial
driver stdin-EOF fixture error after5cases is retained; bounded harness repair passed all7, no product fix.
Initial eng-code session cancelled without verdict; replacement named review completed PASS0 over the
same source and completed supplementary evidence. Security/license PASS0 remain applicable.
**CI**: source1f2c8d2 required Validate and Build/unit PASS in40570680960; macOS and Blender advisory
jobs also passed, without qualifying native019 or desktop/Metal. Final metadata-head CI remains required.
Durable evidence: https://github.com/pevyhlas/agentblender/pull/17#issuecomment-6451919567.
**Next**: finalize record/log on PR17, verify final-head required CI and review gate, then matched-head merge.
