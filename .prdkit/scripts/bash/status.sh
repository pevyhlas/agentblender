#!/usr/bin/env bash
# status.sh — Derived, read-only status from the spec, open change records (in every registered
# worktree), the owner inbox, records and local git. No network calls, including lazy fetches in
# a partial clone. Readiness always needs evidence review.
#
# Usage: status.sh [--json]
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/spec-lib.sh"
source "$SCRIPT_DIR/install-lib.sh"
source "$SCRIPT_DIR/diagnostics-lib.sh"
json=false
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) json=true ;;
        --help|-h) echo 'Usage: status.sh [--json]'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done
root="$(get_repo_root)"
require_consumer_root "$root" status.sh
spec_require_history_layout "$root" || exit 1
spec_require_layout "$root" || exit 1
today="$(spec_today)"
layout="$(spec_layout "$root")"
mixed="$(spec_mixed_forms "$root")"
SEP="$SPEC_SEP"
archive_index="$(spec_archive_log_index "$root")" || exit "$?"
compaction="$(spec_compaction "$root" "$archive_index")" || exit "$?"
read -r closed_since last_pass <<< "$compaction"

age() { # date -> days since, or blank
    if spec_is_date "$1"; then spec_days_between "$1" "$today"; fi
}
js() { printf '"%s"' "$(json_escape "$1")"; }
ju() { if [[ "$1" =~ ^(0|[1-9][0-9]*)$ ]]; then printf '%s' "$1"; else printf null; fi; }
ji() { if [[ "$1" =~ ^(0|-?[1-9][0-9]*)$ ]]; then printf '%s' "$1"; else printf null; fi; }

items="$(verify_lines spec_items "$root")"
part_headers="$(verify_lines spec_part_headings "$root")"
part_graph="$(verify_lines spec_part_graph "$items" "$part_headers")"
spec_lifecycle_require "$root"
stage_now="$SPEC_STAGE_NOW"; stage_next="$SPEC_STAGE_NEXT"
# Live items marked for a stage after the current one: needed later, not now.
later="$(if [ -n "$stage_now" ]; then
    { printf '%s\n' "$items" | awk -F "$SEP" 'NF && $5 == "yes" { print "R" FS $1 }' || exit 1
      spec_stages "$root" | awk -F "$SEP" '{ print "S" FS $1 FS $2 }'; } |
    awk -F "$SEP" -v now="$stage_now" -v stage_order="$SPEC_STAGE_ORDER" -v stage_custom="$SPEC_STAGE_CUSTOM" "$SPEC_STAGE_AWK"'
        $1 == "R" { gone[$2] = 1; next }
        !($2 in gone) && stage_rank($3) > stage_rank(now) { out = out (out ? ", " : "") $2 " (" $3 ")" }
        END { print out }'
fi)"
changes="$(verify_lines spec_open_changes "$root" "$archive_index")"
inbox="$(verify_lines spec_inbox "$root")"
records="$(verify_lines spec_records "$root")"

