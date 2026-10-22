#!/usr/bin/env bash
# new-change.sh — Open a change: number it, create specs/changes/<id>.md from the change
# template, and (by default) put its branch in an isolated worktree under .worktrees/.
#
# Usage: new-change.sh [--json] [--short-name <name>] [--number N] [--no-worktree] <intent>
#
# Never overwrites an existing change record and never switches a dirty main checkout
# onto the new branch.
set -e

# shellcheck source=/dev/null
source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
# shellcheck source=/dev/null
source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/spec-lib.sh"

usage() { echo "Usage: $0 [--json] [--short-name <name>] [--number N] [--no-worktree] <intent>"; }

JSON_MODE=false
NO_WORKTREE=false
SHORT_NAME=""
CHANGE_NUMBER=""
ARGS=()
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) JSON_MODE=true ;;
        --no-worktree) NO_WORKTREE=true ;;
        --short-name|--number)
            if [ "$#" -lt 2 ] || [[ "$2" == --* ]]; then echo "Error: $1 requires a value" >&2; exit 1; fi
            if [ "$1" = --short-name ]; then SHORT_NAME="$2"; else CHANGE_NUMBER="$2"; fi
            shift ;;
        --help|-h)
            usage
            echo "  --json              Output in JSON format"
            echo "  --short-name <name> Short name (2-4 words) for the change id and branch"
            echo "  --number N          Change number (overrides auto-detection)"
            echo "  --no-worktree       Create the branch in the current checkout instead of"
            echo "                      an isolated worktree under .worktrees/"
            exit 0 ;;
        *) ARGS+=("$1") ;;
    esac
    shift
done

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

# Every checkout whose specs/ can hold a claimed number: this one, worktrees under
# .worktrees/, and every worktree git knows about. Paths may repeat or not exist.
list_checkouts() {
    local wt line
    echo "$REPO_ROOT"
    for wt in "$REPO_ROOT/.worktrees"/*; do [ -d "$wt" ] && echo "$wt"; done
    if [ "$HAS_GIT" = true ]; then
        git worktree list --porcelain 2>/dev/null | while IFS= read -r line; do
            case "$line" in "worktree "*) echo "${line#worktree }" ;; esac
        done
    fi
    return 0
}

# Numbered identities already claimed: open change records, closed changes in the log, 0.x
# feature folders (on disk before the cutover; in git after it: at the base commit
# specs/archive.md records, and removed in recent history), the last 0.x feature
# specs/archive.md names, changes the spec's `planned:` markers name, and local and remote
# branches. The highest of them all wins.
list_claimed() {
    list_spec_claims
    if [ "$HAS_GIT" = true ]; then
        git for-each-ref --format='local branch %(refname:strip=2)' refs/heads 2>/dev/null
        git for-each-ref --format='remote branch %(refname:strip=3)' refs/remotes 2>/dev/null
    fi
    return 0
}

list_spec_claims() {
    local checkout path name last token base
    while IFS= read -r checkout; do
        for path in "$checkout"/specs/changes/[0-9]*.md; do
            [ -f "$path" ] && { name="${path##*/}"; echo "change record ${name%.md}"; }
        done
        if [ -f "$checkout/specs/log.md" ]; then
            sed -n 's/^- [0-9-]* \([0-9][0-9][0-9]*-[a-z0-9-]*\):.*/closed change \1/p' "$checkout/specs/log.md"
        fi
        spec_planned_changes "$checkout" | sed 's/^/planned in the spec /'
        if [ -f "$checkout/specs/archive.md" ]; then
            last="$(spec_archive_last "$checkout")"
            case "$last" in ''|0) ;; *) printf 'specs/archive.md, 0.x features through %03d-\n' "$last" ;; esac
            if [ "$HAS_GIT" = true ]; then
                while IFS= read -r token; do
                    base="$(git -C "$checkout" rev-parse --verify --quiet "$token^{commit}" 2>/dev/null)" || continue
                    spec_features_at "$checkout" "$base" | sed 's/^/feature folder at the archive base /'
                    break
                done < <(spec_archive_base_tokens "$checkout")
            fi
        fi
        for path in "$checkout"/specs/features/[0-9]* "$checkout"/specs/features/_archive/[0-9]* \
                    "$checkout"/specs/archive/[0-9]* "$checkout"/specs/archive/*/[0-9]* \
                    "$checkout"/specs/archive/*/_archive/[0-9]*; do
            [ -d "$path" ] && echo "feature folder ${path##*/}"
        done
    done < <(list_checkouts)
    if [ "$HAS_GIT" = true ]; then
        spec_removed_features "$REPO_ROOT" | sed 's/^/feature folder removed from main /'
    fi
    return 0
}

