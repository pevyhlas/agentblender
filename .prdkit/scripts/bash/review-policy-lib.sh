#!/usr/bin/env bash
# Reviewer eligibility from committed policy. Source spec-lib.sh first. No network/dependencies.
# shellcheck disable=SC2154 # root is supplied by the gate.
read_rigor() { # rev -> light|full|unknown; caller supplies root
    { git -C "$root" show "$1:.prdkit/memory/constitution.md" 2>/dev/null || true; } | read_rigor_text
}

read_rigor_text() { # validated constitution bytes on stdin; the same strict grammar
    tr -d '\r' | awk '
        { line = $0 }
        sub(/^[-*[:space:]]*[Rr]igor\**:\**[[:space:]]*/, "", line) { n++; value = tolower(line) }
        END {
            if (n == 1 && value ~ /^(light|full)([[:space:]]|$)/) { sub(/[[:space:]].*/, "", value); print value }
            else print "unknown"
        }'
}

review_registry() { # agents.md on stdin -> identity, role, policy line
    awk "$SPEC_SKIP_AWK"'
        function trim(s) { gsub(/^[[:space:]`]+|[[:space:]`]+$/, "", s); return tolower(s) }
        { if (spec_skip($0)) next }
        /^## / { section = $0; identity = role = 0; next }
        /^\|/ {
            n = split($0, c, "|")
            if (section == "## Review registry" && trim(c[2]) == "identity" && trim(c[3]) == "role") {
                identity = 2; role = 3; next
            }
            if (!identity || c[2] ~ /^[[:space:]:-]+$/) next
            id = trim(c[identity]); kind = trim(c[role])
            if (id !~ /^[a-z0-9][a-z0-9_.-]*$/) next
            if (kind != "reviewer" && kind != "maker") kind = "invalid"
            print id "\037" kind "\037" NR
            next
        }
        /^[[:space:]]*$/ { next }
        { identity = role = 0 }
    '
}

review_eligibility() { # registry identity -> reviewer|maker|ambiguous|unregistered
    printf '%s\n' "$1" | awk -F '\037' -v id="$2" '
        $1 == id { n++; if ($2 == "maker") maker = 1; if ($2 != "reviewer") invalid = 1 }
        END {
            if (maker) print "maker"
            else if (n > 1 || invalid) print "ambiguous"
            else if (n == 1) print "reviewer"
            else print "unregistered"
        }'
}

# Review text in State is not an authority in the current record format.
review_record_format() { # record file -> 0 current, 1 unreadable, 2 unsupported review field
    verify_lf_file "$1" || return 1
    local found
    found="$(LC_ALL=C awk "$SPEC_SKIP_AWK"'
        { gsub(/[\035-\037]/, " "); if (spec_skip($0)) next }
        /^ ? ? ?##([[:space:]]|$)/ { state = ($0 ~ /^ ? ? ?##[[:space:]]+State([[:space:]]*|[[:space:]]+#+[[:space:]]*)$/) }
        state && /(^[[:space:]]*|·[[:space:]]*)\*\*Review\*\*:/ { print NR; exit }
    ' "$1")" || return 1
    [ -z "$found" ] || return 2
}

