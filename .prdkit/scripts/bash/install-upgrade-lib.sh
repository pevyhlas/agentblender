#!/usr/bin/env bash
# Read-only installer preflight and recovery. Sourced by init.sh.
# shellcheck disable=SC2154 # Installer state comes from init.sh.
# Snapshot a HEAD entry without filters or git-show absence masking.
upgrade_head_file() { # relative path destination link-metadata
    local entry mode blob
    entry="$(git -C "$TARGET" ls-tree HEAD -- ":(top,literal)$1")" || return 1
    [ -n "$entry" ] || return 0
    mode="${entry%% *}"; blob="${entry#* }"; blob="${blob%%$'\t'*}"; blob="${blob#* }"
    mkdir -p "$(dirname "$2")" || return 1
    case "$mode" in
        100644|100755)
            git -C "$TARGET" cat-file blob "$blob" > "$2" || return 1
            if [ "$mode" = 100755 ]; then chmod 755 "$2"; else chmod 644 "$2"; fi ;;
        120000)
            git -C "$TARGET" cat-file blob "$blob" > "$2" || return 1
            mkdir -p "$(dirname "$3")" && : > "$3" || return 1 ;;
        *) return 1 ;;
    esac
}

upgrade_committed_manifest() (
    local work
    work="$(mktemp -d)" || return 1
    trap 'rm -rf "$work"' EXIT
    upgrade_head_file .prdkit/.manifest "$work/manifest" "$work/link" &&
        [ ! -f "$work/link" ] && install_manifest_format "$work/manifest" && cat "$work/manifest"
)

# Encode octets for Bash printf instead of using a line-oriented substitution:
# preserve arbitrary bytes and do not add a newline at EOF.
upgrade_retired_crlf() {
    LC_ALL=C od -An -v -to1 "$1" | LC_ALL=C awk '
        {
            for (i=1; i<=NF; i++) {
                if ($i == "012") out=out "\\0015"
                out=out "\\0" $i
            }
            if (length(out) >= 4096) { print out; out="" }
        }
        END { if (length(out)) print out }' |
        while IFS= read -r octets; do printf '%b' "$octets" || return 1; done
}

