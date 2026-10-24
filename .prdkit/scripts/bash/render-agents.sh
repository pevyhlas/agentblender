#!/usr/bin/env bash
# render-agents.sh — deterministic native agent projections from neutral definitions.
#
# Reads .prdkit/agents/<name>.md (role, tier, prompt body) and the tier map in
# .prdkit/memory/agents.md, resolves each agent's model/variant per harness, and writes
# .claude/agents/<name>.md and .opencode/agents/<name>.md. Resolution order (later wins):
#
#   1. Shared baseline — `Tier baseline: agent-library tiers.json@<commit>` plus the vendored
#      snapshot (default .prdkit/agent-library/tiers.json). A tier resolves through an explicit
#      `| Local tier | Library tier |` row, else by its own name when it is a library tier
#      (verydeep/deep/standard/light), else through the compatibility aliases
#      frontier->verydeep, high->deep, fast->light. Without a baseline line, a legacy literal
#      `| Tier | <Harness> model | ... |` table is used instead.
#   2. Overrides `| Scope | Harness | Model | Variant | Reason |`: `tier:<tier>` rows, then
#      `agent:<name>` rows; a specific harness beats `*`. Every override needs a Reason.
#
# The renderer owns only `model` (both harnesses), `variant` (OpenCode), `description` (copied
# from the neutral definition when that is a single-line value; a block scalar is copied whole
# into a new projection, then stays project-owned) and the prompt body. Every other frontmatter
# key of an existing projection (tools, permissions, mode, colour, memory, generated-header
# comments) is project-owned native metadata and is preserved, as are leading generated-marker
# comment lines in the body.
# A neutral definition may limit its harnesses with a bracketed `harnesses: [claude, opencode]`
# list; any other form is a finding. Once a Tier baseline is declared, a projection with no
# neutral definition (or excluded by its harnesses: list) is a finding unless agents.md lists
# it under `Hand-owned agents:`. A missing projection is created
# with name, description, model (and `mode: subagent` + variant for OpenCode); add native
# extras by hand afterwards and they survive later renders. Files are rewritten only when
# their content changes. Claude effort is not projected (session/native default). Neutral or
# provenance keys (tier, domain, harnesses, source, library-ref, library-agent) are removed
# from OpenCode frontmatter, which would otherwise pass them to the model as options.
#
# Usage:
#   render-agents.sh                      write/refresh projections (fails closed: nothing is
#                                         written while the tier map, overrides or snapshot
#                                         have findings; projections that use a banned id are
#                                         rewritten, not a blocker; a failed write exits 1)
#   render-agents.sh --check [--json]     drift = exit 1: tier-map findings, unresolvable
#                                         tiers, malformed harnesses: lists, missing or
#                                         unowned projections, model/variant/description/
#                                         body drift
#   render-agents.sh --resolve [--json]   print agent, harness, tier, model, variant, source
#   render-agents.sh --refresh-baseline <agent-library checkout> [--library-ref <git-ref>]
#                                         vendor tiers.json + provenance and update the
#                                         Tier baseline line (ref default: clean HEAD; a
#                                         given ref must resolve to a commit)
#
# Exit 0: success / no drift; 1: findings or drift; 2: usage error.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/install-lib.sh"
source "$SCRIPT_DIR/agent-tiers-lib.sh"

usage() { sed -n '/^# Usage:/,/^# Exit/p' "$0" | sed 's/^# \{0,1\}//'; }

# Every staged file lives in one private temp directory, removed on exit, error or interrupt.
RA_TMP=""
ra_cleanup() { if [ -n "$RA_TMP" ]; then rm -rf "$RA_TMP"; fi; }
trap ra_cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
ra_tmpdir() {
    [ -z "$RA_TMP" ] || return 0
    RA_TMP="$(mktemp -d)" || { echo 'render-agents.sh: cannot create a temporary directory' >&2; exit 1; }
}

