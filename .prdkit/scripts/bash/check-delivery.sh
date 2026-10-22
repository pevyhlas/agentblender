#!/usr/bin/env bash
# Observe checkpoint availability; never grant authority or mutate Git state.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
json=false; local_only=false; remote=origin; path=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --json) json=true ;;
        --local-only) local_only=true ;;
        --remote) shift; [ "$#" -gt 0 ] || exit 2; remote="$1" ;;
        --help|-h) echo 'Usage: check-delivery.sh [--json] [--local-only] [--remote name] [<path in the checkout>]'; exit 0 ;;
        -*) echo "Unknown option: $1" >&2; exit 2 ;;
        *) [ -z "$path" ] || exit 2; path="$1" ;;
    esac
    shift
done
path="${path:-.}"
[ -d "$path" ] || { echo 'Expected a directory inside the checkout to inspect' >&2; exit 2; }
[[ "$remote" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]*$ ]] || { echo 'Expected a configured remote name' >&2; exit 2; }
root="$(git -C "$path" rev-parse --show-toplevel)" || { echo "Not inside a Git checkout: $path" >&2; exit 2; }
branch="$(git -C "$root" symbolic-ref --quiet --short HEAD || true)"
head="$(git -C "$root" rev-parse HEAD)"
changes="$(git --no-optional-locks -C "$root" status --porcelain=v1 --untracked-files=normal --ignore-submodules=none)"
remote_head=""; transport=BLOCKED; reasons=()
[ -n "$branch" ] || reasons+=("Detached HEAD has no handover branch")
[ -z "$changes" ] || reasons+=("Uncommitted paths remain; checkpoint intended work and identify unrelated owners")
if $local_only; then
    transport=LOCAL_ONLY
else
    if [ -n "$branch" ]; then
        if refs="$(git -C "$root" ls-remote --exit-code --heads "$remote" "refs/heads/$branch" 2>/dev/null)"; then
            while IFS=$'\t' read -r sha ref; do
                [ "$ref" != "refs/heads/$branch" ] || remote_head="$sha"
            done <<< "$refs"
            [ "$remote_head" = "$head" ] || reasons+=("Remote branch revision does not match local HEAD")
        else
            reasons+=("Remote branch is absent or could not be queried")
        fi
    fi
    if [ "${#reasons[@]}" -eq 0 ]; then transport=REMOTE_READY; fi
fi
if $json; then
    printf '{"repository":"%s","branch":"%s","local_revision":"%s","remote":"%s","remote_revision":"%s","transport":"%s","uncommitted":"%s","reasons":%s}\n' \
        "$(json_escape "$root")" "$(json_escape "$branch")" "$head" "$(json_escape "$remote")" \
        "$remote_head" "$transport" "$(json_escape "$changes")" "$(json_array ${reasons[@]+"${reasons[@]}"})"
else
    printf 'Transport: %s\nRepository: %s\nBranch: %s\nLocal revision: %s\nRemote revision: %s\n' \
        "$transport" "$root" "$branch" "$head" "${remote_head:-unverified}"
    for reason in ${reasons[@]+"${reasons[@]}"}; do printf '  %s\n' "$reason"; done
fi
[ "${#reasons[@]}" -eq 0 ]