change_root_at() { case "$1" in /*) printf '%s\n' "$1" ;; *) printf '%s/%s\n' "$root" "$1" ;; esac; }
known_task_parts() { awk -F "$SEP" '$1 == "part" { printf " %s", $2 } END { print " " }' <<< "$1"; }
change_metadata() { # open rows -> same rows with tags, task tallies, and invalid assignments appended
    local row id loc change_root metadata tallies graph known other_items other_headers root_parts
    root_parts="$(known_task_parts "$part_graph")" || return 1
    while IFS= read -r row; do
        [ -n "$row" ] || continue
        id="${row%%"$SEP"*}"; loc="${row##*"$SEP"}"
        change_root="$(change_root_at "$loc")"
        metadata="$(verify_lines spec_change_metadata "$change_root/specs/changes/$id.md" status)" || return 1
        known="$root_parts"
        # Only read other checkouts' part inventories when they have an ID to resolve.
        if [ "$loc" != . ] && [[ "$metadata" == "task$SEP"P* || "$metadata" == *$'\n'"task$SEP"P* ]]; then
            other_items="$(verify_lines spec_items "$change_root")" || return 1
            other_headers="$(verify_lines spec_part_headings "$change_root")" || return 1
            graph="$(verify_lines spec_part_graph "$other_items" "$other_headers")" || return 1
            known="$(known_task_parts "$graph")" || return 1
        fi
        tallies="$(printf '%s\n' "$metadata" | awk -F "$SEP" -v known="$known" '
            $1 == "tags" { tag_rows++; tags = $2; next }
            $1 == "task" {
                issue = $5
                if (issue != "malformed" && $2 != "" && !index(known, " " $2 " ")) issue = "unknown"
                if (issue != "") {
                    invalid = invalid (invalid != "" ? "\t" : "") $4 ":" issue ":" $2
                    next
                }
                if ($2 != "" && !($2 in total)) order[++n] = $2
                total[$2]++; checked[$2] += $3
            }
            END {
                if (tag_rows != 1) exit 1
                printf "%s\037", tags
                for (i = 1; i <= n; i++) {
                    p = order[i]; printf "%s%s:%d:%d", (i > 1 ? " " : ""), p, checked[p], total[p]
                }
                printf "\037"
                if ("" in total) printf "%d:%d", checked[""], total[""]
                print "\037" invalid
            }')" || return 1
        printf '%s\037%s\n' "$row" "$tallies" || return 1
    done <<< "$1"
}
detailed_changes="$(verify_lines change_metadata "$changes")"

change_metadata_json() { # tags, part tallies, unassigned tally, invalid assignments
    local tags="$1" tallies="$2" unassigned="$3" tag tally part counts sep=""
    printf ',"tags":'
    if [ -n "$tags" ]; then
        printf '['
        while [[ "$tags" == *,* ]]; do
            tag="${tags%%,*}"; tags="${tags#*, }"
            printf '%s%s' "$sep" "$(js "$tag")"; sep=,
        done
        printf '%s%s]' "$sep" "$(js "$tags")"
    else printf null; fi
    printf ',"tasks_by_part":'
    if [ -n "$tallies" ]; then
        printf '['; sep=""
        for tally in $tallies; do
            part="${tally%%:*}"; counts="${tally#*:}"
            printf '%s{"part":%s,"checked":%s,"total":%s}' "$sep" "$(js "$part")" "${counts%%:*}" "${counts#*:}"; sep=,
        done
        printf ']'
    else printf null; fi
    printf ',"unassigned_tasks":'
    if [ -n "$unassigned" ]; then
        printf '{"checked":%s,"total":%s}' "${unassigned%%:*}" "${unassigned#*:}"
    else printf null; fi
    printf ',"invalid_task_parts":'
    if [ -n "$4" ]; then printf '['; invalid_task_parts "$4" json; printf ']'
    else printf null; fi
}

invalid_task_parts() { # tab-separated line:reason:part records
    local row line reason part sep=""
    while IFS= read -r row; do
        [ -n "$row" ] || continue
        line="${row%%:*}"; row="${row#*:}"; reason="${row%%:*}"; part="${row#*:}"
        if [ "${2:-}" = json ]; then
            printf '%s{"part":%s,"line":%s,"reason":%s}' "$sep" "$(js "$part")" "$(ju "$line")" "$(js "$reason")"
            sep=,
        else
            case "$reason" in
                outside) printf '    tasks: line %s: task part %s is not in the change header Parts\n' "$line" "$part" ;;
                *) printf '    tasks: line %s: %s task part %s\n' "$line" "$reason" "$part" ;;
            esac
        fi
    done <<< "${1//$'\t'/$'\n'}"
}

# Use the same winning worktree record. Children reference the parent; ordinary standalone
# changes have no outcome row. A referenced but missing/incomplete parent stays visibly unknown.
parents="$(printf '%s\n' "$changes" | awk -F '\037' '$7 != "" && $7 != "none" { print $7 }' | sort -u)"
progress="$(while IFS="$SEP" read -r id title _ _ _ _ parent _ _ _ _ _ _ _ loc; do
    [ -n "$id" ] || continue
    required=false
    if printf '%s\n' "$parents" | grep -qxF "$id"; then required=true
    elif [ -n "$parent" ] && [ "$parent" != none ]; then continue; fi
    change_root="$(change_root_at "$loc")"
    [ -f "$change_root/specs/changes/$id.md" ] || exit 1
    checkpoint="$(verify_lines spec_change_progress "$change_root/specs/changes/$id.md" "$required")" || exit 1
    [ -z "$checkpoint" ] || printf '%s\037%s\037%s\037%s\n' "$id" "$title" "$loc" "$checkpoint"
done <<< "$changes"
while IFS= read -r parent; do
    is_change_id "$parent" || continue
    if ! printf '%s\n' "$changes" | cut -d "$SEP" -f1 | grep -qxF "$parent"; then
        checkpoint="$(verify_lines spec_change_progress /dev/null true)" || exit 1
        [ -n "$checkpoint" ] || exit 1
        printf '%s\037\037\037%s\n' "$parent" "$checkpoint"
    fi
done <<< "$parents")"

# --- Git: default branch, unmerged branches, deployments behind it ------------------------
# Reading a missing object in a partial clone would fetch it from the promisor remote. Git 2.45+
# (and some maintenance releases) refuse under GIT_NO_LAZY_FETCH; in a partial clone any older
# Git skips the object reads instead (squash detection, deploy distance, unclosed records).
export GIT_NO_LAZY_FETCH=1
objects_local=true
base=""; branches=""; unclosed=""
if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    spec_objects_local "$root" || objects_local=false
    base="$(spec_default_branch "$root")"
    if [ -n "$base" ]; then
        branches="$(verify_lines git -C "$root" for-each-ref --no-merged="$base" --sort=committerdate \
            --format="%(refname:short)$SEP%(committerdate:short)" refs/heads)"
        # Reviewed PR-scoped records still on the default branch; a child in any checkout keeps its parent open.
        if $objects_local; then
            unclosed=" $(spec_unclosed_changes "$root" "$base" |
                awk -v named=" $(printf '%s\n' "$parents" | tr -d '`' | tr '\n' ' ') " 'index(named, " " $0 " ") == 0' | tr '\n' ' ')"
        fi
    fi
fi
# A squash merge leaves the branch unmerged in git's eyes: when the default branch has, or had
# at a commit after the merge base, the branch's version of every file it changed, the branch is
# possibly squash-merged. Later commits may have changed those files again.
squashed() { # branch -> yes|no
    local mb files=() file rc=0 commit expected history
    $objects_local || { echo no; return 0; }
    mb="$(git -C "$root" merge-base "$base" "$1" 2>/dev/null)" || { echo no; return 0; }
    # A diff that cannot read its objects (a tree-filtered clone) is not evidence of a squash.
    git -C "$root" diff --quiet --no-renames "$mb" "$1" >/dev/null 2>&1 || rc=$?
    [ "$rc" -le 1 ] || { echo no; return 0; }
    git -C "$root" diff --no-renames --name-only -z "$mb" "$1" > "$work/paths" || return 1
    expected="$(verify_nul_count "$work/paths")" || return 1
    while IFS= read -r -d '' file; do
        [ -n "$file" ] || return 1
        files+=(":(literal)$file")
    done < "$work/paths"
    if [ "${#files[@]}" -ne "$expected" ] || { [ "$expected" -eq 0 ] && [ "$rc" -eq 1 ]; }; then
        echo "Incomplete squash path inventory for $1: expected $expected paths from diff, checked ${#files[@]} (quiet diff status $rc)" >&2
        return 1
    fi
    if [ "$expected" -eq 0 ] || git -C "$root" diff --quiet "$base" "$1" -- "${files[@]}" 2>/dev/null; then echo yes; return 0; fi
    # The squash commit is usually among the first after the merge base to touch those files.
    history="$(verify_lines git -C "$root" log --reverse --format=%H "$mb..$base" -- "${files[@]}" | awk 'NR <= 20')" || return 1
    while IFS= read -r commit; do
        [ -n "$commit" ] || continue
        [[ "$commit" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ ]] || return 1
        git -C "$root" diff --quiet "$commit" "$1" -- "${files[@]}" 2>/dev/null && { echo yes; return 0; }
    done <<< "$history"
    echo no
}
if [ -n "$branches" ]; then
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    branches="$(printf '%s\n' "$branches" | while IFS="$SEP" read -r branch date; do
        [ -n "$branch" ] && { [ -z "$date" ] || spec_is_date "$date"; } || exit 1
        squash="$(squashed "$branch")" || exit 1
        printf '%s\037%s\037%s\n' "$branch" "$date" "$squash"
    done)"
fi

# --- Records: holds, waivers, latest deploy per app and environment -----------------------
holds=""; deploys=""
while IFS="$SEP" read -r line type name who scope env rev date until check reason; do
    [ -n "$line" ] || continue
    case "$type" in
        INVALID) printf 'Malformed record at specs/records.md:%s: %s\n' "$line" "$reason" >&2; exit 1 ;;
        HOLD|WAIVER)
            state=active
            if spec_is_date "$until" && [ "$(spec_days_between "$until" "$today")" -gt 0 ]; then
                if [ "$type" = HOLD ]; then state=due; else state=expired; fi
            fi
            holds+="$type$SEP$name$SEP$who$SEP$scope$SEP$until$SEP$state$SEP$check$SEP$reason"$'\n' ;;
        DEPLOY)
            deploys+="$name$SEP$env$SEP$rev$SEP$date$SEP$who"$'\n' ;;
    esac
done <<< "$records"
# Newest date wins per app/env; ties keep the first line (records are newest first).
deploys="$(printf '%s' "$deploys" | awk -F '\037' 'NF { k = $1 "\037" $2; if (!(k in d) || $4 > d[k]) { d[k] = $4; row[k] = $0; if (!(k in seen)) { seen[k] = 1; order[++n] = k } } }
    END { for (i = 1; i <= n; i++) print row[order[i]] }')"
distance() { # rev -> "<commits on the default branch not deployed> <deployed commits not on it>",
    #             or "unknown" when the revision is not resolvable here
    local counts behind_n ahead_n
    if $objects_local && [ -n "$base" ] && [ -n "$1" ] && git -C "$root" cat-file -e "$1^{commit}" 2>/dev/null &&
        counts="$(verify_lines git -C "$root" rev-list --left-right --count "$base...$1")"; then
        read -r behind_n ahead_n <<< "$counts"
        if [[ "$behind_n" =~ ^[0-9]+$ && "$ahead_n" =~ ^[0-9]+$ && "$counts" != *$'\n'* ]]; then
            printf '%s %s\n' "$behind_n" "$ahead_n"
        else echo unknown; fi
    else
        echo unknown
    fi
}

# --- Longest pole ---------------------------------------------------------------------------
pole=""; pole_waits=""
stalled="$(printf '%s\n' "$changes" | awk -F '\037' '$12 != "" { print $11 "\037" $1 "\037" $12 "\037" $5 }' | sort -t "$SEP" -k1,1nr | head -1)"
if [ -n "$stalled" ]; then
    IFS="$SEP" read -r att cid items_stalled waits <<< "$stalled"
    pole="$cid acceptance $items_stalled (stalled, up to $att attempts)"
    pole_waits="${waits:-an owner decision on the alternatives note}"
fi
if [ -z "$pole" ]; then
    waiting="$(printf '%s\n' "$changes" | awk -F '\037' 'NF && $5 != "" && tolower($5) != "nothing" { print ($3 == "" ? "9999-99-99" : $3) "\037" $1 "\037" $5 }' | sort | head -1)"
    if [ -n "$waiting" ]; then
        IFS="$SEP" read -r _ cid waits <<< "$waiting"
        pole="$cid"; pole_waits="$waits"
    fi
fi
if [ -z "$pole" ]; then
    hold="$(printf '%s' "$holds" | awk -F '\037' '$1 == "HOLD" { print; exit }')"
    if [ -n "$hold" ]; then
        IFS="$SEP" read -r _ hid hwho hscope _ _ _ _ <<< "$hold"
        pole="HOLD $hid on $hscope"; pole_waits="$hwho lifting it"
    fi
fi
if [ -z "$pole" ]; then
    question="$(printf '%s\n' "$inbox" | awk -F '\037' '$4 == "open" { print $2 "\037" $5; exit }')"
    if [ -n "$question" ]; then
        IFS="$SEP" read -r qdate qwords <<< "$question"
        pole="inbox item of $qdate: $qwords"; pole_waits="the owner"
    fi
fi
if [ -z "$pole" ]; then
    oldest="$(printf '%s\n' "$changes" | awk -F '\037' 'NF { print ($3 == "" ? "9999-99-99" : $3) "\037" $1 }' | sort | head -1)"
    if [ -n "$oldest" ]; then pole="${oldest#*"$SEP"}"; pole_waits="nothing recorded"; fi
fi

if [ -z "$pole" ]; then
    pending="$(printf '%s\n' "$inbox" | awk -F '\037' '$4 == "processing" || $4 == "unknown" { print $5; exit }')"
    if [ -n "$pending" ]; then pole="inbox reconciliation: $pending"; pole_waits="the agent processing recorded direction"; fi
fi

# --- Output -----------------------------------------------------------------------------------
if $json; then
    printf '{"date":%s,"layout":%s,"mixed_spec_forms":%s,"compaction":{"closed_since_last":%s,"last":%s,"due":%s},"stage":{"now":%s,"next":%s,"later":%s},"items":[' "$(js "$today")" "$(js "$layout")" \
        "$([ -n "$mixed" ] && echo true || echo false)" "$(ju "$closed_since")" "$(if [ -n "$last_pass" ]; then js "$last_pass"; else echo null; fi)" \
        "$([ "$closed_since" -ge 10 ] && echo true || echo false)" "$(js "$stage_now")" "$(js "$stage_next")" "$(js "$later")"
    sep=""
    while IFS="$SEP" read -r id title mat planned retired unenf where; do
        [ -n "$id" ] || continue
        printf '%s{"id":%s,"title":%s,"maturity":%s,"planned":%s,"retired":%s,"unenforced":%s,"at":%s}' "$sep" \
            "$(js "$id")" "$(js "$title")" "$(js "$mat")" "$(js "$planned")" "$([ "$retired" = yes ] && echo true || echo false)" \
            "$([ "$unenf" = yes ] && echo true || echo false)" "$(js "$where")"
        sep=,
    done <<< "$items"
    printf '],"changes":['; sep=""
    while IFS="$SEP" read -r id title opened step waits nxt parent total passed failed maxatt stalled_items ws ns loc tags tallies unassigned invalid; do
        [ -n "$id" ] || continue
        flag=false; case "$unclosed" in *" $id "*) flag=true ;; esac
        printf '%s{"id":%s,"title":%s,"opened":%s,"age_days":%s,"step":%s,"waiting_on":%s,"next":%s,"parent":%s,"location":%s,"merged_unclosed":%s,"state_fields":{"waiting_on":"%s","next":"%s"},"acceptance":{"total":%s,"passed":%s,"failed_or_blocked":%s,"max_attempts":%s,"stalled":%s}' "$sep" \
            "$(js "$id")" "$(js "$title")" "$(js "$opened")" "$(ji "$(age "$opened")")" "$(js "$step")" "$(js "$waits")" \
            "$(js "$nxt")" "$(js "$parent")" "$(js "$loc")" "$flag" "$ws" "$ns" \
            "$(ju "$total")" "$(ju "$passed")" "$(ju "$failed")" "$(ju "$maxatt")" "$(js "$stalled_items")"
        change_metadata_json "$tags" "$tallies" "$unassigned" "$invalid"
        printf '}'; sep=,
    done <<< "$detailed_changes"
    printf '],"part_parents":'; sep="["
    while IFS="$SEP" read -r kind part parent; do
        [ "$kind" = edge ] || continue
        printf '%s{"id":%s,"parent":%s}' "$sep" "$(js "$part")" "$(js "$parent")"; sep=,
    done <<< "$part_graph"
    if [ "$sep" = '[' ]; then printf null; else printf ']'; fi
    printf ',"deployments":['; sep=""
    while IFS="$SEP" read -r app env rev date by; do
        [ -n "$app" ] || continue
        printf '%s{"app":%s,"env":%s,"rev":%s,"live":%s,"date":%s,"by":%s,"commits_behind_default":%s,"commits_not_on_default":%s}' "$sep" \
            "$(js "$app")" "$(js "$env")" "$(js "$rev")" "$([ "$rev" = none ] && echo false || echo true)" "$(js "$date")" "$(js "$by")" \
            "$(if [ "$rev" = none ]; then echo null; else d="$(distance "$rev")"; js "${d%% *}"; fi)" \
            "$(if [ "$rev" = none ]; then echo null; else d="$(distance "$rev")"; js "${d##* }"; fi)"
        sep=,
    done <<< "$deploys"
    printf '],"holds":['; sep=""
    while IFS="$SEP" read -r type id who scope until state check reason; do
        [ -n "$type" ] || continue
        printf '%s{"type":%s,"id":%s,"owner":%s,"scope":%s,"until":%s,"state":%s,"check":%s,"reason":%s}' "$sep" \
            "$(js "$type")" "$(js "$id")" "$(js "$who")" "$(js "$scope")" "$(js "$until")" "$(js "$state")" "$(js "$check")" "$(js "$reason")"
        sep=,
    done <<< "$holds"
    printf '],"inbox":['; sep=""
    while IFS="$SEP" read -r line date who state words id kind tags revision answer _; do
        [ -n "$line" ] || continue
        [ "$state" = open ] || [ "$state" = processing ] || [ "$state" = unknown ] || continue
        printf '%s{"line":%s,"date":%s,"age_days":%s,"from":%s,"state":%s,"words":%s,"id":%s,"kind":%s,"tags":%s,"revision":%s,"answer":%s}' "$sep" \
            "$(ju "$line")" "$(js "$date")" "$(ji "$(age "$date")")" "$(js "$who")" "$(js "$state")" "$(js "$words")" \
            "$(js "$id")" "$(js "$kind")" "$(js "$tags")" "$(js "$revision")" "$(js "$answer")"
        sep=,
    done <<< "$inbox"
    printf '],"unmerged_branches":['; sep=""
    while IFS="$SEP" read -r branch date squash; do
        [ -n "$branch" ] || continue
        printf '%s{"branch":%s,"last_commit":%s,"possibly_squash_merged":%s}' "$sep" "$(js "$branch")" "$(js "$date")" \
            "$([ "$squash" = yes ] && echo true || echo false)"
        sep=,
    done <<< "$branches"
    printf '],"outcome_progress":['; sep=""
    while IFS="$SEP" read -r id title loc outcome milestone critical allocation advance selection count limit signal; do
        [ -n "$id" ] || continue
        printf '%s{"id":%s,"title":%s,"location":%s,"outcome":%s,"milestone":%s,"critical_path":%s,"allocation":%s,"progress":%s,"selection":%s,"no_progress_changes":%s,"progress_limit":%s,"count_signal":%s}' "$sep" \
            "$(js "$id")" "$(js "$title")" "$(js "$loc")" "$(js "$outcome")" "$(js "$milestone")" \
            "$(js "$critical")" "$(js "$allocation")" "$(js "$advance")" "$(js "$selection")" "$(ju "$count")" "$(ju "$limit")" "$(js "$signal")"
        sep=,
    done <<< "$progress"
    printf '],"longest_pole":{"item":%s,"waits_on":%s},"readiness":"requires-evidence-review","adoption":%s}\n' \
        "$(js "$pole")" "$(js "$pole_waits")" "$(diagnostic_adoption "$root")"
    exit 0
fi

printf 'Product-Kit status — %s (derived from local files and git; read-only)\n' "$today"
diagnostic_adoption "$root" --text
case "$layout" in
    none) printf '\nNo spec yet — start specs/product.md or specs/SPEC.md (.prdkit/workflows/init.md).\n' ;;
esac
[ -z "$mixed" ] || printf '\nTwo spec forms: specs/SPEC.md beside %s — keep one; the views below combine both.\n' "$mixed"
if [ "$closed_since" -ge 10 ]; then
    printf '\nCompaction due: %s changes closed %s (.prdkit/workflows/compact.md, Compaction).\n' "$closed_since" \
        "$(if [ -n "$last_pass" ]; then echo "since the last pass ($last_pass)"; else echo 'and no pass recorded in .prdkit/history/log.md'; fi)"
fi

printf '\nOutcome progress (recorded claims; evidence and allocation need review)\n'
[ -n "$progress" ] || printf '  No open outcome checkpoints.\n'
while IFS="$SEP" read -r id title loc outcome milestone critical allocation advance selection count limit signal; do
    [ -n "$id" ] || continue
    printf '  %s — %s\n' "$id" "$title"
    case "$signal" in
        reassess) printf '    REASSESS: %s integrated changes without recorded milestone/dependency progress (limit %s).\n' "$count" "$limit" ;;
        below-limit) printf '    No-progress changes: %s/%s; below count limit, not an outcome pass.\n' "$count" "$limit" ;;
        invalid) printf '    Invalid outcome checkpoint fields; reconcile before selecting more work.\n' ;;
        *) printf '    Outcome progress accounting unknown; do not infer zero from passing changes.\n' ;;
    esac
    [ -z "$outcome" ] || printf '    outcome: %s\n' "$outcome"
    [ -z "$milestone" ] || printf '    milestone: %s\n' "$milestone"
    printf '    progress: %s\n' "${advance:-unknown}"
    [ -z "$critical" ] || printf '    critical path: %s\n' "$critical"
    [ -z "$allocation" ] || printf '    allocation: %s\n' "$allocation"
    [ -z "$selection" ] || printf '    selection: %s\n' "$selection"
done <<< "$progress"

printf '\nTrue now\n'
[ -z "$stage_now" ] || printf '  Stage: %s%s\n' "$stage_now" "${stage_next:+; next: $stage_next}"
[ -z "$later" ] || printf '  For later stages, not needed now: %s\n' "$later"
printf '%s\n' "$items" | awk -F '\037' '
    NF {
        any = 1
        k = $1; sub(/-.*/, "", k)  # awk-bytes-ok: k is the whole ID before trimming the suffix
        if ($1 ~ /^P[0-9]+(\.[0-9]+)?$/) k = "P"
        else if ($1 ~ /^P[0-9]+(\.[0-9]+)?\.[BI]-/) k = substr(k, length(k), 1)
        if ($5 == "yes") { retired[k]++; next }
        n[k]++; if ($3 == "hypothesis") h[k]++; if ($3 == "validated") v[k]++
        if ($4 != "") planned = planned sprintf("    %s %s (planned: %s)\n", $1, $2, $4)
        if ($6 == "yes") unenf = unenf sprintf("    %s %s\n", $1, $2)
    }
    END {
        split("J R Q C N P B I D", order, " "); split("Journeys Requirements Qualities Constraints Non-goals Parts Behaviors Invariants Decisions", label, " ")
        for (i = 1; i <= 9; i++) {
            k = order[i]; if (!(k in n) && !(k in retired)) continue
            line = sprintf("  %s: %d", label[i], n[k])
            if (k == "J" || k == "R" || k == "Q" || k == "P") line = line sprintf(" (%d hypothesis, %d validated)", h[k], v[k])
            if (retired[k]) line = line sprintf(", %d retired", retired[k])
            print line
        }
        if (planned != "") printf "  Planned, not built:\n%s", planned
        if (unenf != "") printf "  Unenforced:\n%s", unenf
        if (!any) print "  No spec items found."
    }'
