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
today="$(spec_today)"
layout="$(spec_layout "$root")"
mixed="$(spec_mixed_forms "$root")"
SEP="$SPEC_SEP"

age() { # date -> days since, or blank
    if spec_is_date "$1"; then spec_days_between "$1" "$today"; fi
}
js() { printf '"%s"' "$(json_escape "$1")"; }

items="$(spec_items "$root")"
now_next="$(spec_stage_now "$root")"; stage_now="${now_next%%"$SEP"*}"; stage_next="${now_next#*"$SEP"}"
# Live items marked for a stage after the current one: needed later, not now.
later="$(if [ -n "$stage_now" ]; then
    { printf '%s\n' "$items" | awk -F "$SEP" 'NF && $5 == "yes" { print "R" FS $1 }'
      spec_stages "$root" | awk -F "$SEP" '{ print "S" FS $1 FS $2 }'; } |
    awk -F "$SEP" -v now="$stage_now" "$SPEC_STAGE_AWK"'
        $1 == "R" { gone[$2] = 1; next }
        !($2 in gone) && stage_rank($3) > stage_rank(now) { out = out (out ? ", " : "") $2 " (" $3 ")" }
        END { print out }'
fi)"
changes="$(spec_open_changes "$root")"
leftover="$(spec_leftover_features "$root")"
inbox="$(spec_inbox "$root")"
records="$(spec_records "$root")"

# --- Git: default branch, unmerged branches, deployments behind it ------------------------
# Reading a missing object in a partial clone would fetch it from the promisor remote. Git 2.45+
# (and some maintenance releases) refuse under GIT_NO_LAZY_FETCH; in a partial clone any older
# Git skips the object reads instead (squash detection, deploy distance), reporting them unknown.
export GIT_NO_LAZY_FETCH=1
objects_local=true
base=""; branches=""
if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    if { [ -n "$(git -C "$root" config --get extensions.partialclone 2>/dev/null)" ] ||
            git -C "$root" config --get-regexp '^remote\..*\.promisor$' 2>/dev/null | grep -qi ' true$'; } &&
        [ "$(git version 2>/dev/null | awk '{ split($3, v, "."); print v[1] * 1000 + v[2] }')" -lt 2045 ]; then
        objects_local=false
    fi
    base="$(git -C "$root" symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null || true)"
    for candidate in main master; do
        [ -n "$base" ] && break
        git -C "$root" show-ref --verify --quiet "refs/heads/$candidate" && base="$candidate"
    done
    if [ -n "$base" ]; then
        branches="$(git -C "$root" for-each-ref --no-merged="$base" --sort=committerdate \
            --format="%(refname:short)$SEP%(committerdate:short)" refs/heads 2>/dev/null || true)"
    fi
fi
# A squash merge leaves the branch unmerged in git's eyes: when the default branch has, or had
# at a commit after the merge base, the branch's version of every file it changed, the branch is
# possibly squash-merged. Later commits may have changed those files again.
squashed() { # branch -> yes|no
    local mb files=() file rc=0 commit
    $objects_local || { echo no; return 0; }
    mb="$(git -C "$root" merge-base "$base" "$1" 2>/dev/null)" || { echo no; return 0; }
    # A diff that cannot read its objects (a tree-filtered clone) is not evidence of a squash.
    git -C "$root" diff --quiet --no-renames "$mb" "$1" >/dev/null 2>&1 || rc=$?
    [ "$rc" -le 1 ] || { echo no; return 0; }
    while IFS= read -r -d '' file; do files+=(":(literal)$file"); done < <(git -C "$root" diff --no-renames --name-only -z "$mb" "$1" 2>/dev/null)
    if [ "${#files[@]}" -eq 0 ] || git -C "$root" diff --quiet "$base" "$1" -- "${files[@]}" 2>/dev/null; then echo yes; return 0; fi
    # The squash commit is usually among the first after the merge base to touch those files.
    while IFS= read -r commit; do
        git -C "$root" diff --quiet "$commit" "$1" -- "${files[@]}" 2>/dev/null && { echo yes; return 0; }
    done < <(git -C "$root" log --reverse --format=%H "$mb..$base" -- "${files[@]}" 2>/dev/null | head -n 20)
    echo no
}
if [ -n "$branches" ]; then
    branches="$(printf '%s\n' "$branches" | while IFS="$SEP" read -r branch date; do
        [ -n "$branch" ] && printf '%s\037%s\037%s\n' "$branch" "$date" "$(squashed "$branch")"
    done)"
