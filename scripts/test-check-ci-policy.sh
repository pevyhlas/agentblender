#!/usr/bin/env bash
# test-check-ci-policy.sh: mutation test for check-ci-policy.sh. The real .github passes; each
# mutation of a copy (one policy breach at a time) must fail with the matching message.
# Runs with macOS /bin/bash 3.2.
set -euo pipefail
root="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
check="$root/scripts/check-ci-policy.sh"
scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT

bash "$check" "$root/.github" > /dev/null
printf '%s\n' 'PASS: the repository .github meets the policy'

# replace_first FILE FROM TO: replace the first literal occurrence of FROM with TO (one line;
# awk turns a \n escape in TO into a line break).
replace_first() {
  awk -v from="$2" -v to="$3" '
    !done && (i = index($0, from)) { $0 = substr($0, 1, i - 1) to substr($0, i + length(from)); done = 1 }
    { print }' "$1" > "$1.tmp"
  if cmp -s "$1" "$1.tmp"; then
    printf 'FAIL: mutation found no "%s" in %s\n' "$2" "$1" >&2
    exit 1
  fi
  mv "$1.tmp" "$1"
}

# mutant NAME EXPECTED FROM TO: apply one mutation to a fresh copy of ci.yml and expect the
# check to fail with EXPECTED in its output.
mutant() {
  local dir="$scratch/$1"
  mkdir -p "$dir"
  cp -R "$root/.github/." "$dir/"
  replace_first "$dir/workflows/ci.yml" "$3" "$4"
  if bash "$check" "$dir" > "$dir.out" 2>&1; then
    printf 'FAIL: %s passed the policy check\n' "$1" >&2
    exit 1
  fi
  if ! grep -qF -- "$2" "$dir.out"; then
    printf 'FAIL: %s failed without "%s":\n' "$1" "$2" >&2
    cat "$dir.out" >&2
    exit 1
  fi
  printf 'PASS: %s is rejected\n' "$1"
}

# The pinned checkout line is read from ci.yml, so a Dependabot bump does not break the mutants.
pin="$(grep -oE 'actions/checkout@[0-9a-f]{40} # v[0-9][0-9A-Za-z.+-]*' "$root/.github/workflows/ci.yml" | head -n 1)"
if [ -z "$pin" ]; then
  printf '%s\n' 'FAIL: no SHA-pinned actions/checkout line in ci.yml' >&2
  exit 1
fi
sha="${pin#actions/checkout@}"; sha="${sha%% *}"
ver="${pin##* # }"
mutant self-hosted-label 'not a pinned GitHub-hosted image' \
  'runs-on: ubuntu-24.04' 'runs-on: [self-hosted, Linux, X64]'
mutant floating-image 'not a pinned GitHub-hosted image' \
  'runs-on: ubuntu-24.04' 'runs-on: ubuntu-latest'
mutant pull-request-target 'pull_request_target is not allowed' \
  'workflow_dispatch:' 'workflow_dispatch:\n  pull_request_target:'
mutant tag-instead-of-sha 'not pinned by a full commit SHA' \
  "$pin" "actions/checkout@$ver"
mutant missing-version-comment 'not pinned by a full commit SHA' \
  "$pin" "actions/checkout@$sha"
mutant short-sha 'not pinned by a full commit SHA' \
  "$pin" "actions/checkout@$(printf '%s' "$sha" | cut -c1-7) # $ver"
mutant write-permission 'write permission is not allowed' \
  'contents: read' 'contents: write'
mutant no-top-permissions 'no top-level permissions block' \
  'permissions:' 'x-permissions:'
# shellcheck disable=SC2016 # a literal workflow expression, not a shell expansion
mutant reads-secret 'workflows must not read secrets' \
  'DOTNET_NOLOGO: "1"' 'DOTNET_NOLOGO: ${{ secrets.NUGET_TOKEN }}'
