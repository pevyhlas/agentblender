#!/usr/bin/env bash
# doctor.sh — Read-only diagnostics: installation, spec layout and budgets, IDs, records,
# owner inbox, raw evidence, agent context and agent projections. Never repairs anything.
#
# Usage: doctor.sh [--json] [--strict] [--inventory]
# Errors (failing findings): installation drift, malformed IDs, malformed records, agent
# tier-map problems, a change record deleted without its .prdkit/history/log.md line. Everything else is an advisory; --strict makes advisories fail too.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/install-lib.sh"
source "$SCRIPT_DIR/context-budget-lib.sh"
source "$SCRIPT_DIR/agent-tiers-lib.sh"
source "$SCRIPT_DIR/spec-lib.sh"
source "$SCRIPT_DIR/diagnostics-lib.sh"
source "$SCRIPT_DIR/review-policy-lib.sh"
json=false; strict=false; inventory=false
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) json=true ;;
        --strict) strict=true ;;
        --inventory) inventory=true ;;
        *) echo 'Usage: doctor.sh [--json] [--strict] [--inventory]' >&2; exit 2 ;;
    esac
    shift
done
root="$(get_repo_root)"; findings=(); advisories=()
report() {
    local result=pass reason
    [ "${#findings[@]}" -eq 0 ] || result=fail
    if $json; then
        printf '{"result":"%s","findings":%s,"advisories":%s,"trace":{%s},"strict":%s,"readiness":"not-evaluated"' \
            "$result" "$(json_array ${findings[@]+"${findings[@]}"})" "$(json_array ${advisories[@]+"${advisories[@]}"})" "$trace_json" "$strict"
        printf ',"adoption":%s' "$(diagnostic_adoption "$root")"
        printf ',"budget_excess_reading_lines":%s' "$budget_excess_reading_lines"
        if [ "${#authority_records[@]}" -gt 0 ]; then printf ',"authority_records":%s' "$(json_array "${authority_records[@]}")"; fi
        if $inventory; then printf ',"configuration":%s' "$(diagnostic_configuration "$root")"; fi
        printf '}\n'
    else
        printf 'Product-Kit doctor: %s\n' "$result"
        diagnostic_adoption "$root" --text
        printf 'Reading lines over budget (total): %s\n' "$budget_excess_reading_lines"
        for reason in ${findings[@]+"${findings[@]}"}; do printf '  %s\n' "$reason"; done
        for reason in ${advisories[@]+"${advisories[@]}"}; do printf '  advisory: %s\n' "$reason"; done
        [ "${#advisories[@]}" -eq 0 ] || printf 'Advisories do not fail doctor; --strict makes them failing findings.\n'
        for reason in ${authority_records[@]+"${authority_records[@]}"}; do printf '  checked authority: %s\n' "$reason"; done
        printf 'No repairs performed. A clean report is not readiness.\n'
        if $inventory; then
            printf 'Effective configuration (JSON; null means unknown, not a default):\n'
            diagnostic_configuration "$root"; printf '\n'
        fi
    fi
    [ "$result" = pass ]
}
trace_json=""; budget_excess_reading_lines=0; authority_records=()
if history_problem="$(spec_require_history_layout "$root" --head 2>&1)"; then :
elif [ "$?" -eq 2 ]; then
    advisories+=("$history_problem")
else
    findings+=("$history_problem")
    report
    exit 1
fi
today="$(spec_today)"; SEP="$SPEC_SEP"
in_git=false
git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1 && in_git=true
has_head=false
if $in_git; then
    if head_ref="$(git -C "$root" symbolic-ref --quiet HEAD)"; then
        if git -C "$root" show-ref --verify --quiet "$head_ref"; then has_head=true
        elif [ "$?" -ne 1 ]; then exit 1; fi
    elif [ "$?" -eq 1 ]; then
        git -C "$root" rev-parse --verify HEAD >/dev/null
        has_head=true
    else exit 1; fi
fi

# --- Installation ------------------------------------------------------------------------------
if rows="$(verify_lines install_findings "$root")"; then
    while IFS= read -r finding; do [ -z "$finding" ] || findings+=("$finding"); done <<< "$rows"
else
    findings+=("Installation inventory did not complete")
fi
for file in "$root"/specs/changes/*.md; do
    [ -f "$file" ] || continue
    if ! review_record_format "$file"; then
        findings+=("${file#"$root"/}: expected LF-terminated change Markdown with no **Review** field in State; current reviews are native GitHub reviews")
        continue
    fi
    if ! checkpoint_problem="$(spec_change_progress "$file" 2>&1)"; then
        findings+=("$checkpoint_problem")
        continue
    fi
    rows="$(verify_lines spec_change_progress "$file" false advisories)"
    while IFS= read -r advisory; do
        [ -z "$advisory" ] || advisories+=("${file#"$root"/}: $advisory")
    done <<< "$rows"
done
for file in .prdkit/workflows/protocol.md .prdkit/workflows/change.md .prdkit/workflows/review.md .prdkit/memory/constitution.md .prdkit/templates/change-template.md; do
    [ -f "$root/$file" ] || findings+=("Missing installed file: $file")
done

# --- Layout --------------------------------------------------------------------------------------
if ! layout_problem="$(spec_require_layout "$root" 2>&1)"; then findings+=("$layout_problem"); fi
mixed="$(spec_mixed_forms "$root")"
[ -z "$mixed" ] || advisories+=("Two spec forms: specs/SPEC.md beside $mixed; keep one form — split SPEC.md fully in one change and delete it, or fold the split files back (.prdkit/workflows/spec.md)")

# --- Reading budgets (never targets; never drop an item to fit) -------------------------------
# A line counts once per 100 characters (spec_reading_lines), so a long row cannot dodge one.
budget_fix() { # limit remedy path...
    local limit="$1" fix="$2" path reading lines size problem measured diagnostics; shift 2
    for path in "$@"; do
        [ -f "$path" ] || continue
        if ! verify_lf_file "$path" allow-empty; then
            findings+=("Invalid spec text: ${path#"$root"/} (expected LF-terminated text)")
        fi
        if ! measured="$(spec_reading_lines "$path")"; then
            findings+=("Budget reader failed: ${path#"$root"/}; reading budget is unverified")
            continue
        fi
        read -r reading lines <<< "$measured"
        if ! [[ "$reading" =~ ^[0-9]+$ && "$lines" =~ ^[0-9]+$ ]]; then
            findings+=("Budget reader failed: ${path#"$root"/}; missing line counts")
            continue
        fi
        if ! diagnostics="$(verify_lines spec_navigation "$path" diagnostics)"; then
            findings+=("Navigation diagnostics failed: ${path#"$root"/}")
            continue
        fi
        while IFS= read -r problem; do
            [ -z "$problem" ] || advisories+=("${path#"$root"/}: $problem")
        done <<< "$diagnostics"
        [ "$reading" -gt "$limit" ] || continue
        budget_excess_reading_lines=$((budget_excess_reading_lines + reading - limit))
        size="$lines lines that read"; [ "$lines" != 1 ] || size="1 line that reads"
        if [ "$reading" = "$lines" ]; then size="${size% that read*}"; else size="$size as $reading at 100 characters a line"; fi
        advisories+=("Budget: ${path#"$root"/} has $size, over its $limit-line reading budget; $fix")
    done
}
budget() { # limit path...
    budget_fix "$1" "tighten the wording, move detail to code or tests, or split it (spec.md, Layout); never drop an item to fit" "${@:2}"
}
# In the 1.1 layout (beside specs/product/) product.md and architecture.md are one-page summaries;
# the 1.0 single-file forms keep 150 and 300.
if [ -d "$root/specs/product" ]; then
    budget 80 "$root"/specs/product.md; budget 100 "$root"/specs/architecture.md
else
    budget 150 "$root"/specs/product.md; budget 300 "$root"/specs/architecture.md
fi
design="specs/design.md"
if [ -f "$root/$design" ]; then
    budget_fix 100 "tighten the summary and link longer decisions; keep implementation values in code" "$root/$design"
    # shellcheck disable=SC2016 # awk uses $0 for the input line
    rows="$(verify_lines awk "$SPEC_METADATA_SKIP_AWK"'
        !metadata_skip($0) {
            h = $0; sub(/^ ? ? ?##[[:blank:]]+/, "", h)
            if (h == $0) next
            sub(/[[:blank:]]+#+[[:blank:]]*$/, "", h); sub(/[[:blank:]]+$/, "", h)
            seen[h] = 1
        }
        END {
            n = split("Direction Composition Copy Decisions", sections, " ")
            for (i = 1; i <= n; i++) if (!(sections[i] in seen)) print sections[i]
        }' "$root/$design")"
    while IFS= read -r section; do
        [ -z "$section" ] || advisories+=("$design: missing '## $section' heading (design-template.md)")
    done <<< "$rows"
fi
# Each budget has a split (spec.md, Layout), and each piece keeps the budget of its parent.
budget 150 "$root"/specs/product/users.md "$root"/specs/product/users/*.md \
    "$root"/specs/product/non-goals.md "$root"/specs/product/non-goals/*.md \
    "$root"/specs/product/lifecycle.md "$root"/specs/product/lifecycle/*.md "$root"/specs/product/journeys/*.md \
    "$root"/specs/observed.md "$root"/specs/observed/*.md "$root"/specs/changes/*.md "$root"/.prdkit/history/archive.md
budget 300 "$root"/specs/product/requirements.md "$root"/specs/product/qualities.md "$root"/specs/product/constraints.md \
    "$root"/specs/product/requirements/*.md "$root"/specs/product/qualities/*.md "$root"/specs/product/constraints/*.md \
    "$root"/specs/architecture/*.md "$root"/specs/architecture/*/*.md "$root"/specs/decisions/*.md \
    "$root"/specs/SPEC.md
budget 200 "$root"/specs/parts/*.md "$root"/specs/parts/*/*.md
budget_fix 150 "condense its older entries into .prdkit/history/archive.md at the next compaction (compact.md)" "$root"/.prdkit/history/log.md
budget_fix 150 "incorporate answered items (spec.md, Resolve owner answers), review the rest with the owner (/prdkit-inbox), and keep each item's context in the files it links" "$root"/specs/inbox.md
budget_fix 150 "remove lifted holds, expired waivers, and DEPLOY and ATTEST lines a newer one supersedes; git keeps them. Move long owner quotes to specs/authority.md with a checked ref=; keep the full scope and end condition in the row" "$root"/specs/records.md
# specs/authority.md is read on demand by the records checker and has no reading budget.

