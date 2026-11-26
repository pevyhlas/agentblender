#!/usr/bin/env bash
#
# Safely tear down the isolated worktrees that new-change.sh creates
# under .worktrees/<change-id>.
#
# Why this exists: removing a worktree with `rm -rf` (tempting, because
# .worktrees/ is gitignored and looks like disposable scratch) leaves a stale
# "prunable" registration behind in git's worktree list. A harness/agent
# session that was operating inside that directory then fails to re-enter it on
# resume ("can't enter the worktree at startup"). This helper removes a
# finished change's worktree by moving its checkout and registration to quarantine
# and leaves unrelated registrations alone. With --closed it does so
# for every worktree whose change is closed on the trusted integration branch.
#
# Run it from the MAIN checkout, never from inside the worktree being removed.
#
# Invariants:
# A retirement moves the checkout (if present) and its registration into a reported
# quarantine slot. It must verify retained HEAD, reflog, index and submodule recovery
# data before reporting success. Without --force, a live checkout must pass the dirty check.
# --prune targets only stale registrations; --closed requires the kit's worktree layout
# and a missing record plus a log entry on a trusted integration ref.
# The evidence copy does not follow source symlinks. It refuses linked roots and
# linked or differing destinations it encounters when copying regular files.
#
# Threat model:
# Include overlapping cleanups and interruption: retirement locks the registration and
# preserves recovery data through cutover. The shared evidence copy is not serialized
# across registrations. No protection against a hostile writer in either checkout.

set -e

# shellcheck source=/dev/null
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/spec-lib.sh"

JSON_MODE=false
FORCE=false
PRUNE_ONLY=false
CLOSED_MODE=false
TARGET=""
USAGE="Usage: cleanup-change.sh [--json] [--force] (<change-id> | --prune)
       cleanup-change.sh [--json] --closed"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) JSON_MODE=true ;;
        --force) FORCE=true ;;
        --prune) PRUNE_ONLY=true ;;
        --closed) CLOSED_MODE=true ;;
        --help|-h)
            cat <<'EOF'
Usage: cleanup-change.sh [--json] [--force] (<change-id> | --prune)
       cleanup-change.sh [--json] --closed

Remove a finished change's isolated worktree, or clear stale registrations.

Options:
  --json        Output in JSON format
  --force       Remove even when the worktree has uncommitted or untracked
                changes. The checkout and its registration are quarantined.
  --prune       Only clear stale ("prunable") worktree registrations left behind
                when a worktree directory was deleted without this helper.
  --closed      Remove the worktree of every change that is closed on the trusted
                integration branch (origin/HEAD, plus any review-integration-branches
                in its committed config): its record specs/changes/<id>.md is gone
                and the log has its entry. Only a worktree laid out as new-change.sh
                creates it matches: the directory .worktrees/<id> of this checkout, on
                the branch <id>; a detached HEAD, another branch or another directory
                is left alone. Each worktree goes through the same safe
                removal as a single change id; a dirty, locked or otherwise refused
                worktree is reported and skipped, the run continues, and the exit
                status is 1 if any was refused. Never forces. Skips the main checkout
                and any worktree holding the current directory. Stale registrations
                are left for --prune. Refused when no trusted branch resolves (set
                origin/HEAD: git remote set-head origin --auto). Branches are never
                deleted; that stays your choice.
  --help, -h    Show this help message

Run from the MAIN checkout, not from inside the worktree being removed.
A worktree is removed non-forced by default: uncommitted or untracked changes
refuse the removal until they are committed, stashed or discarded. Committed
work is not checked for integration; the branch is never deleted. Ignored files
move with the checkout. Evidence, retrospectives and usage-enrichment inputs
are also copied to the main checkout. Archive and remove any legacy ledger
spool yourself before cleanup.

Cleanup moves the entire registration (including index, reflogs and submodule
stores) and any live checkout to the reported prdkit-retired directory. It
roots shared objects in refs/prdkit-retired/<slot>/ in the main repository.
Never clean a worktree in use. Stop writers and finish in-progress operations
first. Review the slot and retention refs before manually purging either.

Examples:
  cleanup-change.sh 007-user-auth      # remove that change's worktree
  cleanup-change.sh --closed           # remove every closed change's worktree
  cleanup-change.sh --prune            # clear stale registrations only
EOF
            exit 0
            ;;
        --*) echo "Error: unknown option: $1" >&2; exit 2 ;;
        *)
            if [ -n "$TARGET" ]; then
                echo "Error: only one change id may be given" >&2
                exit 2
            fi
            TARGET="$1"
            ;;
    esac
    shift
done

selected=0
[ "$PRUNE_ONLY" = true ] && selected=$((selected + 1))
[ "$CLOSED_MODE" = true ] && selected=$((selected + 1))
[ -z "$TARGET" ] || selected=$((selected + 1))
if [ "$selected" -gt 1 ]; then
    echo "Error: pass one of a change id, --prune or --closed" >&2
    exit 2
fi
if [ "$selected" -eq 0 ]; then
    echo "$USAGE" >&2
    exit 2
fi
if [ "$CLOSED_MODE" = true ] && [ "$FORCE" = true ]; then
    echo "Error: --closed never forces; remove a dirty worktree by id with --force" >&2
    exit 2
fi

if ! git rev-parse --show-toplevel >/dev/null 2>&1; then
    echo "[prdkit] Error: worktrees are a Git feature and no Git repository was detected." >&2
    exit 1
fi

ROOT="$(get_repo_root)"