while IFS="$SEP" read -r kind part parent; do
    [ "$kind" = edge ] || continue
    printf '    %s (parent: %s)\n' "$part" "$parent"
done <<< "$part_graph"

printf '\nIn flight\n'
if [ -z "$changes" ]; then printf '  No open changes.\n'; fi
while IFS="$SEP" read -r id title opened step waits nxt parent total passed failed maxatt stalled_items ws ns loc tags tallies unassigned invalid; do
    [ -n "$id" ] || continue
    a="$(age "$opened")"
    printf '  %s — %s\n' "$id" "$title"
    case "$loc" in ''|.) ;; *) printf '    in %s\n' "$loc" ;; esac
    printf '    step %s%s; acceptance %s/%s passed' "${step:-unknown}" "${a:+, opened $a days ago}" "$passed" "$total"
    [ "$failed" -eq 0 ] || printf ', %s failing or blocked' "$failed"
    [ "$maxatt" -eq 0 ] || printf ', up to %s attempts' "$maxatt"
    printf '\n'
    [ -z "$tags" ] || printf '    tags: %s\n' "$tags"
    for tally in $tallies; do
        counts="${tally#*:}"
        printf '    tasks: %s %s/%s checked\n' "${tally%%:*}" "${counts%%:*}" "${counts#*:}"
    done
    [ -z "$unassigned" ] || printf '    tasks: unassigned %s/%s checked\n' "${unassigned%%:*}" "${unassigned#*:}"
    invalid_task_parts "$invalid"
    [ -z "$stalled_items" ] || printf '    stalled: %s (alternatives note and owner decision due)\n' "$stalled_items"
    [ -z "$waits" ] || printf '    waiting on: %s\n' "$waits"
    [ -z "$nxt" ] || printf '    next: %s\n' "$nxt"
    [ "$ws" = present ] || printf '    State: Waiting on is %s\n' "$ws"
    [ "$ns" = present ] || printf '    State: Next is %s\n' "$ns"
    case "$parent" in ''|none) ;; *) printf '    parent: %s\n' "$parent" ;; esac
    case "$unclosed" in *" $id "*) printf '    merged without closing: record on %s, with no child or outcome checkpoint keeping it open; close it in the next relevant PR, or give a change spanning PRs a checkpoint (review.md, Closing)\n' "$base" ;; esac
