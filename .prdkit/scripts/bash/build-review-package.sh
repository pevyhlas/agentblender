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
# Invariants:
# Keep the full object-bound base-to-head patch alongside any focused delta. Check
# emitted patch paths against the trees, including gitlinks hidden by ignore settings.
# Validate the final artifact inventory before marking a round complete. Completion
# is a package result, not a review verdict or proof that required evidence is present.
#
# Threat model:
# Include overlapping builders, interrupted runs and changing evidence inputs.
# Serialize allocation per change and keep older or incomplete rounds intact.
# A hostile writer able to replace checkout paths or package artifacts is out of scope.
#
# Usage: ./build-review-package.sh CHANGE_ID BASE_SHA [HEAD_SHA] [options]
#   --since REV       Add a focused delta of what the branch changed since REV, an earlier
#                     reviewed head (any commit here but HEAD: after a rebase or squash it is no
#                     longer in HEAD's history); retain the full BASE..HEAD diff. BASE must be
#                     the change's current merge base with a trusted integration branch
#                     (origin/HEAD, or one named by review-integration-branches in its committed
#                     config); anything else is refused. After a base merge, pass the new merge
#                     base as BASE to leave the base out
#   --context PATH    Snapshot a repo-relative tracked file at HEAD (repeatable)
#   --evidence PATH   Copy a completed repo-relative evidence file (repeatable)
#
# Attach a facts.sh pack (set id, base and head to the values in the pack):
#   bash .prdkit/scripts/bash/build-review-package.sh "$id" "$base" "$head" --evidence ".prdkit/cache/evidence/$id/facts/summary.md" --evidence ".prdkit/cache/evidence/$id/facts/index.json"
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
#   delta.patch   optional --since delta (index.md says what it covers); full diff remains
#   context/      optional immutable HEAD snapshots
#   evidence/     optional copied evidence; producer must state revision/result/limits
# The reviewer writes its report (report.md) into the same directory.
#
# STDOUT: {"ROUND":N,"BASE_SHA":"<hash>","HEAD_SHA":"<hash>","PACKAGE_DIR":"/abs/cache/round-N",
#          "REPORT_PATH":"/abs/cache/round-N/report.md","INDEX_PATH":"…","SINCE_SHA":"<hash>"}
#
# Exit status: 0 on success, 1 on usage or revision errors.

set -e

# shellcheck source=/dev/null
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/spec-lib.sh"
source "$SCRIPT_DIR/review-context-lib.sh"
export GIT_NO_LAZY_FETCH=1 GIT_NO_REPLACE_OBJECTS=1
spec_objects_local "$(git rev-parse --show-toplevel 2>/dev/null || echo .)" || {
    echo "ERROR: objects cannot be read locally without fetching; no package allocated" >&2; exit 1;
}

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
# Freeze both ends: a name such as HEAD moves, and the package records immutable provenance.
BASE_SHA="$(git -C "$REPO_ROOT" rev-parse --verify "${BASE_SHA}^{commit}")"
HEAD_SHA="$(git -C "$REPO_ROOT" rev-parse --verify "${HEAD_SHA}^{commit}")"
if ! git -C "$REPO_ROOT" merge-base --is-ancestor "$BASE_SHA" "$HEAD_SHA"; then
    echo "ERROR: BASE must be an ancestor of HEAD" >&2; exit 1
fi
if [ -n "$SINCE" ]; then
    SINCE="$(git -C "$REPO_ROOT" rev-parse --verify "${SINCE}^{commit}")" || exit 1
    # Any earlier reviewed head, in HEAD's history or not: after a base merge the new BASE (the
    # merge base) is newer than it, and after a rebase or squash it is on no branch any more.
    if [ "$SINCE" = "$HEAD_SHA" ]; then
        echo "ERROR: --since must be an earlier reviewed head, not HEAD; no round needed" >&2; exit 1
    fi
