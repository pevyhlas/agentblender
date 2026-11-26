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
| Security Review | `eng-security` | deep | Network boundary + code-exec trust boundary + secret handling; required before merge; never below `deep` |
| Architecture | `eng-arch` | deep | Worker protocol, checkpoint model, tiering; ADR review — runs rarely, consequences last |
| License / Provenance | `eng-license` | deep | MIT inbound rules (C-01, D-10), bpy boundary, CC0-only assets, provenance manifests — legal risk sits at `deep` (local override, see below) |
| Agent Ergonomics / Token Economy | `eng-ergonomics` | standard | Tool surface fitness for agents: deltas, contact sheets, preview sizing, MCP/CLI parity |
| Testing | generic (session subagents) | standard | Golden-file + protocol integration tests |
| Acceptance / Verification | generic (session subagents) | fast | Non-user-facing: automated verification per constitution principles; judgment lives in the change record |
| Spec Writing | driving planning session | frontier | Spec quality compounds downstream |
| Implementation | build / workflow agents | high | Routine implementation runs at `high`; frontier is reserved for deliberate planning |

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

<!-- BEGIN agent-library managed section: ci/sync.py rewrites it, edit outside it -->
Claude skill prefix: agentlib

| Local tier | Library tier |
|------------|--------------|
| verydeep-maker | verydeep |
| deep-maker | deep |
| standard-maker | standard |
| light-maker | light |

| Projection tier | Claude effort | OpenCode permissions | OpenCode preamble | Claude tools |
|-----------------|---------------|----------------------|-------------------|--------------|
| verydeep | high | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash |
| verydeep-maker | high | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}, {action: edit, resource: "*", effect: allow}, {action: shell, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash, Edit, Write |
| deep | high | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash |
| deep-maker | high | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}, {action: edit, resource: "*", effect: allow}, {action: shell, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash, Edit, Write |
| standard | high | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash |
| standard-maker | high | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}, {action: edit, resource: "*", effect: allow}, {action: shell, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash, Edit, Write |
| light | medium | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash |
| light-maker | medium | [{action: read, resource: "*", effect: allow}, {action: read, resource: "*.env", effect: deny}, {action: read, resource: "*.env.*", effect: deny}, {action: read, resource: "*.env.example", effect: allow}, {action: glob, resource: "*", effect: allow}, {action: grep, resource: "*", effect: allow}, {action: edit, resource: "*", effect: allow}, {action: shell, resource: "*", effect: allow}] | Working in this harness: make independent tool calls in parallel, such as several reads or searches at once; run dependent steps in order. Finish the task you were given before you stop; if something blocks you, say what it is and what you tried. Load a skill only when its description fits the task, not because a word in the request matches its name. End with a self-contained final answer: the caller sees only that message, not your tool output. | Read, Grep, Glob, Bash, Edit, Write |
<!-- END agent-library managed section -->

Models resolve through the shared agent-library tier baseline: `render-agents.sh` reads the
vendored snapshot `.prdkit/agent-library/tiers.json` (provenance in `tiers.json.ref`), maps each
local tier to a library tier with the table below, and stamps the resolved ids into the generated
projections. This file restates no model ids. To move to a newer library baseline, run
`.prdkit/scripts/bash/render-agents.sh --refresh-baseline <agent-library checkout> --library-ref
<commit>`, then `render-agents.sh` and `render-agents.sh --check`, and review the projection diff.

Tier baseline: agent-library tiers.json@675b6fbecf6629c4274ccbf6c447f330e01f66c3 (release v1.1.0@d9898076853b4095a0ea4142c33eccdbce04787b)

| Local tier | Library tier |
|------------|--------------|
| frontier | verydeep |
| high | deep |
| standard | standard |
| fast | light |

`frontier` is used for planning only. The five library reviewers carry library tier names
(`deep`, `standard`) in their `tier:` field, because the managed section's settings (Claude
effort and tools, OpenCode rules and preamble) apply only to library tier names; the local names
above remain for the generic roles.

**Local tier choices** (recorded deviations from the reviewer's default tier):

| Agent / row | Choice | Reason |
|---|---|---|
| `eng-license` | `tier: deep` (library default `standard`) | MIT open source (C-01, D-10): an inbound-license miss is legal exposure, not a style finding |

Reviewer projections are read-only: Claude reviewers get `Read, Grep, Glob, Bash` (plus
`Skill` when they declare lens skills); OpenCode reviewers are subagents with deny-by-default
rules allowing only read (`.env` files denied), glob and grep, so they read the diff and lens
facts from the review package. Build/test gates run in the driving session or CI. Start OpenCode in this checkout/worktree;
home-directory sessions do not discover these project-local definitions. Restart
after config/agent changes. Session model-picker overrides still take precedence.

### Native projection contract

- Body: the neutral body verbatim (canonical region + marker + overlay). Header: a YAML
  comment naming the neutral source.
- Claude: `model` from the tier baseline; `effort` and `tools:` from the managed section;
  declared lens skills preload as `agentlib:<name>` from `.claude/skills/agentlib/`.
- OpenCode: V2 frontmatter (`mode: subagent`, `model: <id>#<variant>`, ordered `permissions:`
  from the managed section, skills denied since reviewers have no shell) and the library preamble.
- Lens skills are vendored whole by agent-library `ci/sync.py` into `.claude/skills/agentlib/`
  and `.agents/skills/`; each copy's `references/local.md` is ours (secrets-scan quiets reviewed
  kit-variable and test-token fingerprints).
- Refresh: `python3 <agent-library at a release tag>/ci/sync.py --consumer . --write`, then
  `.prdkit/scripts/bash/render-agents.sh` and `render-agents.sh --check`.

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
