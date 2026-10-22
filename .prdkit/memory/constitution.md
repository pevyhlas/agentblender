# AgentBlender constitution

- **Name**: AgentBlender (`agentblender`)
- **Intent owner**: the project maintainer (product purpose, scope, strategic trade-offs,
  spending, live infrastructure, releases, final acceptance)
- **Rigor**: light
- **Stage**: see `specs/product/lifecycle.md`
- **Version**: 2.0.3 · **Ratified**: 2026-09-11 · **Last amended**: 2026-10-22

`Rigor: full` also makes medium review findings block and requires independent review of every
substantive change; `light` follows the tiers in `.prdkit/workflows/protocol.md`.

Product direction lives in `specs/product.md` and `specs/product/`; the lifecycle stage in
`specs/product/lifecycle.md` (deferred hardening is an inbox question). Pending the
maintainer's confirmation in `specs/inbox.md`.

## Action policy

- **Commit**: agents commit freely on non-default branches, inside a change.
- **Branch push and pull request**: agents may push non-default branches and open pull requests
  (maintainer decision).
- **Merge**: an agent may merge a pull request once the exact head's required CI is green and
  `check-review.sh --head <sha>` passes for its change (maintainer decision). Never
  push `main` directly.
- **Release and tag**: only with an explicit maintainer go; whether an earlier authorization
  for the first (then private) packaged release covers a public release is an open inbox item.
  A release or fresh-history publish is built from the tracked tree only (`git archive HEAD` or
  the `git ls-files` list), never from a copy of the working directory, whose ignored outputs
  (`bin/`, `obj/`, `var/`, `out/`, `TestResults/`, logs) carry absolute local paths; if a copy is
  unavoidable, run `git clean -fdX` first. Product-Kit is vendored in `.prdkit/`, so the packaged
  tree is complete without submodules; run `check-adoption.sh` and `doctor.sh` on the packaged
  tree before pushing it. The fresh commit is a single parentless root commit with a newly
  written message that carries no agent-session trailer; no ref whose ancestry includes
  pre-public commits is pushed. Its metadata stays neutral too: set the GitHub noreply
  identity for author and committer (`GIT_AUTHOR_NAME`/`EMAIL` and `GIT_COMMITTER_NAME`/`EMAIL`)
  and a UTC date (`TZ=UTC`, or `GIT_AUTHOR_DATE`/`GIT_COMMITTER_DATE` with `+0000`), so no local
  name, email or timezone offset is recorded. The published tree stays generic (C-04).
- **Deploy and live infrastructure**: maintainer only. This covers any running deployment the
  maintainer operates, the desktop launcher rollout and other repositories' mainlines.
- **Spending**: none authorized; maintainer only.
- **Dependencies**: a new dependency needs the licence check in C-01 and a clean
  `dotnet list package --vulnerable --include-transitive`; licence questions the text cannot
  settle go to the maintainer.
- **Legal and irreversible**: any legal question (licence, trademark, terms) and any
  irreversible state change (deleting durable data or history, publishing, changing another
  repository) goes to the maintainer.
- **Continuation**: when an approved plan or change already names the next step, a completed,
  merged step authorizes it without a fresh confirmation; stop only at a genuine blocker or a
  decision reserved for the maintainer.

## Commands

```text
# repo root
dotnet restore agentblender.slnx --locked-mode
dotnet build agentblender.slnx -warnaserror
# unit lane; never pipe dotnet test output: redirect to a file. MetalQualification (an Apple
# silicon Mac) and OptixQualification (a Linux host with an NVIDIA OptiX GPU) run only by hand
# on their hardware; they fail by design anywhere else
MSBUILDDISABLENODEREUSE=1 dotnet test agentblender.slnx --no-build --filter "Category!=BlenderIntegration&Category!=MetalQualification&Category!=OptixQualification" > /tmp/testrun.log 2>&1
# integration lane; needs Blender 5.2.1 via ABL_BLENDER or PATH
MSBUILDDISABLENODEREUSE=1 dotnet test agentblender.slnx --no-build --filter "Category=BlenderIntegration&Category!=OptixQualification" > /tmp/itestrun.log 2>&1
python3 -m py_compile worker/*.py && python3 tests/test_blob_transfers.py
python3 tests/test_cycles_devices.py
bash scripts/test-claude-with-telemetry.sh
# CI policy (C-09): hosted runners, SHA-pinned actions, read-only, no pull_request_target
bash scripts/check-ci-policy.sh && bash scripts/test-check-ci-policy.sh
# Product-Kit gates
bash .prdkit/scripts/bash/check-adoption.sh
bash .prdkit/scripts/bash/doctor.sh
bash .prdkit/scripts/bash/status.sh
```

