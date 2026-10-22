# Agent Registry

> The project's agent roster, keyed by **domain/role**. Product-Kit workflows (change
> review, `/prdkit-configure`) read this file instead of any hardcoded roster. Neutral definitions live in `.prdkit/agents/<name>.md`
> (shared canonical reviewer body + `<!-- LOCAL OVERLAY -->` + AgentBlender rubric);
> `.claude/agents/` and `.opencode/agents/` are generated native projections.

## Command / intent roles

| Domain / Role | Agent | Notes |
|---------------|-------|-------|
| Intent Owner | project maintainer | Owns product purpose, strategic tradeoffs, spending, live infra, final acceptance |
| Operational Lead | driving coding-agent session | Resolves routine ambiguity, scope control, orchestration, escalation discipline |

## Registry

The **Tier** column declares the capability tier each role's dispatches run at (see
Capability tiers below). It is policy, not enforcement: the native projections enforce
it through their model/effort fields, resolved from the tier map below.

| Domain / Role | Agent | Tier | Notes |
|---------------|-------|------|-------|
| Code Review | `eng-code` | standard | Risk-area diffs also get their named reviewer (constitution Review and checks) |
| Security Review | `eng-security` | high | Network boundary + code-exec trust boundary + secret handling; required before merge; never below `high` |
| Architecture | `eng-arch` | high | Worker protocol, checkpoint model, tiering; ADR review — runs rarely, consequences last |
| License / Provenance | `eng-license` | high | MIT inbound rules (C-01, D-10), bpy boundary, CC0-only assets, provenance manifests — legal risk sits at `high` (local override, see below) |
| Agent Ergonomics / Token Economy | `eng-ergonomics` | standard | Tool surface fitness for agents: deltas, contact sheets, preview sizing, MCP/CLI parity |
| Testing | generic (session subagents) | standard | Golden-file + protocol integration tests |
| Acceptance / Verification | generic (session subagents) | fast | Non-user-facing: automated verification per constitution principles; judgment lives in the change record |
| Spec Writing | driving planning session | frontier | Spec quality compounds downstream |
| Implementation | build / workflow agents | high | Routine implementation uses Sol; frontier is reserved for deliberate planning |

## Capability tiers

Four generic tiers (the kit's portable contract; neutral `tier:` fields use these names).

| Tier | Meaning | Use for |
|------|---------|---------|
| `frontier` | The strongest available model at maximum reasoning effort | Main executor on design-heavy/complex work; spec-conflict adjudication; escalations |
| `high` | A top-tier model at high effort | Risk-area reviews (security, license, durable state), architecture, merge semantic review |
| `standard` | A mid-tier model, or a top model at reduced effort | Routine structured reviews over bounded packages (per-task code/test reviews, ergonomics), converge audits |
| `fast` | A small/fast model | Verification runs, status rendering, evidence collection, mechanical extraction |

Rules of thumb (from the kit template): tier by volume × risk; never let a cheap reviewer
create fix-round churn; risk areas override to `high`+ regardless of routine tier;
reasoning effort is a tier lever; deterministic gates stay bash scripts, never dispatches.

## Tier → model / effort map

The tier map is a literal table (no shared baseline); `render-agents.sh` stamps these ids
into the generated projections. Update the table and re-render when models change.

| Tier | Claude Code model | OpenCode model | OpenCode variant |
|---|---|---|---|
| frontier | claude-fable-5-1 | openai/gpt-6-astra | xhigh |
| high | claude-opus-5-5 | openai/gpt-6.1-sol | xhigh |
| standard | claude-sonnet-5-5 | openai/gpt-6.1-sol | high |
| fast | claude-sonnet-5-5 | openai/gpt-6.1-sol | medium |

`frontier` is used for planning only.

**Local tier choices** (recorded deviations from the reviewer's default tier):

| Agent / row | Choice | Reason |
|---|---|---|
| `eng-license` | `tier: high` (default `standard`) | MIT open source (C-01, D-10): an inbound-license miss is legal exposure, not a style finding |

Reviewer projections use `mode: all` (named primary or subagent), deny edits and
delegation, and permit only read/search/web/tagging plus bounded Git/GitHub inspection
commands. Build/test gates run in the driving session or CI. Start OpenCode in this checkout/worktree;
home-directory sessions do not discover these project-local definitions. Restart
after config/agent changes. Session model-picker overrides still take precedence.

### Native projection contract

- Body: the neutral body verbatim (canonical region + marker + overlay). Header: a YAML
  comment naming the neutral source.
- Claude keeps `tools: Read, Grep, Glob, LS, Bash`; `model` from the tier map row.
- OpenCode keeps `mode: all` and the bounded read-only `permission` block; `model` /
  `variant` from the tier map row.
- Refresh: edit the neutral definition, run `.prdkit/scripts/bash/render-agents.sh`, then
  `render-agents.sh --check` (fails on body drift or model/variant drift).

### Review concern routing

- Code and Tests & Verification: `eng-code` (test evidence is a distinct review scope;
  deterministic build/test commands remain shell work). A dedicated `eng-test` reviewer is
  not adopted: `rigor: light` runs no per-task review chain and no dedicated test reviewer
  has been needed; adopt it if a feature's test surface outgrows `eng-code`'s pass.
- Security & Privacy: `eng-security`.
- Domain Correctness and agent-facing UX: `eng-ergonomics`.
- Architecture and Data Migration / BCDR: `eng-arch`.
- Release / Operations: `eng-arch`, with security review when triggered.
- License / Provenance: `eng-license`; required for the dependency, inbound
  code, bundled-asset and worker-boundary changes in its rubric.
- Workflow / durable-spec integrity: covered by the driving session's Product-Kit gates
  (`check-adoption.sh`, `doctor.sh`, `check-review.sh`); no dedicated workflow reviewer is
  adopted.

## Notes

- Non-user-facing project: no UX personas; the constitution's principles are the acceptance
  criteria.
- The five reviewers were created in feature 001 T001 as `abl-*` (`abl-code-reviewer`,
  `abl-security-reviewer`, `abl-architecture-reviewer`, `abl-license-reviewer`,
  `abl-agent-ergonomics-reviewer`) and renamed to the shared canonical names
  (`eng-code`, `eng-security`, `eng-arch`, `eng-license`, `eng-ergonomics`) on 2026-10-09.
  Archived review records keep the old names as written. Keep the neutral
  definitions, projections and this registry in sync when roles change.
