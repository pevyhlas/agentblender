#!/usr/bin/env bash

# Review-package builder for a change's independent review.
#
# Deterministically assembles what an independent reviewer needs to see for one
# change's commit range, and computes the review round number from what is
# already on disk — so interrupted runs resume at the right round instead of
# clobbering round-1.
#
# The package always covers the full BASE..HEAD range. Never HEAD~1: that
# silently drops all but the last commit of a multi-commit change.
#
# Usage: ./build-review-package.sh CHANGE_ID BASE_SHA [HEAD_SHA] [options]
#   --since REV       Add a focused REV..HEAD delta; retain the full BASE..HEAD diff
#   --context PATH    Snapshot a repo-relative tracked file at HEAD (repeatable)
#   --evidence PATH   Copy a completed repo-relative evidence file (repeatable)
#
# ARGUMENTS:
#   CHANGE_ID     Change id (e.g. 004-export-csv)
#   BASE_SHA      Revision the change started from (recorded before implementing)
#   HEAD_SHA      Revision to review up to (default: HEAD)
#
# OUTPUT FILES (in the gitignored .prdkit/cache/evidence/CHANGE_ID/reviews/round-N/):
#   commits.txt   git log BASE..HEAD --oneline
#   diffstat.txt  git diff --stat BASE..HEAD
#   diff.patch    git diff BASE..HEAD
#   index.md      source bindings, inventory and dispatch preflight
#   manifest.tsv  file, bytes, Git blob object id, source
#   delta.patch   optional --since range; full context remains available
#   context/      optional immutable HEAD snapshots
#   evidence/     optional copied evidence; producer must state revision/result/limits
# The reviewer writes its report (report.md) into the same directory.
#
# STDOUT: {"ROUND":N,"BASE_SHA":"<hash>","HEAD_SHA":"<hash>","PACKAGE_DIR":"/abs/cache/round-N",
#          "REPORT_PATH":"/abs/cache/round-N/report.md"}; the State Review line records the two hashes.
#
# Exit status: 0 on success, 1 on usage or revision errors.

set -e

# shellcheck source=/dev/null
source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

case "${1:-}" in
    --help|-h)
        sed -n '/^# Usage:/,/^# Exit status/p' "$0" | sed 's/^# \{0,1\}//'
        exit 0
        ;;
esac

if [[ $# -lt 2 ]]; then
    echo "ERROR: expected CHANGE_ID BASE_SHA [HEAD_SHA] [options]. Use --help for usage." >&2
    exit 1
fi

CHANGE_ID="$1"
BASE_SHA="$2"
shift 2
HEAD_SHA=HEAD
if [ "$#" -gt 0 ] && [[ "$1" != --* ]]; then HEAD_SHA="$1"; shift; fi
SINCE=""; CONTEXT=(); EVIDENCE=()
while [ "$#" -gt 0 ]; do
    case "$1" in
        --since|--context|--evidence)
            [ "$#" -ge 2 ] && [ -n "$2" ] || { echo "ERROR: $1 needs a value" >&2; exit 1; }
            case "$1" in
                --since) SINCE="$2" ;;
                --context) CONTEXT+=("$2") ;;
                --evidence) EVIDENCE+=("$2") ;;
            esac
            shift 2 ;;
        *) echo "ERROR: unknown argument: $1" >&2; exit 1 ;;
    esac
done

# CHANGE_ID becomes a path component under the cache — reject anything that is
# not a plain change id (a slash or '..' would write outside the evidence cache).
if ! is_change_id "$CHANGE_ID"; then
    echo "ERROR: CHANGE_ID must look like 004-export-csv (got: $CHANGE_ID)" >&2
    exit 1
fi

if ! REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null)"; then
    echo "ERROR: run inside the change's Git checkout" >&2
    exit 1
fi
for rev in "$BASE_SHA" "$HEAD_SHA"; do
    if ! git -C "$REPO_ROOT" rev-parse --verify --quiet "${rev}^{commit}" >/dev/null 2>&1; then
        echo "ERROR: '$rev' does not resolve to a commit in this repository" >&2
        exit 1
    fi
