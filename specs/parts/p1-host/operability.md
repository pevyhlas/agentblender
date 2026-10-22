# P1 Host — operability

**Code**: `src/Abl.Core/Config/`, `src/Abl.Core/Lifecycle/`, `src/Abl.Cli/Host/`,
`src/Abl.Core/AgentGuidance.cs` · **Decisions**: D-16, D-18

How a fresh agent installs, configures, starts, checks and connects AgentBlender (R-11, J-06).

## Configuration

- **P1.B-09 One resolver**: the daemon and the CLI build their options only through `AblConfig`,
  precedence flag > `ABL_*` env > file > discovered > default; each key reports its source,
  secrets reduced to their source. Test: `ConfigResolverTests`, `ConfigStartupTests`.
- **P1.B-10 Strict, trusted file**: the per-user JSON file (`version: 1`) is refused with the
  file, key and check ID on an unknown key, a comment or another version, and refused before it
  is parsed unless it and its directory are the user's and not writable by others (a symlink is
  judged at its target). Test: `ConfigResolverTests.UntrustedConfigFilesAreRefusedBeforeTheyAreRead`,
  `ConfigStartupTests.DaemonAndCliRefuseAWorldWritableConfig`.
- **P1.B-11 Private token**: `ABL_DESKTOP_TOKEN` wins over `desktop.tokenFile`; a token file
  readable by others or owned by another user leaves the desktop tier off. Test:
  `ConfigResolverTests`.

## Blender and the worker environment

- **P1.B-12 Discovery and version**: an explicit path wins and never falls through; otherwise
  fixed candidates per OS are probed, then (macOS) Spotlight hits under trusted roots only;
  5.2.x is supported, 5.2.1 the reference, other patches warn, other lines are refused unless
  `blender.allowUnsupported`; a candidate others can write is never run. Test:
  `BlenderDiscoveryTests`.
- **P1.B-13 Runtime directory**: worker sockets live in a short private directory; before every
  spawn the longest socket path must fit `sun_path`, and the final component must be a real,
  user-owned 0700 directory. Test: `RuntimePathsTests`.
- **P1.I-05 Allowlisted worker environment**: workers start with `HOME`, `USER`, `PATH`, `LANG`,
  `LC_*`, `TMPDIR` and `worker.envPassthrough` only, every `PYTHON*` removed; the version probe
  gets a narrower list. Enforced by: `ChildEnvironmentTests`, `WorkerEnvironmentTests`.

## Lifecycle

- **P1.B-14 One daemon per store**: an exclusive lock and the instance record live in the store;
  `abl daemon start` detaches, returns once a worker is warm or fails fast with a check ID, and
  hands off to an installed user service that serves this store and port. Test:
  `DaemonLifecycleTests`, `DaemonLifecycleUnitTests`, `LifecycleCoreTests`.
- **P1.B-15 Stop is safe**: stop refuses active sessions unless forced and leaves no orphan
  workers; a forced stop never signals a process that is not the recorded daemon. Test:
  `DaemonLifecycleTests`.
- **P1.B-16 User services**: `install-service` writes a systemd user unit or launchd agent that
  starts this `abl` through `/usr/bin/env -i` with only its identity variables and
  `DOTNET_ROOT`. Test: `ServiceUnitTests`.
- **P1.I-06 Host verbs never mutate through ops**: `setup`, `doctor`, `config`, `daemon …` and
  `mcp config` are disjoint from the op verbs and have no MCP tool; runtime state reaches MCP
  through `daemon_status`. Enforced by: `HostVerbsTests`, `DaemonStatusTests`.

## Setup, doctor and agent guidance

- **P1.B-17 Idempotent setup**: `abl setup` pins Blender and the flagged keys, keeps the rest of
  the file, writes atomically as 0600, and changes nothing on a repeat run. Test: `SetupTests`.
- **P1.B-18 Doctor contract**: `abl doctor` is read-only except the opt-in `gpu.render` check;
  its check IDs, their order and the JSON envelope are a contract; a real worker round trip
  reports version, digest and GPU; every failure carries a remedy. Test: `DoctorTests`,
  `DoctorRoundtripTests`.
- **P1.B-19 Agent guidance**: the MCP server instructions stay in budget and say when 3D helps
  and when the job belongs to AGD; `abl mcp config` prints one client entry and writes nothing.
  Test: `AgentSurfaceTests`.

## Packaging

- **P1.B-20 Packaged worker**: a Release build resolves `worker/` only from the package or
  `paths.workerDir`, never by walking up to a checkout; doctor `worker.files` compares it with
  the build-time manifest. Test: `PackagingTests`.
