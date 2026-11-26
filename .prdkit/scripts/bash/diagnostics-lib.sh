#!/usr/bin/env bash
# Read-only adoption summary and effective configuration inventory. Source common/install/spec
# libraries first; configuration inventory also needs agent-tiers.
# Never probes a CLI, imports plugins, fetches Git objects or interprets policy as authority.

diagnostic_config_value() { # root key; same section/first-key contract as check-adoption.sh
    [ -f "$1/.prdkit/memory/config.md" ] || return 0
    sed -n '/^##[[:space:]]*Configuration/,/^##[[:space:]]/p' "$1/.prdkit/memory/config.md" |
        grep -E "^-?[[:space:]]*$2:" | head -1 |
        sed -E "s/^-?[[:space:]]*$2:[[:space:]]*//" | tr -d '\r' || true
}

diagnostic_string() { # absent values are unknown, never invented defaults
    if [ -n "$1" ]; then printf '"%s"' "$(json_escape "$1")"; else printf null; fi
}

diagnostic_adoption() { # root [--text] -> local observations
    local root="$1" version='' layout mixed=false
    if [ -f "$root/.prdkit/.manifest" ]; then
        version="$(install_manifest_value "$root/.prdkit/.manifest" version || true)"
    fi
    if spec_require_layout "$root" 2>/dev/null; then layout="$(spec_layout "$root")"; else layout=unsupported; fi
    [ -z "$(spec_mixed_forms "$root")" ] || mixed=true
    if [ "${2:-}" = --text ]; then
        printf 'Installed kit version (manifest): %s\nSpec layout: %s\n' "${version:-unknown}" "$layout"
    else
        printf '{"installed_version":%s,"spec_layout":"%s","mixed_spec_forms":%s}' \
            "$(diagnostic_string "$version")" "$layout" "$mixed"
    fi
}

