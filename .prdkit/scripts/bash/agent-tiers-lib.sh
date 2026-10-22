#!/usr/bin/env bash
# agent-tiers-lib.sh — resolve agent models from the project tier map and check projections.
#
# Sourced by render-agents.sh, doctor.sh and check-adoption.sh (install-lib.sh must be
# sourced first). Bash 3.2 + POSIX awk; no jq, no network, no kit source checkout.
#
# The tier map lives in .prdkit/memory/agents.md and has two shapes:
#
#   Shared (opt-in) — resolve from a vendored shared tier baseline:
#     Tier baseline: agent-library tiers.json@<commit>
#     Baseline snapshot: .prdkit/agent-library/tiers.json        (optional; this is the default)
#     | Local tier | Library tier |        local tier name -> baseline tier name
#     | Scope | Harness | Model | Variant | Reason |            deviations, each with a reason
#     Banned models: <id>, <id>        (optional; exact ids, so each harness's form is listed)
#     Hand-owned agents: <name>, <name>   (optional; projections kept without a neutral source)
#
#   Legacy literal — a "| Tier | <Harness> model | ... |" table restating concrete ids.
#     Still resolves; the overrides table and banned list work on top of it.
#
# The kit names no models: every id comes from the project's tier map or its vendored
# baseline snapshot. Fenced code blocks and HTML comments in agents.md are never parsed, so
# documentation examples cannot become configuration.

AT_DEFAULT_SNAPSHOT=".prdkit/agent-library/tiers.json"
AT_HARNESSES="claude opencode"

# --- parsing ---------------------------------------------------------------------------

# Tier map -> tab-separated records (one per line):
#   baseline <ref> <line> | baselinebad <text> <line> | snapshot <path> <line>
#   banned <id> <line> | handowned <agent> <line> | map <local> <library> <line>
#   override <scope> <harness> <model> <variant> <reason> <line> <has-reason-column>
#   literal <tier> <claude-model> <opencode-model> <opencode-variant> <line>
at_tier_map_records() {
    [ -f "$1" ] || return 0
    awk '
        function trim(s) { sub(/^[ \t\r]+/, "", s); sub(/[ \t\r]+$/, "", s); return s }
        function clean(s) {
            gsub(/`/, "", s); gsub(/\*\*/, "", s); s = trim(s)
            if (s == "-" || s == "—" || s == "–" || tolower(s) == "none" || tolower(s) == "n/a") s = ""
            return s
        }
        function after_colon(s) { sub(/^[^:]*:/, "", s); return trim(s) }
        BEGIN { OFS = "\037"; incomment = 0; infence = 0; intable = 0; kind = "" }
        {
            line = $0; sub(/\r$/, "", line)
            if (infence) { if (line ~ /^[ \t]*(```|~~~)/) infence = 0; intable = 0; next }
            if (!incomment && line ~ /^[ \t]*(```|~~~)/) { infence = 1; intable = 0; next }
            if (incomment) {
                p = index(line, "-->"); if (!p) next
                line = substr(line, p + 3); incomment = 0
            }
            while ((p = index(line, "<!--")) > 0) {
                rest = substr(line, p + 4); q = index(rest, "-->")
                if (q) line = substr(line, 1, p - 1) substr(rest, q + 3)
                else { line = substr(line, 1, p - 1); incomment = 1 }
            }
            if (line ~ /^[ \t]*\|/) {
                if (line ~ /^[ \t]*\|[ \t:|-]+$/) next          # separator row
                n = split(line, raw, "|"); nc = 0
                for (i = 2; i <= n; i++) {
                    if (i == n && trim(raw[i]) == "") break
                    nc++; cell[nc] = clean(raw[i])
                }
                if (!intable) {
                    intable = 1; kind = ""
                    lt = lb = sc = hc = mc = vc = rc = cm = om = ov = oe = 0
                    for (i = 1; i <= nc; i++) {
                        h = tolower(cell[i])
                        if (h == "local tier") lt = i
                        if (h == "library tier" || h == "baseline tier") lb = i
                        if (h == "scope") sc = i
                        if (h == "harness") hc = i
                        if (h == "model") mc = i
                        if (h ~ /variant/ && h !~ /claude|opencode/) vc = i
                        if (h == "reason") rc = i
                        if (h ~ /claude/ && h ~ /model/) cm = i
                        if (h ~ /opencode/ && h ~ /model/) om = i
                        if (h ~ /opencode/ && h ~ /variant/) ov = i
                        if (h ~ /opencode/ && h ~ /effort/) oe = i
                    }
                    if (!ov) ov = oe
                    if (lt && lb) kind = "map"
                    else if (sc && mc) kind = "override"
                    else if (tolower(cell[1]) == "tier" && (cm || om)) kind = "literal"
                    next
                }
                if (kind == "map" && cell[lt] != "") print "map", tolower(cell[lt]), tolower(cell[lb]), NR
                else if (kind == "override" && (cell[sc] != "" || cell[mc] != "")) {
                    print "override", cell[sc], (hc ? tolower(cell[hc]) : ""), cell[mc], (vc ? cell[vc] : ""), (rc ? cell[rc] : ""), NR, (rc ? 1 : 0)
                } else if (kind == "literal" && cell[1] != "") {
                    print "literal", tolower(cell[1]), (cm ? cell[cm] : ""), (om ? cell[om] : ""), (ov ? cell[ov] : ""), NR
                }
                next
            }
            intable = 0
            plain = line; gsub(/`/, "", plain); gsub(/\*\*/, "", plain)
            sub(/^[ \t]*([-*][ \t]+)?/, "", plain)
            low = tolower(plain)
            if (low ~ /^tier baseline[ \t]*:/) {
                value = after_colon(plain)
                if (match(value, /agent-library[ \t]+tiers\.json@[0-9a-f]+/)) {
                    ref = substr(value, RSTART, RLENGTH); sub(/.*@/, "", ref)
                    if (length(ref) >= 7 && length(ref) <= 40) { print "baseline", ref, NR; next }
                }
                print "baselinebad", value, NR
            } else if (low ~ /^baseline snapshot[ \t]*:/) {
                value = after_colon(plain); sub(/[ \t].*$/, "", value)
                if (value != "") print "snapshot", value, NR
            } else if (low ~ /^banned models[ \t]*:/) {
                value = after_colon(plain); n = split(value, ids, ",")
                for (i = 1; i <= n; i++) { id = trim(ids[i]); if (id != "") print "banned", id, NR }
            } else if (low ~ /^hand-owned agents[ \t]*:/) {
                value = after_colon(plain); n = split(value, ids, ",")
                for (i = 1; i <= n; i++) { id = trim(ids[i]); if (id != "") print "handowned", id, NR }
            }
        }
    ' "$1"
}

