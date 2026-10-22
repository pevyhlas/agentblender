# Capabilities

Map recurring needs to available agents or humans. One capable session may cover several roles
where independence is not required; a reviewer never reviews its own implementation.

| Role | Available agent or human | Notes |
|------|--------------------------|-------|
| Intent owner | <human> | product direction, consequential choices |
| Implementation | <session or worker> | bounded changes |
| Independent review | <reviewer context> | review by tier |

## Optional tier map

Neutral definitions in `.prdkit/agents/<name>.md` name a tier, never a model;
`.prdkit/scripts/bash/render-agents.sh` resolves it here. Opt in to the shared baseline with
`render-agents.sh --refresh-baseline <agent-library checkout>`, then write these lines outside
the code block:

```text
Tier baseline: agent-library tiers.json@<commit written by --refresh-baseline>
Banned models: <exact ids never to project, comma-separated; optional>
Hand-owned agents: <projection names kept without a definition, comma-separated; optional>

| Local tier | Library tier |
|------------|--------------|
| <tier used by a definition> | <verydeep, deep, standard or light> |

| Scope | Harness | Model | Variant | Reason |
|-------|---------|-------|---------|--------|
| <tier:NAME or agent:NAME> | <claude, opencode or *> | <exact id or blank> | <variant or blank> | <why it deviates> |
```

Every override needs a Reason. Without a baseline, a literal
`| Tier | Claude Code model | OpenCode model | OpenCode variant |` table still renders.
