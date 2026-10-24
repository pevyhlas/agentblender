# Change: public launcher and package hardening

**Opened**: 2026-10-24 · **Parts**: P1, P5 · **Journeys**: J-05, J-06 · **Tier**: T2 · **Parent**: none

## Intent

Owner direction: identify and fix issues in both public projects, then continue the roadmap
and create logos through their workflows. Resolve the three baseline review findings from
010: public launcher binds, credential inheritance and ignored worker files in packages.
The remaining operability roadmap stays in 008; hardware qualification still needs its host.

## Spec edits

- P5.I-01: enforce concrete private/loopback addresses, not merely non-wildcards.
- P5.I-05: desktop workers receive only the documented minimal process environment.
- Operations: release inputs are an immutable tracked archive and worker content is explicit.

## Bounds

- C-03: enforce existing private-network boundary; no daemon/authentication policy expansion.
- C-04: no token/environment values in results; synthetic canaries only.
- C-02, C-01: no Blender code/dependency added; retain MIT/GPL process boundary and notices.
- C-12: keep Windows launcher prerequisites; no new qualified host profile.
- Others in scope: hold.

## Approach

Validate resolved and directly configured endpoints before Start: IPv4 RFC1918 and loopback,
IPv6 loopback and ULA; normalize IPv4-mapped IPv6. Reject all other addresses, including
documentation, multicast and public ranges. Hostnames are resolved once and the same concrete
endpoint is validated and bound. Keep child PATH/home/locale/temp and Windows system/profile
basics; no arbitrary passthrough. Use an explicit worker-module list shared by CLI and launcher.
Release packing stages git archive of one captured commit, never ignored working outputs.

## Tasks

- [x] Bind/environ fixes with positive and negative regressions.
- [x] Explicit worker inputs and tracked-archive release staging; poison-file package proof.
- [x] Independent code, security, licence and operations reviews; one fix round completed.
- [ ] Publish/CI when credential refresh works.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P5.I-01 | public/mapped/wildcard rejection and private acceptance, direct Start bypass | 2 | PASS, includes live mapped-loopback connect after review correction |
| A2 | P5.I-05 | child environment canaries and headless argument contract | 1 | PASS |
| A3 | C-04 | package excludes ignored/untracked worker poison, normal bundle proof | 2 | PASS at 5f24189; SDK pin regression also passes |
| A4 | C-01, C-03 | named independent reviews and exact-head hosted CI | 1 | reviews PASS after fix round 1; hosted CI BLOCKED by App403 |

## State

**Step**: verify
**Waiting on**: app token refresh for public pushes (403 despite owner adding repositories).
**Next**: refresh app installation credential, publish stacked branches, obtain exact-head CI.
**Authority**: standing commit/branch push/PR/green-reviewed merge; no release or deployment.
**Base**: 588d820 (stacked on 010-public-readiness, public ancestry only).
**Evidence**: `.prdkit/cache/evidence/011-public-hardening/`: zero-warning build; initial
targeted run 71 passed and one README assertion failed (corrected); full unit lane 813 passed,
one platform skip; fix-round targeted 35 passed. Package/install/doctor, unknown-file canaries,
synthetic multi-SDK regression, shellcheck, adoption/doctor and CI policy pass. Native Windows,
live desktop and hardware qualification are not claimed. One review fix round used.
**Review**: round 1 · 588d8205d0017ea5d07163e5de7db5c92f56427f..1832b45cbe4d436f26222c18f5186d6cfafbc5bf · eng-code · PASS · 0 critical, 0 high, 2 medium, 0 low · .prdkit/cache/evidence/011-public-hardening/reviews-summary.md
**Review**: round 1 · 588d8205d0017ea5d07163e5de7db5c92f56427f..1832b45cbe4d436f26222c18f5186d6cfafbc5bf · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/011-public-hardening/reviews-summary.md
**Review**: round 1 · 588d8205d0017ea5d07163e5de7db5c92f56427f..1832b45cbe4d436f26222c18f5186d6cfafbc5bf · eng-license · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/011-public-hardening/reviews-summary.md
**Review**: round 1 · 588d8205d0017ea5d07163e5de7db5c92f56427f..1832b45cbe4d436f26222c18f5186d6cfafbc5bf · eng-arch · FAIL · 0 critical, 1 high, 0 medium, 0 low · .prdkit/cache/evidence/011-public-hardening/reviews-summary.md
**Review**: round 2 · 1832b45cbe4d436f26222c18f5186d6cfafbc5bf..5f241890dc4f952b87e35725f64e75e9f554c31c · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/011-public-hardening/reviews-summary.md
**Review**: round 2 · 1832b45cbe4d436f26222c18f5186d6cfafbc5bf..5f241890dc4f952b87e35725f64e75e9f554c31c · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/011-public-hardening/reviews-summary.md
**Review**: round 2 · 1832b45cbe4d436f26222c18f5186d6cfafbc5bf..5f241890dc4f952b87e35725f64e75e9f554c31c · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/011-public-hardening/reviews-summary.md
