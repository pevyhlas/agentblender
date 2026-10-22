#!/usr/bin/env bash
# spec-lib.sh — read-only parsers for the 1.0 spec layout, shared by status.sh, doctor.sh,
# bounds.sh, check-review.sh, new-change.sh and init.sh. Every function prints rows separated
# by the unit separator (\037, SPEC_SEP) — a tab would let `read` collapse empty fields — and
# never writes. Bash 3.2 and POSIX awk only.
# shellcheck disable=SC2034 # used by the scripts that source this library
SPEC_SEP="$(printf '\037')"

# IDs: J-, R-, Q-, C-, N- (product), D- (decisions), P<n>.B- and P<n>.I- (parts), two or more digits.
# A neighbouring character is tested with index(), never a regex: macOS awk counts bytes, and a
# regex over the lone byte of a multibyte character (an em dash after an ID) aborts it in a UTF-8
# locale. tests/test_script_safety.sh guards this for every script.
SPEC_ID_AWK='function spec_ids(s, where,   pre, post, tok, w) {
    w = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"
    while (match(s, /(P[0-9]+\.[BI]|[JRQCDN])-[0-9][0-9]+/)) {
        pre = RSTART > 1 ? substr(s, RSTART - 1, 1) : ""
        tok = substr(s, RSTART, RLENGTH)
        post = substr(s, RSTART + RLENGTH, 1)
        if (!(pre != "" && index(w ".", pre)) && !(post != "" && index(w, post))) print tok "\037" where
        s = substr(s, RSTART + RLENGTH)
    }
}'

# Lines inside HTML comments and code fences are format notes, never content.
SPEC_SKIP_AWK='function spec_skip(line) {
    if (fence) { if (line ~ /^[[:space:]]*```/) fence = 0; return 1 }
    if (line ~ /^[[:space:]]*```/) { fence = 1; return 1 }
    if (comment) { if (index(line, "-->")) comment = 0; return 1 }
    if (line ~ /^[[:space:]]*<!--/) { if (!index(line, "-->")) comment = 1; return 1 }
    return 0
}'