fi
# BASE decides what the focused delta leaves out, so with --since it must be where the change
# leaves the integration branch. Any other commit on the branch would drop the commits between
# SINCE and BASE from both patches while the package still looked complete.
trusted_tips="$(spec_trusted_refs "$REPO_ROOT" --tips)"
trusted_refs="$(printf '%s\n' "$trusted_tips" | cut -f1)"
base_is_merge_base=false; merge_bases=""; BASE_REF=""; BASE_AT=""
POLICY_AT="$(printf '%s\n' "$trusted_tips" | sed -n 1p | cut -f2)"
while IFS=$'\t' read -r trusted tip; do
    [ -n "$trusted" ] || continue
    git -C "$REPO_ROOT" merge-base --is-ancestor "$BASE_SHA" "$tip" || continue
    if [ -n "$SINCE" ]; then
        mb="$(git -C "$REPO_ROOT" merge-base --all "$tip" "$HEAD_SHA")" || exit 1
        if [[ "$mb" == *$'\n'* ]]; then
            merge_bases="$merge_bases ${trusted#refs/remotes/}=multiple merge bases"
            continue # Another trusted integration ref may uniquely support BASE.
        fi
    else
        mb="$(git -C "$REPO_ROOT" merge-base "$tip" "$HEAD_SHA" 2>/dev/null || true)"
    fi
    [ -z "$mb" ] || merge_bases="$merge_bases ${trusted#refs/remotes/}=${mb}"
    if [ "$mb" = "$BASE_SHA" ]; then base_is_merge_base=true; BASE_REF="$trusted"; BASE_AT="$tip"; break; fi
done <<<"$trusted_tips"
if [ -n "$SINCE" ]; then
    if [ -z "$trusted_refs" ]; then
        echo "ERROR: --since needs a trusted integration branch to check BASE against: set origin/HEAD (git remote set-head origin --auto) and fetch it, or build the full package without --since" >&2
        exit 1
    fi
    if ! $base_is_merge_base; then
        echo "ERROR: with --since, BASE must be the change's current merge base with a trusted integration branch ($(printf '%s' "$trusted_refs" | sed 's#^refs/remotes/##' | tr '\n' ' ' | sed 's/ $//')), and is not: $BASE_SHA${merge_bases:+; merge base(s) found:$merge_bases}. Pass the current merge base as BASE, or build the full package without --since" >&2
        exit 1
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
# Repeated explicit requests are one immutable snapshot, not conflicting inventory entries.
unique_snapshot_paths() {
    local candidate previous found
    SNAPSHOT_PATHS=()
    for candidate in "$@"; do
        found=false
        for previous in ${SNAPSHOT_PATHS[@]+"${SNAPSHOT_PATHS[@]}"}; do
            [ "$candidate" != "$previous" ] || found=true
        done
        if ! $found; then SNAPSHOT_PATHS+=("$candidate"); fi
    done
}
unique_snapshot_paths ${CONTEXT[@]+"${CONTEXT[@]}"}; CONTEXT=(${SNAPSHOT_PATHS[@]+"${SNAPSHOT_PATHS[@]}"})
unique_snapshot_paths ${EVIDENCE[@]+"${EVIDENCE[@]}"}; EVIDENCE=(${SNAPSHOT_PATHS[@]+"${SNAPSHOT_PATHS[@]}"})
for p in ${CONTEXT[@]+"${CONTEXT[@]}"}; do
    safe_path "$p" || { echo "ERROR: unsafe context path: $p" >&2; exit 1; }
    # Reject tree entries and symlinks even if the current checkout differs from HEAD.
    mode="$(git -C "$REPO_ROOT" ls-tree "$HEAD_SHA" -- ":(literal)$p")"
    case "$mode" in 100644\ *|100755\ *) ;; *) echo "ERROR: context must be a regular file at HEAD: $p" >&2; exit 1 ;; esac