done
# Freeze both ends: a name such as HEAD moves, and the State Review line records these hashes.
BASE_SHA="$(git -C "$REPO_ROOT" rev-parse --verify "${BASE_SHA}^{commit}")"
HEAD_SHA="$(git -C "$REPO_ROOT" rev-parse --verify "${HEAD_SHA}^{commit}")"
if ! git -C "$REPO_ROOT" merge-base --is-ancestor "$BASE_SHA" "$HEAD_SHA"; then
    echo "ERROR: BASE must be an ancestor of HEAD" >&2; exit 1
fi
if [ -n "$SINCE" ]; then
    SINCE="$(git -C "$REPO_ROOT" rev-parse --verify "${SINCE}^{commit}")" || exit 1
    if ! git -C "$REPO_ROOT" merge-base --is-ancestor "$BASE_SHA" "$SINCE" ||
       ! git -C "$REPO_ROOT" merge-base --is-ancestor "$SINCE" "$HEAD_SHA" || [ "$SINCE" = "$HEAD_SHA" ]; then
        echo "ERROR: --since must be within BASE..HEAD and before HEAD" >&2; exit 1
    fi
fi

safe_path() { # relative path, with no traversal, control characters or symlink components
    local p="$1" part current="$REPO_ROOT" rest="$1"
    case "$p" in ''|/*|*/|*//*|*[[:cntrl:]]*) return 1 ;; esac
    while [ -n "$rest" ]; do
        part="${rest%%/*}"
        case "$part" in .|..) return 1 ;; esac
        current="$current/$part"; [ ! -L "$current" ] || return 1
        if [ "$rest" = "$part" ]; then break; fi
        rest="${rest#*/}"
    done
}
for p in ${CONTEXT[@]+"${CONTEXT[@]}"}; do
    safe_path "$p" || { echo "ERROR: unsafe context path: $p" >&2; exit 1; }
    # Reject tree entries and symlinks even if the current checkout differs from HEAD.
    mode="$(git -C "$REPO_ROOT" ls-tree "$HEAD_SHA" -- ":(literal)$p")"
    case "$mode" in 100644\ *|100755\ *) ;; *) echo "ERROR: context must be a regular file at HEAD: $p" >&2; exit 1 ;; esac
done
for p in ${EVIDENCE[@]+"${EVIDENCE[@]}"}; do
    safe_path "$p" && [ -f "$REPO_ROOT/$p" ] && [ -r "$REPO_ROOT/$p" ] || {
        echo "ERROR: evidence must be a readable, contained regular file: $p" >&2; exit 1;
    }
done

