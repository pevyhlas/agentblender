# AgentBlender — agent guidance

<!-- prdkit:orientation-only -->

> **This file loads into every session and every subagent; keep to orientation and pointers.**
> Never append per-change delivery narrative ("Active Technologies" / "Recent Changes"
> entries); delivered truth belongs in the spec (`specs/`), history in git.

Agent-native 3D asset pipeline on Blender (MCP + CLI over warm headless workers,
checkpointed `.blend` state). **Stage: Public preview** (experimental): surface and formats
may change in any 0.x release (`specs/product/lifecycle.md`). Read first:

1. `.prdkit/memory/constitution.md` — owner, action policy, commands, review and checks,
   principles.
2. `specs/product.md` and `specs/product/` (L0: journeys, requirements, constraints,
   lifecycle) and `specs/architecture.md` with `specs/architecture/` (L1, decisions D-01…);
   `specs/parts/` for the parts a change touches; `specs/changes/` for open changes.
3. `specs/inbox.md` — open owner questions. `specs/archive.md` is condensed history; read it
   only for a history question.

## Hard rules

- Never push `main` — branch → PR → green CI → recorded review → merge.
- CI runs on **GitHub Actions, GitHub-hosted runners only** (`.github/workflows/ci.yml`),
  read-only and without secrets; `scripts/check-ci-policy.sh` enforces it (C-09).
- **No `bpy`/Blender imports host-side** (C# host, C-08/D-01; `bpy` only under `worker/`,
  C-02).
- **MIT open source** (C-01): inbound code MIT-only verbatim (notices retained),
  Apache/copyleft ideas-only, MIT-compatible deps only, never redistribute Blender.
- Daemon/MCP binds loopback only (the desktop launcher binds a specific private-network
  address). Never expose publicly (C-03).
- External assets CC0-only with provenance; procedural-first (C-06).
- 2D authoring belongs to `agentgraphicdesign` — do not grow 2D features here (C-07).

## Practical notes

- Point `ABL_BLENDER` at a Blender 5.2.1 LTS binary (or put `blender` on `PATH`). Known 5.2
  API gotchas: engine enum, auto-smooth, denoiser-in-background.
- Test gotcha: never pipe `dotnet test` output — `MSBUILDDISABLENODEREUSE=1` and redirect
  to a file.
- Trademark posture (C-10): name stays `agentblender` (nominative use); never the Blender
  logo; non-affiliation line ships in the README.
