# Contract: Public Surface (MCP tools ≡ CLI verbs)

Owner part: P1 ([spec](../specs/parts/p1-host.md)). `FR-`/`SC-` references cite the 0.x feature
specs (feature 001 unless prefixed 002/003), summarized in [the archive](../specs/archive.md);
their full text is kept in the private development history.

One shared core (`Abl.Core`); MCP (streamable HTTP at `/mcp`) and CLI (`abl …` → REST
`/api`) are thin parallel layers. The contract test enumerates both surfaces and fails on
any op/semantic drift (FR-009). Daemon binds 127.0.0.1 only in 001 (FR-013).

| Op (MCP tool / CLI verb) | Inputs | Returns (token-economy contract) |
|---|---|---|
| `session_open` | project (required on every surface) | session id + worker readiness |
| `session_list` | — | terse table: id, project, state, head |
| `session_close` | session | the closed session id |
| `scene_state` | session, mode=delta | delta by default; full on request; terse text form |
| `scene_inspect` | session, typed request (objects/camera/landmarks and optional fact sections) | bounded structured transforms, camera/projections and declared raw facts; no head/journal/delta advance; [contract](scene-inspection.md) |
| `scene_exec` | session, script (Python source; the CLI reads it from `--script FILE`), params? (JSON object, the script's global `params`, `{}` when omitted) | state delta + duration; auto-checkpoint hash. The script runs inside Blender (`import bpy` works); on an exception the scene is restored to the pre-call head (`exec_failed`); past the exec timeout the worker is killed (`timeout`) and the error carries `lastCheckpoint`. Example and contract: [README, Op scripts](../README.md#run-it) |
| `scene_preview` | session, size=512, engine=eevee | ONE downscaled image (≤512 default, FR-004) + path |
| `checkpoint_save` | session, tags? | hash (explicit save = tagged tree node) |
| `checkpoint_list` | session? | terse tree: hash-prefix, parent, session, age, tags (ONE renderer serves MCP and CLI) |
| `checkpoint_branch` | checkpoint, project? | NEW session at that state (branch = fresh head, D-03) |
| `checkpoint_tag` | checkpoint, tags | — |
| `checkpoint_diff` | a, b | object-set delta between two checkpoints |
| `checkpoint_prune` | force? | deleted count + refused hashes; live heads and frozen refs ALWAYS refused, tagged refused unless force (FR-006/AC-3) |
| `trajectory_freeze` | session, name, from?, to? | frozen artifact name, op count, base checkpoint, expectedDigest (the replay gate) |
| `trajectory_export` | artifact, out | git-committable bundle at `<out>/<name>/` (artifact.json + base.blend); deterministic bytes (002 FR-002) |
| `trajectory_import` | bundle (dir) | reconstituted artifact (name, ops, base, digest, blender version); integrity-verified BEFORE any store write (002 FR-003/004) |
| `replay_run` | artifact | PASS/FAIL: canonical scene digest equality — verification is always on (R-03 gate); surfaces frozen-vs-current Blender version on mismatch (002 FR-005) |
| `render_final` | session \| checkpoint, engine, res, samples, out?, lease=wait | **path + metadata only, never pixels** (FR-005) |
| `batch_run` | artifact, param-table, res, columns | per-variant paths + ONE contact sheet image (FR-008) |
| `delivery_export` | artifact, out parent, zero/one param row?, engine, res, samples?, seed | fresh generation with strict `payload/artifact.json`+`still.png`, separate v2 producer receipt/snapshot/replay; ONE preview+terse paths over MCP, structured paths over REST |
| `delivery_list` | payload argument = generation parent | producer facts from outside-payload receipt; terse default, optional CLI `--json`; no admission/rights proof |
| `delivery_verify` | payload argument = generation parent | bounded encoded/source self-consistency report; pixelsDecoded/admitted/provenanceVerified/sourceReplayVerified=false; offline Core CLI with optional `--json` |

Error shape everywhere: `{kind, message, hint?, lastCheckpoint?}` — a dead worker's error
carries `lastCheckpoint` so the recovery move (`checkpoint_branch`) is one call
(FR-010/US3).

Telemetry (FR-012): spans carry op/session/project/outcome/tier/duration/payload_bytes;
metric labels are bounded op/outcome/tier, never session/project IDs. Payload size is logical
returned UTF-8 content (serialized JSON for structured content), excluding transport envelopes;
REST/MCP shapes can legitimately differ. No scene payloads/pixels/secrets in labels or logs.
Render/batch fallbackReason is bounded desktop_unavailable/desktop_bringup_failed/
worker_upgrade_required, never raw exception text.
