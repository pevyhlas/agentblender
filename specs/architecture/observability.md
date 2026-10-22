# AgentBlender — observability

- **Signals**: opt-in OpenTelemetry (off by default, C-05). Every op on every surface passes one
  op context (P1.I-03): spans carry op, session, project label, outcome, serving tier, duration
  and payload sizes; metrics (`abl_*`) carry only bounded labels (op, outcome, tier), plus GPU
  wait and independent heartbeat and runtime gauges. Q-01 is read from the op duration
  histogram.
- **Transport**: OTLP push to a collector the operator runs; a separate trace endpoint can be
  set per signal. Dashboards and alert rules live outside this repository.
- **Alerts**: none in the repository; the operator's collector owns freshness and fault rules.
- **Live site**: there is no shared service. Locally, `abl daemon status`, `abl daemon logs`,
  `abl doctor` and the `daemon_status` op are the diagnosis path; daemon logs are quiet by
  default (Warning for framework categories, Information for `Abl.*`).