# Share status's log-order and entry-file accounting; compaction is due at ten closes.
archive_verified=true
if archive_details="$(spec_archive_log_details "$root" 2>&1)"; then
    IFS="$SPEC_SEP" read -r archive_index archive_pointer archive_line allocation_floor floor_line <<< "$archive_details"
    archive_max="${archive_index##* }"; archive_max="${archive_max##*-}"
    if [ "${allocation_floor:-"-"}" = - ]; then
        if [ "$archive_index" != "${archive_index%% *}" ]; then
            findings+=(".prdkit/history/archive.md:$archive_line: a sparse index requires an Allocated through line")
        fi
        allocation_floor=0
    elif [ "$allocation_floor" -lt "$archive_max" ]; then
        findings+=(".prdkit/history/archive.md:$floor_line: Allocated through is below the highest indexed number $archive_max")
    fi
    previous_archive="$(spec_archive_log_previous "$root")"
    IFS="$SPEC_SEP" read -r _ _ _ previous_floor _ <<< "$previous_archive"
    if [ -n "$previous_floor" ] && [ "$previous_floor" != - ] && [ "$allocation_floor" -lt "$previous_floor" ]; then
        if [ "${floor_line:-0}" -eq 0 ]; then floor_line="${archive_line:-0}"; fi
        findings+=(".prdkit/history/archive.md:$((floor_line > 0 ? floor_line : 1)): Allocated through decreased from $previous_floor to $allocation_floor")
    fi
    if [ -n "$archive_pointer" ] && [ "$archive_pointer" != - ]; then
        if archive_proof="$(spec_archive_log_proof "$root" "$archive_index" "$archive_pointer")"; then :
        elif [ "$?" -eq 2 ]; then advisories+=(".prdkit/history/archive.md:$archive_line: $archive_proof")
        else findings+=(".prdkit/history/archive.md:$archive_line: ${archive_proof:-cannot verify sparse snapshot $archive_pointer}")
        fi
    fi
else
    archive_verified=false
    findings+=("$archive_details")
    archive_index=0
fi
compaction_verified=true
if compaction="$(spec_compaction "$root" "$archive_index")"; then
    read -r closed_since last_pass <<< "$compaction"
    if $archive_verified && [ "$closed_since" -ge 10 ]; then
        since="and no pass recorded in .prdkit/history/log.md"
        [ -z "$last_pass" ] || since="since the last pass ($last_pass)"
        advisories+=("Compaction due: $closed_since changes closed $since (.prdkit/workflows/compact.md, Compaction)")
    fi
else
    compaction_verified=false
    findings+=("Compaction reader failed: could not count closes from .prdkit/history/log.md and .prdkit/history/log/; compaction status is unverified")
fi

# --- IDs ---------------------------------------------------------------------------------------
items="$(verify_lines spec_items "$root" --skip-invalid-journey-ids)"
rows="$(printf '%s\n' "$items" | awk -F "$SEP" 'NF { if ($1 in at) print "Duplicate ID " $1 ": defined at " at[$1] " and " $7; else at[$1] = $7 }')"
while IFS= read -r problem; do [ -z "$problem" ] || findings+=("$problem"); done <<< "$rows"
parts="$(verify_lines spec_part_headings "$root")"
part_graph="$(verify_lines spec_part_graph "$items" "$parts")"
known_parts=" "; part_parents=" "
while IFS="$SEP" read -r kind name target; do
    case "$kind" in
        part) known_parts="$known_parts$name " ;;
        edge) part_parents="$part_parents$target " ;;
        error) findings+=("$name") ;;
    esac
done <<< "$part_graph"
while IFS="$SEP" read -r file part _; do
    [ -n "$file" ] || continue
    if [ -z "$part" ]; then advisories+=("$file: no '# P<n> name' heading, so its behaviors and invariants cannot be checked against their part"); continue; fi
    rows="$(printf '%s\n' "$items" | awk -F "$SEP" -v file="$file" -v part="$part" '
            index($7, file ":") == 1 && $1 ~ /^P.*\.[BI]-/ {
                owner = $1; sub(/\.[BI]-.*/, "", owner)
                if (owner != part) print $1 " is defined in " $7 ", the part file of " part }')"
    while IFS= read -r problem; do [ -z "$problem" ] || findings+=("$problem"); done <<< "$rows"
