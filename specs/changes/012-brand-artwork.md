# Change: original procedural brand emblem

**Opened**: 2026-10-24 · **Parts**: P1, P3 · **Journeys**: J-01, J-02, J-03, J-04 · **Tier**: T2 · **Parent**: none

## Intent

Owner requests logos via the respective workflows. Create a sculptural AgentBlender emblem
through scene exec, preview, trajectory freeze/replay and verified still delivery.

## Bounds

- C-01, C-06: original procedural geometry/materials only; independent provenance review.
- C-02: Blender recipe under worker/; no host imports or redistributed Blender.
- C-07: AB owns the sculptural render; no 2D authoring functionality added.
- C-10: no Blender logo or implied endorsement. Others in scope: hold.

## Approach

Opposing beveled brackets and a raised center block express an editable three-dimensional
state. Coordinate with AGD's Offset Precision motif using a cool ceramic material. Export a
single verified still plus the recipe/replay source; final brand adoption is owner acceptance.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | J-01, J-03 | native session/exec/preview/freeze/replay | 1 | PASS: refined lighting and deterministic scene replay |
| A2 | J-04 | delivery export and offline verify | 1 | PASS: AB receipt/bytes and independent AGD RGBA8 decode |
| A3 | C-01, C-02, C-06 | independent artwork/provenance review | 1 | PASS: code/evidence, security and licence/provenance |

## State

**Step**: close
**Waiting on**: closure-head CI; prerequisite PR #7 must merge first.
**Next**: close this proposal-authoring record, retarget [PR #8](https://github.com/pevyhlas/agentblender/pull/8)
after #7, and merge once exact-head CI/review gates pass. Permanent identity selection remains
an owner decision outside this completed proposal-authoring scope.
**Hosted evidence**: [run 40114874578](https://github.com/pevyhlas/agentblender/actions/runs/40114874578)
passed required Validate/build-unit and advisory Blender/macOS jobs on the initial published
artwork head. Subsequent changes close parent records and record evidence only.
**Authority**: owner artwork request, standing feature commit/push/PR; no release/deployment.
**Base**: 1832b45, stacked on 011-public-hardening.
**Evidence**: `.prdkit/cache/evidence/012-brand-artwork/`; public original recipe and PNG in
`docs/brand/`. Two previews inspected; final delivery contact sheet inspected separately.
**Assurance**: original final script matches the delivery snapshot's last operation; historical
replay has two ops. A single-op fresh run was not separately qualified. Bounded binary inspection
passes: no text/private-path metadata; contact-sheet EXIF is only 72dpi resolution. Isolated
authoring daemon stopped with its workers verified; no live service was changed.
**Review**: round 1 · 5f241890dc4f952b87e35725f64e75e9f554c31c..1c76c2a6628b7ffc4642e7b2c1a349a1916c9d82 · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/012-brand-artwork/reviews-summary.md
**Review**: round 1 · 5f241890dc4f952b87e35725f64e75e9f554c31c..1c76c2a6628b7ffc4642e7b2c1a349a1916c9d82 · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/012-brand-artwork/reviews-summary.md
**Review**: round 1 · 5f241890dc4f952b87e35725f64e75e9f554c31c..1c76c2a6628b7ffc4642e7b2c1a349a1916c9d82 · eng-license · PASS · 0 critical, 0 high, 0 medium, 0 low · .prdkit/cache/evidence/012-brand-artwork/reviews-summary.md
**Review**: round 2 · a93b45807a5ca866efdfa3adde9968926ad30503..a681144e81ee34fa4cd9a3d02eacf246d483f929 · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/8#issuecomment-6387281809
