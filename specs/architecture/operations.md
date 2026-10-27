# AgentBlender — operations

- **Delivery**: a change merges through a pull request with green CI on its exact head. A
  release is the bundle `scripts/release-bundle.sh` builds (the `agentblender` tool package,
  `SHA256SUMS`, `provenance.json`) after a temporary install passes `abl doctor --quick --json`
  and the package declares the MIT license and carries the third-party notices
   (`THIRD-PARTY-NOTICES.md`, `licenses/`). Packing uses an archive of the captured commit,
   with only tracked modifications overlaid in explicitly dirty development mode; ignored
   outputs never enter the build. CLI and launcher share an explicit worker-module list;
  tagging and publishing is owner-gated (D-16). How a release reaches a host:
  [environments](environments.md). Rollback is reinstalling the previous bundle; stop the
  daemon first.
- **CI** (`.github/workflows/ci.yml`, C-09): every pull request and push to main on fresh
  GitHub-hosted runners. A validate job (whitespace, the CI policy check, shellcheck, the GPL
  boundary grep, no Blender binaries, worker compile and fakes, the Product-Kit gates and the
  redacting public-text/file/link guard `scripts/check-public-repo.py`); build and
  unit tests with `-warnaserror` on exactly the reference SDK, with the vulnerable-package check
  and a package, temp-install and doctor check; an advisory `macos-26` arm64 unit lane; and a
  real-Blender integration lane on the official build, SHA-256 checked and downloaded at test
  time. The integration lane has no GPU: EEVEE runs on Mesa's software EGL drivers, checked by a
  smoke render in the install step, and the lane excludes `OptixQualification`. It has run
  green on a hosted runner (about 16 minutes of tests) and stays advisory (`continue-on-error`);
  after it stays green on main, the maintainer decides whether it becomes a required check
  ([inbox](../inbox.md), Hosted Blender lane and OptiX coverage). The test steps have their own timeouts below the job's, abort and name a test
  that runs over 10 minutes (`--blame-hang-timeout`) and print the test log on every outcome;
  the integration lane also uploads its log and TRX results. The macOS lane asserts OS major and
  architecture, uses physical temporary paths, and retains runner/image/SDK identity, console
  and TRX artifacts for seven days on success or failure. Its summary reports the actual test
  step outcome. Hosted unit tests are not host or Metal qualification (changes 008 and 009).
- **Signals** (audit, 2026-10-23): every `kill` goes through `UnixIdentity.Signal`, which
  refuses 0, -1 and a real signal to the caller's own process group; .NET's
  `Process.Kill(entireProcessTree: true)` (worker, launcher, discovery and service probes)
  stops and kills only the started process and its descendants, pid by pid. Two group
  signals exist: the recorded group of a daemon that leads it (the record's group must equal
  its pid), and the group of a daemon child that failed to start, which exists only if that
  child called `setsid` (a forked pid never matches an active group). Neither is ever the
  caller's group: `stop` refuses (`daemon.workers`) in the daemon's group, as the daemon or
  under its process tree; the tree check passes silently when the process table cannot be
  read. `setsid` runs only in `daemon run --detached`. Two macOS CI runs that lost their
  runner were not reproduced: 10 traced local and 10 watched hosted iterations without
  `setsid` showed no signal to the runner's group. The macOS unit lane keeps `setsid` as
  defence in depth.
- **Hardware qualification** (C-09): no CI lane has a GPU. `OptixQualification` (the
  real-Blender cases that need a GPU backend, OptiX device evidence included) runs by hand on a
  Linux host with an NVIDIA OptiX GPU, and `MetalQualification` on an Apple silicon Mac, with
  `dotnet test agentblender.slnx --filter "Category=<category>"` and `ABL_BLENDER` set. Run the
  OptiX one before a release that changes `worker/cycles_devices.py`, the render path or the GPU
  guard; a change that relies on it says so under its Checks.
- **Services**: `abl daemon install-service` writes a systemd user unit or a launchd agent that
  starts the daemon through `/usr/bin/env -i` with only its identity variables and
  `DOTNET_ROOT`; `deploy/README.md` describes it.
- **Desktop launcher**: installed and started by hand on the GPU workstation, with the
  `worker/` matching the host; upgraded together with the host. Owner-deferred: current live
  `tier=desktop` evidence and an always-on service with GPU-yield policy; historical renders do
  not qualify the current launcher. Revisit only on owner selection of a bounded live trial/service
  scope (D-15). Undated owner direction preserved from `379be625:specs/inbox.md`, Desktop rollout;
  reconciled2026-10-27, without changing deploy authority.
- **Further delivery lanes**: beyond the first still, require owner input for each named consumer
  before implementation (D-12); revisit when that consumer is named. Undated owner direction from
  `379be625:specs/inbox.md`, Delivery lanes beyond the first; reconciled2026-10-27.
- **Product-Kit**: v1.2.1 vendored in copy mode under `.prdkit/` (no kit submodule); CI checks
  the install against its manifest hash. Upgrade path: `.prdkit/memory/config.md`. Owner requested
  uptake2026-10-27; supersedes the2026-10-26 latest-kit request in `379be625:specs/inbox.md`.
- **Public-workflow direction**: the owner selected public-repository work, concise READMEs,
  public Product-Kit, reviewed fixes/roadmap continuation and original artwork through the native
  workflows on2026-10-24 (`379be625:specs/inbox.md`, Public projects, fixes and logos). The existing
  README, notices, [change log](../log.md) PRs6–8 and constitution enact this; artwork adoption and
  release/deploy authority are not inferred. PR7 closed the baseline specific-bind, child-environment
  and tracked-packaging findings (P5.I-01/P5.I-05 and Delivery above); the release/deploy owner gates
  remain. The2026-10-25 hosted macOS CI choice is implemented
  by PR10 above; changes008/009 retain host/Metal qualification and owner release gates.
- **Scripts**: `scripts/install-local.sh`, `scripts/uninstall-local.sh`,
  `scripts/release-bundle.sh`, the opt-in `scripts/claude-with-telemetry.sh` and its
  `scripts/test-claude-with-telemetry.sh`,
  `scripts/check-ci-policy.sh`; see `scripts/README.md`.
