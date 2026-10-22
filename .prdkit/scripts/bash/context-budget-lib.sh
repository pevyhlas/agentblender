#!/usr/bin/env bash
# Shared, dependency-free agent-context budget helpers used by doctor.sh. Agent context files
# (CLAUDE.md, AGENTS.md, ...) are loaded on every turn and by every subagent, so their size is
# a recurring token cost. These helpers only observe and report; callers decide whether a
# finding is advisory or failing.
#
# Optional key in the ## Configuration section of .prdkit/memory/config.md:
#   - context-budget-bytes: 16384          agent context file budget; 0 disables the check

CONTEXT_DEFAULT_BUDGET_BYTES=16384
# Root-level agent context files harnesses load on every turn.
CONTEXT_AGENT_FILES="CLAUDE.md AGENTS.md GEMINI.md QWEN.md CODEBUDDY.md QODER.md SHAI.md"

# Section-scoped lookup: only the ## Configuration section is configuration, so
# documented examples elsewhere in the file never shadow the real values.
prdkit_config_value() { # root key
    local config="$1/.prdkit/memory/config.md" key="$2"
    [ -f "$config" ] || return 0
    sed -n '/^##[[:space:]]*Configuration/,/^##[[:space:]]/p' "$config" 2>/dev/null \
        | grep -E "^-?[[:space:]]*${key}:" | head -1 \
        | sed -E 's/^-?[[:space:]]*'"$key"':[[:space:]]*//; s/[[:space:]]*$//' | tr -d '\r' || true
}

context_file_bytes() { wc -c < "$1" | tr -d '[:space:]'; }

# Prints one advisory per line for the agent context files under root. Never modifies
# anything; an empty output means within budget.
context_budget_advisories() { # root
    local root="$1" raw budget name file size
    raw="$(prdkit_config_value "$root" context-budget-bytes)"
    budget="$CONTEXT_DEFAULT_BUDGET_BYTES"
    if [ -n "$raw" ]; then
        if [[ "$raw" =~ ^[0-9]{1,9}$ ]]; then
            budget=$((10#$raw))
        else
            printf "Context budget: invalid context-budget-bytes '%s' in .prdkit/memory/config.md; using %s\n" "$raw" "$budget"
        fi
    fi
    [ "$budget" -gt 0 ] || return 0
    for name in $CONTEXT_AGENT_FILES; do
        file="$root/$name"
        [ -f "$file" ] || continue
        size="$(context_file_bytes "$file")"
        if [ "$size" -gt "$budget" ]; then
            printf 'Context budget: %s is %s bytes, over context-budget-bytes %s; keep stable orientation and pointers, move change narrative to the spec or git\n' \
                "$name" "$size" "$budget"
        fi
    done
    return 0
}
