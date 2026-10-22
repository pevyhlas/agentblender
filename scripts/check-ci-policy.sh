#!/usr/bin/env bash
# check-ci-policy.sh [GITHUB_DIR]: asserts the public CI policy (C-09) over the GitHub
# configuration directory (default: .github of this repository).
#
#   - every job runs on a pinned GitHub-hosted image (ubuntu-NN.NN, macos-NN or windows-NNNN):
#     no runner labels, label lists, expressions or -latest aliases;
#   - "self-hosted" appears nowhere under the directory;
#   - no workflow uses the pull_request_target trigger;
#   - every `uses:` is pinned by a full 40-hex commit SHA followed by a `# vX...` version
#     comment (Dependabot rewrites both together); local `./` actions are exempt;
#   - every workflow declares top-level permissions, grants no write scope and reads no
#     secrets, so pull-request runs need none.
#
# Line-based on purpose: the workflows keep one key per line. Runs with macOS /bin/bash 3.2.
set -euo pipefail

root="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
gh_dir="${1:-$root/.github}"
wf_dir="$gh_dir/workflows"
fail=0

err() {
  printf 'ci-policy: %s\n' "$*" >&2
  fail=1
}

if [ ! -d "$wf_dir" ]; then
  err "no workflows directory at $wf_dir"
  exit 1
fi

files=()
while IFS= read -r f; do
  files+=("$f")
done < <(find "$wf_dir" -type f \( -name '*.yml' -o -name '*.yaml' \) | LC_ALL=C sort)
if [ "${#files[@]}" -eq 0 ]; then
  err "no workflow files in $wf_dir"
  exit 1
fi

if hits="$(grep -rniI 'self-hosted' "$gh_dir")"; then
  err "self-hosted runner reference: $hits"
fi

hosted_re='^(ubuntu-[0-9]{2}\.[0-9]{2}|macos-[0-9]+|windows-[0-9]{4})$'
pin_re='^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}[[:space:]]+#[[:space:]]*v[0-9]'
runs_re='^[[:space:]]*runs-on:[[:space:]]*(.*)$'
uses_re='^[[:space:]]*(-[[:space:]]+)?uses:[[:space:]]*(.*)$'
write_re='^[[:space:]]*(permissions:[[:space:]]*write-all|[a-z-]+:[[:space:]]*write)[[:space:]]*(#.*)?$'
secrets_re='\$\{\{[[:space:]]*secrets\.'

for f in "${files[@]}"; do
  rel="${f#"$gh_dir"/}"
  jobs=0
  top_permissions=0
  n=0
  while IFS= read -r line || [ -n "$line" ]; do
    n=$((n + 1))
    if [[ "$line" =~ $runs_re ]]; then
      jobs=$((jobs + 1))
      value="${BASH_REMATCH[1]%%#*}"
      value="${value%"${value##*[![:space:]]}"}"
      if ! [[ "$value" =~ $hosted_re ]]; then
        err "$rel:$n: runs-on '$value' is not a pinned GitHub-hosted image"
      fi
    fi
    if [[ "$line" =~ $uses_re ]]; then
      value="${BASH_REMATCH[2]}"
      case "$value" in
        ./*) ;;
        *)
          if ! [[ "$value" =~ $pin_re ]]; then
            err "$rel:$n: uses '$value' is not pinned by a full commit SHA with a version comment"
          fi
          ;;
      esac
    fi
    case "$line" in
      *pull_request_target*) err "$rel:$n: pull_request_target is not allowed" ;;
      permissions:*) top_permissions=1 ;;
    esac
    if [[ "$line" =~ $write_re ]]; then
      err "$rel:$n: write permission is not allowed: $line"
    fi
    if [[ "$line" =~ $secrets_re ]]; then
      err "$rel:$n: workflows must not read secrets"
    fi
  done < "$f"
  if [ "$jobs" -eq 0 ]; then
    err "$rel: no runs-on found"
  fi
  if [ "$top_permissions" -eq 0 ]; then
    err "$rel: no top-level permissions block"
  fi
done

if [ "$fail" -ne 0 ]; then
  exit 1
fi
printf 'ci-policy: OK (%d workflow file(s): hosted runners, SHA-pinned actions, read-only, no pull_request_target)\n' "${#files[@]}"
