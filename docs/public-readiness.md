# Public readiness

AgentBlender is an **experimental public preview**, not a packaged GA release. Source is
public; a green build, a published package, hardware qualification and real project uptake
are separate claims.

## What is already in place

- Public MIT source with Blender non-affiliation and third-party notices.
- GitHub-hosted CI for fork PRs and main: read-only tokens, SHA-pinned actions, no PR secrets,
  locked restore, worker/GPL-boundary checks, build/unit tests and isolated package installation.
- Real-Blender integration on a checksummed official download, plus an advisory macOS lane.
- Active main/release-tag rulesets. Main requires `Validate` and `Build & unit tests`;
  the Blender and macOS jobs are not required checks.
- Public Product-Kit **1.2.0**, vendored with manifest integrity and upstream attribution.
- Reference SDK **10.0.113**; later SDK bands remain a contributor convenience, not CI evidence.

The baseline main run passed all four lanes:
[GitHub Actions](https://github.com/pevyhlas/agentblender/actions/runs/39886672614).
That run precedes this maintenance and does not qualify it. Maintenance exact-head CI is
**pending**: the current GitHub App credential cannot push to this repository (403). No PR
or release is claimed; restore repository access, publish the branch and run required CI.

## Before a public packaged release

| Priority | Remaining work | Authority / source |
|---|---|---|
| Security blocker | Resolve the independent launcher bind-boundary finding before release; re-review its enforcement | Independent security review of the public baseline; no waiver granted |
| Packaging / environment | Qualify tracked-only worker packaging and desktop-child environment hygiene | Independent review carry-items; package/source and environment-canary regressions needed |
| Required | Finish the shared path-trust hardening, delivery support-status provenance and service/test carry-overs | [Change 008](../specs/changes/008-agent-operability.md), T021, T040, T056 and service tasks |
| Required for macOS claims | Fresh-setup and Metal qualification on real Apple silicon with Blender 5.2.x | [Change 008](../specs/changes/008-agent-operability.md) T074; [change 009](../specs/changes/009-metal-gpu.md) T032–T034 |
| Release decision | Choose the exact qualified source/version and authorize its public tag, release and checksummed bundle | Maintainer; [inbox](../specs/inbox.md#first-public-release) |
| CI decision | Decide whether the now-green hosted Blender lane becomes required; macOS stays advisory until qualification | Maintainer; [CI inbox](../specs/inbox.md#hosted-blender-lane-and-optix-coverage) |
| Interop follow-up | Requalify the receiver against AGD's next public package hashes | [Receiver inbox](../specs/inbox.md#receiver-qualification-against-agds-public-release) |

Linux-only packaging could be a separately scoped maintainer decision; this checklist does
not turn unfinished macOS qualification into support, or silently drop change 008's acceptance.
Live desktop rollout remains separately owner-gated. No release or deployment is performed by CI.

## Public-data hygiene

Commit source and scrubbed summaries, not local logs, runtime state, credentials, machine
inventories or private consumer paths. Preserve required upstream copyright and licence
notices: they are not accidental personal data. Use reserved example addresses and generic
paths in documentation and tests.

A pattern check cannot prove the absence of all secrets or personal information. Review the
complete diff, new assets, commit identity and any public workflow artifacts before publishing.
If a real credential is exposed, revoke or rotate it first and follow [SECURITY.md](../SECURITY.md);
do not post its value in an issue or rely on deleting the current file to remove history.

CI runs `python3 scripts/check-public-repo.py --self-test` and then the same command without
the flag. This standard-library-only developer guard rejects common credential signatures,
non-example home paths and tracked runtime/log/credential files, and checks entry-page links.
It never prints matched values, does not scan binary payloads or history, and is not a substitute
for a dedicated secret scanner or independent review. The baseline review identified no
accidental credentials/private identifiers in the text it inspected; that is not an all-clear.

## Contributing checks

See [CONTRIBUTING.md](../CONTRIBUTING.md) for commands and [operations](../specs/architecture/operations.md)
for CI and manual hardware qualification. Product-Kit doctor remains non-strict while the
maintainer inbox and the fresh-history archive advisories are unresolved. A doctor pass is
not release acceptance.