done <<< "$parts"
listed="$(verify_lines spec_files "$root")"
rows="$(
    while IFS= read -r file; do
        [ -n "$file" ] || continue
        awk -v file="$file" "$SPEC_SKIP_AWK"'{ if (spec_skip($0)) next }
            /^[[:space:]]*[-*][[:space:]]+\*\*(P[0-9]+(\.[0-9]+)*\.)?[JRQCDNBI]-/ && !/^[[:space:]]*[-*][[:space:]]+\*\*(P[0-9]+(\.[0-9]+)?\.[BI]|[JRQCDN])-[0-9][0-9]+[[:space:]*]/ { print file ":" NR }
            /^#[[:space:]]+J-/ && !/^#[[:space:]]+J-[0-9][0-9]+[[:space:]]/ { print file ":" NR }
            /^##?#?[[:space:]]+D-/ && !/^##?#?[[:space:]]+D-[0-9][0-9]+[[:space:]]/ { print file ":" NR }' "$root/$file" || exit 1
    done <<< "$listed")"
while IFS= read -r where; do [ -z "$where" ] || findings+=("Malformed ID at $where: IDs are J-, R-, Q-, C-, N-, D- or <part>.B-/<part>.I- with two or more digits"); done <<< "$rows"

cite_files=()
test_files="$(verify_lines spec_test_files "$root")"
while IFS= read -r file; do [ -z "$file" ] || cite_files+=("$file"); done <<< "$listed"
while IFS= read -r file; do
    [ -n "$file" ] || continue
    if [ -L "$root/$file" ] && [ ! -e "$root/$file" ]; then
        advisories+=("$file: test symlink target is absent; citations not checked")
    else
        cite_files+=("$file")
    fi
