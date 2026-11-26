#!/usr/bin/env bash
# bounds.sh — The constraints, non-goals and qualities a change must respect. Read-only.
#
# Usage: bounds.sh <change-id> | bounds.sh --all
#
# Scope starts from the change record's header (`**Parts**: P2 · **Journeys**: J-01`) in
# specs/changes/<change-id>.md, widened through the parts table: a named part adds the journeys
# its row cites, a named journey adds the parts whose rows cite it. In scope: every live
# non-goal; a live constraint whose Scope field names a part or journey in scope, makes an
# exception, says all or every of something unnamed, or names none; a live quality that cites one in scope, or cites none. A
# record naming no parts or journeys gets everything, as does --all. Items marked for a stage
# after the current one (`from: ga`; lifecycle.md, Now) are listed apart: built only on the
# owner's direction (spec.md, Lifecycle). Over-inclusion is the safe side: the agent marks each item in the
# record's Bounds section and the reviewer checks the diff against this list.
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/spec-lib.sh"
bounds_for_root() { # materialized immutable inputs; same computation as the installed command
local root="$1"; shift
usage='Usage: bounds.sh <change-id> | bounds.sh --all'
case "${1:-}" in
    -h|--help) echo "$usage"; exit 0 ;;
    '') echo "$usage" >&2; exit 2 ;;
esac
scope=""
if [ "$1" != "--all" ]; then
    record="$root/specs/changes/$1.md"
    [ -f "$record" ] || { echo "No change record at specs/changes/$1.md" >&2; exit 2; }
    # The header's Parts and Journeys fields, before the first section; their IDs are read below.
    spec_change_metadata "$record" tags >/dev/null || return 1
    scope="$(set -o pipefail; awk "$SPEC_CHANGE_TAG_AWK"'
        { $0 = change_without_tags($0) }
        /^## /{exit} {
        s = $0; while (match(s, /\*\*(Parts|Journeys)\*\*:[^*]*/)) { print substr(s, RSTART, RLENGTH); s = substr(s, RSTART + RLENGTH) }
    }' "$record" | tr '\n' ' ')" || return 1
fi
spec_lifecycle_require "$root" || return
now="$SPEC_STAGE_NOW"

files=()
listed="$(verify_lines spec_files "$root")" || return 1
while IFS= read -r file; do [ -z "$file" ] || files+=("$file"); done <<< "$listed"
[ "${#files[@]}" -gt 0 ] || { echo "No spec files under specs/" >&2; exit 2; }

