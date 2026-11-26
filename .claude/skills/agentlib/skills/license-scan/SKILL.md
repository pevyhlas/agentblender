---
name: license-scan
description: "Lists license facts for a change (npm, NuGet and Python dependencies added, removed or version-changed, each one's declared license resolved offline and classed against the repo's allow/review/deny lists, and vendored fonts, assets or copied code added without a license file). Use when reviewing a change to dependencies, lockfiles or third-party files; not for deciding whether a license is acceptable."
metadata:
  prdkit/needs: python3 git
  estate/requires-kit: ">=3.1.5"
allowed-tools: Bash(${CLAUDE_SKILL_DIR}/scripts/license_scan.py *)
---

# license-scan

A script compares the dependency manifests and lockfiles of two revisions and lists what changed,
the license each new dependency declares, and the third-party files the change adds. It does not
decide whether a license is compatible: you judge each fact against the repository's target
license, and you own the verdict. Cite these facts; spend your effort on what they cannot prove.

## Run it

Run the script itself (not through `python3`), from or with `--root` at the repository. For a pull
request, pass its base; `--head` defaults to `HEAD`, and the range starts at the merge base.

```bash
${CLAUDE_SKILL_DIR}/scripts/license_scan.py --root <repo> --base origin/main
${CLAUDE_SKILL_DIR}/scripts/license_scan.py --root <repo> --base origin/main --head <sha> --json
${CLAUDE_SKILL_DIR}/scripts/license_scan.py --root <repo> --all
```

`--all` lists every dependency and vendored file at `--head` instead of a diff; in a directory
that is not a git checkout it reads the files on disk. The markdown summary is at most 60 lines;
`--json` holds every dependency with its manifests, licenses and class. Exit 0 means the scan ran,
whatever it found; exit 2 is a usage error, an unknown ref or an unreadable path.

To hand the facts to a reviewer that cannot run scripts, write both files into the change's
evidence directory under these file names, which the reviewer looks for:
`--out .prdkit/cache/evidence/<id>/facts/license-scan.md --json-out .prdkit/cache/evidence/<id>/facts/license-scan.json`.
Attach them to the Product-Kit review package, which keeps them under `evidence/`:
`build-review-package.sh <id> <base> <head> --evidence .prdkit/cache/evidence/<id>/facts/license-scan.md --evidence .prdkit/cache/evidence/<id>/facts/license-scan.json`.

Every report names its revision: `revision.head`, and for a diff `revision.base` and
`revision.merge_base`, plus each manifest's git blob id (`manifests[].blob`). The script reads
committed files only; `revision.uncommitted` lists manifests and vendored files that differ in the
working tree and were not scanned. When the facts are already in the review package, cite them
only if that head and merge base are the ones under review; otherwise run the script again, or
say under Unverified that the facts are for another revision.

## Facts

| id | what the script saw |
|---|---|
| `license-denied` | a new, bumped or scope-changed dependency whose license (or package name) is on the deny list |
| `license-review` | its license or name is on the review list |
| `license-unlisted` | its license id is on no list |
| `license-unknown` | no license could be resolved offline; the detail says why (not restored, license only as a URL or a file in the package) |
| `license-unparsed` | the declared license is not an SPDX expression, such as `SEE LICENSE IN ...` |
| `vendored-without-license` | an added or moved font, 3D model, texture, audio file, or any file under a vendor directory, with no license file beside it or in a parent directory below the repository root; or an added git submodule, whose license lives in that other repository and is not read (symlinks are skipped) |

A dependency is `scope-changed` when its versions stay the same but it was added to or removed from
a manifest (another shipped project) or lost or gained its dev-only or direct flag; `scope` says
which, for version changes too. Licenses are looked up for every version the change places somewhere
new: a manifest that did not have that version, or runtime where it was dev-only. A change that only
removes versions or flags gets no lookup and is listed as "no new placement". A license marked
"dev-only there" belongs to a version that comes in only as a dev dependency. Each dependency also
says whether it is direct (in the root project or an npm workspace) or transitive and dev-only, and
the summary counts transitive additions and every license seen. Allowed dependencies, and vendored
files with the license file that counted for them, are listed under "Other changes", not as facts. A
license file is known by its name (`LICENSE`, `COPYING.LESSER`, `MIT-LICENSE.txt`, `OFL.txt`,
`THIRD-PARTY-NOTICES`, a `<file>.license` sidecar, or a `license-files` glob in the policy); a font
or sound named `Copyright-Sans.ttf` is not one. A license file in a parent directory counts,
including a project's own `LICENSE` (`license_file_at: parent`): check that it really covers the
file.

Licenses come from the lockfile, `node_modules`, the local NuGet package cache or a `.venv`/`venv`
beside the manifest or above it; the script never uses the network and never infers a license. An
unknown license is not a permissive one: read the package's license yourself or say under Unverified
that it is unknown.

The script does not see:
- code copied into existing source files, or a header that changed the license of a file;
- licenses of packages not in a parsed manifest (yarn, pnpm and poetry lockfiles are listed
  under `limits`, not read), or of tools fetched at build time;
- whether a license file next to a vendored file covers that file, or notice and attribution
  duties the license brings;
- how a dependency is linked or distributed, which decides what a copyleft license requires.

## Policy

The repository's allow, review and deny lists live in `${CLAUDE_SKILL_DIR}/references/local.md` inside this skill
(see `${CLAUDE_SKILL_DIR}/references/local.example.md`, which also covers package rules and vendored-file settings).
Without that file every resolved license is `unclassified`: then compare each one with the
repository's license yourself and say that no policy was applied.

<!-- rendered from skills/license-scan/SKILL.md · do not hand-edit (source: agent-library) -->