# Shared tiers.json snapshot -> "tier <library-tier> <harness> <key> <value>" records, or a
# single "error <message>" record. Accepts objects, strings and string arrays/scalars (which
# are ignored); anything malformed fails closed.
at_tiers_json_records() {
    [ -f "$1" ] || { printf 'error\037file not found\n'; return 0; }
    LC_ALL=C awk '
        function fail(msg) { if (err == "") err = msg " at offset " pos; return 0 }
        function ws() { while (pos <= len && index(" \t\r\n", substr(doc, pos, 1)) > 0) pos++ }
        function str(   c, out) {
            if (substr(doc, pos, 1) != "\"") return fail("expected string")
            pos++; out = ""
            while (pos <= len) {
                c = substr(doc, pos, 1)
                if (c == "\\") {
                    c = substr(doc, pos + 1, 1)
                    if (c == "\"" || c == "\\" || c == "/") { out = out c; pos += 2; continue }
                    return fail("unsupported string escape")
                }
                if (c == "\"") { pos++; S = out; return 1 }
                if (c == "\t" || c == "\n" || c == "\r") return fail("control character in string")
                out = out c; pos++
            }
            return fail("unterminated string")
        }
        function scalar(   start) {
            start = pos
            while (pos <= len && index("-+.0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", substr(doc, pos, 1))) pos++
            if (pos == start) return fail("unexpected character")
            return 1
        }
        function array(   c) {
            pos++; ws()
            if (substr(doc, pos, 1) == "]") { pos++; return 1 }
            while (1) {
                ws(); c = substr(doc, pos, 1)
                if (c == "\"") { if (!str()) return 0 }
                else if (!scalar()) return 0
                ws(); c = substr(doc, pos, 1)
                if (c == ",") { pos++; continue }
                if (c == "]") { pos++; return 1 }
                return fail("expected , or ]")
            }
        }
        function object(depth, path,   key, c) {
            ws()
            if (substr(doc, pos, 1) != "{") return fail("expected object")
            pos++; ws()
            if (substr(doc, pos, 1) == "}") { pos++; return 1 }
            while (1) {
                ws(); if (!str()) return 0
                key = S
                if (index(key, " ") || index(key, "\t")) return fail("key contains whitespace")
                ws()
                if (substr(doc, pos, 1) != ":") return fail("expected :")
                pos++; ws(); c = substr(doc, pos, 1)
                if (c == "{") {
                    if (depth >= 3) return fail("nesting deeper than tier/harness/key")
                    if (!object(depth + 1, (path == "" ? key : path "\037" key))) return 0
                } else if (c == "\"") {
                    if (!str()) return 0
                    if (depth == 3) out[++nout] = "tier\037" path "\037" key "\037" S
                } else if (c == "[") { if (!array()) return 0 }
                else if (!scalar()) return 0
                ws(); c = substr(doc, pos, 1)
                if (c == ",") { pos++; continue }
                if (c == "}") { pos++; return 1 }
                return fail("expected , or }")
            }
        }
        { doc = doc $0 "\n" }
        END {
            len = length(doc); pos = 1; err = ""; nout = 0
            if (object(1, "")) { ws(); if (pos <= len) fail("trailing content") }
            if (err != "") { print "error\037" err; exit }
            for (i = 1; i <= nout; i++) print out[i]
        }
    ' "$1"
}

