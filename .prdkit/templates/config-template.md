# Optional settings

Unset values use the defaults. Settings never grant authority or weaken checks.

## Configuration

- context-budget-bytes: 16384

## Meaning

`context-budget-bytes` bounds each agent context file (`CLAUDE.md`, `AGENTS.md`, ...); doctor
reports a larger file, and 0 disables the check. Optional adoption-gate keys for
`.prdkit/scripts/bash/check-adoption.sh`: `adoption-pin` (the expected kit commit),
`adoption-manifest-sha256` and `adoption-source` (when the kit is not at `.product-kit`); update
them together on every kit bump.
