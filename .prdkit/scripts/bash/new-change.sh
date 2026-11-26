#!/usr/bin/env bash
# new-change.sh — Open a change: number it, create specs/changes/<id>.md from the change
# template, and (by default) put its branch in an isolated worktree under .worktrees/.
#
# Usage: new-change.sh [--json] [--short-name <name>] [--number N] [--programme] [--no-worktree|--no-branch] <intent>
#
# --no-worktree (or an already linked worktree) branches in place, carrying
# existing local edits onto the new branch.
#
# Invariants:
# Refuse to overwrite an existing change record. In Git, serialize number selection
# and record creation across kit runs; no explicit allocation at or below the allocated high-water mark.
# Prefer an isolated worktree. Warn before automatic fallback and refuse if the status
# check reports tracked or non-ignored untracked work. Never prune stale registrations.
#
# Threat model:
# Include competing kit invocations and stale locks from ended runs. Branch/worktree
# creation and record creation are not transactional: interruption can leave partial
# state. A hostile writer modifying the checkout or Git directory is out of scope.
set -e

# shellcheck source=/dev/null
source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
# shellcheck source=/dev/null
source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/spec-lib.sh"

usage() { echo "Usage: $0 [--json] [--short-name <name>] [--number N] [--programme] [--no-worktree|--no-branch] <intent>"; }

JSON_MODE=false
NO_WORKTREE=false
NO_BRANCH=false
PROGRAMME=false
SHORT_NAME=""
CHANGE_NUMBER=""
ARGS=()
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) JSON_MODE=true ;;
        --no-worktree) NO_WORKTREE=true ;;
        --no-branch) NO_BRANCH=true ;;
        --programme) PROGRAMME=true ;;
        --) shift; ARGS+=("$@"); break ;;
        --short-name|--number)
            if [ "$#" -lt 2 ] || [[ "$2" == --* ]]; then echo "Error: $1 requires a value" >&2; exit 1; fi
            if [ "$1" = --short-name ]; then SHORT_NAME="$2"; else CHANGE_NUMBER="$2"; fi
            shift ;;
        --help|-h)
            usage
            echo "  --json              Output in JSON format"
            echo "  --short-name <name> Short name (2-4 words) for the change id and branch"
            echo "  --number N          Change number (must exceed the allocated high-water mark)"
            echo "  --programme         Include the outcome checkpoint scaffold for a programme or continuing queue"
            echo "  --no-worktree       Create and switch to the branch in the current checkout"
            echo "                      instead of an isolated worktree; existing edits carry over"
            echo "  --no-branch         Create only the change record in the current checkout"
            echo "                      without creating/switching branches or creating a worktree"
            exit 0 ;;
        -*) echo "Error: unknown option: $1 (use -- before intent text starting with '-')" >&2; exit 2 ;;
        *) ARGS+=("$1") ;;
    esac
    shift
done
if $NO_BRANCH && $NO_WORKTREE; then
    echo "Error: --no-branch and --no-worktree are mutually exclusive" >&2; exit 2
fi

INTENT="${ARGS[*]-}"
if [ -z "$INTENT" ]; then usage >&2; exit 1; fi
if [ -n "$CHANGE_NUMBER" ] && ! [[ "$CHANGE_NUMBER" =~ ^[0-9]{1,6}$ ]]; then
    echo "Error: --number expects a positive integer" >&2; exit 1
fi

if git rev-parse --show-toplevel >/dev/null 2>&1; then
    REPO_ROOT="$(git rev-parse --show-toplevel)"
    HAS_GIT=true
else
    REPO_ROOT="$(get_repo_root)"
    HAS_GIT=false
fi
cd "$REPO_ROOT"

slugify() {
    printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g; s/--*/-/g; s/^-//; s/-$//'
}