# Physical path of the checkout this invocation is standing in. Compared against
# the removal target so we never pull the ground out from under ourselves.
CURRENT_TOP=""
if top="$(git rev-parse --show-toplevel 2>/dev/null)"; then
    CURRENT_TOP="$(CDPATH="" cd "$top" 2>/dev/null && pwd -P)" || CURRENT_TOP=""
fi
CWD_PHYS="$(pwd -P 2>/dev/null)" || CWD_PHYS=""

# Collect worktree registrations. For each we keep the path, the branch (if
# any), whether git considers it prunable (its directory is gone) and whether
# it is locked. The first registration is the main checkout.
STALE_PATHS=()
ENT_PATH=(); ENT_BRANCH=(); ENT_PRUNABLE=(); ENT_LOCKED=(); ENT_BARE=()
MATCH_PATH=""
MATCH_PRUNABLE=false
MATCH_LOCKED=false
KNOWN=()
REMOVED=(); SKIPPED_PATH=(); SKIPPED_WHY=()

cur_path=""; cur_branch=""; cur_prunable=false; cur_locked=false; cur_bare=false
flush_entry() {
    [ -n "$cur_path" ] || return 0
    KNOWN+=("$cur_path")
    ENT_PATH+=("$cur_path"); ENT_BRANCH+=("$cur_branch"); ENT_PRUNABLE+=("$cur_prunable")
    ENT_LOCKED+=("$cur_locked"); ENT_BARE+=("$cur_bare")
    if [ "$cur_prunable" = true ]; then
        STALE_PATHS+=("$cur_path")
    fi
    if [ -n "$TARGET" ]; then
        base="${cur_path##*/}"
        if [ "$cur_branch" = "$TARGET" ] || [ "$base" = "$TARGET" ]; then
            MATCH_PATH="$cur_path"
            MATCH_PRUNABLE="$cur_prunable"
            MATCH_LOCKED="$cur_locked"
        fi
    fi
    cur_path=""; cur_branch=""; cur_prunable=false; cur_locked=false; cur_bare=false
}

# Inspect raw gitdir pointers before using older Git's newline-delimited
# porcelain. A literal newline in a registered path can resemble another field.
common_dir="$(git -C "$ROOT" rev-parse --git-common-dir)"
common_dir="$(CDPATH='' cd "$ROOT" && CDPATH='' cd "$common_dir" && pwd -P)"
legacy_listing_safe() (
    local dir target
    case "$ROOT$common_dir" in *[[:cntrl:]]*) return 1 ;; esac
    if [ ! -e "$common_dir/worktrees" ]; then return 0; fi
    [ -r "$common_dir/worktrees" ] && [ -x "$common_dir/worktrees" ] || return 1
    shopt -s nullglob dotglob
    for dir in "$common_dir/worktrees"/*; do
        [ ! -L "$dir" ] && [ -f "$dir/gitdir" ] && [ ! -L "$dir/gitdir" ] &&
            target="$(cat "$dir/gitdir")" || return 1
        case "$target" in *[[:cntrl:]]*) return 1 ;; */.git) ;; *) return 1 ;; esac
    done
)
registrations="$(mktemp)"; delimiter=''
if ! git -C "$ROOT" worktree list --porcelain -z > "$registrations" 2>/dev/null; then
    delimiter=$'\n'
    if ! legacy_listing_safe || ! git -C "$ROOT" worktree list --porcelain > "$registrations"; then
        rm -f "$registrations"
        echo "[prdkit] Cannot inspect worktree registrations unambiguously." >&2
        exit 1
    fi
fi
while IFS= read -r -d "$delimiter" line; do
    case "$line" in
        "worktree "*) flush_entry; cur_path="${line#worktree }" ;;
        "branch refs/heads/"*) cur_branch="${line#branch refs/heads/}" ;;
        "prunable"*) cur_prunable=true ;;
        "locked"*) cur_locked=true ;;
        "bare") cur_bare=true ;;
    esac
done < "$registrations"
rm -f "$registrations"
flush_entry

emit_json() {
    # $1 result, $2 action, $3 message, then any number of path operands that
    # were removed/pruned.
    local result="$1" action="$2" message="$3"; shift 3
    local items="" p first=true
    for p in "$@"; do
        if [ "$first" = true ]; then first=false; else items="$items,"; fi
        items="$items\"$(json_escape "$p")\""
    done
    printf '{"result":"%s","action":"%s","message":"%s","paths":[%s]}\n' \
        "$result" "$action" "$(json_escape "$message")" "$items"
}

# Return the one registration pointing back to this checkout, including when
# its path no longer exists. Never derive an admin directory from the change ID.
registration() (
    local path="$1" dir target found=""
    [ -d "$common_dir/worktrees" ] && [ ! -L "$common_dir/worktrees" ] || return 1
    shopt -s nullglob dotglob
    for dir in "$common_dir/worktrees"/*; do
        [ -d "$dir" ] && [ ! -L "$dir" ] && [ -f "$dir/gitdir" ] &&
            [ ! -L "$dir/gitdir" ] && target="$(cat "$dir/gitdir")" || return 1
        case "$target" in
            /*) ;;
            */.git) target="$(git -C "$dir" --git-dir=. rev-parse --path-format=absolute --git-path "$target")" || return 1 ;;
            *) return 1 ;;
        esac
        [ "$target" = "$path/.git" ] || continue
        [ -z "$found" ] || return 1
        found="$dir"
    done
    [ -n "$found" ] && printf '%s\n' "$found"
)

