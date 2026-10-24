#!/usr/bin/env bash
#
# ledger-emit.sh — Emit a prdkit.ledger.v1 event to the project-configured sink.
#
# Usage:
#   echo '{"event":"readiness.changed",...}' | ledger-emit.sh <event-type>
#
# The script reads the event JSON on stdin and the delivery policy from
# .prdkit/memory/task-ledger.md, normalizes it to a single JSONL line,
# optionally appends operator attribution (policy 'operator: git'), appends
# the event as one line to the local spool (.prdkit/ledger/events.jsonl —
# always, regardless of backend), then either stops (backend: none or absent)
# or pipes the event to the configured adapter (backend: command). The adapter
# must be a single argument-free executable path inside the repository.
#
# Exit codes:
#   0 — Event spooled and emitted (or spooled only, for backend: none)
#   0 — Policy file absent (default: spool only)
#   1 — Invalid arguments or malformed policy
#   2 — Adapter command or spool write failed (only in strict mode)

set -euo pipefail

# --- Resolve repository root ---
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/json-lib.sh"
if [ -f "$SCRIPT_DIR/common.sh" ]; then
    source "$SCRIPT_DIR/common.sh"
    REPO_ROOT="$(get_repo_root)"
else
    # Fallback when running installed in consumer project
    REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || pwd)"
fi

POLICY_FILE="$REPO_ROOT/.prdkit/memory/task-ledger.md"

# --- Parse arguments ---
EVENT_TYPE="${1:-}"
if [ -z "$EVENT_TYPE" ]; then
    echo "Usage: ledger-emit.sh <event-type>" >&2
    echo "Reads event JSON on stdin." >&2
    exit 1
fi

