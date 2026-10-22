# worker/ — the Blender side of AgentBlender

This directory is the **only** place in the repository that imports `bpy` (or any other
Blender module). Blender is GPL; the host (`src/`) never links or embeds it — it spawns
the Blender **binary** and speaks the socket protocol in
[`docs/worker-protocol.md`](../docs/worker-protocol.md).
Keep that boundary visible: no `bpy` import may appear outside this directory
(non-negotiable GPL boundary, C-02).

These files run **inside** Blender's bundled Python (binary-hosted, D-02):

```
blender --background --factory-startup --python worker/worker.py -- <socket-path>
```

- `worker.py` — single-threaded blocking request loop over a Unix domain socket
  (4-byte big-endian length + UTF-8 JSON). The blocking loop IS Blender's main thread;
  no timers, no threads. The host owns timeouts and kills on breach — workers are
  disposable, checkpoints make that safe.
- `fingerprint.py` — scene fingerprint journal; drives `state` delta reads and the
  delta returned by every `exec`.
- `sheet.py` — numpy contact-sheet composition (imaging stays worker-side; the host
  takes no imaging dependency, D-10).
- `cycles_devices.py` — Cycles GPU backend detection (`hello`, read-only), per-render
  device selection and the pre-render device check (feature 009). It takes the
  preferences and `scene.cycles` as arguments and never imports `bpy`, so
  `tests/test_cycles_devices.py` drives it with CPython fakes.

The Blender binary is resolved by the host via `ABL_BLENDER`
(set `ABL_BLENDER` to a Blender 5.2.1 LTS binary, or put `blender` on `PATH`).

License note: these `.py` files are part of AgentBlender (MIT target, D-10). MIT is
GPL-compatible, so importing `bpy` at runtime is fine; what is never fine is copying
Blender code into this repo or redistributing Blender itself.
