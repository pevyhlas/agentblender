#!/usr/bin/env bash
# check-adoption.sh — consumer CI adoption gate.
#
# Verifies that the working tree still honors the reviewed adoption contract:
# the tracked kit revision, the install manifest, and the installed command
# surface. Consumers wire one CI step:
#
#   bash .prdkit/scripts/bash/check-adoption.sh
#
# Configuration lives in the ## Configuration section of .prdkit/memory/config.md
# (all keys optional; unset keys skip their check):
#
#   - adoption-pin: <40-hex commit>          expected kit submodule commit
#   - adoption-manifest-sha256: <64-hex>     expected sha256 of .prdkit/.manifest
#   - adoption-source: .product-kit          kit source path (default .product-kit)
#
# Always checked, config or not: manifest presence, the exact public command
# surface per configured harness in the recorded install mode (derived from the
# manifest itself, so CI needs no submodule checkout), no kit-named command file the
# manifest does not record (in any harness command directory), and that no kit-internal
# bootstrap leaked into the install. Agent tier-map checks (agent-tiers-lib.sh) run too,
# with projection drift once the tier map declares a shared tier baseline. When the kit
# source IS readable, its VERSION and command set are cross-checked against the manifest.
#
# Exit 0: all applicable checks pass; 1: findings; 2: usage error.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/install-lib.sh"
source "$SCRIPT_DIR/agent-tiers-lib.sh"

json=false
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) json=true ;;
        --help|-h) echo 'Usage: check-adoption.sh [--json]'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

root="$(get_repo_root)"
require_consumer_root "$root" check-adoption.sh
CONFIG="$root/.prdkit/memory/config.md"
MANIFEST="$root/.prdkit/.manifest"

# Section-scoped key lookup: only the ## Configuration section is config, so
# documented examples elsewhere in the file can never shadow the real values.
config_value() {
    local key="$1" section
    [ -f "$CONFIG" ] || return 0
    section="$(sed -n '/^##[[:space:]]*Configuration/,/^##[[:space:]]/p' "$CONFIG" 2>/dev/null)"
    printf '%s\n' "$section" \
        | grep -E "^-?[[:space:]]*${key}:" | head -1 \
        | sed -E 's/^-?[[:space:]]*'"$key"':[[:space:]]*//' | tr -d '\r' || true
}

hash_file() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
    else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

findings=()
checked=0
note() { checked=$((checked + 1)); }
fail() { findings+=("$1"); }

PIN="$(config_value 'adoption-pin')"
MANIFEST_SHA="$(config_value 'adoption-manifest-sha256')"
SOURCE="$(config_value 'adoption-source')"
SOURCE="${SOURCE:-.product-kit}"
case "$SOURCE" in
    /*|*..*) echo "adoption-source must be a repo-relative path without '..': $SOURCE" >&2; exit 2 ;;
esac

# --- Tracked kit revision ---
if [ -n "$PIN" ]; then
    note
    if ! [[ "$PIN" =~ ^[0-9a-f]{40}$ ]]; then
        fail "adoption-pin must be a full 40-hex commit: $PIN"
    else
        gitlink="$(git -C "$root" ls-files -s -- "$SOURCE" 2>/dev/null | awk '$1 == 160000 { print $2; exit }')"
        if [ -z "$gitlink" ]; then
            fail "adoption-pin is configured but $SOURCE is not a tracked submodule"
        elif [ "$gitlink" != "$PIN" ]; then
            fail "Kit pin drift: $SOURCE is at $gitlink, adoption contract pins $PIN"
        fi
    fi
fi

# --- Manifest presence, integrity, and version consistency ---
note
if [ ! -f "$MANIFEST" ]; then
    fail "No install manifest at .prdkit/.manifest (run the installer)"
else
    manifest_version="$(sed -n 's/^version:[[:space:]]*//p' "$MANIFEST" | head -1)"
    [ -n "$manifest_version" ] || fail "Manifest records no version line"
    if [ -f "$root/$SOURCE/VERSION" ]; then
        note
        kit_version="$(tr -d '[:space:]' < "$root/$SOURCE/VERSION")"
        [ "$manifest_version" = "$kit_version" ] ||
            fail "Manifest version $manifest_version does not match kit source $kit_version (re-run init.sh --upgrade)"
    fi
    if [ -n "$MANIFEST_SHA" ]; then
        note
        actual_sha="$(hash_file "$MANIFEST")"
        [ "$actual_sha" = "$MANIFEST_SHA" ] ||
            fail "Manifest hash drift: $actual_sha, adoption contract records $MANIFEST_SHA"
    fi
