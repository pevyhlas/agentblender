#!/usr/bin/env bash
# close-change.sh — Close a reviewed change in its own PR with one commit: refuse unless
# check-review.sh passes at HEAD, then write the change's log entry and delete its record.
# Run it once the current head is approved and before the final push, so required checks
# run once on the closed head. It commits in the current checkout only; it never pushes, merges
# or removes a worktree (cleanup-change.sh does that after merge, or --closed for every closed change).
# The closing commit uses the checkout's ambient Git identity. To use an agent identity, set
# GIT_AUTHOR_NAME, GIT_AUTHOR_EMAIL, GIT_COMMITTER_NAME and GIT_COMMITTER_EMAIL for this invocation.
#
# Usage: close-change.sh --pr <url> [--base <rev>] [--date YYYY-MM-DD] [-m <message>] <change-id> "<log text>"
#   <log text> follows "- <date> <id>: " on the entry: "<intent> — <IDs and parts> — <disposition> — <PR>".
#   The entire row is at most 200 characters. Omit **Tags**: the helper appends valid header tags.
#   The entry is its own file, .prdkit/history/log/<id>.md, so two branches that
#   each close a change never conflict; compaction folds entries into .prdkit/history/log.md (compact.md).
#   Exit 0 closed locally; rerun the gate on the new head, 1 refused or failing, 2 usage.
#
# Invariants:
# Require an online gate pass before writing. Local close does not authorize the new head.
# Refuse a dirty record, pre-existing staged changes and symlinked close paths at preflight.
# Publish a complete per-change log entry without replacing an existing entry. An entry
# write failure stops before staging, deleting the record or committing.
#
# Threat model:
# Include competing entry creation and interrupted closes; entry publication is not a
# transaction with the Git index and commit. Failure after publication can leave the entry,
# staged edits or a closing commit. No protection against a hostile checkout writer.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/spec-lib.sh"
usage="Usage: close-change.sh --pr <url> [--base <rev>] [--date YYYY-MM-DD] [-m <message>] <change-id> \"<intent> — <IDs and parts> — <disposition> — <PR>\" (whole row at most $SPEC_CLOSE_ROW_MAX characters)"
base_args=(); pr=""; day=""; message=""; id=""; text=""; options=true
while [ "$#" -gt 0 ]; do
    if $options; then
        case "$1" in
            --pr|--base|--date|-m|--message)
                [ "$#" -gt 1 ] || { echo "$usage" >&2; exit 2; }
                case "$1" in --pr) pr="$2" ;; --base) base_args=(--base "$2") ;; --date) day="$2" ;; *) message="$2" ;; esac
                shift 2; continue ;;
            --help|-h)
                printf '%s\n' "$usage" \
                    "Omit **Tags** from the description; the helper appends header tags." \
                    "Publishing the entry requires hard-link support in .prdkit/history/log/." \
                    "The closing commit uses the checkout's ambient Git identity." \
                    "For an agent identity, set GIT_AUTHOR_NAME, GIT_AUTHOR_EMAIL, GIT_COMMITTER_NAME and GIT_COMMITTER_EMAIL for this invocation." \
                    "Example: GIT_AUTHOR_NAME='Agent' GIT_AUTHOR_EMAIL=agent@example.com GIT_COMMITTER_NAME='Agent' GIT_COMMITTER_EMAIL=agent@example.com bash .prdkit/scripts/bash/close-change.sh --pr https://github.com/owner/repo/pull/1 001-example 'Update behavior — R-01 — complete — #1'"
                exit 0 ;;
            --) options=false; shift; continue ;;
            -*) echo "Unknown option: $1" >&2; exit 2 ;;
        esac
    fi
    if [ -z "$id" ]; then id="$1"; elif [ -z "$text" ]; then text="$1"; else echo "$usage" >&2; exit 2; fi
    shift
done
is_change_id "$id" && [ -n "$text" ] && [ -n "$pr" ] || { echo "$usage" >&2; exit 2; }
[ -n "$day" ] || day="$(spec_today)"
spec_is_date "$day" || { echo "--date takes YYYY-MM-DD (got: $day)" >&2; exit 2; }
refuse() { echo "close-change.sh: $*" >&2; exit 1; }
case "$text" in
    *$'\n'*|*$'\r'*) refuse "the log text is one line" ;;
    *'**Tags**:'*) refuse "omit **Tags** from the description; it is reserved for header tags" ;;
