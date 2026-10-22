# AgentBlender — non-goals

<!-- What the product deliberately does not do. Past the budget, split into
non-goals/<area>.md and keep one line and a link per area here. -->

- **N-01 No 2D tool**: no 2D authoring, lint or export (C-07). Why: AGD owns 2D. Revisit when:
  never, short of an owner decision.
- **N-02 No render farm or cloud service**: one local host plus optional private-network GPU
  workers. Why: self-hosted, single-operator scale. Revisit when: one host plus remote workers
  no longer serves real demand.
- **N-03 No human GUI or web UI**: Blender's GUI is the inspection path. Why: agents are the
  users (R-01). Revisit when: never, short of an owner decision.
- **N-04 No asset-marketplace integration that compromises CC0**: Why: licence-clean outputs
  (C-06). Revisit when: a CC0-only source with recorded provenance is proposed.
- **N-05 Not general-purpose first**: real consumer needs set priority; third-party use is
  welcome. Why: evidence before breadth. Revisit when: outside use shows a need.
- **N-06 No Blender download or install**: setup and doctor point at the official download
  page; AgentBlender never fetches or installs Blender. Why: D-18. Revisit when: the owner
  reopens it ([inbox](../inbox.md), first public release).
- **N-07 No container distribution**: Why: an image would redistribute Blender (C-02). Revisit
  when: never.