mode=write json=false library="" library_ref=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --check) mode=check ;;
        --resolve) mode=resolve ;;
        --json) json=true ;;
        --refresh-baseline)
            mode=refresh; shift
            [ "$#" -gt 0 ] || { echo 'render-agents.sh: --refresh-baseline needs an agent-library checkout path' >&2; exit 2; }
            library="$1" ;;
        --library-ref)
            shift
            [ "$#" -gt 0 ] || { echo 'render-agents.sh: --library-ref needs a git ref' >&2; exit 2; }
            case "$1" in
                -*) echo "render-agents.sh: --library-ref must name a commit, not an option: $1" >&2; exit 2 ;;
            esac
            library_ref="$1" ;;
        -h|--help) usage; exit 0 ;;
        *) echo "render-agents.sh: unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done
[ -z "$library_ref" ] || [ "$mode" = refresh ] || { echo 'render-agents.sh: --library-ref only applies to --refresh-baseline' >&2; exit 2; }

root="$(get_repo_root)"
require_consumer_root "$root" render-agents.sh

emit_findings() { # json-flag finding...
    local as_json="$1"; shift
    if $as_json; then
        local result=pass; [ "$#" -eq 0 ] || result=fail
        printf '{"result":"%s","findings":%s}\n' "$result" "$(json_array "$@")"
    else
        local f
        for f in "$@"; do printf '✗ %s\n' "$f"; done
        if [ "$#" -eq 0 ]; then printf '✓ Agent projections match the resolved tier map\n'
        else printf '✗ Agent tier map / projections: %s finding(s)\n' "$#"; fi
    fi
}

collect() { # args to agent_tier_findings -> FINDINGS array
    FINDINGS=()
    local f
    while IFS= read -r f; do [ -z "$f" ] || FINDINGS+=("$f"); done < <(agent_tier_findings "$root" "$@")
}

# --- writing ---------------------------------------------------------------------------

# Copy a fully staged file onto dest. An existing file is rewritten in place, keeping its
# mode, owner and inode (a symlinked projection stays a symlink); a new file is created by
# the shell with the umask's default mode (0644 under umask 022), never mktemp's 0600. A
# failed write returns 1 and removes a partially created new file.
ra_install() { # staged dest
    local staged="$1" dest="$2" existed=false
    [ ! -e "$dest" ] || existed=true
    if ! $existed && ! mkdir -p "$(dirname "$dest")" 2>/dev/null; then
        echo "render-agents.sh: cannot create the directory for ${dest#"$root"/}" >&2; return 1
    fi
    if { cat "$staged" > "$dest"; } 2>/dev/null; then return 0; fi
    $existed || rm -f "$dest"
    echo "render-agents.sh: cannot write ${dest#"$root"/}" >&2
    return 1
}