# Lifecycle stages, in order: idea, demo, mvp, private preview, public preview, ga, sunset,
# retired. A project may skip stages; only the owner moves the current one (lifecycle.md, Now).
# shellcheck disable=SC2016 # awk code: its $ is awk's, never the shell's
SPEC_STAGE_AWK='function stage_rank(s) {
    s = tolower(s); gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); gsub(/[[:space:]]+/, " ", s)
    if (s == "idea") return 1; if (s == "demo") return 2; if (s == "mvp") return 3
    if (s == "private preview") return 4; if (s == "public preview") return 5
    if (s == "ga" || s == "general availability") return 6
    if (s == "sunset") return 7; if (s == "retired") return 8
    return 0
}
# The stage named by a `from: <stage>` marker in text (the parenthetical of an item heading or
# bullet, or a parts-table Maturity cell), lowercased; blank when there is none.
function stage_from(text,   t, m) {
    t = tolower(text)
    if (!match(t, /from:[[:space:]]*[a-z]+([[:space:]]+[a-z]+)?/)) return ""
    m = substr(t, RSTART, RLENGTH); sub(/^from:[[:space:]]*/, "", m); gsub(/[[:space:]]+/, " ", m)
    if (m !~ /^(private|public) preview$/ && m !~ /^general availability$/) sub(/ .*/, "", m)
    return m
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

# A directory counts as 0.x canon when it holds anything besides a .gitkeep. In a Git work tree
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

# Prints 1.0, 0.x, both or none. The cutover condenses and removes every 0.x folder, feature
# folders included, so any of them beside a 1.0 spec is a cutover not finished.
spec_layout() { # root
    local root="$1" old=false name
    for name in product-spec targets features; do _spec_dir_used "$root/specs/$name" && old=true; done
    if _spec_has_canon "$root"; then
        if $old; then echo both; else echo 1.0; fi
    elif $old; then echo 0.x
    else echo none; fi
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

# 0.x feature folders still on main beside a 1.0 spec, which the cutover condenses and removes:
# id, tasks checked, tasks total.
spec_leftover_features() { # root
    local root="$1" dir counts
    _spec_has_canon "$root" || return 0
    for dir in "$root"/specs/features/[0-9]*/; do
        dir="${dir%/}"
        _spec_dir_used "$dir" || continue
        counts="0${SPEC_SEP}0"
        [ ! -f "$dir/tasks.md" ] || counts="$(awk '/^[[:space:]]*[-*][[:space:]]+\[[ xXIRB]\]/ { n++; if ($0 ~ /^[[:space:]]*[-*][[:space:]]+\[[xX]\]/) d++ }
            END { print d + 0 "\037" n + 0 }' "$dir/tasks.md")"
        printf '%s\037%s\n' "${dir##*/}" "$counts"
    done
    return 0
}

# --- specs/archive.md (archive-template.md): the cutover's base commit, its last 0.x feature --
# A label is read loosely: bold or plain, in a heading, list item, quote or table row, its value
# after a colon or a dash, or alone on the next line. Bash string matching only, no awk: the
# file may hold any character.
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
_spec_archive_value() { # file base|last -> the value of the first line with that label
    local line v pending=false
    [ -f "$1" ] || return 0
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"; _spec_trim "${line//[*_\`#|>]/}"
        if $pending; then
            [ -z "$_spec_t" ] || { printf '%s\n' "$_spec_t"; return 0; }
            continue
        fi
        case "$2:$_spec_t" in
            base:[Bb][Aa][Ss][Ee]|base:[Bb][Aa][Ss][Ee][!A-Za-z]*)
                _spec_trim "${_spec_t#????}"
                case "$_spec_t" in [Cc][Oo][Mm][Mm][Ii][Tt]*) _spec_trim "${_spec_t#??????}" ;; esac ;;
            last:[Ll][Aa][Ss][Tt]' '0.[Xx]' '[Ff][Ee][Aa][Tt][Uu][Rr][Ee]*)
                v="${_spec_t#????????????????}"; _spec_trim "${v#[Ss]}"
                case "$_spec_t" in [Nn][Uu][Mm][Bb][Ee][Rr]*) v="${_spec_t#??????}"; _spec_trim "${v#[Ss]}" ;; esac ;;
            *) continue ;;
        esac
        [ -z "$_spec_t" ] || { printf '%s\n' "$_spec_t"; return 0; }
        pending=true
    done < "$1"
    return 0
}
# Hex words of 7 to 40 characters in the Base value: candidate commits, first as written.
spec_archive_base_tokens() { # root
    local v
    v="$(_spec_archive_value "$1/specs/archive.md" base)"
    [ -z "$v" ] || printf '%s\n' "$v" | tr -c '0-9a-fA-F' '\n' | sed -n '/^[0-9a-fA-F]\{7,40\}$/p'
    return 0
}
# The last 0.x feature number: the highest number before any "(", ";" or ",", 0 for none,
# nothing when the line is missing or holds no number.
spec_archive_last() { # root
    local v d max=""
    v="$(_spec_archive_value "$1/specs/archive.md" last)"
    case "$v" in [Nn][Oo][Nn][Ee]*) echo 0; return 0 ;; esac
    for d in $(printf '%s' "${v%%[(;,]*}" | tr -c '0-9' ' '); do
        [ "${#d}" -le 9 ] || continue
        d=$((10#$d)); { [ -n "$max" ] && [ "$max" -ge "$d" ]; } || max="$d"
    done
    [ -z "$max" ] || echo "$max"
    return 0
}

# 0.x feature folder names (NNN-slug) in a commit's tree, or deleted in the last 20 commits that
# deleted anything under specs/features or specs/archive: git keeps what the cutover removed.
# No lazy fetch in a partial clone; a missing object reads as nothing.
SPEC_FEATURE_SED='s#^specs/features/\(_archive/\)\{0,1\}\([0-9][0-9][0-9]*-[^/]*\)\(/.*\)\{0,1\}$#\2#p
s#^specs/archive/\([^/]*/\)\{0,1\}\(_archive/\)\{0,1\}\([0-9][0-9][0-9]*-[^/]*\)\(/.*\)\{0,1\}$#\3#p'
spec_features_at() { # root commit
    { GIT_NO_LAZY_FETCH=1 git -C "$1" ls-tree -r -d --name-only "$2" -- specs/features specs/archive 2>/dev/null || true; } |
        sed -n "$SPEC_FEATURE_SED" | sort -u
}
spec_removed_features() { # root
    { GIT_NO_LAZY_FETCH=1 git -C "$1" log -n 20 --no-renames --diff-filter=D --name-only --format= \
          -- specs/features specs/archive 2>/dev/null || true; } |
        sed -n "$SPEC_FEATURE_SED" | sort -u
}

# Spec files that define IDs, relative to root.
spec_files() { # root
    local root="$1" path
    for path in "$root"/specs/SPEC.md "$root"/specs/product.md "$root"/specs/product/*.md \
                "$root"/specs/product/*/*.md "$root"/specs/architecture.md "$root"/specs/architecture/*.md \
                "$root"/specs/architecture/*/*.md \
                "$root"/specs/parts/*.md "$root"/specs/parts/*/*.md "$root"/specs/decisions/*.md; do
        [ -f "$path" ] && printf '%s\n' "${path#"$root"/}"
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
    local root="$1" files=() file; shift
    if [ "$#" -gt 0 ]; then files=("$@")
    else while IFS= read -r file; do files+=("$file"); done < <(spec_files "$root"); fi
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
# as a section of architecture/decisions.md).
spec_items() { # root
    local root="$1" file
    while IFS= read -r file; do
        awk -v file="$file" "$SPEC_SKIP_AWK"'
            function flags(line,   m, p) {
                m = ""
                if (match(line, /\((hypothesis|validated)[,)]/)) m = substr(line, RSTART + 1, RLENGTH - 2)
                p = ""
                if (match(line, /planned:[[:space:]]*[^ .;,)|]+/)) { p = substr(line, RSTART, RLENGTH); sub(/planned:[[:space:]]*/, "", p) }
                return m "\037" p "\037" (tolower(line) ~ /retired:/ ? "yes" : "no") "\037" (tolower(line) ~ /unenforced/ ? "yes" : "no")
            }
            # A bullet item runs on to the next bullet, heading, table row or blank line.
            function flush() { if (pend != "") print pend_id "\037" pend_title "\037" flags(pend) "\037" file ":" pend_nr; pend = "" }
            { if (spec_skip($0)) { flush(); next } }
            pend != "" && $0 !~ /^[[:space:]]*$/ && $0 !~ /^[[:space:]]*([-*+]|[0-9]+\.)[[:space:]]/ && $0 !~ /^(#|\|)/ { pend = pend " " $0; next }
            { flush() }
            /^#/ { retired_list = ($0 ~ /^#+[[:space:]]+Retired[[:space:]]*$/) }
            /^\|[[:space:]]*[Pp]art[[:space:]]*\|/ {
                matcol = 4; n = split($0, cell, "|")
                for (i = 2; i <= n; i++) { h = cell[i]; gsub(/^[[:space:]]+|[[:space:]]+$/, "", h); if (tolower(h) == "maturity") matcol = i }
                next
            }
            /^\|[[:space:]]*P[0-9]+[[:space:]]/ {
                n = split($0, cell, "|"); id = cell[2]; gsub(/^[[:space:]]+|[[:space:]]+$/, "", id)
                title = id; sub(/^P[0-9]+[[:space:]]+/, "", title); sub(/[[:space:]].*/, "", id)
                # Maturity and planned come from the Maturity cell (`hypothesis, planned: 012-x`), so
                # "planned:" elsewhere in the row does not mark the part.
                mc = cell[matcol ? matcol : 4]; mat = mc; gsub(/^[[:space:]]+|[[:space:]]+$/, "", mat); sub(/[,;( ].*/, "", mat)
                if (mat != "hypothesis" && mat != "validated") mat = ""
                f = flags(mc); split(f, mfl, "\037"); f = flags($0); split(f, fl, "\037")
                print id "\037" title "\037" mat "\037" mfl[2] "\037" fl[3] "\037" fl[4] "\037" file ":" NR
                next
            }
            # A part on the Retired list: `- P3 Store: retired: <where>`, only under `## Retired`.
            retired_list && /^[[:space:]]*[-*][[:space:]]+(\*\*)?P[0-9]+[[:space:]][^|]*retired:/ {
                id = $0; sub(/^[[:space:]]*[-*][[:space:]]+(\*\*)?/, "", id); title = id
                sub(/[[:space:]].*/, "", id); sub(/^P[0-9]+[[:space:]]+/, "", title); sub(/[*:].*/, "", title)
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
            /^[[:space:]]*[-*][[:space:]]+\*\*(P[0-9]+\.[BI]|[JRQCDN])-[0-9]/ {
                body = $0; sub(/^[[:space:]]*[-*][[:space:]]+\*\*/, "", body)
                id = body; sub(/[[:space:]*].*/, "", id)
                title = body; sub(/^[^[:space:]*]+[[:space:]]*/, "", title); sub(/\*\*.*/, "", title)
                pend = $0; pend_id = id; pend_title = title; pend_nr = NR
            }
            END { flush() }
        ' "$root/$file"
    done < <(spec_files "$root")
}

# Change IDs that `planned: <change>` markers name (spec.md, Planned) in spec files, outside
# comments and fenced blocks; an ID without a slug ends in "-". new-change.sh reserves them.
# As in spec_items, an item runs on to the next bullet, heading, table row or blank line (a
# paragraph too), so a marker that wraps, even inside `planned:` or its code span, is read whole.
spec_planned_changes() { # root
    local root="$1" file id num
    while IFS= read -r file; do
        awk "$SPEC_SKIP_AWK"'
            function flush() { if (item != "") print item; item = "" }
            { if (spec_skip($0)) { flush(); next } }
            /^[[:space:]]*$/ { flush(); next }
            /^[[:space:]]*([-*+]|[0-9]+\.)[[:space:]]/ || /^(#|\|)/ { flush() }
            { line = $0; sub(/^[[:space:]]+/, "", line); item = (item == "" ? line : item " " line) }
            END { flush() }
        ' "$root/$file"
    done < <(spec_files "$root") |
        { grep -oE 'planned[[:space:]]*:[[:space:]]*`?[[:space:]]*[0-9]{3,}(-[A-Za-z0-9-]*)?' || true; } | sed 's/^planned[^0-9]*//' |
        while IFS= read -r id; do
            num="${id%%-*}"; [ "${#num}" -le 9 ] || continue
            case "$id" in *-*) printf '%s\n' "$id" ;; *) printf '%s-\n' "$id" ;; esac
        done | sort -u
}

# The current lifecycle stage: `**Now**: <stage>` in specs/product/lifecycle.md (blank if none),
# then the next stage from `**Next**:`.
spec_stage_now() { # root
    local file="$1/specs/product/lifecycle.md"
    [ -f "$file" ] || return 0
    awk '
        # The stage name a field starts with (`MVP since 2026-10-01` is MVP), in its own case.
        function field(name,   t, l) {
            if (!match($0, "\\*\\*" name "\\*\\*:[^*·]*")) return ""
            t = substr($0, RSTART, RLENGTH); sub(/^[^:]*:[[:space:]]*/, "", t); l = tolower(t)
            if (l ~ /^(private|public)[[:space:]]+preview/ || l ~ /^general[[:space:]]+availability/) match(t, /^[A-Za-z]+[[:space:]]+[A-Za-z]+/)
            else match(t, /^[A-Za-z]+/)
            return RSTART ? substr(t, 1, RLENGTH) : ""
        }
        !now && index($0, "**Now**:") { now = field("Now"); next_ = field("Next") }
        END { print now "\037" next_ }
    ' "$file"
}

# Items marked for a later stage: id, stage (lowercased), file:line. The marker sits in a
# heading's or bullet's parenthetical (`(hypothesis, from: ga)`) or a part's Maturity cell.
spec_stages() { # root
    local root="$1" file
    while IFS= read -r file; do
        awk -v file="$file" "$SPEC_SKIP_AWK $SPEC_STAGE_AWK"'
            function emit(id, text,   s) { s = stage_from(text); if (s != "") print id "\037" s "\037" file ":" NR }
            { if (spec_skip($0)) next }
            /^\|[[:space:]]*[Pp]art[[:space:]]*\|/ {
                matcol = 4; n = split($0, cell, "|")
                for (i = 2; i <= n; i++) { h = cell[i]; gsub(/^[[:space:]]+|[[:space:]]+$/, "", h); if (tolower(h) == "maturity") matcol = i }
                next
            }
            /^\|[[:space:]]*P[0-9]+[[:space:]]/ {
                n = split($0, cell, "|"); id = cell[2]; gsub(/^[[:space:]]+/, "", id); sub(/[[:space:]].*/, "", id)
                emit(id, cell[matcol ? matcol : 4]); next
            }
            /^#+[[:space:]]+[JD]-[0-9][0-9]+[[:space:]]/ { if (match($0, /\([^)]*\)[[:space:]]*$/)) emit($2, substr($0, RSTART, RLENGTH)); next }
            /^[[:space:]]*[-*+][[:space:]]+\*\*(P[0-9]+\.[BI]|[JRQCN])-[0-9][0-9]+/ {
                id = $0; sub(/^[[:space:]]*[-*+][[:space:]]+\*\*/, "", id); sub(/[[:space:]*].*/, "", id)
                rest = $0; sub(/^[^*]*\*\*[^*]*\*\*/, "", rest)
                if (match(rest, /^[[:space:]]*\([^)]*\)/)) emit(id, substr(rest, RSTART, RLENGTH))
            }
        ' "$root/$file"
    done < <(spec_files "$root")
}

