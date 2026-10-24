# Change: Polish public documentation, adopt Product-Kit 1.1.1, and enforce public repository hygiene

**Opened**: 2026-10-23 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: none

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

Owner follow-up (2026-10-24): make both READMEs very succinct, retain public Product-Kit,
then fix confirmed issues, advance the roadmap and generate logos through the tools' workflows.
README is shortened again here; implementation and artwork continue in bounded child work.

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
- [x] Finish bounded public-text/history audit and repeatable regression protection.
- [ ] Verify, independently review and integrate through exact-head hosted CI.
- [x] Recheck stale PRs (none open) and report release blockers separately.

## Checks

No new R/Q/B/I. PackagingTests changes its exact SDK assertion with the reference patch,
not the policy. Verify links/examples, adoption/doctor, legal notices and hosted CI.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | J-06 | documentation links and unchanged boundary test; independent code/ops read | 2 | PASS locally |
| A2 | C-01 | adoption, doctor (existing advisories) and licence review | 1 | PASS |
| A3 | C-04, C-09 | bounded public audit, guard self-tests/scan and CI-policy mutants | 1 | PASS within stated limits |
| A4 | D-16 | locked restore, build/unit/package and exact-head CI | 3 | LOCAL PASS; hosted CI BLOCKED |

## State

**Step**: verify
**Resume**: 2026-10-24 owner reports app access added; current credential still returns push
403. No new remote branch or PR exists. Public v1.1.1 remains the latest Product-Kit release.
**Waiting on**: repository write access for the automation credential (push 403); then exact-head CI.
**Next**: restore the app's repository write access, push this branch, open a PR, and run required
exact-head hosted CI. Do not merge or publish a release while those gates remain blocked.
**Authority**: constitution permits commits, branch push/PR and green-reviewed merge;
release/tag and deployments stay owner-gated. No history rewrite or remote branch deletion.
**Branch**: 010-public-readiness; base a93b45807a5ca866efdfa3adde9968926ad30503.
**Delivery**: LOCAL_ONLY; remote main is the base above; no remote feature branch or PR exists.
**Attempts**: initial local unit run failed (long temporary path, uncommitted SDK pin and README
boundary assertion); short-temp run isolated the README and umask-sensitive fixture failures.
The committed-pin, restored-guidance, short-temp/umask-022 run passes: 692 core and 103
non-Blender integration tests, with one read-only-filesystem core skip. Build has zero warnings
and errors. Isolated package/install/doctor and missing-worker-negative checks pass at c882d40.
The final developer-guard fixture canonicalizes its temporary root (review medium, fixed in
fix round 2); normal and symlinked-temp self-tests pass. Raw failed attempts stay in cache.
**Evidence**: `.prdkit/cache/evidence/010-public-readiness/` holds restore/build/unit/package
logs, the bounded six-commit/366-blob public scan, upstream proof and all review transcripts.
Public Product-Kit v1.1.1's complete upstream local suite passes all 28 test files; its exact
source also has green hosted Linux and macOS CI. This verifies kit adoption, not our hosted CI.
No real-Blender, Metal/OptiX or live desktop qualification was run for this maintenance.
**Review**: round 1 · a93b45807a5ca866efdfa3adde9968926ad30503..7b9b262d5323ac8eb775db49348fcc3ce3457491 · eng-code · FAIL · 0 critical, 1 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/code-review.jsonl
**Review**: round 1 · a93b45807a5ca866efdfa3adde9968926ad30503..7b9b262d5323ac8eb775db49348fcc3ce3457491 · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/security-review.jsonl
**Review**: round 1 · a93b45807a5ca866efdfa3adde9968926ad30503..7b9b262d5323ac8eb775db49348fcc3ce3457491 · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/ops-review.jsonl
**Review**: round 1 · a93b45807a5ca866efdfa3adde9968926ad30503..7b9b262d5323ac8eb775db49348fcc3ce3457491 · eng-license · FAIL · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/license-review.jsonl (upstream access only)
**Review**: round 1 · a93b45807a5ca866efdfa3adde9968926ad30503..7b9b262d5323ac8eb775db49348fcc3ce3457491 · eng-license · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/license-review-resolved.jsonl
**Review**: round 2 · 7b9b262d5323ac8eb775db49348fcc3ce3457491..c882d404c0b41c7151472c88b60050199d339586 · eng-code · PASS · 0 critical, 0 high, 1 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/code-review-round-2.jsonl
**Review**: round 2 · 7b9b262d5323ac8eb775db49348fcc3ce3457491..c882d404c0b41c7151472c88b60050199d339586 · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/security-review-round-2.jsonl
**Review**: round 2 · 7b9b262d5323ac8eb775db49348fcc3ce3457491..c882d404c0b41c7151472c88b60050199d339586 · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/ops-review-round-2.jsonl
**Review**: round 3 · c882d404c0b41c7151472c88b60050199d339586..fe1bf121e4ba2928e8c799b7c9e5b67cb853f234 · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/010-public-readiness/code-review-round-3.jsonl
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
