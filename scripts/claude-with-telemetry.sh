#!/usr/bin/env bash
# Explicit opt-in launch environment: current Claude ignores project OTEL env settings.
set -euo pipefail
root="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
settings_env="$(jq -er '
  ["CLAUDE_CODE_ENABLE_TELEMETRY", "OTEL_METRICS_EXPORTER",
   "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_ENDPOINT",
   "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE", "OTEL_RESOURCE_ATTRIBUTES",
   "OTEL_METRICS_INCLUDE_SESSION_ID", "OTEL_METRICS_INCLUDE_ACCOUNT_UUID",
   "OTEL_LOGS_EXPORTER", "OTEL_TRACES_EXPORTER"] as $allowed |
  .env | if type != "object" or ((keys | sort) != ($allowed | sort))
  then error("unexpected telemetry environment keys") else to_entries end |
  if any(.[];
    (.key | test("^[A-Z][A-Z0-9_]*$") | not) or
    (.value | type != "string") or (.value | test("[\u0000\r\n]")))
  then error("invalid telemetry environment")
  else .[] | "\(.key)=\(.value)" end
' "$root/scripts/claude-telemetry.env.json")"
mapfile -t telemetry_env <<< "$settings_env"
# A per-signal endpoint overrides the generic endpoint; content/beta flags can
# independently enable extra signals. Start this opt-in profile from a clean OTel env.
for variable in "${!OTEL_@}" "${!CLAUDE_CODE_OTEL_@}"; do
  unset "$variable"
done
unset CLAUDE_CODE_ENHANCED_TELEMETRY_BETA ENABLE_ENHANCED_TELEMETRY_BETA
unset ENABLE_BETA_TRACING_DETAILED BETA_TRACING_ENDPOINT
exec env -- "${telemetry_env[@]}" claude "$@"