done <<< "$test_files"
# Design cites canonical items but does not define them.
[ ! -f "$root/$design" ] || cite_files+=("$design")
if [ "${#cite_files[@]}" -gt 0 ]; then
    rows="$(
        { printf '%s\n' "$items" | awk -F "$SEP" 'NF { print "D" FS $1 FS $5 }' || exit 1
          spec_citations "$root" "${cite_files[@]}" | awk -F "$SEP" '{ print "C" FS $1 FS $2 }'; } |
        awk -F "$SEP" '
            $1 == "D" { defined[$2] = 1; if ($3 == "yes") retired[$2] = 1; next }
            { if (!($2 in defined)) { if (!(($2) in undef)) { undef[$2] = $3; order[++n] = $2 } }
              else if (($2 in retired) && $3 !~ /^specs\//) { if (!(($2) in ret)) { ret[$2] = $3; rorder[++m] = $2 } } }
            END {
                for (i = 1; i <= n; i++) print "ID " order[i] " is cited at " undef[order[i]] " but defined nowhere in the spec"
                for (i = 1; i <= m; i++) print "Retired ID " rorder[i] " is still cited by a test at " ret[rorder[i]] "; edit the test or the line"
            }')"
    while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done <<< "$rows"
fi

# --- References: every link resolves; every spec file is reached from a summary ---------------
# A budget is safe only because a file past it splits and its parent links each piece; a piece
# no summary reaches is lost to readers. A retired file (`retired:` in its heading) is exempt.
# Reach is checked in the 1.1 layout, whose summaries link every piece; a folder link reaches
# the files directly in it. Pieces of observed.md must be linked from it.
# shellcheck disable=SC2016
reach_awk='
    $1 == "F" { n++; file[n] = $2; retired[$2] = $3; next }
    $1 == "E" { to[$2] = to[$2] FS $4 }
    END {
        t = 0
        for (i = 1; i <= n; i++) if (index(" " roots " ", " " file[i] " ")) { seen[file[i]] = 1; q[++t] = file[i] }
        for (h = 1; h <= t; h++) {
            m = split(to[q[h]], d, FS)
            for (j = 2; j <= m; j++) for (i = 1; i <= n; i++)
                if (!(file[i] in seen) && (file[i] == d[j] || (index(file[i], d[j] "/") == 1 && !index(substr(file[i], length(d[j]) + 2), "/")))) { seen[file[i]] = 1; q[++t] = file[i] }
        }
        if (t || always) for (i = 1; i <= n; i++) if (!(file[i] in seen) && !retired[file[i]]) print file[i] " " msg
    }'
reach() { # roots message always links file...: always reports even when no root exists
    local roots="$1" msg="$2" always="$3" edges="$4" file; shift 4
    { for file in "$@"; do
          awk -v f="$file" '/^#/ && !seen++ { r = (tolower($0) ~ /retired:/) } END { print "F\037" f "\037" (r ? 1 : 0) }' "$root/$file" || return 1
      done
      printf '%s\n' "$edges" | awk 'NF { print "E\037" $0 }'; } |
    awk -F "$SEP" -v roots="$roots" -v msg="$msg" -v always="$always" "$reach_awk"
}
missing_links() { # links
    local file line target
    while IFS="$SEP" read -r file line target; do
        if [ -n "$target" ] && [ ! -e "$root/$target" ]; then
            printf 'Link at %s:%s points to %s, which does not exist\n' "$file" "$line" "$target"
        fi
    done <<< "$1"
}
spec_list=(); while IFS= read -r file; do [ -z "$file" ] || spec_list+=("$file"); done <<< "$listed"
summaries="product.md, architecture.md"
if [ -f "$root/$design" ]; then
    spec_list+=("$design")
    summaries="$summaries, design.md"
fi
links=""; [ "${#spec_list[@]}" -eq 0 ] || links="$(spec_links "$root" "${spec_list[@]}")"
observed=(); for file in "$root"/specs/observed.md "$root"/specs/observed/*.md; do [ -f "$file" ] && observed+=("${file#"$root"/}"); done
observed_links=""; [ "${#observed[@]}" -eq 0 ] || observed_links="$(spec_links "$root" "${observed[@]}")"
archive_links=""; [ ! -f "$root/.prdkit/history/archive.md" ] || archive_links="$(spec_links "$root" .prdkit/history/archive.md)"
while IFS="$SEP" read -r file line target; do
    case "$target" in
        .prdkit/history|.prdkit/history/*)
            findings+=("$file:$line: spec links to history ($target); state current truth in the spec, not in its history")
            ;;
    esac
done <<< "$links"
rows="$(
    missing_links "$links" && missing_links "$observed_links" && missing_links "$archive_links" || exit 1
    if [ -d "$root/specs/product" ] && [ "${#spec_list[@]}" -gt 0 ]; then
        reach "specs/product.md specs/architecture.md $design specs/SPEC.md" \
            "is reached from no summary ($summaries or a file they link); give it a line and a link in its parent (spec.md, Layout)" \
            0 "$links" "${spec_list[@]}" || exit 1
    fi
    if [ -d "$root/specs/observed" ] && [ "${#observed[@]}" -gt 0 ]; then
        reach "specs/observed.md" "is not linked from specs/observed.md; give it a line and a link there" \
            1 "$observed_links" "${observed[@]}"
    fi)"
while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done <<< "$rows"

# --- Lifecycle: the current stage, from: markers, the product page's stage columns ----------
stages="$(verify_lines spec_stages "$root")"
spec_lifecycle_load "$root"
now="$SPEC_STAGE_NOW"
while IFS= read -r problem; do [ -z "$problem" ] || findings+=("$problem"); done <<< "$SPEC_STAGE_ERRORS"
# Without an explicit vocabulary, retain the legacy source-note advisory. An explicit order
# makes markers strict. Derived classification tools reject unknown markers in either form.
rows="$(printf '%s\n' "$stages" | spec_stage_problems)"
while IFS= read -r problem; do
    [ -n "$problem" ] || continue
    if [ "$SPEC_STAGE_CUSTOM" = 1 ]; then findings+=("$problem"); else advisories+=("$problem"); fi
done <<< "$rows"
if [ -n "$SPEC_STAGE_ERRORS" ]; then now=""; fi
if [ -n "$stages" ] && [ -z "$now" ] && [ -z "$SPEC_STAGE_ERRORS" ]; then
    advisories+=("Lifecycle: items carry from: markers, but the canonical lifecycle names no current stage, so every item counts as needed now")
fi
if [ -n "$now" ]; then
    if [ -n "$stages" ]; then
        reached="$(printf '%s\n' "$stages" | awk -F "$SEP" -v now="$now" \
            -v stage_order="$SPEC_STAGE_ORDER" -v stage_custom="$SPEC_STAGE_CUSTOM" "$SPEC_STAGE_AWK"'
            NF && stage_rank($2) && stage_rank($2) <= stage_rank(now) { out = out (out ? ", " : "") $1 }
            END { print out }')"
        [ -z "$reached" ] || advisories+=("Lifecycle: the stage is $now, so remove the reached from: markers of $reached")
    fi
    # product.md may show journeys in stage columns; each must match the journey's own marker.
    if [ -f "$root/specs/product.md" ]; then
        rows="$(
            { printf '%s\n' "$stages" | awk -F "$SEP" 'NF && $1 ~ /^J-/ { print "S" FS $1 FS $2 }' || exit 1
              awk "$SPEC_SKIP_AWK"'{ if (spec_skip($0)) next } { print "L" "\037" FNR "\037" $0 }' "$root/specs/product.md"; } |
            awk -F "$SEP" -v now="$now" -v stage_order="$SPEC_STAGE_ORDER" -v stage_custom="$SPEC_STAGE_CUSTOM" "$SPEC_STAGE_AWK"'
                $1 == "S" { from[$2] = $3; next }
                $3 !~ /^\|/ { cols = 0; next }
                {
                    n = split($3, cell, "|")
                    if (!cols || $3 ~ /^\|[[:space:]:-]+\|/) {
                        if ($3 ~ /^\|[[:space:]:-]+\|/) next
                        cols = 0
                        for (i = 2; i < n; i++) { h = tolower(cell[i]); gsub(/\*|\(now\)/, "", h); r = stage_rank(h); colrank[i] = r; colname[i] = h; if (r) cols = 1 }
                        next
                    }
                    for (i = 2; i < n; i++) {
                        if (!colrank[i]) continue
                        t = cell[i]
                        while (match(t, /J-[0-9][0-9]+/)) {
                            j = substr(t, RSTART, RLENGTH); t = substr(t, RSTART + RLENGTH)
                            have = (j in from) ? stage_rank(from[j]) : 0
                            if (colrank[i] > stage_rank(now) && have != colrank[i])
                                print "product.md:" $2 " shows " j " in the" colname[i] "column, but its file has " (have ? "from: " from[j] : "no from: marker")
                            else if (colrank[i] <= stage_rank(now) && have > stage_rank(now))
                                print "product.md:" $2 " shows " j " in the" colname[i] "column, but its file says from: " from[j]
                        }
                    }
                }')"
        while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done <<< "$rows"
    fi
fi

# --- Environments: each stage runs in one the environments topic lists ------------------------
env_files=(); stage_files=()
for path in "$root"/specs/architecture/environments.md "$root"/specs/architecture/environments/*.md; do
    if [ -f "$path" ]; then env_files+=("${path#"$root"/}"); fi
done
for path in "$root"/specs/product/lifecycle.md "$root"/specs/product/lifecycle/*.md; do
    if [ -f "$path" ]; then stage_files+=("${path#"$root"/}"); fi
done
if [ "${#env_files[@]}" -gt 0 ] && [ "${#stage_files[@]}" -gt 0 ]; then
    rows="$(
        cd "$root" && awk "$SPEC_SKIP_AWK"'
            # A name as written: no emphasis, code, link target, or note after it (`TEST (early)`).
            function plain(s) {
                gsub(/\*|`/, "", s); sub(/\]\([^)]*\)/, "", s); gsub(/\[|\]/, "", s)
                gsub(/^[[:space:]_]+|[[:space:]_]+$/, "", s)
                sub(/[[:space:]]+(\(|—|–).*$/, "", s); sub(/:([[:space:]].*)?$/, "", s); return s
            }
            # Whether text names an environment, as a whole word spelled as the topic spells it.
            # A neighbour is tested with index(), never a regex: macOS awk counts bytes, and a
            # regex over the lone byte of a multibyte character (TEST—early) aborts it.
            function names(text,   s, e, at, pre, post, w) {
                w = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-"
                for (e in env) {
                    s = text
                    while ((at = index(s, e)) > 0) {
                        pre = at > 1 ? substr(s, at - 1, 1) : ""; post = substr(s, at + length(e), 1)
                        if (!(pre != "" && index(w, pre)) && !(post != "" && index(w, post))) return 1
                        s = substr(s, at + length(e))
                    }
                }
                return 0
            }
            # A stage Environment line, with its wrapped continuation, once it is complete.
            function check() {
                if (!open) return
                open = 0
                if (tolower(value) ~ /^none([^a-z]|$)/ || value ~ /^</ || names(value)) return
                printf "Environments: %s at %s:%d runs in none of the environments specs/architecture/environments.md lists (%s); name one, or add it there\n", (vstage != "" ? "the " vstage " stage" : "a stage"), vfile, vline, list
            }
            { sub(/\r$/, "") }
            FNR == 1 { check(); fence = 0; comment = 0; table = 0; stage = "" }
            { if (spec_skip($0)) next }
            # The environments: the first cells of the table headed `| Environment |`.
            FILENAME ~ /^specs\/architecture\/environments/ {
                if ($0 !~ /^\|/) { table = 0; next }
                split($0, cell, "|"); name = plain(cell[2])
                if (tolower(name) == "environment") { table = 1; headed = 1; next }
                if (table && name !~ /^:?-+:?$/ && name !~ /^</ && name != "") {
                    if (!(name in env)) list = list (list ? ", " : "") name
                    env[name] = 1
                }
                next
            }
            list == "" { exit }
            open && /^[[:space:]]+[^-*+[:space:]]/ { t = $0; gsub(/^[[:space:]]+|[[:space:]]+$/, "", t); value = value " " t; next }
            { check() }
            /^##[[:space:]]/ { stage = $0; sub(/^##[[:space:]]+/, "", stage); sub(/[[:space:]]*\([^)]*\)[[:space:]]*$/, "", stage); next }
            /^[[:space:]]*([-*+][[:space:]]+)?\*\*Environment(\*\*:|:\*\*)/ {
                value = $0; sub(/^[^:]*:(\*\*)?[[:space:]]*/, "", value); gsub(/[[:space:]]+$/, "", value)
                open = 1; vstage = stage; vfile = FILENAME; vline = FNR
            }
            END {
                check()
                if (list == "" && headed) print "Environments: the | Environment | table in specs/architecture/environments.md holds only placeholders; name each environment, so stages can name where they run"
                else if (list == "") print "Environments: specs/architecture/environments.md has no table headed | Environment |, so no stage can name where it runs"
            }
        ' "${env_files[@]}" "${stage_files[@]}")"
    while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done <<< "$rows"
fi

