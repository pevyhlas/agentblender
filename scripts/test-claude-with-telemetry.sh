#!/usr/bin/env bash
set -euo pipefail
root="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT
cat > "$scratch/claude" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$CLAUDE_CODE_ENABLE_TELEMETRY" == 1 ]]
[[ "$OTEL_METRICS_EXPORTER" == otlp ]]
[[ "$OTEL_EXPORTER_OTLP_PROTOCOL" == http/protobuf ]]
[[ "$OTEL_EXPORTER_OTLP_ENDPOINT" == http://127.0.0.1:4318 ]]
[[ "$OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE" == cumulative ]]
[[ "$OTEL_RESOURCE_ATTRIBUTES" == project=agentblender ]]
[[ "$OTEL_METRICS_INCLUDE_SESSION_ID" == false ]]
[[ "$OTEL_METRICS_INCLUDE_ACCOUNT_UUID" == false ]]
[[ "$OTEL_LOGS_EXPORTER" == none && "$OTEL_TRACES_EXPORTER" == none ]]
[[ -z "${OTEL_EXPORTER_OTLP_METRICS_ENDPOINT+x}${OTEL_EXPORTER_OTLP_HEADERS+x}" ]]
[[ -z "${OTEL_LOG_USER_PROMPTS+x}${CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH+x}" ]]
[[ -z "${CLAUDE_CODE_ENHANCED_TELEMETRY_BETA+x}${ENABLE_BETA_TRACING_DETAILED+x}${BETA_TRACING_ENDPOINT+x}" ]]
[[ "$#" == 2 && "$1" == 'argument with spaces' && "$2" == --version ]]
printf '%s\n' 'PASS: cumulative metrics-only environment and argument forwarding'
SH
chmod +x "$scratch/claude"
PATH="$scratch:$PATH" bash "$root/scripts/claude-with-telemetry.sh" 'argument with spaces' --version
PATH="$scratch:$PATH" OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=https://invalid.example \
  OTEL_EXPORTER_OTLP_HEADERS=test-fixture OTEL_LOG_USER_PROMPTS=1 \
  CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH=999 CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1 \
  ENABLE_BETA_TRACING_DETAILED=1 BETA_TRACING_ENDPOINT=https://invalid.example \
  bash "$root/scripts/claude-with-telemetry.sh" 'argument with spaces' --version
mkdir -p "$scratch/repo/scripts"
cp "$root/scripts/claude-with-telemetry.sh" "$scratch/repo/scripts/"
jq '.env.NODE_OPTIONS = "--inspect"' "$root/scripts/claude-telemetry.env.json" > "$scratch/repo/scripts/claude-telemetry.env.json"
if PATH="$scratch:$PATH" bash "$scratch/repo/scripts/claude-with-telemetry.sh" 'argument with spaces' --version; then
  printf '%s\n' 'FAIL: promoted an unknown committed environment key' >&2
  exit 1
fi
printf '%s\n' 'PASS: ambient telemetry is scrubbed and unknown keys are rejected'
cat > "$scratch/jq" <<'SH'
#!/usr/bin/env bash
exit 42
SH
chmod +x "$scratch/jq"
if PATH="$scratch:$PATH" bash "$root/scripts/claude-with-telemetry.sh" 'argument with spaces' --version; then
  printf '%s\n' 'FAIL: launched despite unreadable configuration' >&2
  exit 1
else
  [[ "$?" == 42 ]]
fi
printf '%s\n' 'PASS: configuration failure prevents launch'
# Telemetry stays opt-in: the tracked project settings must not turn it on for everyone.
if ! jq -e '[(.env // {}) | keys[] | select(test("^(CLAUDE_CODE_ENABLE_TELEMETRY|OTEL_)"))] | length == 0' \
  "$root/.claude/settings.json" > /dev/null; then
  printf '%s\n' 'FAIL: .claude/settings.json enables telemetry for every contributor' >&2
  exit 1
fi
printf '%s\n' 'PASS: tracked project settings leave telemetry off'