fi

# --- Records: active and expired holds/waivers, latest deploy per app and environment ------
holds=""; deploys=""
while IFS="$SEP" read -r line type name who scope env rev date until check reason; do
    [ -n "$line" ] || continue
    case "$type" in
        HOLD|WAIVER)
            state=active
            if spec_is_date "$until" && [ "$(spec_days_between "$until" "$today")" -gt 0 ]; then state=expired; fi
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
        counts="$(git -C "$root" rev-list --left-right --count "$base...$1" 2>/dev/null)"; then
        read -r behind_n ahead_n <<< "$counts"
        printf '%s %s\n' "$behind_n" "$ahead_n"
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
    hold="$(printf '%s' "$holds" | awk -F '\037' '$1 == "HOLD" && $6 == "active" { print; exit }')"
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

# --- Output -----------------------------------------------------------------------------------
if $json; then
    printf '{"date":%s,"layout":%s,"mixed_spec_forms":%s,"stage":{"now":%s,"next":%s,"later":%s},"items":[' "$(js "$today")" "$(js "$layout")" \
        "$([ -n "$mixed" ] && echo true || echo false)" "$(js "$stage_now")" "$(js "$stage_next")" "$(js "$later")"
    sep=""
    while IFS="$SEP" read -r id title mat planned retired unenf where; do
        [ -n "$id" ] || continue
        printf '%s{"id":%s,"title":%s,"maturity":%s,"planned":%s,"retired":%s,"unenforced":%s,"at":%s}' "$sep" \
            "$(js "$id")" "$(js "$title")" "$(js "$mat")" "$(js "$planned")" "$([ "$retired" = yes ] && echo true || echo false)" \
            "$([ "$unenf" = yes ] && echo true || echo false)" "$(js "$where")"
        sep=,
    done <<< "$items"
    printf '],"changes":['; sep=""
    while IFS="$SEP" read -r id title opened step waits nxt parent total passed failed maxatt stalled_items loc; do
        [ -n "$id" ] || continue
        printf '%s{"id":%s,"title":%s,"opened":%s,"age_days":%s,"step":%s,"waiting_on":%s,"next":%s,"parent":%s,"location":%s,"acceptance":{"total":%s,"passed":%s,"failed_or_blocked":%s,"max_attempts":%s,"stalled":%s}}' "$sep" \
            "$(js "$id")" "$(js "$title")" "$(js "$opened")" "$(a="$(age "$opened")"; echo "${a:-null}")" "$(js "$step")" "$(js "$waits")" \
            "$(js "$nxt")" "$(js "$parent")" "$(js "$loc")" "$total" "$passed" "$failed" "$maxatt" "$(js "$stalled_items")"
        sep=,
    done <<< "$changes"
    printf '],"leftover_features":['; sep=""
    while IFS="$SEP" read -r id done_n total_n; do
        [ -n "$id" ] || continue
        printf '%s{"id":%s,"tasks_done":%s,"tasks_total":%s}' "$sep" "$(js "$id")" "$done_n" "$total_n"
        sep=,
    done <<< "$leftover"
    printf '],"deployments":['; sep=""
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
    while IFS="$SEP" read -r line date who state words; do
        [ -n "$line" ] || continue
        [ "$state" = open ] || [ "$state" = unknown ] || continue
        printf '%s{"line":%s,"date":%s,"age_days":%s,"from":%s,"state":%s,"words":%s}' "$sep" \
            "$line" "$(js "$date")" "$(age "$date")" "$(js "$who")" "$(js "$state")" "$(js "$words")"
        sep=,
    done <<< "$inbox"
    printf '],"unmerged_branches":['; sep=""
    while IFS="$SEP" read -r branch date squash; do
        [ -n "$branch" ] || continue
        printf '%s{"branch":%s,"last_commit":%s,"possibly_squash_merged":%s}' "$sep" "$(js "$branch")" "$(js "$date")" \
            "$([ "$squash" = yes ] && echo true || echo false)"
        sep=,
    done <<< "$branches"
    printf '],"longest_pole":{"item":%s,"waits_on":%s},"readiness":"requires-evidence-review"}\n' "$(js "$pole")" "$(js "$pole_waits")"
    exit 0
