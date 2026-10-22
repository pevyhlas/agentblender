# AgentBlender

[![CI](https://github.com/pevyhlas/agentblender/actions/workflows/ci.yml/badge.svg)](https://github.com/pevyhlas/agentblender/actions/workflows/ci.yml)

Agent-native 3D asset pipeline on Blender: an MCP + CLI wrapper over **warm headless
Blender workers** with a **checkpointed `.blend` state tree**, producing rendered game and
product assets (hero models, prop families, icon sets) for consumer projects.

> **Experimental.** One maintainer builds AgentBlender and uses it on a few projects. It may
> not fit your project, platform or agent harness. The core pipeline works and is tested, but
> the public surface, the storage, bundle and delivery formats and the setup flow can still
> change in any 0.x release, with no migration promise; each [CHANGELOG](CHANGELOG.md) entry
> says what changed and how to upgrade. There are no published packages yet; build from
> source.

Licensed under the [MIT License](LICENSE). AgentBlender is a community project and is
**not affiliated with or endorsed by the Blender Foundation**. "Blender" is a trademark of
the Blender Foundation, used here only to describe compatibility.

**Blender is not included.** Blender is GPL-licensed software that you install yourself;
AgentBlender runs it as a separate process and never bundles or redistributes it. Only the
scripts under `worker/` run inside Blender. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Shape

- **One daemon, per-agent sessions** — each concurrent agent gets its own warm Blender
  worker; ops are pure transforms `(checkpoint, op) → checkpoint'`.
- **Checkpoint tree** — immutable content-addressed `.blend` snapshots are the branchable
  design state; winning trajectories freeze into deterministic replay scripts.
- **Two render tiers** — an always-on host (iteration/previews) and an optional remote GPU
  workstation (intermittent: heavy finals and batches, falling back to the host when it is off).
- **Token-economy contracts** — delta state reads, 512px previews, contact sheets,
  delivery renders never through model context.
- **Telemetry feedback loop** — opt-in OpenTelemetry on every op (off by default); the tool
  surface evolves on usage evidence.
- **Complements agentgraphicdesign** — 2D stays in AGD; AgentBlender owns 3D and feeds AGD.

## Start here

- Product (journeys, what must be true, lifecycle): [`specs/product.md`](specs/product.md)
- Architecture, parts and decisions: [`specs/architecture.md`](specs/architecture.md)
- Constitution (action policy, commands, review): `.prdkit/memory/constitution.md`
- Workflow: Product-Kit, vendored under `.prdkit/` (`/prdkit-*` commands for Claude Code
  and OpenCode)
- Agents: [`docs/agent-guide.md`](docs/agent-guide.md)
- Contributing: [CONTRIBUTING.md](CONTRIBUTING.md) · Security: [SECURITY.md](SECURITY.md) ·
  Code of conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## AgentBlender or AgentGraphicDesign?

Use AgentBlender when form, light, material or camera matter: 3D product and hero renders,
lighting and material studies, camera and angle exploration, and parametric variant sweeps
(`batch_run` a params table, judge one contact sheet). Finished stills leave through
`delivery_export` and go to AgentGraphicDesign (`agd`) as raster input. Flat 2D work belongs
to AGD, not here: vector logos and marks, wordmarks and text outlines, pixel art, and icon
and platform kits (ICO, ICNS, web). AgentBlender offers no motion rendering and no family
export. The MCP server sends this guidance to agents on initialize (decision D-09 sets the
boundary); the [agent guide](docs/agent-guide.md) has the working loop, the AGD handoff,
troubleshooting and why the browser-based MCP Inspector cannot connect.

[AgentGraphicDesign](https://github.com/pevyhlas/agentgraphicdesign) is a separate companion tool. The `agd` steps in these docs
are optional: `abl delivery verify` checks a delivered generation on its own, and the
[still contract](docs/still-delivery.md) documents the payload; install `agd` when you want the
receiver's own checks or the 2D work that follows.

## Build from source

**Platforms.** The daemon host is Linux x64, the reference platform and the only one
qualified end to end. An Apple silicon Mac (macOS arm64) runs the daemon as a standalone,
local-only host: it is built and unit-tested but not yet qualified, and the desktop GPU tier is
off there. There is no Windows daemon host. The optional desktop GPU launcher runs on Linux or
Windows. Other platforms are not supported.

Prerequisites:

- .NET SDK **10.0.112** (pinned in `global.json`)
- **Blender 5.2.1 LTS**, installed separately from [blender.org](https://www.blender.org/download/).
  Set `ABL_BLENDER` to the Blender binary, or put `blender` on `PATH`.
- Python 3 is only needed for the worker regression script; the host itself is C#.

```bash
git clone https://github.com/pevyhlas/agentblender.git
cd agentblender
dotnet restore agentblender.slnx --locked-mode
dotnet build agentblender.slnx -warnaserror
```

## Run it

You need a .NET 10 SDK to install `abl` (10.0.112 or later to pack this checkout, see
`global.json`; running it needs only the ASP.NET Core 10 runtime, which the SDK includes) and Blender 5.2 LTS from <https://www.blender.org/download/lts/>
(AgentBlender never downloads Blender).

```bash
# 1. install: the .NET tool `agentblender` (command `abl`; it hosts the daemon and ships worker/)
scripts/install-local.sh            # from a source checkout: packs and installs a global tool
                                    # (add ~/.dotnet/tools to PATH if the script says so)

# 2. set up this machine: discover Blender, check the paths, write the config file
abl setup                           # --dry-run shows the changes; --blender PATH pins a Blender

# 3. start the daemon (binds 127.0.0.1:7811: MCP at /mcp, REST at /api, health at /healthz)
abl daemon start                    # detached; returns once a worker is warm (or fails with a check ID)

# 4. hand the surface to an agent: prints the MCP entry and the `claude mcp add` command, writes nothing
abl mcp config --client claude      # or --client opencode

abl doctor                          # when anything fails: every check with its remedy
abl daemon status                   # the daemon_status op plus a `local` object; exit 3 when not running, 1 when it does not answer
abl daemon logs                     # the tail of this store's log
abl daemon stop                     # refuses while sessions are active unless --force
# optional user service (systemd user unit / launchd agent): abl daemon install-service [--dry-run]
# foreground form: abl daemon run [--log-file FILE]

abl session open --project demo     # returns the session id
cat > my-op.py <<'EOF'             # an op script: see "Op scripts" below
import bpy
bpy.ops.mesh.primitive_cube_add(size=params.get("size", 1))
EOF
abl scene exec <session> --script my-op.py --params '{"size":2}'
abl scene preview <session>
abl scene inspect <session> --request inspection.json  # typed selected reads; see docs/scene-inspection.md
abl trajectory freeze <session> --name hero-v1 && abl replay run hero-v1
```

**Op scripts.** `scene_exec` (`abl scene exec`) runs a Python script inside the session's
Blender, so `import bpy` (and the other Blender modules) works there. The script reads its
arguments from the global dict `params`, built from `--params` (a JSON object; `{}` when
omitted). `--script` names a local file whose text the CLI sends; over MCP and REST `script` is
the source text itself. A script that finishes changes the scene and is checkpointed
automatically, and the result returns the state delta and the new checkpoint. A script that
raises leaves the scene exactly as it was before the call. A script that runs past the exec
timeout (60 s) loses its worker; branch from the last checkpoint to continue. In a checkout of
this repository, keep op scripts outside the tree: CI allows Python that imports `bpy` only
under `worker/` (the GPL boundary, C-02).

**Install, upgrade and remove.** The package is RID-agnostic: one `agentblender.<version>.nupkg`
serves both daemon hosts (Linux, and macOS while its qualification is open; see Platforms), and
`dotnet tool install` creates the `abl` shim on your machine.
`scripts/install-local.sh [--tool-path DIR]` packs this checkout (Release) and installs it,
replacing an earlier install of the same version; `scripts/uninstall-local.sh [--tool-path DIR]`
removes it (your config file, store and renders are kept). Both refuse while a daemon runs
(`--force` overrides). Stop the daemon (`abl daemon stop`) before any upgrade or reinstall,
also one from a release bundle, and start it after: replacing the install removes the files a
running daemon reads, including the `worker/` it spawns workers from. To install from a release
bundle (`agentblender.<version>.nupkg`, `SHA256SUMS`, `provenance.json`) instead, check the sums
and install from its directory only. Use `--source`, not `--add-source`: `--add-source` also asks
your configured feeds (nuget.org), and any of them could serve a package with the same id and
version:

```bash
cd <bundle-dir> && sha256sum -c SHA256SUMS     # macOS: shasum -a 256 -c SHA256SUMS
dotnet tool install -g agentblender --source <bundle-dir> --version <version>
```

The installed `abl` runs `worker/` from its own package, never from a checkout (only a Debug
build of a checkout also looks for the checkout's `worker/`), unless `paths.workerDir`
(`ABL_WORKER_DIR`) names another one. The package carries a manifest of the worker modules'
SHA-256 values (`worker-manifest.sha256` next to `abl.dll`): `abl doctor` `worker.files`
fails when the packaged `worker/` no longer matches it, and warns when an overriding
`paths.workerDir` differs. `abl --version --json` prints the version, the supported Blender
line and the packaged worker digest.

**Network boundary.** The daemon binds IPv4 loopback only and rejects, on every route,
any request whose `Host` name is not `127.0.0.1`, `localhost` or `[::1]` (421) or that
carries an `Origin` other than its own loopback origin (403, including `Origin: null`).
This stops a web page in a local browser from reaching `scene_exec` through DNS
rebinding. Agents, the CLI, `curl` and MCP HTTP clients send no `Origin` and are
unaffected; port-remapping tunnels (`ssh -L 9000:127.0.0.1:7811 …`) keep working.
Browser-based tools such as the MCP Inspector are rejected by design. Use
`http://127.0.0.1:7811`, not `http://[::1]:7811` (no IPv6 listener).

**Status, logs and the user service.** `daemon_status` (an MCP tool, `POST
/api/ops/daemon_status`, and `abl daemon status`) reports the version, pid, URL, log
file, the effective configuration with each key's source and its digest (secrets show as
`<redacted>`), the Blender in use with its support status and `allowUnsupported`, ready
workers with the last worker error (its check ID when a spawn was refused), sessions, GPU,
desktop and telemetry state. The CLI form adds only a `local` object: the store's lock and
record files and the service state. The log has one line per event in UTC: one startup
summary, one line per worker, session or desktop transition, warnings and errors.
Framework categories (ASP.NET Core, MCP) log at `warning`, so no request lines appear;
`daemon.logLevel` sets the `Abl.*` level (a level stricter than `warning` applies to
both), and a `Logging__LogLevel__*` variable overrides either.

`abl daemon install-service` writes a systemd user unit
(`~/.config/systemd/user/abl-daemon.service`) or a launchd agent
(`dev.agentblender.daemon`) that runs this `abl` as `daemon run --log-file <the store's
log>`; `--dry-run` prints it and changes nothing, `abl daemon uninstall-service` removes
it (refused while it runs). The unit starts the daemon through `/usr/bin/env -i`, so its
environment is exactly what the unit names: `HOME`, `USER`, `LOGNAME`, `SHELL`, `PATH`,
`TMPDIR` and `XDG_RUNTIME_DIR` as they were at install, plus `DOTNET_ROOT`, taken from the
runtime running `abl` (a Homebrew Cellar path becomes the stable
`/opt/homebrew/opt/dotnet/libexec`). Nothing in the service manager's own environment
(`environment.d`, an imported login environment, `launchctl setenv`) reaches it, so the
service reads its settings from the config file alone: `ABL_*` variables and the
`worker.envPassthrough` values of a shell never reach it, and `install-service` warns
about each, and about a `blender.path` the file does not pin. Once installed, `abl daemon
start` uses the service only when the config file it reads names this store and port;
otherwise it says why: `daemon.port` when the service serves this store on another port,
`service.state` when the service was installed for this store but its config file now
names another, and a warning when it serves another store, after which `start` runs the
daemon itself. `stop` uses the service when the service runs the daemon (after `stop`'s
session check). A
service whose daemon exits before it is ready fails `start` at once; `start` stops the
service again and shows launchd's output file, or names the `journalctl --user` unit, since
that output never reaches the store log.
`stop --force` boots the service out before it kills the daemon, so no restart policy
brings it back. On Linux, `--enable-at-login` enables the unit; without linger
(`loginctl enable-linger`) a user unit stops at logout. On macOS the plist stays in the
state dir and is loaded by `abl daemon start` (into `gui/<uid>`, else `user/<uid>`);
`--enable-at-login` puts it in `~/Library/LaunchAgents`, which launchd loads at login.

**Configuration.** The daemon and `abl` resolve every setting as
**flag > `ABL_*` env > config file > discovered > default** and can tell you where each
value came from:

```bash
abl config path            # $ABL_CONFIG, else $XDG_CONFIG_HOME/agentblender/config.json,
                           # else ~/.config/agentblender/config.json
abl config show [--json]   # every key with its value and source; secrets show as <redacted>
```

The file is optional, strict JSON (`version: 1`; comments, unknown or repeated keys refuse
the start, naming the file and key). It chooses which programs the daemon runs, so it and
its directory must be owned by you and not writable by group or others (`chmod go-w`);
a symlinked file is judged at its target. Paths may start with `~/`.

```json
{
  "version": 1,
  "blender": { "path": "/Applications/Blender.app/Contents/MacOS/Blender" },
  "daemon": { "port": 7811, "logLevel": "information" },
  "paths": { "store": "~/.local/share/agentblender/store", "renders": "~/.local/share/agentblender/renders" },
  "telemetry": { "enabled": false },
  "desktop": { "endpoint": null, "tokenFile": null }
}
```

| Key | Env | Default |
|---|---|---|
| `blender.path` | `ABL_BLENDER` | discovered (below); no built-in path |
| `daemon.port` | `ABL_PORT` | 7811 (the CLI follows it unless `ABL_URL` is set) |
| `daemon.logLevel` | `ABL_LOG_LEVEL` | `information` for the `Abl.*` categories; framework categories stay at `warning` |
| `paths.store` / `paths.renders` | `ABL_STORE` / `ABL_RENDERS` | `~/.local/share/agentblender/{store,renders}` |
| `paths.runtime` | `ABL_RUNTIME_DIR` | `$XDG_RUNTIME_DIR/agentblender` (Linux, when you own it), else `$XDG_STATE_HOME/agentblender/run`, else `/tmp/abl-<uid>` (below) |
| `paths.workerDir` | `ABL_WORKER_DIR` | `worker/` in the installed package, next to `abl.dll` (a Debug build of a checkout also finds the checkout's `worker/`) |
| `telemetry.enabled` | `ABL_TELEMETRY` (`1`/`0`) | off |
| `telemetry.otlpEndpoint` / `tracesEndpoint` | `OTEL_EXPORTER_OTLP_ENDPOINT` / `…_TRACES_ENDPOINT` | `http://127.0.0.1:4318` / base + `/v1/traces` |
| `desktop.endpoint` | `ABL_DESKTOP_ENDPOINT` | off; `unsupported` on macOS (reported with that error, the tier stays off) |
| `desktop.tokenFile` | `ABL_DESKTOP_TOKEN` (the value; wins) | none; the file must be yours with mode 0600, else the desktop tier stays off |
| `blender.allowUnsupported` | — | `false`; `true` lets a Blender outside 5.2.x run, and `config show` records it |
| `worker.envPassthrough` | — | `[]`; extra variable names passed to workers (below); `PYTHON*` names are refused |

A malformed variable (for example `ABL_PORT=abc` or `ABL_TELEMETRY=yes`) stops the daemon
with `config.value`; it is never ignored. Empty variables count as unset.

**Blender.** An explicit `blender.path` / `ABL_BLENDER` is used as given: if it is missing
or unsupported that is an error (`blender.found` / `blender.version`), never a fall-through
to another Blender. Otherwise the daemon and `abl config show` probe each candidate with a
bounded `blender --version`: on macOS `/Applications/Blender.app`, `~/Applications/Blender.app`,
`/Applications/Blender*.app`, then Spotlight; on Linux `blender` on `PATH`, `/usr/bin`,
`/usr/local/bin`, `/opt/blender*/`, `~/.local/bin`, `~/blender*/`. 5.2.x is supported and
5.2.1 is the reference: 5.2.1 wins, else the highest 5.2.x (with a warning), and the
others are listed. Other versions are refused unless `blender.allowUnsupported` is set.
A discovered candidate is run only when nobody but you (or root) can swap it: the binary,
every symlink hop to it and every directory on their way up to a trust anchor must not be
writable by group or others. For anything under your home directory the walk goes all the
way up to the home, which is checked too; under `/Applications` it stops below
`/Applications` itself (root:admin); elsewhere (`PATH`, the Linux system dirs) it covers
the binary's own directory and, on macOS, its `.app` bundle. A refused candidate is listed
as `not probed` with the directory that failed and does not hide a trusted path to the same
binary. A group-writable directory such as a 0775 `~/.local/bin` (umask 002) or
`~/Applications` is refused this way: run `chmod go-w` on it or set `blender.path`. Every path
the walk checks must also be owned by you or root (a `sudo tar -xf` can keep a foreign archive
owner; a Blender.app another administrator installed counts as foreign), and on macOS must
carry no `allow` ACL entry (`ls -le`; remove with `chmod -N`), since an ACL can let others
write where the mode bits do not. A symlinked directory on the way is judged at its target,
but the target's own parents are not walked. A dangling link (for example
`/usr/local/bin/blender` after the `/opt/blender-*` it pointed to was deleted) is listed as
`not probed` and the search goes on; a dangling explicit path is a `blender.found` error.
Spotlight hits are resolved to the real bundle; those outside `/Applications` and your
home directory are listed but never run, and those inside a directory others can write,
however deep (for example below the stock 0733 `~/Public/Drop Box`), are refused by the
same walk. With `blender.allowUnsupported` and no supported
Blender, the highest version found is used. A worker whose hello version is unreadable or
differs from the probe is rejected. A Blender problem does not stop the
daemon; it is logged at start and each spawn fails with the check ID before anything is
started, so a refused Blender never runs the worker. There is no built-in Blender path: set
`blender.path` (or `ABL_BLENDER`), or rely on the discovery above.

**Runtime dir.** Worker sockets live in a short, private directory. Before every spawn the
daemon checks that the longest socket path fits `sun_path` (103 bytes on macOS, 107 on
Linux; `runtime.socket-path`) and that the final component is a real directory you own
with mode 0700 (`runtime.dir`; created that way when missing). A symlink there, another
owner or a wider mode is refused; symlinks in parent components (`/tmp → /private/tmp`)
are fine. `/tmp/abl-<uid>` (`/private/tmp` on macOS) is used only when the user-owned
choices would be too long. The store root is 0700 and sockets 0600 on every Unix.

**Worker environment.** Workers start with an allowlisted environment: `HOME`, `USER`,
`PATH`, `LANG`, `LC_*` and `TMPDIR`, plus the names listed in `worker.envPassthrough`
(for example a driver variable a GPU needs). Everything else in the daemon's environment,
such as agent API keys and tokens, never reaches Blender, and `PYTHONPATH`, `PYTHONHOME`,
`PYTHONSTARTUP` and every other `PYTHON*` variable are always removed. The discovery
`blender --version` probe gets only `HOME`, `PATH`, `LANG`, `LC_*` and `TMPDIR`. The
allowlist stops a worker from inheriting variables by accident; it is not a boundary
against code running in the worker (`scene_exec` runs as the same user and can read its
parent's environment), so prefer `desktop.tokenFile` over `ABL_DESKTOP_TOKEN` where workers
run untrusted scripts. Listing a name in `envPassthrough` trusts it with code execution in
Blender: list only driver variables, not loader or Blender script-path variables
(`LD_*`, `DYLD_*`, `BLENDER_*`). `abl daemon start` passes the listed names on to the
detached daemon so they reach its workers; a listed `DOTNET_*`, `COREHOST_*`, `CORECLR_*`,
`ASPNETCORE_*` or `Kestrel__*` name, or `URLS`, `HTTP_PORTS`, `HTTPS_PORTS` or
`AllowedHosts`, is never passed to the daemon, because it would configure the daemon rather
than Blender.

**GPU and platform.** `--engine cycles_gpu` needs a GPU backend that the worker reports
in its hello: OptiX, or Metal on a Mac. On a worker without one (for example Linux
without an NVIDIA OptiX driver), `render_final --engine cycles_gpu` fails with `gpu_unavailable`
(REST 503) and the hint `use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback`
(`scene_preview`, `batch_run` and `delivery_export`, which take no `lease`, say
`pass engine=cycles_cpu or engine=eevee`); it never renders on the CPU silently. With
`--lease cpu_fallback` it renders `cycles_cpu` instead, without waiting for the GPU lease,
the sidecar records `engine: cycles_cpu`, and the result and sidecar record
`device: {backend: cpu, name: null, fallback: gpu_unavailable}`. `cycles_gpu` never falls
back to the CPU silently; `--lease cpu_fallback` and `cycles_cpu` are the explicit ways to
render on the CPU. macOS arm64 runs as a standalone, local-only iteration host (not yet
qualified). The desktop tier is
`unsupported` there and stays off even when configured. A Mac worker reports the Metal
backend, so `cycles_gpu` passes this check and renders on Metal (only the Metal devices,
with the OpenImageDenoise denoiser on the GPU); its qualification on a real Mac is still open.
A Mac whose Blender reports no backend is refused as above, and on macOS each
`gpu_unavailable` hint ends with a pointer at the usual cause: a daemon started inside an
agent sandbox cannot see Metal. Starting the daemon outside the sandbox, for example from
your own terminal, is your choice as the owner; an agent is never told to do it, and until
then `cycles_cpu` and `eevee` work. The first `cycles_gpu` render on a Mac can take about a
minute while Metal compiles its kernels; later renders reuse the per-user cache.
`abl doctor --check gpu.render` primes that cache: a throwaway worker renders a 32 px,
1-sample `cycles_gpu` image twice in a private directory, deletes both PNGs, and reports the
device and both times (an interrupted run can leave a `d-gpu-*` directory in the runtime dir). A fast second render passes (with `firstCompileSuspected: true` when
the first took over 30 s); a second render still over 10 s warns; `gpu_unavailable` fails.
Doctor is otherwise read-only, so a plain `abl doctor` reports this check as `skip` (`opt-in`).
`daemon_status` reports `gpu: {backend, devices, observedAt}` from the last ready local
worker's hello (`null` before any).

A Cycles `render_final` reports a `device` object in its result and sidecar, and a Cycles
`batch_run` reports the batch's `device`: the device AB configured and checked just before
the render (`{backend: optix|metal|cpu, name}`, plus `fallback` after a CPU fallback), not
proof of where the pixels were computed. Eevee and Workbench renders, previews and delivery
receipts carry none. Devices are selected per `cycles_gpu` render and restored
afterwards; the worker enables no GPU device for its whole life. A raw
`bpy.ops.render.render()` inside `scene_exec` therefore sees the factory device preferences
(the CPU) unless the script selects a device itself; use `render_final` for GPU renders.

**Worker and host versions:** the worker hello reports `gpu: {backend, devices, metalrt}`.
The host refuses any other shape as `worker_upgrade_required`, so a desktop worker that does
not match the host serves no renders (they fall back to the local tier) until the launcher and
its bundled `worker/` are republished together with the host.

## Ship it to a repo

Sessions are ephemeral and stores are machine-local — **git is the durable home** of
finished work (D-14). A frozen trajectory exports as a two-file bundle any repo can
carry, and reconstitutes anywhere with the pinned Blender (5.2.1 LTS):

```bash
# on the machine that did the work
abl trajectory freeze <session> --name crate-v1
abl trajectory export crate-v1 --out ~/myproject/assets/abl
printf '*.blend -text\n' >> ~/myproject/.gitattributes   # never let git touch .blend bytes
git -C ~/myproject add assets/abl .gitattributes && git commit -m "crate-v1 bundle"

# on any clone (fresh machine, fresh store)
abl trajectory import ~/myproject/assets/abl/crate-v1
abl replay run crate-v1          # PASS = scene reconstituted, digest-verified
echo '[{}]' > variants.json      # frozen params as-is; add rows to overlay params
abl batch run crate-v1 --params-file variants.json   # regenerate deliverables — renders are never committed
```

Import verifies the bundle before touching the store (SHA-256 of `base.blend` must equal
the recorded base hash — catches git/LFS mangling) and is a clean no-op on re-import.

## Delegate heavy renders to a desktop GPU

The daemon host stays the brain; a second machine's GPU (e.g. a workstation GPU) can serve
`render_final` as a **headless remote worker** over a private network (for example a VPN
between your machines). Interactive ops always stay local; a full render prefers the desktop
when it is reachable and **falls back to the host** automatically when it is not. With the desktop unconfigured, everything runs
locally exactly as before.

On the **desktop** (needs the pinned Blender 5.2.1 and, to build the launcher, the .NET 10
SDK; `dotnet publish` bundles `worker/` next to the launcher, so no separate checkout is
needed to run it):

```bash
# publish the launcher once (self-contained: worker/ is copied alongside it)
dotnet publish src/Abl.Launcher -c Release -o ./abl-launcher

# a specific private-network address only (never 0.0.0.0 — the launcher refuses wildcard
# binds), and a shared secret. Keep the token out of the repo.
export ABL_LAUNCHER_BIND=192.0.2.10:7812    # this machine's private-network IP
export ABL_LAUNCHER_TOKEN=$(openssl rand -hex 24)
export ABL_BLENDER="/path/to/blender"        # blender.exe on Windows
./abl-launcher/abl-launcher                  # headless; Ctrl-C to stop
```

On the **daemon host**, point the daemon at it (same token) and restart:

```bash
export ABL_DESKTOP_ENDPOINT=192.0.2.10:7812
export ABL_DESKTOP_TOKEN=<the same token>
# renders now route to the desktop when it is up; render a live session's head ...
abl render final --session <session> --engine cycles_gpu        # prints "... tier desktop"
# ... or a stored checkpoint, by hash, unique hash prefix or checkpoint tag
abl checkpoint list --session <session>
abl checkpoint tag <hash> crate-final
abl render final --checkpoint crate-final --engine cycles_gpu
```

`--checkpoint` takes a checkpoint hash, a unique hash prefix or a checkpoint tag
(`abl checkpoint save --tag` / `abl checkpoint tag`). It does not take a frozen-trajectory or
artifact name such as `crate-v1`.

The base checkpoint is shipped to the desktop and the pixels come back to the host's disk;
the render sidecar records `tier` and the producing Blender. Network ACLs should allow
only the daemon host to reach the launcher port.

### Large scenes and concurrent production

Workers advertise `blob_chunks` in their hello. Checkpoints, PNGs, variants and contact sheets
move as ordered **1 MiB chunks** with length/SHA-256 verification. Files can exceed 64 MiB; that
guard bounds each control message, not the file. Transfers stream through disk; downloads
publish only after verification. Disconnect cleans connection-owned partial files. Available
disk and per-message deadlines still apply; there is no fixed total file-size cap.

Republish **Abl.Launcher together with its bundled worker/** whenever the host changes: a
host-only update never changes the desktop's worker. Workers from before chunked transfer
also report the older hello shape, so the host refuses them as `worker_upgrade_required` and the
job runs locally (see Worker and host versions).

Default batch/final paths include a unique invocation ID. An explicit `--out` still names
the caller's chosen file. CLI requests have no implicit total deadline; set
`ABL_HTTP_TIMEOUT_SECONDS` for an explicit deadline. Ctrl-C cancels cleanly. Worker deadlines
remain independent, so a batch can take longer than one render without a client-side timeout.

## Telemetry and monitoring (optional)

Telemetry is opt-in. Turn it on in the config file: a service written by
`abl daemon install-service` reads its settings from that file alone (its unit starts the
daemon through `env -i`), so `Environment=` lines and drop-ins do not reach the daemon.
Metrics export with cumulative temporality by default.

```json
{
  "version": 1,
  "telemetry": {
    "enabled": true,
    "otlpEndpoint": "http://127.0.0.1:4318",
    "tracesEndpoint": "http://<traces-collector>:4318/v1/traces"
  }
}
```

Point OTLP at any OpenTelemetry collector you run; metrics and traces can go to separate
endpoints. The daemon only pushes outbound and never opens a listener for telemetry. With
telemetry enabled, `service.instance.id` is the machine's host name (override it with
`OTEL_RESOURCE_ATTRIBUTES`). Export interval defaults to 10 seconds (`OTEL_METRIC_EXPORT_INTERVAL`).
Changing the unit requires daemon-reload/restart; preserve active checkpoint heads first.

Observed Prometheus names after OTLP-to-Prometheus conversion:

| Signal | Metric |
|--------|--------|
| Operations | `abl_ops_total{op,outcome,tier}` |
| Duration | `abl_op_duration_milliseconds_{bucket,sum,count}` |
| Context-cost proxy | `abl_op_payload_bytes_{bucket,sum,count}` |
| GPU contention | `abl_gpu_lease_wait_milliseconds_{bucket,sum,count}{tier,outcome}` |
| Independent freshness | `abl_daemon_heartbeat_seconds` (Unix timestamp) |
| Runtime counts | `abl_workers_ready`, `abl_sessions_active` |
| Desktop TCP probe | `abl_desktop_configured`, `abl_desktop_available` (-1 unknown, 0 unavailable, 1 reachable) |

Payload sizes count logical returned UTF-8 content (JSON for structured content), excluding
transport envelopes; different REST/MCP payload shapes can legitimately have different sizes.
Metrics carry bounded op/outcome/tier tags, never paths, scripts, project names or session IDs.
Project/session attribution lives on traces. `tier=none` means no render tier was selected.
TCP availability is not token/Blender/OptiX proof; an actual `tier=desktop` render establishes it.
Dashboards and alert rules can be built from these metrics (they live outside this repo).
The heartbeat detects outages even while idle; fault alerts should require enough calls to
avoid paging on one exploratory error.

Render/batch results expose `fallbackReason` when a configured desktop cannot serve the job;
`worker_upgrade_required` means republish the launcher and worker together. Cancellation of a
session-owned worker marks the session dead and retains its checkpoint; the next operation
returns the exact checkpoint-branch recovery instruction.

Bundle contents are **your own work** — your scripts and your scene data (what you create
with Blender is your sole property, per the Blender Foundation) — and carry no
AgentBlender or Blender license obligation into the repo that commits them.

## Deliver finished assets to a consumer

`delivery export` renders **one frozen procedural still** into a fresh completed
generation with an exact AGD v1 descriptor/PNG payload. Producer receipt, recipe
snapshot, replay source and preview stay outside that strict payload. Verification
is not admission, provenance approval or editable import. Zero/one parameter-overlay
row is supported; family expansion follows qualification of this first profile.

```bash
abl delivery export crate-v1 --out ~/deliveries --res 1024
# Use the returned generation path, not its payload subdirectory, for producer checks:
abl delivery list   ~/deliveries/crate-v1-GENERATION
abl delivery verify ~/deliveries/crate-v1-GENERATION  # no daemon or Blender required
# Optional, with AgentGraphicDesign (a separate tool): the receiver's own payload checks
agd assets preflight ~/deliveries/crate-v1-GENERATION/payload/artifact.json -f json
agd assets verify   ~/deliveries/crate-v1-GENERATION/payload/artifact.json -f json
```

Delivery paths (the output parent, the generation and every file read back) must not
contain a symlink, except a system alias: a link in a root-owned directory that nobody
else can write, such as `/tmp`, `/var` and `/etc` on macOS (links into `/private`). So a
`--out` under `/tmp` or the macOS per-user temp directory works, while a link you or
another user created in any component of the path is refused (`bad_request`). Paths are
judged after `..` is resolved lexically, so do not combine `..` with a link.

`delivery verify` checks encoded/source/snapshot self-consistency; replay and decoded
RGBA checks are separate. Preserve complete generations and required source bytes in
project-managed durable storage; rerendering is not proof of identical pixels. See
[the still contract and the receiver qualification](docs/still-delivery.md).

The source op surface (identical over MCP and CLI) is specified in
[`docs/public-surface.md`](docs/public-surface.md); the worker protocol in
[`docs/worker-protocol.md`](docs/worker-protocol.md). `worker/` is the only directory
that imports `bpy` (GPL boundary — CI-enforced).

## Development

Issue first for anything beyond a small fix, then a draft PR → green CI (GitHub Actions,
GitHub-hosted runners) → review → the maintainer merges. See
[CONTRIBUTING.md](CONTRIBUTING.md) for the full validation list. Build/test commands also live
in the constitution's Commands section:

```bash
dotnet build agentblender.slnx -warnaserror
# never pipe test output; MetalQualification (an Apple silicon Mac) and OptixQualification
# (a Linux host with an NVIDIA OptiX GPU) run only by hand on their hardware
MSBUILDDISABLENODEREUSE=1 dotnet test agentblender.slnx --filter "Category!=BlenderIntegration&Category!=MetalQualification&Category!=OptixQualification" > /tmp/testrun.log 2>&1
MSBUILDDISABLENODEREUSE=1 dotnet test agentblender.slnx --filter "Category=BlenderIntegration&Category!=OptixQualification" > /tmp/itest.log 2>&1  # needs $ABL_BLENDER
```

**SDK.** `global.json` names the reference SDK 10.0.112 with `rollForward: latestFeature`, so
any later 10.0 SDK (10.0.2xx and up) builds without a download; `LangVersion` 14 and
`AnalysisLevel` 10.0 are pinned in `Directory.Build.props`, so a newer band compiles the same
language with the same analyzers. CI rewrites `global.json` to exactly 10.0.112 with
`rollForward: disable` before restoring, asserts `dotnet --version`, and the release bundle
records it. Caveat: restore runs with `--locked-mode`, so if a newer SDK band rewrites a
`packages.lock.json`, do not commit that change unless you meant it; regenerate lock files
with the reference SDK.

**Release bundle.** `scripts/release-bundle.sh [--out DIR]` (with the reference SDK and a
clean tree; `--allow-dirty` marks the bundle dirty) packs `agentblender.<version>.nupkg`,
installs it into a temporary `--tool-path`, runs `abl doctor --quick --json` there in an empty
environment (`worker.files` must pass against the packaged manifest), and writes
`SHA256SUMS` and `provenance.json` (package hash, source commit, SDK, supported Blender line,
worker digest) into `artifacts/release-bundle/`. CI runs it on every change. It tags and
publishes nothing: a published release is the owner's step.