# Part file headings: file, part id declared by its `# P<n> name` heading (blank if none). A
# component file in parts/p<n>-<slug>/ declares its part's id too.
spec_part_headings() { # root
    local root="$1" path
    for path in "$root"/specs/parts/*.md "$root"/specs/parts/*/*.md; do
        [ -f "$path" ] || continue
        printf '%s\037%s\n' "${path#"$root"/}" "$(awk '/^#[[:space:]]+P[0-9]+([[:space:]]|$)/ { print $2; exit }' "$path")"
    done
    return 0
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

# Owner inbox items: line number, date, who, state (open|folded|declined|deferred|unknown), words.
# An item is a `### <title>` heading followed by its meta line `*<date> · <owner|agent> ·
# <disposition>*` (words: the title; line: the heading), or a one-line
# `<date> <owner|agent> "<words>" → <disposition>` outside any item. A heading without a meta
# line prints state "unparsed" with its title as words; so does prose outside an item, except in
# an inbox of headed items, where it is ordinary text (such as "Nothing open.").
spec_inbox() { # root
    local file="$1/specs/inbox.md" headed=0
    [ -f "$file" ] || return 0
    grep -q '^###[[:space:]]' "$file" && headed=1
    awk -v headed="$headed" "$SPEC_SKIP_AWK"'
        function nometa() { if (want) print head "\037\037\037unparsed\037" title; want = 0 }
        function isdate(s) { return s ~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9](T[0-9][0-9]:[0-9][0-9](:[0-9][0-9])?Z?)?$/ }
        { if (spec_skip($0)) next }
        /^###[[:space:]]/ {
            nometa(); head = NR; title = $0; sub(/^###[[:space:]]+/, "", title); sub(/[[:space:]]+$/, "", title)
            gsub(/\t/, " ", title); want = 1; item = 1; next
        }
        /^#[[:space:]]/ || /^##[[:space:]]/ { nometa(); item = 0; next }
        /^[[:space:]]*$/ { next }
        want {
            want = 0; meta = $0
            sub(/^[[:space:]]*[*_]+/, "", meta); sub(/[*_]+[[:space:]]*$/, "", meta)
            n = split(meta, f, /[[:space:]]*(·|\|)[[:space:]]*/)
            if (n >= 2 && isdate(f[1]) && (f[2] == "owner" || f[2] == "agent")) {
                state = "unknown"
                if (n >= 3) { st = f[3]; sub(/[:[:space:]].*/, "", st); if (st ~ /^(open|folded|declined|deferred)$/) state = st }
                print head "\037" substr(f[1], 1, 10) "\037" f[2] "\037" state "\037" title
            } else print head "\037\037\037unparsed\037" title
            next
        }
        /^#/ { next }
        {
            line = $0; sub(/^[[:space:]]*[-*][[:space:]]+/, "", line)
            # Inside an item, only a line in the one-line form counts, and it ends the item: owner
            # direction appended under a headed item must not vanish into its body.
            if (item && line !~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9](T[0-9][0-9]:[0-9][0-9](:[0-9][0-9])?Z?)?[[:space:]]+(owner|agent)[[:space:]]+"/) next
            item = 0
            if (line !~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9](T[0-9][0-9]:[0-9][0-9](:[0-9][0-9])?Z?)?[[:space:]]+(owner|agent)[[:space:]]/) {
                if (headed && line !~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]/) next
                gsub(/\t/, " ", line); print NR "\037\037\037unparsed\037" line; next
            }
            split(line, w, /[[:space:]]+/); w[1] = substr(w[1], 1, 10)
            state = "unknown"; words = line; sub(/^[^[:space:]]+[[:space:]]+[^[:space:]]+[[:space:]]+/, "", words)
            # The disposition arrow follows a closing quote and names a disposition; quoted words
            # and disposition notes may hold arrows too. Otherwise the first arrow.
            arrow = 0
            if (match(words, /"[[:space:]]*(→|->)[[:space:]]*(open|folded|declined|deferred)([:[:space:]]|$)/)) {
                # Skip the spaces by trimming the whole tail, not by testing one character at a
                # time (see SPEC_ID_AWK): macOS awk aborts on the first byte of "→" alone.
                arrow = RSTART + 1; gap = substr(words, arrow); glen = length(gap)
                sub(/^[[:space:]]+/, "", gap); arrow += glen - length(gap)
                alen = (substr(words, arrow, 2) == "->") ? 2 : length("→")
            }
            if (!arrow) { arrow = index(words, "→"); alen = length("→") }
            if (!arrow) { arrow = index(words, "->"); alen = 2 }
            if (arrow) {
                rest = substr(words, arrow + alen); sub(/^[[:space:]]+/, "", rest); sub(/[:[:space:]].*/, "", rest)
                if (rest ~ /^(open|folded|declined|deferred)$/) state = rest
                words = substr(words, 1, arrow - 1); sub(/[[:space:]]+$/, "", words)
            }
            gsub(/\t/, " ", words)
            print NR "\037" w[1] "\037" w[2] "\037" state "\037" words
        }
        END { nometa() }' "$file"
}

