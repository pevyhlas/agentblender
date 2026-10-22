# J-04 Deliver a still (hypothesis)

**Who**: asset agent, for the project developer · **Area**: Ship · **Surfaces**: the `abl` CLI or
MCP; the AgentGraphicDesign (AGD) receiver in the consuming project

## Intent

Hand a finished procedural still to a consuming project with its provenance, in a form the
project's 2D tooling (AGD) checks, so the project never runs AgentBlender.

## Situation

A frozen design renders the still. The consuming project accepts assets only through an explicit
export it can verify offline; hand-copied files are not accepted (C-11).

## Scenario

1. The agent exports one still into a fresh, immutable delivery generation.
2. The payload holds exactly the AGD provided-artifact descriptor and the PNG; a producer receipt
   with the recipe and replay bundle sits beside it.
3. The agent verifies the generation offline and lists earlier generations.
4. The consuming project checks the payload with AGD's preflight and verify.

## Variations

- **A colour profile cannot be met**: the export fails closed; nothing is published.
- **A symlinked delivery path** (other than a system alias such as macOS `/tmp`): refused.
- **No AGD at hand** (AgentGraphicDesign is a separate tool): the offline verify in step 3 is
  the check; AGD's preflight and verify run wherever the receiver is installed.
- **A GPU still on a host without a GPU backend**: refused with `gpu_unavailable` unless the
  caller accepts the CPU (R-13).

## Success

The consuming project admits the delivered still without running AgentBlender (R-08). For the
business: delivered assets carry their provenance into every consuming project.
