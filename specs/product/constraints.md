# AgentBlender — constraints

<!-- C- items: what must hold. Who may act on a limit is the constitution's Action policy. Past
the budget, split into constraints/<area>.md and keep one line and a link per area here. -->

- **C-01 MIT licence and inbound code**: the repo is MIT; verbatim code only from MIT sources
  with notices in `THIRD-PARTY-NOTICES.md`; Apache-2.0 and copyleft sources are ideas only;
  dependencies are MIT-compatible.
  - Why: one clean outbound licence.
  - Scope: all.
  - Source: owner decision (D-10).
  - Revisit when: never.
  - Exception: the owner.
- **C-02 GPL boundary**: only `worker/` code runs inside or imports Blender; the host never
  links Blender; Blender is installed by the user and never redistributed. CI enforces it.
  - Why: keeps the MIT host clean of GPL obligations.
  - Scope: all.
  - Source: owner decision (D-10), constitution Principle 9.
  - Revisit when: never.
  - Exception: none.
- **C-03 Network boundary**: the daemon binds loopback only and serves only loopback `Host`
  names and its own origin; the desktop launcher binds one specific private-network address
  behind a token; nothing is publicly exposed.
  - Why: `scene_exec` runs arbitrary code by design.
  - Scope: P1, P5 (every listener).
  - Source: ratified constitution; launcher design (D-15).
  - Revisit when: endpoint authentication exists.
  - Exception: the owner, with a security review.
- **C-04 No secrets, no private details**: no secrets in the repo, logs, sidecars or telemetry;
  no machine names, private-network details or consumer paths in the tracked tree (targets and
  endpoints are configuration).
  - Why: a public repository.
  - Scope: all.
  - Source: ratified constitution; release preparation (confirmation open in the inbox).
  - Revisit when: never.
  - Exception: none.
- **C-05 Telemetry off by default, no content capture**: telemetry is opt-in and never carries
  prompts, scripts, scene data, pixels or paths.
  - Why: user trust; prior art captured by default.
  - Scope: P1 (telemetry); all journeys.
  - Source: constitution Principle 7; the content rule from release preparation (confirmation
    open in the inbox).
  - Revisit when: never weakened silently.
  - Exception: the owner.
- **C-06 Asset provenance**: procedural first; external inputs CC0 only, provenance recorded.
  - Why: licence-clean outputs.
  - Scope: all.
  - Source: ratified constitution.
  - Revisit when: a non-CC0 source is proposed.
  - Exception: the owner.
- **C-07 AGD boundary**: no 2D authoring, lint or export here; AgentBlender feeds
  AgentGraphicDesign (AGD).
  - Why: no duplicate products.
  - Scope: all.
  - Source: constitution Principle 8 (D-09).
  - Revisit when: an ambiguous capability appears (resolve it against this first).
  - Exception: the owner.
- **C-08 Host runtime**: the host is .NET; Python exists only inside Blender workers.
  - Why: the reasons in D-01 (productivity, one package, a structural GPL wall).
  - Scope: P1, P4, P5.
  - Source: owner decision after an all-Python evaluation (D-01).
  - Revisit when: evidence shows the .NET host or protocol materially slows the work.
  - Exception: the owner.
- **C-09 Public CI**: CI runs only on pinned GitHub-hosted images with a read-only token, PR CI
  needs no secrets, no workflow uses `pull_request_target`, and actions are pinned by commit
  SHA; `scripts/check-ci-policy.sh` enforces it.
  - Why: a public repository runs fork pull requests; fresh hosted runners keep that code away
    from any persistent machine or credential.
  - Consequence: hosted runners have no GPU, so the real-Blender tests that need a GPU backend
    are manual hardware qualifications: `MetalQualification` on an Apple silicon Mac and
    `OptixQualification` on a Linux host with an NVIDIA OptiX GPU. CI runs every other
    real-Blender test, with `cycles_gpu` refused as `gpu_unavailable` there.
  - Scope: all (`.github/`).
  - Source: release preparation (confirmation open in the inbox).
  - Revisit when: hardware CI is needed.
  - Exception: the owner.
- **C-10 Trademark posture**: the name stays `agentblender` as nominative use; never the
  Blender logo; the README carries the non-affiliation line.
  - Why: the Blender Foundation trademark policy.
  - Scope: all (name, docs, branding).
  - Source: owner decision.
  - Revisit when: the Foundation's policy changes.
  - Exception: the owner.
- **C-11 No runtime coupling for consumers**: consumers receive committed artifacts with
  provenance and never depend on AgentBlender at runtime; no hand-copied outputs.
  - Why: reproducible, auditable delivery.
  - Scope: P1 (delivery), P4; J-03, J-04.
  - Source: ratified constitution Principle 10 (D-12).
  - Revisit when: never.
  - Exception: the owner.
- **C-12 Supported hosts**: the daemon runs on Linux x64 (qualified, required CI lanes) and on
  an Apple silicon Mac as a standalone, local-only host (built, qualification open); the
  desktop GPU worker runs on Windows or Linux over TCP and is unsupported from a macOS host;
  no Windows daemon host.
  - Why: only Linux is qualified end to end; the Mac joins once its qualification passes.
  - Scope: P1, P3, P5; J-05, J-06.
  - Source: ratified tech stack; owner decision on macOS hosts.
  - Revisit when: the macOS qualification in changes 008-agent-operability and 009-metal-gpu
    passes.
  - Exception: the owner.
