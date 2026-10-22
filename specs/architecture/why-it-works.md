# AgentBlender — why it works

### Warm, isolated sessions make agent iteration cheap (R-01, R-06, Q-01, Q-04)

- **Mechanism**: one long-lived worker per session keeps Blender loaded; the host enforces
  per-op timeouts and kills and recycles a worker; every exec auto-checkpoints, so recovery is
  "branch the last checkpoint into a fresh worker".
- **Assumptions**: warm round trips are about four orders of magnitude cheaper than a cold
  start ([observed](../observed.md)); one `.blend` per process is a Blender constraint.
- **Alternatives**: a cold `blender -b` per op (headless prior art) pays about 1.4 s per op; an
  off-the-shelf GUI add-on server (the blender-mcp lineage) needs the GUI event loop and one
  shared scene; the `bpy` wheel (an inbox question).
- **Revisit when**: op p95 exceeds Q-01 under real load, or the wheel removes the provisioning
  cost.

### Replay is verified by scene content, not bytes (R-03, Q-03)

- **Mechanism**: the worker computes a canonical scene digest within a stated bound
  ([P4.I-01](../parts/p4-store-replay.md)); freeze records it; replay on a fresh worker must
  match. Byte hashes address storage only.
- **Assumptions**: `.blend` bytes and GPU renders are not reproducible across processes
  ([observed](../observed.md)); the pinned Blender version is installed where replay runs.
- **Alternatives**: byte equality (measured impossible); render hashing (GPU output differs run
  to run); committing final `.blend` files to git or LFS (no parametrization, no proof).
- **Revisit when**: a consumer needs node-tree or modifier-parameter fidelity outside the bound,
  or the Blender pin moves.

### Finished work travels through git (R-04, J-03)

- **Mechanism**: a bundle is the frozen artifact's `artifact.json` plus its verbatim
  `base.blend`, written with deterministic bytes; import checks the base hash and shape before
  any store write ([P4](../parts/p4-store-replay.md) bundles); replay on a fresh worker is the
  proof, so renders never need committing.
- **Assumptions**: the pinned Blender line is installed where the bundle is imported; a base
  checkpoint is small enough for plain git.
- **Alternatives**: committing raw store objects (opaque, couples clones to the store layout);
  committing final `.blend` files or renders through Git LFS, the off-the-shelf route (no
  parametrization, no proof, a server feature consumers must have).
- **Revisit when**: base checkpoints outgrow plain git, or the Blender pin moves (D-14).

### Context stays bounded (Q-02)

- **Mechanism**: delta state text from a worker-side fingerprint journal; 512 px previews; one
  contact sheet per batch; finals return paths and metadata only.
- **Assumptions**: a 512 px image costs about 350 tokens and is enough to judge silhouette,
  proportion and material read.
- **Alternatives**: full scene dumps per step; search-first tool discovery over a large hidden
  inventory (an off-the-shelf pattern; unnecessary at this surface size).
- **Revisit when**: telemetry shows payload bytes per loop growing with scene size.

### Heavy work moves to a remote GPU without blocking (R-07)

- **Mechanism**: the worker protocol is transport-neutral; a desktop launcher authenticates the
  host before spawning a headless worker; the host ships the base checkpoint in and pixels back
  in verified chunks; a cached probe picks the tier; replay and digests stay on the host.
- **Assumptions**: a desktop GPU is reachable often enough to matter (unmeasured); TCP
  reachability is not render proof. Live `tier=desktop` renders were observed once
  (2026-09-30), before the launcher upgrade; the current launcher has not been re-verified live.
- **Alternatives**: an off-the-shelf render farm manager (Flamenco: GPL, per-task cold starts);
  driving a GUI Blender; a second daemon on the desktop.
- **Revisit when**: desktop availability stays low, or an always-on launcher service with a
  GPU-yield policy is wanted.

### A fresh agent can set itself up (R-11)

- **Mechanism**: one resolver with per-key sources, Blender discovery with a version policy,
  checked runtime paths and an allowlisted worker environment remove the hand-set variables;
  `abl daemon start` returns only once a worker is warm or fails with a check ID; `abl doctor`
  gives stable check IDs, a real worker round trip and a remedy each.
- **Assumptions**: Blender is installed in a discoverable place (D-18); a .NET 10 SDK is present
  to install the tool package.
- **Alternatives**: environment-only configuration (the observed failure); self-contained
  binaries (deferred, D-16); a container (N-07).
- **Revisit when**: field use still shows hand-set environment or wrapper scripts.