# --- refresh the vendored baseline -------------------------------------------------------
refresh_baseline() {
    local lib="$1" ref commit records err sha line staged
    [ -d "$lib" ] || { echo "render-agents.sh: not a directory: $lib" >&2; exit 2; }
    git -C "$lib" rev-parse --git-dir >/dev/null 2>&1 ||
        { echo "render-agents.sh: $lib is not a git checkout; the baseline must name a library commit" >&2; exit 1; }
    at_load "$root"
    [ -z "$AT_SNAPSHOT_BAD" ] ||
        { echo "render-agents.sh: refusing to vendor: 'Baseline snapshot:' path '$AT_SNAPSHOT_REL' $AT_SNAPSHOT_BAD; it must be a relative path inside the repository" >&2; exit 1; }
    ra_tmpdir; staged="$RA_TMP/tiers.json"
    if [ -n "$library_ref" ]; then
        # Resolve to a commit first: only the verified object name reaches later git calls.
        commit="$(git -C "$lib" rev-parse --verify --quiet "$library_ref^{commit}")" ||
            { echo "render-agents.sh: --library-ref '$library_ref' is not a commit in $lib" >&2; exit 1; }
        git -C "$lib" show "$commit:tiers.json" > "$staged" 2>/dev/null ||
            { echo "render-agents.sh: no tiers.json at $library_ref in $lib" >&2; exit 1; }
        ref="$(git -C "$lib" log -1 --format=%H "$commit" -- tiers.json)"
    else
        [ -f "$lib/tiers.json" ] || { echo "render-agents.sh: $lib has no tiers.json" >&2; exit 1; }
        if ! git -C "$lib" diff --quiet HEAD -- tiers.json 2>/dev/null; then
            echo "render-agents.sh: $lib/tiers.json has uncommitted changes; vendor a committed revision" >&2; exit 1
        fi
        cp "$lib/tiers.json" "$staged"
        ref="$(git -C "$lib" log -1 --format=%H -- tiers.json)"
    fi
    [ -n "$ref" ] || { echo "render-agents.sh: tiers.json is not committed in $lib" >&2; exit 1; }
    records="$(at_tiers_json_records "$staged")"
    err="$(printf '%s\n' "$records" | awk -F '\037' '!d && $1 == "error" { print $2; d = 1 }')"
    [ -z "$err" ] || { echo "render-agents.sh: library tiers.json is not valid tiers JSON ($err)" >&2; exit 1; }
    ra_install "$staged" "$AT_SNAPSHOT" || exit 1
    sha="$(at_hash_file "$AT_SNAPSHOT")"
    printf 'source: agent-library tiers.json\nlibrary-ref: %s\nsha256: %s\n' "$ref" "$sha" > "$RA_TMP/tiers.ref"
    ra_install "$RA_TMP/tiers.ref" "$AT_SNAPSHOT.ref" || exit 1
    echo "✓ Vendored agent-library tiers.json@$ref into $AT_SNAPSHOT_REL"
    line="$(printf '%s\n' "$AT_MAP" | awk -F '\037' '!d && $1 == "baseline" { print $3; d = 1 }')"
    if [ -n "$line" ]; then
        staged="$RA_TMP/agents.md"
        awk -v ln="$line" -v ref="$ref" 'NR == ln { sub(/tiers\.json@[0-9a-f]+/, "tiers.json@" ref) } { print }' "$AT_TIERMAP" > "$staged"
        if ! cmp -s "$staged" "$AT_TIERMAP"; then
            ra_install "$staged" "$AT_TIERMAP" || exit 1
            echo "✓ Updated the Tier baseline line in .prdkit/memory/agents.md"
        fi
    else
        echo "  Opt in by adding this line to the tier map in .prdkit/memory/agents.md:"
        echo "    Tier baseline: agent-library tiers.json@$ref"
    fi
    echo "  Re-render with render-agents.sh, then review the projection diff."
}

# --- projection writing ------------------------------------------------------------------

