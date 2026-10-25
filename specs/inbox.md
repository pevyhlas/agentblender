# Owner inbox

<!-- Sections: open agent questions, then the owner's direction, then deferred and declined agent
items; oldest first within each. Each item is a `###` heading, then one line
*<YYYY-MM-DD> · <owner|agent> · <disposition>*: open, folded: <where>, declined: <why> or
deferred: <until>.

An agent question: a heading that names the decision, a sentence or two of context with links,
numbered options as `**<label>**: <what it means>`, the recommended one first and marked
(Recommended), and, when the choice is reversible, `**If no answer**: <default>`. The owner
answers with an option number or their own words.

An owner item: a short heading, the owner's words verbatim as a `>` quote (also when relayed
verbatim by another session), and one line on what it answers. In this public repository owner
direction is recorded paraphrased and its meta line has no date (`*owner · <disposition>*`), so
doctor lists it as an advisory and status skips it.

Name roles, never people. Delete a folded item once its change has landed. -->

## Waiting for you

### Confirm the restructured spec

*2026-10-20 · agent · open*

The spec now follows the Product-Kit 1.1 layout: [product](product.md) with journeys and detail
files, the [architecture](architecture.md) overview with topics, parts as boxes (P1 Host absorbs
the former P2, P6, P7 and P8 as components), [changes](changes/) for 008 and 009, and a condensed
[archive](archive.md). Maturity marks: validated where an owner-ratified need and passing
real-Blender checks exist, hypothesis otherwise; the six journeys start as hypothesis. A
review pass added delivery and inspection behaviors (P1.B-24 to P1.B-28, each with its tests)
and split P4 and P5 into components.

1. **Confirm** (Recommended): treat the spec as current truth; promote journeys you consider
   validated.
2. **Correct**: name what is wrong; an agent fixes it before the next change.

**If no answer**: option 1.

### Desktop fallback: automatic or the caller's choice

*2026-10-20 · agent · open*

Constitution Principle 6 and the founding D-06 wording say a job waits for the desktop or falls
back locally by explicit caller choice through a job queue; D-15, feature 003 and the code fall
back to the host automatically, record a `fallbackReason`, and offer no wait option or queue.

1. **Automatic fallback** (Recommended): amend Principle 6 to automatic fallback with a recorded
   reason; add an opt-in wait only if a consumer asks.
2. **Caller choice**: build a wait option and a queue.

**If no answer**: the spec describes automatic fallback; no wait option is built.

### `bpy` pip module or the Blender binary

*2026-10-20 · agent · open*

An old open decision listed this as open, while the 001 research resolved it for the binary
because the wheel pins Python 3.13 (D-02).

1. **Close it for the binary** (Recommended): revisit only if desktop worker provisioning makes
   the binary route painful.
2. **Keep it open**: a spike compares the wheel.

**If no answer**: treated as decided for the binary.

### Telemetry attribution: traces only

*2026-10-20 · agent · open*

Constitution Principle 7 lists session and consumer project among per-op telemetry fields next to
metrics; D-08 and the code keep them on traces only, with bounded op, outcome and tier metric
labels.

1. **Traces only** (Recommended): amend Principle 7 to the trace-only rule.
2. **Metrics too**: add bounded attribution labels to metrics.

**If no answer**: the trace-only behavior stays.

### Checkpoint cadence

*2026-10-20 · agent · open*

The founding D-03 said checkpoint at branch points and every few ops; the 001 research and the
code checkpoint after every successful exec (a save costs about 8 ms).

1. **Every exec** (Recommended): make it the rule (D-03 already states it).
2. **Sparser**: checkpoint at branch points and every N ops.

**If no answer**: the spec states every exec.

### When the deferred hardening is due

*2026-10-20 · agent · open*

The old stage model deferred a durable job queue and format stability to pre-release, and
endpoint authentication and code-exec sandboxing to GA. Lifecycle stages now carry this
([lifecycle](product/lifecycle.md), Exit when).