On macOS set `TMPDIR` to a directory without a symlinked parent (for example
`/private/tmp/ab-tmp/`); the default temp path trips the delivery symlink guard.

## Review and checks

- **Tiers**: T0 to T3 per `.prdkit/workflows/protocol.md`; critical and high findings block,
  medium is advisory (light rigor). An absent reviewer or a failed tool run is not a pass.
- **Named risk areas** always get their independent reviewer, whatever the tier, and are never
  waived:
  - network bind, Host/Origin guard, code-exec path, launcher token, secrets: `eng-security`;
  - worker protocol, checkpoint store and replay gate, tier and session state machines, public
    op contracts, data or storage format changes: `eng-arch`;
  - dependencies, inbound code, bundled assets, the `worker/` GPL boundary: `eng-license`;
  - agent-facing surface (outputs, previews, errors, MCP and CLI parity): `eng-ergonomics`;
  - code and tests for any behavior change: `eng-code`.
  Roles and tiers are in `.prdkit/memory/agents.md`.
- **Design first**: a general, non-trivial feature gets a change record with its design and an
  independent design review before implementation; every question to the maintainer carries
  options, trade-offs and a recommendation (maintainer direction).
- **Before merge**: CI green on the exact head (validate, build with `-warnaserror`, unit tests;
  the real-Blender integration lane and the macOS lane are advisory until their exit conditions,
  see `specs/inbox.md`), no secrets in the diff, and `check-review.sh` passing for a change
  branch. Docs-only T0 changes need no reviewer.
- **Evidence** is proportional to the change and lives in CI, the PR or
  `.prdkit/cache/evidence/`, never under `specs/`. Merged, live and validated are distinct
  claims; live desktop and deployment checks are maintainer-run.
- **Exceptions**: a waiver is a `WAIVER` line in `specs/records.md`, recorded only when the
  maintainer decided it.

## Principles

Product constraints these principles rest on are the C- constraints in
`specs/product/constraints.md`.

1. **Agent-first, CLI/MCP parity**: every capability works end to end for a non-interactive
   agent and exists on MCP and CLI as thin layers over one core; Blender's GUI is an inspection
   affordance, never a required step (R-01, P1.B-01).
2. **Context economy is a design constraint**: delta state reads, 512 px-class default
   previews, one contact sheet per batch, final renders on disk and never in model context
   (Q-02, D-07).
3. **No cold starts in the loop**: interactive ops run on warm, persistent headless workers;
   latency budgets live in `specs/product/qualities.md` (Q-01) and are checked against
   telemetry.
4. **Checkpoint tree, deterministic replay**: durable state is an immutable content-addressed
   checkpoint tree plus journals; branching loads a checkpoint into a fresh head, never undo;
   frozen trajectories reproduce the same canonical scene digest (R-03, P4.I-01).
5. **Per-agent session isolation**: one daemon, one worker and head per session; a crashed or
   hung worker never takes down the daemon or another session (R-06, Q-04).
6. **Hardware-aware tiering**: iteration runs on the always-on host; heavy finals and batches
   may use the optional desktop GPU; its absence never blocks iteration or silently loses a
   job (R-07). Automatic versus caller-chosen fallback: open inbox item.
7. **Instrumented feedback loop**: every op emits OpenTelemetry signals (op, outcome, tier,
   duration, sizes, with session and consumer-project attribution), off by default; surface
   changes cite usage evidence (R-10, D-08). Where attribution lives: open inbox item.
8. **Complement AgentGraphicDesign, never duplicate**: 2D belongs to AGD; this project owns 3D
   and feeds AGD (C-07).
9. **Licence hygiene and asset provenance**: MIT inbound and dependency rules, `bpy` only under
   `worker/`, Blender never redistributed, CC0-only external assets with provenance (C-01, C-02,
   C-06).
10. **Delivery with provenance**: assets reach consumers only through export pipelines with a
    provenance record; consumers never depend on this repo at runtime (C-11, R-08).

Non-negotiable regardless of lifecycle: the network boundary (C-03), no secrets (C-04),
deterministic replay (Principle 4), the GPL boundary (C-02) and asset provenance (C-06).
