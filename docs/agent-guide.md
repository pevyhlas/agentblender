# Agent guide

How an agent should use AgentBlender (AB), and when AgentGraphicDesign (AGD, `agd`) is the
right tool instead. The MCP server sends a short form of this guide as its `instructions` on
initialize; this page and the README say the same thing at more length.

## AB or AGD?

The boundary is decision D-09: 3D modelling, scenes and rendering belong to AB; 2D authoring,
linting and export belong to AGD. AB feeds AGD with rendered stills and never grows 2D
authoring features.

| The task is about… | Use |
|---|---|
| 3D product or hero renders | AB |
| Lighting and material studies | AB |
| Camera and angle exploration | AB |
| Parametric variant sweeps judged on one contact sheet | AB (`batch_run`) |
| A finished still that a 2D asset will be built around | AB (`delivery_export`), then AGD |
| Vector logos and marks, wordmarks, text outlines | AGD |
| Pixel art | AGD |
| Icon and platform kits (ICO, ICNS, web) | AGD |
| Laying out, recolouring or exporting a 2D design | AGD |

Ask whether form, light, material or camera matter. If they do, start in AB. If the result is
flat (a mark, lettering, a pixel sprite, an icon kit), use AGD and do not open a Blender
session for it. Anything ambiguous is settled against D-09 before work starts; AB does not
offer a 2D fallback.

AB does not offer motion rendering or the export of a family of stills. One delivery is one
still.

## What the server tells an agent

The `instructions` sent on MCP initialize (kept to 250 words or fewer):

> AgentBlender renders 3D scenes in warm headless Blender sessions. Reach for it when form,
> light, material or camera matter: 3D product/hero renders, lighting and material studies,
> camera/angle exploration, and parametric variant sweeps: freeze a trajectory, then
> `batch_run` a params table and judge one contact sheet. Start with `session_open`, iterate
> with `scene_exec` + 512 px `scene_preview`, read state as deltas, checkpoint before
> branching. Deliver finished stills with `delivery_export` (one still per generation) and
> hand them to AgentGraphicDesign as raster input. Do NOT use AgentBlender for flat 2D work:
> vector logos/marks, wordmarks and text outlines, pixel art, icon and platform kits
> (ICO/ICNS/web) belong to AgentGraphicDesign (`agd`). Final renders go to disk, never into
> context. The first Metal render (`cycles_gpu` on a Mac) compiles kernels (~1 min; do not
> cancel it); `abl doctor --check gpu.render` primes the cache. Run `daemon_status` if
> something looks misconfigured.

## Connecting

The daemon serves MCP over HTTP at `http://127.0.0.1:7811/mcp` (or the configured
`daemon.port`). On a new machine, once `abl` is installed (the .NET tool `agentblender`; see
the README "Run it"; `abl --version --json` says which version and worker it carries):

```bash
abl setup                         # discover Blender, check paths, write the config file
abl doctor                        # check this host; every failure names its remedy
abl daemon start                  # returns once a worker is warm
abl mcp config --client claude    # or --client opencode; prints the entry, writes nothing
```

`abl mcp config` prints the client entry for the effective port, and for Claude Code the
matching `claude mcp add --transport http agentblender <url>` command. AB does not write
client configuration files itself.

Every MCP tool also exists as a REST route (`POST /api/ops/<op>`) and an `abl` verb, with the
same arguments and results.

## The working loop

