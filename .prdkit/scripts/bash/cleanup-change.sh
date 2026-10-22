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
# finished change's worktree the safe way (git worktree remove, non-forced)
# and prunes stale registrations so nothing dangles.
#
# Run it from the MAIN checkout, never from inside the worktree being removed.

set -e

# shellcheck source=/dev/null
source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

JSON_MODE=false
FORCE=false
PRUNE_ONLY=false
TARGET=""

while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) JSON_MODE=true ;;
        --force) FORCE=true ;;
        --prune) PRUNE_ONLY=true ;;
        --help|-h)
            cat <<'EOF'
Usage: cleanup-change.sh [--json] [--force] (<change-id> | --prune)

Remove a finished change's isolated worktree, or clear stale registrations.

Options:
  --json        Output in JSON format
  --force       Remove even when the worktree has uncommitted or unintegrated
                work (git worktree remove --force). Discards that work.
  --prune       Only clear stale ("prunable") worktree registrations left behind
                when a worktree directory was deleted without this helper.
  --help, -h    Show this help message

Run from the MAIN checkout, not from inside the worktree being removed.
A worktree is removed non-forced by default, so uncommitted or unintegrated
work is preserved and the removal is refused until it is dealt with.

Examples:
  cleanup-change.sh 007-user-auth      # remove that change's worktree
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

if [ "$PRUNE_ONLY" = true ] && [ -n "$TARGET" ]; then
    echo "Error: pass either a change id or --prune, not both" >&2
    exit 2
fi
if [ "$PRUNE_ONLY" = false ] && [ -z "$TARGET" ]; then
    echo "Usage: cleanup-change.sh [--json] [--force] (<change-id> | --prune)" >&2
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

# Collect worktree registrations. For each we keep the path, the branch (if
# any), and whether git considers it prunable (its directory is gone).
STALE_PATHS=()
MATCH_PATH=""
MATCH_PRUNABLE=false
KNOWN=()

cur_path=""; cur_branch=""; cur_prunable=false
flush_entry() {
    [ -n "$cur_path" ] || return 0
    KNOWN+=("$cur_path")
    if [ "$cur_prunable" = true ]; then
        STALE_PATHS+=("$cur_path")
    fi
    if [ -n "$TARGET" ]; then
        base="${cur_path##*/}"
        if [ "$cur_branch" = "$TARGET" ] || [ "$base" = "$TARGET" ]; then
            MATCH_PATH="$cur_path"
            MATCH_PRUNABLE="$cur_prunable"
        fi
    fi
    cur_path=""; cur_branch=""; cur_prunable=false
}

while IFS= read -r line; do
    case "$line" in
        "worktree "*) flush_entry; cur_path="${line#worktree }" ;;
        "branch refs/heads/"*) cur_branch="${line#branch refs/heads/}" ;;
        "prunable"*) cur_prunable=true ;;
    esac
done < <(git -C "$ROOT" worktree list --porcelain 2>/dev/null || true)
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

prune_stale() {
    # Clear registrations whose directories are gone. Reports the paths git drops.
    git -C "$ROOT" worktree prune >/dev/null 2>&1 || true
    if [ "${#STALE_PATHS[@]}" -eq 0 ]; then
        if $JSON_MODE; then
            emit_json ok prune "No stale worktree registrations to prune."
        else
            echo "[prdkit] No stale worktree registrations to prune."
        fi
        return 0
    fi
    if $JSON_MODE; then
        emit_json ok prune "Pruned ${#STALE_PATHS[@]} stale worktree registration(s)." "${STALE_PATHS[@]}"
    else
        echo "[prdkit] Pruned ${#STALE_PATHS[@]} stale worktree registration(s):"
        for p in "${STALE_PATHS[@]}"; do echo "  $p"; done
    fi
}

if [ "$PRUNE_ONLY" = true ]; then
    prune_stale
    exit 0
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
    git -C "$ROOT" worktree prune >/dev/null 2>&1 || true
    msg="Worktree for '$TARGET' was already gone; pruned its stale registration."
    if $JSON_MODE; then emit_json ok prune "$msg" "$MATCH_PATH"; else echo "[prdkit] $msg"; fi
    exit 0
fi

MATCH_TOP="$(CDPATH="" cd "$MATCH_PATH" 2>/dev/null && pwd -P)" || MATCH_TOP="$MATCH_PATH"
if [ -n "$CURRENT_TOP" ] && [ "$CURRENT_TOP" = "$MATCH_TOP" ]; then
    msg="Refusing to remove the worktree you are currently inside ($MATCH_PATH). Run this from the main checkout."
    if $JSON_MODE; then emit_json refused remove "$msg"; else >&2 echo "[prdkit] $msg"; fi
    exit 1
fi

