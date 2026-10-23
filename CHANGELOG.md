# Changelog

## Unreleased — first public release (fresh history)

First public release of AgentBlender under the MIT License. The repository starts from a
fresh history; earlier development history is not published. Lifecycle: experimental. The
public surface and storage, bundle and delivery formats may change in any 0.x release, with no
migration promise.

Included: warm headless Blender workers with per-agent session isolation, the checkpoint
tree with frozen trajectories and deterministic replay, git-portable trajectory bundles,
an optional remote desktop GPU render tier, single-still consumer delivery, structured scene
inspection, opt-in OpenTelemetry, and the loopback Host/Origin guard. Agents set it up
themselves: the `agentblender` .NET tool package (`abl`), `abl setup`, `abl daemon
start|stop|status|logs`, user services, `abl doctor`, `abl mcp config` and a strict per-user
configuration file. An Apple silicon Mac runs as a standalone local host with Cycles on Metal,
and GPU requests report the device they used or fail with `gpu_unavailable`; the macOS
qualification is still open.

Project: CI runs on fresh GitHub-hosted runners for every pull request and every push to
main, read-only, without secrets and with every action pinned by commit SHA
(`scripts/check-ci-policy.sh` checks this); the real-Blender lane stays advisory; after it stays
green on main, the maintainer decides whether it becomes a required check, and the real-Blender tests that need a GPU run by hand on GPU hardware. The tool package declares the MIT license and the project URL and carries the
notices of the third-party assemblies it bundles. A contributing guide, security policy, code
of conduct and issue and pull-request templates describe how to take part.

### Fixed since the initial public source

- A worker spawn prewarms EEVEE with one sample instead of the scene's default 64; the other
  samples warmed nothing and cost about 17 seconds per spawn on a GPU-less host. The prewarm
  restores the render settings it overrides even when its render fails.
- The ownership proof for the configuration, token file and runtime directory no longer fails
  on a read-only file system (such as the sealed macOS system volume): it reads the owner uid
  there instead.
- `abl daemon stop` no longer signals the process group or process tree it runs in. Run from
  inside the daemon (a script one of its workers starts), `stop --force` used to kill its own
  process group, itself and anything sharing that group included; it now refuses with
  `daemon.workers` and signals nothing. The signal primitive also refuses a real signal to the
  caller's own process group.
- CI: `scripts/release-bundle.sh` passes shellcheck; the test steps have their own timeouts,
  abort and name a test that runs over 10 minutes and print their log on every outcome.

### Upgrading

This is the first public version, so there is nothing to upgrade from. Build and install from
source as the README describes (Build from source, Run it).
