#!/usr/bin/env bash
# Removes the `agentblender` .NET tool (008 FR-014). The config file, the store and the
# renders are kept. Refuses while the daemon runs or answers, since its service or
# process would point at removed files: run `abl daemon stop` (and
# `abl daemon uninstall-service` if you installed one) first, or pass --force.
#
# Usage: scripts/uninstall-local.sh [--tool-path DIR] [--force]
set -euo pipefail

PACKAGE_ID="agentblender"
TOOL_PATH=""
FORCE=0

usage() { printf '%s\n' "Usage: scripts/uninstall-local.sh [--tool-path DIR] [--force]" >&2; exit 2; }
while [ $# -gt 0 ]; do
    case "$1" in
        --tool-path) [ $# -ge 2 ] || usage; TOOL_PATH="$2"; shift 2 ;;
        --force) FORCE=1; shift ;;
        *) usage ;;
    esac
done

command -v dotnet > /dev/null || { echo "uninstall-local: dotnet not found" >&2; exit 1; }

if [ -n "$TOOL_PATH" ]; then
    WHERE=(--tool-path "$TOOL_PATH")
    ABL="$TOOL_PATH/abl"
else
    WHERE=(--global)
    ABL="$(command -v abl || true)"
    # The installed shim, also when ~/.dotnet/tools is not on PATH.
    [ -n "$ABL" ] || ABL="$HOME/.dotnet/tools/abl"
fi

if [ "$FORCE" != 1 ] && [ -n "$ABL" ] && [ -x "$ABL" ]; then
    # `abl daemon status`: 3 when no daemon is running; anything else means one may be.
    set +e
    "$ABL" daemon status > /dev/null 2>&1
    status=$?
    set -e
    if [ "$status" != 3 ]; then
        echo "uninstall-local: a daemon is running or does not answer (abl daemon status exit $status)." >&2
        echo "  run: $ABL daemon stop   (and $ABL daemon uninstall-service if you installed one), or pass --force" >&2
        exit 1
    fi
fi

if dotnet tool uninstall "$PACKAGE_ID" "${WHERE[@]}"; then
    echo "Removed $PACKAGE_ID. The config file, store and renders are kept."
else
    echo "$PACKAGE_ID was not installed (${WHERE[*]})."
fi