ROOT_TOP="$(CDPATH="" cd "$ROOT" 2>/dev/null && pwd -P)" || ROOT_TOP="$ROOT"
if [ "$ROOT_TOP" = "$MATCH_TOP" ]; then
    msg="Refusing to remove $MATCH_PATH with its own copy of this script: run the main checkout's .prdkit/scripts/bash/cleanup-change.sh, which keeps the worktree's evidence."
    if $JSON_MODE; then emit_json refused remove "$msg"; else >&2 echo "[prdkit] $msg"; fi
    exit 1
fi

refuse_dirty() { # git's reason
    msg="Refusing to remove $MATCH_PATH: it has uncommitted or unintegrated work. Integrate or commit it first, or pass --force to discard it."
    if $JSON_MODE; then
        emit_json refused remove "$msg"
    else
        >&2 echo "[prdkit] $msg"
        >&2 echo "[prdkit]   ${1:-unknown git error}"
    fi
    exit 1
}
# Refuse before copying anything, so a refused removal leaves this checkout's cache untouched.
if [ "$FORCE" = false ] && [ -n "$(git -C "$MATCH_PATH" status --porcelain --untracked-files=all 2>/dev/null)" ]; then
    refuse_dirty "uncommitted or untracked files in the worktree (git status)"
fi

# Review packages, check outputs, retrospectives and the ledger spool live in the worktree's
# gitignored .prdkit/cache/ and .prdkit/ledger/, which `git worktree remove` deletes without
# counting them as dirty. Copy every evidence and retrospective file into this checkout's cache
# and verify it first; a copy that fails or would overwrite a different file refuses the removal.
# The worktree's ledger events are appended to this checkout's spool, skipping lines it has.
PRESERVED=0; EVENTS=0; CONFLICT=""
preserve() { # path relative to .prdkit/cache/ (a directory or a file)
    local src="$MATCH_PATH/.prdkit/cache/$1" dest="$ROOT/.prdkit/cache/$1" file rel
    [ -e "$src" ] || return 0
    while IFS= read -r -d '' file; do
        rel="${file#"$src"}"
        if [ -e "$dest$rel" ]; then
            cmp -s "$file" "$dest$rel" || { CONFLICT="$dest$rel"; return 1; }
        else
            if ! { mkdir -p "$(dirname "$dest$rel")" && cp -p "$file" "$dest$rel" && cmp -s "$file" "$dest$rel"; }; then
                CONFLICT="$dest$rel"; return 1
            fi
            PRESERVED=$((PRESERVED + 1))
        fi
    done < <(find "$src" -type f -print0)
}
merge_events() {
    local src="$MATCH_PATH/.prdkit/ledger/events.jsonl" dest="$ROOT/.prdkit/ledger/events.jsonl" new
    [ -s "$src" ] || return 0
    CONFLICT="$dest"
    mkdir -p "$(dirname "$dest")" && touch "$dest" || return 1
    # FILENAME, not NR == FNR: with an empty spool here, NR == FNR would hold through the worktree's.
    new="$(awk 'FILENAME == ARGV[1] { seen[$0] = 1; next } !seen[$0]++' "$dest" "$src")" || return 1
    [ -n "$new" ] || return 0
    printf '%s\n' "$new" >> "$dest" || return 1
    EVENTS="$(printf '%s\n' "$new" | wc -l | tr -d ' ')"
}
if ! preserve evidence || ! preserve retrospectives || ! merge_events; then
    msg="Refusing to remove $MATCH_PATH: could not preserve its evidence at $CONFLICT (a different file is already there, or the copy failed). Resolve it, then rerun."
    if $JSON_MODE; then emit_json refused remove "$msg"; else >&2 echo "[prdkit] $msg"; fi
    exit 1
fi

REMOVE_ARGS=("$MATCH_PATH")
if [ "$FORCE" = true ]; then REMOVE_ARGS=(--force "$MATCH_PATH"); fi

if RM_ERR="$(git -C "$ROOT" worktree remove "${REMOVE_ARGS[@]}" 2>&1 >/dev/null)"; then
    git -C "$ROOT" worktree prune >/dev/null 2>&1 || true
    msg="Removed worktree for '$TARGET': $MATCH_PATH"
    [ "$PRESERVED" -eq 0 ] || msg="$msg; preserved $PRESERVED evidence file(s) in $ROOT/.prdkit/cache/"
    [ "$EVENTS" -eq 0 ] || msg="$msg; appended $EVENTS ledger event(s) to $ROOT/.prdkit/ledger/events.jsonl"
    if $JSON_MODE; then emit_json ok remove "$msg" "$MATCH_PATH"; else echo "[prdkit] $msg"; fi
    exit 0
fi

# Non-forced removal refused: almost always uncommitted or unintegrated work.
refuse_dirty "$RM_ERR"
