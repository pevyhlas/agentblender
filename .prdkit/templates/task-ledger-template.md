# Optional execution ledger

## Configuration

- backend: none
- command: ./scripts/my-ledger-adapter.sh
- events: all
- mode: best-effort
- operator: none

Events emitted with `.prdkit/scripts/bash/ledger-emit.sh <event>` are appended to
`.prdkit/ledger/events.jsonl`. `backend: command` also pipes each event to one executable,
repository-contained path without arguments; adapters own credentials and network access.
`.prdkit/scripts/bash/ledger-spool-adapter.sh` mirrors events into a per-host spool that
survives worktree deletion. `events` filters delivery; `mode: strict` returns adapter failure to
the caller; `operator: git` adds the Git identity. Events are diagnostics, never approval.

## Vocabulary: prdkit.ledger.v1

The `feature` id is the change id. Emit, best-effort, at actual boundaries:

| Event | Boundary |
|-------|----------|
| feature.created | Change opened |
| review.recorded | Reviewer verdict recorded |
| evidence.recorded | Acceptance result recorded |
| blocker.opened / blocker.resolved | Blocker or stall lifecycle |
| feature.merged | Closing PR merged |

`tasks.generated`, `task.state_changed`, `phase.changed`, `readiness.changed` and
`exploration.converged` stay valid for 0.x spools; 1.0 workflows do not emit them.
