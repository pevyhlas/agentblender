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
# --require-source selects strict installation proof instead of the source-less
# manifest/tier-map lane below. --head REV implies it and reads committed consumer
# objects only. Neither lane fetches, installs, edits policy or authorizes review.
# Exit 0: all checks in the selected lane pass; 1: findings; 2: usage error.
#
# Invariants:
# A default-lane pass reports installation integrity without claiming pinned-source proof.
# Strict mode uses the committed source gitlink and reports a failed proof on findings;
# --head selects committed installation bytes, never index or worktree substitutes.
# Neither lane repairs an installation or grants review or release approval.
#
# Threat model:
# Include ordinary install drift, missing source objects and interrupted or overlapping
# kit runs. Worktree checks are not a point-in-time snapshot; use --head for a fixed
# candidate. A hostile writer controlling the checker or its checkout is out of scope.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/install-lib.sh"
source "$SCRIPT_DIR/agent-tiers-lib.sh"
source "$SCRIPT_DIR/spec-lib.sh"
source "$SCRIPT_DIR/install-source-lib.sh"

json=false
require_source=false
head=''
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) json=true ;;
        --require-source) require_source=true ;;
        --head)
            shift
            [ -n "${1:-}" ] || { echo '--head requires a committed revision' >&2; exit 2; }
            head="$1"; require_source=true ;;
        --help|-h) echo 'Usage: check-adoption.sh [--json] [--require-source] [--head <commit>]'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

root="$(get_repo_root)"
require_consumer_root "$root" check-adoption.sh

# Strict installation proof is a separate evidence lane from manifest-copy integrity.
# --head reads only committed consumer objects (not index/worktree installation bytes);
# source objects are read from the initialized local source repository, without execution.
if $require_source; then
    work="$(mktemp -d)" || exit 1
    trap 'rm -rf "$work"' EXIT
    findings=()
    if ! install_source_verify "$root" "$head" "$work" > "$work/findings" ||
        ! verify_lf_file "$work/findings" allow-empty; then
        findings+=("Pinned-source installation proof did not complete")
    fi
    while IFS= read -r finding; do [ -z "$finding" ] || findings+=("$finding"); done < "$work/findings"
    result=fail; proof=failed
    if [ "${#findings[@]}" -eq 0 ]; then result=pass; proof=verified; fi
    if $json; then
        printf '{"result":"%s","checks":1,"source_verification":"%s","source_revision":"%s","consumer_revision":"%s","scope":"installation","findings":%s}\n' \
            "$result" "$proof" "$INSTALL_SOURCE_REVISION" "$INSTALL_CONSUMER_REVISION" \
            "$(json_array ${findings[@]+"${findings[@]}"})"
    else
        for finding in ${findings[@]+"${findings[@]}"}; do printf '✗ %s\n' "$finding"; done
        printf 'Pinned-source installation proof: %s (source %s; consumer %s; %s)\n' \
            "$proof" "${INSTALL_SOURCE_REVISION:-unavailable}" "${INSTALL_CONSUMER_REVISION:-unavailable}" \
            "$([ -n "$head" ] && echo committed || echo worktree)"
    fi
    [ "$result" = pass ]
    exit
fi
CONFIG="$root/.prdkit/memory/config.md"
MANIFEST="$root/.prdkit/.manifest"

# Section-scoped key lookup: only the ## Configuration section is config, so
# documented examples elsewhere in the file can never shadow the real values.
config_value() {
    local key="$1"
    [ -f "$CONFIG" ] || return 0
    install_config_value "$CONFIG" "$key"
}

hash_file() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
    else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

findings=()
checked=0
note() { checked=$((checked + 1)); }
fail() { findings+=("$1"); }

if [ -e "$CONFIG" ] || [ -L "$CONFIG" ]; then
    if [ ! -f "$CONFIG" ] || ! verify_lf_file "$CONFIG"; then
        fail "Invalid adoption configuration: expected non-empty LF-terminated text"
    fi
fi
PIN="$(config_value 'adoption-pin')" || fail "Unable to read a unique adoption-pin restriction"
MANIFEST_SHA="$(config_value 'adoption-manifest-sha256')" || fail "Unable to read a unique adoption-manifest-sha256 restriction"
SOURCE="$(config_value 'adoption-source')" || fail "Unable to read a unique adoption-source restriction"
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
        gitlink="$(install_source_pin "$root" "$SOURCE" || true)"
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
    manifest_version="$(install_manifest_value "$MANIFEST" version)" || manifest_version=''
    [ -n "$manifest_version" ] || fail "Manifest records no version line"
    if [ -f "$root/$SOURCE/VERSION" ]; then
        note
        if verify_lf_file "$root/$SOURCE/VERSION" &&
            version_lines="$(wc -l < "$root/$SOURCE/VERSION")" && [ "$version_lines" -eq 1 ] &&
            IFS= read -r kit_version < "$root/$SOURCE/VERSION"; then
            [ "$manifest_version" = "$kit_version" ] ||
                fail "Manifest version $manifest_version does not match kit source $kit_version (re-run init.sh --upgrade)"
        else
            fail 'Invalid kit source VERSION: expected one LF-terminated version line'
        fi
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
if rows="$(verify_lines install_findings "$root")"; then
    while IFS= read -r finding; do [ -z "$finding" ] || fail "$finding"; done <<< "$rows"
else
    fail 'Installation inventory did not complete'
fi
if ! layout_problem="$(spec_require_layout "$root" 2>&1)"; then fail "$layout_problem"; fi
mode="$(install_manifest_value "$MANIFEST" mode 2>/dev/null || true)"
harnesses="$(install_manifest_value "$MANIFEST" harnesses 2>/dev/null || true)"
manifest_commands() { # harness -> expected basenames recorded in the manifest
    awk -v prefix=".$1/commands/" '
        length($1) == 64 && $1 ~ /^[0-9a-f]+$/ && index($2, prefix) == 1 { print substr($2, length(prefix) + 1) }
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
    if ! commands="$(verify_lines manifest_commands "$harness")"; then
        fail "Unable to inventory installed commands for harness '$harness'"
        continue
    fi
    while IFS= read -r name; do
        [ -n "$name" ] && expected+=("$name")
    done <<< "$commands"
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
if rows="$(verify_lines agent_tier_findings "$root" --include-missing)"; then
    while IFS= read -r finding; do [ -z "$finding" ] || fail "$finding"; done <<< "$rows"
else
    fail 'Agent tier inventory did not complete'
fi

# --- Internal bootstrap must not leak into the install ---
note
[ ! -e "$root/.prdkit/scripts/bash/init.sh" ] ||
    fail "Kit bootstrap leaked into the install: .prdkit/scripts/bash/init.sh"

if $json; then
    result=fail
    [ "${#findings[@]}" -eq 0 ] && result=pass
    printf '{"result":"%s","checks":%s,"source_verification":"not-requested","findings":%s}\n' \
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