at_hash_file() { install_hash_stream < "$1"; }

# Frontmatter value of a top-level key (quotes and trailing comments removed).
at_fm_value() { # file key
    awk -v key="$2" '
        NR == 1 { if ($0 != "---") exit; next }
        $0 == "---" { exit }
        index($0, key ":") == 1 {
            v = substr($0, length(key) + 2); sub(/^[ \t]+/, "", v); sub(/[ \t\r]+$/, "", v)
            if (v ~ /^".*"$/ || v ~ /^\047.*\047$/) v = substr(v, 2, length(v) - 2)
            else sub(/[ \t]+#.*$/, "", v)
            print v; exit
        }
    ' "$1"
}

at_fm_line() { # file key -> the raw frontmatter line
    awk -v key="$2" 'NR == 1 { if ($0 != "---") exit; next } $0 == "---" { exit } index($0, key ":") == 1 { print; exit }' "$1"
}

# Body after the frontmatter, with leading blank/generated-marker lines and trailing blank
# lines removed: the comparable prompt content.
at_body_norm() { # file
    awk '
        NR == 1 && $0 == "---" { infm = 1; next }
        infm { if ($0 == "---") infm = 0; next }
        { sub(/\r$/, "") }
        !started && ($0 ~ /^[ \t]*$/ || $0 ~ /^[ \t]*<!--.*([Gg]enerated|rendered from).*-->[ \t]*$/) { next }
        { started = 1; lines[++n] = $0 }
        END { while (n > 0 && lines[n] ~ /^[ \t]*$/) n--; for (i = 1; i <= n; i++) print lines[i] }
    ' "$1"
}

# Leading generated-marker comment lines of a projection body (kept across a re-render).
at_body_markers() { # file
    awk '
        NR == 1 && $0 == "---" { infm = 1; next }
        infm { if ($0 == "---") infm = 0; next }
        $0 ~ /^[ \t]*$/ { next }
        $0 ~ /^[ \t]*<!--.*([Gg]enerated|rendered from).*-->[ \t]*$/ { print; next }
        { exit }
    ' "$1"
}

# --- state ------------------------------------------------------------------------------

# Why a `Baseline snapshot:` path is unsafe, or nothing. The path must be relative, have no
# `..` segment, resolve (through symlinked directories) inside the repository root, and the
# snapshot and its .ref must not themselves be symlinks: --refresh-baseline writes there.
at_snapshot_path_problem() { # root rel
    local root="$1" rel="$2" dir real_root real_dir
    case "$rel" in
        /*) printf 'is absolute'; return 0 ;;
    esac
    case "/$rel/" in
        */../*) printf "has a '..' segment"; return 0 ;;
    esac
    real_root="$(CDPATH="" cd -P "$root" 2>/dev/null && pwd)" || { printf 'has an unreadable repository root'; return 0; }
    dir="$(dirname "$root/$rel")"
    while [ ! -d "$dir" ]; do dir="$(dirname "$dir")"; done
    real_dir="$(CDPATH="" cd -P "$dir" 2>/dev/null && pwd)" || { printf 'has an unreadable directory'; return 0; }
    case "$real_dir/" in
        "$real_root"/*) ;;
        *) printf 'resolves outside the repository'; return 0 ;;
    esac
    if [ -L "$root/$rel" ] || [ -L "$root/$rel.ref" ]; then printf 'is a symlink'; fi
    return 0
}

# Load the tier map + snapshot for a project root into AT_* globals.
at_load() { # root
    local root="$1" rec
    AT_ROOT="$root"; AT_TIERMAP="$root/.prdkit/memory/agents.md"
    AT_MAP="$(at_tier_map_records "$AT_TIERMAP")"
    AT_BASELINE="$(printf '%s\n' "$AT_MAP" | awk -F '\037' '!d && $1 == "baseline" { print $2; d = 1 }')"
    AT_BASELINE_BAD="$(printf '%s\n' "$AT_MAP" | awk -F '\037' '!d && $1 == "baselinebad" { print "line " $3 ": " $2; d = 1 }')"
    rec="$(printf '%s\n' "$AT_MAP" | awk -F '\037' '!d && $1 == "snapshot" { print $2 "\037" $3; d = 1 }')"
    AT_SNAPSHOT_REL=""; AT_SNAPSHOT_LINE=""
    if [ -n "$rec" ]; then
        IFS=$'\037' read -r AT_SNAPSHOT_REL AT_SNAPSHOT_LINE <<EOF
$rec
EOF
    fi
    AT_SNAPSHOT_REL="${AT_SNAPSHOT_REL:-$AT_DEFAULT_SNAPSHOT}"
    AT_SNAPSHOT="$root/$AT_SNAPSHOT_REL"
    AT_SNAP=""; AT_SNAP_ERR=""
    # A snapshot path outside the checkout is never read or written (refresh writes there).
    AT_SNAPSHOT_BAD="$(at_snapshot_path_problem "$root" "$AT_SNAPSHOT_REL")"
    if [ -n "$AT_SNAPSHOT_BAD" ]; then
        AT_SNAPSHOT=""
        if [ -n "$AT_BASELINE" ] || [ -n "$AT_BASELINE_BAD" ]; then AT_SNAP_ERR="unsafe"; fi
    elif [ -n "$AT_BASELINE" ] || [ -n "$AT_BASELINE_BAD" ]; then
        if [ -f "$AT_SNAPSHOT" ]; then
            AT_SNAP="$(at_tiers_json_records "$AT_SNAPSHOT")"
            AT_SNAP_ERR="$(printf '%s\n' "$AT_SNAP" | awk -F '\037' '!d && $1 == "error" { print $2; d = 1 }')"
            [ -z "$AT_SNAP_ERR" ] || AT_SNAP=""
        else
            AT_SNAP_ERR="missing"
        fi
    fi
}

at_snap_value() { # library-tier harness key
    printf '%s\n' "$AT_SNAP" | awk -F '\037' -v t="$1" -v h="$2" -v k="$3" '!d && $1 == "tier" && $2 == t && $3 == h && $4 == k { print $5; d = 1 }'
}
at_snap_has_tier() { printf '%s\n' "$AT_SNAP" | awk -F '\037' -v t="$1" '$1 == "tier" && $2 == t { found = 1 } END { exit !found }'; }

# Compatibility aliases from the kit's former tier names to the shared library names. An
# explicit `| Local tier | Library tier |` row always wins over these.
at_tier_alias() {
    case "$1" in
        frontier) printf 'verydeep' ;;
        high) printf 'deep' ;;
        fast) printf 'light' ;;
        *) printf '%s' "$1" ;;
    esac
}

# Neutral-definition / provenance keys that never belong in a projection's frontmatter:
# OpenCode turns unknown frontmatter keys into model options, so provenance lives in YAML
# comments only.
AT_NEUTRAL_ONLY_KEYS="tier domain harnesses source library-ref library-agent"

at_norm_harness() { # override harness cell -> claude | opencode | * | invalid
    case "$1" in
        ''|'*'|all|any) printf '*' ;;
        claude|'claude code') printf 'claude' ;;
        opencode) printf 'opencode' ;;
        *) printf 'invalid' ;;
    esac
}

# Overrides that apply to (scope, harness): prints "model<TAB>variant" lines in ascending
# precedence (wildcard harness first, then the specific harness).
at_override_for() { # scope harness
    local rec h want scope harness model variant
    for want in '*' "$2"; do
        while IFS= read -r rec; do
            [ -n "$rec" ] || continue
            IFS=$'\037' read -r _ scope harness model variant _ <<EOF
$rec
EOF
            [ "$scope" = "$1" ] || continue
            h="$(at_norm_harness "$harness")"
            [ "$h" = "$want" ] || continue
            printf '%s\037%s\n' "$model" "$variant"
        done <<EOF
$(printf '%s\n' "$AT_MAP" | awk -F '\037' '$1 == "override"')
EOF
        [ "$want" = "$2" ] && break
    done
}

# Resolve (agent, tier, harness) -> AT_R_MODEL, AT_R_VARIANT, AT_R_SOURCE; AT_R_ERR on failure.
at_resolve() { # agent tier harness
    local agent="$1" tier="$2" harness="$3" lib="" model="" variant="" src="" scope m v variant_oc
    AT_R_MODEL=""; AT_R_VARIANT=""; AT_R_SOURCE=""; AT_R_ERR=""
    if [ -n "$AT_BASELINE" ]; then
        lib="$(printf '%s\n' "$AT_MAP" | awk -F '\037' -v t="$tier" '!d && $1 == "map" && $2 == t { print $3; d = 1 }')"
        if [ -z "$lib" ] && at_snap_has_tier "$tier"; then lib="$tier"; fi
        if [ -z "$lib" ]; then
            lib="$(at_tier_alias "$tier")"
            at_snap_has_tier "$lib" || lib=""
        fi
        if [ -n "$lib" ]; then
            model="$(at_snap_value "$lib" "$harness" model)"
            variant="$(at_snap_value "$lib" "$harness" variant)"
            [ -z "$model" ] || src="baseline $lib"
        fi
    else
        IFS=$'\037' read -r _ _ m v variant_oc _ <<EOF
$(printf '%s\n' "$AT_MAP" | awk -F '\037' -v t="$tier" '!d && $1 == "literal" && $2 == t { print; d = 1 }')
EOF
        case "$harness" in
            claude) model="$m"; variant="" ;;
            opencode) model="$v"; variant="${variant_oc:-}" ;;
        esac
        [ -z "$model" ] || src="literal $tier"
    fi
    for scope in "tier:$tier" "agent:$agent"; do
        while IFS=$'\037' read -r m v; do
            [ -n "$m$v" ] || continue
            [ -z "$m" ] || model="$m"
            [ -z "$v" ] || variant="$v"
            src="override $scope"
        done <<EOF
$(at_override_for "$scope" "$harness")
EOF
    done
    [ "$harness" = opencode ] || variant=""
    if [ -z "$model" ]; then
        if [ -n "$AT_BASELINE" ] && [ -z "$lib" ]; then
            AT_R_ERR="tier '$tier' has no Local tier -> Library tier mapping"
        elif [ -n "$AT_BASELINE" ]; then
            AT_R_ERR="baseline tier '$lib' has no $harness model"
        else
            AT_R_ERR="tier '$tier' has no $harness model in the tier map"
        fi
        return 1
    fi
    AT_R_MODEL="$model"; AT_R_VARIANT="$variant"; AT_R_SOURCE="$src"
}

at_is_banned() { # model
    [ -n "$1" ] || return 1
    printf '%s\n' "$AT_MAP" | awk -F '\037' -v m="$1" '$1 == "banned" && $2 == m { found = 1 } END { exit !found }'
}

# A neutral definition's optional `harnesses:` frontmatter -> AT_DH_SET (true when the line is
# present), AT_DH_LIST (space-separated known names) and AT_DH_ERR (why it is malformed). Only
# a bracketed, non-empty list of projection harnesses is valid; anything else is a finding,
# never silently read as "every harness".
at_def_harnesses() { # def
    local line value inner name out=""
    local -a names
    AT_DH_SET=false; AT_DH_LIST=""; AT_DH_ERR=""
    line="$(at_fm_line "$1" harnesses)"
    [ -n "$line" ] || return 0
    AT_DH_SET=true
    value="$(printf '%s' "${line#harnesses:}" | tr -d '\r' | sed -e 's/[[:space:]][[:space:]]*#.*$//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    case "$value" in
        \[*\]) ;;
        *) AT_DH_ERR="'harnesses:' must be a bracketed list such as [opencode] or [claude, opencode], not '$value'"; return 0 ;;
    esac
    inner="${value#[}"; inner="${inner%]}"
    IFS=', ' read -r -a names <<EOF
$inner
EOF
    for name in ${names[@]+"${names[@]}"}; do
        name="$(printf '%s' "$name" | tr -d "\"'")"
        [ -n "$name" ] || continue
        case " $AT_HARNESSES " in
            *" $name "*) out="$out $name" ;;
            *) AT_DH_ERR="'harnesses:' names unknown harness '$name' (agent projections exist for: $(printf '%s' "$AT_HARNESSES" | sed 's/ /, /g'))"; return 0 ;;
        esac
    done
    [ -n "$out" ] || { AT_DH_ERR="'harnesses:' is an empty list; omit the line to project into every configured harness"; return 0; }
    AT_DH_LIST="${out# }"
}

# Harnesses a neutral definition projects into: configured in the manifest or with an
# existing agents directory, filtered by the definition's optional `harnesses:` list. A
# malformed list targets nothing (at_lint_definitions reports it; the render refuses).
at_targets() { # root def
    local root="$1" def="$2" configured harness out=""
    at_def_harnesses "$def"
    [ -z "$AT_DH_ERR" ] || return 0
    configured="$(install_manifest_value "$root/.prdkit/.manifest" harnesses 2>/dev/null || true)"
    for harness in $AT_HARNESSES; do
        case " $(printf '%s' "$configured" | tr ',' ' ') " in
            *" $harness "*) ;;
            *) [ -d "$root/.$harness/agents" ] || continue ;;
        esac
        if $AT_DH_SET; then
            case " $AT_DH_LIST " in *" $harness "*) ;; *) continue ;; esac
        fi
        out="$out $harness"
    done
    printf '%s' "${out# }"
}

at_is_hand_owned() { # agent
    printf '%s\n' "$AT_MAP" | awk -F '\037' -v a="$1" '$1 == "handowned" && $2 == a { found = 1 } END { exit !found }'
}

# A neutral definition's description value when the renderer owns it: a single-line scalar.
# Block scalars (`>`/`|`) stay project-owned in each projection and are not compared.
at_owned_description() { # def
    local value
    value="$(at_fm_value "$1" description)"
    case "$value" in '>'*|'|'*) return 0 ;; esac
    printf '%s' "$value"
}

# --- findings ---------------------------------------------------------------------------

# Static tier-map findings. Every check keys off syntax the project opted into, so a legacy
# literal-only map without a banned list produces none.
at_lint() { at_lint_map; at_lint_definitions; at_lint_projection_bans; }

# Neutral-definition findings that make the render target set ambiguous: a `harnesses:` line
# that is not a bracketed list of known projection harnesses.
at_lint_definitions() {
    local def name
    for def in "$AT_ROOT"/.prdkit/agents/*.md; do
        [ -f "$def" ] || continue
        name="$(basename "$def" .md)"
        at_def_harnesses "$def"
        [ -z "$AT_DH_ERR" ] || printf "Agent '%s': .prdkit/agents/%s.md %s\n" "$name" "$name" "$AT_DH_ERR"
    done
}

# Once a shared baseline is declared every projection must be rendered from a neutral
# definition or explicitly hand-owned: an unowned file in .claude/agents or .opencode/agents
# (no definition, or one whose harnesses: list excludes that harness) would otherwise keep
# its model and body with nothing checking either.
at_ownership_findings() {
    local harness proj name def why
    for harness in $AT_HARNESSES; do
        for proj in "$AT_ROOT/.$harness/agents"/*.md; do
            [ -f "$proj" ] || continue
            name="$(basename "$proj" .md)"
            def="$AT_ROOT/.prdkit/agents/$name.md"
            if [ -f "$def" ]; then
                case " $(at_targets "$AT_ROOT" "$def") " in *" $harness "*) continue ;; esac
                at_def_harnesses "$def"
                [ -z "$AT_DH_ERR" ] || continue   # reported by at_lint_definitions
                why="its neutral definition's harnesses: list excludes $harness"
            else
                why="no neutral definition .prdkit/agents/$name.md"
            fi
            at_is_hand_owned "$name" && continue
            printf "Agent projection .%s/agents/%s.md is unowned (%s), so nothing checks its model or body; add the definition and run render-agents.sh, remove the file, or list '%s' under 'Hand-owned agents:' in .prdkit/memory/agents.md\n" "$harness" "$name" "$why" "$name"
        done
    done
}

# Projections whose current model is banned. Kept apart from at_lint_map: render-agents.sh
# repairs these (it rewrites the model) once the map itself is clean, so they must not block
# the render that would fix them; --check, doctor and the adoption gate still report them.
at_lint_projection_bans() {
    local proj model
    printf '%s\n' "$AT_MAP" | awk -F '\037' '$1 == "banned" { f = 1 } END { exit !f }' || return 0
    for proj in "$AT_ROOT"/.claude/agents/*.md "$AT_ROOT"/.opencode/agents/*.md; do
        [ -f "$proj" ] || continue
        model="$(at_fm_value "$proj" model)"
        ! at_is_banned "$model" || printf "Agent projection %s uses banned model '%s'\n" "${proj#"$AT_ROOT"/}" "$model"
    done
}

# Tier map, override and snapshot findings (everything except projection content).
at_lint_map() {
    local rec kind a b c d e f model h snap_ref snap_sha actual
    # Only an opted-in map (a baseline line, valid or not, or an explicit snapshot line) reads
    # the snapshot, so only then is an unsafe path a finding; --refresh-baseline refuses it always.
    if [ -n "$AT_SNAPSHOT_BAD" ] && { [ -n "$AT_BASELINE" ] || [ -n "$AT_BASELINE_BAD" ] || [ -n "$AT_SNAPSHOT_LINE" ]; }; then
        if [ -n "$AT_SNAPSHOT_LINE" ]; then
            printf "Tier map line %s: 'Baseline snapshot:' path '%s' %s; it must be a relative path inside the repository\n" "$AT_SNAPSHOT_LINE" "$AT_SNAPSHOT_REL" "$AT_SNAPSHOT_BAD"
        else
            printf "Tier map: default baseline snapshot path '%s' %s; it must be a relative path inside the repository\n" "$AT_SNAPSHOT_REL" "$AT_SNAPSHOT_BAD"
        fi
    fi
    [ -z "$AT_BASELINE_BAD" ] || printf "Tier map: 'Tier baseline:' must read 'agent-library tiers.json@<7-40 hex commit>' (%s)\n" "$AT_BASELINE_BAD"
    if [ -n "$AT_BASELINE" ] && [ "$AT_SNAP_ERR" != unsafe ]; then
        if [ "$AT_SNAP_ERR" = missing ]; then
            printf 'Tier map: baseline snapshot %s is missing (vendor it: render-agents.sh --refresh-baseline <agent-library checkout>)\n' "$AT_SNAPSHOT_REL"
        elif [ -n "$AT_SNAP_ERR" ]; then
            printf 'Tier map: baseline snapshot %s is not valid tiers JSON (%s)\n' "$AT_SNAPSHOT_REL" "$AT_SNAP_ERR"
        elif [ ! -f "$AT_SNAPSHOT.ref" ]; then
            printf 'Tier map: baseline snapshot %s has no provenance file %s.ref (re-vendor with render-agents.sh --refresh-baseline)\n' "$AT_SNAPSHOT_REL" "$AT_SNAPSHOT_REL"
        else
            snap_ref="$(sed -n 's/^library-ref:[[:space:]]*//p' "$AT_SNAPSHOT.ref" | head -n1 | tr -d '[:space:]')"
            snap_sha="$(sed -n 's/^sha256:[[:space:]]*//p' "$AT_SNAPSHOT.ref" | head -n1 | tr -d '[:space:]')"
            case "$snap_ref" in
                "$AT_BASELINE"*) ;;
                *) printf 'Tier map: baseline names tiers.json@%s but the vendored snapshot is from %s (refresh the snapshot or the baseline line)\n' "$AT_BASELINE" "${snap_ref:-an unknown commit}" ;;
            esac
            actual="$(at_hash_file "$AT_SNAPSHOT")"
            [ "$actual" = "$snap_sha" ] || printf 'Tier map: vendored snapshot %s was edited after vendoring (sha256 mismatch); put deviations in the overrides table instead\n' "$AT_SNAPSHOT_REL"
        fi
    fi
    while IFS= read -r rec; do
        [ -n "$rec" ] || continue
        IFS=$'\037' read -r kind a b c d e f _ <<EOF