# Validate relocation dependencies only. Never use reachability to authorize
# deletion. Inspect each repository's own metadata, not names inside its refs.
supported_layout() (
    local admin="$1" path="$2" list file repo
    list="$(mktemp)" || return 1
    trap 'rm -f "$list"' EXIT
    if [ -d "$path" ]; then
        [ ! -L "$path" ] && [ -f "$path/.git" ] && [ ! -L "$path/.git" ] &&
            [ "$(git -C "$path" rev-parse --absolute-git-dir)" = "$admin" ] || return 1
        find "$path" -iname .git -prune -print0 > "$list" || return 1
        while IFS= read -r -d '' file; do
            [ "$file" != "$path/.git" ] || continue
            [ -f "$file" ] && [ ! -L "$file" ] &&
                repo="$(git rev-parse --resolve-git-dir "$file")" &&
                repo="$(CDPATH='' cd -P "$repo" && pwd -P)" || return 1
            case "$repo" in "$admin/modules/"*) ;; *) return 1 ;; esac
            [ -d "$repo/objects" ] && [ -f "$repo/HEAD" ] || return 1
        done < "$list"
    fi
    [ ! -L "$admin/modules" ] || return 1
    [ -e "$admin/modules" ] || return 0
    [ -d "$admin/modules" ] &&
        find "$admin/modules" \( -type l -o -name HEAD \) -print0 > "$list" || return 1
    while IFS= read -r -d '' file; do
        [ ! -L "$file" ] || return 1
        repo="${file%/HEAD}"
        [ -d "$repo/objects" ] || [ -f "$repo/commondir" ] || continue
        [ ! -e "$repo/commondir" ] && [ ! -e "$repo/objects/info/alternates" ] || return 1
        if [ -e "$repo/worktrees" ]; then
            echo "[prdkit] $repo administers linked worktrees" >&2
            return 1
        fi
    done < "$list"
)

