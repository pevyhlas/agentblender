# AgentBlender — decisions

<!-- D- decisions, technology choices included, a `## D-NN <title>` section each with bold-label
lines: decision, alternatives, revisit when. Past the budget, split into decisions/<area>.md;
one long decision gets specs/decisions/D-NN.md. -->

## D-01 .NET host, Python only inside Blender

**Decided by**: owner · **Cited by**: C-08
- **Decision**: a .NET 10 host (ASP.NET Core); Python only inside Blender workers. Reaffirmed
  after an all-Python evaluation.
- **Alternatives**: an all-Python host (evaluated; rejected for productivity, single-package
  distribution and a structural GPL wall).
- **Revisit when**: evidence shows the host or protocol materially slows the work.

## D-02 Warm binary-hosted workers

**Decided by**: owner · **Cited by**: Q-01, P1.B-05
- **Decision**: sessions run against persistent headless Blender processes started as the
  Blender 5.2.1 LTS binary (`--background --factory-startup --python worker.py`); a pool keeps
  pre-warmed workers so no interactive op pays a cold start; finals, replays and batches use
  transient workers from that pool or the desktop launcher. Host and worker speak 4-byte
  length-prefixed JSON over a Unix socket, or TCP to the desktop.
- **Alternatives**: newline-framed JSON (stream desync seen in prior art); a cold `blender -b`
  per op (about 1.4 s each); the `bpy` pip wheel (it pins
  Python 3.13; whether this is closed is an inbox question); a GUI add-on server (needs the GUI
  event loop and one shared scene).
- **Revisit when**: desktop worker provisioning makes the binary route painful, or op p95
  exceeds Q-01.

## D-03 Checkpoint tree is the state model

**Decided by**: owner · **Cited by**: R-02
- **Decision**: immutable content-addressed snapshots plus op journals; branch loads a
  checkpoint into a fresh session; programmatic undo is banned; checkpoints open in Blender's
  GUI. The code checkpoints after every successful exec (cadence: an inbox question).
- **Alternatives**: `bpy` undo (unreliable, single-state); an embedded database (opaque to git
  and humans).
- **Revisit when**: save cost per exec grows with real scenes.

## D-04 MCP is a thin wrapper

