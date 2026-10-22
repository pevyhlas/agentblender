# Cutover and compaction

**Inputs**: current canon, code, constitution, agent memory and worklogs, owner direction.
**Writes**: the spec files ([spec.md](spec.md), Layout), inbox, `specs/archive.md`.
**Result**: one current canon the owner reads quickly, plus owner questions.

## Cutover (once per project, between changes)

1. Every 0.x feature closes at the cutover unless the owner keeps it open. One the owner keeps
   becomes a change record, `specs/changes/<id>.md`, with the same ID (`change-template.md`):
   its intent, remaining tasks and acceptance, and its old folder named at the base (step 5).
   Its folder then goes with the rest. An open target the owner keeps becomes a change record
   too, under a new number past every reserved one, and each `planned:` marker that named the
   target names that change; a marker must never name a target that step 5 removes.
2. Write the spec from the current canon, the code and the constitution ([spec.md](spec.md),
   templates in `.prdkit/templates/`): journeys first, then requirements, qualities,
   constraints and non-goals, then the lifecycle, then the summary, then architecture. Describe what is true now; mark
   owner-decided, unbuilt items `planned`. Where canon contradicts itself or the code, do not
   pick: add an inbox question with both sources, a recommendation and a default. IDs have two
   or more digits: renumber `D-1` to `D-01` and update its citations in code and docs; doctor
   and status read only IDs of two or more digits.
3. Give each constraint its why, scope (part and journey IDs, or `all`), source and revisit
   when. A constraint whose only source is an earlier agent's text gets an inbox question to the
   owner.
4. Draw the system diagram and list each box as a part in the `architecture.md` overview, with
   the journeys that need it; detail goes to `specs/architecture/<topic>.md`. Write a part file for each box with real obligations, its components as
   sections; a box may link a document that stays on main as `legacy` until first touched. A
   part has one authority: its part file or its legacy document.
5. Condense, then remove: a sleep pass over the old canon, targets and every feature folder
   (`_archive` too). The base is the main commit the cutover starts from. Sort each file:
   - **Still binds** (a rule, policy, decision or observed fact): into the live spec where it
     belongs: a decision as a D-NN in `specs/architecture/decisions.md`, a fact in
     `specs/observed.md`, a rule in requirements, constraints or the constitution. Owner words
     stay verbatim. The old verification policy folds into the constitution.
   - **Worth remembering** (a direction tried and why it stopped, a lesson, a notable outcome):
     an entry of a few lines in `specs/archive.md` (`archive-template.md`), with its details as a
     path at the base. The file records the base once, and the last 0.x feature number, which
     `new-change.sh` never reuses.
   - **The rest**: nothing; git keeps it.

   Then `git rm` them all; git keeps them. A raw archive on main surfaces in searches and
   conflicts with the current spec. Take them off every load path: agent context files
   (`CLAUDE.md`, `AGENTS.md`), harness memory and agent instructions that name them point at the
   current spec, and history questions at `specs/archive.md`. Repoint links into them to where
   their content went, or drop them; links in history stay. Raw evidence elsewhere under
   `specs/` is untracked (`git rm --cached`) and kept under `.prdkit/cache/evidence/`.
6. Copy owner direction found in agent memory, plans and worklogs into `specs/inbox.md` as
   headed items (`inbox-template.md`); replace an older prose preamble with the template's comment.
7. Read every file once more for the reader: crisp wording, roles not names, repo-relative paths.
   Run doctor and status. The owner reads L0 and L1 and confirms or corrects.

## Restructure (a 1.0 spec to the 1.1 layout, once, between changes)

1. Write a journey file for each journey from `product.md`, renumbered to two digits (`J-1` to
   `J-01`, citations too). Fill intent, situation, scenario, variations and success from the
   canon, code and owner direction; mark gaps as inbox questions, never invent them.
2. Move R, Q and C items to `specs/product/requirements.md`, `qualities.md` and `constraints.md`,
   adding `Journeys:` to each R and Q; turn non-goals into N- items in `non-goals.md`. Restore
   anything a ceiling squeezed out, from git history. Give each constraint a
   scope of part and journey IDs, or `all`. `product.md` becomes the executive page
   (`product-template.md`): pains with the journeys that solve them, personas only, the core loop.
3. Write `specs/product/lifecycle.md` (`lifecycle-template.md`): the stage now, from owner
   direction, and the next. Mark items needed only later with `from: <stage>`. Where the stage
   or an item's stage is unclear, ask the owner in the inbox. If the constitution has an older
   `**Stage**` field, it points to `lifecycle.md` instead; if it has Stage Gates, each names the
   stage it is due before (`before Public preview`). No rule is weakened; an unclear gate goes
   to the inbox.
4. Redraw parts as the boxes of the system diagram (spec.md); fold concern parts into their box
   as components and tooling into the operations topic; environments go to the environments
   topic, and each stage's **Environment** line names one. `architecture.md` becomes the overview
   (`architecture-template.md`); its long sections move to `specs/architecture/<topic>.md`
   (`architecture-topics-template.md`), technology choices to decisions. Behaviors and
   invariants move with their component and keep their IDs when their part keeps its number. A
   part that goes, and each ID that moves, is listed under `## Retired` in
   `architecture/retired.md` (spec.md, IDs); update citations in code and tests to the new IDs.
5. Run the Cutover's steps 1 and 5 over `specs/archive/` and any 0.x feature folder still on
   main, with the main commit the Restructure starts from as the base. A part that links an
   archived document as `legacy` takes in what still binds; agent context, harness memory and
   agent instructions that name `specs/archive/` point at `specs/archive.md`.
6. Rewrite the inbox as headed items with options; replace personal names with roles and local
   paths with repo-relative ones. Run doctor and status.

## Compaction (on overflow, and every ten closed changes)

Rewrite within the budgets, by tightening and splitting, never by dropping items. Keep current truth, why-it-works entries with their alternatives,
assumptions and revisit conditions, and retired IDs (one line each). Drop to git: history,
amendment notes, superseded wording and resolved questions. Keep a rejected alternative that is
still a plausible proposal; it prevents re-proposals. `specs/archive.md` grows into git
instead: condense its older entries harder; their details stay at the base.
