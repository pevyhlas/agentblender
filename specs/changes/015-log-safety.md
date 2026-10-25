# Change: Validate daemon log transactions and every tail path

**Opened**: 2026-10-25 · **Parts**: P1 · **Journeys**: J-06 · **Tier**: T2 · **Parent**: 008-agent-operability

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

Finish parent008/T021's log opening/rotation and reader safeguards after shared facts landed
in013. Existing log chmod-after-open can follow links and rotate publicly readable files;
path inspection alone cannot bind subsequent IO to the inspected directory/file. Startup/down
tails also bypass the verb's predicate. Continue the owner's roadmap direction using native
Linux and hosted macOS unit evidence; full host/Metal qualification stays open.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L2 | P1.B-11/P1.B-13 operability | fail-closed private regular log transactions and every tail open | parent008/T021 |

## Bounds

<!-- From `.prdkit/scripts/bash/bounds.sh <id>`: each constraint, non-goal or quality this change
could break, with how it holds; then "Others in scope: hold". A conflict goes to the owner inbox
with both sides, never resolved here. -->

- C-03: no listener or authentication change.
- C-04: reject unsafe logs before read/write/rotation; never chmod-repair existing paths.
- C-08: shared C# Unix primitives, no production Python or new dependency.
- C-12: Linux/Darwin native coverage, no host/Metal qualification claim.
- Q-04: preserve descriptors and bound tail bytes/lines; validation refusal leaves all pre-existing
  objects/bytes/modes untouched. Tail never creates. Exclusively created empty entries may remain
  after later validation fails, as defined below.
- Others in scope: hold.

## Approach

Proposed for independent eng-security/eng-arch design review before implementation:

1. A shared Core directory-handle transaction helper walks from a root anchor using openat with
   directory/no-follow/close-on-exec flags, validating each opened directory's UID, mode and macOS
   ACL. Allow root/current-user owners only and no group/other write; root-owned sticky temporary
   ancestors are a narrow exception. Only verified system aliases /tmp,/var,/etc may normalize to
   their existing trusted targets. No arbitrary final/ancestor links. Retain the leaf-parent fd
   through every log operation, so another UID cannot substitute a pathname ancestor after checks.
2. Missing directories are created 0700 relative to a validated parent and opened/validated before
   descending. Existing ancestors are not repaired. This must preserve ordinary state paths and
   safe custom log directories; the containing directory need not be0700 when owner/mode/ACL makes
   replacement by another UID impossible. Final files must be owned regular private files.
3. Inspect leaf type without following before opening; use no-follow/nonblocking/close-on-exec
   open, then inspect the opened descriptor (Linux statx empty-path, Darwin fstat/descriptor ACL).
   Require current-user UID, no group/other permission or allow ACL, and link count1. Create new
   logs with exclusive0600 creation. No truncation or write before descriptor validation.
4. Rotation validates active and existing .1 entries first, checks the active descriptor's identity
   against its parent entry, renames relative to the held parent, and exclusively creates the new
   log. If replacement creation fails, preserve all old bytes in .1 and report the failure; this is
   not a multi-file atomic transaction. Same-UID malicious mutation and root are outside the
   existing local-user threat model; no lock/retry journal is introduced.
5. Redirect duplicates only a verified append handle. Tail validates at its actual open (all paths,
   including derived/startup/down), uses one captured length and a bounded read window, and handles
   growth/truncation without allocating by a later unchecked length. Preserve terminal cleaning.
   Lifecycle failure reporting returns an actionable refusal note instead of masking startup error.
6. PathFacts remains the read-only inspection DTO for existing callers. Native fd facts share its
   UID/mode/type/ACL semantics plus device/inode/link/size identity; no second policy based on chmod.
7. Ownership/effects: the transaction owns every directory handle, closes each intermediate handle
   after opening its child, and disposes all handles on failure; the caller owns a returned verified
   log handle. Readers are strictly non-creating. After a creation followed by validation failure,
   retain any exclusively created empty entries (including new0700 ancestors); report refusal and
   never reuse them without validation. No compensating deletion can target a substituted entry.
   Pre-existing files/dirs/modes/bytes remain unchanged on validation refusal. Rotation's explicit
   rename/create failure rule is the only pre-existing-name change. Redirect first acquires all
   resources and saved close-on-exec copies of original descriptors, restores originals on partial
   dup/cwd failure, and never continues daemon startup after a redirection error.

Alternatives: pathname checks followed by FileStream are insufficient for retained other-UID
ancestor-substitution findings. Restricting logs to the state directory would remove supported
custom paths; prefer a reusable anchored transaction for the existing paths.

## Tasks

