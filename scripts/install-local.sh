#!/usr/bin/env bash
# Installs `abl` from this source checkout as the .NET tool `agentblender` (008 FR-014):
# packs src/Abl.Cli (Release) into artifacts/packages and installs that package, so the
# installed abl runs exactly what a released package would (worker/ from the package, never
# from this checkout). Needs a .NET 10 SDK (10.0.112 or later, see global.json); running abl
# needs the ASP.NET Core 10 runtime, which the SDK includes.
#
# Replacing an install deletes the package directory a running daemon still reads (its
# assemblies and the worker/ it spawns workers from), so the script refuses while a daemon
# runs or answers: run `abl daemon stop` first and `abl daemon start` after, or pass --force.
#
# Usage: scripts/install-local.sh [--tool-path DIR] [--force]
#   default: a global tool (~/.dotnet/tools/abl); --tool-path installs into DIR instead.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd -P)"
PROJECT="$ROOT_DIR/src/Abl.Cli/Abl.Cli.csproj"
PACKAGE_ID="agentblender"
PACK_OUTPUT="$ROOT_DIR/artifacts/packages"
TOOL_PATH=""
FORCE=0

usage() { printf '%s\n' "Usage: scripts/install-local.sh [--tool-path DIR] [--force]" >&2; exit 2; }
while [ $# -gt 0 ]; do
    case "$1" in
        --tool-path) [ $# -ge 2 ] || usage; TOOL_PATH="$2"; shift 2 ;;
        --force) FORCE=1; shift ;;
        *) usage ;;
    esac
done

command -v dotnet > /dev/null || { echo "install-local: dotnet not found; install a .NET 10 SDK" >&2; exit 1; }

if [ -n "$TOOL_PATH" ]; then
    WHERE=(--tool-path "$TOOL_PATH")
    ABL="$TOOL_PATH/abl"
    INSTALLED="$ABL"
else
    WHERE=(--global)
    ABL="abl"
    # The installed shim, also when ~/.dotnet/tools is not on PATH.
    INSTALLED="$(command -v abl || true)"
    [ -n "$INSTALLED" ] || INSTALLED="$HOME/.dotnet/tools/abl"
fi

if [ "$FORCE" != 1 ] && [ -x "$INSTALLED" ]; then
    # `abl daemon status`: 3 when no daemon is running; anything else means one may be.
    set +e
    "$INSTALLED" daemon status > /dev/null 2>&1
    status=$?
    set -e
    if [ "$status" != 3 ]; then
        echo "install-local: a daemon is running or does not answer (abl daemon status exit $status)." >&2
        echo "  Replacing the install would delete the files it runs and spawns workers from." >&2
        echo "  run: $INSTALLED daemon stop   then this script, then $INSTALLED daemon start; or pass --force" >&2
        exit 1
    fi
fi

VERSION="$(dotnet msbuild "$PROJECT" --nologo -getProperty:PackageVersion -p:Configuration=Release)"
echo "== pack $PACKAGE_ID $VERSION"
rm -rf -- "$PACK_OUTPUT"
mkdir -p "$PACK_OUTPUT"
dotnet pack "$PROJECT" --configuration Release --nologo --output "$PACK_OUTPUT"
[ -f "$PACK_OUTPUT/$PACKAGE_ID.$VERSION.nupkg" ] || { echo "install-local: $PACKAGE_ID.$VERSION.nupkg was not created" >&2; exit 1; }

# A reinstall of the same version must take the new package, not a cached one. --source
# replaces every configured feed, so the package comes from this checkout's pack only, never
# from a feed that serves the same id and version (nuget.org included).
echo "== install $PACKAGE_ID $VERSION (${WHERE[*]})"
dotnet tool uninstall "$PACKAGE_ID" "${WHERE[@]}" > /dev/null 2>&1 || true
dotnet tool install "$PACKAGE_ID" "${WHERE[@]}" --source "$PACK_OUTPUT" --version "$VERSION"

if [ -z "$TOOL_PATH" ] && ! command -v abl > /dev/null; then
    echo
    echo "abl is not on PATH yet; add the .NET tools directory, for example:"
    # shellcheck disable=SC2016 # printed for the user to run, unexpanded
    echo '  export PATH="$HOME/.dotnet/tools:$PATH"'
fi

cat <<NEXT

Installed. Next:
  $ABL setup                      # discover Blender, write the config file
  $ABL daemon start               # returns once a worker is warm
  $ABL mcp config --client claude # the MCP entry for your agent (writes nothing)
  $ABL doctor                     # if anything fails
Before the next upgrade or reinstall, stop the daemon (\`$ABL daemon stop\`) and start it after:
replacing the install removes the files a running daemon reads.
NEXT
