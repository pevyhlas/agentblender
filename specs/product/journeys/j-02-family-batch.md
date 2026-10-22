# J-02 Family batch (hypothesis)

**Who**: asset agent · **Area**: Design · **Surfaces**: MCP tools or the `abl` CLI

## Intent

Turn one frozen design into a family (colourways, sizes, attachments, an icon set) and judge
every variant in one look, attending only to the few that come out wrong.

## Situation

A frozen artifact exists (J-01). The family may be tens of variants; looking at each one alone
would cost a full image of context per item.

## Scenario

1. Writes a parameter table: one row per variant, one column per parameter.
2. Runs the batch; each variant renders to its own path on disk.
3. Reads one contact sheet with every variant labelled, and judges them together.
4. Fixes the outliers in the table or the design and re-runs only what changed.

## Variations

- **A heavy batch**: with a desktop GPU configured and reachable, the batch runs there and the
  pixels come back to the host (J-05).
- **A variant fails**: the batch stops at that variant and fails with one structured error;
  later variants do not render and no contact sheet is made. Renders already written stay on
  disk in the batch's output directory; fix the table and re-run.
- **A repeat run**: default output paths are unique per invocation, so an earlier run is never
  overwritten.

## Success

Every variant is on disk and verified from exactly one image (R-05, Q-02). For the business: the
cost per asset falls with batch size.
