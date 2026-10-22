# AgentBlender — archive

<!-- Past tense, condensed: directions tried, why they stopped, lessons and notable outcomes.
What still binds lives in the spec, never here. Agents read this file on demand (a history
question, before re-trying a direction), never by default. Past the budget, condense older
entries harder. new-change.sh reads the Base and Last lines to never reuse an old feature
number. -->

**Base**: the last commit of the private development history before the public release; this
repository starts from fresh history, so that commit is not here. The detailed records (the 0.x
product spec, the target, feature folders 001 to 009, reviews and results) are kept in the
private history.

**Last 0.x feature**: 009; new changes number above it. Features 008 and 009 continue as
change records under `specs/changes/`.

## Founding design and spike

- **Tried**: a spike of headless Blender 5.2.1 rendering fully procedural props (about
  1.5 s per frame on a modest GPU, no third-party assets), then a design around MCP as a
  thin wrapper over warm workers and a checkpoint tree.
- **Outcome**: became D-01 to D-13 and the founding requirements; the vision's asset-factory
  regimes (heroes iterated, families batched, the middle scripted) shaped J-01 and J-02.
- **Lesson**: the bottleneck was model context, not render time: deltas, 512 px previews and
  contact sheets became contracts (Q-02).

## Prior-art survey

- **Tried**: before the spike, a desk survey of public Blender MCP servers and Blender automation
  projects (GUI add-on MCP servers, standalone MCP bridges, Flamenco, BlenderProc), the PyPI
  `bpy` wheel, and the Blender Foundation licence and trademark pages.
- **Outcome**: GUI add-on servers dispatch through `bpy.app.timers`, need a running GUI and share
  one scene, so none offered headless per-agent sessions, branching state or reliable
  programmatic undo (the J-01 pain, D-02). Each project's licence is mapped in
  [observed](observed.md) with public upstream links; they inform ideas only, and code reuse
  follows C-01 (D-13).
- **Lesson**: read the licence file, not the README; an unlicensed repository is all rights
  reserved. The survey notes stay in the private history; the facts that bind are in
  [observed](observed.md).

## Core warm-worker pipeline (001)

- **Tried**: daemon, warm workers, checkpoint tree, freeze and replay, the MCP, REST and CLI
  surface, telemetry. Integrated.
- **Directions dropped**: replay proved by `.blend` bytes or render hashes (measured not
  reproducible across processes), replaced by the canonical scene digest; checkpoints "every
  few ops", replaced by every exec (a save costs about 8 ms; the cadence question is in the
  inbox); the `bpy` pip wheel (Python 3.13 pin).
- **Lesson**: make workers disposable (ops are transforms over checkpoints), so crash recovery
  is an ordinary path, not an exception.

## Git-portable bundles (002)

- **Tried**: committing raw store objects, or only a final `.blend`; settled on `artifact.json`
  plus verbatim `base.blend` with deterministic bytes and import verified before any write
  (D-14). Integrated.
- **Lesson**: the store stays local and disposable; git holds the reconstitution unit only.

## Desktop remote worker (003) and the two-tier live target

- **Tried**: running the desktop worker under WSL2 against native Blender on the worker's OS;
  chose native Blender as a remote headless worker driven over TCP behind a launcher and token
  (D-15). Pre-checks passed (OptiX on a desktop GPU served finals faster than the local tier).
  Batch routing, source shipping and pixel-back followed and passed in loopback tests.
- **Stopped because**: live `tier=desktop` renders were observed once (2026-09-30 render
  sidecars), but the upgraded launcher was never re-verified live and consumer admission was
  never produced; the delivery target was dropped at the 1.0 cutover.
- **Lesson**: merged code and TCP reachability are not live evidence; only a `tier=desktop`
  render is.

## Consumer still export (004)

- **Tried**: one procedural still per immutable generation in the AGD provided-artifact v1
  payload, a producer receipt beside it, offline verify; receiver compatibility qualified
  against AGD 0.3.0. Integrated.
- **Not pursued**: named families, motion, native import and other formats: they need owner
  input per consumer.
- **Lesson**: qualify against the pinned receiver; compatibility is not admission.

## Telemetry and runtime readiness (005)

- **Tried**: OTLP metrics and traces with bounded labels, serving-tier metrics, heartbeat,
  unique default output paths, CLI caller deadlines, and chunked transfers once whole-file
  messages met the 64 MiB control-message guard. Integrated; dashboards stayed outside the repo.
- **Lesson**: session and project attribution belong on traces; metric labels stay bounded.

## Render-state isolation (006) and structured inspection (007)

- **Tried**: temporary render overrides leaked into later saves; they are now restored, and a
  failed restoration kills the worker. Inspection gave agents bounded measurements instead of
  screenshot judgment. Both integrated.
- **Lesson**: any per-render setting is session state until proven restored.

## Agent operability (008)

- **Tried**: on a pilot project an agent built from source, set six environment variables, moved
  the runtime directory around a socket-path limit and started the daemon with `nohup`; the
  feature replaced all of it (D-16). Its design review found that a web page could reach
  `scene_exec` by DNS rebinding, so the Host/Origin guard shipped first, alone.
- **Lesson**: files that choose which programs run (config, Blender candidates, runtime dirs)
  need ownership and permission checks; discovery took six review rounds to get right.
- **Continues**: change 008-agent-operability.

## Metal GPU (009)

- **Tried**: a probe on a Mac showed Cycles silently path-tracing on the CPU for a requested GPU
  render in one call sequence, so the design re-selects and checks devices before every GPU
  render and refuses honestly when no backend exists (D-17).
- **Continues**: change 009-metal-gpu (qualification on a real Mac).

## Process and kit

- **Tried**: the 0.x Product-Kit canon with stage gates ("prototype" stage); the 1.0 cutover
  moved it under `specs/archive/` and retired the Stage model; the 1.1 restructure condensed it
  here. A private kit submodule was dropped for a vendored copy because public clones could not
  resolve it.
- **Lesson**: one canon on main; history is read on demand.

## Directions not taken

- An all-Python host (D-01); a GUI add-on MCP server; stdio MCP; Flamenco as the render farm;
  host-side imaging with ImageSharp (licence); an embedded database for the store; a container
  image (N-07); downloading Blender (D-18); self-contained notarized binaries (deferred, D-16);
  `abl mcp install` (deferred until an onboarding design is reviewed).