- [x] Native syscall/ABI contracts in observed, independent security and architecture design review.
- [x] Anchored log open/rotate and authoritative reader path; no unsafe-path repair.
- [ ] Native and deterministic race-seam tests, startup/derived-tail regressions on both OSes.
- [ ] Full unit/installed checks, independent final risk reviews, exact-head CI and delivery.

## Checks

Design review must settle sticky/system-alias handling, opened-object ACL semantics and rotation
failure behavior before coding. Test effects (sentinel bytes/modes and rotated files), not just
predicate booleans. Retain Linux failures and hosted macOS results; qualification exclusions stay.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P1 operability, C-04 | create/append/rotate only private owned regular files; final links/specials/ACL/foreign/wide/hardlink cases refuse before effects | Linux1, native3 | FAIL native append fixture: expected first+second lines, got second; other native path/ACL/mode/rotation refusals pass |
| A2 | Q-04 | injected ancestor/leaf substitution and opened-object mismatch cannot redirect IO; failures preserve bytes and modes | Linux1, native3 | PASS Linux/native substitution, fd-owner and rollback cases atb513703 |
| A3 | P1 operability | recorded, derived and startup/down tails guard actual opened file, bounded reads and actionable refusal | Linux2, native3 | PASS Linux source/installed and all103native lifecycle integration cases |
| A4 | Delivery | Linux/macOS unit and installed behavior; independent security/architecture/code/ergonomics reviews and exact-head CI | source3, native3 | BLOCKED: run40232947019 Linux863pass/1skip, Blender56pass/1skip; macOS863pass/1fail. Installed checks pass; final code/security verdicts remain pending |

## State

