#!/usr/bin/env bash
# Print the checked reason for a HOLD or WAIVER row from its protected authority entry.
set -euo pipefail
SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"
source "$SCRIPT_DIR/spec-lib.sh"

if [ "$#" -ne 1 ]; then
    echo 'Usage: bash .prdkit/scripts/bash/authority-ref.sh <anchor>' >&2
    exit 2
fi
root="$(get_repo_root)"
require_consumer_root "$root" authority-ref.sh
spec_authority_ref "$root" "$1"