esac
[[ "$text" =~ [^[:space:]] ]] || refuse "the log text must contain a description"
root="$(git rev-parse --show-toplevel 2>/dev/null)" || refuse "not inside a Git checkout"
spec_require_history_layout "$root" || exit 1
spec_objects_local "$root" || refuse "local Git cannot guarantee reads without fetching missing objects"
export GIT_NO_LAZY_FETCH=1 GIT_NO_REPLACE_OBJECTS=1
tips="$(spec_integration_tips "$root")" || refuse "could not identify integration tips"
spec_require_history_tips "$root" "$tips" || exit 1
path="specs/changes/$id.md"; history=".prdkit/history"; log="$history/log.md"; entry="$history/log/$id.md"
check_paths() {
    local component
    for component in specs specs/changes "$path" .prdkit "$history" "$history/log" "$entry" "$log" "$history/archive.md"; do
        [ ! -L "$root/$component" ] || refuse "symlinked close path: $component"
    done
}
check_paths
working_index="$(spec_archive_log_index "$root")" || refuse "could not read the condensed log index"
committed_indexes="$(spec_archive_log_integration "$root" "$tips")" || refuse "could not read the condensed log index"
while IFS= read -r index; do
    if [ -n "$index" ] && spec_archive_log_covers "$index" "$id"; then
        refuse "$id is already closed: its number is condensed in .prdkit/history/archive.md"
    fi
done <<< "$working_index"$'\n'"$committed_indexes"
git -C "$root" diff --quiet HEAD -- "$path" ||
    refuse "$path has uncommitted edits: commit the record edits first; the check reads committed state"
verify_lf_file "$root/$path" || refuse "invalid record $path: expected non-empty LF-terminated text"
record_oid="$(verify_lines git -C "$root" rev-parse --verify "HEAD:$path")" ||
    refuse "cannot identify record $path at HEAD"
tag_row="$(verify_lines spec_change_metadata "$root/$path" tags)" ||
    refuse "cannot read tags from $path"
IFS="$SPEC_SEP" read -r tag_kind tags tag_line tag_issue <<< "$tag_row"
[ "$tag_kind" = tags ] || refuse "incomplete tag metadata from $path"
[ -z "$tag_issue" ] || refuse "$path:$tag_line: $tag_issue"
# Escape emphasis and the GFM bare-link prefix in otherwise safe, literal tag values.
tags="${tags//_/\\_}"; tags="${tags//www./www\\.}"
row="- $day $id: $text${tags:+ · **Tags**: $tags}"
length="$(LC_ALL=C awk "$SPEC_CLOSE_LENGTH_AWK"'{ print close_length($0) }' <<< "$row")"
[ "$length" -le "$SPEC_CLOSE_ROW_MAX" ] || refuse "the log row is $length characters, over $SPEC_CLOSE_ROW_MAX: keep \"<intent> — <IDs and parts> — <disposition> — <PR>\"; details stay in the PR"
record_hash="$(verify_lines git -C "$root" hash-object --no-filters -- "$root/$path")" ||
    refuse "cannot hash record $path"
[[ "$record_oid" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ && "$record_hash" = "$record_oid" ]] ||
    refuse "record $path does not match the bytes at HEAD"
git -C "$root" diff --cached --quiet || refuse "other changes are staged; commit or unstage them, so this commit holds only the record and its log entry"
existing="$(verify_lines git -C "$root" ls-tree --name-only HEAD -- ":(literal)$entry")" ||
    refuse "could not check for $entry at HEAD"
if [ -e "$root/$entry" ] || [ -n "$existing" ]; then
    refuse "the log already has an entry for $id"
fi
if git -C "$root" check-ignore -q -- "$entry"; then
    refuse "$entry is ignored by Git; track .prdkit/history/ and ignore only local state such as .prdkit/cache/"
elif [ "$?" -ne 1 ]; then
    refuse "could not check ignore rules for $entry"
fi
check_folded_log() {
    if [ ! -f "$1" ] || ! verify_lf_file "$1"; then
        refuse "invalid $log: expected non-empty LF-terminated text"
    fi
    # Do not use grep -q: finding a match early can mask a later read failure.
    if grep "^- [0-9-]* $id:" "$1" >/dev/null; then
        refuse "the log already has an entry for $id"
    elif [ "$?" -ne 1 ]; then
        refuse "could not check $log for an existing entry"
    fi
}
if [ -e "$root/$log" ]; then check_folded_log "$root/$log"; fi
# Only a successful literal tree lookup with no row means absent. The file
# can be committed and deliberately not materialized in a sparse checkout.
folded="$(verify_lines git -C "$root" ls-tree HEAD -- ":(literal)$log")" ||
    refuse "could not check for $log at HEAD"
