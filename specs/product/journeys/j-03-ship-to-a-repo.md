# J-03 Ship to a repo (hypothesis)

**Who**: asset agent, then project developer · **Area**: Ship · **Surfaces**: the `abl` CLI or
MCP; git

## Intent

Put finished design work where the project lives, so that any clone can rebuild and verify it,
without committing render outputs or a machine-local store.

## Situation

A frozen artifact exists in a machine-local store, which is disposable. The project repository is
the durable home; whoever clones it has the pinned Blender but not this store.

## Scenario

1. The agent exports the frozen artifact as a bundle: `artifact.json` plus `base.blend`.
2. The developer commits the bundle to the project repository.
3. On another machine, an agent imports the bundle; the import checks it before writing anything.
4. A replay on a fresh worker passes, and renders are regenerated from it.

## Variations

- **A corrupted or tampered bundle**: import refuses it with no store change.
- **The same bundle again**: a no-op; different content under an existing name is refused.
- **Another Blender version**: replay reports both versions instead of a silent mismatch.
- **Re-export unchanged**: byte-identical, so git shows no diff.

## Success

Any clone reconstitutes the design and replay passes (R-03, R-04, Q-03). For the business: asset
work survives machines and is reviewed like code.
