#!/usr/bin/env bash
# doctor.sh — Read-only diagnostics: installation, spec layout and budgets, IDs, records,
# owner inbox, raw evidence, agent context and agent projections. Never repairs anything.
#
# Usage: doctor.sh [--json] [--strict]
# Errors (failing findings): installation drift, malformed IDs, malformed records, agent
# tier-map problems, a change record deleted without its specs/log.md line. Everything else is an advisory; --strict makes advisories fail too.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/install-lib.sh"
source "$SCRIPT_DIR/context-budget-lib.sh"
source "$SCRIPT_DIR/agent-tiers-lib.sh"
source "$SCRIPT_DIR/spec-lib.sh"
json=false; strict=false
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) json=true ;;
        --strict) strict=true ;;
        *) echo 'Usage: doctor.sh [--json] [--strict]' >&2; exit 2 ;;
    esac
    shift
done
root="$(get_repo_root)"; findings=(); advisories=()
today="$(spec_today)"; SEP="$SPEC_SEP"
in_git=false
git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1 && in_git=true

# --- Installation ------------------------------------------------------------------------------
while IFS= read -r finding; do
    [ -z "$finding" ] || findings+=("$finding")
done < <(install_findings "$root")
for file in .prdkit/workflows/protocol.md .prdkit/workflows/change.md .prdkit/memory/constitution.md .prdkit/templates/change-template.md; do
    [ -f "$root/$file" ] || findings+=("Missing installed file: $file")
done

# --- Layout --------------------------------------------------------------------------------------
case "$(spec_layout "$root")" in
    0.x) advisories+=("0.x layout — run the cutover (.prdkit/workflows/compact.md) before 1.0 workflows apply") ;;
    both) advisories+=("Two canons: 1.0 spec files beside 0.x folders (specs/product-spec, specs/targets or specs/features); finish the cutover: condense them into the spec and specs/archive.md, turn a feature the owner keeps open into a change record, and git rm them (.prdkit/workflows/compact.md)") ;;
esac
# No raw archive on main: it surfaces in searches beside the current spec. Git keeps the files.
if $in_git; then
    archived="$({ git -C "$root" ls-files -- specs/archive/ 2>/dev/null || true; } | wc -l | tr -d ' ')"
    [ "${archived:-0}" -eq 0 ] || advisories+=("Archive on main: specs/archive/ tracks $archived file(s); condense them into specs/archive.md and remove them from main with git rm; git keeps them (.prdkit/workflows/compact.md)")
fi
# specs/archive.md records the base commit and the last 0.x feature; new-change.sh reserves the
# old feature numbers from both. A base a shallow clone lacks is not judged.
if [ -f "$root/specs/archive.md" ]; then
    last="$(spec_archive_last "$root")"; archive_problems=""
    [ -n "$last" ] || archive_problems="no readable **Last 0.x feature** line (a number, or none)"
    if [ "$last" != 0 ]; then
        tokens="$(spec_archive_base_tokens "$root")"
        if [ -z "$tokens" ]; then
            archive_problems="${archive_problems:+$archive_problems and }no **Base** commit"
        elif $in_git && [ "$(git -C "$root" rev-parse --is-shallow-repository 2>/dev/null)" != true ]; then
            resolved=false
            while IFS= read -r token; do
                if git -C "$root" rev-parse --verify --quiet "$token^{commit}" >/dev/null 2>&1; then resolved=true; break; fi
            done <<< "$tokens"
            $resolved || archive_problems="${archive_problems:+$archive_problems and }a **Base** commit that is not in this repository ($(printf '%s\n' "$tokens" | sed -n 1p))"
        fi
    fi
    [ -z "$archive_problems" ] || advisories+=("Archive: specs/archive.md has $archive_problems; new-change.sh reserves the old 0.x feature numbers from them (.prdkit/templates/archive-template.md)")
fi
mixed="$(spec_mixed_forms "$root")"
[ -z "$mixed" ] || advisories+=("Two spec forms: specs/SPEC.md beside $mixed; keep one form — split SPEC.md fully in one change and delete it, or fold the split files back (.prdkit/workflows/spec.md)")