upgrade_retired_head_state() ( # relative path, pristine install hash
    local rel="$1" rec="$2" entry hash link_target work status
    case "$rel" in .prdkit/memory/*) echo kept; return 0 ;; esac
    entry="$(git -C "$TARGET" ls-tree HEAD -- ":(top,literal)$1")" || return 1
    case "${entry%% *}" in
        '') echo absent; return 0 ;;
        100644|100755)
            work="$(mktemp -d)" || return 1
            trap 'rm -rf "$work"' EXIT
            git -C "$TARGET" cat-file blob "HEAD:$rel" > "$work/blob" || return 1
            hash="$(install_hash_stream < "$work/blob")" || return 1
            if [ "$hash" = "$rec" ]; then echo unedited; return 0; fi
            # Permit only the fixed LF-to-CRLF form of a CR-free blob, never
            # checkout-configured conversions or external filter output.
            if LC_ALL=C grep -aq $'\r' "$work/blob"; then echo kept; return 0
            else status=$?; [ "$status" -eq 1 ] || return 1; fi
            hash="$(upgrade_retired_crlf "$work/blob" | install_hash_stream)" || return 1
            if [ "$hash" = "$rec" ]; then echo unedited; return 0; fi ;;
        120000)
            link_target="$(git -C "$TARGET" cat-file blob "HEAD:$rel" && printf x)" || return 1
            if is_retired_kit_link "$rel" "${link_target%x}"; then echo unedited; return 0; fi ;;
    esac
    echo kept
)

upgrade_same_file() { # real file rendered regular file link-metadata
    if [ -f "$3" ]; then
        [ -L "$1" ] && [ "$(readlink "$1")" = "$(cat "$2")" ]
    elif [ -L "$1" ] || [ -L "$2" ]; then
        return 1
    elif [ -f "$1" ] && [ -f "$2" ]; then
        { { [ -x "$1" ] && [ -x "$2" ]; } || { [ ! -x "$1" ] && [ ! -x "$2" ]; }; } && cmp -s "$1" "$2"
    else [ ! -e "$1" ] && [ ! -e "$2" ]; fi
}

upgrade_resolved_destination() { # also resolve the existing parent of a new destination
    local path="$1" parent suffix=''
    while [ ! -e "$path" ] && [ ! -L "$path" ]; do
        parent="$(dirname "$path")"
        [ "$parent" != "$path" ] || return 1
        suffix="/$(basename "$path")$suffix"; path="$parent"
    done
    path="$(physical_path "$path")" || return 1
    printf '%s%s\n' "$path" "$suffix"
}

# Before any renderer or target write: root-file edits cannot share a destination
# with installation/deletion, another root edit, the source, or the record namespace.
upgrade_destinations_safe() (
    local work src rel dest other role root record_root
    work="$(mktemp -d)" || return 1
    trap 'rm -rf "$work"' EXIT
    : > "$work/planned"
    while IFS=$'\t' read -r src rel; do printf '%s\037installed %s\n' "$TARGET/$rel" "$rel"; done <<< "$KIT_FILES" > "$work/roles"
    for rel in $REMOVE_OPTIONS_FILES; do printf '%s\037optional file %s\n' "$TARGET/$rel" "$rel" >> "$work/roles"; done
    for rel in .prdkit/.manifest .prdkit/.manifest.tmp .prdkit/.install-state .prdkit/.install-lock; do
        printf '%s\037installer metadata %s\n' "$TARGET/$rel" "$rel" >> "$work/roles"
    done
    LC_ALL=C sort -u "$work/roles" > "$work/unique-roles" || return 1
    record_root="$(upgrade_resolved_destination "$TARGET/specs/changes")" || return 1
    if [ ${#RETIRED_REMOVE[@]} -gt 0 ]; then
        for rel in "${RETIRED_REMOVE[@]}"; do
            # Preflight checked every parent. Removal unlinks the file, never its link target.
            case "$TARGET/$rel/" in "$PK_ROOT/"*|"$TARGET/specs/changes/"*|"$record_root/"*)
                echo "Planned destination (retired $rel) overlaps the kit source or protected specs/changes/; nothing was written." >&2
                return 1 ;;
            esac
        done
    fi
    while IFS=$'\037' read -r dest role; do
        other="$(upgrade_resolved_destination "$dest")" || { echo "Cannot resolve planned destination ($role); nothing was written." >&2; return 1; }
        case "$other/" in "$TARGET/specs/changes/"*|"$record_root/"*)
            echo "Planned destination ($role) overlaps protected specs/changes/: $other; nothing was written." >&2; return 1 ;;
        esac
        printf '%s\037%s\n%s\037%s\n' "$dest" "$role" "$other" "$role" >> "$work/planned"
    done < "$work/unique-roles"
    for root in AGENTS.md .gitignore; do
        [ "$root" != AGENTS.md ] || has_harness agents || continue
        dest="$(upgrade_resolved_destination "$TARGET/$root")" || { echo "Cannot resolve root-file edit $root; nothing was written." >&2; return 1; }
        case "$dest/" in "$PK_ROOT/"*) echo "Root-file edit $root overlaps kit source tree: $dest; nothing was written." >&2; return 1 ;; esac
        case "$dest/" in "$TARGET/"*) ;; *) echo "Root-file edit $root resolves outside the checkout: $dest; nothing was written." >&2; return 1 ;; esac
        case "$dest/" in "$TARGET/specs/changes/"*|"$record_root/"*)
            echo "Root-file edit $root overlaps protected specs/changes/: $dest; nothing was written." >&2; return 1 ;;
        esac
        while IFS=$'\037' read -r other role; do
            if [ "$dest" = "$other" ]; then
                echo "Root-file edit $root overlaps $role at $dest; nothing was written." >&2; return 1
            fi
        done < "$work/planned"
        if [ ${#RETIRED_REMOVE[@]} -gt 0 ]; then
            for rel in "${RETIRED_REMOVE[@]}"; do
                if [ "$dest" = "$TARGET/$rel" ] || [ "$dest" -ef "$TARGET/$rel" ]; then
                    echo "Root-file edit $root overlaps retired file $rel; nothing was written." >&2
                    return 1
                fi
            done
        fi
        printf '%s\037root-file edit %s\n' "$dest" "$root" >> "$work/planned"
    done
)

upgrade_render_expected() { # caller-owned work directory -> head/, rendered/, paths (NUL)
    local work="$1" src rel entry rec cur kit ignore hash dest trailing
    mkdir -p "$work/head/.prdkit" "$work/rendered" || return 1
    upgrade_head_file .prdkit/.manifest "$work/head/.prdkit/.manifest" "$work/head-links/.prdkit/.manifest" || return 1
    # The last committed manifest, never a dirty intermediate, is the ownership baseline.
    local MANIFEST="$work/head/.prdkit/.manifest"
    printf '%s\n' "$KIT_FILES" > "$work/files" || return 1
    : > "$work/paths" || return 1
    while IFS=$'\t' read -r src rel; do
        printf '%s\0' "$rel" >> "$work/paths" || return 1
        upgrade_head_file "$rel" "$work/head/$rel" "$work/head-links/$rel" || return 1
        dest="$work/rendered/$rel"
        mkdir -p "$(dirname "$dest")" || return 1
        case "$rel" in
            .claude/commands/*|.opencode/commands/*)
                if ! $MODE_COPY; then
                    case "$src" in "$TARGET"/*) entry="../../${src#"$TARGET"/}" ;; *) entry="$src" ;; esac
                    printf '%s' "$entry" > "$dest" || return 1
                    mkdir -p "$work/rendered-links/$(dirname "$rel")" && : > "$work/rendered-links/$rel" || return 1
                    continue
                fi ;;
            .prdkit/templates/*|.prdkit/memory/*)
                if [ -f "$work/head-links/$rel" ]; then
                    cp "$work/head/$rel" "$dest" || return 1
                    mkdir -p "$work/rendered-links/$(dirname "$rel")" && : > "$work/rendered-links/$rel" || return 1
                    continue
                fi
                if [ -f "$work/head/$rel" ] && ! $FORCE; then
                    cur="$(hash_file "$work/head/$rel")"; rec="$(recorded_hash "$rel")"
                    kit="$(hash_file "$src")"
                    if [ "$cur" != "$rec" ] && [ "$cur" != "$kit" ]; then cp -p "$work/head/$rel" "$dest"; continue; fi
                fi ;;
        esac
        if [ -f "$work/head/$rel" ] && { [[ "$rel" = .prdkit/templates/* ]] || [[ "$rel" = .prdkit/memory/* ]]; }; then
            cp -p "$work/head/$rel" "$dest" || return 1
        fi
        cp "$src" "$dest" || return 1
        case "$rel" in .prdkit/scripts/bash/*.sh) chmod +x "$dest" || return 1 ;; esac
    done < "$work/files"
    for rel in $REMOVE_OPTIONS_FILES; do
        upgrade_head_file "$rel" "$work/head/$rel" "$work/head-links/$rel" || return 1
        if [[ "$rel" = .prdkit/templates/* ]]; then
            rec="$(recorded_hash "$rel")"
            if [ -f "$work/head-links/$rel" ] ||
                { [ -f "$work/head/$rel" ] && [ "$(hash_file "$work/head/$rel")" != "$rec" ]; }; then continue; fi
        fi
        printf '%s\0' "$rel" >> "$work/paths" || return 1
    done
    # Only independently pristine committed retired paths may be missing as
    # installer intermediates. The rendered outcome is absence.
    if [ ${#RETIRED_DELETED[@]} -gt 0 ]; then
        for rel in "${RETIRED_DELETED[@]}"; do
            printf '%s\0' "$rel" >> "$work/paths" || return 1
        done
    fi
    for rel in AGENTS.md .gitignore; do
        [ "$rel" != AGENTS.md ] || has_harness agents || continue
        if dest="$(root_file_path "$rel")"; then dest="${dest#"$TARGET"/}"; else continue; fi
        printf '%s\0' "$dest" >> "$work/paths" || return 1
        upgrade_head_file "$dest" "$work/head/$dest" "$work/head-links/$dest" || return 1
        mkdir -p "$(dirname "$work/rendered/$dest")" || return 1
        # Always create a fresh regular payload, never a writable rendering symlink.
        rm -f "$work/rendered/$dest" || return 1
        if [ -f "$work/head/$dest" ]; then cp -p "$work/head/$dest" "$work/rendered/$dest"; else : > "$work/rendered/$dest"; fi
        if [ "$rel" = AGENTS.md ]; then
            render_agents_section > "$work/section" || return 1
            if grep -qF "$AGENTS_BEGIN" "$work/rendered/$dest"; then
                managed_agents_section "$work/rendered/$dest" >/dev/null || return 1
                render_agents_replacement "$work/rendered/$dest" "$work/section" "$work/next" || return 1
                cat "$work/next" > "$work/rendered/$dest" || return 1
            elif [ -f "$work/head/$dest" ]; then
                trailing="$(tail -c 1 "$work/rendered/$dest")"
                { [ -z "$trailing" ] || echo; echo; cat "$work/section"; } >> "$work/rendered/$dest" || return 1
            else cat "$work/section" > "$work/rendered/$dest" || return 1; fi
        else
            for ignore in $IGNORE_ENTRIES; do
                if ! grep -qxF "$ignore" "$work/rendered/$dest"; then
                    trailing="$(tail -c 1 "$work/rendered/$dest")"
                    { [ -z "$trailing" ] || echo; echo "$ignore"; } >> "$work/rendered/$dest" || return 1
                fi
            done
        fi
    done
    printf '.prdkit/.manifest\0' >> "$work/paths" || return 1
    {
        printf 'version: %s\nharnesses: %s\nmode: %s\noptions: %s\n' "$PK_VERSION" "$HARNESSES" "$($MODE_COPY && echo copy || echo symlink)" "${OPTIONS:-none}"
        if has_harness agents; then printf 'agents-section: %s\n' "$(render_agents_section | install_hash_stream)"; fi
        while IFS=$'\t' read -r src rel; do
            dest="$work/rendered/$rel"
            kit="$(hash_file "$src")"
            if [ -f "$work/rendered-links/$rel" ]; then
                case "$rel" in
                    .claude/commands/*|.opencode/commands/*) cur="$kit" ;;
                    *) [ -e "$TARGET/$rel" ] || continue; cur="$(hash_file "$TARGET/$rel")" ;;
                esac
            else [ -e "$dest" ] || continue; cur="$(hash_file "$dest")"; fi
            hash="$kit"
            if [ "$cur" != "$kit" ]; then rec="$(recorded_hash "$rel")"; hash="${rec:-$kit}"; fi
            printf '%s  %s\n' "$hash" "$rel"
        done < "$work/files"
    } > "$work/rendered/.prdkit/.manifest"
    printf '.prdkit/.manifest.tmp\0' >> "$work/paths" || return 1
    upgrade_head_file .prdkit/.manifest.tmp "$work/head/.prdkit/.manifest.tmp" "$work/head-links/.prdkit/.manifest.tmp" || return 1
    cp "$work/rendered/.prdkit/.manifest" "$work/rendered/.prdkit/.manifest.tmp" || return 1
    printf '.prdkit/.install-state\0' >> "$work/paths" || return 1
    if [ -f "$PRDKIT_DIR/.install-state" ] && [ ! -L "$PRDKIT_DIR/.install-state" ]; then
        cp "$PRDKIT_DIR/.install-state" "$work/rendered/.prdkit/.install-state" || return 1
    fi
}

upgrade_marker_valid() { # rendered work; only metadata the installer actually emits
    local marker="$PRDKIT_DIR/.install-state" phase path version entry
    [ -f "$marker" ] && [ ! -L "$marker" ] && [ ! -x "$marker" ] && verify_lf_file "$marker" || return 1
    if git -C "$TARGET" ls-files --error-unmatch -- .prdkit/.install-state >/dev/null 2>&1; then return 1; fi
    awk '/^(phase|path|source-version): / { n++; next } { bad=1 } END { exit bad || n!=3 }' "$marker" || return 1
    phase="$(install_manifest_value "$marker" phase)" && path="$(install_manifest_value "$marker" path)" &&
        version="$(install_manifest_value "$marker" source-version)" || return 1
    [ "$version" = "$PK_VERSION" ] || return 1
    case "$phase:$path" in
        prepare:.prdkit|managed-section:AGENTS.md|ignore:.gitignore|manifest:.prdkit/.manifest) return 0 ;;
        file:*) awk -F '\t' -v p="$path" '$2==p { found=1 } END { exit !found }' "$1/files"; return ;;
        optional:*) case " $REMOVE_OPTIONS_FILES " in *" $path "*) return 0 ;; esac; return 1 ;;
        retired:*)
            # Validate phase metadata only; dirty paths still need an independently rendered outcome.
            while IFS= read -r entry; do
                if [ -n "$entry" ] && [ "${entry#*  }" = "$path" ]; then return 0; fi
            done <<< "$RETIRED_ENTRIES"
            return 1 ;;
        *) return 1 ;;
    esac
}

upgrade_checkout_clean() (
    local status entry version work rel allowed agents
    work="$(mktemp -d)" || return 1
    trap 'rm -rf "$work"' EXIT
    GIT_OPTIONAL_LOCKS=0 git -C "$TARGET" status --porcelain=v1 -z --untracked-files=all --ignore-submodules=none > "$work/status" || return 1
    if [ ! -s "$work/status" ] && [ ! -e "$PRDKIT_DIR/.install-state" ]; then return 0; fi
    version="$(install_manifest_value "$PRDKIT_DIR/.install-state" source-version 2>/dev/null)" || version=""
    if [ "$version" != "$PK_VERSION" ] || ! git -C "$TARGET" diff --cached --quiet --ignore-submodules=none HEAD --; then
        echo 'P1: checkout must be clean (staged, unstaged and untracked files included); nothing was written.' >&2; return 1
    fi
    upgrade_render_expected "$work" || return 1
    if ! upgrade_marker_valid "$work"; then
        echo 'P1: .prdkit/.install-state is not a valid installer marker for this source; nothing was written.' >&2; return 1
    fi
    agents="$(root_file_path AGENTS.md 2>/dev/null)" || agents=''
    agents="${agents#"$TARGET"/}"
    while IFS= read -r -d '' entry; do
        status="${entry:0:2}"; rel="${entry:3}"
        case "$status" in *R*|*C*) echo "P1: renamed/copied dirty path is not an installer intermediate: $rel; nothing was written." >&2; return 1 ;; esac
        if [ "$rel" = .prdkit/.install-state ]; then continue; fi
        case "$rel" in specs/changes/*) allowed=false ;; *) allowed=true ;; esac
        $allowed || { echo "P1: records must always be clean: $rel" >&2; return 1; }
        allowed=false
        while IFS= read -r -d '' version; do [ "$rel" != "$version" ] || allowed=true; done < "$work/paths"
        if ! $allowed; then
            echo "P1: not a validated installer-owned intermediate state: $rel; nothing was written." >&2; return 1
        fi
        if has_harness agents && [ "$rel" = "$agents" ] && [ -f "$work/head/$rel" ] &&
            managed_agents_section "$work/head/$rel" >/dev/null; then
            upgrade_agents_outside "$TARGET/$rel" > "$work/outside-current" || return 1
            upgrade_agents_outside "$work/head/$rel" > "$work/outside-head" || return 1
            if ! cmp -s "$work/outside-current" "$work/outside-head"; then
                echo "P1: AGENTS.md changed outside the managed section: $rel; nothing was written." >&2; return 1
            fi
        fi
        if ! upgrade_same_file "$TARGET/$rel" "$work/head/$rel" "$work/head-links/$rel" && ! upgrade_same_file "$TARGET/$rel" "$work/rendered/$rel" "$work/rendered-links/$rel"; then
            echo "P1: not a validated intermediate state (bytes, type or mode): $rel; nothing was written." >&2; return 1
        fi
    done < "$work/status"
)

upgrade_agents_outside() { # raw bytes outside the one validated managed block
    local offsets prefix suffix
    managed_agents_section "$1" >/dev/null || return 1
    offsets="$(LC_ALL=C awk -v b="$AGENTS_BEGIN" -v e="$AGENTS_END" '
        BEGIN { bytes=0; first=0 }
        $0==b { first=bytes } { bytes+=length($0)+1 } $0==e { after=bytes }
        END { print first, after+1 }' "$1")" || return 1
    read -r prefix suffix <<< "$offsets"
    install_head_bytes "$prefix" "$1" && tail -c "+$suffix" "$1"
}

# Inspect the entire planned overwrite/delete set, including ignored files. Git cleanliness
# alone is not ownership: a committed customization is still the project's work.
upgrade_no_overwrite() (
    local work rel src dest rec cur section expected problem=false
    work="$(mktemp -d)" || return 1
    trap 'rm -rf "$work"' EXIT
    upgrade_render_expected "$work" || return 1
    while IFS= read -r -d '' rel; do
        dest="$TARGET/$rel"
        { [ -e "$dest" ] || [ -L "$dest" ]; } || continue
        src="$(awk -F '\t' -v p="$rel" '$2==p { print $1 }' "$work/files")"
        case "$rel" in .prdkit/.manifest) continue ;; esac
        if [ -z "$src" ] && { [ "$dest" = "$(root_file_path AGENTS.md 2>/dev/null)" ] || [ "$dest" = "$(root_file_path .gitignore 2>/dev/null)" ]; }; then
            # Root files are edited in place, not replaced as kit-owned documents. Only
            # the managed block is overwritten; the ignore operation only appends entries.
            if has_harness agents && [ "$dest" = "$(root_file_path AGENTS.md 2>/dev/null)" ] && grep -qF "$AGENTS_BEGIN" "$dest"; then
                section="$(managed_agents_section "$dest")" || { echo "Customized managed section: $rel" >&2; problem=true; continue; }
                cur="$(printf '%s\n' "$section" | install_hash_stream)"
                rec="$(install_manifest_value "$MANIFEST" agents-section 2>/dev/null)" || rec=""
                expected="$(render_agents_section | install_hash_stream)"
                if [ "$cur" != "$rec" ] && [ "$cur" != "$expected" ]; then
                    echo "Customized managed section: $rel" >&2; problem=true
                fi
            fi
            continue
        fi
        if upgrade_same_file "$dest" "$work/rendered/$rel" "$work/rendered-links/$rel"; then continue; fi
        case "$rel" in .prdkit/templates/*|.prdkit/memory/*)
            # Non-forced conffiles with custom bytes are preserved by install_conffile.
            if ! $FORCE && [ -n "$src" ]; then continue; fi ;;
        esac
        rec="$(recorded_hash "$rel")"
        if [ -L "$dest" ]; then
            case "$rel" in .claude/commands/*|.opencode/commands/*)
                if is_kit_command_link "$dest"; then continue; fi ;;
            esac
        elif [ -f "$dest" ] && [ -n "$rec" ]; then
            cur="$(hash_file "$dest")" || return 1
            if [ "$cur" = "$rec" ]; then
                if [ -z "$src" ] || { { [ -x "$dest" ] && [ -x "$work/rendered/$rel" ]; } ||
                    { [ ! -x "$dest" ] && [ ! -x "$work/rendered/$rel" ]; }; }; then continue; fi
            fi
        fi
        echo "Customized overwrite/delete destination: $rel" >&2
        problem=true
    done < "$work/paths"
    if $problem; then
        echo 'Refusing to overwrite or delete customized content; nothing was written. Reconcile these files before upgrading.' >&2
        return 1
    fi
)
