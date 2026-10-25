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

- [ ] Update the lane and operational description.
- [ ] Validate locally, publish a PR, inspect native tests and download the artifact.
- [ ] Independent code/test and architecture/operations review; exact-head gates; merge.

## Checks

No L0/L2 change. Run CI policy and its regression suite, Product-Kit adoption/doctor, public
hygiene and whitespace; then the hosted lane. eng-code reviews code/test evidence, eng-arch
reviews operations per the registry. Security, license and ergonomics lenses are not triggered:
no product boundary, dependency, asset, protocol or agent-facing surface changes.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | C-09, L1 CI | CI policy/self-tests, adoption, doctor, public hygiene and whitespace | 1 | PASS: Linux local checks, `.prdkit/cache/evidence/014-macos-ci/local-checks.log`; hosted verification pending |
| A2 | C-12, Q-04, L1 CI | PR run asserts macOS 26 arm64, builds and completes unit lane; downloadable profile/log/TRX artifact and summary agree with actual outcomes | 0 | NOT_RUN |
| A3 | Delivery policy | exact-head required checks and independent reviews pass | 0 | NOT_RUN |

## State

**Step**: implement
**Waiting on**: nothing
**Authority**: owner request “go ahead - we should setup github CI for mac os”; constitution
permits feature commit/push/PR and exact-head reviewed green merge. Release/live rollout remain
owner-gated.
**Branch**: 014-macos-ci; base d5b9ea260f60eb45dd7b57fd29df3b7a485d5c41.
**Attempts**: acceptance 0; review fix rounds 0. Branch-protection API read returned 403 with
app credentials; workflow delivery does not require administration access.
**Next**: publish the CI checkpoint and inspect hosted macOS results before final review.