done <<< "$detailed_changes"
if [ -n "$branches" ]; then
    printf '  Unmerged branches (oldest first):\n'
    printf '%s\n' "$branches" | head -10 | awk -F '\037' 'NF { printf "    %s (last commit %s%s)\n", $1, $2, ($3 == "yes" ? ", possibly squash-merged" : "") }'
fi

printf '\nLive\n'
if [ -z "$deploys" ]; then printf '  No DEPLOY records — what is live is unknown.\n'; fi
while IFS="$SEP" read -r app env rev date by; do
    [ -n "$app" ] || continue
    if [ "$rev" = none ]; then printf '  %s %s: nothing deployed since %s\n' "$app" "$env" "$date"; continue; fi
    d="$(distance "$rev")"; behind_n="${d%% *}"; ahead_n="${d##* }"
    case "$d" in
        unknown) if $objects_local; then note="revision not resolvable here"; else note="distance unknown: this partial clone's Git is older than 2.45"; fi ;;
        "0 0") note="matches $base" ;;
        "0 "*) note="$ahead_n deployed commit(s) not on $base" ;;
        *" 0") note="$behind_n commit(s) on $base not deployed" ;;
        *) note="$behind_n commit(s) on $base not deployed, $ahead_n deployed commit(s) not on $base" ;;
    esac
    printf '  %s %s: %s on %s (%s)\n' "$app" "$env" "$rev" "$date" "$note"