# --- Trace: spec to code, plans, retired IDs, allocation, change records -----------------------
# One advisory per check: its count and first locations. --json counts each check by its code.
trace() { # code message fix rows (one location each)
    local out n
    out="$(printf '%s\n' "$4" | awk 'NF { if (++n <= 5) at = at (n > 1 ? ", " : "") $0 }
        END { print n + 0 "\037" at (n > 5 ? " and " n - 5 " more" : "") }')"
    n="${out%%"$SEP"*}"; trace_json="${trace_json:+$trace_json,}\"$1\":$n"
    [ "$n" -eq 0 ] || advisories+=("$2 ($n): ${out#*"$SEP"}; $3")
}
pick() { printf '%s\n' "$1" | awk -F "$SEP" -v k="$2" '$1 == k { print $2 }'; } # rows kind -> rest
# A path after a Code: label that is not in the repository. Judged: a code span holding a "/",
# a bare word whose first directory exists, and a file name (`b.py`) once a path named before it
# in the same item gives it a directory; a path also resolves in the directory of one before it.
# Placeholders, globs, commands, URLs, absolute paths and org/repo:path are not judged; a
# #anchor or :line suffix is dropped.
code_paths_missing() {
    local kind path where prev="" bases="" base found code_rows
    local line_re='^(.*):[0-9]+(-[0-9]+)?$' ext_re='\.[A-Za-z][A-Za-z0-9]*$' odd_re="[][:space:]<>*?{}\$|=:'\"[]"
    code_rows="$(verify_lines spec_code_paths "$root")" || return 1
    while IFS="$SEP" read -r kind path where; do
        [ "$where" = "$prev" ] || { prev="$where"; bases=""; }
        path="${path#./}"; path="${path%%#*}"
        while :; do case "$path" in *[.,:\;!?/]) path="${path%?}" ;; *) break ;; esac; done
        if [[ "$path" =~ $line_re ]]; then path="${BASH_REMATCH[1]}"; fi
        case "$path" in ''|/*|~*|-*) continue ;; esac
        if [[ "$path" =~ $odd_re ]]; then continue; fi
        case "$kind" in
            span) case "$path" in */*) ;; *) [[ "$path" =~ $ext_re ]] || continue ;; esac ;;
            *) [ -e "$root/${path%%/*}" ] || continue ;;
        esac
        found=""
        if [ -e "$root/$path" ]; then found="$path"
        else
            while IFS= read -r base; do
                [ -n "$base" ] && [ -e "$root/$base/$path" ] && { found="$base/$path"; break; }
            done <<< "$bases"
        fi
        if [ -n "$found" ]; then
            [ -d "$root/$found" ] || case "$found" in */*) found="${found%/*}" ;; *) found=. ;; esac
            bases="$bases$found"$'\n'
        elif [ -n "$bases" ] || [ "${path#*/}" != "$path" ]; then
            printf '%s at %s\n' "$path" "$where"
        fi
    done <<< "$code_rows"
}
rows="$(code_paths_missing)"
trace code-path "Code path not in the repository" "name repo-relative paths that exist (spec.md, Writing)" "$rows"

# planned: <change> names an open change record (here, or in a worktree unless the log or archive
# closed it), else a closed or unknown one. org/repo#n names another repository's change.
closed_ids=' '; open_changes=''; entries=''
if $compaction_verified; then
    entries="$(spec_log_entries "$root")"
    closed_ids=" $(sed -n 's/^- [0-9-]* \([^ :]*\):.*/\1/p' <<< "$entries" | tr '\n' ' ')"
    if $archive_verified; then open_changes="$(verify_lines spec_open_changes "$root" "$archive_index")"; fi
fi
open_ids=" "
checkouts="$(verify_lines spec_checkouts "$root")"
while IFS="$SEP" read -r path _ loc; do
    for file in "$path"/specs/changes/*.md; do
        [ -f "$file" ] || continue
        id="${file##*/}"; id="${id%.md}"
        if $archive_verified && spec_archive_log_covers "$archive_index" "$id"; then
            [ "$loc" != . ] || findings+=("specs/changes/$id.md is open but its number is condensed in .prdkit/history/archive.md")
            continue
        fi
        [ "$loc" = . ] || case "$closed_ids" in *" $id "*) continue ;; esac
        open_ids="$open_ids$id "
    done
done <<< "$checkouts"
while IFS="$SEP" read -r id _ _ _ _ _ _ _ _ _ _ _ ws ns loc; do
    [ -n "$id" ] || continue
    file="specs/changes/$id.md"
    [ "$loc" = . ] || file="$loc/$file"
    [ "$ws" = present ] || advisories+=("$file (## State): Waiting on is $ws")
    [ "$ns" = present ] || advisories+=("$file (## State): Next is $ns")
done <<< "$open_changes"
planned_closed=""; planned_unknown=""
rows="$(verify_lines spec_planned_at "$root")"
while IFS="$SEP" read -r id where; do
    [ -n "$id" ] || continue
    $archive_verified || continue
    if spec_archive_log_covers "$archive_index" "$id"; then
        findings+=("Planned for a closed change: ${id%-} at $where is condensed in .prdkit/history/archive.md; remove the marker or re-plan it to an open change")
        continue
    fi
    case "$open_ids" in *" ${id%%-*}-"*) continue ;; esac
    case "$closed_ids" in
        *" ${id%%-*}-"*) planned_closed+="${id%-} at $where"$'\n' ;;
        *) planned_unknown+="${id%-} at $where"$'\n' ;;
    esac
done <<< "$rows"
xrepo_re='^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(#[0-9]+)?$'
while IFS="$SEP" read -r _ _ _ target retired _ where; do
    [ "$retired" = no ] || continue
    target="${target//[\`*]/}"
    while [ "${target#_}" != "$target" ]; do target="${target#_}"; done
    while [ "${target%_}" != "$target" ]; do target="${target%_}"; done
    case "$target" in ''|[0-9][0-9][0-9]*) continue ;; esac
    [[ "$target" =~ $xrepo_re ]] || planned_unknown+="$target at $where"$'\n'
done <<< "$items"
trace planned-closed "Planned for a closed change" ".prdkit/history/log.md closed it: remove the marker if it was built, or re-plan it to an open change" "$planned_closed"
trace planned-unknown "Planned for no open change" "point it at an open change record (new-change.sh opens one) or remove the marker" "$planned_unknown"