done
PACKAGE_SCRATCH="$(mktemp -d)"
LOCK_CANDIDATE=""; LOCK_PRIVATE=""; LOCK_OWNED=false
cleanup_review_package() {
    # A pathname is not ownership. Refused/interrupted contenders leave the shared entry
    # alone; even an armed owner must still identify its own regular claim by inode.
    if $LOCK_OWNED && [ ! -L "$LOCK_CANDIDATE" ] && [ -d "$LOCK_CANDIDATE" ] &&
        [ ! -L "$LOCK_CANDIDATE/claim" ] && [ -f "$LOCK_CANDIDATE/claim" ] &&
        [ -n "$LOCK_PRIVATE" ] && [ ! -L "$LOCK_PRIVATE" ] &&
        [ "$LOCK_CANDIDATE/claim" -ef "$LOCK_PRIVATE" ]; then
        if rm "$LOCK_CANDIDATE/claim"; then
            rmdir "$LOCK_CANDIDATE" 2>/dev/null ||
                echo "WARNING: allocation directory retained; reconcile it before retrying" >&2
        fi
    fi
    [ -z "$LOCK_PRIVATE" ] || rm -f "$LOCK_PRIVATE"
    rm -rf "$PACKAGE_SCRATCH"
}
acquire_review_lock() {
    LOCK_CANDIDATE="$REVIEW_DIR/.allocation-lock"
    # Never redirect into an existing leaf: FIFO and nonregular/symlink entries are busy.
    if [ -e "$LOCK_CANDIDATE" ] || [ -L "$LOCK_CANDIDATE" ]; then return 1; fi
    LOCK_PRIVATE="$(mktemp "$REVIEW_DIR/.allocation-claim.XXXXXXXX")" || return 1
    printf 'pid=%s\n' "$$" > "$LOCK_PRIVATE" || return 1
    if ! mkdir "$LOCK_CANDIDATE" 2>/dev/null; then return 1; fi
    if [ -L "$LOCK_CANDIDATE" ] || [ ! -d "$LOCK_CANDIDATE" ] ||
        [ -e "$LOCK_CANDIDATE/claim" ] || [ -L "$LOCK_CANDIDATE/claim" ]; then return 1; fi
    # Same-filesystem hard-link creation never opens a FIFO. Verify the private inode rather
    # than trusting a utility's success status or a shared directory's mere existence.
    if ! ln -n "$LOCK_PRIVATE" "$LOCK_CANDIDATE/claim" 2>/dev/null; then return 1; fi
    if [ -L "$LOCK_CANDIDATE" ] || [ -L "$LOCK_CANDIDATE/claim" ] ||
        [ ! -f "$LOCK_CANDIDATE/claim" ] || [ ! "$LOCK_CANDIDATE/claim" -ef "$LOCK_PRIVATE" ]; then return 1; fi
    LOCK_OWNED=true
}
trap cleanup_review_package EXIT
for p in ${EVIDENCE[@]+"${EVIDENCE[@]}"}; do
    safe_path "$p" && [ -f "$REPO_ROOT/$p" ] && [ -r "$REPO_ROOT/$p" ] || {
        echo "ERROR: evidence must be a readable, contained regular file: $p" >&2; exit 1;
    }
done

# --- Compute the next round from existing round-* directories ----------------
REVIEW_DIR="$REPO_ROOT/.prdkit/cache/evidence/$CHANGE_ID/reviews"
safe_path ".prdkit/cache/evidence/$CHANGE_ID/reviews" || { echo "ERROR: symlinked package directory" >&2; exit 1; }
# Serialize completed-binding comparison AND assembly. Busy is retry-later, never permission
# to allocate another identical round. Interrupted packages remain numbered and untouched.
mkdir -p "$REVIEW_DIR"
if ! acquire_review_lock; then
    echo "ERROR: package allocation busy or claim unconfirmed; reconcile retained evidence before retrying" >&2; exit 1
fi
# Only a clean, ordinary full package can be reused automatically. Caller snapshots,
# focused deltas and evidence can differ even on the same head.
reuse_context=full
if [ -n "$SINCE" ] || [ "${#CONTEXT[@]}" -ne 0 ] || [ "${#EVIDENCE[@]}" -ne 0 ] ||
    ! git -C "$REPO_ROOT" diff --quiet --ignore-submodules=none "$HEAD_SHA" --; then
    reuse_context=custom