# --- Compute the next round from existing round-* directories ----------------
REVIEW_DIR="$REPO_ROOT/.prdkit/cache/evidence/$CHANGE_ID/reviews"
safe_path ".prdkit/cache/evidence/$CHANGE_ID/reviews" || { echo "ERROR: symlinked package directory" >&2; exit 1; }
ROUND=1
for d in "$REVIEW_DIR"/round-*/; do
    [[ -d "$d" ]] || continue
    n="${d%/}"
    n="${n##*/round-}"
    case "$n" in
        ''|*[!0-9]*) continue ;;
    esac
    n=$((10#$n))
    if [[ "$n" -ge "$ROUND" ]]; then
        ROUND=$((n + 1))
    fi
done

# Claim the round directory atomically: `mkdir` without -p fails if the
# directory already exists, so two concurrent invocations can never both
# claim the same round and overwrite each other's package.
mkdir -p "$REVIEW_DIR"
PACKAGE_DIR="$REVIEW_DIR/round-$ROUND"
until mkdir "$PACKAGE_DIR" 2>/dev/null; do
    ROUND=$((ROUND + 1))
    PACKAGE_DIR="$REVIEW_DIR/round-$ROUND"
    if [[ "$ROUND" -gt 999 ]]; then
        echo "ERROR: could not claim a review round directory under $REVIEW_DIR" >&2
        exit 1
    fi
done
if ! git -C "$REPO_ROOT" check-ignore -q "$PACKAGE_DIR/diff.patch" 2>/dev/null; then
    echo "WARNING: .prdkit/cache/ is not gitignored; add it (init.sh --upgrade does) so raw review packages are never committed." >&2
fi

# --- Write the package over the FULL range (never HEAD~1) --------------------
git -C "$REPO_ROOT" log "$BASE_SHA..$HEAD_SHA" --oneline > "$PACKAGE_DIR/commits.txt"
git -C "$REPO_ROOT" diff --no-ext-diff --no-textconv --stat "$BASE_SHA..$HEAD_SHA" > "$PACKAGE_DIR/diffstat.txt"
git -C "$REPO_ROOT" diff --no-ext-diff --no-textconv --binary "$BASE_SHA..$HEAD_SHA" > "$PACKAGE_DIR/diff.patch"

printf 'file\tbytes\tblob_oid\tsource\n' > "$PACKAGE_DIR/manifest.tsv"
inventory() {
    local file="$1" source="$2"
    printf '%s\t%s\t%s\t%s\n' "$file" "$(wc -c < "$PACKAGE_DIR/$file" | tr -d ' ')" \
        "$(git -C "$REPO_ROOT" hash-object --no-filters "$PACKAGE_DIR/$file")" "$source" >> "$PACKAGE_DIR/manifest.tsv"
}
for p in commits.txt diffstat.txt diff.patch; do inventory "$p" "$BASE_SHA..$HEAD_SHA"; done
if [ -n "$SINCE" ]; then
    git -C "$REPO_ROOT" diff --no-ext-diff --no-textconv --binary "$SINCE..$HEAD_SHA" > "$PACKAGE_DIR/delta.patch"
    inventory delta.patch "$SINCE..$HEAD_SHA"
fi
for p in ${CONTEXT[@]+"${CONTEXT[@]}"}; do
    mkdir -p "$(dirname "$PACKAGE_DIR/context/$p")"
    git -C "$REPO_ROOT" show "$HEAD_SHA:$p" > "$PACKAGE_DIR/context/$p"
    inventory "context/$p" "$HEAD_SHA:$p"
done
for p in ${EVIDENCE[@]+"${EVIDENCE[@]}"}; do
    before="$(git -C "$REPO_ROOT" hash-object --no-filters "$REPO_ROOT/$p")"
    mkdir -p "$(dirname "$PACKAGE_DIR/evidence/$p")"
    cp "$REPO_ROOT/$p" "$PACKAGE_DIR/evidence/$p"
    copied="$(git -C "$REPO_ROOT" hash-object --no-filters "$PACKAGE_DIR/evidence/$p")"
    after="$(git -C "$REPO_ROOT" hash-object --no-filters "$REPO_ROOT/$p")"
    if [ "$before" != "$copied" ] || [ "$before" != "$after" ]; then
        echo "ERROR: evidence changed while copying: $p; incomplete round retained" >&2; exit 1
    fi
    inventory "evidence/$p" "caller-supplied:$p (verify producer revision/result)"
done
cat > "$PACKAGE_DIR/index.md" <<EOF
# Review package: $CHANGE_ID

- Full range: $BASE_SHA..$HEAD_SHA
- Focused delta: ${SINCE:-none}${SINCE:+..$HEAD_SHA}
- Inventory: manifest.tsv (bytes and Git blob object IDs of exact packaged content).
- Tracked context is read from the named HEAD, never the working copy.
- Evidence is a stable copy, not proof of completion or applicability to HEAD.

## Before dispatch

Read the inventory. Supply the change/acceptance record, applicable canon and review policy,
bounds output with its input revision, prior findings for re-review, and completed verification
results (command, revision, environment, exit/result and limitations). Explicitly identify missing
required proof. This structural package does not certify semantic completeness or review readiness.

Confirm the reviewer's allowed read paths, shell/network access and report channel. Copy or attach
this package inside that access boundary; local sibling paths are not portable evidence. A read-only
reviewer consumes captured bounds/results and returns its report as text when it cannot write.
Full diff remains available; use the focused delta and affected obligations for a re-review.
EOF

printf '{"ROUND":%d,"BASE_SHA":"%s","HEAD_SHA":"%s","PACKAGE_DIR":"%s","REPORT_PATH":"%s","INDEX_PATH":"%s","SINCE_SHA":"%s"}\n' "$ROUND" "$BASE_SHA" "$HEAD_SHA" "$(json_escape "$PACKAGE_DIR")" "$(json_escape "$PACKAGE_DIR/report.md")" "$(json_escape "$PACKAGE_DIR/index.md")" "$SINCE"