# Spec text by item, as spec_items reads it. The program defines flush(), which sees the item's
# line[1..lines], at[1..lines] and text, and newfile(); its own rules run after these.
# shellcheck disable=SC2016
ITEM_AWK='
    function add() { line[++lines] = $0; at[lines] = FILENAME ":" FNR; text = text " " $0 }
    FNR == 1 { flush(); lines = 0; text = ""; fence = 0; comment = 0; newfile() }
    { if (spec_skip($0)) { flush(); lines = 0; text = ""; next } }
    /^[[:space:]]*$/ { flush(); lines = 0; text = ""; next }
    /^[[:space:]]*([-*+]|[0-9]+\.)[[:space:]]/ || /^(#|\|)/ { flush(); lines = 0; text = "" }
    { add() }
    END { flush() }'

# A retired ID cited in the spec, once per file. Not judged: architecture/retired and decision
# records (history), a file whose heading retires it, and an item that says retired, superseded
# or replaced (a retiring line, or history).
cited_in=(); retired_in=(); for file in ${spec_list[@]+"${spec_list[@]}"}; do
    case "$file" in specs/architecture/retired.md|specs/architecture/retired/*) continue ;; esac
    cited_in+=("$file")
    case "$file" in specs/decisions/*|specs/architecture/decisions.md|specs/architecture/decisions/*) ;; *) retired_in+=("$file") ;; esac
done
retired_cited=""
if [ "${#retired_in[@]}" -gt 0 ]; then
    retired_cited="$({ printf '%s\n' "$items" | awk -F "$SEP" 'NF && $5 == "yes" { print "R" FS $1 }' || exit 1
        (cd "$root" && awk "$SPEC_SKIP_AWK $SPEC_ID_AWK $ITEM_AWK"'
            function flush(   i) {
                if (gone || tolower(text) ~ /retired|supersed|replac/) return
                for (i = 1; i <= lines; i++) spec_ids(line[i], "C\037" at[i])
            }
            function newfile() { gone = 0; head = 1 }
            head && /^#/ { head = 0; gone = (tolower($0) ~ /retired:/) }' "${retired_in[@]}"); } |
        awk -F "$SEP" '$1 == "R" { r[$2] = 1; next }
            $2 == "C" && ($1 in r) { f = $3; sub(/:[0-9]+$/, "", f); if (!seen[$1, f]++) print $1 " at " $3 }')"
fi
trace retired-cited "Retired ID cited in the spec" "cite what replaced it, or drop the reference" "$retired_cited"

# Allocation: a live R or Q needed now (no later from: stage) that no part or architecture file
# cites; a part whose parts-table row and Why it exists cite no J, R or Q. A range (R-03 to R-07)
# cites every ID in it. An item's own text never allocates it (SPEC.md holds both).
alloc=(); for file in ${cited_in[@]+"${cited_in[@]}"}; do
    case "$file" in specs/SPEC.md|specs/architecture*|specs/parts/*|specs/decisions/*) alloc+=("$file") ;; esac
done
allocation="$({ printf '%s\n' "$items" | awk 'NF { print "T\037" $0 }' || exit 1
    printf '%s\n' "$stages" | awk 'NF { print "S\037" $0 }' || exit 1
    printf '%s\n' "$parts" | awk 'NF { print "H\037" $0 }' || exit 1
    [ "${#alloc[@]}" -eq 0 ] || (cd "$root" && awk "$SPEC_SKIP_AWK $ITEM_AWK"'
        function cites(s, where,   w, pre, tok, post, end, lo, hi, k, pad) {
            w = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"
            while (match(s, /[JRQ]-[0-9][0-9]+/)) {
                pre = RSTART > 1 ? substr(s, RSTART - 1, 1) : ""
                tok = substr(s, RSTART, RLENGTH); post = substr(s, RSTART + RLENGTH, 1)
                s = substr(s, RSTART + RLENGTH)
                if ((pre != "" && index(w ".", pre)) || (post != "" && index(w, post))) continue
                print "C\037" tok "\037" where
                if (!match(s, /^[[:space:]]*(to|through|-|–|—|\.\.)[[:space:]]*[JRQ]-[0-9][0-9]+/)) continue
                end = substr(s, RSTART, RLENGTH); match(end, /[JRQ]-[0-9][0-9]+$/); end = substr(end, RSTART)
                if (substr(end, 1, 1) != substr(tok, 1, 1)) continue
                lo = substr(tok, 3) + 0; hi = substr(end, 3) + 0; pad = length(tok) - 2
                if (lo > hi) { k = lo; lo = hi; hi = k }
                for (k = lo + 1; k < hi && k <= lo + 99; k++) print "C\037" sprintf("%s-%0" pad "d", substr(tok, 1, 1), k) "\037" where
            }
        }
        function flush(   f) { if (lines) { f = at[1]; sub(/:[0-9]+$/, "", f); cites(text, at[1] "\037" (why ? f : "")) } }
        function newfile() { why = 0; top = (FILENAME ~ /^specs\/parts\/[^\/]+\.md$/) }
        /^#/ { why = top && tolower($0) ~ /^##[[:space:]]+why/ }' "${alloc[@]}"); } |
    awk -F "$SEP" -v now="$now" -v parents="$part_parents" -v stage_order="$SPEC_STAGE_ORDER" -v stage_custom="$SPEC_STAGE_CUSTOM" "$SPEC_STAGE_AWK"'
        $1 == "T" { at[$2] = $8; if ($6 == "yes") gone[$2] = 1
                    else if ($2 ~ /^[RQ]-/) need[++n] = $2
                    else if ($2 ~ /^P[0-9]+(\.[0-9]+)?$/ && !($2 in listed)) { listed[$2] = 1; part[++m] = $2 }
                    next }
        $1 == "S" { from[$2] = $3; next }
        $1 == "H" { if ($2 ~ /^specs\/parts\/[^\/]+\.md$/ && $3 != "") { file[$3] = $2; if (!($3 in listed)) { listed[$3] = 1; part[++m] = $3 } } next }
        $1 == "C" { if ($3 != at[$2]) cited[$2] = 1; if ($4 != "") why[$4] = 1; row[$3] = 1 }
        END {
            for (i = 1; i <= n; i++) {
                id = need[i]
                if (now != "" && (id in from) && stage_rank(from[id]) > stage_rank(now)) continue
                if (!(id in cited)) print "U\037" id " at " at[id]
            }
            for (i = 1; i <= m; i++) {
                p = part[i]
                if ((p in gone) || index(parents, " " p " ") || ((p in at) && (at[p] in row)) || ((p in file) && (file[p] in why))) continue
                print "X\037" p " at " ((p in at) ? at[p] : file[p])
            }
        }')"
trace unallocated "R or Q no part or architecture file cites" "allocate it to a part, or ask the owner whether it is still needed" \
    "$(pick "$allocation" U)"
trace part-need "Part citing no journey, R or Q" "say in Why it exists which J, R and Q need it, or ask the owner (spec.md, Parts)" \
    "$(pick "$allocation" X)"

# Change records are not spec files: one restating a spec item, or citing (once per record) an ID
# that neither the spec, a record's own item, nor a record's Spec edits section (IDs a change
# adds) names.
change_files=(); for file in "$root"/specs/changes/*.md; do [ -f "$file" ] && change_files+=("${file#"$root"/}"); done
change_ids=""
if [ "${#change_files[@]}" -gt 0 ]; then
    for file in "${change_files[@]}"; do
        rows="$(verify_lines spec_change_metadata "$root/$file")"
        while IFS="$SEP" read -r kind name a b problem; do
            case "$kind" in
                duplicate) findings+=("$file:$a: Duplicate $name field") ;;
                tags) [ -z "$b" ] || advisories+=("$file:$a: $b") ;;
                task)
                    case "$problem" in
                        malformed) findings+=("$file:$b: Malformed task part $name; use (P<n>) or (P<n>.<n>)"); continue ;;
                    esac
                    [ -n "$name" ] || continue
                    case "$known_parts" in *" $name "*) ;; *) findings+=("$file:$b: Unknown task part $name"); continue ;; esac
                    [ "$problem" != outside ] || findings+=("$file:$b: Task part $name is not in the change header Parts")
                    ;;
            esac
        done <<< "$rows"
    done
    change_ids="$({ printf '%s\n' "$items" | awk -F "$SEP" 'NF { print "D" FS $1 FS $7 }' || exit 1
        spec_items "$root" "${change_files[@]}" | awk -F "$SEP" 'NF { print "N" FS $1 FS $7 }' || exit 1
        (cd "$root" && awk "$SPEC_SKIP_AWK $SPEC_ID_AWK"'
            FNR == 1 { fence = 0; comment = 0; edits = 0 }
            { if (spec_skip($0)) next }
            /^## / { edits = ($0 ~ /^##[[:space:]]+Spec edits/) }
            { spec_ids($0, FILENAME ":" FNR "\037" edits) }' "${change_files[@]}"); } |
        awk -F "$SEP" '$1 == "D" { spec[$2] = $3; next }
            $1 == "N" { own[$2] = 1; if ($2 in spec) print "R" FS $2 " at " $3 " (spec: " spec[$2] ")"; next }
            { if ($3 == 1) own[$1] = 1; id[++n] = $1; at[n] = $2 }
            END { for (i = 1; i <= n; i++) {
                f = at[i]; sub(/:[0-9]+$/, "", f)
                if (!(id[i] in spec) && !(id[i] in own) && !seen[id[i], f]++) print "U" FS id[i] " at " at[i] } }')"
fi
trace change-restates "Change record restates a spec item" "cite its ID and edit the spec instead" \
    "$(pick "$change_ids" R)"
trace change-undefined "Change record cites an ID defined nowhere" "list it under Spec edits, define it in the spec, or fix the reference" \
    "$(pick "$change_ids" U)"

# --- Records and inbox -----------------------------------------------------------------------
rows="$(verify_lines spec_records "$root")"
while IFS="$SEP" read -r line type name who scope _ _ _ until check reason; do
    [ -n "$line" ] || continue
    case "$type" in
        INVALID) findings+=("Malformed record at specs/records.md:$line: $reason") ;;
        HOLD|WAIVER)
            case "$reason" in
                ref=*) authority_records+=("$type $name on $scope${check:+ (check $check)} by $who, until $until — $reason") ;;
            esac
            if spec_is_date "$until" && [ "$(spec_days_between "$until" "$today")" -gt 0 ]; then
                if [ "$type" = HOLD ]; then
                    advisories+=("HOLD $name due (owner $who, until $until): it still applies; ask the owner to confirm or lift it")
                else
                    advisories+=("Expired WAIVER $name (owner $who, until $until): it no longer applies; the owner renews or removes it")
                fi
            fi ;;
    esac
done <<< "$rows"
open_count=0; oldest=""; undisposed=0; unparsed=""; resolved=0; processing=0
if ! rows="$(verify_lines spec_inbox "$root" 2>&1)"; then
    inbox_error="${rows%%$'\n'*}"
    case "$inbox_error" in
        'Unsupported inbox entry at '*) ;;
        *) inbox_error="Invalid specs/inbox.md" ;;
    esac
    findings+=("$inbox_error: expected headed items with a date, actor and disposition, and structured **Answer** fields (inbox-template.md)")
    rows=""
fi
while IFS="$SEP" read -r line date who state _ _ _ _ _ _ problems; do
    [ -n "$line" ] || continue
    case "$state" in
        open) open_count=$((open_count + 1)); { [ -n "$oldest" ] && [[ ! "$date" < "$oldest" ]]; } || oldest="$date" ;;
        processing) processing=$((processing + 1)) ;;
        unknown) undisposed=$((undisposed + 1)) ;;
        folded|declined|deferred) resolved=$((resolved + 1)) ;;
        unparsed) unparsed="${unparsed:+$unparsed, }$line" ;;
    esac
    if [ -n "$problems" ]; then
        case "$problems" in
            *duplicate*|*'answer revision mismatch'*) findings+=("Owner inbox specs/inbox.md:$line: $problems") ;;
            *) advisories+=("Owner inbox specs/inbox.md:$line: $problems; reconcile with /prdkit-inbox") ;;
        esac
    fi
done <<< "$rows"
[ "$open_count" -eq 0 ] || advisories+=("Owner inbox: $open_count open item(s), oldest from $oldest ($(spec_days_between "$oldest" "$today") days)")
[ "$processing" -eq 0 ] || advisories+=("Owner inbox: $processing item(s) awaiting agent processing, not another owner answer; incorporate and verify spec/policy, provenance and obligations with /prdkit-inbox")
[ "$resolved" -eq 0 ] || advisories+=("Owner inbox: $resolved resolved-state item(s) retained; verify canonical decisions, provenance and remaining obligations, then remove them (spec.md, Resolve owner answers)")
[ "$undisposed" -eq 0 ] || advisories+=("Owner inbox: $undisposed item(s) without an open, processing, folded, declined or deferred disposition")
[ -z "$unparsed" ] || advisories+=("Owner inbox: specs/inbox.md line(s) $unparsed are not inbox items (a '### <title>' heading, then '*<YYYY-MM-DD> · <owner|agent> · <disposition>*'), so status skips them")

# --- Local machine paths: tracked docs, specs and agent context use repo-relative paths -------
# A machine's own path (/home/<user>, /Users/<user>, C:\Users\<user>) is flagged; a generic ~/
# default is not, nor is a `>` quote: owner words stay verbatim.
path_files=()
if $in_git; then
    rows="$(verify_lines git -C "$root" ls-files -- '*.md' ':(exclude)specs/authority.md' \
        ':(exclude).prdkit/templates/**' ':(exclude).prdkit/workflows/**' ':(exclude).product-kit/**')"
else
    rows="$listed"
    for file in specs/inbox.md specs/records.md specs/observed.md .prdkit/history/log.md \
                .prdkit/memory/constitution.md AGENTS.md CLAUDE.md; do
        [ ! -f "$root/$file" ] || rows="$rows"$'\n'"$file"
    done
fi
while IFS= read -r file; do [ -z "$file" ] || path_files+=("$file"); done <<< "$rows"
if [ "${#path_files[@]}" -gt 0 ]; then
    present=()
    for file in "${path_files[@]}"; do
        # Report the compaction finding even when the unreadable folded log is tracked.
        if [ "$file" = .prdkit/history/log.md ] && ! $compaction_verified; then continue; fi
        [ -f "$root/$file" ] && present+=("$file")
    done
    [ "${#present[@]}" -gt 0 ] || present=(/dev/null)
    local_paths="$(cd "$root" && awk '
        /^[[:space:]]*>/ { next }
        /(^|[^A-Za-z0-9_.-])(\/home\/[A-Za-z0-9_]|\/Users\/[A-Za-z0-9_]|[A-Za-z]:\\Users\\)/ {
            if (++n <= 8) at = at (n > 1 ? ", " : "") FILENAME ":" FNR }
        END { if (n > 8) at = at " and " n - 8 " more"; print at }' "${present[@]}" 2>/dev/null)"
    [ -z "$local_paths" ] || advisories+=("Local machine path at $local_paths: write paths relative to the repository, and org/repo plus a path for another repository")
fi

# --- Closed changes reach the log: a deleted change record needs its log entry ---------------------
# An entry file holds exactly its own change's one dated line, so folding it at compaction is safe.
check_close_lengths() { # repo-relative Markdown file
    local rows row
    rows="$(LC_ALL=C awk -v f="$1" -v max="$SPEC_CLOSE_ROW_MAX" "$SPEC_METADATA_SKIP_AWK$SPEC_CLOSE_LENGTH_AWK"'
        { if (metadata_skip($0)) next }
        /^- [0-9-]+ [0-9][0-9][0-9][0-9]*-[a-z0-9-]+:/ {
            n = close_length($0)
            if (n > max) printf "%s:%d: close row is %d characters, over %d; keep details in the PR\n", f, FNR, n, max
        }
    ' "$root/$1")" || { findings+=("Could not check close row lengths in $1"); return; }
    while IFS= read -r row; do [ -z "$row" ] || findings+=("$row"); done <<< "$rows"
}
if $compaction_verified && [ -f "$root/.prdkit/history/log.md" ]; then
    check_close_lengths .prdkit/history/log.md
fi
for entry_file in "$root"/.prdkit/history/log/* "$root"/.prdkit/history/log/.[!.]* "$root"/.prdkit/history/log/..?*; do
    [ -e "$entry_file" ] || [ -L "$entry_file" ] || continue
    entry_id="${entry_file##*/}"; entry_id="${entry_id%.md}"
    if [ "${entry_file##*/}" = "$entry_id.md" ] && [ -f "$entry_file" ] && [ ! -L "$entry_file" ] &&
        spec_log_entry_valid "$entry_file" "$entry_id"; then
        check_close_lengths "${entry_file#"$root"/}"
        continue
    fi
    findings+=("Malformed log entry .prdkit/history/log/${entry_file##*/}: a plain file <id>.md holding one line, '- <date> <id>: <what changed>', for the change its name gives (close-change.sh writes it)")
