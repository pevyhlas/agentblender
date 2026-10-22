# AgentBlender — requirements

<!-- R- items by area, using the journey index's area names. Each says what must be true for
users, testable in principle, the journeys it serves and its source. Past the budget, split into
requirements/<area>.md and keep one line and a link per area here. -->

## Design

- **R-01 Agent-only operation** (validated): every capability is operable end to end by a
  non-interactive agent through either MCP or CLI, with one op vocabulary and identical
  semantics; opening a result in Blender's GUI is an inspection affordance, never a step.
  Journeys: J-01, J-02, J-03, J-04, J-05, J-06. Source: owner's founding design.
- **R-02 Branchable design state** (validated): design state is a tree of immutable
  checkpoints that can be saved, branched into a fresh session, listed, tagged, diffed and
  pruned; programmatic undo is never the mechanism; every checkpoint opens in Blender's GUI.
  Journeys: J-01. Source: owner's founding design (D-03).
- **R-05 One-look batch verification** (validated): a frozen design fans across a parameter
  table into on-disk renders plus one contact sheet. Journeys: J-02. Source: owner's founding
  design (D-05).
- **R-06 Session isolation** (validated): concurrent sessions never share mutable scene state;
  a crashed or hung worker affects only its session, which recovers from its last checkpoint.
  Journeys: J-01. Source: owner's founding design (D-11).
- **R-09 Measured inspection** (hypothesis): agents read bounded structured scene measurements
  (transforms, camera, optional geometry, material and modifier facts) without advancing state.
  Journeys: J-01. Source: owner-selected inspection work (archived feature 007).
- **R-10 Evidence-driven surface** (validated): opt-in operation telemetry shows usage,
  outcome, serving tier, latency and payload size, and surface changes cite that evidence.
  Journeys: J-01, J-02, J-05. Source: owner's founding design (D-08).

## Ship

- **R-03 Reproducible frozen designs** (validated): a winning trajectory freezes into a
  self-contained artifact that reconstitutes the same scene content on any machine with the
  pinned Blender version; verification compares scene content within a stated bound, never
  file bytes or render pixels. Journeys: J-01, J-03. Source: owner's founding design.
- **R-04 Git-portable finished work** (validated): a frozen design exports as a small bundle a
  consumer repo commits; import is integrity-checked before any write and idempotent.
  Journeys: J-03. Source: owner directive (D-14).
- **R-08 Delivery with provenance** (hypothesis): assets reach consumer projects only through
  explicit export with a provenance record that the consumer verifies offline; the first lane is
  one procedural still per generation (D-12). Journeys: J-04. Source: owner decision (D-12).

## Render

- **R-07 Non-blocking heavy tier** (hypothesis): final renders and batches may use an optional
  remote GPU tier; its absence never blocks iteration or silently loses a job, and the serving
  tier is recorded with the result. Fallback semantics: an open [inbox](../inbox.md) question.
  Journeys: J-05, J-02. Source: owner decisions (D-06, D-15).
- **R-13 Honest GPU reporting** (hypothesis): a GPU render request on a host or worker without
  a GPU backend fails with an actionable error instead of silently rendering on the CPU, unless
  the caller accepts the CPU; every Cycles result names the device configured and checked for
  it. Journeys: J-05, J-04. Source: change 009-metal-gpu (D-17).

## Setup

- **R-11 Fresh-agent setup** (hypothesis): an agent installs, configures, starts, diagnoses and
  connects AgentBlender without a source build, hand-set environment or wrapper scripts, and
  learns when 3D work belongs here and when it belongs to AGD. Journeys: J-06. Source: field
  evidence on a pilot project (change 008-agent-operability).
- **R-12 macOS host** (hypothesis, planned: 009-metal-gpu): an Apple silicon Mac is a supported
  standalone, local-only iteration host, rendering Cycles on its GPU through Metal.
  Journeys: J-06, J-05. Source: owner decision.

## Left open

- `abl mcp install` (writing an MCP client's config file): deferred until an onboarding design
  passes review; `abl mcp config` prints the entry today.