1. **All four before GA** (Recommended): authentication and `render_final` output-path
   confinement land together; each becomes an item marked `from: ga`.
2. **Some earlier**: name which must hold during Public preview.

**If no answer**: all four stay deferred under Left open, as GA exit criteria.

### Constraint sources written by an agent

*2026-10-20 · agent · open*

C-05 (no content capture in telemetry), C-09 (GitHub-hosted CI only, no PR secrets, SHA-pinned
actions), the C-04 clause on machine names, private-network details and consumer paths, and the
tracked-tree-only packaging rule in the constitution's Release and tag policy cite only release
preparation an agent wrote ([constraints](product/constraints.md)).

1. **Confirm them as owner rules** (Recommended).
2. **Amend**: name the change.

**If no answer**: enforced as written.

### First public release

*2026-10-20 · agent · open*

An earlier owner authorization let an agent tag and publish the first packaged release once
qualified, while the repository was private. Change 008-agent-operability carries the release
step.

1. **Explicit go required** (Recommended): no public tag or release without the owner's go.
2. **The earlier authorization covers it.**

**If no answer**: no tag or release without that go.

### Distribution channel

*2026-10-20 · agent · open*

The tool package and release bundle exist (D-16). Self-contained per-platform binaries and macOS
signing and notarization were deferred.

1. **Release bundle on GitHub Releases; binaries deferred** (Recommended).
2. **Also a public NuGet feed**.
3. **Self-contained binaries now**: a build matrix and signing spend.

**If no answer**: option 1.

### Lifecycle stage: Public preview

*2026-10-22 · agent · open*

The project's status is experimental and it is published as an open-source repository. The 1.1
stage that matches is Public preview with the promise "experimental, interfaces may change"
([lifecycle](product/lifecycle.md)).

1. **Public preview** (Recommended): anyone may use it; formats and the op surface may break in
   any 0.x release; GA is next.
2. **Private preview**: if publication waits, invited users only.
3. **Another stage**: name it.

**If no answer**: option 1.

### Blender acquisition at the first public release

*2026-10-22 · agent · open*

D-18 says AgentBlender never downloads Blender, to be revisited at the first public release.

1. **Keep it** (Recommended): setup and doctor point at the official 5.2 LTS download page.
2. **Download with pinned checksums**: about 400 MB per install and a checksum table to keep.

**If no answer**: option 1.

### macOS qualification

*2026-10-22 · agent · open*

The Mac host and Metal rendering are built; both qualifications need an Apple silicon Mac with
Blender 5.2.x ([008](changes/008-agent-operability.md) T074, [009](changes/009-metal-gpu.md)
T032 and T033). C-12 and R-12 already name the Mac as a standalone, local-only host with Cycles
on Metal, qualification open.

1. **Install Blender 5.2.x on an Apple silicon Mac and let an agent run both qualifications**
   (Recommended): then C-12 drops "qualification open", R-12 its `planned:` marker, and the
   macOS CI lane becomes required.
2. **Defer**: mark R-12 `from: ga` and keep the macOS CI lane advisory.

**If no answer**: R-12 stays planned and the lane advisory.

### Hosted Blender lane and OptiX coverage

*2026-10-22 · agent · open*

