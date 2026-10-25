# Change: Exercise macOS 26 arm64 and retain macOS CI results

**Opened**: 2026-10-25 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: 008-agent-operability

## Intent

The owner requested GitHub macOS CI while continuing the roadmap. Upgrade the existing advisory
PR lane to standard hosted macOS 26 arm64 and retain its results so future log-path work has
inspectable native coverage. The macOS/Metal qualification inbox item stays open: this unit lane
does not perform the real-Blender and hardware qualifications.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L1 | operations | macOS 26 arm64 lane with profile, console and TRX artifacts | advisory policy remains |

## Bounds

- C-04, C-09: read-only token, no secrets, existing SHA-pinned actions, standard hosted runner;
  only fixture-based CI outputs are uploaded.
- C-12: unit CI is advisory, not macOS host or Metal qualification.
- Q-04: retain process-session isolation and hang/step/job bounds; use physical temporary paths.
- C-01, C-02, N-06: no new dependency or Blender acquisition in this lane.
- Others in scope: hold.

## Approach

Use `macos-26`, an explicitly versioned standard arm64 image, with actual OS/architecture
assertions and image/SDK identity in a log. Preserve the current unit filter and `setsid`
isolation. Add TRX and always-uploaded diagnostics, with the real test outcome in the summary.
Keep the existing advisory exit condition in the constitution/inbox.

## Tasks

- [x] Update the lane and operational description.
- [x] Validate locally, publish PR #10, inspect native tests and download the artifact.
- [x] Independent code/test and architecture/operations review; required source-head gates.
- [ ] Close and merge after final-head gates.

## Checks

No L0/L2 change. Run CI policy and its regression suite, Product-Kit adoption/doctor, public
hygiene and whitespace; then the hosted lane. eng-code reviews code/test evidence, eng-arch
reviews operations per the registry. Security, license and ergonomics lenses are not triggered:
no product boundary, dependency, asset, protocol or agent-facing surface changes.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | C-09, L1 CI | CI policy/self-tests, adoption, doctor, public hygiene and whitespace | 1 | PASS: local log and hosted Validate on `e6d1ff5`, [run](https://github.com/pevyhlas/agentblender/actions/runs/40219612662) |
| A2 | C-12, Q-04, L1 CI | native macOS PR build/unit run with downloaded diagnostics and truthful summary | 1 | PASS: macOS 26.6.2 arm64/SDK 10.0.113, 843/843 unit passes, zero skipped or failed; [independent evidence](https://github.com/pevyhlas/agentblender/pull/10#issuecomment-6401297110) |
| A3 | Delivery policy | required checks and independent reviews pass on exact source head | 1 | PASS: Validate and Build & unit tests green on `e6d1ff5`, two independent PASS verdicts below; final metadata-only head gated again before merge |

## State

**Step**: close
**Waiting on**: nothing
**Authority**: owner request “go ahead - we should setup github CI for mac os”; constitution
permits feature commit/push/PR and exact-head reviewed green merge. Release/live rollout remain
owner-gated.
**Branch / PR**: 014-macos-ci; base d5b9ea260f60eb45dd7b57fd29df3b7a485d5c41; [PR #10](https://github.com/pevyhlas/agentblender/pull/10).
**Attempts**: A1/A2/A3 one each; review fix rounds 0. Branch-protection API read returned 403 with
app credentials; workflow delivery does not require administration access.
**Next**: commit review record, close, publish and enforce final-head gates before merge.
**Review**: round 1 · d5b9ea260f60eb45dd7b57fd29df3b7a485d5c41..e6d1ff59a200ae0f6423cf23a81d5af54b9f6f24 · eng-code · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/10#issuecomment-6401263766
**Review**: round 1 · d5b9ea260f60eb45dd7b57fd29df3b7a485d5c41..e6d1ff59a200ae0f6423cf23a81d5af54b9f6f24 · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/10#issuecomment-6401263959
**Evidence addendum**: eng-code independently established A2 on the same range without re-reviewing unchanged source: https://github.com/pevyhlas/agentblender/pull/10#issuecomment-6401297110
