# Change: Record the producing Blender support policy in delivery provenance

**Opened**: 2026-10-25 · **Parts**: P1 · **Journeys**: J-04, J-06 · **Tier**: T2 · **Parent**: 008-agent-operability

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

Continue parent008/T040: a delivered still already names its producing Blender version, but
does not preserve whether the producer enabled `blender.allowUnsupported` or how that version
related to its supported line. An offline consumer must be able to distinguish an ordinary
supported producer from an explicit override without consulting the current daemon configuration.
This follows the owner's public-roadmap continuation; release and live rollout remain separate.

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|
| L2 | P1.B-25 | producer receipt and bound recipe snapshot preserve Blender support policy | parent008/T040 |
| L2 | P1.B-26 | offline verification checks recorded policy structure and receipt/snapshot agreement | integrity, not provenance approval |

## Bounds

<!-- From `.prdkit/scripts/bash/bounds.sh <id>`: each constraint, non-goal or quality this change
could break, with how it holds; then "Others in scope: hold". A conflict goes to the owner inbox
with both sides, never resolved here. -->

- C-01/C-02/C-08: host-only C# using existing libraries; no dependency, inbound implementation,
  external asset or worker/GPL-boundary change.
- C-04/C-05: preserve only version/policy facts, never a Blender executable path, machine identity
  or environment content. No telemetry change.
- C-06/C-11: information stays in producer files delivered to the consumer; no runtime coupling
  or assertion of verified provenance/rights.
- Q-02: add one bounded producer object, no new tool or default response expansion.
- Q-03: reuse `SupportedBlender` for export classification, never infer historical policy from a
  later verifier's installed Blender or configured supported line.
- Q-04: verification remains read-only and bounded; malformed or inconsistent claims fail before
  a generation can publish. Existing staging, failure and cancellation behavior holds.
- Others in scope: hold.

## Approach

Proposed for independent architecture/security design review before implementation:

1. Add optional `producer.blenderPolicy` to the existing version2 producer receipt, with a matching
   `blenderPolicy` object in its hash-bound recipe snapshot. Proposed closed fields:
   `supportedLine`, `referenceVersion`, `support` and `allowUnsupported`. The existing
   `blenderVersion` remains the authoritative producing version and is not duplicated inside
   the policy object. New exports always write the object in deterministic member order.
2. Export captures the effective `AblOptions.AllowUnsupportedBlender` and classification of the
   producing worker's hello version. Use `SupportedBlender.Line`, `Reference`, `Classify` and
   `Name`, not another support table. `support` is `reference`, `supported`, `unsupported`, or
   `unknown` when the existing explicit-override gate allowed an unparseable version.
   `allowUnsupported` means the setting was enabled; it does not falsely imply the override was
   necessary when `support` is reference/supported. The version/support combination makes that
   distinction inspectable. No change to the worker version gate is proposed.
3. Offline verification validates the bounded closed object and compares it exactly with the
   snapshot. It evaluates internal consistency against the recorded policy, not today's support
   constants. Unsupported/unknown production cannot claim `allowUnsupported=false`. Unknown
   values, invalid types, duplicate keys and partial objects fail through existing typed errors.
4. Existing version2 receipts and snapshots with both fields absent retain unknown historical
   policy; absence never defaults to false or retroactively proves supported production. A field
   present in only one of the two files fails agreement. This is an additive optional field, not
   a migration or rewriting of old generations. Older strict readers reject new receipts and
   require the newer tool; document that explicitly for the experimental surface.
5. The closed AGD payload shape stays as defined by P1.B-25. Producer-policy data remains in the
   receipt/snapshot; only the already-existing snapshot hash binding changes as a consequence.
   CLI/MCP export share the Core capture, and offline CLI/Core verification shares one policy.

Alternatives: recording only the enabled flag loses the distinction between supported operation
and a needed override; deriving support during verification rewrites history when the supported
line changes. A new receipt version would force a cutover for an optional additive producer fact;
prefer the explicit absent-as-unknown policy unless architecture review finds it incompatible
with the existing strict reader contract.

## Tasks

- [ ] Independent architecture/security design review; resolve optional-field and historical-policy semantics.
- [ ] Add the producer policy value/capture and bind it into receipt/snapshot verification.
- [ ] Pin supported reference/other patch/unsupported/unknown and enabled/disabled combinations;
  historical absence, partial/malformed objects and tampering; deterministic serialization.
- [ ] Real-Blender export and installed offline verification evidence; code, architecture,
  security and agent-ergonomics review; exact-head CI/review and authorized delivery.

## Checks

Use handwritten expected policy values rather than recomputing the oracle through the capture
helper. Preserve existing receipt/snapshot integrity, receiver-payload shape and staging tests.
Prove the enabled flag on a supported version does not label it unsupported. No unsupported
Blender binary needs installing: deterministic fixtures exercise version policy; the existing
reference Blender integration proves production wiring. Installed verification must consume a
generation from the actual exporter, then reject an inconsistent policy without writing it.

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | P1.B-25, T040 | new exports capture the actual version/policy in both producer files; reference, patch, unsupported and unparseable cases | 0 | NOT_RUN |
| A2 | P1.B-26, Q-04 | offline verify rejects malformed/inconsistent policy; absence in both files remains unknown; current verifier policy cannot reclassify history | 0 | NOT_RUN |
| A3 | P1.B-25, R-01 | deterministic receipt bytes, unchanged AGD payload shape, real exporter and installed offline verification through shared Core | 0 | NOT_RUN |
| A4 | Delivery verification | required source/installed checks and applicable independent reviews | 0 | NOT_RUN |

## State

**Step**: bounded design draft
**Waiting on**: independent architecture/security design review.
**Authority**: owner public-roadmap continuation and “and then continue with next steps”;
constitution feature commit/push/PR and reviewed exact-head green merge policy.
**Attempts**: design0, implementation0, acceptance0.
**Branch / PR**: 016-delivery-provenance, [draft #12](https://github.com/pevyhlas/agentblender/pull/12).
Merged015 is incorporated from main8bd52bb; its final source/installed, native and review evidence
is in [PR #11](https://github.com/pevyhlas/agentblender/pull/11).
**Next**: obtain architecture/security design review, then implement. No release, deployment or
platform-qualification approval is inferred.
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