if [ -n "$folded" ]; then
    meta="${folded%%$'\t'*}"
    [[ "${folded#*$'\t'}" = "$log" && "$meta" =~ ^100(644|755)\ blob\ [0-9a-f]{40}([0-9a-f]{24})?$ ]] ||
        refuse "invalid $log at HEAD"
    snapshot="$(mktemp)" || refuse "cannot allocate preflight scratch"
    trap 'rm -f "$snapshot"' EXIT
    git -C "$root" cat-file blob "${meta##* }" > "$snapshot" ||
        refuse "could not read $log at HEAD"
    check_folded_log "$snapshot"
    rm -f "$snapshot"
    trap - EXIT
fi
default="$(spec_default_branch "$root")" || refuse "could not identify the default branch"
if [ -n "$default" ]; then
    existing="$(verify_lines git -C "$root" ls-tree --name-only "$default" -- ":(literal)$path")" ||
        refuse "could not check for $path on $default"
    if [ -n "$existing" ]; then
        refuse "$path is already on $default: a change merged with its record closes inside the next relevant PR, before that PR's final review (review.md, Closing)"
    fi
fi

# Check the actual resolved base inside the gate as well as the frozen preflight tips.
check() { PRDKIT_CLOSE_CHECK_HISTORY=1 bash "$SCRIPT_DIR/check-review.sh" --pr "$pr" --repo "$root" --head HEAD ${base_args[@]+"${base_args[@]}"} "$id" 2>&1; }
if ! out="$(verify_lines check)" || [ -z "$out" ]; then
    printf '%s\n' "$out" >&2
    refuse "check-review.sh does not pass at HEAD; resolve its problems and meet the tier's approval minimum first"
fi
check_paths
# The process cwd pins a directory just like a directory descriptor. Verify
# its physical identity *after* entering it, before any relative write.
publish_entry() (
    cd -P "$root/$history" && [ "$(pwd -P)" = "$root/$history" ] || exit 1
    created_log=false
    if [ ! -d ./log ]; then mkdir ./log || exit 1; created_log=true; fi
    [ ! -L ./log ] && cd -P ./log && [ "$(pwd -P)" = "$root/.prdkit/history/log" ] || exit 1
    if $created_log; then
        # Restore an initially absent log on refusal. rmdir preserves concurrent entries.
        trap 'if [ "$?" -ne 0 ] && [[ . -ef "$root/.prdkit/history/log" ]]; then rmdir "$root/.prdkit/history/log" 2>/dev/null || :; fi' EXIT
    fi
    stage="$(mktemp -d .close-XXXXXXXX)" || exit 1
    (
        cd -P "./$stage" && [ "$(pwd -P)" = "$root/.prdkit/history/log/$stage" ] || exit 1
        # Keep cleanup in the private cwd even if that directory gets moved.
        # shellcheck disable=SC2329 # called by the EXIT trap
        cleanup_stage() {
            rm -f "./$id.md"
            if [[ . -ef "$root/.prdkit/history/log/$stage" ]]; then rmdir "$root/.prdkit/history/log/$stage"; fi
        }
        trap cleanup_stage EXIT
        set -C
        cat <<< "$row" > "./$id.md" || exit 1
        # Pin the publishing cwd separately. A moved stage can make its source
        # disappear, but can never redirect the destination to its new parent.
        (
            cd -P "$root/.prdkit/history/log" && [ "$(pwd -P)" = "$root/.prdkit/history/log" ] || exit 1
            if ! ln "./$stage/$id.md" .; then
                if [ -e "./$id.md" ] || [ -L "./$id.md" ]; then exit 3; fi
                exit 4
            fi
        ) || exit "$?"
        [[ ! -L "$root/$entry" && "./$id.md" -ef "$root/$entry" ]]
    )
)
(
    cd -P "$root/.prdkit" && [ "$(pwd -P)" = "$root/.prdkit" ] || exit 1
    if [ ! -d ./history ]; then
        mkdir ./history || exit 1
        trap 'if [ "$?" -ne 0 ] && [ ! -L ./history ]; then rmdir ./history 2>/dev/null || :; fi' EXIT
    fi
    publish_entry
) || {
    case $? in
        3) refuse "the log already has an entry for $id; nothing was deleted or committed" ;;
        4) refuse "could not create the hard link for $entry; nothing was deleted or committed. Closing requires hard-link support in .prdkit/history/log/" ;;
        *) refuse "could not write $entry; nothing was deleted or committed" ;;
    esac
}
check_paths
git -C "$root" add -- "$entry"
git -C "$root" rm -q -- "$path"
git -C "$root" commit -q -m "${message:-$id: close (log entry, record deleted)}" ||
    refuse "the commit failed; the record deletion and the log entry are staged"

head="$(git -C "$root" rev-parse HEAD)"
printf 'Closed %s in %s. Push this commit, obtain the approvals required for its tier, and rerun check-review.sh before merging.\n' "$id" "$head"
