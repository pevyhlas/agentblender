#!/usr/bin/env bash
set -eu
source "$(dirname "${BASH_SOURCE[0]}")/pr-facts-runtime.sh"
pr_facts_exec pr-state "$@"