# Check the inherited checked record and both sides of every changed record. A
# candidate cannot erase unsupported evidence by deleting or moving its record
# or by merging away a branch-side commit. No verdict parsing or clearance.
review_record_history() ( # root id base head work -> first diagnostic, if any
    local root="$1" id="$2" base="$3" head="$4" work="$5" start entry commit meta mode new_mode old new blob expected checked=0
    # Object-read settings are intentionally local to this scan.
    # shellcheck disable=SC2030
    export GIT_NO_REPLACE_OBJECTS=1 GIT_NO_LAZY_FETCH=1
    set -o pipefail
    spec_objects_local "$root" &&
        [ "$(git -C "$root" rev-parse --is-shallow-repository)" = false ] &&
        start="$(git -C "$root" merge-base --all "$base" "$head")" &&
        [[ -n "$start" && "$start" != *$'\n'* ]] || return 1
    entry="$(git -C "$root" ls-tree "$start" -- "specs/changes/$id.md")" || return 1
    : > "$work/record-blobs" || return 1
    case "$entry" in
        '100644 blob '*|'100755 blob '*)
            meta="${entry%%$'\t'*}"
            printf '%s\n' "${meta##* }" >> "$work/record-blobs" || return 1 ;;
    esac
    git -C "$root" rev-list "$start..$head" > "$work/record-commits" || return 1
    verify_lf_file "$work/record-commits" allow-empty || return 1
    # Include the net diff so an inherited record changed across the range is
    # checked too; use all branch-side merge parents, not a simplified log.
    git -C "$root" diff-tree --no-commit-id --no-renames --no-abbrev -r -z "$start" "$head" -- specs/changes/ > "$work/record-diff" || return 1
    while IFS= read -r commit; do
        git -C "$root" diff-tree --no-commit-id --no-renames --no-abbrev --root -m -r -z "$commit" -- specs/changes/ >> "$work/record-diff" || return 1
    done < "$work/record-commits"
    expected="$(verify_nul_count "$work/record-diff")" || return 1
    while IFS= read -r -d '' meta; do
        [[ "$meta" =~ ^:[0-7]{6}\ [0-7]{6}\ [0-9a-f]+\ [0-9a-f]+\ [AMDT]$ ]] || return 1
        IFS= read -r -d '' entry || return 1
        [ -n "$entry" ] || return 1
        checked=$((checked + 2))
        case "$entry" in *.md) ;; *) continue ;; esac
        read -r mode new_mode old new _ <<< "${meta#:}"
        case "$mode" in 100644|100755) printf '%s\n' "$old" >> "$work/record-blobs" || return 1 ;; esac
        case "$new_mode" in 100644|100755) printf '%s\n' "$new" >> "$work/record-blobs" || return 1 ;; esac
    done < "$work/record-diff"
    [ "$checked" -eq "$expected" ] || return 1
    LC_ALL=C sort -u "$work/record-blobs" > "$work/record-unique" || return 1
    while IFS= read -r blob; do
        git -C "$root" cat-file blob "$blob" > "$work/record-text" || return 1
        if review_record_format "$work/record-text"; then continue
        elif [ "$?" -eq 2 ]; then
            printf 'Unsupported **Review** field in change record history (blob %s): current reviews are native GitHub reviews, not State text.\n' "$blob"
            return 0
        else
            printf 'Invalid change record text in history (blob %s): expected LF-terminated Markdown.\n' "$blob"
            return 0
        fi
    done < "$work/record-unique"
)

# Convert NUL-delimited Git paths to an injective line format for portable awk.
# Escape '%' first; tabs, spaces and non-ASCII bytes can be kept verbatim.
review_encode_record_paths() { # file -> paths with %, LF and CR escaped
    local file
    while IFS= read -r -d '' file; do
        [ -n "$file" ] || return 1
        case "$file" in specs/changes/*.md) ;; *) continue ;; esac
        file="${file//%/%25}"
        file="${file//$'\n'/%0A}"
        file="${file//$'\r'/%0D}"
        printf '%s\n' "$file" || return 1
    done < "$1" || return 1
    [ -z "$file" ]
}

# Tier attribution is narrower than format validation. Keep the net diff and
# every branch-side commit, but do not count an unchanged import from the target.
review_touched_paths() ( # root start head work -> escaped paths, one per line
    local root="$1" start="$2" head="$3" work="$4"
    # Set object-read settings independently of the format scan.
    # shellcheck disable=SC2031
    export GIT_NO_REPLACE_OBJECTS=1 GIT_NO_LAZY_FETCH=1 LC_ALL=C
    set -o pipefail
    git -C "$root" rev-list --boundary --parents "$start..$head" > "$work/tier-commits" &&
        verify_lf_file "$work/tier-commits" allow-empty || return 1
    # With a single merge base, the boundary parents are exactly the parents
    # also reachable from the target. For a sync, compare to those parents only;
    # for other merges, count resolutions differing from every parent. Using a
    # combined diff preserves this rule for octopus merges too.
    awk '
        FNR == NR { if ($1 ~ /^-/) boundary[substr($1, 2)] = 1; next }
        $1 !~ /^-/ {
            parents = ""
            if (NF > 2)
                for (i = 2; i <= NF; i++)
                    if ($i in boundary) parents = parents " " $i
            if (parents != "") print $1 parents; else print
        }
    ' "$work/tier-commits" "$work/tier-commits" > "$work/tier-parent-diffs" || return 1
    git -C "$root" diff-tree --no-commit-id --name-only --no-renames --ignore-submodules=none -r -z \
        "$start" "$head" -- specs/changes/ > "$work/tier-diff" &&
        git -C "$root" diff-tree --stdin --root --no-commit-id --name-only --no-renames --ignore-submodules=none -c -r -z \
            -- specs/changes/ < "$work/tier-parent-diffs" >> "$work/tier-diff" || return 1
    review_encode_record_paths "$work/tier-diff" | awk '!seen[$0]++'
)