# Move whole directories instead of asking Git to recursively delete them.
# A private slot and a link across the two directories rule out mv's cross-
# device copy-and-delete fallback. Only our scratch probe/lock are unlinked.
same_mount() (
    local probe
    probe="$(mktemp "$1/.prdkit-device.XXXXXXXX")" || return 1
    trap 'rm -f "$probe"' EXIT
    ln "$probe" "$2/device" && rm "$2/device"
)
idle_index() ( # check read-only before moving anything, including on --force
    local admin="$1" p busy conflicts
    for p in "$admin"/{MERGE_HEAD,REBASE_HEAD,CHERRY_PICK_HEAD,REVERT_HEAD,BISECT_START,rebase-apply,rebase-merge,sequencer} \
        "$common_dir"/{gc.pid,index.lock,HEAD.lock} "$common_dir"/objects/{pack,info}/*.lock; do
        [ ! -e "$p" ] && [ ! -L "$p" ] || return 1
    done
    for p in "$admin" "$common_dir"; do
        busy="$(find "$p" \( -name prdkit-retired -o -name objects -o -name worktrees \) -type d -prune -o \
            -name '*.lock' ! -type d -print)" && [ -z "$busy" ] || return 1
    done
    [ -f "$admin/commondir" ] && [ ! -L "$admin/commondir" ] && [ ! -L "$admin/index" ] &&
        git --git-dir="$admin" rev-parse --verify --quiet 'HEAD^{commit}' >/dev/null &&
        conflicts="$(GIT_INDEX_FILE="$admin/index" git --git-dir="$admin" --work-tree="$ROOT" -c core.fsmonitor=false ls-files --unmerged)" && [ -z "$conflicts" ]
)

check_objects() { # Git exits zero for a missing batch item; check its response too.
    git --git-dir="$1" --work-tree="$ROOT" cat-file --batch-check='%(objecttype)' |
        LC_ALL=C awk '!/^(commit|tree|blob|tag)$/ {bad=1} END {exit bad}'
}

# Read only the linked worktree's ref table. In the normal Git view even
# --single-worktree --reflog includes the common branch reflogs. This read-only
# view uses Git's parser, with no shared refs or logs and no copied object DB.
table_log_objects() (
    local admin="$1" view="$2/table-view"; shift 2
    mkdir "$view" "$view/refs" || return 1
    trap 'rm -f "$view/HEAD" "$view/config" "$view/reftable"; rmdir "$view/refs" "$view"' EXIT
    cp "$admin/HEAD" "$view/HEAD" &&
        git config --file "$view/config" core.repositoryformatversion 1 &&
        git config --file "$view/config" extensions.refStorage reftable &&
        git config --file "$view/config" extensions.objectFormat "$(archived rev-parse --show-object-format)" &&
        ln -s "$admin/reftable" "$view/reftable" &&
        GIT_OBJECT_DIRECTORY="$common_dir/objects" git --git-dir="$view" rev-list \
            --single-worktree --reflog --objects --no-object-names "$@"
)

private_logs() (
    local admin="$1" slot="$2" head="$3" log; shift 3
    if [ -d "$admin/reftable" ]; then table_log_objects "$admin" "$slot" "$@"; return; fi
    [ -d "$admin/logs" ] || return 0
    find "$admin/logs" -type f -print0 > "$slot/log-list" || return 1
    while IFS= read -r -d '' log; do
        awk -v n="${#head}" '
            length($1)!=n || length($2)!=n || $1$2 ~ /[^0-9a-f]/ {exit 1}
            {if ($1 !~ /^0+$/) print $1; if ($2 !~ /^0+$/) print $2}' "$log" || return 1
    done < "$slot/log-list"
)

retain_refs() (
    set -o pipefail
    local slot="$1" admin="$2" phase="$3" ns="refs/prdkit-retired/${1##*/}" head tree index oid log repo
    [ "$phase" != before ] || ns="$ns/before"
    export GIT_NO_REPLACE_OBJECTS=1 GIT_NO_LAZY_FETCH=1 LC_ALL=C
    archived() { git --git-dir="$admin" --work-tree="$ROOT" "$@"; }
    head="$(archived rev-parse --verify 'HEAD^{commit}')" || return 1
    # write-tree may refresh the cache tree. Leave the archived index pristine.
    if [ -f "$admin/index" ]; then cp "$admin/index" "$slot/index-copy" || return 1; fi
    tree="$(GIT_INDEX_FILE="$slot/index-copy" archived write-tree)" || return 1
    rm -f "$slot/index-copy" || return 1
    index="$(archived -c user.name=Product-Kit -c user.email=retirement@localhost commit-tree "$tree" -p "$head" -m 'Retained worktree index')" || return 1
    archived for-each-ref --format='%(objectname)' refs/bisect refs/rewritten refs/worktree > "$slot/oids" || return 1
    if [ -f "$admin/ORIG_HEAD" ]; then archived rev-parse --verify ORIG_HEAD >> "$slot/oids" || return 1; fi
    # Include non-commit endpoints too. Do not turn every descendant of a
    # logged commit into a separate ref; Git walks its tree during verification.
    private_logs "$admin" "$slot" "$head" --no-walk --filter=tree:0 >> "$slot/oids" || return 1
    # Git before 2.37.2 omitted resolve-undo blobs from --indexed-objects.
    # ls-files quotes unusual filenames, leaving the mode and OID fields intact.
    archived ls-files --resolve-undo | awk '$1!="160000" {print $2}' > "$slot/undo" || return 1
    cat "$slot/undo" >> "$slot/oids" || return 1
    # Protect every object Git marks from the index but omits from its tree
    # (e.g. add -N's empty blob), without creating a ref per normal entry.
    archived rev-list --single-worktree --objects --no-object-names --indexed-objects "^$tree" >> "$slot/oids" || return 1
    LC_ALL=C sort -u "$slot/oids" > "$slot/unique" || return 1
    {
        printf 'create %s/HEAD %s\ncreate %s/index %s\n' "$ns" "$head" "$ns" "$index"
        while IFS= read -r oid; do printf 'create %s/objects/%s %s\n' "$ns" "$oid" "$oid"; done < "$slot/unique"
    } | git -C "$ROOT" update-ref --stdin || return 1
    [ "$phase" != before ] || return 0
    # Inspect through the published refs. A gitlink refers to a different
    # repository; verify populated module stores against their own HEAD below.
    printf '%s\n' "$ns/HEAD^{commit}" "$ns/index^{tree}" > "$slot/check" || return 1
    while IFS= read -r oid; do printf '%s/objects/%s\n' "$ns" "$oid"; done < "$slot/unique" >> "$slot/check" || return 1
    git -C "$ROOT" ls-tree -r "$ns/index" | awk '$1!="160000" {print $3}' >> "$slot/check" || return 1
    check_objects "$common_dir" < "$slot/check" || return 1
    # Independently compare what Git would keep for the retired worktree with
    # the closure of *only* this slot's published refs, not mere DB presence.
    private_logs "$admin" "$slot" "$head" > "$slot/roots" || return 1
    if [ -f "$admin/ORIG_HEAD" ]; then archived rev-parse --verify ORIG_HEAD >> "$slot/roots" || return 1; fi
    archived rev-list --stdin --single-worktree --objects --no-object-names --indexed-objects \
        --glob=refs/worktree --glob=refs/rewritten --glob=refs/bisect HEAD < "$slot/roots" > "$slot/expected" || return 1
    sort -u "$slot/expected" "$slot/undo" > "$slot/required" || return 1
    git -C "$ROOT" for-each-ref --format='%(objectname)' "$ns/" |
        git -C "$ROOT" rev-list --stdin --objects --no-object-names | sort -u > "$slot/reachable" || return 1
    # Merge the two sorted inventories without loading a repository's history
    # into memory. Prefix comparisons so even all-digit OIDs compare as text.
    awk 'BEGIN {kept=ARGV[1]; ARGV[1]=""}
        {while (!ended && ("x" oid) < ("x" $0)) {
            rc=(getline oid < kept); if (rc<0) exit 1; if (!rc) ended=1
        }
        if (("x" oid) != ("x" $0)) print}' "$slot/reachable" "$slot/required" > "$slot/missing" || return 1
    if [ -s "$slot/missing" ]; then echo "unretained objects listed at $slot/missing" >&2; return 1; fi
    if [ -d "$admin/modules" ]; then
        find "$admin/modules" -name HEAD -type f -print0 > "$slot/log-list" || return 1
        while IFS= read -r -d '' log; do
            repo="${log%/HEAD}"; [ -d "$repo/objects" ] || continue
            printf 'HEAD^{commit}\n' | check_objects "$repo" || return 1
        done < "$slot/log-list"
    fi
    rm -f "$slot"/{oids,unique,log-list,check,undo,expected,required,reachable,missing,roots}
)
retire() (
    local path="$1" mode="$2" admin root="$common_dir/prdkit-retired" slot holding state=0 lock config_rc=0
    admin="$(registration "$path")" || { echo "ambiguous worktree registration: $path"; return 1; }
    lock="$admin/prdkit-cleanup.lock"
    if [ "$mode" != preflight ]; then
        mkdir "$lock" 2>/dev/null || { echo "another cleanup-change.sh run holds $lock"; return 1; }
        trap 'rmdir "$lock" 2>/dev/null || :' EXIT
    elif [ -e "$lock" ]; then echo "another cleanup-change.sh run holds $lock"; return 1; fi
    if [ -e "$admin/locked" ]; then echo "cannot remove a locked working tree: $(cat "$admin/locked")"; return 1; fi
    [ ! -L "$path" ] || return 1
    supported_layout "$admin" "$path" || { echo "unsupported submodule layout: $path"; return 1; }
    case "$mode" in
        stale|preflight) [ ! -e "$path" ] || { echo "checkout is no longer missing"; return 1; } ;;
        *) [ -d "$path" ] || return 1
           if [ "$FORCE" = false ]; then
               wt_dirty || state=$?
               [ "$state" -eq 1 ] || { echo "cannot confirm worktree is still clean"; return 1; }
           fi ;;
    esac
    # Retention refs must protect objects in the common repository itself.
    [ -z "${GIT_OBJECT_DIRECTORY:-}${GIT_ALTERNATE_OBJECT_DIRECTORIES:-}${GIT_COMMON_DIR:-}${GIT_SHALLOW_FILE:-}${GIT_INDEX_FILE:-}" ] &&
        [ "$(git -C "$ROOT" rev-parse --is-shallow-repository)" = false ] &&
        [ ! -e "$common_dir/objects/info/alternates" ] && [ ! -L "$common_dir/objects/info/alternates" ] ||
        { echo "unsupported shared object store"; return 1; }
    git -C "$ROOT" config --get-regexp '^(extensions\.partialclone|remote\..*\.(promisor|partialclonefilter))$' >/dev/null || config_rc=$?
    [ "$config_rc" = 1 ] || { echo "cannot verify complete local object storage"; return 1; }
    case "$root/" in "$path"/*) return 1 ;; esac
    idle_index "$admin" || { echo "registration is busy, unreadable, has no committed HEAD or has an unmerged index"; return 1; }
    if ! git check-ref-format "refs/prdkit-retired/${admin##*/}.slot/HEAD" || [ -L "$root" ]; then
        echo "unsupported quarantine location"; return 1
    fi
    [ "$mode" != preflight ] || return 0
    [ ! -L "$root" ] && mkdir -p "$root" &&
        slot="$(mktemp -d "$root/${admin##*/}.$(date -u +%Y%m%dT%H%M%SZ).XXXXXXXX")" || return 1
    printf '[prdkit] Quarantine: %s\n' "$slot" >&2
    if ! same_mount "$admin" "$slot" || { [ "$mode" != stale ] && ! same_mount "$path" "$slot"; }; then
        echo "cannot guarantee a same-filesystem move to $slot"; return 1
    fi
    if [ "$mode" = live ] && ! verify_preserved; then
        echo "could not preserve its evidence: destination identity changed"; return 1
    fi
    # Keep native Git from pruning the registration while the checkout is
    # absent. An interrupted partial retirement stays locked for recovery.
    printf 'Product-Kit retirement: %s\n' "$slot" > "$slot/lock-reason" &&
        ln "$slot/lock-reason" "$admin/locked" && [ ! -L "$admin/locked" ] &&
        [ "$admin/locked" -ef "$slot/lock-reason" ] || { echo "cannot lock registration"; return 1; }
    if [ "$mode" != stale ]; then
        mv "$path" "$slot/checkout" || { echo "checkout move failed; inspect locked registration $admin and $slot"; return 1; }
    fi
    # Keep the real common store resolvable on either side of the rename.
    if ! printf '%s\n' "$common_dir" > "$slot/common-path" || ! mv "$slot/common-path" "$admin/commondir"; then
        echo "could not bind registration $admin to the common store; inspect $slot"; return 1
    fi
    # Publish before cutover. Move first to a locked, Git-visible sibling so
    # even a writer finishing at that rename is still rooted if interrupted.
    holding="$common_dir/worktrees/${slot##*/}.retiring"
    if [ -e "$holding" ] || [ -L "$holding" ] || ! retain_refs "$slot" "$admin" before; then
        echo "initial retention failed; locked registration $admin; inspect $slot"; return 1
    fi
    mv "$admin" "$holding" || { echo "registration move failed: $slot"; return 1; }
    lock="$holding/prdkit-cleanup.lock"
    if ! retain_refs "$slot" "$holding" after; then
        echo "retention verification failed; locked registration $holding; inspect $slot"; return 1
    fi
    mv "$holding" "$slot/admin" || { echo "final move failed; locked registration $holding; inspect $slot"; return 1; }
    lock="$slot/admin/prdkit-cleanup.lock"
    printf '%s\n' "$slot"
)
prune_stale() {
    local path slot msg slots="" retired=()
    for path in "$@"; do
        if ! msg="$(retire "$path" preflight)"; then
            msg="Cannot prune $path: $msg"
            if $JSON_MODE; then emit_json refused prune "$msg"; else echo "[prdkit] $msg" >&2; fi
            return 1
        fi
    done
    for path in "$@"; do
        if ! slot="$(retire "$path" stale)"; then
            msg="Could not finish stale registration $path: $slot.${slots:+ Previously retired:$slots}"
            if $JSON_MODE; then emit_json refused prune "$msg" ${retired[@]+"${retired[@]}"}; else echo "[prdkit] $msg" >&2; fi
            return 1
        fi
        slots="$slots $slot"
        retired+=("$path")
    done
    msg="Pruned $# stale worktree registration(s) whose directories were already gone.${slots:+ Quarantine:$slots}"
    if $JSON_MODE; then emit_json ok prune "$msg" "$@"; else echo "[prdkit] $msg"; fi
}