# --- Read event JSON from stdin ---
[[ "$EVENT_TYPE" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || {
    echo 'Error: event type must be a portable event name.' >&2; exit 1;
}
if ! EVENT_JSON="$(json_object_normalize "$EVENT_TYPE")"; then
    echo 'Error: expected one valid JSON object with an unambiguous top-level event.' >&2
    exit 1
fi

# --- Parse policy file (simple key-value extraction from markdown) ---
parse_policy_value() {
    local key="$1"
    # Match "- key: value" or "key: value" — but ONLY inside the
    # "## Configuration" section, so documented examples elsewhere in the file
    # (e.g. "- backend: none" under ## Fields) can never shadow the real
    # config. POSIX character classes only: \s is a GNU extension and silently
    # degrades to a literal 's' on BSD/macOS grep, which made the pattern
    # never match and the backend fall back to "none".
    local section
    section="$(sed -n '/^##[[:space:]]*Configuration/,/^##[[:space:]]/p' "$POLICY_FILE" 2>/dev/null)"
    # Loosely-written policy files may have no ## Configuration header at all;
    # fall back to scanning the whole file in that case.
    [ -n "$section" ] || section="$(cat "$POLICY_FILE" 2>/dev/null)"
    printf '%s\n' "$section" \
        | grep -E "^-?[[:space:]]*${key}:" | head -1 \
        | sed -E 's/^-?[[:space:]]*'"$key"':[[:space:]]*//' | tr -d '\r' || true
}

BACKEND=""
COMMAND=""
EVENTS=""
MODE=""
OPERATOR=""
if [ -f "$POLICY_FILE" ]; then
    BACKEND="$(parse_policy_value 'backend')"
    COMMAND="$(parse_policy_value 'command')"
    EVENTS="$(parse_policy_value 'events')"
    MODE="$(parse_policy_value 'mode')"
    OPERATOR="$(parse_policy_value 'operator')"
fi

# Default values (also used when the policy file is absent)
BACKEND="${BACKEND:-none}"
EVENTS="${EVENTS:-all}"
MODE="${MODE:-best-effort}"

# --- Optional operator attribution (opt-in via 'operator: git') ---
# When the policy sets 'operator: git' and the event does not already carry an
# operator field, append one from the repository's git identity (email, falling
# back to name). Only absent top-level metadata is injected; nested fields are untouched.
if [ "$OPERATOR" = "git" ]; then
    op_id="$(git -C "$REPO_ROOT" config user.email 2>/dev/null || true)"
    if [ -z "$op_id" ]; then
        op_id="$(git -C "$REPO_ROOT" config user.name 2>/dev/null || true)"
    fi
    if [ -n "$op_id" ]; then
        EVENT_JSON="$(printf '%s\n' "$EVENT_JSON" | json_object_normalize "$EVENT_TYPE" "$op_id")"
    fi
fi

# --- Append to the local spool (always, before any backend dispatch) ---
# Every event is recorded as one JSON line in .prdkit/ledger/events.jsonl,
# regardless of backend configuration. This is the durable, replayable local
# record; backend delivery is a projection on top of it.
SPOOL_DIR="$REPO_ROOT/.prdkit/ledger"
SPOOL_FILE="$SPOOL_DIR/events.jsonl"
spool_ok=true
if ! { mkdir -p "$SPOOL_DIR" && printf '%s\n' "$EVENT_JSON" >> "$SPOOL_FILE"; } 2>/dev/null; then
    spool_ok=false
fi
if [ "$spool_ok" = false ]; then
    if [ "$MODE" = "strict" ]; then
        echo "Error: failed to write ledger spool ($SPOOL_FILE)" >&2
        exit 2
    fi
    echo "Warning: failed to write ledger spool ($SPOOL_FILE) (best-effort mode, continuing)" >&2
fi

# --- Check for policy file ---
if [ ! -f "$POLICY_FILE" ]; then
    # No policy file = default backend: none = spool only
    exit 0
fi

# --- Check backend ---
case "$BACKEND" in
    none)
        # Spool only, exit success
        exit 0
        ;;
    command)
        if [ -z "$COMMAND" ]; then
            echo "Error: backend is 'command' but no command configured" >&2
            exit 1
        fi
        # The policy file is repo-controlled content — a PR can edit it. To
        # keep that from becoming arbitrary command execution on whoever next
        # runs a workflow command, the adapter must be ONE path (no arguments,
        # no shell syntax) from a conservative character set, resolving to an
        # executable regular file inside this repository, not a symlink (-f and -x follow
        # a link, and only the parent directory is resolved below). An adapter that needs
        # arguments wraps them in a script.
        case "$COMMAND" in
            *[!A-Za-z0-9_./-]*)
                echo "Error: adapter command must be a single path of [A-Za-z0-9_./-] (no arguments or shell syntax): '$COMMAND'" >&2
                exit 1 ;;
            -*)
                echo "Error: adapter command must be a path, not an option: '$COMMAND'" >&2
                exit 1 ;;
            ..|../*|*/..|*/../*)
                echo "Error: adapter command must not contain '..' segments: '$COMMAND'" >&2
                exit 1 ;;
        esac
        case "$COMMAND" in
            /*) ADAPTER="$COMMAND" ;;
            *)  ADAPTER="$REPO_ROOT/$COMMAND" ;;
        esac
        if [ -L "$ADAPTER" ]; then
            echo "Error: adapter command must not be a symlink; point command: at the executable file itself: $ADAPTER" >&2
            exit 1
        fi
        if [ ! -f "$ADAPTER" ] || [ ! -x "$ADAPTER" ]; then
            echo "Error: adapter command is not an executable file: $ADAPTER" >&2
            exit 1
        fi
        adapter_dir="$(CDPATH="" cd "$(dirname "$ADAPTER")" 2>/dev/null && pwd -P)" || adapter_dir=""
        repo_root_p="$(CDPATH="" cd "$REPO_ROOT" && pwd -P)"
        case "$adapter_dir/" in
            "$repo_root_p"/*) ;;
            *)
                echo "Error: adapter command must live inside the repository: $ADAPTER" >&2
                exit 1 ;;
        esac
        ADAPTER="$adapter_dir/$(basename "$ADAPTER")"
        ;;
    *)
        echo "Error: unknown backend '$BACKEND' (expected 'none' or 'command')" >&2
        exit 1
        ;;
esac

# --- Check event filter ---
emit_event() {
    if [ "$EVENTS" = "all" ]; then
        return 0
    fi
    # Check if event type matches the filter (simple prefix/glob match)
    # Format: [event1, event2, event3.*] or just "event1"
    local filter_list
    filter_list="$(echo "$EVENTS" | tr -d '[]' | tr ',' '\n' | tr -d ' ')"
    while IFS= read -r pattern; do
        [ -z "$pattern" ] && continue
        # Exact match
        if [ "$EVENT_TYPE" = "$pattern" ]; then
            return 0
        fi
        # Glob match (e.g., "blocker.*" matches "blocker.opened")
        if [[ "$pattern" == *'*' ]]; then
            local prefix="${pattern%\*}"
            if [[ "$EVENT_TYPE" == "$prefix"* ]]; then
                return 0
            fi
        fi
    done <<< "$filter_list"
    return 1
}

if ! emit_event; then
    # Event type not in filter, skip
    exit 0
fi

# --- Emit to command ---
# Run the adapter from the repo root so relative paths inside it work
cd "$REPO_ROOT"

if [ "$MODE" = "strict" ]; then
    # Strict mode: adapter failure = script failure
    echo "$EVENT_JSON" | "$ADAPTER"
else
    # Best-effort mode: adapter failure is logged but script succeeds
    if ! echo "$EVENT_JSON" | "$ADAPTER" 2>&1; then
        echo "Warning: ledger adapter command failed (best-effort mode, continuing)" >&2
    fi
fi

exit 0
