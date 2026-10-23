# Change: Polish public documentation, adopt Product-Kit 1.1.1, and enforce public repository hygiene

**Opened**: 2026-10-23 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: none

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

Polish public documentation, verify hosted CI and public-data hygiene, and adopt the latest
public kit. Afterwards check stale PRs. Release, hardware and live-deployment questions stay
open; this maintenance does not decide them.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L1 | D-16, operations | SDK patch and kit provenance | no runtime contract or L0 change |

## Bounds

<!-- From `.prdkit/scripts/bash/bounds.sh <id>`: each constraint, non-goal or quality this change
could break, with how it holds; then "Others in scope: hold". A conflict goes to the owner inbox
with both sides, never resolved here. -->

- C-01: holds: retain full upstream MIT notices; independent licence review.
- C-02, C-03: hold: no worker or bind change.
- C-04: holds: audit public ancestry only; findings do not expose sensitive values.
- C-09: holds: hosted runners, pinned actions, read-only tokens, no secrets.
- C-10, C-12: hold: keep non-affiliation and exact platform limits prominent.
- Others in scope: hold.

## Approach

Keep the advanced guide as REFERENCE.md; make README a short start/decision page. Preserve
vendored-copy installation, upgrading from public Product-Kit v1.1.1 with source/manifest
hashes and full notices. Update the reference SDK to patch 10.0.113; no package graph or
platform expansion. Add only bounded regression protection, not a claim of complete PII detection.

## Tasks

- [x] Inspect public remote, green CI, rulesets, PRs and releases.
- [x] Polish README and preserve advanced reference and incoming anchors.
- [x] Upgrade kit/notices and SDK patch.
- [ ] Finish public-data audit and repeatable regression protection.
- [ ] Verify, independently review and integrate through exact-head hosted CI.
- [ ] Recheck stale PRs and report release blockers separately.

## Checks

No new R/Q/B/I. PackagingTests changes its exact SDK assertion with the reference patch,
not the policy. Verify links/examples, adoption/doctor, legal notices and hosted CI.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | J-06 | documentation links/examples and independent read | 0 | NOT_RUN |
| A2 | C-01 | adoption, doctor and licence review | 0 | NOT_RUN |
| A3 | C-04, C-09 | public audit, regression and workflow checks | 0 | NOT_RUN |
| A4 | D-16 | locked restore, build/unit/package and exact-head CI | 0 | NOT_RUN |

## State

**Step**: build
**Waiting on**: nothing
**Next**: finish hygiene checks, verify and review.
**Authority**: constitution permits commits, branch push/PR and green-reviewed merge;
release/tag and deployments stay owner-gated. No history rewrite or remote branch deletion.
**Branch**: 010-public-readiness; base a93b45807a5ca866efdfa3adde9968926ad30503.
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
