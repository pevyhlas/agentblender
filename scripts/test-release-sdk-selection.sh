#!/usr/bin/env bash
# Model a runner with two SDKs: committed latestFeature, checkout pinned by CI.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
WORK="$(mktemp -d)"
trap 'rm -rf -- "$WORK"' EXIT
mkdir -p "$WORK/repo/scripts" "$WORK/bin"
cp "$ROOT/scripts/release-bundle.sh" "$WORK/repo/scripts/"
printf '%s\n' '{"sdk":{"version":"10.0.113","rollForward":"latestFeature"}}' > "$WORK/repo/global.json"
git -C "$WORK/repo" init --quiet
git -C "$WORK/repo" add .
git -C "$WORK/repo" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm fixture
jq '.sdk.rollForward="disable"' "$WORK/repo/global.json" > "$WORK/repo/global.json.tmp"
mv "$WORK/repo/global.json.tmp" "$WORK/repo/global.json"
cat > "$WORK/bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$(jq -r '.sdk.rollForward' global.json)" != disable ]]; then
    if [[ "$1" == --version ]]; then printf '10.0.999\n'; exit 0; fi
    printf 'WRONG_SDK_SELECTED\n' >&2
    exit 72
fi
case "$1" in
    --version) printf '10.0.113\n' ;;
    msbuild) printf '0.2.0-dev\n' ;;
    restore) exit 0 ;;
    pack) printf 'REFERENCE_SDK_REACHED_PACK\n' >&2; exit 73 ;;
    *) exit 74 ;;
esac
SH
chmod +x "$WORK/bin/dotnet"
status=0
PATH="$WORK/bin:$PATH" bash "$WORK/repo/scripts/release-bundle.sh" > "$WORK/result" 2>&1 || status=$?
if [[ "$status" != 73 ]] || ! grep -qx 'REFERENCE_SDK_REACHED_PACK' "$WORK/result"; then
    cat "$WORK/result"
    exit 1
fi
printf '%s\n' 'PASS: tracked archive retains reference SDK selection with simulated newer SDK'
