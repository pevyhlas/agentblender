# AgentBlender — qualities

<!-- Q- items: a band and its method, never a tuning number. Past the budget, split into
qualities/<area>.md and keep one line and a link per area here. -->

- **Q-01 Interactive latency** (validated): non-render op round trip p95 under 1 s on the
  iteration host, from the op duration histogram. Journeys: J-01. Enforced by: telemetry, owner
  review.
- **Q-02 Context economy** (validated): state reads default to deltas bounded by change size;
  previews default to the 512 px class; batch verification is one image; final renders never
  pass through model context. Journeys: J-01, J-02. Enforced by: `StateTextGoldenTests`,
  `BatchContactSheetTests`.
- **Q-03 Replay fidelity** (validated): fresh-worker replay passes the content check every
  time, measured 10 of 10 at qualification. Journeys: J-01, J-03. Enforced by:
  `ReplayDeterminismTests` (three fresh workers), `BundlePortabilityTests`.
- **Q-04 Failure blast radius** (validated): a worker crash, hang or timeout affects only its
  session. Journeys: J-01, J-02. Enforced by: `SessionIsolationTests`, `TimeoutRecycleTests`.
