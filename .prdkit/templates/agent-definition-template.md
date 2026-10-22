---
name: agent-name
domain: Domain / Role
tier: standard
description: One line — when this agent should be dispatched.
# harnesses: [claude, opencode]   # optional; default = every configured harness
# source: agent-library           # optional provenance, never projected
---

<!-- Neutral definition under `.prdkit/agents/<name>.md`. Harness projections are rendered from it
by `.prdkit/scripts/bash/render-agents.sh`; edit here, never the projection's model or body.
`tier` is verydeep, deep, standard or light; the tier map in `.prdkit/memory/agents.md` resolves
it. Keep the body harness-neutral. -->

# <Agent title>

## Role

<What this agent is responsible for, and where that ends.>

## Method

<What it reads, in what order, and what it must not do. A reviewer reads the package, the change
record and the spec sections it is given by name, never a glob over `specs/`.>

## Output

<The exact shape of what it returns.>
