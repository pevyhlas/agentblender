# Configuration

**Inputs**: the requested policy or capability change, existing project files.
**Writes**: files under `.prdkit/memory/`, optional `.prdkit/agents/` definitions.
**Result**: the applied change and its effect on open changes.

Edit `.prdkit/memory/constitution.md` as the owner directs, explaining a material change's effect
on open changes first; a configuration edit never grants itself authority. `agents.md` maps
capabilities to agents or humans. Optional neutral definitions
(`.prdkit/templates/agent-definition-template.md`) name a tier, never a model; render projections
with `.prdkit/scripts/bash/render-agents.sh` and `--check`, never by hand. Both install with
`init.sh --upgrade --with agent-tiers`; propose that to the owner first when they are missing. `config.md` holds
optional settings. Run doctor afterward.