fi

# --- Installed command surface (manifest-driven: no submodule checkout needed) ---
note
while IFS= read -r finding; do
    [ -z "$finding" ] || fail "$finding"
done < <(install_findings "$root")
mode="$(sed -n 's/^mode:[[:space:]]*//p' "$MANIFEST" 2>/dev/null | head -1 || true)"
mode="${mode:-symlink}"
harnesses="$(sed -n 's/^harnesses:[[:space:]]*//p' "$MANIFEST" 2>/dev/null | head -1 || true)"
manifest_commands() { # harness -> expected basenames recorded in the manifest
    awk -v prefix=".$1/commands/" '
        $1 ~ /^[0-9a-f]{64}$/ && index($2, prefix) == 1 { print substr($2, length(prefix) + 1) }
    ' "$MANIFEST" 2>/dev/null
}
for harness in $(printf '%s' "$harnesses" | tr ',' ' '); do
    case "$harness" in
        claude) dir="$root/.claude/commands" ;;
        opencode) dir="$root/.opencode/commands" ;;
        *) continue ;;
    esac
    note
    expected=()
    while IFS= read -r name; do
        [ -n "$name" ] && expected+=("$name")
    done < <(manifest_commands "$harness")
    if [ "${#expected[@]}" -eq 0 ]; then
        fail "Manifest records no installed commands for harness '$harness'"
        continue
    fi
    for name in "${expected[@]}"; do
        installed="$dir/$name"
        if [ ! -e "$installed" ]; then
            fail "Missing installed command: ${installed#"$root"/}"
        elif [ "$mode" = copy ]; then
            { [ -f "$installed" ] && [ ! -L "$installed" ]; } ||
                fail "Command must be a regular copy (mode: copy): ${installed#"$root"/}"
        else
            { [ -L "$installed" ] && [ -e "$installed" ]; } ||
                fail "Command must be a resolving symlink (mode: symlink): ${installed#"$root"/}"
        fi
    done
    # Kit-named command files the manifest does not record (for this harness or one the
    # manifest no longer lists) are reported by install_findings above.
    # Cross-check when the kit source happens to be readable: the manifest must
    # not have silently dropped a shipped command.
    if [ -d "$root/$SOURCE/commands" ]; then
        for file in "$root/$SOURCE/commands"/*.md; do
            [ -f "$file" ] || continue
            name="$(basename "$file")"
            known=false
            for candidate in "${expected[@]}"; do
                [ "$candidate" = "$name" ] && known=true
            done
            $known || fail "Kit source ships $name but the manifest records no install for harness '$harness'"
        done
    fi
done

# --- Agent tier map and projections (render-agents.sh resolution) ---
# Static tier-map checks always; projection drift (including missing projections) once the
# tier map declares a shared `Tier baseline:` — the opt-in to rendered, resolvable models.
note
while IFS= read -r finding; do
    [ -z "$finding" ] || fail "$finding"
done < <(agent_tier_findings "$root" --include-missing)

# --- Internal bootstrap must not leak into the install ---
note
[ ! -e "$root/.prdkit/scripts/bash/init.sh" ] ||
    fail "Kit bootstrap leaked into the install: .prdkit/scripts/bash/init.sh"

if $json; then
    result=fail
    [ "${#findings[@]}" -eq 0 ] && result=pass
    printf '{"result":"%s","checks":%s,"findings":%s}\n' \
        "$result" "$checked" "$(json_array ${findings[@]+"${findings[@]}"})"
else
    for finding in ${findings[@]+"${findings[@]}"}; do
        printf '✗ %s\n' "$finding"
    done
    if [ "${#findings[@]}" -eq 0 ]; then
        printf '✓ Adoption gate: %s check group(s) pass' "$checked"
        [ -n "$PIN" ] || printf ' (no adoption-pin configured — pin drift not checked)'
        printf '\n'
    else
        printf '✗ Adoption gate: %s finding(s)\n' "${#findings[@]}"
    fi
fi
[ "${#findings[@]}" -eq 0 ]