done
if $compaction_verified; then
    live_close_ids="$(sed -n 's/^- [0-9-]* \([0-9][0-9][0-9]*-[a-z0-9-]*\):.*/\1/p' <<< "$entries" | sort)"
    previous=""
    while IFS= read -r id; do
        [ -n "$id" ] || continue
        if [ "$id" = "$previous" ]; then
            findings+=("Duplicate live log entry for $id; keep only one in .prdkit/history/log.md or .prdkit/history/log/")
        elif $archive_verified && spec_archive_log_covers "$archive_index" "$id"; then
            findings+=("Live log entry for $id is also condensed in .prdkit/history/archive.md; remove the duplicate from the live log")
        fi
        previous="$id"
    done <<< "$live_close_ids"
fi
if $in_git; then
    if $compaction_verified && $archive_verified && $has_head; then
        unlogged=""
        history="$(verify_lines git -C "$root" log -M --diff-filter=D --name-only --format= -- 'specs/changes/*.md')"
        rows="$(printf '%s\n' "$history" | sed -n 's|^specs/changes/\(.*\)\.md$|\1|p' | sort -u)"
        while IFS= read -r id; do
            [ -n "$id" ] && [ ! -f "$root/specs/changes/$id.md" ] || continue
            # Entries condensed into .prdkit/history/archive.md (compact.md) left the log on purpose.
            if spec_archive_log_covers "$archive_index" "$id"; then continue; fi
            grep -q "^- [0-9-]* $id:" <<< "$entries" || unlogged="${unlogged:+$unlogged, }$id"
        done <<< "$rows"
        [ -z "$unlogged" ] || findings+=("Closed without a log entry: $unlogged; the change that deletes its record adds .prdkit/history/log/<id>.md, '- <date> <id>: <what changed>' (close-change.sh)")
    fi
    # A reviewed record still on the default branch and in this checkout: its change merged
    # without closing. A branch that closes it (record deleted, log line added) is not flagged.
    base="$(spec_default_branch "$root")"
    if $compaction_verified && [ -n "$base" ] && spec_objects_local "$root"; then
        named=" $(printf '%s\n' "$open_changes" | awk -F "$SEP" '{ v = $7; gsub(/`/, "", v); if (v != "") printf "%s ", v }')"
        rows="$(verify_lines spec_unclosed_changes "$root" "$base")"
        unclosed=""; n=0
        while IFS= read -r cid; do
            [ -f "$root/specs/changes/$cid.md" ] || continue
            case "$named" in *" $cid "*) continue ;; esac
            n=$((n + 1)); [ "$n" -gt 8 ] || unclosed="${unclosed:+$unclosed, }$cid"
        done <<< "$rows"
        [ "$n" -le 8 ] || unclosed="$unclosed and $((n - 8)) more"
        [ "$n" -eq 0 ] || advisories+=("Merged without closing: $unclosed records are still on $base, with no child or outcome checkpoint keeping them open; close each in the next relevant PR, or give one that spans PRs an outcome checkpoint (.prdkit/workflows/review.md, Closing)")
    fi
