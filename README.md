<div align="center">

# AgentBlender

### Give your agents a 3D asset pipeline.

[![CI](https://github.com/pevyhlas/agentblender/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/pevyhlas/agentblender/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Status: experimental](https://img.shields.io/badge/status-experimental-orange.svg)](#status)

[Quick start](#quick-start) · [Agent guide](docs/agent-guide.md) · [Reference](REFERENCE.md) · [Roadmap](docs/public-readiness.md)

</div>

Build scenes, compare variants and ship renders through **MCP or the `abl` CLI**.
Blender stays warm between edits; checkpoints let agents branch and replay their work.

- **Fast iteration:** one isolated Blender worker per session.
- **Small feedback:** scene deltas, previews and batch contact sheets.
- **Reproducible delivery:** replay bundles and stills with provenance.
- **Local first:** optional private GPU workstation; telemetry off by default.

## AgentBlender or AgentGraphicDesign?

Use AgentBlender when form, light, material or camera matter: product renders, props and
parameter sweeps (`batch_run`). Use AgentGraphicDesign (`agd`) for vector logos and marks,
wordmarks, pixel art, and icon and platform kits ([repository](https://github.com/pevyhlas/agentgraphicdesign)).
`delivery_export` hands stills to AGD. The [agent guide](docs/agent-guide.md) covers the loop
and why the browser-based MCP Inspector is rejected by design.

## Quick start

Install **.NET SDK 10.0.113** and **Blender 5.2.1 LTS** separately. Point `ABL_BLENDER` at the
Blender executable, or put `blender` on `PATH`. AgentBlender never downloads or bundles Blender.

```bash
git clone https://github.com/pevyhlas/agentblender.git
cd agentblender
bash scripts/install-local.sh

abl setup --dry-run             # review discovery and proposed configuration
abl setup
abl daemon start                # loopback only: http://127.0.0.1:7811
abl doctor
abl mcp config --client claude   # prints configuration; does not edit your client
# Or: abl mcp config --client opencode
```

Add `~/.dotnet/tools` to `PATH` if needed. Follow the [agent guide](docs/agent-guide.md)
or [CLI examples](REFERENCE.md#run-it). Stop with `abl daemon stop`.

**Local code execution:** scene scripts run Python with your user privileges, not in a sandbox.
Keep the daemon on loopback. See [security](SECURITY.md).

## Status

**Experimental; build from source.** Linux x64 is qualified. macOS/Metal qualification is open;
no Windows daemon host, motion rendering or family delivery. APIs and formats can change in 0.x.
See [readiness and roadmap](docs/public-readiness.md), [changelog](CHANGELOG.md) and
[operation reference](docs/public-surface.md).

## Contributing and license

[Contributing](CONTRIBUTING.md) · [Brand artwork](docs/brand/README.md) · [Product and architecture](specs/product.md) · [MIT](LICENSE)
· [Third-party notices](THIRD-PARTY-NOTICES.md). Fork-safe GitHub-hosted CI;
[Product-Kit 1.1.1](https://github.com/pevyhlas/product-kit) vendored, no submodule required.

Blender is separately installed GPL software, never bundled here. AgentBlender is **not affiliated
with or endorsed by the Blender Foundation**; “Blender” is its trademark, used for compatibility.