fi
# A completion binds the manifest itself. Check every listed byte again on reuse,
# including navigation, context and dispatch, without trusting paths from the cache.
verify_inventory() { # directory patch-start head
    local dir="$1" start="$2" head="$3" file bytes oid source header
    [ -f "$dir/manifest.tsv" ] && [ ! -L "$dir/manifest.tsv" ] || return 1
    {
        IFS= read -r header && [ "$header" = $'file\tbytes\tblob_oid\tsource' ] || return 1
        while IFS=$'\t' read -r file bytes oid source; do
            safe_path "${dir#"$REPO_ROOT"/}/$file" && [ -f "$dir/$file" ] &&
                [ "$bytes" = "$(wc -c < "$dir/$file" | tr -d ' ')" ] &&
                [ "$oid" = "$(git -C "$REPO_ROOT" hash-object --no-filters "$dir/$file")" ] || {
                    echo "ERROR: final artifact integrity failed for $file ($source)" >&2; return 1;
                }
            case "$file" in commits.txt|diffstat.txt|diff.patch) [ "$source" = "$start..$head" ] || return 1 ;; esac
        done
    } < "$dir/manifest.tsv"
    awk -F '\t' 'NR>1 { if (NF!=4 || seen[$1]++) bad=1 }
        END { split("commits.txt diffstat.txt diff.patch index.md context-gaps.txt context-manifest.tsv bounds-inputs.tsv", core, " ")
              for (i in core) if (!seen[core[i]]) bad=1; exit bad }' "$dir/manifest.tsv"
}
ROUND=1; COMPLETED=0; COMPLETED_HEAD=""; COMPLETED_BASE=""; COMPLETED_TIP=""; COMPLETED_POLICY=""; COMPLETED_CONTEXT=""
for d in "$REVIEW_DIR"/round-*/; do
    [[ -d "$d" ]] || continue
    [ ! -L "${d%/}" ] || { echo "ERROR: symlinked round directory" >&2; exit 1; }
    n="${d%/}"
    n="${n##*/round-}"
    case "$n" in
        ''|*[!0-9]*) continue ;;
    esac
    n=$((10#$n))
    if [[ "$n" -ge "$ROUND" ]]; then
        ROUND=$((n + 1))
    fi
    if [ "$n" -gt "$COMPLETED" ] && [ -f "$d/completed.tsv" ] && [ ! -L "$d/completed.tsv" ]; then
        # Older interrupted writers can leave empty/unterminated files. A failed complete
        # read or malformed record is incomplete evidence, never a fatal errexit or binding.
        if review_file_without_nul "$d/completed.tsv" &&
            awk -F '\t' 'NR!=1 || NF!=10 { bad=1 } END { exit (bad || NR!=1) }' "$d/completed.tsv" &&
            IFS=$'\t' read -r version bound_id bound_head bound_base bound_tip bound_policy bound_comparison bound_context bound_start bound_inventory < "$d/completed.tsv" &&
            [ "$version" = package5 ] && [ "$bound_id" = "$CHANGE_ID" ] && [ "$bound_comparison" = known ] &&
            [[ "$bound_start" =~ ^[0-9a-f]{40}$ && "$bound_inventory" =~ ^[0-9a-f]{40}$ ]] &&
            [[ "$bound_head" =~ ^[0-9a-f]{40}$ && "$bound_tip" =~ ^[0-9a-f]{40}$ && "$bound_policy" =~ ^[0-9a-f]{40}$ ]] &&
            [[ "$bound_base" == refs/remotes/* && ( "$bound_context" = full || "$bound_context" = custom ) ]]; then
            COMPLETED="$n"; COMPLETED_HEAD="$bound_head"; COMPLETED_BASE="$bound_base"
            COMPLETED_TIP="$bound_tip"; COMPLETED_POLICY="$bound_policy"; COMPLETED_CONTEXT="$bound_context"
            COMPLETED_START="$bound_start"; COMPLETED_INVENTORY="$bound_inventory"; COMPLETED_DIR="${d%/}"
        fi
    fi
done
if [ "$HEAD_SHA" = "$COMPLETED_HEAD" ] && [ "$BASE_REF" = "$COMPLETED_BASE" ] &&
    [ "$BASE_AT" = "$COMPLETED_TIP" ] && [ "$BASE_SHA" = "$COMPLETED_START" ] && [ "$POLICY_AT" = "$COMPLETED_POLICY" ] &&
    [ "$reuse_context" = full ] && [ "$COMPLETED_CONTEXT" = full ] &&
    [ -f "$COMPLETED_DIR/manifest.tsv" ] && [ ! -L "$COMPLETED_DIR/manifest.tsv" ] &&
    [ "$COMPLETED_INVENTORY" = "$(git -C "$REPO_ROOT" hash-object --no-filters "$COMPLETED_DIR/manifest.tsv")" ] &&
    verify_inventory "$COMPLETED_DIR" "$COMPLETED_START" "$COMPLETED_HEAD" 2>/dev/null; then
    echo "ERROR: exact head already packaged in round-$COMPLETED; no round needed" >&2; exit 1
fi

# Claim the round directory atomically: `mkdir` without -p fails if the
# directory already exists, so two concurrent invocations can never both
# claim the same round and overwrite each other's package.
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
patch_options=(--no-ext-diff --no-textconv --no-color --no-renames --no-relative
    --src-prefix=a/ --dst-prefix=b/ --ignore-submodules=none --submodule=short)
write_patch() { # old tree, new tree, destination
    local from="$1" to="$2" dest="$3"
    git -C "$REPO_ROOT" diff "${patch_options[@]}" --binary "$from" "$to" -- > "$dest"
    # Read the actual patch's paths with Git's patch parser, then compare with
    # the tree objects. Git quotes special characters in both line inventories;
    # a type change can emit two patch records for the same path.
    git -C "$REPO_ROOT" -c core.quotepath=true diff-tree --no-commit-id -r --name-only \
        --no-renames --ignore-submodules=none "$from" "$to" -- > "$PACKAGE_SCRATCH/tree-paths"
    : > "$PACKAGE_SCRATCH/numstat"
    if [ -s "$dest" ]; then
        git -C "$REPO_ROOT" -c core.quotepath=true apply --numstat -p1 < "$dest" > "$PACKAGE_SCRATCH/numstat"
    fi
    cut -f3- "$PACKAGE_SCRATCH/numstat" > "$PACKAGE_SCRATCH/patch-paths"
    LC_ALL=C sort -u "$PACKAGE_SCRATCH/patch-paths" > "$PACKAGE_SCRATCH/actual"
    LC_ALL=C sort -u "$PACKAGE_SCRATCH/tree-paths" > "$PACKAGE_SCRATCH/expected"
    cmp -s "$PACKAGE_SCRATCH/actual" "$PACKAGE_SCRATCH/expected" ||
        { echo "ERROR: patch path inventory differs from the object delta; incomplete round retained" >&2; exit 1; }
}
git -C "$REPO_ROOT" log "$BASE_SHA..$HEAD_SHA" --oneline > "$PACKAGE_DIR/commits.txt"
git -C "$REPO_ROOT" diff "${patch_options[@]}" --stat "$BASE_SHA" "$HEAD_SHA" -- > "$PACKAGE_DIR/diffstat.txt"
write_patch "$BASE_SHA" "$HEAD_SHA" "$PACKAGE_DIR/diff.patch"

printf 'file\tbytes\tblob_oid\tsource\n' > "$PACKAGE_DIR/manifest.tsv"
inventory() {
    local file="$1" source="$2"
    printf '%s\t%s\t%s\t%s\n' "$file" "$(wc -c < "$PACKAGE_DIR/$file" | tr -d ' ')" \
        "$(git -C "$REPO_ROOT" hash-object --no-filters "$PACKAGE_DIR/$file")" "$source" >> "$PACKAGE_DIR/manifest.tsv"
}
for p in commits.txt diffstat.txt diff.patch; do inventory "$p" "$BASE_SHA..$HEAD_SHA"; done
# The focused delta is what the branch changed since SINCE, leaving out what BASE brought. BASE
# is the current merge base with a trusted integration branch (checked above), so everything in
# SINCE..BASE is integration history, not unreviewed branch work. When BASE is in SINCE's history
# that is SINCE..HEAD. When the base was merged in after SINCE, a plain
# SINCE..HEAD diff would also show everything the base brought, so the delta compares HEAD with
# Git's automatic merge of BASE into SINCE: commits since SINCE and merge resolutions show, the
# base's own changes do not. When that merge is unavailable (merge-tree --write-tree needs Git
# 2.38+ and full history), it falls back to the patches of the non-merge commits since SINCE that
# BASE does not contain, and index.md names the merges to check in the full diff: a combined diff
# of a merge can still carry base lines.
DELTA="none"
if [ -n "$SINCE" ]; then
    delta_from=""; source_label="$SINCE..$HEAD_SHA"
    if git -C "$REPO_ROOT" merge-base --is-ancestor "$BASE_SHA" "$SINCE"; then
        delta_from="$SINCE"
        DELTA="$SINCE..$HEAD_SHA, everything since $SINCE."
        merges="$(git -C "$REPO_ROOT" rev-list --min-parents=2 "$SINCE..$HEAD_SHA" | tr '\n' ' ' | sed 's/ $//')"
        [ -z "$merges" ] || DELTA="$DELTA It includes merge $merges; check its resolution here."
    elif git -C "$REPO_ROOT" merge-base --is-ancestor "$SINCE" "$BASE_SHA"; then
        delta_from="$BASE_SHA"; source_label="$BASE_SHA..$HEAD_SHA"
        DELTA="$BASE_SHA..$HEAD_SHA, the full range: $SINCE is in the base's history, so nothing on the branch lies between $SINCE and $BASE_SHA."
    else
        merge_status=0
        merged="$(git -C "$REPO_ROOT" -c core.quotepath=true merge-tree --write-tree --name-only --no-messages \
            "$SINCE" "$BASE_SHA" 2>/dev/null)" || merge_status=$?
        tree="$(printf '%s\n' "$merged" | sed -n 1p)"
        if [ "$merge_status" -le 1 ] && [ -n "$tree" ] &&
            git -C "$REPO_ROOT" rev-parse --verify --quiet "$tree^{tree}" >/dev/null; then
            delta_from="$tree"; source_label="$tree..$HEAD_SHA (automatic merge of $BASE_SHA into $SINCE)"
            DELTA="$SINCE..$HEAD_SHA without what the base brought: HEAD against $tree, the automatic merge of $BASE_SHA into $SINCE, so commits since $SINCE and merge resolutions show and the base's own changes do not."
            conflicts="$(printf '%s\n' "$merged" | sed -n '2,$p' | sed '/^$/d' | tr '\n' ' ' | sed 's/ $//')"
            [ -z "$conflicts" ] || DELTA="$DELTA That merge conflicts on $conflicts; the delta shows HEAD's resolution against its conflict markers."
        fi
    fi
    if [ -n "$delta_from" ]; then
        write_patch "$delta_from" "$HEAD_SHA" "$PACKAGE_DIR/delta.patch"
    else
        source_label="$HEAD_SHA ^$SINCE ^$BASE_SHA (non-merge commit patches)"
        git -C "$REPO_ROOT" rev-list --reverse --no-merges --parents "$HEAD_SHA" --not "$SINCE" "$BASE_SHA" > "$PACKAGE_SCRATCH/commits"
        while read -r commit parent; do
            [ -n "$parent" ] || parent="$(git -C "$REPO_ROOT" hash-object -t tree --stdin < /dev/null)"
            git -C "$REPO_ROOT" show -s --no-color --no-decorate --format=medium "$commit"
            write_patch "$parent" "$commit" "$PACKAGE_SCRATCH/commit.patch"
            cat "$PACKAGE_SCRATCH/commit.patch"
        done < "$PACKAGE_SCRATCH/commits" > "$PACKAGE_DIR/delta.patch"
        merges="$(git -C "$REPO_ROOT" rev-list --min-parents=2 "$HEAD_SHA" --not "$SINCE" "$BASE_SHA" | tr '\n' ' ' | sed 's/ $//')"
        DELTA="$SINCE..$HEAD_SHA without what the base brought: the patches of the non-merge commits since $SINCE that $BASE_SHA does not contain, oldest first, as the automatic merge was unavailable (merge-tree --write-tree needs Git 2.38+ and full history)."
        git -C "$REPO_ROOT" merge-base --is-ancestor "$SINCE" "$HEAD_SHA" ||
            DELTA="$DELTA $SINCE is not in HEAD's history (a rebase or squash), so these are all of the change's commits, not only those since the earlier review."
        [ -z "$merges" ] || DELTA="$DELTA Merge resolutions are not in it: check merge $merges in the full diff."
    fi
    inventory delta.patch "$source_label"
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
review_context_build
inventory context-gaps.txt "unresolved required context and verification"
inventory context-manifest.tsv "immutable object/blob/span provenance; gaps are explicit"

cat > "$PACKAGE_DIR/index.md" <<EOF
# Review package: $CHANGE_ID

- Full range: $BASE_SHA..$HEAD_SHA
- Focused delta (delta.patch): $DELTA
- Review commit: $HEAD_SHA (only an approval on this exact PR head counts).
- Inventory: manifest.tsv (bytes and Git blob object IDs of exact packaged content).
- Context: context-manifest.tsv (immutable source revisions, blobs and complete spans; gaps named).
- Tracked context is read from the named HEAD, never the working copy.
- Evidence is a stable copy, not proof of completion or applicability to HEAD.

## Before dispatch

Start at the index, context manifest and full diff, not the whole programme parent.
Read the inventory. Supply the change/acceptance record, applicable canon and review policy,
bounds output with its input revision, prior findings for re-review, and completed verification
results (command, revision, environment, exit/result and limitations). Explicitly identify missing
required proof. This structural package does not certify semantic completeness or review readiness.

Confirm the reviewer's allowed read paths, shell/network access and report channel. Copy or attach
this package inside that access boundary; local sibling paths are not portable evidence. A read-only
reviewer consumes captured bounds/results and returns its report as text when it cannot write.
Full diff remains available; use the focused delta and affected obligations for a re-review.
EOF
inventory index.md "package navigation and limitations"
# Validate final artifacts, not just hashes recorded before later assembly steps.
verify_inventory "$PACKAGE_DIR" "$BASE_SHA" "$HEAD_SHA"
# Completion is written LAST, never to an interrupted round. It is a comparison binding, not
# approval: reuse requires identical head, range, context, policy and intact inventory.
comparison=unknown; [ -z "$BASE_REF" ] || comparison=known
manifest_oid="$(git -C "$REPO_ROOT" hash-object --no-filters "$PACKAGE_DIR/manifest.tsv")"
printf 'package5\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$CHANGE_ID" "$HEAD_SHA" "$BASE_REF" "$BASE_AT" "$POLICY_AT" "$comparison" "$reuse_context" "$BASE_SHA" "$manifest_oid" > "$PACKAGE_DIR/completed.pending"
mv "$PACKAGE_DIR/completed.pending" "$PACKAGE_DIR/completed.tsv"

printf '{"ROUND":%d,"BASE_SHA":"%s","HEAD_SHA":"%s","PACKAGE_DIR":"%s","REPORT_PATH":"%s","INDEX_PATH":"%s","SINCE_SHA":"%s"}\n' "$ROUND" "$BASE_SHA" "$HEAD_SHA" "$(json_escape "$PACKAGE_DIR")" "$(json_escape "$PACKAGE_DIR/report.md")" "$(json_escape "$PACKAGE_DIR/index.md")" "$SINCE"
