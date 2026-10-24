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
| A3 | C-01, C-02, C-06 | independent artwork/provenance review | 0 | NOT_RUN |

## State

**Step**: review
**Waiting on**: App credential blocks public push; final identity selection is owner acceptance.
**Next**: inspect render preview, deliver, verify and checkpoint original sources.
**Authority**: owner artwork request, standing feature commit/push/PR; no release/deployment.
**Base**: 1832b45, stacked on 011-public-hardening.
**Evidence**: `.prdkit/cache/evidence/012-brand-artwork/`; public original recipe and PNG in
`docs/brand/`. Two previews inspected; final delivery contact sheet inspected separately.