done <<< "$deploys"

printf '\nHolds and waivers\n'
if [ -z "$holds" ]; then printf '  None recorded.\n'; fi
while IFS="$SEP" read -r type id who scope until state check reason; do
    [ -n "$type" ] || continue
    case "$state" in
        due) note=' — DUE, still applies; owner to confirm or lift' ;;
        expired) note=' — EXPIRED, no longer applies' ;;
        *) note='' ;;
    esac
    printf '  %s %s on %s%s by %s, until %s%s%s\n' "$type" "$id" "$scope" "${check:+ (check $check)}" "$who" "$until" \
        "$note" "${reason:+ — $reason}"
done <<< "$holds"

printf '\nOwner queue\n'
queued=0
while IFS="$SEP" read -r line date who state words _; do
    [ -n "$line" ] || continue
    [ "$state" = open ] || continue
    queued=$((queued + 1))
    printf '  %s %s, %s days: %s\n' "$date" "$who" "$(age "$date")" "$words"
done <<< "$inbox"
[ "$queued" -gt 0 ] || printf '  Nothing open.\n'

printf '\nInbox awaiting agent processing\n'
queued=0
while IFS="$SEP" read -r line date who state words _; do
    [ "$state" = processing ] || [ "$state" = unknown ] || continue
    queued=$((queued + 1))
    printf '  %s %s, %s days: %s%s\n' "$date" "$who" "$(age "$date")" "$words" "$([ "$state" = unknown ] && echo ' (no disposition)')"
done <<< "$inbox"
[ "$queued" -gt 0 ] || printf '  Nothing awaiting incorporation.\n'

printf '\nLongest pole\n'
if [ -n "$pole" ]; then printf '  %s — waits on %s\n' "$pole" "$pole_waits"; else printf '  None: no open changes, holds or owner questions.\n'; fi
printf '\nMerged, live and validated are distinct; acceptance results need their linked evidence.\n'
