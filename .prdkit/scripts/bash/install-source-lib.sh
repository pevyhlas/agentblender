#!/usr/bin/env bash
# Read-only installation proof. Never execute source objects or consult the index.
# Caller sources common.sh, spec-lib.sh and install-lib.sh, supplies private scratch,
# and captures findings on stdout.
#
# Invariants:
# Derive expected kit-owned destinations from the locally available pinned source tree
# using the installer mapping. Compare installed bytes, Git modes, command links and
# managed text; do not require pristine project-owned template or memory bytes.
# Do not fetch or execute source files. Use findings, not function success alone, as evidence.
#
# Threat model:
# Include missing objects, a drifted installation, static path aliases and interrupted
# upgrades. Committed proof ignores worktree/index substitutions; worktree proof reads
# live paths. A hostile writer controlling the local checker or object store is out of scope.

install_object_git() {
    GIT_NO_REPLACE_OBJECTS=1 GIT_NO_LAZY_FETCH=1 GIT_OPTIONAL_LOCKS=0 git "$@"
}

install_object_entry() { # repo revision literal-path -> mode type oid (or empty)
    local entry
    install_object_git -C "$1" ls-tree -z "$2" -- ":(literal)$3" |
        {
            local checked=0 meta
            while IFS= read -r -d '' entry; do
                meta="${entry%%$'\t'*}"
                [[ "$meta" =~ ^[0-9]{6}\ (blob|tree|commit)\ [0-9a-f]{40}$ ]] || return 1
                [ "${entry#*$'\t'}" != "$3" ] || {
                    [ "$checked" -eq 0 ] || return 1
                    checked=1
                    printf '%s\n' "$meta" || return 1
                }
            done
            [ -z "$entry" ] # not an unterminated final tree entry
        }
}

install_candidate_mode() { # root revision-or-empty path
    local entry perms
    if [ -n "$2" ]; then
        entry="$(install_object_entry "$1" "$2" "$3")" || return 1
        printf '%s\n' "${entry%% *}"
    elif [ -L "$1/$3" ]; then echo 120000
    elif [ -f "$1/$3" ]; then
        # Git records the owner execute bit, not effective access, so do not use test -x.
        perms="$(find "$1/$3" -prune -perm -0100)" || return 1
        if [ -n "$perms" ]; then echo 100755; else echo 100644; fi
    elif [ -d "$1/$3" ]; then echo 040000
    elif [ -e "$1/$3" ]; then echo special
    fi
}

install_candidate_read() { # root revision-or-empty path output-file (raw blob, no link following)
    local entry
    if [ -n "$2" ]; then
        entry="$(install_object_entry "$1" "$2" "$3")" || return 1
        case "$entry" in 100644\ blob\ *|100755\ blob\ *|120000\ blob\ *)
            install_object_git -C "$1" cat-file blob "${entry##* }" > "$4" ;; *) return 1 ;; esac
    elif [ -L "$1/$3" ]; then
        # Git stores the link text without readlink's terminating newline.
        local link
        link="$(readlink "$1/$3" && printf '.')" || return 1
        link="${link%.}"
        printf '%s' "${link%$'\n'}" > "$4"
    else cat "$1/$3" > "$4" && cmp -s "$1/$3" "$4"; fi
}

install_config_value() { # config-file key (duplicates are not valid restrictions)
    local expected
    expected="$(wc -l < "$1")" || return 1
    awk -v key="$2" -v expected="$expected" '
        /^##[[:space:]]*Configuration/ { section=1; next }
        /^##[[:space:]]/ { section=0; next }
        section {
            line=$0; sub(/^-?[[:space:]]*/, "", line)
            if(index(line,key ":") == 1) {
                n++; sub(/^[^:]*:[[:space:]]*/, "", line); sub(/\r$/, "", line); value=line
            }
        }
        END { if(n > 1 || NR != expected) exit 1; print value }
    ' "$1"
}

