# Change: Unify filesystem ownership and path trust for agent setup and daemon lifecycle

**Opened**: 2026-10-25 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: 008-agent-operability

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

Deliver the shared-facts increment of 008/T021 so setup and lifecycle operations judge paths from one read-only ownership
inspection instead of probing ownership by changing mode bits. The owner requested continued
capabilities on public history. macOS qualification and public release questions stay open.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L2 | P1.B-10, P1.B-11, P1.B-13 | Shared uid/mode/type/ACL facts for config, tokens and discovery | T021 |
| L2 | P1 operability | Shared state/runtime path and store/log-reader trust checks | T021 first increment |

## Bounds

<!-- From `.prdkit/scripts/bash/bounds.sh <id>`: each constraint, non-goal or quality this change
could break, with how it holds; then "Others in scope: hold". A conflict goes to the owner inbox
with both sides, never resolved here. -->

- C-01, C-02: holds: existing .NET/libc primitives only; no new dependency or worker boundary.
- C-03, C-04: holds: no network change and no token content in diagnostics or evidence.
- C-12: holds: simulated ACL cases and advisory macOS CI do not claim hardware qualification.
- Q-04: holds: refused paths are not opened for writes or chmodded to prove ownership.
- Others in scope: hold.

## Approach

Design reviewed by eng-security before implementation; shared facts and private-directory
choices approved. The log writer is sequenced as its own follow-up because the review found
rotation privacy and ancestor-replacement issues requiring a separately verified open/rotation
transaction. This increment does not close all of T021.

- Extend `PathFacts` with owner uid, effective uid, regular-file kind and macOS allow-ACL facts;
  derive `OwnedByCurrentUser`. Keep the existing config filesystem seam, including tests with
  fake foreign owners. Retire the chmod ownership proof. The existing statx/stat readers supply
  metadata; inspection failures fail closed.
- Route discovery (including its trust-anchor walk), delivery's system-alias predicate, store
  gates and daemon log checks through these same facts. Preserve each caller's distinct rules:
  discovery admits user/root; system aliases require root; private files require this user.
- Refuse macOS allow ACLs for config/token/runtime/state/store/log-reader trust as well as
  discovery, since mode bits alone cannot establish those guarantees. Deny-only ACLs remain
  permitted. 008 explicitly assigns this remaining choice to the security review.
- Derive the runtime fallback from `DaemonPaths.StateDir`. Apply the same final-component
  rule for the state directory as runtime: real user-owned directory, 0700, no allow ACL.
  Parent aliases remain supported. Store symlink-target policy otherwise stays as today.
- Route the existing log-reader predicate through shared facts. The follow-up must gate every
  selected tail path and protect the write/rotation transaction: private existing files,
  no-follow/exclusive open, opened-object verification and an anchored directory or trusted
  ancestor chain. Preserve the two medium design findings in the parent rather than claim
  that shared metadata alone fixes them.

## Tasks

- [x] Independent security/design review resolves ACL and state-directory choices.
- [x] Shared read-only facts and caller migration; remove duplicate state-path construction.
- [x] Refusal/ownership/ACL and real-filesystem regression tests, including read-only inspection proofs.
- [ ] Required CI, independent code/security/architecture/ergonomics reviews, publish and merge.

## Checks

Run locked restore, warning-free build and unit lane; focused tests cover trust decisions,
discovery's root/user distinction, configuration reads, runtime/state preparation, system-alias
classification and the existing log-reader predicate. No Blender rendering is needed for these checks.
Required implementation concerns: eng-code (including test evidence), eng-security,
eng-arch (lifecycle/storage seam), eng-ergonomics (new refusal diagnostics). License reviewer
not applicable: no dependency, inbound code, asset or worker change.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P1.B-10, P1.B-11, P1.B-13 | Shared ownership/ACL facts; fake and native filesystem trust regressions | 1 | PASS on Linux x64 in `unit-tests-isolated.log`; macOS ACL policy simulated |
| A2 | P1 operability | Runtime/state path parity; private state and store gates; shared log-reader facts | 1 | PASS in `unit-tests-isolated.log` |
| A3 | Q-04, C-12 | Unit lane, policy gates, exact-head hosted CI and independent reviews | 2 | Local PASS: 833 tests, one macOS-only skip; hosted CI/reviews pending |

## State

**Step**: implementation
**Waiting on**: nothing for shared-facts increment
**Next**: independent implementation review and exact-head CI; log transaction follows separately.
**Branch**: `013-path-trust`, from public main `d83c020ffb9d3724f5c647eb27dde4d28bc8bbd5`; PR pending.
**Authority**: owner “please continue on next capabilities” (2026-10-25); constitution authorizes
feature commits, branch push/PR and exact-head green/reviewed merge. No release or deployment.
**Attempts**: first unit-lane run failed 34 setup/doctor/discovery cases under the inherited long
temporary path and group-writable umask (runtime fallback encountered an existing 0775 directory).
Second unit-lane run passed with a short private test root and umask 022; existing runtime data
was not modified. Two distinct test-authoring compile mistakes were corrected and their failed
logs retained (`build.log`, `build-final.log`); complete build is `build-complete.log` with zero
warnings/errors. Design review PASS, 0 critical/high and 2 medium advisories assigned to the
log transaction increment. No implementation review yet, no holds on shared facts.
**Evidence**: `.prdkit/cache/evidence/013-path-trust/`.
**Design review**: eng-security PASS on public base `d83c020ffb9d3724f5c647eb27dde4d28bc8bbd5`
plus the proposed record; `design-review.md` and original `design-review.jsonl` retained in cache.
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