# Records: line, type, name, owner, scope, env, rev, date, until, check, reason. A line that
# is not a valid record prints type INVALID and the problem as its reason.
spec_records() { # root
    local file="$1/specs/records.md"
    [ -f "$file" ] || return 0
    awk "$SPEC_SKIP_AWK"'
        function isdate(s) { return s ~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]$/ }
        function bad(why) { print NR "\037INVALID\037\037\037\037\037\037\037\037\037" why; }
        { if (spec_skip($0)) next }
        /^[[:space:]]*$/ || /^#/ { next }
        {
            line = $0; reason = ""
            if (match(line, /[[:space:]]—([[:space:]]|$)/)) {
                reason = substr(line, RSTART + RLENGTH); line = substr(line, 1, RSTART - 1)
                sub(/^[[:space:]]+/, "", reason)
            }
            gsub(/\t/, " ", reason)
            n = split(line, f, /[[:space:]]+/)
            type = f[1]; name = f[2]
            split("", kv)
            # A word without "=" continues the previous value: until=<condition> and what=<claim>
            # may hold spaces. A date stays a date (a note after it is dropped, as before). A word
            # before the first key is malformed.
            stray = ""; k = ""
            for (i = 3; i <= n; i++)
                if (index(f[i], "=")) { k = substr(f[i], 1, index(f[i], "=") - 1); kv[k] = substr(f[i], index(f[i], "=") + 1) }
                else if (f[i] != "" && k != "") { if (!isdate(kv[k])) kv[k] = kv[k] " " f[i] }
                else if (f[i] != "" && stray == "") stray = f[i]
            if (type !~ /^(HOLD|WAIVER|ATTEST|DEPLOY)$/) { bad("not a HOLD, WAIVER, ATTEST or DEPLOY record"); next }
            if (name == "" || index(name, "=") || name !~ /^[A-Za-z0-9][A-Za-z0-9._-]*$/) { bad(type " needs an id or app name first"); next }
            if (type == "HOLD") { need = "owner scope since until"; date = kv["since"]; until = kv["until"]; who = kv["owner"] }
            else if (type == "WAIVER") { need = "owner check scope expires"; date = ""; until = kv["expires"]; who = kv["owner"] }
            else if (type == "ATTEST") { need = "by what env version date"; date = kv["date"]; until = ""; who = kv["by"] }
            else { need = "env rev date by"; date = kv["date"]; until = ""; who = kv["by"] }
            if (stray != "") { bad(type " " name ": \"" stray "\" comes before any key=value"); next }
            m = split(need, req, " "); missing = ""
            for (i = 1; i <= m; i++) if (kv[req[i]] == "") missing = missing " " req[i] "="
            if (missing != "") { bad(type " " name " is missing" missing); next }
            if (date != "" && !isdate(date)) { bad(type " " name " date must be YYYY-MM-DD"); next }
            if (type == "WAIVER" && !isdate(until)) { bad("WAIVER " name " expires= must be YYYY-MM-DD"); next }
            rev = (type == "ATTEST") ? kv["version"] : kv["rev"]
            check = (type == "ATTEST") ? kv["what"] : kv["check"]
            print NR "\037" type "\037" name "\037" who "\037" kv["scope"] "\037" kv["env"] "\037" rev "\037" date "\037" until "\037" check "\037" reason
        }' "$file"
}

