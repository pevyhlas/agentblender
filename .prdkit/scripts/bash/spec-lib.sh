#!/usr/bin/env bash
# spec-lib.sh — read-only parsers for the 1.0 spec layout, shared by status.sh, doctor.sh,
# bounds.sh, check-review.sh, close-change.sh, new-change.sh and init.sh. Every function prints
# rows separated by the unit separator (\037, SPEC_SEP) — a tab would let `read` collapse empty
# fields — and never writes. Bash 3.2 and POSIX awk only.
# shellcheck disable=SC2034 # used by the scripts that source this library
SPEC_SEP="$(printf '\037')"
if ! declare -F verify_lf_file >/dev/null; then
    source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
fi

# Text carried in row fields must never introduce a field or record boundary.
spec_field() { printf '%s' "$1" | tr '\037\t\r\n' '    '; }

# IDs: J-, R-, Q-, C-, N- (product), D- (decisions), <part>.B- and <part>.I-, two or more digits.
# A neighbouring character is tested with index(), never a regex: macOS awk counts bytes, and a
# regex over the lone byte of a multibyte character (an em dash after an ID) aborts it in a UTF-8
# locale. tests/test_script_safety.sh guards this for every script.
SPEC_ID_AWK='function spec_ids(s, where,   pre, post, tok, w) {
    w = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"
    while (match(s, /(P[0-9]+(\.[0-9]+)?\.[BI]|[JRQCDN])-[0-9][0-9]+/)) {
        pre = RSTART > 1 ? substr(s, RSTART - 1, 1) : ""
        tok = substr(s, RSTART, RLENGTH)
        post = substr(s, RSTART + RLENGTH, 1)
        if (!(pre != "" && index(w ".", pre)) && !(post != "" && index(w, post))) print tok "\037" where
        s = substr(s, RSTART + RLENGTH)
    }
}'

# Legacy definition prefix used by spec_items.
SPEC_BULLET_ID_AWK='function spec_bullet_id(s) {
    if (!sub(/^[[:space:]]*[-*][[:space:]]+\*\*/, "", s) ||
        s !~ /^(P[0-9]+(\.[0-9]+)?\.[BI]|[JRQCDN])-[0-9]/) return ""
    sub(/[[:space:]*].*/, "", s)
    return s
}'

SPEC_HEADING_AWK='function spec_heading(line,   depth) {
    if (!match(line, /^#+/) || RLENGTH > 6) return 0
    depth = RLENGTH; heading_title = substr(line, RLENGTH + 1)
    if (heading_title !~ /^([ \t]|$)/) return 0
    sub(/[ \t]+#+[ \t]*$/, "", heading_title)
    gsub(/^[ \t]+|[ \t]+$/, "", heading_title)
    return depth
}'

SPEC_PART_AWK='
    function spec_part_id(s) { return s ~ /^P[0-9]+(\.[0-9]+)?$/ }
    function spec_part_header(s) { return s ~ /^\|[[:space:]]*[Pp]art[[:space:]]*\|/ }
    function spec_part_row(s,   id) {
        if (!sub(/^\|[[:space:]]*/, "", s) || !match(s, /^[^[:space:]|]+[[:space:]]/)) return ""
        id = substr(s, 1, RLENGTH - 1)
        return spec_part_id(id) ? id : ""
    }
    function spec_retired_part(s,   id) {
        if (!sub(/^[[:space:]]*[-*][[:space:]]+(\*\*)?/, "", s) ||
            !match(s, /^[^[:space:]|]+[[:space:]]/) || s !~ /^[^|]*retired:/) return ""
        id = substr(s, 1, RLENGTH - 1)
        return spec_part_id(id) ? id : ""
    }'

# Parse a record row without consulting authority files. Return a diagnostic or fill row.
SPEC_RECORD_ROW_AWK='
    function spec_record_date(s) { return s ~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]$/ }
    function spec_record_row(line, row,   n, f, type, name, kv, need, date, until, who, i, k, stray, m, req, missing, reason) {
        if (line !~ /^(HOLD|WAIVER|ATTEST|DEPLOY)([[:space:]]|$)/) return "not a HOLD, WAIVER, ATTEST or DEPLOY record"
        if (match(line, /[[:space:]]—([[:space:]]|$)/)) {
            reason = substr(line, RSTART + RLENGTH); line = substr(line, 1, RSTART - 1)
            sub(/^[[:space:]]+/, "", reason)
        }
        gsub(/\t/, " ", reason)
        n = split(line, f, /[[:space:]]+/)
        type = f[1]; name = f[2]
        if (name !~ /^[A-Za-z0-9][A-Za-z0-9._-]*$/) return type " needs an id or app name first"
        # A word without "=" continues the preceding value, except when it is a date.
        for (i = 3; i <= n; i++)
            if (index(f[i], "=")) { k = substr(f[i], 1, index(f[i], "=") - 1); kv[k] = substr(f[i], index(f[i], "=") + 1) }
            else if (f[i] != "" && k != "") { if (!spec_record_date(kv[k])) kv[k] = kv[k] " " f[i] }
            else if (f[i] != "" && stray == "") stray = f[i]
        if ((type == "HOLD" || type == "WAIVER") && ("ref" in kv))
            return type " " name ": put ref= after the reason separator ( — )"
        if (type == "HOLD") { need = "owner scope since until"; date = kv["since"]; until = kv["until"]; who = kv["owner"] }
        else if (type == "WAIVER") { need = "owner check scope expires"; until = kv["expires"]; who = kv["owner"] }
        else if (type == "ATTEST") { need = "by what env version date"; date = kv["date"]; who = kv["by"] }
        else { need = "env rev date by"; date = kv["date"]; who = kv["by"] }
        if (stray != "") return type " " name ": \"" stray "\" comes before any key=value"
        m = split(need, req, " ")
        for (i = 1; i <= m; i++) if (kv[req[i]] == "") missing = missing " " req[i] "="
        if (missing != "") return type " " name " is missing" missing
        if (date != "" && !spec_record_date(date)) return type " " name " date must be YYYY-MM-DD"
        if (type == "WAIVER" && !spec_record_date(until)) return "WAIVER " name " expires= must be YYYY-MM-DD"
        row["type"] = type; row["name"] = name; row["owner"] = who
        row["scope"] = kv["scope"]; row["env"] = kv["env"]; row["date"] = date; row["until"] = until
        row["rev"] = (type == "ATTEST") ? kv["version"] : kv["rev"]
        row["check"] = (type == "ATTEST") ? kv["what"] : kv["check"]; row["reason"] = reason
        return ""
    }'

source "$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/spec-navigation-lib.sh"

# Metadata readers ignore both kinds of Markdown fences, including shorter inner fences.
SPEC_METADATA_SKIP_AWK='function metadata_skip(line,    marker, ending) {
    marker = line; sub(/^ ? ? ?/, "", marker)
    if (metadata_code != "") {
        ending = marker; sub(/[[:space:]]+$/, "", ending)
        if (length(ending) >= length(metadata_code) && (ending ~ /^`+$/ || ending ~ /^~+$/) &&
            substr(ending, 1, 1) == substr(metadata_code, 1, 1)) metadata_code = ""
        return 1
    }
    if (metadata_comment || line ~ /^[[:space:]]*<!--/) { metadata_comment = !index(line, "-->"); return 1 }
    if (match(marker, /^```+|^~~~+/)) { metadata_code = substr(marker, 1, RLENGTH); return 1 }
    return 0
}'

# Read metadata only at the start of a field, never inside its prose. For readers of the
# other fields, remove only Tags and leave their delimiters intact.
SPEC_HEADER_AWK='function metadata_trim(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
function metadata_header(s,   label, value, rest, field, kept, original, has_tags, indent, sep, nextsep, gap) {
    original = s
    match(s, /^ ? ? ?/); indent = substr(s, 1, RLENGTH); s = substr(s, RLENGTH + 1)
    while (match(s, /^\*\*[^*]+\*\*:/)) {
        field = substr(s, 1, RLENGTH); label = substr(s, 3, RLENGTH - 5); s = substr(s, RLENGTH + 1)
        value = s; rest = ""; nextsep = ""
        if (match(s, /[[:space:]]*·[[:space:]]*\*\*[^*]+\*\*:/)) {
            value = substr(s, 1, RSTART - 1); rest = substr(s, RSTART)
            match(rest, /^[[:space:]]*·[[:space:]]*/)
            nextsep = substr(rest, 1, RLENGTH); rest = substr(rest, RLENGTH + 1)
        }
        header[label] = metadata_trim(value); fields[label]++; field_line[label] = NR
        if (label != "Tags") {
            # Only normalize newly joined boundaries: other readers split on a spaced dot.
            if (gap) sep = " · "
            kept = kept (kept != "" ? sep : indent) field value
            sep = nextsep; gap = 0
        } else {
            has_tags = 1; gap = 1
        }
        s = rest
    }
    if (has_tags) return kept
    return original
}'

# Additive input hook: no change to any line without a header Tags field.
SPEC_CHANGE_TAG_AWK="$SPEC_METADATA_SKIP_AWK $SPEC_HEADER_AWK"'function change_without_tags(line) {
    if (tag_section || metadata_skip(line)) return line
    if (line ~ /^##+[[:space:]]/) { tag_section = 1; return line }
    return metadata_header(line)
}'

# Lines inside HTML comments and code fences are format notes, never content.
SPEC_SKIP_AWK='function spec_skip(line) {
    if (fence) { if (line ~ /^[[:space:]]*```/) fence = 0; return 1 }
    if (line ~ /^[[:space:]]*```/) { fence = 1; return 1 }
    if (comment) { if (index(line, "-->")) comment = 0; return 1 }
    if (line ~ /^[[:space:]]*<!--/) { if (!index(line, "-->")) comment = 1; return 1 }
    return 0
}'

# The default order; a canonical lifecycle header can replace it with **Stages**: A > B > C.
# Load once per consumer invocation, then pass stage_order and stage_custom to ranking awk.
SPEC_STAGE_DEFAULT='idea>demo>mvp>private preview>public preview>paid preview>ga>sunset>retired'
# shellcheck disable=SC2016 # awk code: its $ is awk's, never the shell's
SPEC_STAGE_AWK='function stage_trim(s) {
    gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); gsub(/[[:space:]]+/, " ", s); return s
}
function stage_rank(s,   i, n, a) {
    if (!stage_loaded) {
        stage_loaded = 1
        n = split(stage_order, a, ">")
        for (i = 1; i <= n; i++) stage_ranks[tolower(stage_trim(a[i]))] = i
    }
    s = tolower(stage_trim(s))
    if (!stage_custom && s == "general availability") s = "ga"
    return (s in stage_ranks) ? stage_ranks[s] : 0
}
# The stage named by a `from: <stage>` marker in text (the parenthetical of an item heading or
# bullet, or a parts-table Maturity cell), lowercased; blank when there is none.
function stage_from(text,   t) {
    t = tolower(text)
    if (!match(t, /(^|[[:space:],;(])from:[[:space:]]*/)) return ""
    t = substr(t, RSTART + RLENGTH); sub(/[,;)|].*/, "", t); t = stage_trim(t)
    return t == "" ? "(empty)" : t
}'

spec_today() { printf '%s\n' "${PRDKIT_TODAY:-$(date +%Y-%m-%d)}"; }

spec_is_date() { [[ "$1" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]]; }

# Days from $1 to $2 (both YYYY-MM-DD), without relying on GNU or BSD date options.
spec_days_between() {
    awk -v a="$1" -v b="$2" '
        function days(s,   y, m, d, era, yoe, doy) {
            y = substr(s, 1, 4) + 0; m = substr(s, 6, 2) + 0; d = substr(s, 9, 2) + 0
            if (m <= 2) y--
            era = int(y / 400); yoe = y - era * 400
            doy = int((153 * (m > 2 ? m - 3 : m + 9) + 2) / 5) + d - 1
            return era * 146097 + yoe * 365 + int(yoe / 4) - int(yoe / 100) + doy
        }
        BEGIN { print days(b) - days(a) }'
}

# A retired spec directory is in use when it holds anything besides a .gitkeep. In a Git work tree
# only files Git does not ignore count: an ignored leftover never reaches main.
_spec_dir_used() {
    local entry listed
    [ -d "$1" ] || return 1
    if listed="$(git -C "$1" ls-files --cached --others --exclude-standard -- . 2>/dev/null)"; then
        while IFS= read -r entry; do
            case "$entry" in ''|.gitkeep|*/.gitkeep) ;; *) return 0 ;; esac
        done <<< "$listed"
        return 1
    fi
    for entry in "$1"/* "$1"/.[!.]*; do
        { [ -e "$entry" ] || [ -L "$entry" ]; } || continue
        [ "${entry##*/}" = .gitkeep ] || return 0
    done
    return 1
}

_spec_has_canon() { [ -f "$1/specs/product.md" ] || [ -f "$1/specs/architecture.md" ] || [ -f "$1/specs/SPEC.md" ]; }

# Detection only: never use the old paths as inputs to history or numbering.
_spec_history_move_required() {
    printf '%s\n' "Old history paths remain ($1). Move any that exist, then commit the move (see CHANGELOG):" \
        '  mkdir -p .prdkit/history' \
        '  git mv specs/log.md .prdkit/history/log.md' \
        '  git mv specs/log .prdkit/history/log' \
        '  git mv specs/archive.md .prdkit/history/archive.md' >&2
    return 1
}
spec_require_history_layout() { # root [--head] -> physical paths, index (including sparse entries), optionally HEAD
    local path old head
    for path in specs/log.md specs/log specs/archive.md; do
        if [ -e "$1/$path" ] || [ -L "$1/$path" ]; then
            _spec_history_move_required "$1"; return 1
        fi
    done
    if [ "$(git -C "$1" rev-parse --is-inside-work-tree 2>/dev/null)" = true ]; then
        old="$(verify_lines git -C "$1" ls-files -- ':(top)specs/log.md' ':(top)specs/log' ':(top)specs/archive.md')" ||
            { echo "Cannot check the Git index for old history paths at $1" >&2; return 1; }
        [ -z "$old" ] || { _spec_history_move_required "$1"; return 1; }
        [ "${2:-}" = --head ] || return 0
        # A staged deletion does not establish that the history has moved.
        if head="$(_spec_archive_git -C "$1" rev-parse --verify --quiet HEAD)"; then
            spec_require_history_head "$1" "$head" || return "$?"
        elif [ "$?" -ne 1 ]; then
            echo "Cannot identify HEAD for the history check at $1" >&2
            return 1
        fi
    fi
}
spec_require_history_head() ( # root, frozen HEAD
    if [ "${SPEC_HISTORY_VIEW_ROOT:-}" != "$1" ]; then
        spec_open_history_view "$1" || return 1
        trap spec_close_history_view EXIT
    fi
    spec_require_history_tips "$1" "$2" "HEAD of $1; restore any staged deletions before moving"
)
spec_require_history_tips() { # root, frozen tips [diagnostic context]
    local rev old
    while IFS= read -r rev; do
        [ -n "$rev" ] || continue
        if ! old="$(verify_lines _spec_history_git "$1" ls-tree --name-only "$rev" -- specs/log.md specs/log specs/archive.md)"; then
            if [ "${SPEC_HISTORY_VIEW_ROOT:-}" = "$1" ] && [ -n "$SPEC_HISTORY_VIEW" ]; then
                echo "Legacy history layout unchecked at $rev: the local object store could not supply the tree on this partial clone's older Git" >&2
                return 2
            fi
            echo "Cannot check $rev for old history paths" >&2
            return 1
        fi
        [ -z "$old" ] || { _spec_history_move_required "${3:-commit $rev; update stale branches and refs after moving}"; return 1; }
    done <<< "$2"
}