if [ "$PRUNE_ONLY" = true ]; then
    prune_stale "${STALE_PATHS[@]}"
    exit 0
fi

ROOT_TOP="$(CDPATH="" cd "$ROOT" 2>/dev/null && pwd -P)" || ROOT_TOP="$ROOT"

# --- Remove one worktree, safely ---------------------------------------------
# remove_worktree <path> <label> <locked> sets RESULT_REASON (short) and RESULT_MSG (full) and
# returns 0 when the worktree was removed, 1 when it was refused. It never exits and prints nothing.

# Cleanliness: 0 has changes, 1 clean, 2 git could not say.
WT=""
wt_dirty() {
    local out
    out="$(git -C "$WT" -c core.fsmonitor=false -c core.hooksPath=/dev/null status --porcelain --untracked-files=all 2>/dev/null)" || return 2
    [ -n "$out" ]
}

# Review packages, check outputs, retrospectives and usage-enrichment inputs live
# in the worktree's gitignored .prdkit/cache/ and .prdkit/retrospective/, which
# `git worktree remove` deletes without counting them as dirty. Copy every evidence, retrospective
# and enrichment file into this checkout's .prdkit/ and verify it first; a copy that fails or
# would overwrite a different file refuses the removal.
PRESERVED=0; CONFLICT=""
unlinked_path() { # absolute child and trusted checkout root
    local p="$1"
    while [ "$p" != "$2" ]; do
        case "$p" in "$2"/*) ;; *) return 1 ;; esac
        [ ! -L "$p" ] || { CONFLICT="$p"; return 1; }
        p="${p%/*}"
    done
}
file_identity() { # physical device:inode, GNU or BSD stat
    stat -c '%d:%i' "$1" 2>/dev/null || stat -f '%d:%i' "$1"
}
verify_preserved() {
    local i dest
    for i in ${preserved_paths[@]+"${!preserved_paths[@]}"}; do
        dest="${preserved_paths[i]}"
        unlinked_path "$dest" "$ROOT" && [ -f "$dest" ] &&
            [ "$(file_identity "${dest%/*}")/$(file_identity "$dest")" = "${preserved_ids[i]}" ] || return 1
    done
}
preserve_file() ( # source destination; work beside the destination on its filesystem
    local file="$1" dest="$2" dir="${2%/*}" name="${2##*/}" stage parent_id stage_id published
    mkdir -p "$dir" && unlinked_path "$dir" "$ROOT" || return 1
    cd -P "$dir" && [ "$(pwd -P)" = "$dir" ] || return 1
    parent_id="$(file_identity .)" || return 1
    stage="$(mktemp -d .prdkit-evidence.XXXXXXXX)" || return 1
    stage_id="$(file_identity "$stage")" && cd -P "$stage" &&
        [ "$(pwd -P)" = "$dir/$stage" ] && [ "$(file_identity .)" = "$stage_id" ] || return 1
    trap 'rm -f "./$name"; if unlinked_path "$dir/$stage" "$ROOT" && [ "$dir/$stage" -ef . ]; then rmdir "$dir/$stage"; fi' EXIT
    cp -p "$file" "./$name" && cmp -s "$file" "./$name" || return 1
    unlinked_path "$dest" "$ROOT" && [ "$dir/$stage" -ef . ] && [ "$dir" -ef .. ] &&
        [ "$(file_identity ..)" = "$parent_id" ] || return 1
    # Use the destination directory as the ln operand. An existing leaf, even an
    # actual directory or symlink, cannot redirect the link to another name.
    if ln "./$name" .. 2>/dev/null; then
        [ ! -L "../$name" ] && [ "../$name" -ef "./$name" ] || return 1
    fi
    # No replacement on publication. If someone else won, accept only the same bytes.
    [ -f "../$name" ] && [ ! -L "../$name" ] && published="$(file_identity "../$name")" &&
        cmp -s "./$name" "../$name" && cmp -s "$file" "../$name" &&
        unlinked_path "$dest" "$ROOT" && [ "$dir" -ef .. ] && [ "$(file_identity "$dest")" = "$published" ] &&
        printf '%s/%s\n' "$parent_id" "$published"
)
preserve() { # path relative to .prdkit/ (a directory or a file)
    local src="$WT/.prdkit/$1" dest="$ROOT/.prdkit/$1" file rel list rc=0 published target
    unlinked_path "$src" "$WT" && unlinked_path "$dest" "$ROOT" || return 1
    [ -e "$src" ] || return 0
    list="$(mktemp)" || return 1
    if ! find -P "$src" -type f -print0 > "$list"; then rm -f "$list"; return 1; fi
    while IFS= read -r -d '' file; do
        rel="${file#"$src"}"
        target="$dest$rel"
        if ! unlinked_path "$file" "$WT" || ! unlinked_path "$dest$rel" "$ROOT"; then rc=1; break; fi
        if [ -e "$dest$rel" ]; then
            if [ ! -f "$target" ] || [ -L "$target" ] ||
                ! published="$(file_identity "${target%/*}")" || ! published="$published/$(file_identity "$target")" ||
                ! cmp -s "$file" "$target"; then
                CONFLICT="$dest$rel"; rc=1; break
            fi
        else
            published="$(preserve_file "$file" "$target")" || { CONFLICT="$target"; rc=1; break; }
            PRESERVED=$((PRESERVED + 1))
        fi
        preserved_paths+=("$target"); preserved_ids+=("$published")
    done < "$list"
    rm -f "$list"
    return "$rc"
}

# One line from git's own words: no "fatal:" prefix, no line breaks.
flatten_git_error() {
    local e="${1#fatal: }"
    e="$(printf '%s' "$e" | tr '\n' ' ')"
    printf '%s' "${e:-unknown git error}"
}

refuse_with() { # short reason, full message
    RESULT_REASON="$1"; RESULT_MSG="$2"   # callers return 1 after it
}
refuse_dirty() {
    refuse_with "uncommitted or untracked changes" \
        "Refusing to remove $WT: it has uncommitted or untracked changes. Commit, stash or discard them first, or pass --force to discard them."
}
refuse_git() { # git's reason
    local why; why="$(flatten_git_error "$1")"
    refuse_with "git refused the removal: $why" \
        "Refusing to remove $WT: git refused the removal, and the worktree itself is clean. Git's reason: $why"
}

remove_worktree() { # path label locked
    local label="$2" locked="$3" state admin slot
    local preserved_paths=() preserved_ids=()
    WT="$1"; PRESERVED=0; CONFLICT=""
    admin="$(registration "$WT")" || { refuse_git "cannot resolve worktree registration"; return 1; }
    if [ -d "$admin/prdkit-cleanup.lock" ]; then refuse_git "another cleanup-change.sh run holds $admin/prdkit-cleanup.lock"; return 1; fi
    if [ "$locked" = true ]; then
        refuse_git "cannot remove a locked working tree, lock reason: $(cat "$admin/locked")"; return 1
    fi
    # Refuse before copying anything, so a refused removal leaves this checkout's cache untouched.
    if ! supported_layout "$admin" "$WT"; then refuse_git "unsupported submodule layout: $WT"; return 1; fi
    if [ "$FORCE" = false ]; then
        state=0; wt_dirty || state=$?
        case "$state" in
            0) refuse_dirty; return 1 ;;
            2) refuse_git "git status failed in the worktree"; return 1 ;;
        esac
    fi

    if [ -L "$WT/.prdkit" ] || [ -L "$WT/.prdkit/ledger" ] ||
        [ -L "$WT/.prdkit/ledger/events.jsonl" ] || [ -e "$WT/.prdkit/ledger/events.jsonl" ]; then
        refuse_with "legacy ledger spool or linked ledger path" \
            "Refusing to remove $WT: archive the legacy ledger spool if needed, then remove it or the linked ledger path before retrying."
        return 1
    fi
    if ! preserve cache/evidence || ! preserve cache/retrospectives || ! preserve retrospective; then
        refuse_with "could not preserve its evidence at $CONFLICT" \
            "Refusing to remove $WT: could not preserve its evidence at $CONFLICT (a different file is already there, or the copy failed). Resolve it, then rerun."
        return 1
    fi

    if ! slot="$(retire "$WT" live)"; then
        refuse_with "retirement incomplete: $slot" "Worktree retirement did not complete for $WT: $slot"
        return 1
    fi
    RESULT_REASON="removed"
    RESULT_MSG="Removed worktree for '$label': $WT; quarantine: $slot"
    [ "$PRESERVED" -eq 0 ] || RESULT_MSG="$RESULT_MSG; preserved $PRESERVED evidence file(s) in $ROOT/.prdkit/"
    return 0
}

# --- Remove every closed change's worktree ---------------------------------

# Whether <id> is closed on a trusted ref: its record specs/changes/<id>.md is gone there and
# the log has its entry, .prdkit/history/log/<id>.md as close-change.sh writes it or a line in .prdkit/history/log.md
# ("- <date> <id>: ..."). Both facts are read from the trusted ref, never from the worktree's own
# branch.
CLOSED_REFS=""
closed_on_trusted() { # id
    local id="$1" ref
    while IFS= read -r ref; do
        [ -n "$ref" ] || continue
        GIT_NO_LAZY_FETCH=1 git -C "$ROOT" rev-parse --verify --quiet "$ref^{commit}" >/dev/null 2>&1 || continue
        if GIT_NO_LAZY_FETCH=1 git -C "$ROOT" cat-file -e "$ref:specs/changes/$id.md" 2>/dev/null; then continue; fi
        if spec_log_entries_at "$ROOT" "$ref" |
            sed -n 's/^- [0-9-]* \([^ :]*\):.*/\1/p' | grep -Fqx -- "$id"; then
            return 0
        fi
    done <<EOF
$CLOSED_REFS
EOF
    return 1
}

# A worktree matches only as new-change.sh lays it out: the directory .worktrees/<id> of this
# checkout, on the branch <id>, and <id> is closed. Anything else is left alone: a detached HEAD
# (it can hold commits no ref reaches, which removing the worktree would lose), another branch, or
# a directory that merely carries a closed id's name somewhere else.
WORKTREES_TOP=""
worktree_closed() { # branch path
    local branch="$1" path="$2" parent
    [ -n "$WORKTREES_TOP" ] && [ -n "$branch" ] || return 1
    is_change_id "$branch" || return 1
    [ "${path##*/}" = "$branch" ] || return 1
    parent="$(CDPATH="" cd "${path%/*}" 2>/dev/null && pwd -P)" || return 1
    [ "$parent" = "$WORKTREES_TOP" ] || return 1
    closed_on_trusted "$branch"
}

run_closed() {
    local i path branch locked top msg result removed_n=0 skipped_n=0 open_n=0 failed=0
    REMOVED=(); SKIPPED_PATH=(); SKIPPED_WHY=()
    CLOSED_REFS="$(spec_trusted_refs "$ROOT")"
    if [ -z "$CLOSED_REFS" ]; then
        msg="Refusing --closed: no trusted integration branch resolves, so no change can be called closed. Set origin/HEAD (git remote set-head origin --auto) and fetch, then rerun."
        if $JSON_MODE; then emit_json refused closed "$msg"; else >&2 echo "[prdkit] $msg"; fi
        exit 1
    fi
    WORKTREES_TOP="$(CDPATH="" cd "$ROOT/.worktrees" 2>/dev/null && pwd -P)" || WORKTREES_TOP=""

    # Entry 0 is the main checkout.
    for ((i = 1; i < ${#ENT_PATH[@]}; i++)); do
        path="${ENT_PATH[$i]}"; branch="${ENT_BRANCH[$i]}"; locked="${ENT_LOCKED[$i]}"
        # A bare repository or a registration whose directory is gone is no candidate.
        if [ "${ENT_BARE[$i]}" = true ] || [ "${ENT_PRUNABLE[$i]}" = true ]; then continue; fi
        top="$(CDPATH="" cd "$path" 2>/dev/null && pwd -P)" || top="$path"
        [ "$top" != "$ROOT_TOP" ] || continue
        if ! worktree_closed "$branch" "$path"; then
            open_n=$((open_n + 1)); continue
        fi
        case "$CWD_PHYS/" in
            "$top"/*)
                skipped_n=$((skipped_n + 1)); SKIPPED_PATH+=("$path"); SKIPPED_WHY+=("it holds the current directory; run this from the main checkout")
                continue ;;
        esac
        if remove_worktree "$path" "${branch:-${path##*/}}" "$locked"; then
            removed_n=$((removed_n + 1)); REMOVED+=("$path")
            $JSON_MODE || echo "[prdkit] $RESULT_MSG"
        else
            failed=1; skipped_n=$((skipped_n + 1)); SKIPPED_PATH+=("$path"); SKIPPED_WHY+=("$RESULT_REASON")
        fi
    done

    msg="Removed $removed_n closed change worktree(s), skipped $skipped_n, left $open_n for open or unrelated work."
    if $JSON_MODE; then
        local skipped_json="" sep=""
        i=0
        while [ "$i" -lt "${#SKIPPED_PATH[@]}" ]; do
            skipped_json="$skipped_json$sep{\"path\":\"$(json_escape "${SKIPPED_PATH[$i]}")\",\"reason\":\"$(json_escape "${SKIPPED_WHY[$i]}")\"}"
            sep=","; i=$((i + 1))
        done
        result=ok; [ "$failed" -eq 0 ] || result=refused
        printf '{"result":"%s","action":"closed","message":"%s","paths":%s,"skipped":[%s],"pruned":[]}\n' \
            "$result" "$(json_escape "$msg")" "$(json_array ${REMOVED[@]+"${REMOVED[@]}"})" "$skipped_json"
    else
        echo "[prdkit] $msg"
        if [ "${#SKIPPED_PATH[@]}" -gt 0 ]; then
            >&2 echo "[prdkit] Skipped:"
            i=0
            while [ "$i" -lt "${#SKIPPED_PATH[@]}" ]; do
                >&2 echo "  ${SKIPPED_PATH[$i]}: ${SKIPPED_WHY[$i]}"; i=$((i + 1))
            done
        fi
    fi
    exit "$failed"
}

