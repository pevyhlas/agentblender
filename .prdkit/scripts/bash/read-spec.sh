#!/usr/bin/env bash
# Read routine spec context with original line numbers; raw source remains the review artifact.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/spec-lib.sh"
mode=compact; selector=""; path=""
usage() {
    cat <<'USAGE'
Usage: read-spec.sh [--raw | --stats | --target <nav-key> | --index | --section <heading-or-id>] specs/<file>.md
  --index                  List ATX headings with source lines and reading-line counts.
  --section <heading-or-id> Print one section with its source blob and original line numbers.
                           Quote multiword headings; no match or ambiguity exits 2 and lists candidates.
                           Both focused modes require supported spec Markdown; use --raw otherwise.
  --target <nav-key>       Resolve one validated navigation key.
  --stats                  Show reading-budget and navigation statistics.
  --raw                    Print exact source bytes.
USAGE
}
while [ "$#" -gt 0 ]; do
    case "$1" in
        --raw|--stats|--index)
            [ "$mode" = compact ] || { usage >&2; exit 2; }
            mode="${1#--}" ;;
        --target)
            [ "$mode" = compact ] && [ "$#" -gt 1 ] || { usage >&2; exit 2; }
            mode=target; shift; selector="$1" ;;
        --section)
            [ "$mode" = compact ] || { usage >&2; exit 2; }
            mode=section
            # A lone path after --section still lets us report the available candidates.
            if [ "$#" -gt 1 ] && { [ -n "$path" ] || [ "$#" -gt 2 ] || [[ "$2" != specs/*.md ]]; }; then
                shift; selector="$1"
            fi ;;
        --help|-h) usage; exit 0 ;;
        -*) usage >&2; exit 2 ;;
        *)
            [ -z "$path" ] || { usage >&2; exit 2; }
            path="$1" ;;
    esac
    shift
done
case "$path" in specs/*.md) ;; *) usage >&2; exit 2 ;; esac
case "/$path/" in */../*|*/./*|*//*)
    echo 'Use a repository-relative spec path without dot components.' >&2; exit 2 ;;
esac
if [[ "$path" =~ [[:cntrl:]] ]]; then echo 'Control characters in source paths are unsupported.' >&2; exit 2; fi
root="$(get_repo_root)"
require_consumer_root "$root" read-spec.sh
spec_require_layout "$root" || exit 1
file="$root/$path"
[ -f "$file" ] || { printf 'No source file: %s\n' "$path" >&2; exit 2; }
snapshot="$(mktemp)"
trap 'rm -f "$snapshot"' EXIT
cat "$file" > "$snapshot"
cmp -s "$file" "$snapshot" || { printf 'Incomplete spec snapshot: %s\n' "$path" >&2; exit 1; }
if [ "$mode" = raw ]; then cat "$snapshot"; exit 0; fi
if ! verify_lf_file "$snapshot"; then
    printf 'Invalid spec text: %s (expected non-empty LF-terminated text; use --raw to inspect it)\n' "$path" >&2
    exit 1
fi
expected_lines="$(wc -l < "$snapshot")"
expected_bytes="$(wc -c < "$snapshot")"
head="$(GIT_NO_LAZY_FETCH=1 git -C "$root" rev-parse --verify HEAD 2>/dev/null)" || head=uncommitted
blob="$(git -C "$root" hash-object --no-filters -- "$snapshot")"
source_header() {
    printf 'Source: %s head=%s content_blob=%s (working-tree bytes; not a committed-snapshot claim)\n' "$path" "$head" "$blob"
}
if [ "$mode" = index ] || [ "$mode" = section ]; then
    source "$SCRIPT_DIR/spec-focused-lib.sh"
    if view="$(spec_focused "$snapshot" "$mode" "$selector" "$expected_lines" "$expected_bytes")"; then
        source_header
        printf '%s\n' "$view"
    elif [ "$?" -eq 10 ] && [ -n "$view" ]; then
        printf '%s: %s\n' "$path" "$view" >&2
        exit 2
    else
        if [ -n "$view" ]; then printf '%s\n' "$view" >&2; fi
        printf 'Could not read %s for --%s; use --raw to inspect the source.\n' "$path" "$mode" >&2
        exit 1
    fi
    exit 0
fi
source_header
spec_navigation "$snapshot" stats '' "$expected_lines" "$expected_bytes"
spec_navigation "$snapshot" diagnostics '' "$expected_lines" "$expected_bytes" >&2
if [ "$mode" = compact ]; then
    spec_navigation "$snapshot" compact '' "$expected_lines" "$expected_bytes"
elif [ "$mode" = target ]; then
    if ! spec_navigation "$snapshot" target "$selector" "$expected_lines" "$expected_bytes"; then
        printf 'No validated navigation target for %s; use --raw to inspect the source.\n' "$selector" >&2
        exit 1
    fi
fi
