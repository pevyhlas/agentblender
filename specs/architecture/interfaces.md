# AgentBlender — interfaces

| Interface | Owner part | Consumers | Promise | Schema in code |
|-----------|------------|-----------|---------|----------------|
| Op surface (MCP tools = CLI verbs = REST ops) | P1 | agents, CLI | same ops and semantics on every surface; experimental, may change | `src/Abl.Core/OpSurface.cs`, [public surface](../../docs/public-surface.md) |
| Host verbs (`setup`, `doctor`, `config`, `daemon …`, `mcp config`) | P1 | agents, operators | CLI only, never mutate through `/api/ops`; doctor check IDs and their order are a contract | `src/Abl.Cli/HostVerbs.cs`, `src/Abl.Cli/Host/` |
| `daemon_status` op | P1 | agents, CLI | the same object on MCP and REST; never a token value | `src/Abl.Core/Lifecycle/DaemonStatus.cs` |
| Configuration file `version: 1` | P1 | operators, `abl setup` | strict: unknown keys, comments and other versions refused | `src/Abl.Core/Config/` |
| Worker protocol | P1 | P3, P5 | host and bundled worker ship together; `hello.capabilities` gates newer features; an older hello shape is `worker_upgrade_required` | `src/Abl.Core/Workers/`, [worker protocol](../../docs/worker-protocol.md) |
| Bundle (`artifact.json` + `base.blend`) | P4 | project repositories | `formatVersion` 1; deterministic bytes; the base hash is the import gate | `src/Abl.Core/Store/CheckpointStore.cs` |
| Delivery payload (AGD provided-artifact v1) | P1 | the AGD receiver | the exact AGD v1 descriptor and PNG; a producer receipt v2 outside the payload | `src/Abl.Core/Delivery/`, [still contract](../../docs/still-delivery.md) |
| Inspection profile `abl.scene-inspection.v1` | P1 | agents | versioned, bounded, read-only | `src/Abl.Core/Sessions/SceneInspection.cs`, [inspection contract](../../docs/scene-inspection.md) |
| Telemetry names (`abl_*`) | P1 | the operator's collector | bounded labels op, outcome, tier | `src/Abl.Core/Telemetry/` |
| Release bundle (`.nupkg`, `SHA256SUMS`, `provenance.json`) | P1 | installers | built only after a temporary install passes `abl doctor --quick` | `scripts/release-bundle.sh` |

| External system | Used for | By | When it fails |
|-----------------|----------|----|---------------|
| Blender 5.2.x (user-installed) | every scene op and render | P3 | setup and doctor fail with the found and supported versions; the daemon does not start a worker |
| Cycles GPU backends (OptiX, Metal) | `cycles_gpu` renders | P3 | `gpu_unavailable` with a hint, or the CPU under `lease=cpu_fallback` (R-13) |
| AgentGraphicDesign receiver | checking delivered stills | the consuming project | the project does not admit the still; AgentBlender is unaffected |
| An OTLP collector (optional) | telemetry | P1 | export fails quietly; ops are unaffected |
| An MCP client (an agent's harness) | calling the op surface | agents | the CLI covers every op |