Hosted runners have no GPU (C-09), so the real-Blender tests that need a GPU backend carry
`OptixQualification` and run by hand ([operations](architecture/operations.md), Hardware
qualification). The hosted Blender lane (EEVEE on Mesa's software EGL drivers) first ran green
on a GitHub-hosted runner on 2026-10-22, in a pull-request run, once the worker prewarm stopped
rendering 64 EEVEE samples per spawn. `ci.yml` still marks it advisory (`continue-on-error`), so
a red result there does not block a merge.

1. **Manual OptiX qualification, advisory hosted lane** (Recommended): run the OptiX
   qualification before a release that touches the GPU path; the Blender lane has run green on
   a hosted runner, and after it stays green on main the maintainer decides whether to
   drop `continue-on-error` and make it a required check.
2. **Hardware CI**: a GPU runner, which C-09 rules out for a public repository today.

**If no answer**: option 1.

### Receiver qualification against AGD's public release

*2026-10-22 · agent · open*

[AgentGraphicDesign](https://github.com/pevyhlas/agentgraphicdesign) (AGD), the receiver for delivered stills, is a separate tool. The
README, [still delivery](../docs/still-delivery.md) and the [agent guide](../docs/agent-guide.md)
link it and keep the `agd` steps optional; the receiver qualification pins the AGD 0.3.0 package
hashes AB was qualified against.

1. **Requalify on AGD's next public release** (Recommended): pin that release's CLI and MCP
   package hashes in the still contract and the test, rerun the receiver qualification and
   record the result in observed.
2. **Keep 0.3.0**: leave the pin and qualify only when the provided-artifact contract changes.

**If no answer**: option 1.

### Public baseline security and packaging findings

*2026-10-23 · agent · folded: 011-public-hardening*

Independent review of the public baseline found three pre-existing issues; the documentation/
CI maintenance does not fix or waive them. No credentials or accidental private identifiers
were identified by that bounded text review.

- **High, C-03**: `LauncherOptions.ResolveBind` rejects wildcard binds but accepts a public
  address. Enforce the approved private/loopback ranges, including resolved hostnames and
  mapped addresses, before every listener starts; add rejection tests.
- **Medium, C-04**: desktop Blender children inherit unrelated credentials from the launcher
  environment. Apply a minimal environment and credential/token/Python-variable canary tests.
- **Medium, release hygiene**: recursive worker-content globs can package ignored diagnostic
  files. Use tracked-only release inputs and restricted content, with an ignored-file canary.

1. **A separate bounded hardening change** (Recommended): address these findings and obtain
   security, licence and package verification before a packaged release or desktop rollout.
2. **Keep source-only preview**: document the outstanding findings; do not approve a packaged
   release or desktop rollout until the same boundaries are enforced and re-reviewed.

**If no answer**: leave release/rollout blocked; do not widen this documentation/CI change or
change the network/security policy. Baseline and maintenance reviews stay separate.

## Your direction

### Continue after CI setup

*2026-10-25 · owner · folded: 015-log-safety*

> and then continue with next steps

Continues the independently designed log-safety increment after hosted macOS CI merged.
Capability acceptance, exact-head review/CI, release and rollout gates still apply.

### GitHub macOS CI

*2026-10-25 · owner · folded: 014-macos-ci*

> go ahead - we should setup github CI for mac os

Updates hosted macOS CI to macOS 26 arm64 with retained diagnostics. Host and Metal
qualification remain open under changes 008 and 009.

### Public projects, fixes and logos

*2026-10-24 · owner · folded: 011-public-hardening*

> so both repos are now public and the app should have access to it to merge PRs - I have added both - so lets work on the public repos now - also here is what we need to do - 1. make sure we have very succint and to the point readme for both - also that public product kit is adopted - see product kit readme for inspiration it is quite to the point (not that much but not bad) - next we should identify any issues in both AGD and AB and then fix them, next proceed with roadmap - next we want to generate logos for AGD and AB via their respective workflows - please proceed with all of that

Answers the baseline-hardening scope; roadmap and original artwork follow the existing gates.

### Delivery lanes beyond the first

*owner · deferred: owner input for a named consumer*

Paraphrased: consumer delivery lanes beyond the first still need the owner's input per consumer
before they are built. Answers what follows the single-still lane (R-08).

### Desktop rollout

*owner · deferred: live desktop evidence*

Paraphrased: live desktop evidence (a `tier=desktop` render) and an always-on launcher service
with a GPU-yield policy are deferred. Answers when the desktop tier gets live evidence (R-07):
renders were observed once historically, and the current launcher is not re-verified.

## Deferred and declined

None.
