# Contract: Worker Protocol (host ⇄ Blender worker)

Owner parts: P1 (sessions) and P3 ([architecture](../specs/architecture.md)). `FR-` references cite
the 0.x feature specs (feature 001 unless stated), summarized in
[the archive](../specs/archive.md); their full text is kept in the private development history.

Transport: Unix domain socket at `$XDG_RUNTIME_DIR/agentblender/w-<id>.sock` (AF_UNIX
~108-byte path limit — never under deep paths). Framing: 4-byte big-endian length +
UTF-8 JSON, both directions. Worker is single-threaded blocking; one in-flight request.
Host enforces the per-op timeout (default 60 s exec / 600 s render); breach ⇒ SIGKILL +
recycle (checkpoints make workers disposable).

Envelope: request `{"id": n, "op": "...", ...}` → response `{"id": n, "ok": true, ...}`
or `{"id": n, "ok": false, "error": {"kind", "message", "traceback"}}`. `error.kind` is
either a host-sanctioned semantic kind, which the host preserves — `bad_request` (a caller
mistake the worker validated, e.g. an unknown render engine) or `gpu_unavailable` (feature
009: device selection or the pre-render device check of a `cycles_gpu` render failed; no
render ran; a caller's `exec` script can also raise it, and keeps that op's hint) — or the raw
Python exception type name, which the host treats as opaque and collapses to `exec_failed`
(so a worker cannot expand the host's kind vocabulary). `error.traceback` is `null` for a
deliberate structured error (a caller mistake is not a worker fault) and otherwise the
tail of the Python traceback. On an `exec` error the worker MUST first restore its pre-op state by
reloading the last auto-checkpoint the host names in the request (`recover_to`), preserving
FR-003 atomicity.

Render-request overrides are temporary: engine, resolution, output path/format (including
format-coerced color mode/depth), applicable samples/device/seed/denoiser (including
`denoising_use_gpu`, feature 009) and GPU device preferences are restored before success or an ordinary render error is returned.
Restoration preserves unset Cycles properties. If restoration fails, the rendering
worker exits. A session-owned preview/local-final worker failure makes that session
dead and recoverable from its unchanged durable checkpoint. A transient checkpoint
or desktop worker failure discards only that worker; an originating local session
remains intact. Cancellation/timeouts during an in-flight worker request discard
the affected worker under the same ownership-specific lifecycle contract.
This is isolation of the worker's render overrides, not an exec-style transaction for
arbitrary scene changes made by caller-supplied render handlers or Blender evaluation.

| op | request fields | response fields |
|---|---|---|
| `hello` | — | `blender_version, python_version, gpu: {backend, devices, metalrt}, capabilities: {blob_chunks: bool}` (gates chunked transfer; every worker with the current `gpu` shape advertises it). `gpu`: `backend` is `optix`, `metal` or `none`, from a read-only probe (OptiX, then Metal; no preference property assigned); `devices` lists 0–8 `{name, type}` entries of that backend (names ≤ 128 UTF-16 code units, control characters replaced; empty if and only if `none`); `metalrt` is `OFF\|ON\|AUTO` on Metal, else `null`. The host refuses an older shape (`gpu.optix` without `gpu.backend`) as `worker_upgrade_required` and any other malformed `gpu` as an invalid reply |
| `warm` | `prewarm_render: bool` | `warmed_ms` (runs the 64² Eevee throwaway) |
| `exec` | `script` (path) **or** `source` (inline code; feature 003/T005b — a remote worker cannot read the host's tmp script), `params, recover_to?` | `state_delta` (see below), `duration_ms` |
| `state` | `mode: full\|delta` | full: object/collection/material inventory; delta: `{added[], removed[], changed[]}` since last read |
| `inspect` | objects[], camera?, landmarks[], geometry/materials/modifiers booleans | `inspection` under `abl.scene-inspection.v1`; bounded selected observations, no journal advance or save; [contract](scene-inspection.md) |
| `save` | `path` | `bytes` (host hashes the file), `digest` (canonical scene digest — the replay gate; R-03: .blend bytes embed process memory layout and are not cross-process deterministic), `blender_version` (provenance — recorded on checkpoints/frozen artifacts, 002 FR-007) |
| `load` | `path` | — (resets fingerprint journal) |
| `load_bytes` | `blend_b64` (base64 `.blend`; feature 003 — a remote worker cannot read the host store), `keep?` | — (writes a temp, opens it, resets journal); when `keep` (T005b): retains the temp and returns `path` so a batch can reset to the base per variant without re-shipping |
| `render` | `engine: eevee\|cycles_gpu\|cycles_cpu\|workbench, res, samples, out, seed, return_bytes?, keep?, profile?` | `duration_ms, out`; when `return_bytes` (feature 003, remote): also `png_b64` (renders to a UNIQUE worker temp and ships the PNG back — pixels land on the host's disk); `keep` (T005b) retains that temp so a remote contact sheet can compose it; `profile` (feature 004): emit the AGD Mode-A delivery still — 8-bit RGBA, sRGB (Standard view transform, no tone map), straight alpha over a transparent film (the host verifies the byte-visible RGBA8 half and DECLARES colour/alpha in the delivery descriptor). Cycles replies (feature 009) also carry `device: {backend, name}`, outside `settings`: the device the worker configured and checked immediately before the render, not proven execution; `backend` equals the hello backend for `cycles_gpu` and is `cpu` (name `null`) for `cycles_cpu`; absent for `eevee` and `workbench`. Before a `cycles_gpu` render the worker selects the backend's devices (Metal: only METAL devices; OptiX: every returned entry) and checks them; a rejected selection value or a failed check raises `gpu_unavailable` |
| `contact_sheet` | `inputs[], labels[], columns, cell, out, return_bytes?` | `out`; when `return_bytes` (T005b, remote): also `png_b64` — composes from the worker's own variant PNGs and ships the finished sheet back (numpy grid compose; D-10 / C-01) |
| `quit` | — | — |

## Chunked bulk files (005, optional capability)

The 64 MiB guard bounds one control message, not total file size with `blob_chunks`. Raw
chunks are <=1 MiB. Transfers use connection-local IDs, ordered offsets, byte lengths and
SHA-256. Worker paths are never host write targets. The host uses chunked transfer only when
the hello advertises `blob_chunks`. Workers from before chunked transfer also report the older
`gpu.optix` hello shape, so the host refuses them at bringup as `worker_upgrade_required` and the
job falls back locally; republish the launcher with its bundled `worker/`.

| op | request | response |
|----|---------|----------|
| `blob_put_begin` | `bytes, sha256` | `transfer_id, chunk_bytes` |
| `blob_put_chunk` | `transfer_id, offset, data` (bounded base64) | acknowledged next `offset` |
| `blob_put_commit` | `transfer_id, keep?` | verifies and loads checkpoint; `path` when kept for batch resets |
| `blob_get_chunk` | `transfer_id, offset` | `offset, data` (bounded base64) |
| `blob_get_finish` | `transfer_id` | releases download; kept batch inputs survive until disconnect |
| `blob_abort` | `transfer_id` | cleans unfinished transfer |

`render`/`contact_sheet` accept `return_blob=true` and return `blob: {transfer_id, bytes,
sha256}` instead of `png_b64`. The host verifies a unique staged file before publication.
No fixed total file limit applies; disk and per-message deadlines still do. Disconnect reaps
transfers/kept files; hard kills and restoration-fatal exits bypass finalizers and rely
on OS temp cleanup. Caller cancellation discards the
connection so an unread response cannot be mistaken for the next request.

Fingerprint journal (drives `state delta` and exec's returned delta): per object —
name, parent, rounded transform, mesh vert/face counts, material names; plus collection
and material sets. Diffed after every exec; response size scales with the change, never
the scene (FR-002/AC-2).

GPL note: `worker/` is the only directory that imports `bpy` (D-10); the host knows only
this protocol.