fi

printf 'Product-Kit status — %s (derived from local files and git; read-only)\n' "$today"
case "$layout" in
    0.x) printf '\n0.x layout — run the cutover (.prdkit/workflows/compact.md); 1.0 views below are partial.\n' ;;
    both) printf '\nTwo canons: 1.0 spec files and 0.x folders (specs/product-spec, specs/targets or specs/features) — finish the cutover (.prdkit/workflows/compact.md).\n' ;;
    none) printf '\nNo spec yet — start specs/product.md or specs/SPEC.md (.prdkit/workflows/init.md).\n' ;;
esac
[ -z "$mixed" ] || printf '\nTwo spec forms: specs/SPEC.md beside %s — keep one; the views below combine both.\n' "$mixed"

printf '\nTrue now\n'
[ -z "$stage_now" ] || printf '  Stage: %s%s\n' "$stage_now" "${stage_next:+; next: $stage_next}"
[ -z "$later" ] || printf '  For later stages, not needed now: %s\n' "$later"
printf '%s\n' "$items" | awk -F '\037' '
    NF {
        any = 1
        k = $1; sub(/-.*/, "", k); if ($1 ~ /^P[0-9]+$/) k = "P"; else if ($1 ~ /^P[0-9]+\./) k = substr($1, index($1, ".") + 1, 1)  # awk-bytes-ok: sub() reads k only as $1; the one-letter k is a subscript, compared with ==
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

printf '\nIn flight\n'
if [ -z "$changes" ] && [ -z "$leftover" ]; then printf '  No open changes.\n'; fi
while IFS="$SEP" read -r id title opened step waits nxt parent total passed failed maxatt stalled_items loc; do
    [ -n "$id" ] || continue
    a="$(age "$opened")"
    printf '  %s — %s\n' "$id" "$title"
    case "$loc" in ''|.) ;; *) printf '    in %s\n' "$loc" ;; esac
    printf '    step %s%s; acceptance %s/%s passed' "${step:-unknown}" "${a:+, opened $a days ago}" "$passed" "$total"
    [ "$failed" -eq 0 ] || printf ', %s failing or blocked' "$failed"
    [ "$maxatt" -eq 0 ] || printf ', up to %s attempts' "$maxatt"
    printf '\n'
    [ -z "$stalled_items" ] || printf '    stalled: %s (alternatives note and owner decision due)\n' "$stalled_items"
    [ -z "$waits" ] || printf '    waiting on: %s\n' "$waits"
    [ -z "$nxt" ] || printf '    next: %s\n' "$nxt"
    case "$parent" in ''|none) ;; *) printf '    parent: %s\n' "$parent" ;; esac
done <<< "$changes"
while IFS="$SEP" read -r id done_n total_n; do
    [ -n "$id" ] || continue
    printf '  0.x feature %s — still in specs/features/%s/, tasks %s/%s checked; finish the cutover: a change record if the owner keeps it open (.prdkit/workflows/compact.md)\n' "$id" "$id" "$done_n" "$total_n"
done <<< "$leftover"
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
    printf '  %s %s on %s%s by %s, until %s%s%s\n' "$type" "$id" "$scope" "${check:+ (check $check)}" "$who" "$until" \
        "$([ "$state" = expired ] && echo ' — EXPIRED, no longer applies')" "${reason:+ — $reason}"
done <<< "$holds"

printf '\nOwner queue\n'
queued=0
while IFS="$SEP" read -r line date who state words; do
    [ -n "$line" ] || continue
    [ "$state" = open ] || [ "$state" = unknown ] || continue
    queued=$((queued + 1))
    printf '  %s %s, %s days: %s%s\n' "$date" "$who" "$(age "$date")" "$words" "$([ "$state" = unknown ] && echo ' (no disposition)')"
done <<< "$inbox"
[ "$queued" -gt 0 ] || printf '  Nothing open.\n'

printf '\nLongest pole\n'
if [ -n "$pole" ]; then printf '  %s — waits on %s\n' "$pole" "$pole_waits"; else printf '  None: no open changes, holds or owner questions.\n'; fi
printf '\nMerged, live and validated are distinct; acceptance results need their linked evidence.\n'