$rec
EOF
        case "$kind" in
            literal)
                if [ -n "$AT_BASELINE" ]; then
                    printf "Tier map line %s: declares a Tier baseline but restates literal model ids for tier '%s'; remove the literal table and record each deviation as an override with a Reason\n" "$e" "$a"
                fi
                for model in "$b" "$c"; do
                    ! at_is_banned "$model" || printf "Tier map line %s: banned model '%s'\n" "$e" "$model"
                done ;;
            map)
                [ -n "$AT_BASELINE" ] || printf 'Tier map line %s: a Local tier -> Library tier mapping needs a Tier baseline line\n' "$c"
                if [ -n "$AT_BASELINE" ] && [ -n "$AT_SNAP" ] && ! at_snap_has_tier "$b"; then
                    printf "Tier map line %s: library tier '%s' is not in the baseline snapshot\n" "$c" "$b"
                fi ;;
            override)
                h="$(at_norm_harness "$b")"
                case "$a" in
                    tier:?*|agent:?*) ;;
                    *) printf "Tier map line %s: override scope '%s' must be tier:<local tier> or agent:<agent name>\n" "$f" "$a" ;;
                esac
                [ "$h" != invalid ] || printf "Tier map line %s: override harness '%s' must be claude, opencode or *\n" "$f" "$b"
                [ -n "$c$d" ] || printf 'Tier map line %s: override names neither a model nor a variant\n' "$f"
                [ -n "$e" ] || printf "Tier map line %s: override for '%s' has no Reason; every deviation from the shared map records why\n" "$f" "$a"
                case "$a" in
                    agent:?*) [ -f "$AT_ROOT/.prdkit/agents/${a#agent:}.md" ] || printf "Tier map line %s: override names unknown agent '%s'\n" "$f" "${a#agent:}" ;;
                esac
                ! at_is_banned "$c" || printf "Tier map line %s: banned model '%s'\n" "$f" "$c" ;;
        esac
    done <<EOF