**Step**: resumed native verification under owner-extended budget
**Waiting on**: native append-oracle correction, final code/security review and exact-head gates.
**Authority**: owner roadmap continuation and “go ahead - we should setup github CI for mac os”;
constitution feature commit/push/PR and exact-head reviewed green merge. Releases and live rollout
remain owner-gated.
**Branch / PR**: 015-log-safety, originally based on d5b9ea260f60eb45dd7b57fd29df3b7a485d5c41;
[PR #11](https://github.com/pevyhlas/agentblender/pull/11). Merged014 hosted macOS CI is incorporated;
integration base is64c538a95dd95fac00b440386160a2979358dd37. Anchored primitives and all log paths implemented.
**Attempts**: Linux focused102pass on first run; complete acceptance pending. Initial design passed
with one advisory clarification. Pre-implementation Linux CI run40221466466
failed two version-probe tests, then unchanged-head diagnostic rerun attempt2passed (all jobs).
Original failure and rerun evidence retained under `.prdkit/cache/evidence/015-log-safety/`.
This establishes a transient failure, not a proved root cause or production repair.
Full unit attempt1 found three stale pre-contract tests: two 0644 fixtures intended for valid
reads/rotation and one explicit chmod-repair expectation. Fixtures now use0600; the repair test
now proves refusal preserves bytes/modes, and service failure covers both private and unsafe output.
No production repair was needed for these failures. Partial dup/cwd restoration uses real native
operations over isolated test descriptors. Full unit attempt2passed at9e03ce0:859passed,1skipped,
0failed (the pre-existing read-only-filesystem fixture skips on this host).
Review fix round1 adds foreign-owner fd-fact/native refusal and empty-created-entry checks plus
unsafe daemon-log failure preservation in the lifecycle test. Focused tests pass. Security's
initial final-review FAIL is tooling/evidence-only (shell denied), with no code finding; code
review's high finding is missing completed evidence and those two regressions. Both reports
are preserved; final exact-head evidence and scoped re-review remain required.
**Next**: run the corrected write(2) append oracle on both platforms, finish reviews, merge after gates.
**Owner extension**: 2026-10-25 conversation: “what is the status and next steps? I approve the
budget at least 5x extra”. Apply up to15additional native acceptance attempts (five times the
original three), counted from run afterb513703. Initial3attempts remain recorded; no automatic
reset of any result, merge gate, release or platform-qualification requirement.
**Native verification repair1**: run40231067326 passed required Linux checks but failed advisory
macOS because descriptor ACL absence returnsNULL/ENOENT. Its logs/TRX are retained. Treat that
specific absence as no ACL after successful fstat; every other error still refuses. Add native
absent/deny-only/file-allow/directory-allow regressions. Run40231692889 started before this fix
and does not exercise it. Native retry after this repair is pending.
**Native verification repair2**: run40231977979 required Linux/integration jobs passed; native
macOS had847pass/17fail. ACL absence was fixed; new evidence identifies Apple arm64 variadic ABI
placement (incorrect creation modes/fcntl EINVAL) plus chmod's named-principal fixture requirement.
Add architecture-restricted stack signatures for the two scalar calls and fix ACL principals;
rollback tests now prove the injected failure was reached. Third native acceptance attempt is
next; if it still fails, stop that line and record alternatives for the owner under protocol limits.
**Installed evidence**: sourceb513703 clean bundle (SDK10.0.113, package0.2.0-dev), isolated
doctor/worker/lifecycle checks and installed private/wide/link/startup log checks passed.
`installed-bundle-b513703.log`, provenance and `installed-log-probe-b513703.log` retain proof.
Security/license lens: no dependency, inbound implementation or worker/GPL-boundary change;
observed Apple/.NET sources establish ABI behavior only, with no implementation copied.
Unchanged ergonomics approval covers the native-only ABI repair; source architecture re-review
passes, while native acceptance remains a separate gate.

### Native acceptance alternatives (after attempt3, resolved by owner extension)

Observed: run40232947019 atb513703 passed required Linux gates and advisory Blender integration;
macOS passed863of864 tests with no skips. All103lifecycle integration cases passed. The only
failure is `CreateAppendAndRotatePreservePrivateBytesAndModes`: its second `FileStream(handle)`
write replaced the first. The two earlier native failures and all logs/TRX remain preserved.

Learned: ACL absence and arm64 varargs defects are repaired and their native regressions pass.
The remaining test uses a seekable FileStream over a raw fd, which .NET implements using
positioned writes; POSIX pwrite ignores O_APPEND, whereas Linux exceptionally honors it. The
daemon uses dup2 and ordinary stdout/stderr writes. This supports a fixture mismatch hypothesis,
but a corrected descriptor-write oracle has not been run and A1 stays failed.
Contracts: [.NET FileStream strategy](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/OSFileStreamStrategy.cs),
[pwrite/O_APPEND distinction](https://man7.org/linux/man-pages/man2/pwrite.2.html).

1. **One further attempt (recommended)**: owner grants one additional native acceptance run to
   replace the positional test write with the daemon's ordinary descriptor-write semantics,
   independently review the change/evidence and merge only with all acceptance/required gates.
   No production change is presumed. If that attempt fails, stop and return the new evidence.
2. **Managed/off-the-shelf IO direction**: reopen design around a supported fixed-arity native
   shim or a managed append abstraction, with fresh ABI/packaging/security review and an
   owner-set attempt limit. More scope; it must preserve anchored validation and refusal effects.
3. **Defer native delivery**: leave this draft published and pause015. Resume other roadmap
   work; later split or relax the cross-platform acceptance only on explicit owner direction.

The record-only checkpoint438f14a skipped CI while awaiting the owner's decision. The owner
subsequently extended the budget as recorded above. The next change corrects only the test oracle:
a fixed-arity write(2) delegate matches the daemon's descriptor semantics and verifies both prefix
bytes and private modes. Production code is unchanged; native attempt4(extension1of15) is next.
**Review**: round 2 · 64c538a95dd95fac00b440386160a2979358dd37..9e03ce0b271b80117a65fd4308222e4cf25862d2 · eng-code · FAIL · 0 critical, 1 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6402901141
**Review**: round 2 · 64c538a95dd95fac00b440386160a2979358dd37..9e03ce0b271b80117a65fd4308222e4cf25862d2 · eng-security · FAIL · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6402901333
**Review**: round 2 · 64c538a95dd95fac00b440386160a2979358dd37..9e03ce0b271b80117a65fd4308222e4cf25862d2 · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6402901521
**Review**: round 2 · 64c538a95dd95fac00b440386160a2979358dd37..9e03ce0b271b80117a65fd4308222e4cf25862d2 · eng-ergonomics · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6402901750
**Review**: round 4 · 9e03ce0b271b80117a65fd4308222e4cf25862d2..e7d32be9de7c71617ba5493a7b62787a4d9aad36 · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6403106522
**Review**: round 4 · 9e03ce0b271b80117a65fd4308222e4cf25862d2..e7d32be9de7c71617ba5493a7b62787a4d9aad36 · eng-ergonomics · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6403106726
**Review**: round 5 · e7d32be9de7c71617ba5493a7b62787a4d9aad36..b5137037c397eaf4adaf9c71a236d71688f2bdf9 · eng-arch · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6403255547
**Design review**: d5b9ea260f60eb45dd7b57fd29df3b7a485d5c41..a190e884ca5bc8173a57955197e89d7b3d0092f5 · eng-security · PASS · 0 critical, 0 high, 0 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6401409148
**Design review**: d5b9ea260f60eb45dd7b57fd29df3b7a485d5c41..a190e884ca5bc8173a57955197e89d7b3d0092f5 · eng-arch · PASS · 0 critical, 0 high, 1 medium, 0 low · https://github.com/pevyhlas/agentblender/pull/11#issuecomment-6401409361; advisory addressed by item7 before coding.
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