# --- Reading budgets (never targets; never drop an item to fit) -------------------------------
budget() { # limit path...
    local limit="$1" path lines; shift
    for path in "$@"; do
        [ -f "$path" ] || continue
        lines="$(wc -l < "$path" | tr -d ' ')"
        [ "$lines" -le "$limit" ] || advisories+=("Budget: ${path#"$root"/} has $lines lines, over its $limit-line reading budget; tighten the wording, move detail to code or tests, or split it (spec.md, Layout); never drop an item to fit")
    done
}
# In the 1.1 layout (beside specs/product/) product.md and architecture.md are one-page summaries;
# the 1.0 single-file forms keep 150 and 300.
if [ -d "$root/specs/product" ]; then
    budget 80 "$root"/specs/product.md; budget 100 "$root"/specs/architecture.md
else
    budget 150 "$root"/specs/product.md; budget 300 "$root"/specs/architecture.md
fi
# Each budget has a split (spec.md, Layout), and each piece keeps the budget of its parent.
budget 150 "$root"/specs/product/users.md "$root"/specs/product/users/*.md \
    "$root"/specs/product/non-goals.md "$root"/specs/product/non-goals/*.md \
    "$root"/specs/product/lifecycle.md "$root"/specs/product/lifecycle/*.md "$root"/specs/product/journeys/*.md \
    "$root"/specs/observed.md "$root"/specs/observed/*.md "$root"/specs/changes/*.md "$root"/specs/archive.md
budget 300 "$root"/specs/product/requirements.md "$root"/specs/product/qualities.md "$root"/specs/product/constraints.md \
    "$root"/specs/product/requirements/*.md "$root"/specs/product/qualities/*.md "$root"/specs/product/constraints/*.md \
    "$root"/specs/architecture/*.md "$root"/specs/architecture/*/*.md "$root"/specs/decisions/*.md \
    "$root"/specs/SPEC.md
budget 200 "$root"/specs/parts/*.md "$root"/specs/parts/*/*.md

# --- IDs ---------------------------------------------------------------------------------------
items="$(spec_items "$root")"
while IFS= read -r problem; do [ -z "$problem" ] || findings+=("$problem"); done < <(
    printf '%s\n' "$items" | awk -F "$SEP" 'NF { if ($1 in at) print "Duplicate ID " $1 ": defined at " at[$1] " and " $7; else at[$1] = $7 }')