# Open changes: id, title, opened, step, waiting on, next, parent, acceptance total, passed,
# failed or blocked, highest attempts, stalled items (not passed, and attempts >= 3 or BLOCKED;
# comma-separated).
spec_changes() { # root
    local root="$1" path
    for path in "$root"/specs/changes/*.md; do
        [ -f "$path" ] || continue
        awk -v id="$(basename "$path" .md)" "$SPEC_SKIP_AWK"'
            function trim(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
            function field(line, name,   v) {
                v = line; sub("^.*\\*\\*" name "\\*\\*:[[:space:]]*", "", v); sub(/[[:space:]]+·.*/, "", v); return trim(v)
            }
            { if (spec_skip($0)) next }
            NR == 1 && /^# / { title = $0; sub(/^#[[:space:]]+(Change:[[:space:]]*)?/, "", title) }
            /\*\*Opened\*\*:/ { opened = field($0, "Opened") }
            /\*\*Parent\*\*:/ { parent = field($0, "Parent") }
            /^\*\*Step\*\*:/ { step = field($0, "Step") }
            /^\*\*Waiting on\*\*:/ { waiting = field($0, "Waiting on") }
            /^\*\*Next\*\*:/ { nxt = field($0, "Next") }
            /^## / { sec = $0; sub(/^##[[:space:]]+/, "", sec); next }
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
                gsub(/\t/, " ", title); gsub(/\t/, " ", waiting); gsub(/\t/, " ", nxt)
                print id "\037" title "\037" opened "\037" step "\037" waiting "\037" nxt "\037" parent "\037" total + 0 "\037" passed + 0 "\037" failed + 0 "\037" maxatt + 0 "\037" stalled
            }' "$path"
    done
    return 0
}

