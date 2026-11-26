#!/usr/bin/env bash
# Common functions for all scripts.

# Get repository root, with fallback for non-git repositories.
# Resolved from THIS FILE's location, not the caller's cwd: an installed
# script invoked from some other git repository must operate on the project
# it is installed in, never on whatever repo the shell happens to be in.
get_repo_root() {
    local script_dir
    script_dir="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    if git -C "$script_dir" rev-parse --show-toplevel >/dev/null 2>&1; then
        git -C "$script_dir" rev-parse --show-toplevel
    else
        # Fall back to the install layout (.prdkit/scripts/bash -> repo root)
        (cd "$script_dir/../../.." && pwd)
    fi
}

# Stop a consumer-facing script run from the kit source (for example .product-kit/scripts/x.sh):
# it resolves its root from its own location, so it would check or write the kit checkout
# instead of the project. The installed copy lives in <project>/.prdkit/scripts/bash/.
require_consumer_root() { # root script-name
    [ -d "$1/.prdkit" ] && return 0
    echo "$2: resolved root $1 has no .prdkit/ (a kit-source run, or a project nested inside another Git repository); run the installed copy <project>/.prdkit/scripts/bash/$2 from a project whose Git root holds .prdkit/" >&2
    exit 2
}

# A change id is NNN-short-name: digits, a hyphen, then lowercase/digit words joined by
# single hyphens. It never contains a slash, a dot segment or whitespace, so it is safe
# to join into a path.
is_change_id() {
    [[ "$1" =~ ^[0-9]{3,}-[a-z0-9]+(-[a-z0-9]+)*$ ]]
}

# Byte checks must not use awk or shell strings: either can discard NUL.
# Return 0 for NUL-free input, 1 for NUL, 2 for an unreadable input/check failure.
review_file_without_nul() (
    set -o pipefail
    local size kept
    size="$(wc -c < "$1" | tr -d '[:space:]')" || return 2
    kept="$(LC_ALL=C tr -d '\000' < "$1" | wc -c | tr -d '[:space:]')" || return 2
    [ -n "$size" ] && [ -n "$kept" ] || return 2
    [ "$size" = "$kept" ]
)

# Text-only policy/protocol inputs must reject NUL before any awk or shell decoding.
review_text_stream() (
    local file
    file="$(mktemp)" || return 1
    trap 'rm -f "$file"' EXIT
    cat > "$file" && review_file_without_nul "$file" && cat "$file"
)

# Line-oriented verifier input must be ordinary LF text. Only callers with an
# explicitly optional/empty inventory may pass allow-empty.
verify_lf_file() (
    set -o pipefail
    local bytes kept last
    bytes="$(wc -c < "$1" | tr -d '[:space:]')" || return 1
    [[ "$bytes" =~ ^[0-9]+$ ]] || return 1
    if [ "$bytes" -eq 0 ]; then [ "${2:-}" = allow-empty ]; return; fi
    kept="$(LC_ALL=C tr -d '\000\r' < "$1" | wc -c | tr -d '[:space:]')" || return 1
    [ "$bytes" = "$kept" ] || return 1
    last="$(tail -c 1 "$1" && printf x)" || return 1
    [ "$last" = $'\nx' ]
)

# Materialize a line producer before consuming its output. Preserve the final
# delimiter until it is checked; command substitution otherwise hides a short
# final record. Empty successful streams are allowed by this transport helper.
verify_lines() (
    set -o pipefail
    local rows
    rows="$("$@" | {
        if IFS= read -r -d '' rows; then
            printf 'Invalid NUL in output from %s\n' "$1" >&2
            exit 1
        fi
        printf '%sx' "$rows"
    })" || {
        printf 'Incomplete output from %s: producer or reader failed\n' "$1" >&2
        return 1
    }
    rows="${rows%x}"
    case "$rows" in
        ''|*$'\n') ;;
        *) printf 'Incomplete output from %s: missing final LF\n' "$1" >&2; return 1 ;;
    esac
    case "$rows" in *$'\r'*) printf 'Invalid CR in output from %s\n' "$1" >&2; return 1 ;; esac
    printf '%s' "$rows"
)

# Count NUL records in a completed spool without putting binary data into a
# shell variable. The consumer compares this count with successful reads.
verify_nul_count() (
    set -o pipefail
    local last
    [ -s "$1" ] || { [ -r "$1" ] && printf '0\n'; return; }
    last="$(tail -c 1 "$1" | od -An -tu1 | tr -d '[:space:]')" || return 1
    [ "$last" = 0 ] || return 1
    LC_ALL=C tr -cd '\000' < "$1" | wc -c | tr -d '[:space:]'
)

# Escape a UTF-8 value for embedding inside a double-quoted JSON string. Bash
# strings cannot contain NUL; every other U+0001–U+001F control must be escaped.
json_escape() {
    local s="$1" code octal character escaped
    s="${s//\\/\\\\}"
    s="${s//\"/\\\"}"
    s="${s//$'\n'/\\n}"
    s="${s//$'\r'/\\r}"
    s="${s//$'\t'/\\t}"
    # Keep the common path cheap; use Bash builtins (including on macOS 3.2),
    # not locale-sensitive byte slicing or a required external JSON runtime.
    if [[ "$s" == *[$'\001'-$'\037']* ]]; then
        for ((code=1; code<32; code++)); do
            printf -v octal '%03o' "$code"
            printf -v character '%b' "\\$octal"
            printf -v escaped '\\u%04x' "$code"
            s="${s//"$character"/$escaped}"
        done
    fi
    printf '%s' "$s"
}

# Print the arguments as a JSON array of strings.
json_array() {
    local value separator=""
    printf '['
    for value in "$@"; do
        printf '%s"%s"' "$separator" "$(json_escape "$value")"
        separator=,
    done
    printf ']'
}
