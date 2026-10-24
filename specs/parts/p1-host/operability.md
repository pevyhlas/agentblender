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
  is parsed unless it is regular and it and its directory are the user's, not writable by others,
  and have no macOS allow ACL (a symlink is judged at its target). Setup/reset uses the same ACL
  rule. Test: `ConfigResolverTests.UntrustedConfigFilesAreRefusedBeforeTheyAreRead`,
  `ConfigStartupTests.DaemonAndCliRefuseAWorldWritableConfig`.
- **P1.B-11 Private token**: `ABL_DESKTOP_TOKEN` wins over `desktop.tokenFile`; a token file
  non-regular, readable by others, owned by another user or carrying a macOS allow ACL leaves the desktop tier off. Test:
  `ConfigResolverTests`.

## Blender and the worker environment

- **P1.B-12 Discovery and version**: an explicit path wins and never falls through; otherwise
  fixed candidates per OS are probed, then (macOS) Spotlight hits under trusted roots only;
  5.2.x is supported, 5.2.1 the reference, other patches warn, other lines are refused unless
  `blender.allowUnsupported`; a candidate others can write is never run. Test:
  `BlenderDiscoveryTests`.
- **P1.B-13 Runtime directory**: worker sockets live in a short private directory; before every
  spawn the longest socket path must fit `sun_path`, and the final component must be a real,
  user-owned 0700 directory without a macOS allow ACL. Trailing separators cannot hide a final
  symlink. Test: `RuntimePathsTests`, `PathTrustTests`.
- **P1.I-05 Allowlisted worker environment**: workers start with `HOME`, `USER`, `PATH`, `LANG`,
  `LC_*`, `TMPDIR` and `worker.envPassthrough` only, every `PYTHON*` removed; the version probe
  gets a narrower list. Enforced by: `ChildEnvironmentTests`, `WorkerEnvironmentTests`.

## Lifecycle

- Path-trust policies consume shared read-only `PathFacts` (owner/effective uid, mode, kind,
  final symlink and macOS allow ACL). Ownership is uid equality, never a chmod probe.
  Discovery preserves its user/root trust-anchor walk and delivery aliases still require root.
  Deny-only ACLs remain permitted; inspection failures refuse trust. Tests: `UnixIdentityTests`,
  `PathTrustTests`, `BlenderDiscoveryTests`, `ConfigResolverTests`.
- State derives from one `DaemonPaths.StateDir`; runtime fallback appends `run`. Before use,
  state's final directory obeys the runtime private-directory rule. Existing unsafe state is
  refused rather than repaired. Store gates retain target-symlink semantics and require a
  user-owned directory, no group/other write and no allow ACL before trusting the lock/record.
  Doctor reports the same ACL failure. Tests: `PathTrustTests`, `LifecycleCoreTests`.
- Startup checks state before preparing a runtime beneath it; service installation uses the
  same state-only gate before writing. Config inspection failures retain `config.file` and
  structured setup/doctor remedies. Log-reader ACL refusals identify the ACL and recovery action.
  Tests: `PathTrustTests`, `LifecycleCoreTests.StartRefusesUnsafeStateBeforePreparingItsRuntime`.
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