**Decided by**: owner · **Cited by**: R-01, P1.B-01, P1.I-02
- **Decision**: one op vocabulary in the core; MCP (C# SDK, streamable HTTP), REST and CLI are
  parallel thin layers; parity is a contract test.
- **Alternatives**: stdio MCP (one daemon per agent defeats shared sessions).
- **Revisit when**: never for parity; transport per evidence.

## D-05 Parallelism follows evaluation

**Decided by**: owner · **Cited by**: R-05
- **Decision**: no fan-out inside iterative design; parallel only for batches verified in one
  contact sheet.
- **Alternatives**: parallel design branches (evaluation is serial at the agent anyway).
- **Revisit when**: agents can judge several design branches at once.

## D-06 Two hardware tiers

**Decided by**: owner · **Cited by**: R-07, P1.B-21
- **Decision**: the always-on host iterates (warm workers, previews, light Cycles, a single-slot
  GPU lease); an intermittent desktop GPU takes heavy finals and batches. The host's GPU is the
  local tier's GPU, whichever Cycles backend it has (D-17); the lease applies to both, unified
  memory included.
- **Alternatives**: one tier (heavy work blocks iteration).
- **Revisit when**: the desktop's availability stays too low to matter.

## D-07 Context economy is contract

**Decided by**: owner · **Cited by**: Q-02
- **Decision**: delta reads, 512 px previews, contact sheets composed in the worker with numpy,
  and disk-only finals are op-contract requirements.
- **Alternatives**: host-side imaging (ImageSharp is not MIT-compatible); full scene dumps.
- **Revisit when**: telemetry shows payload per loop growing with scene size.

## D-08 Telemetry-driven surface

**Decided by**: owner · **Cited by**: R-10, C-05
- **Decision**: ops emit OpenTelemetry traces and metrics, pushed over OTLP; surface changes
  cite usage evidence; metric labels stay bounded (op, outcome, tier); session and project
  attribution live on traces only.
- **Alternatives**: a bespoke scrape endpoint (a new listener, against C-03).
- **Revisit when**: a needed question cannot be answered from bounded labels.

## D-09 AGD boundary

**Decided by**: owner · **Cited by**: C-07, N-01
- **Decision**: 2D authoring, lint and export belong to AgentGraphicDesign; AgentBlender feeds
  it (rasters, shared tokens, the same export discipline).
- **Alternatives**: 2D features here (duplicate products).
- **Revisit when**: never, short of an owner decision.

## D-10 MIT licence posture

**Decided by**: owner · **Cited by**: C-01, C-02
- **Decision**: MIT for the whole repo; MIT is GPL-compatible, so worker scripts stay MIT;
  inbound and dependency rules are C-01; Blender is never redistributed (C-02).
- **Alternatives**: a split licence (unneeded); Apache-2.0 inbound code (NOTICE drag).
- **Revisit when**: never.

## D-11 One daemon, per-agent sessions

**Decided by**: owner · **Cited by**: R-06
- **Decision**: one loopback daemon serves N concurrent sessions, each with its own worker and
  head; a crash costs one session.
- **Alternatives**: a daemon per agent (no shared store, more cold starts).
- **Revisit when**: one host no longer serves the sessions it is asked for.

## D-12 Delivery via export with provenance

**Decided by**: owner · **Cited by**: R-08, C-11
- **Decision**: consumers get assets only through export pipelines with provenance; the first
  lane is one AGD-compatible still per immutable generation.
- **Alternatives**: hand-copied outputs; a runtime dependency on AgentBlender.
- **Revisit when**: a consumer needs another lane (owner input per consumer).

## D-13 Prior art

**Decided by**: owner · **Cited by**: C-01
- **Decision**: surveyed Blender MCP projects inform ideas; code reuse only per C-01; the
  per-project licence map is in [observed](../observed.md).
- **Alternatives**: forking a GUI add-on server (needs the GUI, one shared scene).
- **Revisit when**: a surveyed project changes licence.

## D-14 Bundles are the portable unit

**Decided by**: owner · **Cited by**: R-04, P4.B-03
- **Decision**: a bundle is exactly `artifact.json` plus verbatim `base.blend`; stores stay
  local and disposable; renders are never bundled. The reference Blender stays pinned at 5.2.1
  and the digest gate binds (version rule in D-16).
- **Alternatives**: committing raw store objects; committing final `.blend` only (no
  parametrization, no proof).
- **Revisit when**: the Blender pin moves.

## D-15 Desktop is a remote headless worker

**Decided by**: owner · **Cited by**: R-07, P5
- **Decision**: the host stays the single brain and digest authority; native Windows Blender for
  full OptiX; the launcher is started by hand and spawns `--background --factory-startup`
  workers behind a token on one private address; pixels come back to the host.
- **Alternatives**: GUI Blender; a second daemon on the desktop; WSL2 as default; a render farm
  manager (Flamenco: GPL, per-task cold starts).
- **Revisit when**: an always-on launcher service with a GPU-yield policy is wanted.

## D-16 Host configuration and distribution

**Decided by**: owner, after design review · **Cited by**: R-11, P1.B-09, P1.B-12
- **Decision**: one resolver in Core (`AblConfig`), precedence flag > `ABL_*` env > file >
  discovered > default, each key reporting its source; one strict per-user JSON file, refused
  unless it and its directory are the user's and not writable by others; secrets referenced by
  file. Host-only verbs (`setup`, `doctor`, `config`, `daemon …`, `mcp config`) never mutate
  through `/api/ops`; runtime state reaches MCP through `daemon_status`. Blender 5.2.x is
  supported, 5.2.1 the reference, other patches warn, other lines are refused unless
  `blender.allowUnsupported`. One RID-agnostic .NET tool package `agentblender` (command `abl`,
  daemon inside, `worker/` and its manifest as content). The release bundle is the `.nupkg`,
  `SHA256SUMS` and `provenance.json`; tagging and publishing is owner-gated. SDK: `global.json`
  rolls forward from the reference 10.0.112; CI builds with exactly the reference.
- **Alternatives**: env-only configuration (the observed failure);
  `Microsoft.Extensions.Configuration` (no per-key source, ambient files); self-contained
  per-platform archives (build matrix, signing and notarization; deferred); a container (N-07);
  two tools (version skew); SDK `disable`, `latestPatch` or `latestMajor` roll-forward.
- **Revisit when**: a published feed or self-contained binaries are wanted (inbox).

## D-17 GPU backend model

**Decided by**: owner, after design review · **Cited by**: R-12, R-13, P1.B-22
- **Decision**: detection is worker-side and read-only, in `hello`: probe OptiX, then Metal, and
  report `gpu: {backend, devices, metalrt}`; the host refuses the older `gpu.optix` shape as
  `worker_upgrade_required`. Metal enables only Metal devices, with OpenImageDenoise on the GPU;
  OptiX keeps enable-all. Before every `cycles_gpu` render the worker re-selects and checks the
  devices, else raises `gpu_unavailable`. One Core predicate (`GpuGuard`) decides every
  `cycles_gpu` path before the GPU lease and on desktop bringup; `lease=cpu_fallback` renders on
  the CPU and records it. The reported `device` means configured and checked, never proven
  execution. The host platforms are C-12.
- **Alternatives**: host-side Metal or IOKit probing (says nothing about Cycles' device list and
  breaks the no-`bpy`-host-side rule); a doctor-only probe.
- **Revisit when**: OptiX device evidence justifies GPU-only selection there too.

## D-18 Blender is never downloaded

**Decided by**: owner · **Cited by**: N-06
- **Decision**: `abl setup` and `abl doctor` print the official 5.2 LTS download page for the OS
  and architecture; AgentBlender never fetches or installs Blender.
- **Alternatives**: fetching from download.blender.org with pinned checksums (about 400 MB per
  install, a checksum table per platform, DMG and archive extraction, installing GPL software
  on the user's behalf); the Homebrew cask (tracks the newest Blender, not 5.2 LTS).
- **Revisit when**: the first public release (an inbox question).
