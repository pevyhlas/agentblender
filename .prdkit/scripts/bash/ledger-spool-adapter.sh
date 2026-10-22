#!/usr/bin/env bash
# ledger-spool-adapter.sh — mirror prdkit.ledger.v1 events into the per-host
# central spool with repo attribution (the central-spool reference adapter;
# wire it in .prdkit/memory/task-ledger.md as
#   command: .prdkit/scripts/bash/ledger-spool-adapter.sh).
#
# Per-repo spools live wherever the command ran — including worktree feature
# checkouts that are deleted at merge — so this adapter appends every event to
# one durable per-host spool that a host metrics collector can read.
#
# Events are written per-project under one host root:
#   ${XDG_DATA_HOME:-$HOME/.local/share}/prdkit/ledger/<project>/events.jsonl
# A collector that globs <root>/*/events.jsonl attributes each event to its
# parent DIRECTORY, so the project name here IS that directory name. It is
# DERIVED so a new repo needs no edit: PRDKIT_PROJECT override, else the git
# ORIGIN repo name — worktree-safe because origin is identical across every
# worktree, unlike the toplevel directory name — else the repository directory
# name as a last resort. The embedded "project" field is kept for humans and
# legacy readers; the collector trusts the directory. No credentials, no
# network — a local append (under flock where available) only.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/json-lib.sh"
SPOOL_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/prdkit/ledger"
project="${PRDKIT_PROJECT:-}"
if [ -z "$project" ]; then
  origin="$(git config --get remote.origin.url 2>/dev/null || true)"
  if [ -n "$origin" ]; then
    project="${origin##*/}"; project="${project%.git}"
  fi
fi
if [ -z "$project" ]; then
  toplevel="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
  project="$(basename "$toplevel")"
fi
# One safe path segment: the project name becomes a spool directory.
project="$(printf '%s' "$project" | tr -cd 'A-Za-z0-9._-')"
project="${project:-unattributed}"
case "$project" in .|..) project=unattributed ;; esac
SPOOL_DIR="$SPOOL_ROOT/$project"
if ! line="$(json_object_normalize '' '' "$project")"; then
  echo 'Error: central spool requires one valid JSON object.' >&2; exit 1;
fi
mkdir -p "$SPOOL_DIR"
{
  if command -v flock >/dev/null 2>&1; then flock 9; fi
  printf '%s\n' "$line" >&9
} 9>>"$SPOOL_DIR/events.jsonl"