install_normalize_path() { # repo-relative path; reject escape, empty or absolute
    local part out='' rest="$1"
    case "$rest" in ''|/*) return 1 ;; esac
    while [ -n "$rest" ]; do
        part="${rest%%/*}"
        if [ "$rest" = "$part" ]; then rest=''; else rest="${rest#*/}"; fi
        case "$part" in
            ''|.) ;;
            ..) [ -n "$out" ] || return 1
                case "$out" in */*) out="${out%/*}" ;; *) out='' ;; esac ;;
            *) out="${out:+$out/}$part" ;;
        esac
    done
    [ -n "$out" ] || return 1
    printf '%s\n' "$out"
}

# Resolve one component at a time in the selected lane; never inspect a worktree
# child through an unresolved parent. Return 2 for absence, 1 for an invalid alias.
install_resolve_path() { # root revision-or-empty path scratch
    local rest="$3" dir='' part path mode link hops=0
    while [ -n "$rest" ]; do
        part="${rest%%/*}"
        if [ "$part" = "$rest" ]; then rest=''; else rest="${rest#*/}"; fi
        case "$part" in
            ''|.) continue ;;
            ..) [ -n "$dir" ] || return 1
                case "$dir" in */*) dir="${dir%/*}" ;; *) dir='' ;; esac; continue ;;
            .[gG][iI][tT]) return 1 ;;
        esac
        path="${dir:+$dir/}$part"
        mode="$(install_candidate_mode "$1" "$2" "$path")" || return 1
        case "$mode" in
            120000)
                hops=$((hops+1)); [ "$hops" -le 40 ] || return 1
                install_candidate_read "$1" "$2" "$path" "$4/link" &&
                    review_file_without_nul "$4/link" || return 1
                link="$(cat "$4/link" && printf '.')" || return 1; link="${link%.}"
                case "$link" in ''|/*|*$'\n'*|*$'\r'*|*$'\t'*) return 1 ;; esac
                case "$link" in */) link="${link}." ;; esac
                rest="$link${rest:+/$rest}" ;;
            '') [ "$hops" -eq 0 ] || return 1; return 2 ;;
            *) [ -z "$rest" ] || [ "$mode" = 040000 ] || return 1
                dir="$path" ;;
        esac
    done
    [ -n "$dir" ] || return 1
    printf '%s\n' "$dir"
}