if [ "$CLOSED_MODE" = true ]; then
    run_closed
fi

# --- Remove a specific change's worktree ------------------------------------

if [ -z "$MATCH_PATH" ]; then
    msg="No worktree found for '$TARGET'."
    if $JSON_MODE; then
        emit_json error remove "$msg"
    else
        >&2 echo "[prdkit] $msg"
        if [ "${#KNOWN[@]}" -gt 1 ]; then
            >&2 echo "[prdkit] Known worktrees:"
            for p in "${KNOWN[@]}"; do >&2 echo "  $p"; done
        fi
    fi
    exit 1
fi

# A prunable match means the directory is already gone; just drop the registration.
if [ "$MATCH_PRUNABLE" = true ]; then
    prune_stale "$MATCH_PATH"
    exit 0
fi

MATCH_TOP="$(CDPATH="" cd "$MATCH_PATH" 2>/dev/null && pwd -P)" || MATCH_TOP="$MATCH_PATH"
if [ -n "$CURRENT_TOP" ] && [ "$CURRENT_TOP" = "$MATCH_TOP" ]; then
    msg="Refusing to remove the worktree you are currently inside ($MATCH_PATH). Run this from the main checkout."
    if $JSON_MODE; then emit_json refused remove "$msg"; else >&2 echo "[prdkit] $msg"; fi
    exit 1
fi

if [ "$ROOT_TOP" = "$MATCH_TOP" ]; then
    msg="Refusing to remove $MATCH_PATH with its own copy of this script: run the main checkout's .prdkit/scripts/bash/cleanup-change.sh, which keeps the worktree's evidence."
    if $JSON_MODE; then emit_json refused remove "$msg"; else >&2 echo "[prdkit] $msg"; fi
    exit 1
fi

if remove_worktree "$MATCH_PATH" "$TARGET" "$MATCH_LOCKED"; then
    if $JSON_MODE; then emit_json ok remove "$RESULT_MSG" "$MATCH_PATH"; else echo "[prdkit] $RESULT_MSG"; fi
    exit 0
fi
if $JSON_MODE; then emit_json refused remove "$RESULT_MSG"; else >&2 echo "[prdkit] $RESULT_MSG"; fi
exit 1