# Checkouts that can hold open change records, as path, branch and location (relative to root,
# "." for root itself): root first, then every other registered worktree whose directory exists
# (new-change.sh creates the record in one). Local git metadata only.
spec_checkouts() { # root
    local root="$1" top line path="" branch="" real
    printf '%s\037%s\037.\n' "$root" "$(git -C "$root" symbolic-ref --quiet --short HEAD 2>/dev/null || true)"
    git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1 || return 0
    top="$(CDPATH="" cd "$root" && pwd -P)"
    while IFS= read -r line; do
        case "$line" in
            "worktree "*) path="${line#worktree }"; branch="" ;;
            "branch refs/heads/"*) branch="${line#branch refs/heads/}" ;;
            "")
                real=""
                [ -z "$path" ] || [ ! -d "$path" ] || real="$(CDPATH="" cd "$path" && pwd -P)"
                if [ -n "$real" ] && [ "$real" != "$top" ]; then
                    printf '%s\037%s\037%s\n' "$real" "$branch" "${real#"$top"/}"
                fi
                path="" ;;
        esac
    done < <(git -C "$root" worktree list --porcelain 2>/dev/null; echo)
    return 0
}

# Open changes across checkouts: the spec_changes columns, then the record's location. An id
# found in several checkouts is listed once: the copy on the branch named after the change wins,
# else the first found.
spec_open_changes() { # root
    local root="$1" path branch loc row closed
    # A worktree branched before a close still carries the record: the root's log has the final word.
    closed=" "
    [ ! -f "$root/specs/log.md" ] || closed=" $(sed -n 's/^- [0-9-]* \([^ :]*\):.*/\1/p' "$root/specs/log.md" | tr '\n' ' ')"
    while IFS="$SPEC_SEP" read -r path branch loc; do
        [ -n "$path" ] || continue
        while IFS= read -r row; do
            [ -n "$row" ] || continue
            printf '%s\037%s\037%s\n' "$row" "$loc" "$([ "${row%%"$SPEC_SEP"*}" = "$branch" ] && echo 1 || echo 0)"
        done < <(spec_changes "$path")
    done < <(spec_checkouts "$root") | awk -F '\037' -v closed="$closed" 'NF {
            own = $NF; r = $0; sub("\037[01]$", "", r)
            if ($(NF - 1) != "." && index(closed, " " $1 " ")) next
            if (!($1 in row)) order[++n] = $1
            else if (!own || mine[$1]) next
            row[$1] = r; mine[$1] = own
        }
        END { for (i = 1; i <= n; i++) print row[order[i]] }'
    return 0
}