while IFS="$SEP" read -r file part; do
    [ -n "$file" ] || continue
    if [ -z "$part" ]; then advisories+=("$file: no '# P<n> name' heading, so its behaviors and invariants cannot be checked against their part"); continue; fi
    while IFS= read -r problem; do [ -z "$problem" ] || findings+=("$problem"); done < <(
        printf '%s\n' "$items" | awk -F "$SEP" -v file="$file" -v part="$part" '
            index($7, file ":") == 1 && $1 ~ /^P[0-9]+\./ && substr($1, 1, index($1, ".") - 1) != part {
                print $1 " is defined in " $7 ", the part file of " part }')
done < <(spec_part_headings "$root")
while IFS= read -r where; do [ -z "$where" ] || findings+=("Malformed ID at $where: IDs are J-, R-, Q-, C-, N-, D- or P<n>.B-/P<n>.I- with two or more digits"); done < <(
    while IFS= read -r file; do
        awk -v file="$file" "$SPEC_SKIP_AWK"'{ if (spec_skip($0)) next }
            /^[[:space:]]*[-*][[:space:]]+\*\*J-[0-9][[:space:]*]/ { next }
            /^[[:space:]]*[-*][[:space:]]+\*\*(P[0-9]+\.)?[JRQCDNBI]-/ && !/^[[:space:]]*[-*][[:space:]]+\*\*(P[0-9]+\.[BI]|[JRQCDN])-[0-9][0-9]+[[:space:]*]/ { print file ":" NR }
            /^#[[:space:]]+J-/ && !/^#[[:space:]]+J-[0-9][0-9]+[[:space:]]/ { print file ":" NR }
            /^##?#?[[:space:]]+D-/ && !/^##?#?[[:space:]]+D-[0-9][0-9]+[[:space:]]/ { print file ":" NR }' "$root/$file"
    done < <(spec_files "$root"))
# The 1.0 product template wrote journeys as one-digit `- **J-1 ...**` bullets; the Restructure
# renumbers them.
oldj=""
while IFS= read -r where; do [ -z "$where" ] || oldj="${oldj:+$oldj, }$where"; done < <(
    while IFS= read -r file; do
        awk -v file="$file" "$SPEC_SKIP_AWK"'{ if (spec_skip($0)) next }
            /^[[:space:]]*[-*][[:space:]]+\*\*J-[0-9][[:space:]*]/ { print file ":" NR }' "$root/$file"
    done < <(spec_files "$root"))
[ -z "$oldj" ] || advisories+=("One-digit journey IDs at $oldj (the 1.0 form): the Restructure in .prdkit/workflows/compact.md renumbers them to J-01")

cite_files=()
while IFS= read -r file; do cite_files+=("$file"); done < <(
    spec_files "$root"
    spec_test_files "$root")
if [ "${#cite_files[@]}" -gt 0 ]; then
    while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done < <(
        { printf '%s\n' "$items" | awk -F "$SEP" 'NF { print "D" FS $1 FS $5 }'
          spec_citations "$root" "${cite_files[@]}" | awk -F "$SEP" '{ print "C" FS $1 FS $2 }'; } |
        awk -F "$SEP" '
            $1 == "D" { defined[$2] = 1; if ($3 == "yes") retired[$2] = 1; next }
            { if (!($2 in defined)) { if (!(($2) in undef)) { undef[$2] = $3; order[++n] = $2 } }
              else if (($2 in retired) && $3 !~ /^specs\//) { if (!(($2) in ret)) { ret[$2] = $3; rorder[++m] = $2 } } }
            END {
                for (i = 1; i <= n; i++) print "ID " order[i] " is cited at " undef[order[i]] " but defined nowhere in the spec"
                for (i = 1; i <= m; i++) print "Retired ID " rorder[i] " is still cited by a test at " ret[rorder[i]] "; edit the test or the line"
            }')
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
          awk -v f="$file" '/^#/ { r = (tolower($0) ~ /retired:/); exit } END { print "F\037" f "\037" (r ? 1 : 0) }' "$root/$file"
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
links="$(spec_links "$root")"
spec_list=(); while IFS= read -r file; do spec_list+=("$file"); done < <(spec_files "$root")
observed=(); for file in "$root"/specs/observed.md "$root"/specs/observed/*.md; do [ -f "$file" ] && observed+=("${file#"$root"/}"); done
observed_links=""; [ "${#observed[@]}" -eq 0 ] || observed_links="$(spec_links "$root" "${observed[@]}")"
archive_links=""; [ ! -f "$root/specs/archive.md" ] || archive_links="$(spec_links "$root" specs/archive.md)"
while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done < <(
    missing_links "$links"; missing_links "$observed_links"; missing_links "$archive_links"
    if [ -d "$root/specs/product" ] && [ "${#spec_list[@]}" -gt 0 ]; then
        reach "specs/product.md specs/architecture.md specs/SPEC.md" \
            "is reached from no summary (product.md, architecture.md or a file they link); give it a line and a link in its parent (spec.md, Layout)" \
            0 "$links" "${spec_list[@]}"
    fi
    if [ -d "$root/specs/observed" ] && [ "${#observed[@]}" -gt 0 ]; then
        reach "specs/observed.md" "is not linked from specs/observed.md; give it a line and a link there" \
            1 "$observed_links" "${observed[@]}"
    fi)

# --- Lifecycle: the current stage, from: markers, the product page's stage columns ----------
stages="$(spec_stages "$root")"
now_next="$(spec_stage_now "$root")"; now="${now_next%%"$SEP"*}"
stage_list='idea, demo, mvp, private preview, public preview, ga, sunset, retired'
if [ -f "$root/specs/product/lifecycle.md" ]; then
    known="$(awk "$SPEC_STAGE_AWK"' BEGIN { print stage_rank(ARGV[1]); exit }' "$now")"
    [ "$known" != 0 ] || findings+=("Lifecycle: specs/product/lifecycle.md needs '**Now**: <stage>', one of: $stage_list")
fi
# An advisory: `from:` may also start a source note, such as `from: owner interview`.
while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done < <(
    printf '%s\n' "$stages" | awk -F "$SEP" -v list="$stage_list" "$SPEC_STAGE_AWK"'
        NF && !stage_rank($2) { print "Lifecycle: \"from: " $2 "\" in " $1 " at " $3 " names no stage; stages: " list }')
if [ -n "$stages" ] && [ -z "$now" ]; then
    advisories+=("Lifecycle: items carry from: markers, but specs/product/lifecycle.md names no current stage, so every item counts as needed now")
fi
if [ -n "$now" ]; then
    if [ -n "$stages" ]; then
        reached="$(printf '%s\n' "$stages" | awk -F "$SEP" -v now="$now" "$SPEC_STAGE_AWK"'
            NF && stage_rank($2) && stage_rank($2) <= stage_rank(now) { out = out (out ? ", " : "") $1 }
            END { print out }')"
        [ -z "$reached" ] || advisories+=("Lifecycle: the stage is $now, so remove the reached from: markers of $reached")
    fi
    # product.md may show journeys in stage columns; each must match the journey's own marker.
    if [ -f "$root/specs/product.md" ]; then
        while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done < <(
            { printf '%s\n' "$stages" | awk -F "$SEP" 'NF && $1 ~ /^J-/ { print "S" FS $1 FS $2 }'
              awk "$SPEC_SKIP_AWK"'{ if (spec_skip($0)) next } { print "L" "\037" FNR "\037" $0 }' "$root/specs/product.md"; } |
            awk -F "$SEP" -v now="$now" "$SPEC_STAGE_AWK"'
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
                }')
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
    while IFS= read -r problem; do [ -z "$problem" ] || advisories+=("$problem"); done < <(
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
        ' "${env_files[@]}" "${stage_files[@]}")
fi

# --- Records and inbox -----------------------------------------------------------------------
while IFS="$SEP" read -r line type name who _ _ _ date until _ reason; do
    [ -n "$line" ] || continue
    case "$type" in
        INVALID) findings+=("Malformed record at specs/records.md:$line: $reason") ;;
        HOLD|WAIVER)
            if spec_is_date "$until" && [ "$(spec_days_between "$until" "$today")" -gt 0 ]; then
                advisories+=("Expired $type $name (owner $who, until $until): it no longer applies; the owner renews or removes it")
            fi ;;
    esac
done < <(spec_records "$root")
open_count=0; oldest=""; undisposed=0; unparsed=""; oneline=0; resolved=0
while IFS="$SEP" read -r line date who state _; do
    [ -n "$line" ] || continue
    [ "$state" = unparsed ] || [ "$(sed -n "${line}p" "$root/specs/inbox.md" | cut -c1-4)" = "### " ] || oneline=$((oneline + 1))
    case "$state" in
        open) open_count=$((open_count + 1)); { [ -n "$oldest" ] && [[ ! "$date" < "$oldest" ]]; } || oldest="$date" ;;
        unknown) undisposed=$((undisposed + 1)) ;;
        folded|declined|deferred) resolved=$((resolved + 1)) ;;
        unparsed) unparsed="${unparsed:+$unparsed, }$line" ;;
    esac
done < <(spec_inbox "$root")
[ "$open_count" -eq 0 ] || advisories+=("Owner inbox: $open_count open item(s), oldest from $oldest ($(spec_days_between "$oldest" "$today") days)")
[ "$resolved" -eq 0 ] || advisories+=("Owner inbox: $resolved resolved-state item(s) retained; verify canonical decisions, provenance and remaining obligations, then remove them (spec.md, Resolve owner answers)")
[ "$undisposed" -eq 0 ] || advisories+=("Owner inbox: $undisposed item(s) without an open, folded, declined or deferred disposition")
[ -z "$unparsed" ] || advisories+=("Owner inbox: specs/inbox.md line(s) $unparsed are not inbox items (a '### <title>' heading, then '*<YYYY-MM-DD> · <owner|agent> · <disposition>*'), so status skips them")
[ "$oneline" -eq 0 ] || advisories+=("Owner inbox: $oneline item(s) in the one-line form; rewrite each as a heading with its meta line and options (.prdkit/templates/inbox-template.md)")

# --- Local machine paths: tracked docs, specs and agent context use repo-relative paths -------
# A machine's own path (/home/<user>, /Users/<user>, C:\Users\<user>) is flagged; a generic ~/
# default is not, nor is a `>` quote: owner words stay verbatim. 0.x folders and a leftover
# specs/archive/, each its own advisory until removed, keep their history as it was.
path_files=()
while IFS= read -r file; do path_files+=("$file"); done < <(
    if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        git -C "$root" ls-files -- '*.md' ':(exclude)specs/archive/**' ':(exclude)specs/features/**' \
            ':(exclude).prdkit/templates/**' ':(exclude).prdkit/workflows/**' ':(exclude).product-kit/**'
    else
        spec_files "$root"
        for file in specs/inbox.md specs/records.md specs/observed.md specs/log.md \
                    .prdkit/memory/constitution.md AGENTS.md CLAUDE.md; do
            [ -f "$root/$file" ] && printf '%s\n' "$file"
        done
    fi)
if [ "${#path_files[@]}" -gt 0 ]; then
    present=(); for file in "${path_files[@]}"; do [ -f "$root/$file" ] && present+=("$file"); done
    [ "${#present[@]}" -gt 0 ] || present=(/dev/null)
    local_paths="$(cd "$root" && awk '
        /^[[:space:]]*>/ { next }
        /(^|[^A-Za-z0-9_.-])(\/home\/[A-Za-z0-9_]|\/Users\/[A-Za-z0-9_]|[A-Za-z]:\\Users\\)/ {
            if (++n <= 8) at = at (n > 1 ? ", " : "") FILENAME ":" FNR }
        END { if (n > 8) at = at " and " n - 8 " more"; print at }' "${present[@]}" 2>/dev/null)"
    [ -z "$local_paths" ] || advisories+=("Local machine path at $local_paths: write paths relative to the repository, and org/repo plus a path for another repository")
fi

# --- Closed changes reach the log: a deleted change record needs its specs/log.md line ------------
if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    unlogged=""
    while IFS= read -r id; do
        [ -n "$id" ] && [ ! -f "$root/specs/changes/$id.md" ] || continue
        grep -q "^- [0-9-]* $id:" "$root/specs/log.md" 2>/dev/null || unlogged="${unlogged:+$unlogged, }$id"
    done < <(git -C "$root" log -M --diff-filter=D --name-only --format= -- 'specs/changes/*.md' 2>/dev/null |
        sed -n 's|^specs/changes/\(.*\)\.md$|\1|p' | sort -u)
    [ -z "$unlogged" ] || findings+=("Closed without a specs/log.md line: $unlogged; the change that deletes its record adds '- <date> <id>: <what changed>' to specs/log.md")
fi

# --- Raw evidence under specs/ (it belongs in CI, the PR or .prdkit/cache/evidence/) -------------
if $in_git; then
    raw_count=0; raw_bytes=0; raw_first=""
    while IFS= read -r -d '' path; do
        case "$path" in
            *.md) continue ;;
            */reviews/*|*/results/*|*/evidence/*|*.patch|*.trx|*.log) ;;
            *) continue ;;
        esac
        raw_count=$((raw_count + 1)); [ -n "$raw_first" ] || raw_first="$path"
        if [ -f "$root/$path" ] && [ ! -L "$root/$path" ]; then
            raw_bytes=$((raw_bytes + $(wc -c < "$root/$path" | tr -d ' ')))
        fi
    done < <(git -C "$root" ls-files -z -- specs 2>/dev/null || true)
    [ "$raw_count" -eq 0 ] || advisories+=("Raw evidence tracked under specs/: $raw_count file(s), $((raw_bytes / 1024)) KB, e.g. $raw_first. Keep packages and outputs in .prdkit/cache/evidence/ or CI and link them; untrack with git rm --cached")
fi

# --- Agent context and agent definitions ---------------------------------------------------------
while IFS= read -r advisory; do [ -z "$advisory" ] || advisories+=("$advisory"); done < <(context_budget_advisories "$root")
# Agent context loads on every turn: a path into 0.x folders or an archive the cutover removed
# sends agents to files that are gone; history questions go to specs/archive.md. Only a plain
# repo-relative path counts, `./` and an `@` import too: not one after ":", "/" or a word
# character (a git show pointer, a URL or absolute path, another repository's owner/repo), not
# a placeholder or glob (<, *, {), not a bare folder (a rule such as "never recreate
# specs/features/"). A function: bash 3.2 misparses case patterns inside a process substitution.
context_0x_paths() { # file
    local token path
    { grep -oE '(\./|[^[:space:]])?specs/(archive|product-spec|targets|features)/[^[:space:]]*' "$1" 2>/dev/null || true; } |
        while IFS= read -r token; do
            case "$token" in
                specs/*) path="$token" ;;
                ./specs/*) path="${token#./}" ;;
                *) path="${token#?}"; case "${token%"$path"}" in :|/|[[:alnum:]_]) continue ;; esac ;;
            esac
            while :; do
                case "$path" in
                    *'`'|*'*'|*.|*,|*';'|*:|*')'|*']'|*'"'|*"'"|*'!'|*'?'|*'>') path="${path%?}" ;;
                    *) break ;;
                esac
            done
            case "$path" in *'<'*|*'*'*|*'{'*) continue ;; esac
            path="${path%%[!A-Za-z0-9._/-]*}"
            case "$path" in specs/archive/|specs/product-spec/|specs/targets/|specs/features/) ;; *) printf '%s\n' "$path" ;; esac
        done | LC_ALL=C sort -u
}
stale_paths=""
for name in $CONTEXT_AGENT_FILES; do
    [ -f "$root/$name" ] || continue
    if $in_git && ! git -C "$root" ls-files --error-unmatch -- "$name" >/dev/null 2>&1; then continue; fi
    while IFS= read -r path; do
        [ -e "$root/$path" ] || stale_paths="${stale_paths:+$stale_paths, }$name: $path"
    done < <(context_0x_paths "$root/$name")
done
[ -z "$stale_paths" ] || advisories+=("Agent context names removed 0.x paths ($stale_paths); point it at the current spec, and history questions at specs/archive.md; qualify paths in other repositories as owner/repo:path (.prdkit/workflows/compact.md)")
roster="$root/.prdkit/memory/agents.md"
for def in "$root"/.prdkit/agents/*.md; do
    [ -f "$def" ] || continue
    name="$(basename "$def" .md)"
    if [ ! -f "$roster" ] || ! grep -qF -- "$name" "$roster"; then
        findings+=("Agent '$name': neutral definition has no entry in .prdkit/memory/agents.md")
    fi
    for harness in $(at_targets "$root" "$def"); do
        [ -f "$root/.$harness/agents/$name.md" ] || findings+=("Agent '$name': no projection in .$harness/agents/ (render-agents.sh, or /prdkit-configure)")
    done
done
while IFS= read -r finding; do [ -z "$finding" ] || findings+=("$finding"); done < <(agent_tier_findings "$root")
# A worktree directory removed without `git worktree remove` leaves a prunable registration
# that a resuming session cannot re-enter. Detect only; pruning is cleanup-change.sh's job.
if $in_git; then
    wt_path=""
    while IFS= read -r line; do
        case "$line" in
            "worktree "*) wt_path="${line#worktree }" ;;
            "prunable"*) [ -z "$wt_path" ] || advisories+=("Stale worktree registration: $wt_path (directory gone; clear it with .prdkit/scripts/bash/cleanup-change.sh --prune)") ;;
        esac
    done < <(git -C "$root" worktree list --porcelain 2>/dev/null || true)
fi

if $strict; then findings+=(${advisories[@]+"${advisories[@]}"}); advisories=(); fi
result=pass; [ "${#findings[@]}" -eq 0 ] || result=fail
if $json; then
    printf '{"result":"%s","findings":%s,"advisories":%s,"strict":%s,"readiness":"not-evaluated"}\n' \
        "$result" "$(json_array ${findings[@]+"${findings[@]}"})" "$(json_array ${advisories[@]+"${advisories[@]}"})" "$strict"
else
    printf 'Product-Kit doctor: %s\n' "$result"
    for reason in ${findings[@]+"${findings[@]}"}; do printf '  %s\n' "$reason"; done
    for reason in ${advisories[@]+"${advisories[@]}"}; do printf '  advisory: %s\n' "$reason"; done
    [ "${#advisories[@]}" -eq 0 ] || printf 'Advisories do not fail doctor; --strict makes them failing findings.\n'
    printf 'No repairs performed. A clean report is not readiness.\n'
fi
[ "$result" = pass ]