diagnostic_file_hash() { # root relative-path; don't follow outside links while inventorying
    local root="$1" path="$1/$2" dir link count=0
    while [ -L "$path" ]; do
        count=$((count + 1)); [ "$count" -le 40 ] || return 0
        link="$(readlink "$path")" || return 0
        case "$link" in /*) path="$link" ;; *) path="$(dirname "$path")/$link" ;; esac
    done
    [ -f "$path" ] || return 0
    dir="$(CDPATH='' cd -P "$(dirname "$path")" 2>/dev/null && pwd)" || return 0
    case "$dir/" in "$root/"*) install_hash_stream < "$path" ;; esac
}

diagnostic_configuration() ( # root -> JSON; environment changes stay in the subshell
    export GIT_OPTIONAL_LOCKS=0 GIT_NO_LAZY_FETCH=1 LC_ALL=C
    root="$(CDPATH='' cd -P "$1" && pwd)"
    manifest="$root/.prdkit/.manifest"
    version=''; mode=''; harnesses=''; manifest_hash=''
    if [ -f "$manifest" ]; then
        version="$(install_manifest_value "$manifest" version || true)"
        mode="$(install_manifest_value "$manifest" mode || true)"
        harnesses="$(install_manifest_value "$manifest" harnesses || true)"
        manifest_hash="$(diagnostic_file_hash "$root" .prdkit/.manifest)"
    fi
    source_path="$(diagnostic_config_value "$root" adoption-source)"
    source_path="${source_path:-.product-kit}"
    expected="$(diagnostic_config_value "$root" adoption-pin)"; pin=''; checkout=''
    # A copied source directory is not a Git checkout: parent Git discovery would lie.
    case "$source_path" in
        /*|*..*) source_state=invalid ;;
        *) source_state=unavailable
           pin="$(install_source_pin "$root" "$source_path" || true)"
           if [ ! -L "$root/$source_path" ] && [ -e "$root/$source_path/.git" ]; then
               source_dir="$(CDPATH='' cd -P "$root/$source_path" 2>/dev/null && pwd)"
               case "$source_dir/" in "$root/"*)
                   checkout="$(git -C "$source_dir" rev-parse --verify HEAD 2>/dev/null || true)"
                   [ -z "$checkout" ] || source_state=checkout ;;
               esac
           elif [ -L "$root/$source_path" ]; then source_state=symlink-uninspected
           elif [ -n "$pin" ]; then source_state=uninitialized
           elif [ -d "$root/$source_path" ]; then source_state=copy; fi ;;
    esac
    printf '{"schema_version":1,"version":%s,"manifest_sha256":%s,"mode":%s,' \
        "$(diagnostic_string "$version")" "$(diagnostic_string "$manifest_hash")" "$(diagnostic_string "$mode")"
    printf '"source":{"path":%s,"state":"%s","expected_pin":%s,"tracked_pin":%s,"checkout_head":%s},' \
        "$(diagnostic_string "$source_path")" "$source_state" "$(diagnostic_string "$expected")" \
        "$(diagnostic_string "$pin")" "$(diagnostic_string "$checkout")"
    printf '"files":['; sep=''
    if [ -f "$manifest" ]; then
        while IFS= read -r line; do
            hash="${line%%  *}"; path="${line#*  }"
            [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || continue
            owner=unknown; winner=unknown; actual=''; state=unsafe-path
            if [[ "$path" =~ ^\.(prdkit/(scripts/bash|workflows|templates|memory)|(claude|opencode)/commands)/[A-Za-z0-9_][A-Za-z0-9_.-]*$ ]]; then
                case "$path" in
                    .prdkit/templates/*|.prdkit/memory/*) owner=project; winner=project-copy ;;
                    *) owner=kit; winner=installed-kit ;;
                esac
                actual="$(diagnostic_file_hash "$root" "$path")"
                state=unavailable
                if [ -n "$actual" ]; then
                    if [ "$actual" = "$hash" ]; then state=pristine
                    elif [ "$owner" = project ]; then state=customized
                    else state=drift; fi
                fi
            fi
            printf '%s{"path":"%s","owner":"%s","effective":"%s","state":"%s","recorded_sha256":"%s","actual_sha256":%s}' \
                "$sep" "$(json_escape "$path")" "$owner" "$winner" "$state" "$hash" "$(diagnostic_string "$actual")"
            sep=,
        done < "$manifest"
    fi
    printf '],"agents":['; sep=''
    at_load "$root"
    for def in "$root"/.prdkit/agents/*.md; do
        [ -f "$def" ] || continue
        agent="$(basename "$def" .md)"; tier="$(at_fm_value "$def" tier)"
        for harness in $(at_targets "$root" "$def"); do
            at_resolve "$agent" "$tier" "$harness" || true
            projection="$root/.$harness/agents/$agent.md"; projected=''; projected_variant=''
            if [ -f "$projection" ]; then
                projected="$(at_fm_value "$projection" model)"
                projected_variant="$(at_fm_value "$projection" variant)"
            fi
            printf '%s{"agent":"%s","harness":"%s","tier":"%s","resolved_model":%s,"resolved_variant":%s,"winning_rule":%s,"projected_model":%s,"projected_variant":%s,"error":%s}' \
                "$sep" "$(json_escape "$agent")" "$harness" "$(json_escape "$tier")" \
                "$(diagnostic_string "$AT_R_MODEL")" "$(diagnostic_string "$AT_R_VARIANT")" "$(diagnostic_string "$AT_R_SOURCE")" \
                "$(diagnostic_string "$projected")" "$(diagnostic_string "$projected_variant")" "$(diagnostic_string "$AT_R_ERR")"
            sep=,
        done
    done
    printf '],"harnesses":['; sep=''
    for harness in claude opencode codex agents; do
        configured=false
        case " $(printf '%s' "$harnesses" | tr ',' ' ') " in *" $harness "*) configured=true ;; esac
        printf '%s{"harness":"%s","configured":%s,"version":null,"native_loading":"unknown","execution":"unknown","structured_capture":"unknown","events":"unknown","independent_review_access":"unknown"}' "$sep" "$harness" "$configured"
        sep=,
    done
    printf '],"active_harness":null,"active_model":null,"authority":"not-evaluated"}'
)