# Derive a short slug from the intent: drop stop words and words under three letters
# unless the intent spells them as an uppercase acronym; keep three (or all four) words.
generate_slug() {
    local stop="^(i|a|an|the|to|for|of|in|on|at|by|with|from|is|are|was|were|be|been|being|have|has|had|do|does|did|will|would|should|could|can|may|might|must|shall|this|that|these|those|my|your|our|their|want|need|add|get|set)$"
    local words=() word upper result="" count=0 max=3
    for word in $(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/ /g'); do
        printf '%s' "$word" | grep -qiE "$stop" && continue
        if [ ${#word} -ge 3 ]; then words+=("$word"); continue; fi
        upper="$(printf '%s' "$word" | tr '[:lower:]' '[:upper:]')"
        if printf '%s' "$1" | grep -qE "(^|[^[:alnum:]_])${upper}([^[:alnum:]_]|$)"; then words+=("$word"); fi
    done
    if [ ${#words[@]} -eq 0 ]; then
        slugify "$1" | tr '-' '\n' | grep -v '^$' | head -3 | tr '\n' '-' | sed 's/-$//'
        return
    fi
    [ ${#words[@]} -eq 4 ] && max=4
    for word in "${words[@]}"; do
        [ "$count" -lt "$max" ] || break
        result="${result:+$result-}$word"
        count=$((count + 1))
    done
    printf '%s\n' "$result"
}

# Every checkout that can hold a claimed number: this one, worktrees under
# .worktrees/, and every worktree git knows about. Paths may repeat or not exist.
list_checkouts() {
    local wt line worktrees checkout="" bare=false
    echo "$REPO_ROOT"
    for wt in "$REPO_ROOT/.worktrees"/*; do [ -d "$wt" ] && echo "$wt"; done
    if [ "$HAS_GIT" = true ]; then
        worktrees="$(spec_worktrees "$REPO_ROOT")" || return 1
        while IFS= read -r line; do
            case "$line" in
                "worktree "*) checkout="${line#worktree }"; bare=false ;;
                bare) bare=true ;;
                "") if [ -n "$checkout" ] && ! $bare; then printf '%s\n' "$checkout"; fi ;;
            esac
        done <<< "$worktrees"$'\n'
    fi
    return 0
}

# Claims: open and closed records, planned markers and branches. Archive indexes and
# integration-tip records/logs are loaded separately under the lock.
list_claimed() {
    list_spec_claims || return 1
    if [ "$HAS_GIT" = true ]; then
        git for-each-ref --format='local branch %(refname:strip=2)' refs/heads || return 1
        git for-each-ref --format='remote branch %(refname:strip=3)' refs/remotes || return 1
    fi
    return 0
}

list_spec_claims() {
    local checkout path name entries planned
    while IFS= read -r checkout; do
        spec_require_layout "$checkout" || return 1
        for path in "$checkout"/specs/changes/[0-9]*.md; do
            [ -f "$path" ] && { name="${path##*/}"; echo "change record ${name%.md}"; }
        done
        entries="$(spec_log_entries "$checkout")" || return 1
        sed -n 's/^- [0-9-]* \([0-9][0-9][0-9]*-[a-z0-9-]*\):.*/closed change \1/p' <<< "$entries" || return 1
        planned="$(spec_planned_changes "$checkout")" || return 1
        while IFS= read -r name; do
            [ -z "$name" ] || printf 'planned in the spec %s\n' "$name"
        done <<< "$planned"
    done <<< "$CHECKOUTS"
    return 0
}

list_archive_details() {
    local checkouts checkout
    checkouts="$(sort -u <<< "$CHECKOUTS")" || return 1
    while IFS= read -r checkout; do
        spec_archive_log_details "$checkout" "$checkout/.prdkit/history/archive.md" || return 1
    done <<< "$checkouts"
    [ "$HAS_GIT" = true ] || return 0
    spec_archive_log_integration "$REPO_ROOT" "$INTEGRATION_TIPS" details
}

list_committed_claims() {
    local rev
    while IFS= read -r rev; do
        [ -n "$rev" ] || continue
        spec_number_claims_at "$REPO_ROOT" "$rev" || return 1
    done <<< "$INTEGRATION_TIPS"
}

claimed_numbers() {
    while IFS= read -r line; do
        case "${line##* }" in [0-9][0-9][0-9]*-*) echo "$line" ;; esac
    done <<< "$CLAIMS"
}

allocated_high_water() {
    local highest=0 line name number index floor
    while IFS="$SPEC_SEP" read -r index _ _ floor _; do
        name="${index##* }"; number="${name##*-}"
        [ "$number" -gt "$highest" ] && highest="$number"
        if [ -n "$floor" ] && [ "$floor" != - ] && [ "$floor" -gt "$highest" ]; then highest="$floor"; fi
    done <<< "$ARCHIVE_DETAILS"
    while IFS= read -r line; do
        case "$line" in
            "change record "*|"closed change "*|"committed "*) ;;
            *) continue ;;
        esac
        name="${line##* }"; number=$((10#${name%%-*}))
        [ "$number" -gt "$highest" ] && highest=$number
    done <<< "$CLAIMS"
    echo "$highest"
}

next_number() {
    local highest="$HIGH_WATER" line name number
    while IFS= read -r line; do
        [ -n "$line" ] || continue
        name="${line##* }"; number=$((10#${name%%-*}))
        [ "$number" -gt "$highest" ] && highest=$number
    done <<< "$CLAIMS"
    echo $((highest + 1))
}

number_owner() { # zero-padded number; prints the claiming source
    local line
    while IFS= read -r line; do
        case "${line##* }" in "$1"-*) echo "$line"; return 0 ;; esac
    done < <(claimed_numbers)
    return 1
}

# Automatic numbering reads the claims, then creates one. Concurrent runs, in this checkout or
# in linked worktrees of the same repository, serialize on one lock in the Git common directory
# (the project's .prdkit/ without Git), held from computing the number until the change's branch
# and record exist, so each run sees the claims made before it. The lock is a file the shell
# creates exclusively (noclobber opens it with O_EXCL), naming the holder's PID and host; mkdir
# is not used because some coreutils (uutils) report success when another run created the
# directory first. A lock whose holder is gone (a dead PID on this host, or no owner after a
# minute) is removed by one reaper at a time, which re-checks it first. PRDKIT_NUMBER_LOCK_WAIT
# bounds the wait in seconds (default 120).
NUMBER_LOCK=""
RECORD_TMP=""
HOST_NAME="$(uname -n)"
create_exclusive() { ( set -C; printf '%s %s\n' "$$" "$HOST_NAME" > "$1" ) 2>/dev/null; }
release_number_lock() { [ -z "$NUMBER_LOCK" ] || rm -f "$NUMBER_LOCK"; NUMBER_LOCK=""; }
cleanup_open() {
    release_number_lock
    spec_close_history_view
    if [ -n "$RECORD_TMP" ]; then
        rm -f "$RECORD_TMP"/{template,scaffold,record}
        rmdir "$RECORD_TMP"
    fi
}
number_lock_stale() { # lock file -> 0 when its holder is gone
    local owner pid
    owner="$(cat "$1" 2>/dev/null)" || owner=""
    if [ -z "$owner" ]; then
        [ -n "$(find "$1" -maxdepth 0 -mmin +1 2>/dev/null)" ]
        return
    fi
    pid="${owner%% *}"
    [ "${owner#* }" = "$HOST_NAME" ] && ! ps -p "$pid" >/dev/null 2>&1
}
acquire_number_lock() {
    local base lock waited=0 limit="${PRDKIT_NUMBER_LOCK_WAIT:-120}"
    if [ "$HAS_GIT" = true ]; then
        base="$(CDPATH="" cd "$(git rev-parse --git-common-dir)" && pwd -P)"
    elif [ -d "$REPO_ROOT/.prdkit" ]; then
        base="$REPO_ROOT/.prdkit"
    else
        base="$REPO_ROOT"
    fi
    lock="$base/prdkit-change-number.lock"
    while ! create_exclusive "$lock"; do
        if number_lock_stale "$lock" && create_exclusive "$lock.reap"; then
            if number_lock_stale "$lock"; then
                rm -f "$lock"
                echo "[prdkit] Notice: removed a stale change-number lock left by an ended run: $lock" >&2
            fi
            rm -f "$lock.reap"
            continue
        fi
        if [ "$waited" -ge "$limit" ]; then
            echo "[prdkit] Error: another run has held the change-number lock for over ${limit}s: $lock (holder: $(cat "$lock" 2>/dev/null || echo unknown)). If no new-change.sh run is in progress, remove it and retry." >&2
            exit 1
        fi
        sleep 1
        waited=$((waited + 1))
    done
    NUMBER_LOCK="$lock"
    trap cleanup_open EXIT
    trap 'exit 1' INT TERM HUP
}

if [ -n "$SHORT_NAME" ]; then SLUG="$(slugify "$SHORT_NAME")"; else SLUG="$(generate_slug "$INTENT")"; fi
SLUG="$(printf '%s' "$SLUG" | cut -c1-60 | sed 's/-$//')"
if [ -z "$SLUG" ]; then echo "Error: could not derive a short name; pass --short-name" >&2; exit 1; fi

AUTO_NUMBER=false
# Both automatic and explicit allocation must see a complete, valid archive index from
# every checkout. Keep the lock through record creation; a malformed index cannot be overridden.
acquire_number_lock
CHECKOUTS="$(list_checkouts | awk '!seen[$0]++')" || exit 1
if [ "$HAS_GIT" = true ]; then
    spec_open_history_view "$REPO_ROOT" ||
        { echo "[prdkit] Error: could not set up local integration history reads" >&2; exit 1; }
fi
while IFS= read -r checkout; do
    spec_require_history_layout "$checkout" --head || exit 1
done <<< "$CHECKOUTS"
INTEGRATION_TIPS=""
if [ "$HAS_GIT" = true ]; then
    INTEGRATION_TIPS="$(spec_integration_tips "$REPO_ROOT")" ||
        { echo "[prdkit] Error: could not identify local integration tips" >&2; exit 1; }
    spec_require_history_tips "$REPO_ROOT" "$INTEGRATION_TIPS" || exit 1
fi
ARCHIVE_DETAILS="$(list_archive_details)"
COMMITTED_CLAIMS="$(list_committed_claims)"
CLAIMS="$(list_claimed)" || exit 1
CLAIMS="$(claimed_numbers)${COMMITTED_CLAIMS:+$'\n'$COMMITTED_CLAIMS}"
HIGH_WATER="$(allocated_high_water)"
if [ -z "$CHANGE_NUMBER" ]; then
    CHANGE_NUMBER="$(next_number)"; AUTO_NUMBER=true
fi
NUM="$(printf '%03d' "$((10#$CHANGE_NUMBER))")"
if [ "$AUTO_NUMBER" = true ]; then
    # Refresh branch claims in case a branch was created outside the numbering lock.
    CLAIMS="$(list_claimed)" || exit 1
    while OWNER="$(number_owner "$NUM")"; do
        NEXT="$(printf '%03d' "$((10#$NUM + 1))")"
        echo "[prdkit] Notice: change number $NUM taken by $OWNER, using $NEXT" >&2
        NUM="$NEXT"
    done
fi
CHANGE_ID="$NUM-$SLUG"
is_change_id "$CHANGE_ID" || { echo "Error: '$CHANGE_ID' is not a valid change id" >&2; exit 1; }
BRANCH_NAME="$CHANGE_ID"

while IFS= read -r checkout; do
    if [ -e "$checkout/specs/changes/$CHANGE_ID.md" ] || [ -L "$checkout/specs/changes/$CHANGE_ID.md" ]; then
        echo "[prdkit] Error: $checkout/specs/changes/$CHANGE_ID.md already exists; it is never overwritten." >&2
        exit 1
    fi
done <<< "$CHECKOUTS"

if [ "$AUTO_NUMBER" = false ]; then
    while IFS="$SPEC_SEP" read -r index _; do
        spec_archive_log_covers "$index" "$NUM" || continue
        cutoff="${index%% *}"
        if [ "$((10#$NUM))" -le "$cutoff" ]; then
            printf '[prdkit] Error: change number %s is reserved: .prdkit/history/archive.md condenses the log through %03d; omit --number or use a number above %03d.\n' "$NUM" "$cutoff" "$HIGH_WATER" >&2
        else
            printf '[prdkit] Error: change number %s is reserved by the condensed log index in .prdkit/history/archive.md; omit --number or use a number above %03d.\n' "$NUM" "$HIGH_WATER" >&2
        fi
        exit 1
    done <<< "$ARCHIVE_DETAILS"
    if [ "$((10#$NUM))" -le "$HIGH_WATER" ]; then
        printf '[prdkit] Error: change number %s is at or below the allocated high-water mark %03d; omit --number or use a number above %03d.\n' "$NUM" "$HIGH_WATER" "$HIGH_WATER" >&2
        exit 1
    fi
    # Branch and planned reservations can still be overridden; name their holders.
    # The new change's own planned marker does not count.
    HOLDERS=""
    while IFS= read -r held_line; do
        [ -n "$held_line" ] || continue
        held="${held_line##* }"
        case "$held_line" in
            "planned in the spec $NUM-$SLUG"|"planned in the spec $NUM-") continue ;;
            *) [ "$((10#${held%%-*}))" -eq "$((10#$NUM))" ] || continue ;;
        esac
        case "; $HOLDERS; " in *"; $held_line; "*) continue ;; esac
        HOLDERS="${HOLDERS:+$HOLDERS; }$held_line"
    done <<< "$CLAIMS"
    [ -z "$HOLDERS" ] || echo "[prdkit] Notice: change number $NUM is also held by $HOLDERS; --number overrides it" >&2
fi

WORKTREE_PATH="$REPO_ROOT"
CREATED_WORKTREE=false
TEMPLATE=.prdkit/templates/change-template.md
SCAFFOLD=.prdkit/scripts/bash/outcome-checkpoint.md
POINTER='<!-- Outcome checkpoint format: .prdkit/scripts/bash/outcome-checkpoint.md -->'
TODAY="${PRDKIT_TODAY:-$(date +%Y-%m-%d)}"
RECORD_TMP="$(mktemp -d)"
RENDERED_RECORD="$RECORD_TMP/record"
stage_record_input() { # project-relative path, temp path, pinned start commit
    local path="$1" staged="$2" rev="$3" entry oid type parent rows meta name
    local paths=("$path")
    entry="$(_spec_history_git "$REPO_ROOT" cat-file --batch-check --follow-symlinks <<< "$rev:$path")" || return 1
    read -r oid type _ <<< "$entry"
    case "$oid" in
        dangling|loop|notdir|symlink)
            # A link that cannot be followed. In a partial clone any link or target
            # along the chain may be an object that was never fetched, so only an
            # ordinary clone keeps the built-in fallback.
            ! partial_clone "$REPO_ROOT"
            return ;;
    esac
    [ "$type" != blob ] || { _spec_history_git "$REPO_ROOT" cat-file blob "$oid" > "$staged"; return; }
    [ "$type" = missing ] || return 0
    # Batch mode reports both absent paths and missing objects as "missing". Keep the
    # fallback only if the tree shows no entry for the path and no symlink on the path
    # itself or one of its exact ancestors.
    parent="$path"
    while [[ "$parent" = */* ]]; do
        parent="${parent%/*}"
        paths+=("$parent")
    done
    rows="$(verify_lines _spec_history_git "$REPO_ROOT" ls-tree -t "$rev" -- "${paths[@]}")" || return 1
    while IFS=$'\t' read -r meta name; do
        [ "$name" != "$path" ] || return 1
        [[ "$meta" = '120000 '* ]] || continue
        for parent in "${paths[@]}"; do [ "$name" != "$parent" ] || return 1; done
    done <<< "$rows"
}
partial_clone() { # root -> 0 when a promisor remote may hold objects not stored locally
    git -C "$1" config --get extensions.partialclone >/dev/null 2>&1 && return 0
    git -C "$1" config --name-only --get-regexp '^remote\..*\.partialclonefilter$' >/dev/null 2>&1 && return 0
    git -C "$1" config --bool --get-regexp '^remote\..*\.promisor$' 2>/dev/null | grep ' true$' >/dev/null
}
record_input() { # project-relative path, temp path, optional pinned start commit
    local path="$1" staged="$2"
    if [ -n "${3:-}" ]; then
        if ! stage_record_input "$path" "$staged" "$3"; then
            printf '[prdkit] Error: cannot read %s at %s from local Git objects; make the committed file available locally and retry.\n' "$path" "$3" >&2
            return 1
        fi
        [ ! -f "$staged" ] || path="$staged"
    fi
    printf '%s\n' "$path"
}
render_record() {
    local template="$1" scaffold="$2" snippet=""
    if $PROGRAMME && [ -f "$scaffold" ] && verify_lf_file "$scaffold"; then snippet="$scaffold"; fi
    if [ -f "$template" ]; then
        review_file_without_nul "$template" || return 1
        LC_ALL=C PK_INTENT="$INTENT" PK_TODAY="$TODAY" PK_SCAFFOLD="$snippet" PK_POINTER="$POINTER" \
            awk -v programme="$PROGRAMME" "$SPEC_SKIP_AWK"'
            function scaffold(    row, rc, n, file) {
                file = ENVIRON["PK_SCAFFOLD"]
                if (file == "") exit 1
                while ((rc = (getline row < file)) > 0) { print row; n++ }
                if (rc < 0 || !n) exit 1
                close(file)
            }
            {
                # Emit LF text even for CRLF templates or a missing final terminator.
                sub(/\r$/, "")
                lines[NR] = $0
                # Recognize the kit comments only when not already inside an example.
                outside = !fence && !comment
                hidden = spec_skip($0)
                if (outside && /^<!-- (A programme or continuing follow-up queue \(|For a programme\/continuing follow-up queue,)/)
                    obsolete = 1
                if (obsolete && !fence && $0 == "### Outcome checkpoint") unsupported = 1
                if (!comment) obsolete = 0
                if (outside && $0 == ENVIRON["PK_POINTER"]) {
                    if (state && !slot) slot = NR
                }
                if (!hidden && /^## /) state = ($0 == "## State")
                if (!hidden && state && $0 == "### Outcome checkpoint") has_block = 1
                # A safe slot follows a line that leaves neither a comment nor a fence open.
                if (!fence && !comment) {
                    safe = NR + 1
                    if (state) state_end = safe
                }
            }
            END {
                if (unsupported) exit 2
                # Use the stock pointer as the insertion slot; otherwise stay in State.
                at = slot ? slot : state_end ? state_end : safe ? safe : 1
                if (programme != "true" || has_block) at = 0
                for (i = 1; i <= NR + 1; i++) {
                    if (i == at) {
                        if (!state_end) print "\n## State\n"
                        scaffold()
                        if (i == slot) continue
                    }
                    if (i > NR) break
                    text = lines[i]
                    if (i == 1 && text ~ /^# Change:/) text = "# Change: " ENVIRON["PK_INTENT"]
                    if (text ~ /^\*\*Opened\*\*: <date>/) sub(/<date>/, ENVIRON["PK_TODAY"], text)
                    print text
                }
            }
        ' "$template"
    else
        printf '# Change: %s\n\n**Opened**: %s\n' "$INTENT" "$TODAY" || return 1
        if $PROGRAMME; then [ -n "$snippet" ] && printf '\n## State\n\n' && cat "$snippet"; fi
    fi
}
prepare_record() ( # optional pinned start commit for a fresh worktree
    set -o pipefail
    local template scaffold="" tag_row line issue render_status=0
    template="$(record_input "$TEMPLATE" "$RECORD_TMP/template" "${1:-}")" || exit 1
    if $PROGRAMME; then
        scaffold="$(record_input "$SCAFFOLD" "$RECORD_TMP/scaffold" "${1:-}")" || exit 1
    fi
    render_record "$template" "$scaffold" > "$RENDERED_RECORD" || render_status=$?
    if [ "$render_status" -eq 2 ]; then
        echo "[prdkit] Error: Unsupported change template scaffold; use the current outcome checkpoint pointer: $POINTER" >&2
        exit 1
    fi
    if [ "$render_status" -ne 0 ] || ! verify_lf_file "$RENDERED_RECORD"; then
        echo "[prdkit] Error: could not render the change record; check the change template and the installed outcome checkpoint snippet." >&2
        exit 1
    fi
    tag_row="$(spec_change_metadata "$RENDERED_RECORD" create)" || {
        echo "[prdkit] Error: could not validate Tags in the rendered record from $TEMPLATE." >&2
        exit 1
    }
    IFS="$SPEC_SEP" read -r _ _ line issue <<< "$tag_row"
    if [ -n "$issue" ]; then
        echo "[prdkit] Error: $TEMPLATE:$line: $issue." >&2
        exit 1
    fi
)
if $NO_BRANCH; then
    prepare_record
    BRANCH_NAME=""
    if $HAS_GIT; then BRANCH_NAME="$(git symbolic-ref --quiet --short HEAD 2>/dev/null || true)"; fi
elif [ "$HAS_GIT" = true ]; then
    if git show-ref --verify --quiet "refs/heads/$BRANCH_NAME"; then
        echo "[prdkit] Error: Branch '$BRANCH_NAME' already exists." >&2
        echo "[prdkit] To continue that change, run: git checkout $BRANCH_NAME" >&2
        echo "[prdkit] To open a different change, choose a unique short name." >&2
        exit 1
    fi
    # A linked worktree has a per-checkout git dir that differs from the common dir.
    IN_LINKED_WORKTREE=false
    GIT_DIR_ABS="$(CDPATH="" cd "$(git rev-parse --git-dir)" 2>/dev/null && pwd -P)" || GIT_DIR_ABS=""
    GIT_COMMON_ABS="$(CDPATH="" cd "$(git rev-parse --git-common-dir)" 2>/dev/null && pwd -P)" || GIT_COMMON_ABS=""
    if [ -n "$GIT_DIR_ABS" ] && [ -n "$GIT_COMMON_ABS" ] && [ "$GIT_DIR_ABS" != "$GIT_COMMON_ABS" ]; then
        IN_LINKED_WORKTREE=true
    fi
    if [ "$NO_WORKTREE" = true ] || [ "$IN_LINKED_WORKTREE" = true ]; then
        # Explicit opt-out, or already isolated: create the branch in place (no nesting).
        prepare_record
        git checkout -q -b "$BRANCH_NAME"
    else
        # Stage the record from the same committed template the new checkout will inherit.
        worktree_base="$(git rev-parse --verify HEAD)"
        prepare_record "$worktree_base"
        WORKTREE_DIR="$REPO_ROOT/.worktrees/$BRANCH_NAME"
        mkdir -p "$REPO_ROOT/.worktrees"
        if ! git check-ignore -q .worktrees 2>/dev/null; then
            if [ -s "$REPO_ROOT/.gitignore" ] && [ -n "$(tail -c 1 "$REPO_ROOT/.gitignore")" ]; then
                echo >> "$REPO_ROOT/.gitignore"
            fi
            echo ".worktrees/" >> "$REPO_ROOT/.gitignore"
            echo "[prdkit] Added '.worktrees/' to .gitignore so isolated worktrees stay untracked." >&2
        fi
        if WT_ERR="$(git worktree add "$WORKTREE_DIR" -b "$BRANCH_NAME" "$worktree_base" 2>&1 >/dev/null)"; then
            WORKTREE_PATH="$WORKTREE_DIR"
            CREATED_WORKTREE=true
        else
            echo "[prdkit] Warning: Could not create a worktree at $WORKTREE_DIR:" >&2
            echo "[prdkit]   ${WT_ERR:-unknown git error}" >&2
            if git worktree list --porcelain | grep '^prunable' >/dev/null; then
                echo "[prdkit] Inspect stale registrations with cleanup-change.sh --prune before retrying." >&2
                exit 1
            fi
            # Falling back switches the MAIN checkout onto the new branch; never do that
            # on top of uncommitted work.
            if [ -n "$(git status --porcelain --untracked-files=all 2>/dev/null)" ]; then
                echo "[prdkit] ERROR: The current checkout has uncommitted changes (tracked or untracked); refusing to fall back to 'git checkout -b' (it would carry your work onto the new branch)." >&2
                echo "[prdkit] Fix the worktree failure above, commit or stash first, or pass --no-worktree explicitly." >&2
                exit 1
            fi
            echo "[prdkit] Falling back to 'git checkout -b' in the current checkout." >&2
            prepare_record
            # A failed `worktree add -b` can leave the new branch behind; reuse it then.
            if git show-ref --verify --quiet "refs/heads/$BRANCH_NAME"; then
                git checkout -q "$BRANCH_NAME"
            else
                git checkout -q -b "$BRANCH_NAME"
            fi
        fi
    fi
else
    prepare_record
    echo "[prdkit] Warning: Git repository not detected; skipped branch creation for $BRANCH_NAME" >&2
fi

CHANGES_DIR="$WORKTREE_PATH/specs/changes"
CHANGE_FILE="$CHANGES_DIR/$CHANGE_ID.md"
mkdir -p "$CHANGES_DIR"
if [ -e "$CHANGE_FILE" ] || [ -L "$CHANGE_FILE" ]; then
    echo "[prdkit] Error: $CHANGE_FILE already exists; it is never overwritten." >&2
    exit 1
fi
# Refuse a file/symlink created after the existence check, including by an external writer.
if ! (set -C; cat "$RENDERED_RECORD" > "$CHANGE_FILE"); then
    echo "[prdkit] Error: could not create $CHANGE_FILE; existing records are never overwritten." >&2
    exit 1
fi
# The branch and record now claim the number; the next run may compute its own.
release_number_lock

# A new worktree starts from committed HEAD: owner context still uncommitted under specs/ in this
# checkout (a fresh inbox line, a spec edit) is not in it. Name it so the agent carries it over.
UNCARRIED=()
if [ "$CREATED_WORKTREE" = true ]; then
    while IFS= read -r -d '' entry; do
        UNCARRIED+=("${entry:3}")
    done < <(git -C "$REPO_ROOT" status --porcelain=v1 -z --untracked-files=all --no-renames -- specs 2>/dev/null)
fi
if [ "${#UNCARRIED[@]}" -gt 0 ]; then
    echo "[prdkit] Warning: uncommitted spec context in $REPO_ROOT is not in the new worktree, which starts from committed HEAD:" >&2
    for entry in "${UNCARRIED[@]}"; do echo "[prdkit]   $entry" >&2; done
    echo "[prdkit] Record what this change needs inside the worktree." >&2
fi

# Symlinked commands (init.sh --symlink) point into the kit checkout, which a new worktree lacks
# until its submodule is initialized there. Say so instead of handing over a worktree whose
# slash commands silently fail to load; never fetch anything here.
if [ "$CREATED_WORKTREE" = true ]; then
    DANGLING=0; KIT_DIR=""
    for link in "$WORKTREE_PATH"/.claude/commands/prdkit*.md "$WORKTREE_PATH"/.opencode/commands/prdkit*.md; do
        { [ -L "$link" ] && [ ! -e "$link" ]; } || continue
        DANGLING=$((DANGLING + 1))
        if [ -z "$KIT_DIR" ]; then
            case "$(readlink "$link")" in
                ../../*/commands/*) KIT_DIR="$(readlink "$link")"; KIT_DIR="${KIT_DIR#../../}"; KIT_DIR="${KIT_DIR%/commands/*}" ;;
            esac
        fi
    done
    if [ "$DANGLING" -gt 0 ]; then
        echo "[prdkit] Warning: $DANGLING slash-command link(s) in the new worktree do not resolve${KIT_DIR:+: they point into $KIT_DIR, which is not checked out there}." >&2
        if [ -n "$KIT_DIR" ] && [ "$(git -C "$WORKTREE_PATH" ls-files -s -- "$KIT_DIR" 2>/dev/null | cut -d' ' -f1)" = 160000 ]; then
            echo "[prdkit]   Fix: git -C $WORKTREE_PATH submodule update --init $KIT_DIR" >&2
        fi
        echo "[prdkit]   Or install command copies, which work in every checkout: bash ${KIT_DIR:-<kit>}/scripts/init.sh --upgrade --copy (in $REPO_ROOT, then commit)." >&2
    fi
fi

if $JSON_MODE; then
    uncarried_json=""
    for entry in ${UNCARRIED[@]+"${UNCARRIED[@]}"}; do
        uncarried_json="$uncarried_json${uncarried_json:+,}\"$(json_escape "$entry")\""
    done
    printf '{"CHANGE_ID":"%s","CHANGE_FILE":"%s","BRANCH_NAME":"%s","WORKTREE_PATH":"%s","UNCARRIED_SPECS":[%s]}\n' \
        "$CHANGE_ID" "$(json_escape "$CHANGE_FILE")" "$(json_escape "$BRANCH_NAME")" "$(json_escape "$WORKTREE_PATH")" "$uncarried_json"
else
    echo "CHANGE_ID: $CHANGE_ID"
    echo "CHANGE_FILE: $CHANGE_FILE"
    echo "BRANCH_NAME: $BRANCH_NAME"
    echo "WORKTREE_PATH: $WORKTREE_PATH"
fi
if [ "$CREATED_WORKTREE" = true ]; then
    echo "[prdkit] Change '$CHANGE_ID' lives in an isolated worktree." >&2
fi
echo "[prdkit] Continue all work for this change inside: $WORKTREE_PATH" >&2
