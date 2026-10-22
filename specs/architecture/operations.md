# AgentBlender — operations

- **Delivery**: a change merges through a pull request with green CI on its exact head. A
  release is the bundle `scripts/release-bundle.sh` builds (the `agentblender` tool package,
  `SHA256SUMS`, `provenance.json`) after a temporary install passes `abl doctor --quick --json`
  and the package declares the MIT license and carries the third-party notices
  (`THIRD-PARTY-NOTICES.md`, `licenses/`);
  tagging and publishing is owner-gated (D-16). How a release reaches a host:
  [environments](environments.md). Rollback is reinstalling the previous bundle; stop the
  daemon first.
- **CI** (`.github/workflows/ci.yml`, C-09): every pull request and push to main on fresh
  GitHub-hosted runners. A validate job (whitespace, the CI policy check, shellcheck, the GPL
  boundary grep, no Blender binaries, worker compile and fakes, the Product-Kit gates); build and
  unit tests with `-warnaserror` on exactly the reference SDK, with the vulnerable-package check
  and a package, temp-install and doctor check; an advisory macOS arm64 unit lane; and a
  real-Blender integration lane on the official build, SHA-256 checked and downloaded at test
  time. The integration lane has no GPU: EEVEE runs on Mesa's software EGL drivers, checked by a
  smoke render in the install step, and the lane excludes `OptixQualification`. It has run
  green on a hosted runner (about 16 minutes of tests) and stays advisory (`continue-on-error`)
  until the owner makes it required ([inbox](../inbox.md), Hosted Blender lane and OptiX
  coverage). The test steps have their own timeouts below the job's, abort and name a test
  that runs over 5 minutes (`--blame-hang-timeout`) and print the test log on every outcome;
  the integration lane also uploads its log and TRX results.
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
  `worker/` matching the host; upgraded together with the host.
- **Product-Kit**: v1.1.0 vendored in copy mode under `.prdkit/` (no kit submodule); CI checks
  the install against its manifest hash. Upgrade path: `.prdkit/memory/config.md`.
- **Scripts**: `scripts/install-local.sh`, `scripts/uninstall-local.sh`,
  `scripts/release-bundle.sh`, the opt-in `scripts/claude-with-telemetry.sh` and its
  `scripts/test-claude-with-telemetry.sh`,
  `scripts/check-ci-policy.sh`; see `scripts/README.md`.