# A specs/archive.md from which neither a base commit here nor a last 0.x feature can be read.
archive_unreadable() {
    local checkout token
    while IFS= read -r checkout; do
        [ -f "$checkout/specs/archive.md" ] || continue
        [ -z "$(spec_archive_last "$checkout")" ] || continue
        if [ "$HAS_GIT" = true ]; then
            while IFS= read -r token; do
                git -C "$checkout" rev-parse --verify --quiet "$token^{commit}" >/dev/null 2>&1 && continue 2
            done < <(spec_archive_base_tokens "$checkout")
        fi
        echo "$checkout/specs/archive.md"
    done < <(list_checkouts | sort -u)
    return 0
}

claimed_numbers() {
    list_claimed | while IFS= read -r line; do
        case "${line##* }" in [0-9][0-9][0-9]*-*) echo "$line" ;; esac
    done
}

next_number() {
    local highest=0 line name number
    while IFS= read -r line; do
        name="${line##* }"; number=$((10#${name%%-*}))
        [ "$number" -gt "$highest" ] && highest=$number
    done < <(claimed_numbers)
    echo $((highest + 1))
}

number_owner() { # zero-padded number; prints the claiming source
    local line
    while IFS= read -r line; do
        case "${line##* }" in "$1"-*) echo "$line"; return 0 ;; esac
    done < <(claimed_numbers)
    return 1
}

if [ -n "$SHORT_NAME" ]; then SLUG="$(slugify "$SHORT_NAME")"; else SLUG="$(generate_slug "$INTENT")"; fi
SLUG="$(printf '%s' "$SLUG" | cut -c1-60 | sed 's/-$//')"
if [ -z "$SLUG" ]; then echo "Error: could not derive a short name; pass --short-name" >&2; exit 1; fi

AUTO_NUMBER=false
if [ -z "$CHANGE_NUMBER" ]; then
    # Never reuse a 0.x feature number silently. With no feature folder anywhere to read, stop on
    # a specs/archive.md that yields no number, and warn in a shallow clone without one: its
    # history is cut short.
    if ! list_spec_claims | grep -q -e '^feature folder ' -e '^specs/archive.md, '; then
        UNREADABLE="$(archive_unreadable | sed -n 1p)"
        if [ -n "$UNREADABLE" ]; then
            echo "[prdkit] Error: $UNREADABLE names no **Base** commit in this repository and no readable **Last 0.x feature** line (a number, or none), and git history shows no removed 0.x feature folder, so an old feature number could be reused. Fix it (.prdkit/templates/archive-template.md) or pass --number." >&2
            exit 1
        fi
        if [ ! -f "$REPO_ROOT/specs/archive.md" ] && [ "$HAS_GIT" = true ] &&
            [ "$(git rev-parse --is-shallow-repository 2>/dev/null)" = true ]; then
            echo "[prdkit] Warning: shallow clone: 0.x feature folders removed from main are not in its history, so their numbers are not reserved; fetch the full history or pass --number." >&2
        fi
    fi
    CHANGE_NUMBER="$(next_number)"; AUTO_NUMBER=true
fi
NUM="$(printf '%03d' "$((10#$CHANGE_NUMBER))")"
if [ "$AUTO_NUMBER" = true ]; then
    # A concurrent run may have claimed the number since it was computed: bump past it.
    while OWNER="$(number_owner "$NUM")"; do
        NEXT="$(printf '%03d' "$((10#$NUM + 1))")"
        echo "[prdkit] Notice: change number $NUM taken by $OWNER, using $NEXT" >&2
        NUM="$NEXT"
    done
else
    # An explicit --number overrides every reservation; it names what else holds the number. Its
    # own planned: marker does not count.
    HOLDERS=""
    while IFS= read -r held_line; do
        held="${held_line##* }"
        case "$held_line" in
            "planned in the spec $NUM-$SLUG"|"planned in the spec $NUM-") continue ;;
            "specs/archive.md, 0.x features through "*) [ "$((10#$NUM))" -le "$((10#${held%-}))" ] || continue ;;
            *) [ "$((10#${held%%-*}))" -eq "$((10#$NUM))" ] || continue ;;
        esac
        case "; $HOLDERS; " in *"; $held_line; "*) continue ;; esac
        HOLDERS="${HOLDERS:+$HOLDERS; }$held_line"
    done < <(claimed_numbers)
    [ -z "$HOLDERS" ] || echo "[prdkit] Notice: change number $NUM is also held by $HOLDERS; --number overrides it" >&2
fi
CHANGE_ID="$NUM-$SLUG"
is_change_id "$CHANGE_ID" || { echo "Error: '$CHANGE_ID' is not a valid change id" >&2; exit 1; }
BRANCH_NAME="$CHANGE_ID"

for checkout in "$REPO_ROOT" "$REPO_ROOT/.worktrees/$BRANCH_NAME"; do
    if [ -e "$checkout/specs/changes/$CHANGE_ID.md" ]; then
        echo "[prdkit] Error: $checkout/specs/changes/$CHANGE_ID.md already exists; it is never overwritten." >&2
        exit 1
    fi
done

WORKTREE_PATH="$REPO_ROOT"
CREATED_WORKTREE=false
if [ "$HAS_GIT" = true ]; then
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
        git checkout -q -b "$BRANCH_NAME"
    else
        WORKTREE_DIR="$REPO_ROOT/.worktrees/$BRANCH_NAME"
        mkdir -p "$REPO_ROOT/.worktrees"
        if ! git check-ignore -q .worktrees 2>/dev/null; then
            if [ -s "$REPO_ROOT/.gitignore" ] && [ -n "$(tail -c 1 "$REPO_ROOT/.gitignore")" ]; then
                echo >> "$REPO_ROOT/.gitignore"
            fi
            echo ".worktrees/" >> "$REPO_ROOT/.gitignore"
            echo "[prdkit] Added '.worktrees/' to .gitignore so isolated worktrees stay untracked." >&2
        fi
        git worktree prune >/dev/null 2>&1 || true
        if WT_ERR="$(git worktree add "$WORKTREE_DIR" -b "$BRANCH_NAME" 2>&1 >/dev/null)"; then
            WORKTREE_PATH="$WORKTREE_DIR"
            CREATED_WORKTREE=true
        else
            echo "[prdkit] Warning: Could not create a worktree at $WORKTREE_DIR:" >&2
            echo "[prdkit]   ${WT_ERR:-unknown git error}" >&2
            # Falling back switches the MAIN checkout onto the new branch; never do that
            # on top of uncommitted work.
            if [ -n "$(git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
                echo "[prdkit] ERROR: The current checkout has uncommitted changes; refusing to fall back to 'git checkout -b' (it would carry your work onto the new branch)." >&2
                echo "[prdkit] Fix the worktree failure above, commit or stash first, or pass --no-worktree explicitly." >&2
                exit 1
            fi
            echo "[prdkit] Falling back to 'git checkout -b' in the current checkout." >&2
            # A failed `worktree add -b` can leave the new branch behind; reuse it then.
            if git show-ref --verify --quiet "refs/heads/$BRANCH_NAME"; then
                git checkout -q "$BRANCH_NAME"
            else
                git checkout -q -b "$BRANCH_NAME"
            fi
        fi
    fi
else
    echo "[prdkit] Warning: Git repository not detected; skipped branch creation for $BRANCH_NAME" >&2
fi

CHANGES_DIR="$WORKTREE_PATH/specs/changes"
CHANGE_FILE="$CHANGES_DIR/$CHANGE_ID.md"
mkdir -p "$CHANGES_DIR"
TEMPLATE="$WORKTREE_PATH/.prdkit/templates/change-template.md"
[ -f "$TEMPLATE" ] || TEMPLATE="$REPO_ROOT/.prdkit/templates/change-template.md"
if [ -e "$CHANGE_FILE" ]; then
    echo "[prdkit] Error: $CHANGE_FILE already exists; it is never overwritten." >&2
    exit 1
fi
TODAY="${PRDKIT_TODAY:-$(date +%Y-%m-%d)}"
if [ -f "$TEMPLATE" ]; then
    PK_INTENT="$INTENT" PK_TODAY="$TODAY" awk '
        NR == 1 && /^# Change:/ { print "# Change: " ENVIRON["PK_INTENT"]; next }
        /^\*\*Opened\*\*: <date>/ { sub(/<date>/, ENVIRON["PK_TODAY"]) }
        { print }
    ' "$TEMPLATE" > "$CHANGE_FILE"
else
    printf '# Change: %s\n\n**Opened**: %s\n' "$INTENT" "$TODAY" > "$CHANGE_FILE"
fi

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