$AT_MAP
EOF
    if [ -n "$AT_SNAP" ]; then
        while IFS=$'\037' read -r _ a b c d; do
            [ "$c" = model ] || continue
            ! at_is_banned "$d" || printf "Tier map: baseline tier '%s' resolves %s to banned model '%s'; override it with a Reason\n" "$a" "$b" "$d"
        done <<EOF
$(printf '%s\n' "$AT_SNAP" | awk -F '\037' '$1 == "tier"')
EOF
    fi
}

# Projection findings for every neutral definition and target harness.
#   include_missing  report absent projections (doctor reports those itself)
at_projection_findings() { # include_missing
    local include_missing="$1" def name tier harness proj current variant key desc
    for def in "$AT_ROOT"/.prdkit/agents/*.md; do
        [ -f "$def" ] || continue
        name="$(basename "$def" .md)"
        tier="$(at_fm_value "$def" tier)"
        for harness in $(at_targets "$AT_ROOT" "$def"); do
            proj="$AT_ROOT/.$harness/agents/$name.md"
            if ! at_resolve "$name" "$tier" "$harness"; then
                printf "Agent '%s': no %s model resolves (%s)\n" "$name" "$harness" "$AT_R_ERR"
                continue
            fi
            if [ ! -f "$proj" ]; then
                [ "$include_missing" != true ] || printf "Agent '%s': no projection in .%s/agents/ (run render-agents.sh)\n" "$name" "$harness"
                continue
            fi
            current="$(at_fm_value "$proj" model)"
            [ "$current" = "$AT_R_MODEL" ] ||
                printf "Agent '%s': .%s/agents/%s.md model '%s' differs from resolved '%s' (%s); run render-agents.sh\n" "$name" "$harness" "$name" "${current:-<none>}" "$AT_R_MODEL" "$AT_R_SOURCE"
            if [ "$harness" = opencode ]; then
                for key in $AT_NEUTRAL_ONLY_KEYS; do
                    [ -z "$(at_fm_line "$proj" "$key")" ] ||
                        printf "Agent '%s': .opencode/agents/%s.md carries neutral/provenance key '%s:' as a field (OpenCode passes unknown keys to the model as options); keep provenance in YAML comments\n" "$name" "$name" "$key"
                done
                variant="$(at_fm_value "$proj" variant)"
                [ "$variant" = "$AT_R_VARIANT" ] ||
                    printf "Agent '%s': .%s/agents/%s.md variant '%s' differs from resolved '%s' (%s); run render-agents.sh\n" "$name" "$harness" "$name" "${variant:-<none>}" "${AT_R_VARIANT:-<none>}" "$AT_R_SOURCE"
            fi
            desc="$(at_owned_description "$def")"
            [ -z "$desc" ] || [ "$(at_fm_value "$proj" description)" = "$desc" ] ||
                printf "Agent '%s': .%s/agents/%s.md description differs from the neutral definition; run render-agents.sh\n" "$name" "$harness" "$name"
            [ "$(at_body_norm "$proj")" = "$(at_body_norm "$def")" ] ||
                printf "Agent '%s': .%s/agents/%s.md prompt body differs from the neutral definition; run render-agents.sh\n" "$name" "$harness" "$name"
        done
    done
}

# Entry point for doctor/adoption/render --check.
#   agent_tier_findings <root> [--include-missing] [--always-projections]
# Projection checks run when the tier map declares a shared baseline (the opt-in), or always
# with --always-projections (render-agents.sh --check). Legacy literal maps keep their
# pre-renderer doctor behavior unless the project runs the renderer's check explicitly.
agent_tier_findings() {
    local root="$1" include_missing=false always=false
    shift
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --include-missing) include_missing=true ;;
            --always-projections) always=true ;;
        esac
        shift
    done
    at_load "$root"
    at_lint
    if $always || [ -n "$AT_BASELINE" ]; then
        if [ -z "$AT_BASELINE" ] || [ -n "$AT_SNAP" ]; then
            at_projection_findings "$include_missing"
        fi
    fi
    [ -z "$AT_BASELINE" ] || at_ownership_findings
}
