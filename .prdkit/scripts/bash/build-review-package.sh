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
# Usage: ./build-review-package.sh CHANGE_ID BASE_SHA [HEAD_SHA]
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

if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "ERROR: expected CHANGE_ID BASE_SHA [HEAD_SHA]. Use --help for usage." >&2
    exit 1
fi

CHANGE_ID="$1"
BASE_SHA="$2"
HEAD_SHA="${3:-HEAD}"

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

# --- Compute the next round from existing round-* directories ----------------
REVIEW_DIR="$REPO_ROOT/.prdkit/cache/evidence/$CHANGE_ID/reviews"
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
git -C "$REPO_ROOT" diff --stat "$BASE_SHA..$HEAD_SHA" > "$PACKAGE_DIR/diffstat.txt"
git -C "$REPO_ROOT" diff "$BASE_SHA..$HEAD_SHA" > "$PACKAGE_DIR/diff.patch"

printf '{"ROUND":%d,"BASE_SHA":"%s","HEAD_SHA":"%s","PACKAGE_DIR":"%s","REPORT_PATH":"%s"}\n' "$ROUND" "$BASE_SHA" "$HEAD_SHA" "$(json_escape "$PACKAGE_DIR")" "$(json_escape "$PACKAGE_DIR/report.md")"