1. `session_open` gives you a warm Blender session for a project.
2. Change the scene with `scene_exec` (a Python script that runs inside Blender, imports `bpy`
   and reads its arguments from the global dict `params`; see Op scripts in the
   [usage reference](../REFERENCE.md#run-it)) and look at it with `scene_preview`. Previews are at most
   512 px, which is enough to judge form and light.
3. Read state as deltas with `scene_state`. Use `scene_inspect` for typed reads of selected
   objects, landmarks or the camera (see [scene inspection](scene-inspection.md)). Ask for
   the full state only when you need it.
4. `checkpoint_save` before you try an alternative; `checkpoint_branch` opens a new session at
   a saved checkpoint. Checkpoints are immutable, so a failed experiment never damages the
   state you kept, and `scene_exec` checkpoints automatically after each change.
5. When a direction works, `trajectory_freeze` it into a replayable artifact.
6. For variants, `batch_run` the frozen artifact over a params table and judge the one contact
   sheet it returns, rather than rendering and reading each variant in turn.
7. `render_final` writes a full render to disk. `delivery_export` writes one finished still as
   a generation that AGD can take as raster input.

Final renders, batch outputs and deliveries go to disk and come back as paths. Do not read
them back into context; use the preview or the contact sheet to judge them.

## Handing a still to AGD

```bash
abl delivery export hero-v1 --out deliveries --res 1024
abl delivery verify deliveries/hero-v1-GENERATION        # offline: no daemon, no Blender
# optional, where AGD (a separate tool) is installed:
agd assets preflight deliveries/hero-v1-GENERATION/payload/artifact.json -f json
agd assets verify   deliveries/hero-v1-GENERATION/payload/artifact.json -f json
```

Use the generation path the export returns. [AGD](https://github.com/pevyhlas/agentgraphicdesign) is a separate companion tool, so
the `agd` steps are optional; without it, `abl delivery verify` is the check. The `payload/`
directory holds exactly what AGD reads; the rest of the generation is AB's own receipt and
replay source. The full contract is in [still delivery](still-delivery.md). From there the 2D
work (placement, type, recolouring, icon kits) happens in AGD.

## When something goes wrong

User services stop retrying explicit configuration, private-directory and occupied-store-lock
refusals. Ordinary `abl daemon run` returns78 for these; systemd suppresses automatic retries.
The generated launchd job uses `--launchd --log-file FILE` and exits0 on refusal solely to stop
KeepAlive, with the original error in the job's output. Exit0 here does **not** mean ready:
`abl daemon start` still requires a warm worker and reports a stopped job as failure. Fix the
reported check, then start again. Other startup failures retain the manager's restart policy.

To update an existing service definition, run `abl daemon stop` first. Proceed only when it
succeeds or reports not running (exit3); active-session refusal must be resolved first. Then
run `abl daemon uninstall-service`, require success, run `abl daemon install-service` (include
`--enable-at-login` if wanted), and `abl daemon start`. This unloads an old launchd job before
loading the new argv; merely rewriting its plist does not activate the new policy.

- **The CLI cannot reach the daemon.** It prints
  `abl: cannot reach the daemon at <url> — start it with 'abl daemon start' (diagnose: 'abl doctor')`.
  Do what it says. `abl doctor --json` returns a `doctor.v1` report in which each failed
  check has an ID and an exact remedy command.
- **Something looks misconfigured while the daemon runs.** Call the `daemon_status` tool. It
  reports the effective configuration with each key's source, the Blender in use, ready
  workers with the last worker error, sessions and GPU state.
- **`gpu_unavailable`.** This worker has no GPU backend (OptiX, or Metal on a Mac). Use
  `engine=cycles_cpu` or `engine=eevee`, or `lease=cpu_fallback` on `render_final`. AB never
  falls back to the CPU silently. On a Mac the usual cause is a daemon started inside an agent
  sandbox, which cannot see Metal. Do not start the daemon outside your sandbox yourself;
  tell the user, and keep working on the CPU engines until they decide.
- **The first `cycles_gpu` render on a Mac is slow.** Metal compiles its kernels on the first
  render, which can take about a minute. Do not cancel it; later renders reuse the cache.
  `abl doctor --check gpu.render` primes the cache ahead of time: it renders a 32 px image
  twice on a throwaway worker and reports both times (`firstCompileSuspected: true` means the
  first render compiled the kernels). A plain `abl doctor` skips this check (`opt-in`).

## Network boundary

The daemon binds IPv4 loopback only and rejects, on every route, a request whose `Host` name
is not `127.0.0.1`, `localhost` or `[::1]` (421) or whose `Origin` is anything other than its
own loopback origin (403, including `Origin: null`). This keeps a web page in a local browser
from reaching `scene_exec`.

Agents' MCP HTTP clients, the CLI and `curl` send no `Origin` and are unaffected. **The browser-based MCP Inspector is rejected by
design**: it runs in a browser page, so it sends an `Origin` the guard refuses. To look at
the server's tools, use your MCP client's own tool listing, the `abl` CLI or the REST routes
instead. Use `http://127.0.0.1:7811`, not `http://[::1]:7811`, since there is no IPv6
listener. A port-remapping tunnel to `127.0.0.1` keeps working.