spec_require_layout() { # root
    local name line nr=0
    for name in product-spec targets features archive; do
        if _spec_dir_used "$1/specs/$name"; then
            printf 'Unsupported specs/%s/ layout: expected specs/SPEC.md or split specs/product.md and specs/architecture.md, specs/changes/<id>.md records and .prdkit/history/archive.md history.\n' "$name" >&2
            return 1
        fi
    done
    if [ -f "$1/.prdkit/history/archive.md" ]; then
        while IFS= read -r line || [ -n "$line" ]; do
            nr=$((nr + 1))
            _spec_trim "${line//[*_\`#|>]/}"
            case "$_spec_t" in
                [Ll][Aa][Ss][Tt]' '0.[Xx]' '[Ff][Ee][Aa][Tt][Uu][Rr][Ee]*)
                    printf 'Unsupported feature counter in .prdkit/history/archive.md:%s: current archives reserve change numbers with **Log condensed through**: <number>.\n' "$nr" >&2
                    return 1 ;;
            esac
        done < "$1/.prdkit/history/archive.md" || return 1
    fi
}

# Current spec files present, or none yet.
spec_layout() { # root
    if _spec_has_canon "$1"; then echo 1.0; else echo none; fi
}

# The split spec files that sit beside a single-file specs/SPEC.md, space-separated relative
# paths; empty when only one form is in use. Both at once means two competing 1.0 specs.
spec_mixed_forms() { # root
    local root="$1" path found=""
    [ -f "$root/specs/SPEC.md" ] || return 0
    for path in "$root"/specs/product.md "$root"/specs/product/*.md "$root"/specs/architecture.md \
                "$root"/specs/parts/*.md; do
        [ -f "$path" ] && found="$found${found:+ }${path#"$root"/}"
    done
    printf '%s' "$found"
}

_spec_trim() { # text -> $_spec_t without leading blanks, colons, equals signs and dashes
    _spec_t="$1"
    while :; do
        case "$_spec_t" in
            [[:space:]:=-]*) _spec_t="${_spec_t#?}" ;;
            —*) _spec_t="${_spec_t#—}" ;;
            –*) _spec_t="${_spec_t#–}" ;;
            *) return 0 ;;
        esac
    done
}
# One normalized row: cutoff (0 for none), then sparse lo-hi pairs, without expanding
# ranges. Details also carry the sparse pointer and the independent allocation floor,
# each with its source line. The floor does not imply a close.
spec_archive_log_index() { # root [diagnostic-path]
    local row
    row="$(spec_archive_log_details "$@")" || return 1
    printf '%s\n' "${row%%"$SPEC_SEP"*}"
}
spec_archive_log_details() ( # root [diagnostic-path]
    local path=.prdkit/history/archive.md entries tmp diagnostic="${2:-.prdkit/history/archive.md}"
    if [ ! -e "$1/$path" ] && [ ! -L "$1/$path" ] &&
        _spec_archive_git -C "$1" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        entries="$(verify_lines _spec_archive_git -C "$1" ls-files -t -- "$path")" || return 1
        # An absent skip-worktree path is not a deletion. Read the index, not HEAD:
        # a staged archive may differ even if Git leaves the file out of the checkout.
        if [ "$entries" = "S $path" ]; then
            if ! spec_objects_local "$1" || ! tmp="$(mktemp)"; then
                printf '%s: condensed log index unavailable from the Git index\n' "$diagnostic" >&2
                return 1
            fi
            trap 'rm -f "$tmp"' EXIT
            if ! _spec_archive_git -C "$1" show ":./$path" > "$tmp" 2>/dev/null; then
                printf '%s: condensed log index unavailable from the Git index\n' "$diagnostic" >&2
                return 1
            fi
            _spec_archive_log_file "$tmp" "$diagnostic" " (in the Git index)"
            return
        fi
    fi
    _spec_archive_log_file "$1/$path" "$diagnostic"
)

_spec_archive_log_file() { # file diagnostic-path [diagnostic-context]
    local parsed expected end index pointer line floor floor_line context="${3:-}"
    if [ ! -e "$1" ] && [ ! -L "$1" ]; then echo 0; return 0; fi
    if [ ! -f "$1" ] || ! review_file_without_nul "$1"; then
        printf '%s: cannot read condensed log index (expected text)%s\n' "$2" "$context" >&2
        return 1
    fi
    expected="$(wc -l < "$1")" || return 1
    if [ -s "$1" ]; then
        end="$(tail -c 1 "$1" && printf x)" || return 1
        [ "$end" = $'\nx' ] || expected=$((expected + 1))
    fi
    parsed="$(LC_ALL=C awk -v expected="$expected" "$SPEC_METADATA_SKIP_AWK"'
        function trim(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
        function number(s) { return s ~ /^[0-9]+$/ && length(s) <= 9 }
        function fail(why) { print NR ": " why; bad = 1; exit 1 }
        # Parse only at the field boundary. Ordinary prose after the code span is not a set.
        function git_pointer(s,    sha, tail) {
            if (!match(s, /^;[ \t]*those entries:[ \t]*`git show [0-9a-fA-F]+:\.prdkit\/history\/log\.md`/)) return ""
            tail = trim(substr(s, RLENGTH + 1))
            if (tail != "" && tail != "." && tail !~ /^[.;][ \t]+[^[:space:];,]/) return ""
            if (continuation(sub_tail(tail))) return ""
            sha = substr(s, 1, RLENGTH); sub(/^.*`git show /, "", sha); sub(/:.*/, "", sha)
            if (length(sha) < 7 || length(sha) > 64) return ""
            return sha
        }
        function sub_tail(s) { sub(/^[.;]/, "", s); return s }
        function value(s,    lead, rest, raw, semi, list, fields, n, i, bounds, pair, lo, hi, prev) {
            s = trim(s)
            # Older archives also used a bare value, an "as of" date, or a dash and
            # "entries at" annotation. Do not accept arbitrary text in the metadata lane.
            if (s == "<NNN, or none>; those entries: `git show <sha>:.prdkit/history/log.md`." || s ~ /^through [0-9]+$/) return
            raw = s; sub(/[ \t;].*$/, "", raw)
            rest = trim(substr(s, length(raw) + 1))
            lead = tolower(raw); gsub(/[*_`]/, "", lead)
            if (match(lead, /^([0-9]+|none|n\/a)/) && RLENGTH < length(lead)) {
                rest = substr(lead, RLENGTH + 1) (rest == "" ? "" : " " rest)
                lead = substr(lead, 1, RLENGTH)
            }
            if (lead ~ /^[0-9]/ && !number(lead)) fail("invalid cutoff number")
            if (lead != "none" && lead != "n/a" && !number(lead)) fail("expected a cutoff number or none")
            cutoff = lead + 0; prev = cutoff
            if (rest == "" || rest == "." || rest == "|" || git_pointer(rest) != "" ||
                rest ~ /^as of [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\.?$/ ||
                rest ~ /^(—|–|-) entries at `git show [0-9a-fA-F]+:\.prdkit\/history\/log\.md`\.?$/) return
            if (!match(rest, /^;[ \t]*also[ \t]+/)) fail("expected ; also <IDs and ranges>; or ; those entries: `git show <sha>:.prdkit/history/log.md`")
            if (NR != label_line || lead == "n/a" || tolower(raw) != lead)
                fail("keep the sparse field on the cutoff line and use a plain number or none")
            rest = substr(rest, RLENGTH + 1); semi = index(rest, ";")
            if (!semi || (pointer = git_pointer(substr(rest, semi))) == "") fail("expected ; those entries: `git show <sha>:.prdkit/history/log.md` after the sparse set")
            list = trim(substr(rest, 1, semi - 1))
            if (list == "" || list ~ /,$/) fail("empty ID or range")
            n = split(list, fields, ",")
            for (i = 1; i <= n; i++) {
                pair = split(trim(fields[i]), bounds, "-")
                if (pair < 1 || pair > 2 || !number(trim(bounds[1])) || (pair == 2 && !number(trim(bounds[2]))))
                    fail("expected a numeric ID or closed range")
                lo = trim(bounds[1]) + 0; hi = pair == 2 ? trim(bounds[2]) + 0 : lo
                if (hi < lo) fail("descending range")
                if (lo <= prev) fail("IDs and ranges must be above the cutoff, ascending and non-overlapping")
                ranges = ranges " " lo "-" hi; prev = hi
            }
        }
        function continuation(s,    low) {
            low = tolower(trim(s))
            return low ~ /^[;,]|^als[o]?[ \t]*([0-9]|[-,;:]|$)|^those entries:|^[0-9]+([-,;]|$)/
        }
        {
            if (metadata_skip($0)) next
            s = $0; sub(/\r$/, "", s)
            if (after && trim(s) != "") {
                rest = trim(s); sub(/^([>#*]+|-)[ \t]+/, "", rest)
                if (continuation(rest) || tolower(rest) ~ /^als[o]?([ \t]|$)/)
                    fail("keep the condensed log fields on one line")
                after = 0
            }
            sub(/^([-[:space:]*_`#|>:=]|—|–)+/, "", s)
            if (!pending && tolower(s) ~ /^allocated through([[:space:]*_:=-]|$)/) {
                if (floor_line) fail("duplicate Allocated through field")
                s = substr(s, 18)
                if (!sub(/^[ \t*_]*:[ \t]*/, "", s) || !number(trim(s)))
                    fail("expected Allocated through: followed by one to nine decimal digits")
                floor = trim(s) + 0; floor_line = NR
                next
            }
            if (tolower(s) ~ /^log condensed through([-[:space:]*_`#|>:=]|—|–|$)/) {
                if (seen++) fail("duplicate Log condensed through field")
                label_line = NR
                s = substr(s, 22)
                if (!sub(/^[[:space:]*_`#|>]*:[[:space:]]*/, "", s))
                    sub(/^([-[:space:]*_`#|>:=]|—|–)+/, "", s)
                pending = 1
            }
            if (pending && trim(s) != "") {
                value(s); value_line = NR; after = 1; pending = 0
            }
        }
        END {
            if (bad) exit 1
            if (NR != expected) fail("incomplete archive read (expected " expected + 0 " lines, checked " NR ")")
            if (pending) { print label_line ": expected a cutoff number or none"; exit 1 }
            print cutoff + 0 ranges "\037" (pointer != "" ? pointer : "-") "\037" value_line + 0 \
                  "\037" (floor_line ? floor : "-") "\037" floor_line + 0
        }
    ' "$1")" || {
        printf '%s:%s%s\n' "$2" "${parsed:- cannot read condensed log index}" "$context" >&2
        return 1
    }
    IFS="$SPEC_SEP" read -r index pointer line floor floor_line <<< "$parsed"
    if ! [[ "$index" =~ ^[0-9]+(\ [0-9]+-[0-9]+)*$ && "$line" =~ ^[0-9]+$ &&
        "$floor" =~ ^(-|[0-9]+)$ && "$floor_line" =~ ^[0-9]+$ ]]; then
        printf '%s: incomplete condensed log index%s\n' "$2" "$context" >&2
        return 1
    fi
    if [ "$pointer" != - ] && ! verify_lf_file "$1"; then
        printf '%s:%s: sparse condensed log index requires LF-terminated text%s\n' "$2" "$line" "$context" >&2
        return 1
    fi
    printf '%s\n' "$parsed"
}

_spec_archive_git() {
    GIT_NO_LAZY_FETCH=1 GIT_NO_REPLACE_OBJECTS=1 git "$@"
}

# Older Git has no no-lazy-fetch switch. Read frozen IDs through an empty repo
# with the original object store as an alternate, but no promisor remotes.
SPEC_HISTORY_VIEW=""; SPEC_HISTORY_VIEW_ROOT=""
_spec_history_view_git() (
    unset GIT_DIR GIT_COMMON_DIR GIT_WORK_TREE GIT_OBJECT_DIRECTORY GIT_CONFIG GIT_CONFIG_PARAMETERS
    GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_COUNT=0 GIT_ALLOW_PROTOCOL='' _spec_archive_git "$@"
)
spec_open_history_view() { # root; caller owns the lifetime of the optional view
    local objects format
    SPEC_HISTORY_VIEW=""; SPEC_HISTORY_VIEW_ROOT=""
    if spec_objects_local "$1"; then
        SPEC_HISTORY_VIEW_ROOT="$1"
        return 0
    elif [ "$?" -ne 2 ]; then return 1; fi
    objects="$(git -C "$1" rev-parse --path-format=absolute --git-path objects)" || return 1
    format="$(git -C "$1" rev-parse --show-object-format)" || return 1
    SPEC_HISTORY_VIEW="$(mktemp -d)" || return 1
    if ! _spec_history_view_git init --quiet --bare --template= --object-format="$format" "$SPEC_HISTORY_VIEW" ||
        ! printf '%s\n' "$objects" > "$SPEC_HISTORY_VIEW/objects/info/alternates"; then
        spec_close_history_view
        return 1
    fi
    SPEC_HISTORY_VIEW_ROOT="$1"
}
spec_close_history_view() {
    [ -z "${SPEC_HISTORY_VIEW:-}" ] || rm -rf -- "$SPEC_HISTORY_VIEW"
    SPEC_HISTORY_VIEW=""; SPEC_HISTORY_VIEW_ROOT=""
}
_spec_history_git() { # root command ...
    local root="$1"; shift
    if [ "${SPEC_HISTORY_VIEW_ROOT:-}" = "$root" ]; then
        if [ -n "$SPEC_HISTORY_VIEW" ]; then
            _spec_history_view_git --git-dir="$SPEC_HISTORY_VIEW" "$@"
            return
        fi
    else
        spec_objects_local "$root" || return 1
    fi
    _spec_archive_git -C "$root" "$@"
}

_spec_regular_blob_at() { # root rev path -> blob ID, empty when absent
    local path="$3" row meta
    row="$(verify_lines _spec_history_git "$1" ls-tree "$2" -- ":(literal)$path")" || return 1
    [ -n "$row" ] || return 0
    meta="${row%%$'\t'*}"
    if [ "${4:-}" = --skip-symlink ] &&
        [[ "${row#*$'\t'}" = "$path" && "$meta" =~ ^120000\ blob\ [0-9a-f]{40}([0-9a-f]{24})?$ ]]; then
        return 0
    fi
    if ! [[ "${row#*$'\t'}" = "$path" && "$meta" =~ ^100(644|755)\ blob\ [0-9a-f]{40}([0-9a-f]{24})?$ ]]; then
        printf '%s at %s: expected a regular file\n' "$path" "$2" >&2
        return 1
    fi
    printf '%s\n' "${meta##* }"
}

spec_archive_log_details_at() ( # root commit
    local path=.prdkit/history/archive.md blob tmp
    blob="$(_spec_regular_blob_at "$1" "$2" "$path")" || return 1
    if [ -z "$blob" ]; then echo 0; return 0; fi
    tmp="$(mktemp)" || return 1
    trap 'rm -f "$tmp"' EXIT
    _spec_history_git "$1" cat-file blob "$blob" > "$tmp" || return 1
    _spec_archive_log_file "$tmp" "$path" " (at $2)"
)

spec_archive_log_previous() { # root -> at most one earlier committed archive
    local rev=HEAD
    spec_objects_local "$1" || return 0
    # Compare working edits to HEAD, or a clean checkout to the first parent.
    if _spec_archive_git -C "$1" diff --quiet HEAD -- .prdkit/history/archive.md 2>/dev/null; then rev=HEAD^; fi
    _spec_archive_git -C "$1" rev-parse --verify --quiet "$rev^{commit}" >/dev/null 2>&1 || return 0
    spec_archive_log_details_at "$1" "$rev" 2>/dev/null || return 0
}

spec_integration_tips() ( # root -> distinct local commit IDs, no fetch
    local root="$1" target refs rev ref refname default="" configured seen=" " candidates=()
    target="$(_spec_archive_git -C "$root" symbolic-ref --quiet refs/remotes/origin/HEAD)" ||
        { [ "$?" -eq 1 ] || return 1; }
    case "$target" in
        refs/remotes/*/*)
            ref="${target#refs/remotes/}"
            candidates+=("$target" "refs/heads/${ref#*/}") ;;
        refs/heads/*) candidates+=("$target") ;;
    esac
    candidates+=(refs/remotes/origin/main refs/remotes/origin/master refs/heads/main refs/heads/master)
    refs="$(verify_lines _spec_archive_git -C "$root" for-each-ref --format='%(objectname) %(refname)' "${candidates[@]}")" || return 1
    if rev="$(_spec_archive_git -C "$root" rev-parse --verify --quiet HEAD)"; then
        seen="$seen$rev "; printf '%s\n' "$rev" || return 1
    elif [ "$?" -ne 1 ]; then return 1
    fi
    while read -r rev ref; do
        case " ${candidates[*]} " in *" $ref "*) ;; *) continue ;; esac
        if [ "$ref" = "$target" ]; then default="$rev"; fi
        case "$seen" in *" $rev "*) continue ;; esac
        seen="$seen$rev "; printf '%s\n' "$rev" || return 1
    done <<< "$refs"
    # Use the same committed policy as review. Read it at the frozen origin/HEAD
    # tip, never from the checkout or from a configured branch itself.
    case "$target" in refs/remotes/*) ;; *) return 0 ;; esac
    [ -n "$default" ] || return 0
    configured="$(spec_trusted_refs "$root" --at "$target" "$default")" || return 1
    while IFS= read -r ref; do
        [ -n "$ref" ] || continue
        # Do not re-read refs already frozen above.
        case " ${candidates[*]} " in *" $ref "*) continue ;; esac
        refs="$(verify_lines _spec_archive_git -C "$root" for-each-ref --format='%(objectname) %(refname)' "$ref")" || return 1
        while read -r rev refname; do
            # A configured ref can be absent; a match beneath it is not a match.
            [ "$refname" = "$ref" ] || continue
            rev="$(_spec_history_git "$root" rev-parse --verify "$rev^{commit}")" || return 1
            case "$seen" in *" $rev "*) continue ;; esac
            seen="$seen$rev "; printf '%s\n' "$rev" || return 1
        done <<< "$refs"
    done <<< "$configured"
    return 0
)

spec_archive_log_integration() { # root [frozen tips [details]] -> distinct commit rows
    local tips rev row
    if [ "$#" -ge 2 ]; then tips="$2"; else tips="$(spec_integration_tips "$1")" || return 1; fi
    while IFS= read -r rev; do
        [ -n "$rev" ] || continue
        row="$(spec_archive_log_details_at "$1" "$rev")" || return 1
        if [ "${3:-}" != details ]; then row="${row%%"$SPEC_SEP"*}"; fi
        printf '%s\n' "$row" || return 1
    done <<< "$tips"
}

spec_number_claims_at() ( # root commit -> allocated log and record numbers, no fetch
    local paths blob
    paths="$(verify_lines _spec_history_git "$1" ls-tree "$2" -- specs/changes/ .prdkit/history/log/)" || return 1
    LC_ALL=C awk '
        $1 ~ /^100(644|755)$/ && $2 == "blob" &&
        $4 ~ /^(specs\/changes|\.prdkit\/history\/log)\/[0-9][0-9][0-9][0-9]*-[a-z0-9-]+\.md$/ && NF == 4 {
            n = split($4, path, "/"); sub(/\.md$/, "", path[n]); print "committed", path[n-1], path[n]
        }
    ' <<< "$paths" || return 1
    blob="$(_spec_regular_blob_at "$1" "$2" .prdkit/history/log.md)" || return 1
    [ -n "$blob" ] || return 0
    set -o pipefail
    _spec_history_git "$1" cat-file blob "$blob" |
        sed -n 's/^- [0-9-]* \([0-9][0-9][0-9]*-[a-z0-9-]*\):.*/committed log \1/p'
)

spec_archive_log_covers() { # normalized index, change ID (or just its number)
    local cutoff="${1%% *}" n="${2%%-*}" range
    case "$n" in ''|*[!0-9]*) return 1 ;; esac
    n="${n#"${n%%[!0]*}"}"; n="${n:-0}"
    [ "${#n}" -le 9 ] || return 1
    if [ "$cutoff" -gt 0 ] && [ "$n" -le "$cutoff" ]; then return 0; fi
    for range in ${1#"$cutoff"}; do
        [ "$n" -ge "${range%-*}" ] || return 1
        [ "$n" -gt "${range#*-}" ] || return 0
    done
    return 1
}

# Sparse proof only. A shallow clone without the commit returns 2 (explicitly
# unverified); an available commit must prove every range in its folded regular log.
spec_archive_log_proof() ( # root index pointer
    local root="$1" index="$2" pointer="$3" commit blob tmp numbers missing expected
    spec_objects_local "$root" || { echo "cannot verify snapshot $pointer without local Git objects"; return 1; }
    commit="$(verify_lines _spec_archive_git -C "$root" rev-parse --disambiguate="$pointer")" ||
        { echo "cannot look up sparse snapshot $pointer in local Git objects"; return 1; }
    if [ -z "$commit" ]; then
        if [ "$(_spec_archive_git -C "$root" rev-parse --is-shallow-repository 2>/dev/null)" = true ]; then
            echo "history unavailable in this shallow clone: cannot verify sparse snapshot $pointer; fetch the history and rerun doctor"
            return 2
        fi
        echo "cannot resolve sparse snapshot $pointer to a commit"
        return 1
    fi
    if ! [[ "$commit" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ ]] ||
        [ "$(_spec_archive_git -C "$root" cat-file -t "$commit")" != commit ]; then
        echo "sparse snapshot $pointer does not identify a unique commit"
        return 1
    fi
    blob="$(_spec_regular_blob_at "$root" "$commit" .prdkit/history/log.md 2>/dev/null)" &&
        [ -n "$blob" ] || { echo "sparse snapshot $pointer has no readable regular .prdkit/history/log.md"; return 1; }
    tmp="$(mktemp)" || return 1
    trap 'rm -f "$tmp"' EXIT
    if ! _spec_archive_git -C "$root" cat-file blob "$blob" > "$tmp" || ! verify_lf_file "$tmp"; then
        echo "cannot read complete LF text at $pointer:.prdkit/history/log.md"
        return 1
    fi
    expected="$(wc -l < "$tmp")" || return 1
    set -o pipefail
    numbers="$(LC_ALL=C awk -v expected="$expected" "$SPEC_METADATA_SKIP_AWK"'
        { if (metadata_skip($0)) next }
        /^- [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9] [0-9][0-9][0-9]+-[a-z0-9]+(-[a-z0-9]+)*: [[:space:]]*[^[:space:]]/ {
            n = $3; sub(/-.*/, "", n); sub(/^0+/, "", n)
            if (length(n) <= 9) print n + 0
        }
        END { if (NR != expected) exit 1 }
    ' "$tmp" | LC_ALL=C sort -nu)" || { echo "incomplete read of $pointer:.prdkit/history/log.md"; return 1; }
    # Walk sorted evidence and interval endpoints; report the first missing run.
    missing="$(LC_ALL=C awk -v index_row="$index" '
        function advance(   endpoints) {
            if (++i > n) return 0
            split(ranges[i], endpoints, "-"); lo = endpoints[1] + 0; hi = endpoints[2] + 0
            return 1
        }
        function gap(a, b) {
            if (!found) printf "%03d%s\n", a, (a == b ? "" : sprintf("-%03d", b))
            found = 1
        }
        BEGIN { n = split(index_row, ranges, " "); i = 1; advance() }
        NF && i <= n {
            id = $1 + 0
            if (id > hi) { gap(lo, hi); exit }
            if (id < lo) next
            if (id > lo) { gap(lo, id - 1); exit }
            if (++lo > hi) advance()
        }
        END { if (!found && i <= n) gap(lo, hi) }
    ' <<< "$numbers")" || return 1
    if [ -n "$missing" ]; then
        printf 'sparse snapshot %s:.prdkit/history/log.md is missing closed number(s) %s\n' "$pointer" "$missing"
        return 1
    fi
)

# The closed-change log: the lines of .prdkit/history/log.md, then each entry file .prdkit/history/log/<id>.md, one
# "- <date> <id>: <text>" line that close-change.sh writes, so two branches that each close a
# change touch different files and never conflict. Compaction folds the entry files into
# .prdkit/history/log.md (compact.md). Callers match the dated lines they need; awk ends every line.
spec_log_entries() { # root
    if [ -f "$1/.prdkit/history/log.md" ]; then
        verify_lf_file "$1/.prdkit/history/log.md" || { echo 'Invalid text in .prdkit/history/log.md: expected LF-terminated Markdown' >&2; return 1; }
        awk 1 "$1/.prdkit/history/log.md" || return 1
    fi
    spec_log_files "$@"
}
spec_log_files() { # root -> the entry files' lines only
    local file id
    for file in "$1"/.prdkit/history/log/[0-9]*.md; do
        [ -f "$file" ] && [ ! -L "$file" ] || continue
        id="${file##*/}"; id="${id%.md}"
        if ! spec_log_entry_valid "$file" "$id"; then
            printf 'Malformed log entry: %s; expected one LF-terminated line: - <date> <id>: <what changed>\n' "$file" >&2; return 1
        fi
        awk 1 "$file" || return 1
    done
    return 0
}

spec_log_entry_valid() { # file change-id
    local lines
    is_change_id "$2" && verify_lf_file "$1" &&
        lines="$(wc -l < "$1")" && [ "$lines" -eq 1 ] &&
        grep "^- [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9] $2: [[:space:]]*[^[:space:]]" "$1" >/dev/null
}

SPEC_CLOSE_ROW_MAX=200
# Under LC_ALL=C, dropping UTF-8 continuation bytes counts characters on BSD awk
# and on mawk alike. Apply this to the entire dated row, including header tags.
SPEC_CLOSE_LENGTH_AWK='function close_length(s) { gsub(/[\200-\277]/, "", s); return length(s) }'

# The same at a commit, for a gate that reads a ref and never the checkout. Only regular files
# count (a blob of mode 100644 or 100755): a tree, symlink or submodule at .prdkit/history/log.md or
# .prdkit/history/log/<id>.md is no entry. Fails, reading nothing, where an object read could fetch from a
# promisor remote (spec_objects_local); a missing object reads as nothing.
spec_log_entries_at() { # root rev
    local mode type sha path
    spec_objects_local "$1" || return 1
    { GIT_NO_LAZY_FETCH=1 git -C "$1" ls-tree "$2" -- .prdkit/history/log.md .prdkit/history/log/ 2>/dev/null || true; } |
        while read -r mode type sha path; do
            case "$mode $type" in "100644 blob"|"100755 blob") ;; *) continue ;; esac
            case "$path" in .prdkit/history/log.md|.prdkit/history/log/[0-9]*.md) ;; *) continue ;; esac
            GIT_NO_LAZY_FETCH=1 git -C "$1" cat-file blob "$sha" 2>/dev/null | awk 1 || true
        done
    return 0
}

# Compaction is due every ten closed changes (compact.md). Return "<count> [<date>]":
# entries newer than the latest compaction line, or all entries if no pass is recorded.
# Within log.md position counts, not dates, in either log order. Every entry file is
# newer: compaction folds them all. Keep status and doctor on this same accounting.
# A failed folded-log read must not be mistaken for an absent log. Archive
# duplicates do not count, but still mark dates used to recognize log order.
SPEC_ARCHIVE_COUNT_AWK='
    BEGIN {
        n_ranges = split(archive_index, archive_ranges, " "); cutoff = archive_ranges[1] + 0
        for (i = 2; i <= n_ranges; i++) {
            split(archive_ranges[i], endpoints, "-"); lo[i] = endpoints[1] + 0; hi[i] = endpoints[2] + 0
        }
    }
    function archived(s,   a, b, m, id) {
        sub(/-.*/, "", s); id = s + 0
        if (cutoff > 0 && id <= cutoff) return 1
        a = 2; b = n_ranges
        while (a <= b) {
            m = int((a + b) / 2)
            if (id < lo[m]) b = m - 1
            else if (id > hi[m]) a = m + 1
            else return 1
        }
        return 0
    }'
spec_compaction() { # root, normalized archive index
    local root="$1" compaction='0 0' closed_since last_pass pending expected checked
    if [ -f "$root/.prdkit/history/log.md" ]; then
        # Read order and counts together. Equal first/last dates mean newest-first.
        compaction="$(LC_ALL=C awk -v archive_index="$2" "$SPEC_ARCHIVE_COUNT_AWK"'
            /^- [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9] / {
                date = substr($0, 3, 10); if (!n++) first_date = date; last_date = date
            }
            /^- [0-9-]+ compaction:/ { if (!seen++) { above = c; top = $2 }; c = 0; last = $2; next }
            /^- [0-9-]+ [0-9][0-9][0-9][0-9]*-[a-z0-9-]*:/ && !archived($3) { c++ }
            END {
                printf "%d ", NR
                if (!seen) print c + 0
                else if (n && first_date >= last_date) print above + 0, top
                else print c + 0, last
            }' "$root/.prdkit/history/log.md")" || return "$?"
        verify_lf_file "$root/.prdkit/history/log.md" || { echo 'Invalid text in .prdkit/history/log.md' >&2; return 1; }
        expected="$(wc -l < "$root/.prdkit/history/log.md")" || return 1
        if ! [ "${compaction%% *}" -eq "$expected" ]; then
            printf 'Incomplete .prdkit/history/log.md: expected %s lines, checked %s\n' "$expected" "${compaction%% *}" >&2
            return 1
        fi
    fi
    read -r checked closed_since last_pass <<< "$compaction"
    pending="$(spec_log_files "$root" | LC_ALL=C awk -v archive_index="$2" "$SPEC_ARCHIVE_COUNT_AWK"'
        /^- [0-9-]+ [0-9][0-9][0-9][0-9]*-[a-z0-9-]*:/ && !archived($3) { c++ }
        END { print c + 0 }')" || return "$?"
    [[ "$closed_since" =~ ^[0-9]+$ && "$pending" =~ ^[0-9]+$ ]] || return 1
    printf '%s %s\n' "$((closed_since + pending))" "$last_pass"
}

# Reading length: a line counts once per 100 characters, rounded up, and at least once, so a
# long table row or paragraph cannot dodge a line budget. Only a validated generated-navigation
# footer is exempt, through the same recognizer as read-spec.sh. Physical lines remain raw.
spec_reading_lines() { # file -> "<reading lines> <lines>"
    spec_navigation "$1" budget
}

# Spec files that define IDs, relative to root.
spec_files() { # root
    local root="$1" path
    for path in "$root"/specs/SPEC.md "$root"/specs/product.md "$root"/specs/product/*.md \
                "$root"/specs/product/*/*.md "$root"/specs/architecture.md "$root"/specs/architecture/*.md \
                "$root"/specs/architecture/*/*.md \
                "$root"/specs/parts/*.md "$root"/specs/parts/*/*.md "$root"/specs/decisions/*.md; do
        [ -f "$path" ] || continue
        case "$path" in *$'\n'*|*$'\r'*|*"$SPEC_SEP"*) printf 'Unsupported record separator in spec path: %s\n' "$path" >&2; return 1 ;; esac
        printf '%s\n' "${path#"$root"/}" || return 1
    done
    return 0
}

# Relative links in spec files (or the files given): file, line, target, resolved from the
# repository root without its #anchor. A target in <...> runs to ">", otherwise to whitespace or
# an unbalanced ")", so a title is dropped; %20 is a space; a leading / starts at the repository
# root. Inline code, fenced blocks and comments are skipped; reference definitions (`[x]: path`)
# count when the destination looks like a path, footnotes never. URLs (`//host` too) and targets
# leaving the repository are not printed.
spec_links() { # root [file...]
    local root="$1" files=() file listed; shift
    if [ "$#" -gt 0 ]; then files=("$@")
    else
        listed="$(verify_lines spec_files "$root")" || return 1
        while IFS= read -r file; do [ -z "$file" ] || files+=("$file"); done <<< "$listed"
    fi
    [ "${#files[@]}" -gt 0 ] || return 0
    (cd "$root" && awk "$SPEC_SKIP_AWK"'
        function norm(p,   n, a, i, k, st, out) {
            n = split(p, a, "/"); k = 0
            for (i = 1; i <= n; i++) {
                if (a[i] == "" || a[i] == ".") continue
                if (a[i] == "..") { if (k == 0) return ""; k--; continue }
                st[++k] = a[i]
            }
            out = st[1]; for (i = 2; i <= k; i++) out = out "/" st[i]
            return out
        }
        function target(s,   t, c, i, d, part) {
            sub(/^[[:space:]]+/, "", s)
            if (substr(s, 1, 1) == "<") { t = substr(s, 2); sub(/>.*/, "", t); return t }
            # Cut at the first space by a regex over the whole string, not over one character
            # (see SPEC_ID_AWK); split(), unlike match(), leaves the caller its RSTART.
            if (split(s, part, /[[:space:]]/) > 1) s = part[1]
            t = ""; d = 0
            for (i = 1; i <= length(s); i++) {
                c = substr(s, i, 1)
                if (c == "(") d++
                else if (c == ")") { if (d == 0) break; d-- }
                t = t c  # awk-bytes-ok: t is s up to an unmatched ")", so it ends on a whole character
            }
            return t
        }
        function emit(t,   r, base) {
            if (t ~ /^[A-Za-z][A-Za-z+.-]*:/ || t ~ /^#/ || t ~ /^\/\//) return
            sub(/[#?].*/, "", t); gsub(/%20/, " ", t); if (t == "") return
            base = (substr(t, 1, 1) == "/") ? "" : dir
            r = norm(base t); if (r != "") print FILENAME "\037" FNR "\037" r
        }
        FNR == 1 { fence = 0; comment = 0; dir = FILENAME; sub(/[^\/]*$/, "", dir) }
        { if (spec_skip($0)) next }
        {
            line = $0; gsub(/`[^`]*`/, "", line)
            if (match(line, /^[[:space:]]*\[[^]]+\]:[[:space:]]*/)) {
                # A footnote or a label over prose is no link: a destination looks like a path.
                t = target(substr(line, RSTART + RLENGTH))
                if (line !~ /^[[:space:]]*\[\^/ && (t ~ /[\/.]/ || substr(line, RSTART + RLENGTH, 1) == "<")) emit(t)
            }
            else while ((at = index(line, "](")) > 0) { line = substr(line, at + 2); emit(target(line)) }
        }
    ' "${files[@]}")
}

# Defined items: id, title, maturity, planned change, retired (yes/no), unenforced (yes/no),
# file:line. Parts come from the architecture (or SPEC.md) parts table, its Maturity column found
# by the header; everything else from bold bullets (`- **R-01 Title** ...`), a journey file
# heading (`# J-01 Title (hypothesis)`) or a decision heading (`# D-01 Title`, or `## D-01 Title`
# as a section of architecture/decisions.md). An `Enforced by:` label anywhere in an item's block,
# sub-bullets included, decides by its own value, which runs to the end of its bullet; without
# one, a live Q or I is unenforced, and any item that says `unenforced` is.
spec_items() { # root [--skip-invalid-journey-ids] [file...]: the spec files when none are given
    local root="$1" files=() file listed expected strict_journeys=1; shift
    # Doctor reports malformed IDs in a separate pass. Never emit them as items.
    if [ "${1:-}" = --skip-invalid-journey-ids ]; then strict_journeys=0; shift; fi
    if [ "$#" -gt 0 ]; then files=("$@")
    else
        listed="$(verify_lines spec_files "$root")" || return 1
        while IFS= read -r file; do [ -z "$file" ] || files+=("$file"); done <<< "$listed"
    fi
    for file in ${files[@]+"${files[@]}"}; do
        verify_lf_file "$root/$file" || { printf 'Invalid spec text: %s (expected non-empty LF-terminated text)\n' "$file" >&2; return 1; }
        expected="$(wc -l < "$root/$file")" || return 1
        awk -v expected="$expected" -v file="$(spec_field "$file")" -v strict_journeys="$strict_journeys" "$SPEC_SKIP_AWK $SPEC_BULLET_ID_AWK $SPEC_PART_AWK"'
            BEGIN { gsub(/[\037\r\n]/, " ", file) }
            function indent(s) { sub(/[^[:space:]].*/, "", s); return length(s) }
            function flags(line, id, block,   m, p, t, b, v, i, u) {
                m = ""
                if (match(line, /\((hypothesis|validated)[,)]/)) m = substr(line, RSTART + 1, RLENGTH - 2)
                p = ""
                if (match(line, /planned:[[:space:]]*[^ .;,)|]+/)) { p = substr(line, RSTART, RLENGTH); sub(/planned:[[:space:]]*/, "", p) }
                t = tolower(line); b = tolower(block)
                if (match(b, /enforced[[:space:]]+by[[:space:]]*:?/)) {
                    v = substr(b, RSTART + RLENGTH); sub(/^[[:space:]]+/, "", v)
                    if ((i = index(v, "\n")) > 0) v = substr(v, 1, i - 1)
                    u = (v ~ /unenforced/)
                } else u = (t ~ /unenforced/ || (id ~ /^(Q|P[0-9]+(\.[0-9]+)?\.I)-/ && t !~ /retired:/))
                return m "\037" p "\037" (t ~ /retired:/ ? "yes" : "no") "\037" (u ? "yes" : "no")
            }
            # A bullet item runs on to the next bullet, heading, table row or blank line; its deeper
            # sub-bullets (not items themselves; each starts a line of subs) count only for enforcement.
            function flush() { if (pend != "") print pend_id "\037" pend_title "\037" flags(pend, pend_id, pend " " subs) "\037" file ":" pend_nr; pend = ""; subs = ""; insub = 0 }
            { gsub(/\037/, " "); if (spec_skip($0)) { flush(); next } }
            pend != "" && $0 !~ /^[[:space:]]*$/ && $0 !~ /^[[:space:]]*([-*+]|[0-9]+\.)[[:space:]]/ && $0 !~ /^(#|\|)/ { if (insub) subs = subs " " $0; else pend = pend " " $0; next }
            pend != "" && /^[[:space:]]*([-*+]|[0-9]+\.)[[:space:]]/ && !spec_bullet_id($0) && indent($0) > pend_ind { insub = 1; subs = subs "\n" $0; next }
            { flush() }
            /^#/ { retired_list = ($0 ~ /^#+[[:space:]]+Retired[[:space:]]*$/) }
            spec_part_header($0) {
                matcol = 4; n = split($0, cell, "|")
                for (i = 2; i <= n; i++) { h = cell[i]; gsub(/^[[:space:]]+|[[:space:]]+$/, "", h); if (tolower(h) == "maturity") matcol = i }
                next
            }
            (id = spec_part_row($0)) != "" {
                n = split($0, cell, "|"); title = cell[2]
                gsub(/^[[:space:]]+|[[:space:]]+$/, "", title); sub(/^[^[:space:]]+[[:space:]]+/, "", title)
                # Maturity and planned come from the Maturity cell (`hypothesis, planned: 012-x`), so
                # "planned:" elsewhere in the row does not mark the part.
                mc = cell[matcol ? matcol : 4]; mat = mc; gsub(/^[[:space:]]+|[[:space:]]+$/, "", mat); sub(/[,;( ].*/, "", mat)
                if (mat != "hypothesis" && mat != "validated") mat = ""
                f = flags(mc); split(f, mfl, "\037"); f = flags($0); split(f, fl, "\037")
                print id "\037" title "\037" mat "\037" mfl[2] "\037" fl[3] "\037" fl[4] "\037" file ":" NR
                next
            }
            # A part on the Retired list: `- P3 Store: retired: <where>`, only under `## Retired`.
            retired_list && (id = spec_retired_part($0)) != "" {
                title = $0; sub(/^[[:space:]]*[-*][[:space:]]+(\*\*)?[^[:space:]]+[[:space:]]+/, "", title)
                sub(/[*:].*/, "", title)
                print id "\037" title "\037\037\037yes\037no\037" file ":" NR; next
            }
            /^##?#?[[:space:]]+D-[0-9][0-9]+[[:space:]]/ {
                id = $2; title = $0; sub(/^#+[[:space:]]+D-[0-9]+[[:space:]]+/, "", title); sub(/[[:space:]]*\((hypothesis|validated|retired|planned)[^)]*\)[[:space:]]*$/, "", title)
                f = flags($0); split(f, fl, "\037")
                print id "\037" title "\037\037\037" fl[3] "\037no\037" file ":" NR; next
            }
            /^#[[:space:]]+J-[0-9][0-9]+[[:space:]]/ {
                id = $2; title = $0; sub(/^#[[:space:]]+J-[0-9]+[[:space:]]+/, "", title); sub(/[[:space:]]*\((hypothesis|validated|retired|planned)[^)]*\)[[:space:]]*$/, "", title)
                print id "\037" title "\037" flags($0) "\037" file ":" NR; next
            }
            (id = spec_bullet_id($0)) != "" {
                body = $0; sub(/^[[:space:]]*[-*][[:space:]]+\*\*/, "", body)
                if (id ~ /^J-[0-9]$/) {
                    if (!strict_journeys) next
                    print "Unsupported journey ID at " file ":" NR ": use a journey ID with two or more digits, e.g. J-01." > "/dev/stderr"
                    exit 1
                }
                title = body; sub(/^[^[:space:]*]+[[:space:]]*/, "", title); sub(/\*\*.*/, "", title)
                pend = $0; pend_id = id; pend_title = title; pend_nr = NR; pend_ind = indent($0)
            }
            END {
                if (NR != expected) { print "Incomplete spec " file ": expected " expected + 0 " lines, checked " NR; exit 1 }
                flush()
            }
        ' "$root/$file" || { printf 'Spec item reader failed: %s\n' "$file" >&2; return 1; }
    done
}

# The items of spec files, read as spec_items does: an item runs on to the next bullet, heading,
# table row or blank line (a paragraph too), so a marker that wraps is read whole. Prints
# file:line where the item starts, then its lines joined by \036. Comments and fences are skipped.
_spec_paragraphs() { # root
    local root="$1" files=() file listed
    listed="$(verify_lines spec_files "$root")" || return 1
    while IFS= read -r file; do [ -z "$file" ] || files+=("$file"); done <<< "$listed"
    [ "${#files[@]}" -gt 0 ] || return 0
    (cd "$root" && awk "$SPEC_SKIP_AWK"'
        function flush() { if (item != "") print where "\037" item; item = "" }
        FNR == 1 { flush(); fence = 0; comment = 0; file = FILENAME; gsub(/[\037\r\n]/, " ", file) }
        { gsub(/[\036\037]/, " "); if (spec_skip($0)) { flush(); next } }
        /^[[:space:]]*$/ { flush(); next }
        /^[[:space:]]*([-*+]|[0-9]+\.)[[:space:]]/ || /^(#|\|)/ { flush() }
        { line = $0; sub(/^[[:space:]]+/, "", line); if (item == "") where = file ":" FNR; item = (item == "" ? line : item "\036" line) }
        END { flush() }
    ' "${files[@]}")
}

# Change IDs that `planned: <change>` markers name (spec.md, Planned) in spec files, outside
# comments and fenced blocks, even a marker that wraps inside `planned:` or its code span; an
# ID without a slug ends in "-". spec_planned_at adds the file:line of the item that holds it;
# new-change.sh reserves them.
spec_planned_at() { # root
    _spec_paragraphs "$1" | awk -F '\037' '{
        t = $2; gsub(/\036/, " ", t)
        while (match(t, /planned[[:space:]]*:[[:space:]]*`?[[:space:]]*[0-9][0-9][0-9]+(-[A-Za-z0-9-]*)?/)) {
            m = substr(t, RSTART, RLENGTH); t = substr(t, RSTART + RLENGTH)
            sub(/^planned[^0-9]*/, "", m); num = m; sub(/-.*/, "", num)
            if (length(num) <= 9) print (index(m, "-") ? m : m "-") "\037" $1
        }
    }'
}
spec_planned_changes() { # root
    spec_planned_at "$1" | cut -d "$SPEC_SEP" -f1 | sort -u
}

# Paths named after a `Code:` label (`**Code**: <paths>` in a part file, `Code: <path>` in a
# topic), up to the end of its item or the next ` · **` label: kind, path, file:line. The label
# opens a line or bullet, a ` · ` field or a sentence, never mid-phrase ("Status Code:"). Kind
# "span" is a code span; "word" is a bare word holding a "/". The caller decides what is a path.
spec_code_paths() { # root
    _spec_paragraphs "$1" | awk -F '\037' '{
        t = $2
        if (!match(t, /(^|\036|[.;] |· )([-*+] )?(\*\*|`)?Code(\*\*|`)?:/)) next
        t = substr(t, RSTART + RLENGTH); gsub(/\036/, " ", t)
        if ((i = index(t, " · **")) > 0) t = substr(t, 1, i - 1)
        bare = t; gsub(/`[^`]*`/, " ", bare)
        while (match(t, /`[^`]*`/)) { print "span\037" substr(t, RSTART + 1, RLENGTH - 2) "\037" $1; t = substr(t, RSTART + RLENGTH) }
        n = split(bare, w, /[[:space:],;()]+/)
        for (i = 1; i <= n; i++) if (index(w[i], "/")) print "word\037" w[i] "\037" $1
    }'
}

# Shared lifecycle configuration. Fields belong before the first stage heading, either in
# lifecycle.md or under ## Lifecycle in the single-file form. Never infer order from prose.
# Emits tagged rows, including errors; loader keeps errors available to doctor without
# terminating its other diagnostics. Names are plain ASCII words, digits, spaces and hyphens.
spec_lifecycle() { # root
    local file="specs/product/lifecycle.md" single=0 expected
    if [ -f "$1/specs/SPEC.md" ]; then file="specs/SPEC.md"; single=1; fi
    if [ ! -f "$1/$file" ]; then printf 'order\037%s\ncustom\0370\n' "$SPEC_STAGE_DEFAULT"; return 0; fi
    verify_lf_file "$1/$file" || return 1
    expected="$(wc -l < "$1/$file")" || return 1
    LC_ALL=C awk -v source="$file" -v single="$single" -v default_order="$SPEC_STAGE_DEFAULT" -v expected="$expected" "$SPEC_STAGE_AWK"'
        function problem(s) { print "error\037Lifecycle: " source " " s }
        function field(name,   t, mark) {
            mark = "**" name "**:"
            t = substr($0, index($0, mark) + length(mark))
            if (index(t, mark)) problem("declares " name " more than once")
            sub(/\*\*.*/, "", t); sub(/·.*/, "", t)
            return stage_trim(t)
        }
        function stage_name(t) {
            if (stage_rank(t)) return t
            # Notes have an explicit delimiter; never accept a known prefix of an unknown name.
            if (match(tolower(t), /[[:space:]]+since[[:space:]]|[[:space:]]*\(|[[:space:]]*;/)) t = substr(t, 1, RSTART - 1)
            return stage_trim(t)
        }
        BEGIN { active = !single; present = !single; stage_order = default_order }
        {
            line = $0; sub(/\r$/, "", line)
            # Ignore HTML comments and fenced examples (both fence kinds, with closing length).
            t = line; sub(/^ */, "", t)
            if (fence != "") {
                if (substr(t, 1, 1) == fence) {
                    closing = t
                    if (fence == "`") gsub(/`/, "", closing); else gsub(/~/, "", closing)
                    if (closing ~ /^[[:space:]]*$/ && length(t) - length(closing) >= fence_len) fence = ""
                }
                next
            }
            if (comment) { if (index(line, "-->")) comment = 0; next }
            if (line ~ /^[[:space:]]*<!--/) { if (!index(line, "-->")) comment = 1; next }
            if (match(t, /^```+|^~~~+/)) { fence = substr(t, 1, 1); fence_len = RLENGTH; next }
            if (line ~ /^    |^\t/) next
            if (single && tolower(stage_trim(line)) == "## lifecycle") {
                if (present++) problem("has more than one Lifecycle section")
                active = 1; next
            }
            if (line ~ /^##+[[:space:]]/) active = 0
            if (!active) next
            if (line ~ /^[[:space:]]*\*\*Stages\*\*/) {
                stage_custom = 1
                if (orders++) problem("declares Stages more than once")
                if (line !~ /^[[:space:]]*\*\*Stages\*\*:/) { problem("needs **Stages**: A > B > C"); stage_order = ""; next }
                stage_order = line; sub(/^[^:]*:[[:space:]]*/, "", stage_order)
                n = split(stage_order, a, ">"); order = ""
                if (!n) problem("has an empty Stages declaration")
                for (i = 1; i <= n; i++) {
                    name = stage_trim(a[i]); key = tolower(name)
                    if (name !~ /^[A-Za-z]([A-Za-z0-9 -]*[A-Za-z0-9])?$/) problem("has an invalid stage name: \"" name "\"")
                    if (key in seen) problem("has a duplicate stage: \"" name "\"")
                    seen[key] = 1; order = order (i > 1 ? ">" : "") name
                }
                stage_order = order
                next
            }
            if (index(line, "**Now**:")) { if (nows++) problem("declares Now more than once"); now = field("Now") }
            if (index(line, "**Next**:")) { if (nexts++) problem("declares Next more than once"); next_ = field("Next") }
            if (index(line, "**Next**") && !index(line, "**Next**:")) problem("needs **Next**: <stage>")
        }
        END {
            if (NR != expected) exit 1
            list = stage_order; gsub(/>/, ", ", list)
            now = stage_name(now); next_ = stage_name(next_)
            if (present && !stage_rank(now)) problem("needs '\''**Now**: <stage>'\'', one of: " list)
            if (nexts && !stage_rank(next_)) problem("has an unknown Next stage: \"" next_ "\"; stages: " list)
            if (stage_rank(now) && stage_rank(next_) && stage_rank(next_) <= stage_rank(now)) problem("needs Next to follow Now in the stage order")
            print "order\037" stage_order
            print "custom\037" (stage_custom ? 1 : 0)
            print "now\037" now
            print "next\037" next_
        }
    ' "$1/$file"
}

spec_lifecycle_load() { # root -> SPEC_STAGE_{ORDER,LIST,CUSTOM,NOW,NEXT,ERRORS}
    local rows key value seen=' ' expected=4 checked=0
    rows="$(verify_lines spec_lifecycle "$1")" || return
    [ -f "$1/specs/SPEC.md" ] || [ -f "$1/specs/product/lifecycle.md" ] || expected=2
    SPEC_STAGE_ORDER=""; SPEC_STAGE_CUSTOM=0; SPEC_STAGE_NOW=""; SPEC_STAGE_NEXT=""; SPEC_STAGE_ERRORS=""
    while IFS="$SPEC_SEP" read -r key value; do
        case "$key" in
            order|custom|now|next)
                case "$seen" in *" $key "*) return 1 ;; esac
                seen="$seen$key "; checked=$((checked + 1)) ;;
            error) ;;
            *) return 1 ;;
        esac
        case "$key" in
            order) SPEC_STAGE_ORDER="$value" ;;
            custom) SPEC_STAGE_CUSTOM="$value" ;;
            now) SPEC_STAGE_NOW="$value" ;;
            next) SPEC_STAGE_NEXT="$value" ;;
            error) SPEC_STAGE_ERRORS="${SPEC_STAGE_ERRORS}${SPEC_STAGE_ERRORS:+$'\n'}$value" ;;
        esac
    done <<< "$rows"
    if [ "$checked" -ne "$expected" ] || { [ -z "$SPEC_STAGE_ORDER" ] && [ -z "$SPEC_STAGE_ERRORS" ]; }; then
        printf 'Incomplete lifecycle inventory: expected %s fields, checked %s\n' "$expected" "$checked" >&2
        return 1
    fi
    SPEC_STAGE_LIST="${SPEC_STAGE_ORDER//>/, }"
}

spec_stage_now() { # root -> current, next; invalid configuration fails without a classification
    spec_lifecycle_load "$1" || return
    if [ -n "$SPEC_STAGE_ERRORS" ]; then printf '%s\n' "$SPEC_STAGE_ERRORS" >&2; return 1; fi
    printf '%s\037%s\n' "$SPEC_STAGE_NOW" "$SPEC_STAGE_NEXT"
}

# The caller has loaded configuration. Unknown from: markers may be source notes in old specs,
# but cannot support a successful current/later classification.
spec_stage_problems() { # stage rows on stdin
    awk -F "$SPEC_SEP" -v stage_order="$SPEC_STAGE_ORDER" -v stage_custom="$SPEC_STAGE_CUSTOM" \
        -v list="$SPEC_STAGE_LIST" "$SPEC_STAGE_AWK"'
        NF && !stage_rank($2) { print "Lifecycle: \"from: " $2 "\" in " $1 " at " $3 " names no stage; stages: " list }'
}

spec_lifecycle_require() { # root -> load configuration and reject unclassifiable data
    local problems
    spec_lifecycle_load "$1" || return
    problems="$(spec_stages "$1" | spec_stage_problems)" || return
    if [ -n "$SPEC_STAGE_ERRORS$problems" ]; then
        [ -z "$SPEC_STAGE_ERRORS" ] || printf '%s\n' "$SPEC_STAGE_ERRORS" >&2
        [ -z "$problems" ] || printf '%s\n' "$problems" >&2
        echo 'Lifecycle classification unavailable; correct the lifecycle fields/markers first.' >&2
        return 1
    fi
}

# Items marked for a later stage: id, stage (lowercased), file:line. The marker sits in a
# heading's or bullet's parenthetical (`(hypothesis, from: ga)`) or a part's Maturity cell.
spec_stages() { # root
    local root="$1" file listed
    listed="$(verify_lines spec_files "$root")" || return 1
    while IFS= read -r file; do
        [ -n "$file" ] || continue
        awk -v file="$(spec_field "$file")" "$SPEC_SKIP_AWK $SPEC_STAGE_AWK"'
            BEGIN { gsub(/[\037\r\n]/, " ", file) }
            function emit(id, text,   s) { s = stage_from(text); if (s != "") print id "\037" s "\037" file ":" NR }
            { gsub(/\037/, " "); if (spec_skip($0)) next }
            /^\|[[:space:]]*[Pp]art[[:space:]]*\|/ {
                matcol = 4; n = split($0, cell, "|")
                for (i = 2; i <= n; i++) { h = cell[i]; gsub(/^[[:space:]]+|[[:space:]]+$/, "", h); if (tolower(h) == "maturity") matcol = i }
                next
            }
            /^\|[[:space:]]*P[0-9]+(\.[0-9]+)?[[:space:]]/ {
                n = split($0, cell, "|"); id = cell[2]; gsub(/^[[:space:]]+/, "", id); sub(/[[:space:]].*/, "", id)
                emit(id, cell[matcol ? matcol : 4]); next
            }
            /^#+[[:space:]]+[JD]-[0-9][0-9]+[[:space:]]/ { if (match($0, /\([^)]*\)[[:space:]]*$/)) emit($2, substr($0, RSTART, RLENGTH)); next }
            /^[[:space:]]*[-*+][[:space:]]+\*\*(P[0-9]+(\.[0-9]+)?\.[BI]|[JRQCN])-[0-9][0-9]+/ {
                id = $0; sub(/^[[:space:]]*[-*+][[:space:]]+\*\*/, "", id); sub(/[[:space:]*].*/, "", id)
                rest = $0; sub(/^[^*]*\*\*[^*]*\*\*/, "", rest)
                if (match(rest, /^[[:space:]]*\([^)]*\)/)) emit(id, substr(rest, RSTART, RLENGTH))
            }
        ' "$root/$file" || return 1
    done <<< "$listed"
}

# Part file headers: file, ID (blank if none), Parent, Parent line, and any field error.
# Component files repeat the part ID but do not declare a Parent.
spec_part_headings() { # root
    local root="$1" path file expected component row part parent line issue
    local id='P[0-9]+(\.[0-9]+)?' shape
    shape="^($id)?$SPEC_SEP($id)?$SPEC_SEP([1-9][0-9]*)?${SPEC_SEP}[^[:cntrl:]]*$"
    for path in "$root"/specs/parts/*.md "$root"/specs/parts/*/*.md; do
        [ -f "$path" ] || continue
        file="${path#"$root"/}"
        case "$file" in *$'\n'*|*$'\r'*|*"$SPEC_SEP"*) return 1 ;; esac
        verify_lf_file "$path" || return 1
        expected="$(wc -l < "$path")" || return 1
        component=0; case "$file" in specs/parts/*/*) component=1 ;; esac
        # shellcheck disable=SC2016 # the single-quoted program uses awk fields
        row="$(cd "$root" && verify_lines awk -v expected="$expected" -v component="$component" "$SPEC_METADATA_SKIP_AWK $SPEC_HEADER_AWK"'
            /^#[[:space:]]+P[0-9]+(\.[0-9]+)?([[:space:]]|$)/ && !part { part = $2 }
            { if (metadata_skip($0)) next }
            !section && /^##+[[:space:]]/ { section = 1 }
            !section { metadata_header($0) }
            END {
                if (NR != expected) exit 1
                parent = header["Parent"]; error = ""
                if (fields["Parent"] > 1) error = "Duplicate part Parent field"
                else if (fields["Parent"] && (!part || component)) error = "Parent belongs in a main part file header"
                else if (parent != "" && parent !~ /^P[0-9]+(\.[0-9]+)?$/) error = "Malformed part Parent (expected a part ID)"
                print FILENAME "\037" part "\037" (error == "" ? parent : "") "\037" field_line["Parent"] "\037" error
            }' "$file" && printf x)" || return 1
        row="${row%$'\n'x}"
        if [[ "$row" != "$file$SPEC_SEP"* || ! "${row#*"$SPEC_SEP"}" =~ $shape ]]; then
            printf 'Invalid part metadata row: %s\n' "$file" >&2; return 1
        fi
        IFS="$SPEC_SEP" read -r _ part parent line issue <<< "$row"
        if { [ -n "$parent$issue" ] && [ -z "$line" ]; } ||
           { [ -n "$parent" ] && { [ -z "$part" ] || [ -n "$issue" ]; }; }; then
            printf 'Invalid part metadata row: %s\n' "$file" >&2; return 1
        fi
        printf '%s\n' "$row" || return 1
    done
    return 0
}

# Part inventory and validated edges from complete item and header rows already read by the caller.
# Emits part, ID; edge, child, parent; or error, diagnostic. IDs are explicit: dots do not infer
# ancestry. A second edge from the same child is ambiguous, even if it repeats the first one.
spec_part_graph() { # items part-headings
    { printf '%s\n' "$1" | awk 'NF { print "item\037" $0 }' || return 1
      printf '%s\n' "$2" | awk 'NF { print "header\037" $0 }'; } |
    awk -F "$SPEC_SEP" '
        function add(id) { if (!(id in known)) { known[id] = 1; order[++n] = id } }
        $1 == "item" && $2 ~ /^P[0-9]+(\.[0-9]+)?$/ { add($2); next }
        $1 == "header" {
            file = $2
            if (file ~ /^specs\/parts\/[^\/]+\.md$/ && $3 != "") add($3)
            if ($6 != "") print "error\037" file ":" $5 ": " $6
            if ($4 == "") next
            child = $3
            if (child in parent) { bad[child] = 1; print "error\037" file ":" $5 ": Multiple Parents declared for " child; next }
            parent[child] = $4; at[child] = file ":" $5
        }
        END {
            for (i = 1; i <= n; i++) {
                child = order[i]; print "part\037" child
                if (!(child in parent) || bad[child]) continue
                p = parent[child]
                if (!(p in known)) { print "error\037" at[child] ": Unknown parent " p " for " child; continue }
                for (a = child; a in parent && !bad[a] && !seen[child, a]++; a = parent[a]) {}
                if (a in parent && !bad[a]) { print "error\037" at[child] ": Part parent cycle from " child; continue }
                if (p in parent) { print "error\037" at[child] ": Part nesting exceeds one level for " child; continue }
                print "edge\037" child "\037" p
            }
        }'
}

# Cited IDs: id, file:line, for every ID mentioned in the given files (relative to root).
spec_citations() { # root file...
    local root="$1"; shift
    [ "$#" -gt 0 ] || return 0
    (cd "$root" && awk "$SPEC_SKIP_AWK $SPEC_ID_AWK"'
        FNR == 1 { fence = 0; comment = 0 }
        { if (spec_skip($0)) next; spec_ids($0, FILENAME ":" FNR) }
    ' "$@")
}

# Tracked test files (path names that look like tests), relative to root, excluding specs/.
spec_test_files() { # root
    git -C "$1" rev-parse --is-inside-work-tree >/dev/null 2>&1 || return 0
    git -C "$1" ls-files 2>/dev/null | awk '
        /^specs\// || /^\.prdkit\// || /^\.product-kit\// { next }
        {
            p = tolower($0); b = p; sub(/.*\//, "", b)
            if (p ~ /(^|\/)(tests?|__tests__|e2e)\// || b ~ /^test[_-]|_test\.|\.test\.|^tests?\.|tests?\.[a-z]+$|[._]spec\./) print
        }'
}

# Owner inbox items: line, date, who, state, words, id, kind, tags, revision, answer, problems.
# State: open|processing|folded|declined|deferred|unknown|unparsed. Processing is inferred from
# owner direction or an answer field, never completion.
# An item is a `### <title>` heading followed by its meta line `*<date> · <owner|agent> ·
# <disposition>*` (words: the title; line: the heading). A heading without a meta
# line prints state "unparsed" with its title as words; so does prose outside an item, except in
# an inbox of headed items, where it is ordinary text (such as "Nothing open.").
spec_inbox() { # root
    local file="$1/specs/inbox.md" headed=0 expected
    [ -f "$file" ] || return 0
    verify_lf_file "$file" allow-empty || { echo 'Invalid text in specs/inbox.md' >&2; return 1; }
    if grep '^###[[:space:]]' "$file" >/dev/null; then headed=1
    elif [ "$?" -ne 1 ]; then return 1; fi
    expected="$(wc -l < "$file")" || return 1
    awk -v headed="$headed" -v expected="$expected" "$SPEC_SKIP_AWK"'
        function trim(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
        function problem(s) { problems = problems (problems == "" ? "" : "; ") s }
        function flush(   state) {
            if (!item) return
            state = meta_state
            if (want) state = "unparsed"
            if (state == "open" && (who == "owner" || answer != "")) state = "processing"
            # A known stale answer needs a fresh owner clarification, rather than another pass
            # at incorporating the old selection. Preserve the original answer as evidence.
            if ((state == "open" || state == "processing") && kind != "direction" &&
                (who == "agent" || kind ~ /^(decision|clarification|feedback)$/) && answer != "" &&
                revision ~ /^[1-9][0-9]*$/ && answer_revision ~ /^[1-9][0-9]*$/ && revision != answer_revision) state = "open"
            if (id != "" && seen[id]++) problem("duplicate ID " id)
            if (state == "open" || state == "processing" || state == "unknown") {
                if (id == "") problem("missing ID")
                else if (id !~ /^[A-Za-z][A-Za-z0-9._-]*$/) problem("invalid ID; use a stable single token")
                if (revision !~ /^[1-9][0-9]*$/) problem("missing or invalid Revision")
                if (kind !~ /^(decision|clarification|feedback|direction)$/) problem("missing or invalid Kind")
                if (tags == "") problem("missing Tags")
                else if (tags !~ /^[a-z][a-z0-9-]*(, [a-z][a-z0-9-]*)*$/) problem("invalid Tags; use comma-separated lowercase topics")
                if (kind == "decision" && (options < 2 || keyed != options)) problem("decision needs at least two options with stable keys")
                if (answer != "") {
                    if (!isdate(answer_date)) problem("missing or invalid Answer date")
                    if (answer_source == "") problem("missing Answer source")
                    if (answer_revision !~ /^[1-9][0-9]*$/) problem("missing or invalid Answer revision")
                    else if (revision != "" && answer_revision != revision) problem("answer revision mismatch; clarify before incorporation")
                }
            }
            print head "\037" date "\037" who "\037" state "\037" title "\037" id "\037" kind "\037" tags "\037" revision "\037" answer "\037" problems
            item = 0; want = 0
        }
        function start() {
            date = ""; who = ""; meta_state = "unparsed"; id = ""; kind = ""; tags = ""; revision = ""
            answer = ""; answer_date = ""; answer_source = ""; answer_revision = ""; problems = ""
            options = 0; keyed = 0; answering = 0; split("", keys); split("", fields)
        }
        function add_answer(s) { s = trim(s); sub(/^>[[:space:]]?/, "", s); if (s != "") answer = answer (answer == "" ? "" : " ") s }
        function isdate(s) { return s ~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9](T[0-9][0-9]:[0-9][0-9](:[0-9][0-9])?Z?)?$/ }
        { gsub(/[\037\t\r]/, " "); if (spec_skip($0)) next }
        /^###[[:space:]]/ {
            flush(); start(); head = NR; title = $0; sub(/^###[[:space:]]+/, "", title); sub(/[[:space:]]+$/, "", title)
            gsub(/\t/, " ", title); want = 1; item = 1; next
        }
        /^#[[:space:]]/ || /^##[[:space:]]/ { flush(); next }
        /^[[:space:]]*$/ { next }
        {
            line = $0; sub(/^[[:space:]]*[-*][[:space:]]+/, "", line)
            # A quoted item can appear in the body. At the metadata position, also
            # reject an unquoted item with an explicit old disposition. A date and
            # actor alone can be plain prose in a structured Answer.
            if (line ~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9](T[0-9][0-9]:[0-9][0-9](:[0-9][0-9])?Z?)?[[:space:]]+(owner|agent)[[:space:]]+/ &&
                (!item || line ~ /^[^[:space:]]+[[:space:]]+(owner|agent)[[:space:]]+"/ ||
                 (want && line ~ /(→|->)[[:space:]]*(open|processing|folded|declined|deferred)([:[:space:]]|$)/))) {
                unsupported = NR; exit 1
            }
            if ($0 ~ /^(\*\*Owner answer\*\*:|Owner answer[,:])/) { unsupported = NR; exit 1 }
        }
        want {
            want = 0; meta = $0
            sub(/^[[:space:]]*[*_]+/, "", meta); sub(/[*_]+[[:space:]]*$/, "", meta)
            n = split(meta, f, /[[:space:]]*(·|\|)[[:space:]]*/)
            if (n >= 2 && isdate(f[1]) && (f[2] == "owner" || f[2] == "agent")) {
                meta_state = "unknown"; date = substr(f[1], 1, 10); who = f[2]
                if (n >= 3) { st = f[3]; sub(/[:[:space:]].*/, "", st); if (st ~ /^(open|processing|folded|declined|deferred)$/) meta_state = st }
            }
            next
        }
        /^#/ { next }
        {
            if (item) {
                if (match($0, /^\*\*[^*]+\*\*:/)) {
                    label = substr($0, 3, RLENGTH - 5); value = trim(substr($0, RLENGTH + 1)); answering = 0
                    if (label ~ /^(ID|Revision|Kind|Tags|Answer|Answer date|Answer source|Answer revision)$/ && fields[label]++) problem("duplicate field " label)
                    if (label == "ID") id = value
                    else if (label == "Revision") revision = value
                    else if (label == "Kind") kind = value
                    else if (label == "Tags") { tags = value; gsub(/[[:space:]]*,[[:space:]]*/, ", ", tags) }
                    else if (label == "Answer") { add_answer(value); answering = 1 }
                    else if (label == "Answer date") answer_date = value
                    else if (label == "Answer source") answer_source = value
                    else if (label == "Answer revision") answer_revision = value
                } else if (answering) add_answer($0)
                if ($0 ~ /^[0-9]+\.[[:space:]]+\*\*/) {
                    options++; key = $0; sub(/^[0-9]+\.[[:space:]]+\*\*/, "", key)
                    if (key ~ /^[a-z][a-z0-9-]* — /) {
                        sub(/ .*/, "", key); keyed++
                        if (keys[key]++) problem("duplicate option key " key)
                    }
                }
                next
            }
            flush()
            if (headed && line !~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]/) next
            gsub(/\t/, " ", line); print NR "\037\037\037unparsed\037" line "\037\037\037\037\037\037"
        }
        END {
            if (unsupported) print "Unsupported inbox entry at specs/inbox.md:" unsupported > "/dev/stderr"
            if (NR != expected || unsupported) exit 1
            flush()
        }' "$file" || {
            echo 'Invalid specs/inbox.md: expected headed items with *<date> · <owner|agent> · <disposition>* and structured **Answer** fields (inbox-template.md).' >&2
            return 1
        }
}

# Print the checked reference for one authority entry, or a diagnostic on stderr. Only ATX
# headings outside comments and fences delimit entries; the bytes inside them are left alone.
spec_authority_ref() { # root anchor
    local file="$1/specs/authority.md" anchor="$2" entry digest="" target
    target="specs/authority.md#$anchor"
    if ! [[ "$anchor" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
        echo 'invalid authority anchor (expected a single ASCII ID)' >&2
        return 1
    fi
    if [ -L "$1/specs" ] || [ -L "$file" ] || { [ -e "$file" ] && [ ! -f "$file" ]; }; then
        echo 'specs/authority.md must be a regular file under specs/ (no symlinks)' >&2
        return 1
    fi
    [ -f "$file" ] || { echo 'missing authority file specs/authority.md' >&2; return 1; }
    review_file_without_nul "$file" 2>/dev/null ||
        { echo 'cannot read specs/authority.md as LF text' >&2; return 1; }
    if entry="$(LC_ALL=C awk -v anchor="$anchor" "$SPEC_METADATA_SKIP_AWK"'
        index($0, "\r") { problem = 12; exit }
        !metadata_skip($0) {
            h = $0; sub(/^ ? ? ?/, "", h)
            if (match(h, /^#+/) && RLENGTH <= 6 && substr(h, RLENGTH + 1) ~ /^([ \t]|$)/) {
                depth = RLENGTH
                if (level && depth <= level) level = 0
                h = substr(h, depth + 1)
                sub(/[ \t]+#+[ \t]*$/, "", h)
                gsub(/^[ \t]+|[ \t]+$/, "", h)
                if ("x" h == "x" anchor) {
                    if (++found > 1) { problem = 11; exit }
                    level = depth
                }
            }
        }
        level { print }
        END { if (problem) exit problem; if (!found) exit 10 }
    ' "$file" 2>/dev/null)"; then
        # awk prints each line; substitution strips all trailing LF bytes. Put exactly one back.
        if command -v sha256sum >/dev/null 2>&1; then
            digest="$(printf '%s\n' "$entry" | sha256sum 2>/dev/null)" || digest=""
        elif command -v shasum >/dev/null 2>&1; then
            digest="$(printf '%s\n' "$entry" | shasum -a 256 2>/dev/null)" || digest=""
        fi
        digest="${digest%%[[:space:]]*}"
        if [[ "$digest" =~ ^[0-9a-f]{64}$ ]]; then
            printf 'ref=%s@%s\n' "$target" "${digest:0:12}"
            return
        fi
        echo 'could not compute SHA-256 (requires sha256sum or shasum)' >&2
    else
        case "$?" in
            10) printf 'missing authority anchor %s\n' "$target" >&2 ;;
            11) printf 'duplicate authority anchor %s\n' "$target" >&2 ;;
            *) echo 'cannot read specs/authority.md as LF text' >&2 ;;
        esac
    fi
    return 1
}

# Records: line, type, name, owner, scope, env, rev, date, until, check, reason. A line that
# is not a valid record prints type INVALID and the problem as its reason.
spec_records() { # root
    local root="$1" file="$1/specs/records.md" expected rows row line type reason checked error
    local ref_format='^ref=specs/authority\.md#([A-Za-z0-9][A-Za-z0-9._-]*)@[0-9a-f]{12}$'
    [ -f "$file" ] || return 0
    verify_lf_file "$file" allow-empty || { echo 'Invalid text in specs/records.md' >&2; return 1; }
    expected="$(wc -l < "$file")" || return 1
    rows="$(awk -v expected="$expected" "$SPEC_SKIP_AWK $SPEC_RECORD_ROW_AWK"'
        function bad(why) { print NR "\037INVALID\037\037\037\037\037\037\037\037\037" why; }
        { gsub(/\037/, " "); if (spec_skip($0)) next }
        /^[[:space:]]*$/ || /^#/ { next }
        {
            error = spec_record_row($0, row)
            if (error != "") { bad(error); next }
            print NR "\037" row["type"] "\037" row["name"] "\037" row["owner"] "\037" row["scope"] "\037" row["env"] \
                "\037" row["rev"] "\037" row["date"] "\037" row["until"] "\037" row["check"] "\037" row["reason"]
        }
        END { if (NR != expected) exit 1 }' "$file")" || return 1
    while IFS= read -r row; do
        [ -n "$row" ] || continue
        IFS="$SPEC_SEP" read -r line type _ _ _ _ _ _ _ _ reason <<< "$row"
        error=""
        case "$type:$reason" in
            HOLD:ref=*|WAIVER:ref=*)
                if ! [[ "$reason" =~ $ref_format ]]; then
                    error='invalid authority reference (expected ref=specs/authority.md#<anchor>@<12 lowercase hex digits>)'
                elif ! checked="$(spec_authority_ref "$root" "${BASH_REMATCH[1]}" 2>&1)"; then
                    error="${checked:-authority reference could not be verified}"
                elif [ "$checked" != "$reason" ]; then
                    error="authority digest mismatch for ${reason#ref=}"
                fi ;;
        esac
        if [ -n "$error" ]; then
            printf '%s\037INVALID\037\037\037\037\037\037\037\037\037%s\n' "$line" "$error" || return 1
        else
            printf '%s\n' "$row" || return 1
        fi
    done <<< "$rows"
}

# Open changes: id, title, opened, step, waiting on, next, parent, acceptance total, passed,
# failed or blocked, highest attempts, stalled items (not passed, and attempts >= 3 or BLOCKED;
# comma-separated), Waiting on and Next field status.
spec_changes() { # root
    local root="$1" path expected
    for path in "$root"/specs/changes/*.md; do
        [ -f "$path" ] || continue
        verify_lf_file "$path" || { printf 'Invalid change record text: %s\n' "$path" >&2; return 1; }
        expected="$(wc -l < "$path")" || return 1
        awk -v expected="$expected" -v id="$(spec_field "$(basename "$path" .md)")" "$SPEC_CHANGE_TAG_AWK"'
            BEGIN { gsub(/[\037\r\n]/, " ", id) }
            { $0 = change_without_tags($0) }
            function trim(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
            function field(line, name,   v) {
                v = line; sub("^.*\\*\\*" name "\\*\\*:[[:space:]]*", "", v); sub(/[[:space:]]+·.*/, "", v); return trim(v)
            }
            function presence(n,v) { return !n ? "missing" : n>1 ? "duplicate" : v=="" ? "blank" : "present" }
            {
                gsub(/\037/, " "); sub(/\r$/, "")
                # Ignore complete fenced examples, including inner shorter fences.
                marker=$0; sub(/^ ? ? ?/,"",marker)
                if (code) {
                    ending=marker; sub(/[[:space:]]+$/, "", ending)
                    if (length(ending)>=length(code) && (ending ~ /^`+$/ || ending ~ /^~+$/) && substr(ending,1,1)==substr(code,1,1)) code=""
                    next
                }
                if (comment || /^[[:space:]]*<!--/) { comment=!index($0,"-->"); next }
                if (match(marker,/^```+|^~~~+/)) { code=substr(marker,1,RLENGTH); next }
            }
            FNR == 1 && /^# / { title = $0; sub(/^#[[:space:]]+(Change:[[:space:]]*)?/, "", title) }
            /\*\*Opened\*\*:/ { opened = field($0, "Opened") }
            /\*\*Parent\*\*:/ { parent = field($0, "Parent") }
            /^\*\*Step\*\*:/ { step = field($0, "Step") }
            sec == "State" && /^\*\*Waiting on\*\*:/ { nw++; waiting = field($0, "Waiting on") }
            sec == "State" && /^\*\*Next\*\*:/ { nn++; nxt = field($0, "Next") }
            /^# / { sec = ""; next }
            /^## / { sec = $0; sub(/^##[[:space:]]+/, "", sec); sec = trim(sec); next }
            sec == "Acceptance" && /^\|/ {
                n = split($0, c, "|"); item = trim(c[2])
                if (item == "" || item == "Item" || item ~ /^-+$/) next
                att = trim(c[5]) + 0; res = toupper(trim(c[6]))
                total++; if (res == "PASS" || res == "NOT_APPLICABLE") passed++
                if (res == "FAIL" || res == "BLOCKED") failed++
                if (att > maxatt) maxatt = att
                if (res != "PASS" && res != "NOT_APPLICABLE" && (att >= 3 || res == "BLOCKED"))
                    stalled = stalled (stalled == "" ? "" : ",") item
            }
            END {
                if (NR != expected) exit 1
                gsub(/\t/, " ", title); gsub(/\t/, " ", waiting); gsub(/\t/, " ", nxt)
                print id "\037" title "\037" opened "\037" step "\037" (nw==1 ? waiting : "") "\037" (nn==1 ? nxt : "") "\037" parent "\037" total + 0 "\037" passed + 0 "\037" failed + 0 "\037" maxatt + 0 "\037" stalled "\037" presence(nw,waiting) "\037" presence(nn,nxt)
            }' "$path" || { printf 'Change record reader failed: %s (expected %s lines)\n' "$path" "$expected" >&2; return 1; }
    done
    return 0
}

# Optional change metadata. Only doctor receives duplicate-field diagnostics as rows.
# Header-only mode emits no task rows (for bounds and close). Create checks only Tags.
# task, part (empty for unassigned), checked (0/1), source line, issue (malformed/outside);
# tags, normalized value, source line, issue. No semantic dependency on tags.
spec_change_metadata() { # file [tags|status|create]
    local file="$1" expected rows mode="${2:-}" kind label line
    verify_lf_file "$file" || return 1
    expected="$(wc -l < "$file")" || return 1
    # shellcheck disable=SC2016 # the single-quoted program uses awk fields
    rows="$(verify_lines awk -v expected="$expected" -v mode="$mode" "$SPEC_METADATA_SKIP_AWK $SPEC_HEADER_AWK"'
        function tag_list(value,   a, i, n, tag, out) {
            n = split(value, a, ",")
            for (i = 1; i <= n; i++) {
                tag = metadata_trim(a[i])
                if (tag !~ /^[abcdefghijklmnopqrstuvwxyz0123456789 _.-]+$/) return ""
                out = out (i > 1 ? ", " : "") tag
            }
            return out
        }
        { if (metadata_skip($0)) next }
        /^##+[[:space:]]/ {
            if (!section) {
                section = 1
                if (fields["Parts"] == 1) {
                    n = split(header["Parts"], ids, /[[:space:],]+/)
                    for (i = 1; i <= n; i++) scoped[ids[i]] = 1
                }
            }
            if ($0 ~ /^##[[:space:]]/) tasks = (metadata_trim(substr($0, 3)) == "Tasks")
            next
        }
        !section { metadata_header($0); next }
        (mode == "" || mode == "status") && tasks && /^[[:space:]]*[-*+][[:blank:]]+\[[ xX]\][[:blank:]]+[^[:blank:]]/ {
            s = $0
            sub(/^[[:space:]]*[-*+][[:blank:]]+\[/, "", s)
            checked = (substr(s, 1, 1) != " ")
            s = substr(s, 3); sub(/^[[:blank:]]+/, "", s)
            part = ""; issue = ""
            # Only an ID-like prefix is reserved. A task starting "(optional)" is ordinary prose.
            if (s ~ /^\(P([0-9]|[[:punct:][:space:]]|$)/ || s ~ /^\(p[0-9]/) {
                if (match(s, /^\(P[0-9]+(\.[0-9]+)?\)[[:blank:]]+/)) {
                    part = substr(s, 2); sub(/\).*/, "", part)
                    if (!(part in scoped)) issue = "outside"
                } else {
                    part = substr(s, 2); sub(/[[:space:])].*/, "", part); gsub(/[[:cntrl:]]/, " ", part)
                    issue = "malformed"
                }
            }
            print "task\037" part "\037" (checked ? 1 : 0) "\037" NR "\037" issue
        }
        END {
            if (NR != expected) exit 1
            n = split(mode == "create" ? "Tags" : "Parts Journeys Tags", labels, " ")
            for (i = 1; i <= n; i++) {
                label = labels[i]
                if (fields[label] > 1) print "duplicate\037" label "\037" field_line[label]
            }
            value = header["Tags"]; tags = tag_list(value)
            issue = value != "" && tags == "" ? "Malformed Tags (use comma-separated lowercase letters, digits, spaces, hyphens, underscores or periods)" : ""
            print "tags\037" (issue == "" ? tags : "") "\037" field_line["Tags"] "\037" issue
        }' "$file")" || return 1
    if [ -n "$mode" ]; then
        while IFS="$SPEC_SEP" read -r kind label line; do
            if [ "$kind" = duplicate ]; then
                printf '%s:%s: Duplicate %s field\n' "$file" "$line" "$label" >&2; return 1
            fi
        done <<< "$rows"
    fi
    case "${rows##*$'\n'}" in "tags$SPEC_SEP"*"$SPEC_SEP"*"$SPEC_SEP"*) ;; *) return 1 ;; esac
    printf '%s\n' "$rows"
}

spec_change_progress() { # file [required=true] [advisories] -> outcome, milestone, critical path, allocation, progress,
    # selection, no-progress count (number|null), limit (number|null), count signal.
    # Project recorded State only; no inference from acceptance, PR totals or dates in prose.
    local file="$1" expected=0
    if [ "$file" != /dev/null ]; then
        [ -f "$file" ] && verify_lf_file "$file" || return 1
        expected="$(wc -l < "$file")" || return 1
    fi
    awk -v expected="$expected" -v required="${2:-false}" -v report="${3:-}" "$SPEC_SKIP_AWK"'
        function trim(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
        function uint(s) { return s ~ /^(0|[1-9][0-9]*)$/ }
        # Compare decimal strings without machine-integer overflow or floating-point rounding.
        function reached(a, b) { return length(a) > length(b) || (length(a) == length(b) && "x" a >= "x" b) }
        { if (spec_skip($0)) next }
        /^## / { sec = $0; block = 0; next }
        sec == "## State" && /^### / {
            block = ($0 == "### Outcome checkpoint")
            if (block) blocks++
            next
        }
        sec == "## State" && /^\*\*[^*]+\*\*:/ {
            key = $0; sub(/^\*\*/, "", key); sub(/\*\*:.*/, "", key)
            if (key !~ /^(Outcome|Milestone|Critical path|Allocation|Progress|Selection|No-progress changes|Progress limit)$/) next
            value = $0; sub(/^\*\*[^*]+\*\*:[[:space:]]*/, "", value)
            gsub(/\037/, " ", value)
            data[block, key] = trim(value)
            counts[block, key]++
            if (!block) outside = 1
        }
        END {
            if (NR != expected) exit 1
            unscoped = counts[0, "No-progress changes"] || counts[0, "Progress limit"] ||
                (counts[0, "Outcome"] && counts[0, "Milestone"] &&
                 (counts[0, "Critical path"] || counts[0, "Allocation"] || counts[0, "Progress"] || counts[0, "Selection"]))
            if (report == "advisories") {
                if (outside) print "Checkpoint keys outside ### Outcome checkpoint in State; " \
                    (blocks ? "unscoped keys are ignored" : "ordinary State text is not a programme checkpoint")
                exit
            }
            if (!blocks && unscoped) exit 2
            if (!blocks && required != "true") exit
            n = split("Outcome|Milestone|Critical path|Allocation|Progress|Selection|No-progress changes|Progress limit", keys, "|")
            for (i = 1; i <= n; i++) {
                key = keys[i]; seen[key] = counts[1, key]
                # A referenced parent without a checkpoint remains unknown, not generic prose.
                values[key] = data[1, key]
                if (seen[key] > 1) invalid = 1
            }
            if (blocks > 1) invalid = 1
            count = values["No-progress changes"]; limit = seen["Progress limit"] ? values["Progress limit"] : "3"
            if (seen["No-progress changes"] && count != "unknown" && !uint(count)) invalid = 1
            if (!uint(limit) || limit == "0") invalid = 1
            signal = "unknown"
            if (invalid) signal = "invalid"
            else if (uint(count)) signal = reached(count, limit) ? "reassess" : "below-limit"
            if (!uint(count) || seen["No-progress changes"] > 1) count = "null"
            if (!uint(limit) || limit == "0" || seen["Progress limit"] > 1) limit = "null"
            print values["Outcome"] "\037" values["Milestone"] "\037" values["Critical path"] "\037" values["Allocation"] "\037" values["Progress"] "\037" values["Selection"] "\037" count "\037" limit "\037" signal
        }' "$file" || {
            printf '%s: expected programme checkpoint fields under ### Outcome checkpoint in ## State\n' "$file" >&2
            return 1
        }
}

# Checkouts that can hold open change records, as path, branch and location (relative to root,
# "." for root itself): root first, then every other registered worktree whose directory exists
# (new-change.sh creates the record in one). Local git metadata only.
spec_worktrees() { # root -> Git porcelain, checked before exposing any rows
    local input
    input="$(verify_lines git -C "$1" worktree list --porcelain && printf x)" || return 1
    case "$input" in worktree\ *$'\n\nx') ;; *) echo 'Incomplete Git worktree inventory' >&2; return 1 ;; esac
    printf '%s' "${input%x}" | LC_ALL=C awk '
        /^worktree / { if (open || length($0) == 9 || substr($0,10,1) == "\"") bad=1; open=1; expected++; next }
        /^HEAD [0-9a-f]+$/ { if (!open || head || (length($2) != 40 && length($2) != 64)) bad=1; head=1; next }
        /^bare$/ { if (!open || head) bad=1; head=kind=1; next }
        /^branch refs\/heads\/.+|^detached$/ { if (!head || kind) bad=1; kind=1; next }
        /^$/ { if (!open || !head || !kind) bad=1; open=head=kind=0; checked++; next }
        !open || !/^(locked( |$)|prunable( |$))/ { bad=1 }
        END {
            if (bad || open || !expected || checked != expected) exit 1
        }' || { echo 'Invalid or incomplete Git worktree records' >&2; return 1; }
    printf '%s' "${input%x}"
}

spec_checkouts() { # root
    local root="$1" top line path="" branch="" real rows
    case "$root" in *"$SPEC_SEP"*|*$'\n'*|*$'\r'*) printf 'Cannot read status rows from a checkout path containing a record separator\n' >&2; return 1 ;; esac
    if ! git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        printf '%s\037\037.\n' "$root"
        return
    fi
    branch="$(git -C "$root" symbolic-ref --quiet --short HEAD 2>/dev/null)" || { [ "$?" -eq 1 ] || return 1; }
    top="$(CDPATH="" cd "$root" && pwd -P)" || return 1
    rows="$(spec_worktrees "$root")" || return 1
    printf '%s\037%s\037.\n' "$root" "$branch"
    while IFS= read -r line; do
        case "$line" in
            "worktree "*) path="${line#worktree }"; branch="" ;;
            "branch refs/heads/"*) branch="${line#branch refs/heads/}" ;;
            "")
                real=""
                [ -z "$path" ] || [ ! -d "$path" ] || real="$(CDPATH="" cd "$path" && pwd -P)" || return 1
                if [ -n "$real" ] && [ "$real" != "$top" ]; then
                    case "$real" in *"$SPEC_SEP"*|*$'\n'*|*$'\r'*) printf 'Cannot read status rows from a checkout path containing a record separator\n' >&2; return 1 ;; esac
                    printf '%s\037%s\037%s\n' "$real" "$branch" "${real#"$top"/}"
                fi
                path="" ;;
        esac
    done <<< "$rows"$'\n'
    return 0
}

# Open changes across checkouts: the spec_changes columns, then the record's location. An id
# found in several checkouts is listed once: the copy on the branch named after the change wins,
# else the first found.
spec_open_changes() { # root, validated archive log index
    local root="$1" path branch loc row closed checkouts changes
    # A worktree branched before a close may still carry the record: the current log and
    # archive have the final word. Doctor checks for a conflicting record in the current checkout.
    closed=" $(spec_log_entries "$root" | sed -n 's/^- [0-9-]* \([^ :]*\):.*/\1/p' | tr '\n' ' ')" || return 1
    checkouts="$(verify_lines spec_checkouts "$root")" || return 1
    [ -n "$checkouts" ] || return 1
    while IFS="$SPEC_SEP" read -r path branch loc; do
        [ -n "$path" ] || continue
        changes="$(verify_lines spec_changes "$path")" || return 1
        while IFS= read -r row; do
            [ -n "$row" ] || continue
            if spec_archive_log_covers "$2" "${row%%"$SPEC_SEP"*}"; then continue; fi
            printf '%s\037%s\037%s\n' "$row" "$loc" "$([ "${row%%"$SPEC_SEP"*}" = "$branch" ] && echo 1 || echo 0)"
        done <<< "$changes"
    done <<< "$checkouts" | awk -F '\037' -v closed="$closed" 'NF {
            own = $NF; r = $0; sub("\037[01]$", "", r)
            if ($(NF - 1) != "." && index(closed, " " $1 " ")) next
            if (!($1 in row)) order[++n] = $1
            else if (!own || mine[$1]) next
            row[$1] = r; mine[$1] = own
        }
        END { for (i = 1; i <= n; i++) print row[order[i]] }'
}

# The default integration branch: origin/HEAD, else a local main or master; blank without one.
spec_default_branch() { # root
    local base candidate
    base="$(git -C "$1" symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null)" ||
        { [ "$?" -eq 1 ] || return 1; }
    for candidate in main master; do
        [ -n "$base" ] && break
        if git -C "$1" show-ref --verify --quiet "refs/heads/$candidate"; then base="$candidate"
        elif [ "$?" -ne 1 ]; then return 1; fi
    done
    printf '%s\n' "$base"
}

# Unicode White_Space in UTF-8; use in the C byte locale on BSD and older awk.
SPEC_REVIEW_SPACES_AWK='function spaces(s) {
    gsub(/[[:space:]]|\302[\205\240]|\341\232\200|\342\200[\200-\212\250\251\257]|\342\201\237|\343\200\200/, " ", s)
    return s
}'

# Validate the whole raw config before emitting policy. Blank prefix lines preserve
# assignment line numbers. An absent level-two Configuration heading emits no policy.
spec_review_config_section() {
    LC_ALL=C awk -v diagnostic="${1:-/dev/stderr}" "$SPEC_REVIEW_SPACES_AWK"'
        # Only the underline check exempts examples. Raw ATX duplicate and marker
        # checks below still see every line, including examples.
        function underline_check(s,   tail,mark,at) {
            if (fence != "") {
                tail=s; sub(/[ \t]+$/, "", tail)
                if (index(tail,fence)==1 && (tail ~ /^`+$/ || tail ~ /^~+$/)) fence=""
                return
            }
            if (!comment && match(s,/^```+|^~~~+/)) {
                mark=substr(s,1,RLENGTH); tail=substr(s,RLENGTH+1)
                if (substr(mark,1,1)=="~" || !index(tail,"`")) { fence=mark; return }
            }
            if (!comment && !underline && nonblank && s ~ /^(=+|-+)[ \t]*$/) underline=NR
            # Comments may open inline or close and reopen on the same line.
            # A literal opener in indented code, an escape or a code span must
            # not hide a later visible underline.
            if (!comment && s ~ /^[ \t]/) return
            while (length(s)) {
                if (comment) {
                    at=index(s,"-->"); if (!at) return
                    comment=0; s=substr(s,at+3)
                } else {
                    if (!match(s,/<!--|\\.|`+/)) return
                    mark=substr(s,RSTART,RLENGTH); s=substr(s,RSTART+RLENGTH)
                    if (mark=="<!--") comment=1
                    else if (substr(mark,1,1)=="`") {
                        tail=s
                        while (match(tail,/`+/)) {
                            at=RLENGTH; tail=substr(tail,RSTART+RLENGTH)
                            if (at==length(mark)) { s=tail; break }
                        }
                    }
                }
            }
        }
        function scan(raw,n,   s,title,level) {
            s=raw; sub(/\r$/, "", s); sub(/^ ? ? ?/, "", s)
            underline_check(s)
            nonblank=(spaces(raw) ~ /[^[:space:]]/)
            if (match(s,/^#+/) && RLENGTH<=6 && substr(s,RLENGTH+1) ~ /^([ \t]|$)/) {
                level=RLENGTH; title=substr(s,level+1)
            }
            if (level) {
                title=spaces(title)
                sub(/[[:space:]]+$/, "", title); sub(/#+$/, "", title)
                gsub(/^[[:space:]]+|[[:space:]]+$/, "", title)
                if (tolower(title)=="configuration") {
                    if (!first) first=n; else if (!duplicate) duplicate=n
                    if (level==2 && !start) start=n
                }
                if (start && !stop && n>start && level<=2) stop=n
            }
            if (!stop && !marker) {
                s=spaces(raw)
                if (s ~ /<!--|-->/ || s ~ /^[[:space:]]*(```|~~~)/) marker=n
            }
            if (start && !stop && n>start) body[n]=raw
        }
        { scan($0,NR) }
        END {
            if (underline) {
                print "config.md line " underline ": underlined headings are unsupported; use #/## headings" > diagnostic; exit 1
            }
            if (duplicate) {
                print "Duplicate Configuration headings at config.md lines " first " and " duplicate > diagnostic; exit 1
            }
            if (!start) exit
            if (marker) {
                print "config.md line " marker ": comments and fenced blocks are not allowed before or within the policy section" > diagnostic; exit 1
            }
            for (i=1;i<(stop ? stop : NR+1);i++) print body[i]
        }'
}

# Full remote-tracking names from one committed configuration; read by both review
# and the local-only allocation/close preflight. Configuration is literal, not patterns.
spec_configured_integration_refs() {
    local configured candidate
    configured="$(awk '
        /^- review-integration-branches:/ {
            n++; value = $0; sub(/^- review-integration-branches:[[:space:]]*/, "", value)
        }
        END { if (n == 1) { gsub(/,/, " ", value); print value } }')" || return 1
    while IFS= read -r candidate; do
        [ -n "$candidate" ] || continue
        case "$candidate" in refs/remotes/*) ;; *) candidate="refs/remotes/$candidate" ;; esac
        git check-ref-format "$candidate" >/dev/null 2>&1 || continue
        printf '%s\n' "$candidate" || return 1
    done < <(printf '%s\n' "$configured" | tr ' \t\r' '\n')
}

# The trusted integration refs: origin/HEAD's target (a full refs/remotes/ name, always first),
# then each ref named by `review-integration-branches` in the committed config at that target,
# never the worktree's own. One full ref per line; blank when origin/HEAD does not resolve to a
# commit, in which case nothing is trusted.
spec_trusted_refs() { # root [--tips | --policy-errors FILE | --at <frozen-default-ref> <commit>]
    local root="$1" default sha policy configured candidate tip seen diagnostic=/dev/null
    if [ "${2:-}" = --policy-errors ]; then diagnostic="$3"; fi
    if [ "${2:-}" = --at ]; then
        default="$3"; sha="$4"
    else
        default="$(git -C "$root" symbolic-ref --quiet refs/remotes/origin/HEAD 2>/dev/null || true)"
        case "$default" in refs/remotes/*) ;; *) return 0 ;; esac
        sha="$(git -C "$root" rev-parse --verify --quiet "$default^{commit}" || true)"
        [ -n "$sha" ] || return 0
    fi
    if [ "${2:-}" = --tips ]; then printf '%s\t%s\n' "$default" "$sha"; else printf '%s\n' "$default"; fi
    seen="$default"
    if [ "${2:-}" = --at ]; then
        policy="$(_spec_trusted_integration_policy "$root" "$sha")" || return 1
    else
        policy="$(_spec_trusted_integration_policy "$root" "$sha" 2>/dev/null)" || policy=""
    fi
    if ! policy="$(spec_review_config_section "$diagnostic" <<< "$policy")"; then
        case "${2:-}" in --policy-errors|--at) return 1 ;; *) return 0 ;; esac
    fi
    configured="$(spec_configured_integration_refs <<< "$policy")" || return 1
    while IFS= read -r candidate; do
        [ -n "$candidate" ] || continue
        if [ "${2:-}" = --tips ]; then
            case $'\n'"$seen"$'\n' in *$'\n'"$candidate"$'\n'*) continue ;; esac
            seen="$seen"$'\n'"$candidate"
            tip="$(git -C "$root" rev-parse --verify --quiet "$candidate^{commit}")" || continue
            printf '%s\t%s\n' "$candidate" "$tip"
        else printf '%s\n' "$candidate"; fi
    done <<< "$configured"
}

_spec_trusted_integration_policy() ( # root frozen default commit
    local blob
    if [ "${SPEC_HISTORY_VIEW_ROOT:-}" != "$1" ]; then
        spec_open_history_view "$1" || return 1
        trap spec_close_history_view EXIT
    fi
    # A symlink is customization, not policy. Neither follow it nor parse the link target.
    blob="$(_spec_regular_blob_at "$1" "$2" .prdkit/memory/config.md --skip-symlink)" || return 1
    [ -z "$blob" ] || _spec_history_git "$1" cat-file blob "$blob"
)

# Reading an object a partial clone lacks would fetch it from the promisor remote. Git 2.45+
# refuses under GIT_NO_LAZY_FETCH; with an older Git, callers skip object reads in a partial clone.
spec_objects_local() { # root -> 0 safe in place, 1 probe error, 2 old Git in a partial clone
    local value status entries partial=false version
    if value="$(git -C "$1" config --get extensions.partialclone 2>/dev/null)"; then
        [ -n "$value" ] || return 1
        partial=true
    else
        status=$?; [ "$status" -eq 1 ] || return 1
    fi
    if entries="$(git -C "$1" config --bool --get-regexp '^remote\..*\.promisor$' 2>/dev/null)"; then
        while IFS= read -r value; do
            case "$value" in *' true') partial=true ;; *' false') ;; *) return 1 ;; esac
        done <<< "$entries"
    else
        status=$?; [ "$status" -eq 1 ] || return 1
    fi
    # A remote filter alone registers a promisor remote, even without a Boolean flag.
    if git -C "$1" config --name-only --get-regexp '^remote\..*\.partialclonefilter$' >/dev/null 2>&1; then
        partial=true
    else
        status=$?; [ "$status" -eq 1 ] || return 1
    fi
    $partial || return 0
    version="$(git version 2>/dev/null)" || return 1
    [[ "$version" =~ ^git[[:space:]]version[[:space:]]([0-9]+)\.([0-9]+) ]] || return 1
    [ "$((10#${BASH_REMATCH[1]} * 1000 + 10#${BASH_REMATCH[2]}))" -ge 2045 ] || return 2
}

# Records committed at <rev> with no child or outcome checkpoint keeping them open.
# Reviews are native GitHub reviews, not record text: T0 and not-yet-reviewed records are listed too.
# Committed objects only; prints change ids.
spec_unclosed_changes() { # root rev
    local root="$1" rev="$2" path paths record_text
    paths="$(GIT_NO_LAZY_FETCH=1 verify_lines git -C "$root" ls-tree --name-only "$rev" -- specs/changes/)" || return 1
    while IFS= read -r path; do
        case "$path" in specs/changes/*.md) ;; *) continue ;; esac
        record_text="$(GIT_NO_LAZY_FETCH=1 verify_lines git -C "$root" cat-file blob "$rev:$path")" || return 1
        [ -n "$record_text" ] || { printf 'Empty committed change record: %s\n' "$path" >&2; return 1; }
        printf '%s\n' "$record_text" |
            awk -v id="$(spec_field "$(basename "$path" .md)")" "$SPEC_SKIP_AWK $SPEC_CHANGE_TAG_AWK"'
            function trim(s) { gsub(/`/, "", s); gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
            { $0 = change_without_tags($0) }
            { gsub(/\037/, " "); if (spec_skip($0)) next }
            /\*\*Parent\*\*:/ && !seen_parent {
                seen_parent = 1; v = $0; sub(/^.*\*\*Parent\*\*:[[:space:]]*[[`]?/, "", v)
                if (match(v, /^[0-9][0-9][0-9]+-[a-z0-9]+(-[a-z0-9]+)*/)) parent = substr(v, 1, RLENGTH)
            }
            /^## / { sec = $0; next }
            sec != "## State" { next }
            /^### Outcome checkpoint[[:space:]]*$/ { checkpoint = 1 }
            END {
                print id "\037" parent "\037" (!checkpoint)
            }' || return 1
    done <<< "$paths" | awk -F '\037' '{ id[++n] = $1; flagged[n] = $3; if ($2 != "") named[$2] = 1 }
        END { for (i = 1; i <= n; i++) if (flagged[i] && !(id[i] in named)) print id[i] }'
}
