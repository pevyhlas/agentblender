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

- [ ] Bind/environ fixes with positive and negative regressions.
- [ ] Explicit worker inputs and tracked-archive release staging; poison-file package proof.
- [ ] Independent code, security and licence reviews; publish/CI when credential refresh works.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P5.I-01 | public/mapped/wildcard rejection and private acceptance, direct Start bypass | 0 | NOT_RUN |
| A2 | P5.I-05 | child environment canaries and headless argument contract | 0 | NOT_RUN |
| A3 | C-04 | package excludes ignored/untracked worker poison, normal bundle proof | 0 | NOT_RUN |
| A4 | C-01, C-03 | named independent reviews and exact-head hosted CI | 0 | NOT_RUN |

## State

**Step**: build
**Waiting on**: app token refresh for public pushes (403 despite owner adding repositories).
**Next**: fix and verify locally, then checkpoint and publish.
**Authority**: standing commit/branch push/PR/green-reviewed merge; no release or deployment.
**Base**: 588d820 (stacked on 010-public-readiness, public ancestry only).