# Existing projection frontmatter with model/variant (and an owned description) set; other
# keys untouched. desc_line is the neutral definition's raw description line, desc its value;
# both empty when the description is not owned.
updated_frontmatter() { # proj harness model variant desc_line desc
    local oc=0
    if [ "$2" = opencode ]; then oc=1; fi
    awk -v model="$3" -v variant="$4" -v oc="$oc" -v drop="$AT_NEUTRAL_ONLY_KEYS" -v dline="$5" -v dval="$6" '
        function val(s) {
            sub(/^[^:]*:[ \t]*/, "", s); sub(/[ \t\r]+$/, "", s)
            if (s ~ /^".*"$/ || s ~ /^\047.*\047$/) s = substr(s, 2, length(s) - 2); else sub(/[ \t]+#.*$/, "", s)
            return s
        }
        BEGIN { n = split(drop, keys, " "); for (i = 1; i <= n; i++) neutral[keys[i]] = 1 }
        NR == 1 { print; next }
        skipcont && /^[ \t]/ { next }
        { skipcont = 0 }
        oc && match($0, /^[A-Za-z_-]+:/) && (substr($0, 1, RLENGTH - 1) in neutral) { next }
        /^model:/ { seenm = 1; if (val($0) == model) print; else print "model: " model; next }
        oc && /^variant:/ { seenv = 1; if (variant == "") next; if (val($0) == variant) print; else print "variant: " variant; next }
        dline != "" && /^description:/ {
            seend = 1
            if (val($0) == dval) print; else { print dline; skipcont = 1 }
            next
        }
        $0 == "---" {
            if (dline != "" && !seend) print dline
            if (!seenm) print "model: " model
            if (oc && !seenv && variant != "") print "variant: " variant
            print; exit
        }
        { print }
    ' "$1"
}

# A frontmatter key's whole entry: its line plus the indented lines of a block or folded value
# (blank lines inside it included), so a new projection copies a multiline description whole.
fm_entry() { # file key
    awk -v key="$2" '
        NR == 1 { if ($0 != "---") exit; next }
        $0 == "---" { exit }
        on && /^[ \t]*$/ { held = held $0 "\n"; next }
        on && /^[ \t]/ { printf "%s", held; held = ""; print; next }
        on { exit }
        index($0, key ":") == 1 { print; on = 1 }
    ' "$1"
}

new_projection() { # def harness name model variant
    local def="$1" harness="$2" name="$3" model="$4" variant="$5" line
    printf -- '---\n'
    printf '# Generated from .prdkit/agents/%s.md by render-agents.sh; model/variant resolve from .prdkit/memory/agents.md. Edit the neutral definition, then re-render.\n' "$name"
    line="$(at_fm_line "$def" name)"; printf '%s\n' "${line:-name: $name}"
    line="$(fm_entry "$def" description)"; [ -z "$line" ] || printf '%s\n' "$line"
    [ "$harness" != opencode ] || printf 'mode: subagent\n'
    printf 'model: %s\n' "$model"
    [ "$harness" != opencode ] || [ -z "$variant" ] || printf 'variant: %s\n' "$variant"
    printf -- '---\n\n'
    at_body_norm "$def"
}

# Sets RP_OUTCOME to created|updated|unchanged; returns 1 (outcome empty) on a failed write.
# Called directly, never inside $(...), so a write failure reaches the caller.
render_projection() { # def harness name model variant
    local def="$1" harness="$2" name="$3" model="$4" variant="$5"
    local proj="$root/.$harness/agents/$name.md" tmp="$RA_TMP/projection" markers desc dline=""
    RP_OUTCOME=""
    desc="$(at_owned_description "$def")"
    [ -z "$desc" ] || dline="$(at_fm_line "$def" description)"
    if [ ! -f "$proj" ]; then
        new_projection "$def" "$harness" "$name" "$model" "$variant" > "$tmp" || return 1
        ra_install "$tmp" "$proj" || return 1
        RP_OUTCOME=created; return 0
    fi
    if [ "$(head -n1 "$proj")" != '---' ]; then
        new_projection "$def" "$harness" "$name" "$model" "$variant" > "$tmp" || return 1
    elif [ "$(at_body_norm "$proj")" = "$(at_body_norm "$def")" ]; then
        # Body already current: change only frontmatter lines, keep body bytes exactly.
        { updated_frontmatter "$proj" "$harness" "$model" "$variant" "$dline" "$desc" &&
          awk 'NR == 1 { next } f { print; next } $0 == "---" { f = 1 }' "$proj"; } > "$tmp" || return 1
    else
        markers="$(at_body_markers "$proj")"
        { updated_frontmatter "$proj" "$harness" "$model" "$variant" "$dline" "$desc" &&
          printf '\n' &&
          { [ -z "$markers" ] || printf '%s\n\n' "$markers"; } &&
          at_body_norm "$def"; } > "$tmp" || return 1
    fi
    if cmp -s "$tmp" "$proj"; then RP_OUTCOME=unchanged; return 0; fi
    ra_install "$tmp" "$proj" || return 1
    RP_OUTCOME=updated
}

# --- main --------------------------------------------------------------------------------
case "$mode" in
    refresh) refresh_baseline "$library"; exit 0 ;;
    check)
        collect --include-missing --always-projections
        emit_findings "$json" ${FINDINGS[@]+"${FINDINGS[@]}"}
        [ "${#FINDINGS[@]}" -eq 0 ]; exit $? ;;
esac

# write/resolve: lint first, then resolve everything before touching any file. Projections
# that use a banned id are not linted here: the render rewrites their model, and --check
# keeps failing until it has (any the render cannot reach are reported after it).
FINDINGS=()
at_load "$root"
while IFS= read -r f; do [ -z "$f" ] || FINDINGS+=("$f"); done < <(at_lint_map; at_lint_definitions)
if [ -n "$AT_BASELINE" ] && [ -z "$AT_SNAP" ] && [ "${#FINDINGS[@]}" -eq 0 ]; then
    FINDINGS+=("Tier map: baseline snapshot $AT_SNAPSHOT_REL is unusable")