fi

# --- Raw evidence under specs/ (it belongs in CI, the PR or .prdkit/cache/evidence/) -------------
if $in_git; then
    raw_count=0; raw_bytes=0; raw_first=""
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    git -C "$root" ls-files -z -- specs > "$work/spec-paths"
    expected="$(verify_nul_count "$work/spec-paths")"
    checked=0
    while IFS= read -r -d '' path; do
        checked=$((checked + 1))
        [ -n "$path" ] || { findings+=("Empty record in tracked spec inventory"); continue; }
        case "$path" in
            *.md) continue ;;
            */reviews/*|*/results/*|*/evidence/*|*.patch|*.trx|*.log) ;;
            *) continue ;;
        esac
        raw_count=$((raw_count + 1)); [ -n "$raw_first" ] || raw_first="$path"
        if [ -f "$root/$path" ] && [ ! -L "$root/$path" ]; then
            size="$(wc -c < "$root/$path" | tr -d '[:space:]')"
            raw_bytes=$((raw_bytes + size))
        fi
    done < "$work/spec-paths"
    [ "$checked" -eq "$expected" ] || findings+=("Incomplete tracked spec inventory: expected $expected, checked $checked")
    [ "$raw_count" -eq 0 ] || advisories+=("Raw evidence tracked under specs/: $raw_count file(s), $((raw_bytes / 1024)) KB, e.g. $raw_first. Keep packages and outputs in .prdkit/cache/evidence/ or CI and link them; untrack with git rm --cached")
fi

# --- Agent context and agent definitions ---------------------------------------------------------
rows="$(verify_lines context_budget_advisories "$root")"
while IFS= read -r advisory; do [ -z "$advisory" ] || advisories+=("$advisory"); done <<< "$rows"
roster="$root/.prdkit/memory/agents.md"
tiers_used=false
[ ! -d "$root/.prdkit/agent-library" ] || tiers_used=true
for def in "$root"/.prdkit/agents/*.md; do
    [ -f "$def" ] || continue
    tiers_used=true
    name="$(basename "$def" .md)"
    if [ ! -f "$roster" ] || ! grep -qF -- "$name" "$roster"; then
        findings+=("Agent '$name': neutral definition has no entry in .prdkit/memory/agents.md")
    fi
    for harness in $(at_targets "$root" "$def"); do
        [ -f "$root/.$harness/agents/$name.md" ] || findings+=("Agent '$name': no projection in .$harness/agents/ (render-agents.sh, or /prdkit-configure)")
    done
done
if $tiers_used && [ ! -f "$root/.prdkit/scripts/bash/render-agents.sh" ]; then
    findings+=("Agent tiers are in use (.prdkit/agents/ or .prdkit/agent-library/) but render-agents.sh is not installed: run init.sh --upgrade --with agent-tiers")
fi
if rows="$(verify_lines agent_tier_findings "$root")"; then
    while IFS= read -r finding; do [ -z "$finding" ] || findings+=("$finding"); done <<< "$rows"
else
    findings+=("Agent tier inventory did not complete")
fi
# A worktree directory removed without `git worktree remove` leaves a prunable registration
# that a resuming session cannot re-enter. Detect only; pruning is cleanup-change.sh's job.
if $in_git; then
    wt_path=""
    rows="$(spec_worktrees "$root")"
    while IFS= read -r line; do
        case "$line" in
            "worktree "*) wt_path="${line#worktree }" ;;
            "prunable"*) [ -z "$wt_path" ] || advisories+=("Stale worktree registration: $wt_path (directory gone; clear it with .prdkit/scripts/bash/cleanup-change.sh --prune)") ;;
        esac
    done <<< "$rows"
fi

if $strict; then findings+=(${advisories[@]+"${advisories[@]}"}); advisories=(); fi
report
