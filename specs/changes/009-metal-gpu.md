# Change: Metal GPU rendering

**Opened**: 2026-10-20 · **Parts**: P1, P3, P5 · **Journeys**: J-05, J-04 · **Tier**: T2 · **Parent**: none

<!-- Continues 0.x feature 009-metal-gpu, kept open at the Product-Kit 1.1 restructure. Its
feature folder is summarized in specs/archive.md; details are in the private history. -->

## Intent

Cycles renders on an Apple silicon Mac's GPU through Metal, and every surface reports honestly
which device served and when no GPU exists (R-12, R-13, D-17). Built and merged: worker-side
backend detection and per-render device selection with a check (P3), `WorkerHandle.Gpu` and the
`GpuGuard` host contract with `device` provenance (P1.B-21 to P1.B-23, P1.I-07), the desktop
bringup rule (P5.B-05), `daemon_status.gpu`, doctor `gpu.backend` and the opt-in `gpu.render`,
agent-facing text, and the `MetalQualification` harness with a test-only PNG decoder. The OptiX
device evidence held ([observed](../observed.md)). What remains is owner set-up and the
qualification on a real Mac. Inbox: "macOS qualification".

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L0 | R-12 | remove `planned: 009-metal-gpu` | when T033 passes |
| L1 | observed | 5.2.x Metal numbers replace the 5.0.1 probe | T034 |

## Bounds

- C-02: holds: detection stays in `worker/`; the host never probes the GPU.
- C-04: holds: the record shows device names redacted to `Apple M<n>`, no environment dump, no
  token content; the environment-scrub proof lists variable names only.
- C-12: holds: the Mac stays standalone; the desktop tier stays unsupported there.
- Others in scope: hold.

## Approach

Decided in the design (D-17). Qualification gates, evidence and not product guarantees, on the
qualification scenes (denoise off, fixed seed and samples): Metal against CPU PSNR at least
40 dB with at least 99.9% of channel values within 4/255; Metal against Metal in one process at
least 50 dB; a second worker's first `cycles_gpu` render at 64 px under 10 s (no recompile under
the environment allowlist); the first compile under the 600 s render timeout; a replay with
`cycles_gpu` renders between its ops keeps the expected digest.

## Tasks

- [ ] T032 (owner) Provide an Apple silicon Mac with Blender 5.2.x (5.2.1 reference) for
  qualification. Inbox: "macOS
  qualification".
- [ ] T033 Qualification on an Apple silicon Mac, run by an agent through its harness with the worker
  environment allowlist: first the cold-cache first-compile case alone (state the cache
  state), then all of `Category=MetalQualification`. Confirm first that the log windows capture Cycles'
  `Path tracing on:` line. Record Blender, macOS and chip identity (redacted) with the hello
  payload; the Cycles log line; first and second process times with the cache state and
  `firstCompileSuspected` (a warm-cache time is never a compile time); parity numbers; the
  device snapshots for success and failure; a `delivery_export --engine cycles_gpu` that
  verifies; `abl doctor --check gpu.render --json`, `daemon_status` and the sandbox
  observation. The record is evidence, kept out of `specs/` (constitution, Evidence).
- [ ] T034 Close-out: fold the qualified truth into the spec (R-12, observed facts, D-17); note
  any missed threshold and its investigation; record or drop the information-only denoised
  Metal and CPU pair.

## Checks

The `MetalQualification` category fails off an Apple silicon Mac by design and no CI lane runs
it; the unit and Blender lanes keep the contract tests. Since CI moved to GitHub-hosted runners
(C-09), the OptiX real-Blender cases are a manual qualification as well (`OptixQualification`,
on a Linux host with an NVIDIA OptiX GPU; [operations](../architecture/operations.md)); the
host guard's contract stays covered by the unit lane's stub workers.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | R-12, R-13 | `MetalQualificationTests` on an Apple silicon Mac with Blender 5.2.x (T033) | 0 | NOT_RUN |
| A2 | D-17 | the qualification record folded into the spec (T034) | 0 | NOT_RUN |

## State

**Step**: verify
**Waiting on**: the owner for T032
**Next**: T033 once an Apple silicon Mac has Blender 5.2.x