fi
plan=()
for def in "$root"/.prdkit/agents/*.md; do
    [ -f "$def" ] || continue
    name="$(basename "$def" .md)"
    tier="$(at_fm_value "$def" tier)"
    for harness in $(at_targets "$root" "$def"); do
        if at_resolve "$name" "$tier" "$harness"; then
            plan+=("$name"$'\037'"$harness"$'\037'"$tier"$'\037'"$AT_R_MODEL"$'\037'"$AT_R_VARIANT"$'\037'"$AT_R_SOURCE")
        else
            FINDINGS+=("Agent '$name': no $harness model resolves ($AT_R_ERR)")
        fi
    done
done
if [ "${#FINDINGS[@]}" -gt 0 ]; then
    emit_findings "$json" "${FINDINGS[@]}" >&2
    if [ "$mode" = resolve ]; then
        echo 'render-agents.sh: cannot resolve; fix the tier map, override, snapshot, definition or tier findings above.' >&2
    else
        echo 'render-agents.sh: nothing written; fix the tier map, override, snapshot, definition or tier findings above first.' >&2
    fi
    exit 1
fi

if [ "$mode" = resolve ]; then
    if $json; then
        sep=""; printf '['
        for entry in ${plan[@]+"${plan[@]}"}; do
            IFS=$'\037' read -r name harness tier model variant source <<EOF
$entry
EOF
            printf '%s{"agent":"%s","harness":"%s","tier":"%s","model":"%s","variant":"%s","source":"%s"}' "$sep" \
                "$(json_escape "$name")" "$harness" "$(json_escape "$tier")" "$(json_escape "$model")" "$(json_escape "$variant")" "$(json_escape "$source")"
            sep=,
        done
        printf ']\n'
    else
        for entry in ${plan[@]+"${plan[@]}"}; do
            IFS=$'\037' read -r name harness tier model variant source <<EOF
$entry
EOF
            printf '%-28s %-9s %-9s %-28s %-8s %s\n' "$name" "$harness" "$tier" "$model" "${variant:--}" "$source"
        done
    fi
    exit 0
fi

ra_tmpdir
created=0 updated=0 unchanged=0
for entry in ${plan[@]+"${plan[@]}"}; do
    IFS=$'\037' read -r name harness tier model variant source <<EOF
$entry
EOF
    if ! render_projection "$root/.prdkit/agents/$name.md" "$harness" "$name" "$model" "$variant"; then
        echo "✗ .$harness/agents/$name.md: write failed" >&2
        echo "render-agents.sh: stopped at the first write failure; $((created + updated)) projection file(s) were written before it. Fix the permissions and re-run." >&2
        exit 1
    fi
    case "$RP_OUTCOME" in
        created) created=$((created + 1)); echo "+ .$harness/agents/$name.md ($model${variant:+ / $variant}; $source)" ;;
        updated) updated=$((updated + 1)); echo "~ .$harness/agents/$name.md ($model${variant:+ / $variant}; $source)" ;;
        *) unchanged=$((unchanged + 1)) ;;
    esac
done
banned=()
while IFS= read -r f; do [ -z "$f" ] || banned+=("$f"); done < <(at_lint_projection_bans)
if [ "${#banned[@]}" -gt 0 ]; then
    echo "  Agent projections: $created created, $updated updated, $unchanged unchanged"
    emit_findings "$json" "${banned[@]}" >&2
    echo 'render-agents.sh: the render does not own these projections (no neutral definition, or its harnesses: list excludes them); change their model by hand or remove them.' >&2
    exit 1
fi
# Unowned projections are not something the render can fix: report them after rendering.
unowned=()
if [ -n "$AT_BASELINE" ]; then
    while IFS= read -r f; do [ -z "$f" ] || unowned+=("$f"); done < <(at_ownership_findings)
fi
if [ "${#unowned[@]}" -gt 0 ]; then
    echo "  Agent projections: $created created, $updated updated, $unchanged unchanged"
    emit_findings "$json" "${unowned[@]}" >&2
    exit 1
fi
echo "✓ Agent projections: $created created, $updated updated, $unchanged unchanged"
echo "  Restart the harness from this checkout to load changed definitions; validate with its native loader."