install_source_verify() { # consumer-root consumer-revision-or-empty scratch
    local root="$1" head="$2" work="$3" source pin restriction manifest_hash repo entry
    local version manifest_version harnesses selection options mode src dest oid smode expected actual line hash path n parent count checked
    # Public result fields are consumed by check-adoption.sh, not this library.
    # shellcheck disable=SC2034
    INSTALL_SOURCE_REVISION=''
    INSTALL_CONSUMER_REVISION=''
    # Refuse partial-clone reads on Git versions that cannot suppress lazy fetching.
    spec_objects_local "$root" || { echo 'Source proof unavailable: consumer Git cannot guarantee local-only object reads'; return; }
    INSTALL_CONSUMER_REVISION="$(install_object_git -C "$root" rev-parse --verify "${head:-HEAD}^{commit}" 2>/dev/null)" ||
        { echo 'Source proof unavailable: consumer commit is absent'; return; }
    if [ -n "$head" ]; then head="$INSTALL_CONSUMER_REVISION"; fi
    if path="$(install_resolve_path "$root" "$head" .prdkit/memory/config.md "$work")"; then
        case "$(install_candidate_mode "$root" "$head" "$path")" in
            100644|100755)
                if ! install_candidate_read "$root" "$head" "$path" "$work/config" 2>/dev/null ||
                    ! verify_lf_file "$work/config"; then
                    echo 'Invalid or unreadable adoption configuration: expected non-empty LF-terminated text'; return
                fi ;;
            *) echo 'Adoption configuration must be a regular file'; return ;;
        esac
    elif [ "$?" -eq 2 ]; then : > "$work/config" || return 1
    else echo 'Cannot resolve adoption configuration inside repository'; return
    fi
    source="$(install_config_value "$work/config" adoption-source)" ||
        { echo 'Ambiguous adoption-source restriction'; return; }
    source="${source:-.product-kit}"
    if ! [[ "$source" =~ ^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$ ]] ||
        [ "$(install_normalize_path "$source" || true)" != "$source" ]; then
        echo 'Invalid adoption-source: expected a normalized repo-relative path'; return
    fi
    entry="$(install_object_entry "$root" "$INSTALL_CONSUMER_REVISION" "$source")" ||
        { echo 'Source proof unavailable: cannot read committed gitlink'; return; }
    case "$entry" in 160000\ commit\ *) pin="${entry##* }" ;; *)
        echo "Source proof unavailable: $source is not a committed gitlink"; return ;; esac
    # shellcheck disable=SC2034
    INSTALL_SOURCE_REVISION="$pin"
    restriction="$(install_config_value "$work/config" adoption-pin)" ||
        { echo 'Ambiguous adoption-pin restriction'; return; }
    if [ -n "$restriction" ] && { ! [[ "$restriction" =~ ^[0-9a-f]{40}$ ]] || [ "$restriction" != "$pin" ]; }; then
        echo "Kit pin drift: committed $source is at $pin, adoption contract pins $restriction"
    fi
    case "$(install_candidate_mode "$root" "$head" .prdkit/.manifest)" in
        100644) ;; *) echo 'Manifest must be a regular non-executable file'; return ;; esac
    if ! install_candidate_read "$root" "$head" .prdkit/.manifest "$work/manifest" 2>/dev/null; then
        echo 'Source proof unavailable: missing install manifest'; return
    fi
    verify_lf_file "$work/manifest" || { echo 'Invalid or unreadable adoption manifest: expected non-empty LF-terminated text'; return; }
    restriction="$(install_config_value "$work/config" adoption-manifest-sha256)" ||
        { echo 'Ambiguous adoption-manifest-sha256 restriction'; return; }
    manifest_hash="$(install_hash_stream < "$work/manifest")" || return 1
    if [ -n "$restriction" ] && [ "$restriction" != "$manifest_hash" ]; then
        echo "Manifest hash drift: $manifest_hash, adoption contract records $restriction"
    fi
    # A missing/uninitialized submodule must not accidentally resolve to the consumer repo.
    repo="$root/$source"
    if [ ! -d "$repo" ] || [ -L "$repo" ] ||
        [ "$(install_object_git -C "$repo" rev-parse --show-toplevel 2>/dev/null)" != "$(CDPATH='' cd "$repo" && pwd -P)" ]; then
        echo "Source proof unavailable: local source object repository absent at $source"; return
    fi
    spec_objects_local "$repo" || { echo 'Source proof unavailable: source Git cannot guarantee local-only object reads'; return; }
    install_object_git -C "$repo" cat-file -e "$pin^{commit}" 2>/dev/null ||
        { echo "Source proof unavailable: missing source commit $pin (no fetch performed)"; return; }
    install_object_git -C "$repo" ls-tree -r "$pin" > "$work/source-tree" 2>/dev/null ||
        { echo 'Source proof unavailable: missing source tree objects'; return; }
    install_object_git -C "$repo" cat-file blob "$pin:VERSION" > "$work/version" 2>/dev/null ||
        { echo 'Source proof unavailable: missing source VERSION object'; return; }
    verify_lf_file "$work/version" || { echo 'Invalid pinned source VERSION'; return; }
    version="$(tr -d '[:space:]' < "$work/version")" || return 1
    manifest_version="$(install_manifest_value "$work/manifest" version)" ||
        { echo 'Manifest must record exactly one readable version'; return; }
    [ -n "$version" ] && [ "$manifest_version" = "$version" ] ||
        echo "Manifest version does not match pinned source $version"
    harnesses="$(install_manifest_value "$work/manifest" harnesses)" ||
        { echo 'Manifest must record exactly one harness selection'; return; }
    selection=''
    for path in $harnesses; do
        case "$path" in claude|opencode|codex|agents) ;; *) echo "Unknown manifest harness: $path"; return ;; esac
        selection="${selection:+$selection }$path"
    done
    [ -n "$selection" ] || { echo 'Manifest must select a harness'; return; }
    harnesses="$selection"
    case " $harnesses " in *' codex '*) case " $harnesses " in *' agents '*) ;; *)
        echo 'Codex selection must include agents'; return ;; esac ;; esac
    options="$(install_manifest_value "$work/manifest" options)" ||
        { echo 'Manifest must record exactly one optional-component selection'; return; }
    case "$options" in none|'agent-tiers') ;; *) echo "Unknown manifest options: $options"; return ;; esac
    if [ "$options" = none ]; then
        if [ -n "$head" ]; then
            if path="$(install_resolve_path "$root" "$head" .prdkit/agent-library "$work")"; then
                case "$(install_candidate_mode "$root" "$head" "$path")" in
                    040000|160000) echo 'Agent tiers are in use but the manifest deselects agent-tiers' ;;
                esac
            elif [ "$?" -ne 2 ]; then echo 'Cannot resolve agent library'; fi
            if parent="$(install_resolve_path "$root" "$head" .prdkit/agents "$work")"; then
                case "$(install_candidate_mode "$root" "$head" "$parent")" in
                160000) echo 'Agent definitions unavailable: directory is a committed gitlink'; return ;;
                040000)
                    install_object_git -C "$root" ls-tree -z --name-only "$head:$parent" > "$work/definitions" ||
                        { echo 'Cannot inventory agent definitions'; return; }
                    count="$(verify_nul_count "$work/definitions")" || { echo 'Incomplete agent definitions inventory'; return; }
                    checked=0
                    while IFS= read -r -d '' path; do
                        checked=$((checked + 1))
                        [ -n "$path" ] || { echo 'Empty agent definitions path'; continue; }
                        case "$path" in .*) continue ;; *.md) ;; *) continue ;; esac
                        if dest="$(install_resolve_path "$root" "$head" "$parent/$path" "$work")"; then
                            case "$(install_candidate_mode "$root" "$head" "$dest")" in
                                100644|100755) echo 'Agent tiers are in use but the manifest deselects agent-tiers' ;;
                            esac
                        else echo 'Cannot resolve agent definition'; fi
                    done < "$work/definitions"
                    [ "$count" -eq "$checked" ] || echo "Incomplete agent definitions inventory: expected $count, checked $checked"
                    ;;
                esac
            elif [ "$?" -ne 2 ]; then echo 'Cannot resolve agent definitions directory'; fi
        else
            [ ! -d "$root/.prdkit/agent-library" ] || echo 'Agent tiers are in use but the manifest deselects agent-tiers'
            for path in "$root"/.prdkit/agents/*.md; do
                if [ -f "$path" ]; then echo 'Agent tiers are in use but the manifest deselects agent-tiers'; break; fi
            done
        fi
    fi
    mode="$(install_manifest_value "$work/manifest" mode)" ||
        { echo 'Manifest must record exactly one command mode'; return; }
    case "$mode" in copy|symlink) ;; *) echo "Unknown manifest command mode: $mode"; return ;; esac
    verify_lf_file "$work/source-tree" || { echo 'Incomplete pinned source tree: expected non-empty LF-terminated records'; return; }
    count="$(wc -l < "$work/source-tree")" || return 1
    checked=0
    : > "$work/mapping" || { echo 'Unable to build installation mapping'; return; }
    while IFS=$'\t' read -r entry src; do
        checked=$((checked + 1))
        if [ -z "$entry" ] || [ -z "$src" ]; then echo 'Malformed pinned source tree entry'; continue; fi
        # Only flat kit directories are installable. Quoted/unusual Git paths are never operands.
        [[ "$src" =~ ^(commands|scripts|templates|workflows)/[A-Za-z0-9_][A-Za-z0-9_.-]*$ ]] || continue
        install_file_targets "$src" "$harnesses" "$options" "$entry"$'\t'"$src"$'\t' >> "$work/mapping" || {
            echo "Unable to build installation mapping: $src"; return
        }
    done < "$work/source-tree"
    [ "$count" -eq "$checked" ] || { echo "Incomplete pinned source tree: expected $count lines, checked $checked"; return; }
    verify_lf_file "$work/mapping" || { echo 'Pinned source provides no complete installation mapping'; return; }
    count="$(wc -l < "$work/mapping")" || return 1
    checked=0
    while IFS=$'\t' read -r entry src dest; do
        checked=$((checked + 1))
        if [ -z "$src" ] || [ -z "$dest" ]; then echo 'Malformed installation mapping entry'; continue; fi
        smode="${entry%% *}"; oid="${entry##* }"
        case "$entry" in 100644\ blob\ *|100755\ blob\ *) ;; *)
            echo "Pinned source is not a regular file: $src"; continue ;; esac
        if ! install_object_git -C "$repo" cat-file blob "$oid" > "$work/blob" 2>/dev/null; then
            echo "Missing pinned source object: $src"; continue
        fi
        n="$(awk -v p="$dest" 'substr($0,67) == p { n++ } END { print n+0 }' "$work/manifest")" || return 1
        [ "$n" -eq 1 ] || echo "Manifest must record exactly one installed path: $dest"
        # The installer refuses symlinked managed directories, including command parents.
        parent="${dest%/*}"
        if [ -z "$head" ]; then
            while [ "$parent" != . ] && [ -n "$parent" ]; do
                [ ! -L "$root/$parent" ] || echo "Symlinked installation directory: $parent"
                case "$parent" in */*) parent="${parent%/*}" ;; *) break ;; esac
            done
        fi
        case "$dest" in .prdkit/templates/*|.prdkit/memory/*)
            # Conffiles are project-owned, including their prior pristine hash.
            case "$(install_candidate_mode "$root" "$head" "$dest")" in
                100644|100755|120000) ;; *) echo "Missing project-owned installed path or invalid type: $dest" ;; esac
            continue ;;
        esac
        expected="$(install_hash_stream < "$work/blob")" || return 1
        hash="$(awk -v p="$dest" 'substr($0,67) == p { print substr($0,1,64) }' "$work/manifest")" || return 1
        [ "$hash" = "$expected" ] || echo "Manifest differs from pinned source: $dest"
        actual="$(install_candidate_mode "$root" "$head" "$dest")" || return 1
        case "$dest:$mode" in
            .claude/commands/*:symlink|.opencode/commands/*:symlink)
                [ "$actual" = 120000 ] || { echo "Expected command symlink: $dest"; continue; }
                install_candidate_read "$root" "$head" "$dest" "$work/link" 2>/dev/null ||
                    { echo "Unreadable command link: $dest"; continue; }
                printf '%s' "../../$source/$src" > "$work/expected-link" || return 1
                cmp -s "$work/link" "$work/expected-link" || echo "Wrong command link target: $dest"
                if [ -z "$head" ]; then
                    # A resolving link can still serve modified source-worktree bytes.
                    cmp -s "$root/$dest" "$work/blob" || echo "Command link content differs from pinned source: $dest"
                fi
                continue ;;
            .prdkit/scripts/bash/*.sh:*) smode=100755 ;;
        esac
        [ "$actual" = "$smode" ] ||
            { echo "Installed Git mode mismatch: $dest (expected $smode, got ${actual:-absent})"; continue; }
        if ! install_candidate_read "$root" "$head" "$dest" "$work/installed" 2>/dev/null ||
            ! cmp -s "$work/blob" "$work/installed"; then
            echo "Installed content differs from pinned source: $dest"
        fi
    done < "$work/mapping"
    [ "$count" -eq "$checked" ] || { echo "Incomplete installation mapping: expected $count lines, checked $checked"; return; }
    # All manifest data must be well-formed. Extra conffiles can be legitimate retirements;
    # extra kit-owned entries cannot authenticate themselves with a copied hash.
    count="$(wc -l < "$work/manifest")" || return 1
    checked=0
    while IFS= read -r line; do
        checked=$((checked + 1))
        case "$line" in version:*|harnesses:*|mode:*|options:*|agents-section:*|'') continue ;; esac
        hash="${line%%  *}"; path="${line#*  }"
        if ! [[ "$hash" =~ ^[0-9a-f]{64}$ ]] ||
            ! [[ "$path" =~ ^(\.prdkit/(scripts/bash|workflows|templates|memory)|\.(claude|opencode)/commands)/[A-Za-z0-9_][A-Za-z0-9_.-]*$ ]]; then
            echo "Invalid manifest entry: $line"; continue
        fi
        case "$path" in .prdkit/templates/*|.prdkit/memory/*) continue ;; esac
        LC_ALL=C PRDKIT_INSTALL_PATH="$path" awk -F '\t' '$3 == ENVIRON["PRDKIT_INSTALL_PATH"] { found=1 } END { exit !found }' "$work/mapping" ||
            echo "Unexpected installed manifest path: $path"
    done < "$work/manifest"
    [ "$count" -eq "$checked" ] || { echo "Incomplete adoption manifest: expected $count lines, checked $checked"; return; }
    if [ -n "$head" ]; then
        install_object_git -C "$root" ls-tree -r -z --name-only "$head" -- .prdkit/scripts/bash/ .prdkit/workflows/ \
            .claude/commands/ .opencode/commands/ > "$work/installed-paths" ||
            { echo 'Unable to inventory committed installation'; return; }
    else
        : > "$work/installed-paths" || return 1
        for parent in .prdkit/scripts/bash .prdkit/workflows .claude/commands .opencode/commands; do
            [ ! -d "$root/$parent" ] || find "$root/$parent" ! -path "$root/$parent" -prune -print0 >> "$work/installed-paths" ||
                { echo "Unable to inventory worktree installation: $parent"; return; }
        done
    fi
    count="$(verify_nul_count "$work/installed-paths")" || { echo 'Incomplete installed path inventory'; return; }
    checked=0
    : > "$work/seen-paths" || return 1
    while IFS= read -r -d '' path; do
        checked=$((checked + 1))
        path="${path#"$root"/}"
        [ -n "$path" ] || { echo 'Empty installed path'; continue; }
        case "$path" in .claude/commands/*|.opencode/commands/*)
            case "${path##*/}" in prdkit*.md) ;; *) continue ;; esac ;; esac
        if LC_ALL=C PRDKIT_INSTALL_PATH="$path" awk -F '\t' '$3 == ENVIRON["PRDKIT_INSTALL_PATH"] { found=1 } END { exit !found }' "$work/mapping"; then
            printf '%s\n' "$path" >> "$work/seen-paths" || return 1
        else
            echo "Unexpected kit-owned installed path: $path"
        fi
    done < "$work/installed-paths"
    [ "$count" -eq "$checked" ] || echo "Incomplete installed path inventory: expected $count, checked $checked"
    # Independent expectation: every mapped kit-owned file must occur exactly once,
    # even when the file data itself was readable through a separate lookup.
    awk -F '\t' '
        FILENAME == ARGV[1] { seen[$0]++; next }
        $3 !~ /^\.prdkit\/(templates|memory)\// {
            expected++; if (seen[$3] == 1) checked++
        }
        END {
            if (expected != checked || !expected)
                print "Incomplete kit-owned installation inventory: expected " expected+0 ", checked " checked+0
        }' "$work/seen-paths" "$work/mapping" || return 1
    for path in .prdkit/.install-state .prdkit/.install-lock; do
        actual="$(install_candidate_mode "$root" "$head" "$path")" || return 1
        [ -z "$actual" ] ||
            echo "Installer runtime artifact prevents source proof: $path"
    done
    if path="$(install_resolve_path "$root" "$head" AGENTS.md "$work")"; then
        actual="$(install_candidate_mode "$root" "$head" "$path")" || return 1
        if { [ "$actual" != 100644 ] && [ "$actual" != 100755 ]; } ||
            ! install_candidate_read "$root" "$head" "$path" "$work/agents" 2>/dev/null; then
            echo 'Cannot read AGENTS.md inside repository'; return
        fi
    elif [ "$?" -ne 2 ]; then echo 'Cannot resolve AGENTS.md inside repository'; return
    fi
    case " $harnesses " in *' agents '*)
        if ! managed_agents_section "$work/agents" > "$work/section"; then
            echo 'Missing or malformed Product-Kit managed section in AGENTS.md'
        elif ! install_object_git -C "$repo" cat-file blob "$pin:scripts/agents-section.md" > "$work/expected-section" 2>/dev/null; then
            echo 'Missing pinned managed-section source object'
        else
            cmp -s "$work/section" "$work/expected-section" || echo 'AGENTS.md managed section differs from pinned source'
            expected="$(install_hash_stream < "$work/expected-section")" || return 1
            [ "$(install_manifest_value "$work/manifest" agents-section || true)" = "$expected" ] ||
                echo 'Manifest managed-section hash differs from pinned source'
        fi ;;
    *)
        if grep '^agents-section:' "$work/manifest" >/dev/null; then echo 'Deselected agents harness retains managed-section metadata'
        elif [ "$?" -ne 1 ]; then return 1; fi
        if [ -f "$work/agents" ]; then
            if ! review_file_without_nul "$work/agents"; then echo 'Unreadable AGENTS.md'
            elif grep -Fx -e "$AGENTS_BEGIN" -e "$AGENTS_END" \
                -e "$AGENTS_BEGIN"$'\r' -e "$AGENTS_END"$'\r' "$work/agents" >/dev/null; then
                echo 'Deselected agents harness retains managed-section markers'
            elif [ "$?" -ne 1 ]; then echo 'Unreadable AGENTS.md'; fi
        fi ;;
    esac
}
