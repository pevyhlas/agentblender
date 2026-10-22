# One-still producer handoff

The first profile exports one procedural still (zero/one parameter-overlay row).
Multiple rows reject instead of being mislabeled as animation or silently truncated.
Existing batch rendering remains available for studies; a family delivery contract is
separate work. This source capability does not assert a live daemon upgrade.

## Public journey and layout

Freeze the chosen source through ordinary CLI/MCP authoring, then:

```sh
abl delivery export shared-still --out artwork --engine cycles_cpu --res 64 --samples 16
abl delivery verify artwork/shared-still-GENERATION
abl delivery list artwork/shared-still-GENERATION
# Optional: the AgentGraphicDesign receiver's own checks (AGD is a separate tool)
agd assets preflight artwork/shared-still-GENERATION/payload/artifact.json -f json
agd assets verify artwork/shared-still-GENERATION/payload/artifact.json -f json
```

Use returned paths; GENERATION is a fresh invocation identity, not "latest selected".
[AgentGraphicDesign](https://github.com/pevyhlas/agentgraphicdesign) (AGD) is a separate companion tool, so the two `agd` steps
are optional: `abl delivery verify` checks a delivered generation on its own, and the
layout and limits below describe the payload without AGD.
MCP `delivery_export(artifact,out,engine,res,samples,paramTable?)` returns paths and
one contact-sheet preview. `delivery_verify(payload=GENERATION)` / list use the
completed generation directory despite the historical argument name. CLI verify/list
run Core directly and do not require a daemon or Blender; export still requires both.
Standalone checks retain the shared public-op telemetry seam without a network exporter
or daemon requirement. Defaults are terse; use `--json` for structured CLI output.

```text
shared-still-GENERATION/
  payload/
    artifact.json       # exact existing AGD provided-artifact v1, one untimed still
    still.png           # normalized bounded RGBA8/sRGB container
  delivery.json         # AB producer receipt formatVersion 2, not AGD metadata
  recipe-snapshot.json  # full frozen recipe, overlay, effective profile settings/identities
  replay-source/shared-still/
    artifact.json
    base.blend
  contact-sheet.png
```

The receiver descriptor declares an untrimmed canvas, zero crop, top-left pixel-edge
coordinates and explicit central pivot (width/2,height/2). Rich producer fields never
enter the closed AGD payload. The AB receipt format is v2; any other receipt version is
rejected, never migrated. The AGD schema and profile are those of the AGD 0.3.0 receiver that AB was qualified
against ([receiver qualification](#receiver-qualification)).

## Interpretation, identities and limits

The worker forces PNG/RGBA8, sRGB display/Standard view/None look, exposure 0/gamma 1,
no curves/dither/border/crop, square pixels and transparent film, with all temporary
overrides restored. Setup errors cannot continue as profile success. Host checks
bounded chunk lengths/CRCs/order/methods/interlace; normalization strips ancillary
container metadata, inserts sRGB intent 0 and retains IDAT without pixel conversion.
Legal source sRGB intents 0..3 are accepted for that normalization. IDAT remains opaque
to producer byte verification: independent AGD decoding is the interoperability gate.

Receipt records build identity, exact host-assembly and worker-source byte observations,
Blender version/build, encoded PNG/descriptor hashes, source checkpoint, recipe snapshot
and effective settings. These observations are not a signed installation manifest,
dependency closure, provenance approval or rights certificate. Precommit build version
strings may identify the base commit; the qualification of a specific commit is recorded
with the change that ran it (CI or the pull request), not in this file. Preserve PNGs: R3
replay equality is explicitly bounded (objects, transforms/mesh/basic camera/light facts), not
full shader/modifier/render equivalence or guaranteed repeated pixel bytes.

Single image: res 1..4096, samples 1..4096, 64 KiB parameter row, 64 MiB encoded PNG,
4096 chunks. Receipt 1 MiB, snapshot 8 MiB, preserved base 256 MiB; reads reject links,
missing/empty/oversize/extra payload files and capture bounded bytes before checks.
The local trust model requires paused writers to the source/output tree; path checks
are not hostile-replacement-safe confinement. Ordinary filesystem/cancellation failure
leaves prior completed generations intact. A stage is private/incomplete until same-
parent directory publication; no selection reference is automatically advanced.

`delivery_verify` reports pixelsDecoded=false, admitted=false, provenanceVerified=false
and sourceReplayVerified=false, even on pass. Where AGD is available, run its preflight/verify,
and a separate replay, for the proofs they provide. Generic script external I/O cannot be qualified by
inspecting Blender data alone; the shared acceptance recipe is procedural. Native image/
layer import and stronger artifact admission remain AGD's later slices.

## Receiver qualification

AB was qualified against one [AgentGraphicDesign](https://github.com/pevyhlas/agentgraphicdesign) receiver build, AGD 0.3.0; this
section records what the qualification ran against.
The real-Blender test `PublicStillOutputPassesPinnedCliAndMcpReceiverAndIndependentSamples`
checks the CLI and MCP 0.3.0 package SHA-256 values plus each installed payload file against
its archive, and launches the verified DLLs with the trusted .NET runtime:

- receiver: the AgentGraphicDesign 0.3.0 release build, whose `--version` reports
  `0.3.0+<source revision>`;
- CLI package SHA-256 `b029f9338809a7a720690a3ac79d4f220556b5ba66ab0eec72d7201cbcb88ed6`;
- MCP package SHA-256 `9e1fa0d23caefcdcc354d68dd7651ac49a8d349a7d631fd264f7384521b8668a`.

These AGD 0.3.0 packages come from a release made before AGD's public repository; they cannot
be downloaded publicly, so this qualification is a historical record that outside readers cannot
reproduce. A public AGD release needs a new qualification
([inbox](../specs/inbox.md#receiver-qualification-against-agds-public-release)). On a host that
had those packages, `AGD_CLI`/`AGD_MCP` were set to the tool-install shims (standard `.store`
layout); the test bypasses the shim after archive/payload verification.
Without them the test skips rather than asserting adoption, so CI and contributors without AGD
are unaffected. The qualification records actual samples and both receiver protocols; its
result is in [observed](../specs/observed.md). Qualifying a later AGD release means pinning
its package hashes here and in the test, then rerunning it.
Keep full generations/source/payloads in project-managed durable files/Git/LFS as needed;
neither Git nor hidden selection storage is operated by these commands.