(cd "$root" && awk -v header="$scope" -v now="$now" -v what="${1/--all/every change}" \
    -v stage_order="$SPEC_STAGE_ORDER" -v stage_custom="$SPEC_STAGE_CUSTOM" "$SPEC_SKIP_AWK $SPEC_STAGE_AWK"'
    # IDs in text, with word boundaries: a part counts only when the parts table defines it (so
    # P95 or HTTP2 never does), and a part behavior (P2.B-07) cites its part. A range
    # (`J-01 to J-05`, `P1–P3`) cites every ID in it.
    # Whether one character continues a word. index(), never a regex: macOS awk counts bytes, and
    # a regex over the lone byte of a multibyte character (the en dash in P1–P3) aborts it.
    function wordch(c) { return c != "" && index("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_", c) > 0 }
    function ids(text,   out, tok, pre, post, end, k, lo, hi, id, rng, pad) {
        out = " "
        while (match(text, /P[0-9]+(\.[0-9]+)*|J-[0-9][0-9]+/)) {
            tok = substr(text, RSTART, RLENGTH)
            pre = RSTART > 1 ? substr(text, RSTART - 1, 1) : ""
            post = substr(text, RSTART + RLENGTH, 1)
            text = substr(text, RSTART + RLENGTH)
            if (wordch(pre) || wordch(post)) continue
            rng = ""
            if (match(text, /^[[:space:]]*(to|through|-|–|—|\.\.)[[:space:]]*(P[0-9]+(\.[0-9]+)*|J-[0-9][0-9]+)/)) {
                end = substr(text, RSTART, RLENGTH); match(end, /(P[0-9]+(\.[0-9]+)*|J-[0-9][0-9]+)$/); end = substr(end, RSTART)
                if (!index(tok, ".") && !index(end, ".") && substr(end, 1, 1) == substr(tok, 1, 1)) {
                    lo = tok; sub(/^[^0-9]*/, "", lo); hi = end; sub(/^[^0-9]*/, "", hi)
                    pad = length(lo); if (lo + 0 > hi + 0) { k = lo; lo = hi; hi = k }
                    for (k = lo + 1; k < hi + 0 && k <= lo + 99; k++) {
                        id = (tok ~ /^J/) ? sprintf("J-%0" pad "d", k) : "P" k
                        if (id ~ /^J/ || (id in part)) rng = rng id " "
                    }
                }
            }
            if (tok ~ /^P/ && !(tok in part)) { out = out rng; continue }
            out = out tok " " rng
        }
        return out
    }
    # The Scope field runs to the next `Label:` field, inline or as a sub-bullet.
    function scope_field(body,   s) {
        if (!match(body, /Scope:/)) return ""
        s = substr(body, RSTART + 6)
        sub(/([.;][[:space:]]+|[[:space:]][-*+][[:space:]]+)[A-Z][A-Za-z ]*:.*/, "", s)
        return s
    }
    # A Scope binds everywhere when it makes an exception (`all parts but P3`) or uses all, every
    # or any without an ID right after it (`P4 and all journeys`, not `every P2 endpoint`).
    function everywhere(s,   t, w, n, i, j, part_token) {
        t = tolower(s)
        if (t ~ /(^|[^a-z])(except|excluding|other than|outside|but|apart from|besides)([^a-z]|$)/) return 1
        gsub(/\.+([[:space:]]|$)/, " ", t); gsub(/[^a-z0-9.-]+/, " ", t); n = split(t, w, " ")
        for (i = 1; i <= n; i++) {
            if (w[i] != "all" && w[i] != "every" && w[i] != "any") continue
            j = i + 1; if (w[j] == "of") j++; if (w[j] == "the") j++
            if ((w[j] == "it" || w[j] == "them") && i > 1 && (w[i-1] ~ /^j-[0-9][0-9]+$/ || (toupper(w[i-1]) in part))) continue
            part_token = toupper(w[j]); sub(/\.[BI]-[0-9]+$/, "", part_token)
            if (!(w[j] ~ /^j-[0-9][0-9]+$/ || (part_token in part))) return 1
        }
        return 0
    }
    function binds(k, body,   s, n, i, t, cited) {
        if (wide == " " || k == "N") return 1
        s = (k == "C") ? scope_field(body) : body
        if (k == "C" && everywhere(s)) return 1
        n = split(ids(s), t, " "); cited = 0
        for (i = 1; i <= n; i++) { cited = 1; if (index(wide, " " t[i] " ")) return 1 }
        return !cited
    }
    FNR == 1 { fence = 0; comment = 0; cur = 0; gap = 0 }
    { if (spec_skip($0)) next }
    /^\|[[:space:]]*P[0-9]+(\.[0-9]+)?[[:space:]]/ { id = $0; sub(/^\|[[:space:]]*/, "", id); sub(/[[:space:]].*/, "", id); part[id] = 1; row[id] = $0 }
    # An item runs from its bullet to the next heading, table row, or bullet no deeper than its
    # own; after a blank line, a paragraph no deeper than its bullet ends it too.
    /^[[:space:]]*$/ { gap = 1; next }
    cur && gap && !/^[[:space:]]*[-*+][[:space:]]/ { match($0, /^[[:space:]]*/); if (RLENGTH <= indent[cur]) cur = 0 }
    { gap = 0 }
    /^(#|\|)/ { cur = 0 }
    cur && /^[[:space:]]*[-*+][[:space:]]/ { match($0, /^[[:space:]]*/); if (RLENGTH <= indent[cur]) cur = 0 }
    /^[[:space:]]*[-*+][[:space:]]+\*\*[CNQ]-[0-9][0-9]+[[:space:]*]/ {
        match($0, /^[[:space:]]*/); n_items++; cur = n_items; indent[cur] = RLENGTH
        line = $0; sub(/^[[:space:]]*[-*+][[:space:]]+\*\*/, "", line)
        iid[cur] = line; sub(/[[:space:]*].*/, "", iid[cur])
        ititle[cur] = line; sub(/^[^[:space:]*]+[[:space:]]*/, "", ititle[cur]); sub(/\*\*.*/, "", ititle[cur])
        rest = $0; sub(/^[^*]*\*\*[^*]*\*\*/, "", rest); istage[cur] = ""
        if (match(rest, /^[[:space:]]*\([^)]*\)/)) istage[cur] = stage_from(substr(rest, RSTART, RLENGTH))
        iretired[cur] = (tolower($0) ~ /retired:/); ibody[cur] = $0; ifile[cur] = FILENAME
        next
    }
    cur { ibody[cur] = ibody[cur] " " $0 }
    END {
        # The IDs the header names, widened through the parts table one step each way. A part
        # missing from the parts table is reported, not silently dropped.
        n = split(ids(header), t, " "); named = " "
        for (i = 1; i <= n; i++) if (!index(named, " " t[i] " ")) named = named t[i] " "
        h = header; unknown = ""
        while (match(h, /P[0-9]+(\.[0-9]+)*/)) {
            tok = substr(h, RSTART, RLENGTH); pre = RSTART > 1 ? substr(h, RSTART - 1, 1) : ""
            post = substr(h, RSTART + RLENGTH, 1); h = substr(h, RSTART + RLENGTH)
            if (!wordch(pre) && !wordch(post) && !(tok in part) && !index(unknown " ", " " tok " ")) unknown = unknown " " tok
        }
        wide = named " "
        if (named != " ") for (p in part) {
            if (index(named " ", " " p " ")) {
                n = split(ids(row[p]), t, " ")
                for (i = 1; i <= n; i++) if (!index(wide, " " t[i] " ")) wide = wide t[i] " "
            } else {
                n = split(named, t, " ")
                for (i = 1; i <= n; i++) if (t[i] ~ /^J/ && index(ids(row[p]), " " t[i] " ") && !index(wide, " " p " ")) wide = wide p " "
            }
        }
        if (named == " ") wide = " "
        nowrank = stage_rank(now)
        for (c = 1; c <= n_items; c++) {
            if (iretired[c]) continue
            k = substr(iid[c], 1, 1)
            if (!binds(k, ibody[c])) continue
            if (nowrank && stage_rank(istage[c]) > nowrank) later = later "- " iid[c] " " ititle[c] " (from: " istage[c] "; " ifile[c] ")\n"
            else out[k] = out[k] "- " iid[c] " " ititle[c] " (" ifile[c] ")\n"
        }
        label = wide; gsub(/^ +| +$/, "", label); gsub(/ +/, " ", label)
        printf "Bounds for %s (in scope: %s)\n", what, (label == "" ? "everything" : label)
        if (unknown != "") printf "Not in the parts table, so not in scope:%s\n", unknown
        if (now != "") printf "Stage now: %s\n", now
        printf "Constraints:\n%s", (out["C"] != "" ? out["C"] : "- none\n")
        printf "Non-goals:\n%s", (out["N"] != "" ? out["N"] : "- none\n")
        printf "Qualities:\n%s", (out["Q"] != "" ? out["Q"] : "- none\n")
        if (later != "") printf "Later stages (built only on owner direction; spec.md, Lifecycle):\n%s", later
    }
' "${files[@]}") || return 1
echo "Mark each in the record's Bounds: holds (how), or conflict (to the owner inbox; never resolved by the agent)."
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    set -euo pipefail
    root="$(get_repo_root)"
    require_consumer_root "$root" bounds.sh
    # The CLI scopes live changes. Review packaging calls bounds_for_root directly and
    # can intentionally use a historical record to reconstruct its bounds.
    if is_change_id "${1:-}"; then
        archive_index="$(spec_archive_log_index "$root")" || exit "$?"
        if spec_archive_log_covers "$archive_index" "$1"; then
            printf '%s is closed: its number is condensed in .prdkit/history/archive.md\n' "$1" >&2
            exit 2
        fi
    fi
    bounds_for_root "$root" "$@"
fi
