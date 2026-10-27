# Writing and editing the spec

**Inputs**: the change's intent, current spec files, code and tests, observed facts.
**Writes**: the files in Layout, or `specs/SPEC.md`: one form at a time (a split moves
everything in one change and deletes `SPEC.md`).
**Result**: a spec an owner reads quickly, that states current truth and narrows what matters.

## Layout

| File | Level | Holds | Budget | Past its budget |
|------|-------|-------|--------|-----------------|
| `specs/product.md` | L0 summary | the executive page, read at a glance: one sentence, the stage, the problem and its pains, a diagram, who it is for, the core loop and supporting journeys, the most important items, the bet, what waits on the owner | 80 | detail moves down to `specs/product/` (all pains and personas to `users.md`); a line and a link stay |
| `specs/product/users.md` | L0 | every pain and persona, once `product.md` shows only the main ones | 150 | `users/<area>.md`, 150 each |
| `specs/product/journeys/j-NN-<slug>.md` | L0 | one journey: who, intent, situation, scenario, variations, success | 150 each | it is two journeys: one intent each, the first keeps its ID, each links the other |
| `specs/product/requirements.md` | L0 | R- items by area | 300 | `requirements/<area>.md`, 300 each |
| `specs/product/qualities.md` | L0 | Q- items | 300 | `qualities/<area>.md`, 300 each |
| `specs/product/constraints.md` | L0 | C- items | 300 | `constraints/<area>.md`, 300 each |
| `specs/product/non-goals.md` | L0 | N- items | 150 | `non-goals/<area>.md`, 150 each |
| `specs/product/lifecycle.md` | L0 | the current and next stage; per stage its goal, audience, environment, promise and exit criteria (`lifecycle-template.md`) | 150 | `lifecycle/<stage>.md`, 150 each; Now and Next stay |
| `specs/architecture.md` | L1 summary | the overview, read at a glance: one sentence, the system diagram, parts and why each exists, the core loop's path, key decisions, key areas (security and privacy, observability and live site, data, interfaces, environments, operations) in a line each, risks, left open | 100 | detail moves to a topic or a part; a line and a link stay |
| `specs/architecture/<topic>.md` | L1 | one topic in detail: `security`, `observability`, `data`, `interfaces` (with external systems), `why-it-works`, `environments` (where the product runs), `operations` (`architecture-topics-template.md`) | 300 each | `architecture/<topic>/<area>.md`, 300 each |
| `specs/architecture/decisions.md` | L1 | D- decisions, technology choices included, a `## D-NN <title>` section each with bold-label lines: decision, alternatives, revisit when (`decision-template.md`) | 300 | `architecture/decisions/<area>.md`, 300 each; one long decision gets `specs/decisions/D-NN.md` |
| `specs/architecture/retired.md` | L1 | the `## Retired` list: parts that went and IDs that moved, a line each; linked from `architecture.md` | 300 | `architecture/retired/<area>.md`, each under `## Retired` |
| `specs/parts/p<n>-<slug>.md` | L2 summary | one part: why it exists, components with their behaviors and invariants, interfaces, failure, budgets | 200 each | a component moves to `parts/p<n>-<slug>/<component>.md` |
| `specs/parts/p<n>-<slug>/<component>.md` | L2 | one component, once its part outgrows its budget | 200 each | it is two components |
| `specs/SPEC.md` | L0–L2 | all of the above in one file, for a small project | 300 | the layout above, in one change |
| `specs/archive.md` | history | the archive, in past tense: directions tried and why they stopped, lessons, notable outcomes, a few lines each with a git path at the recorded base commit; read on demand, never by default (`archive-template.md`) | 150 | condense older entries harder: their details stay in git at the base |

Each level opens with a one-page summary that links its detail: `product.md`, `architecture.md`,
each part file. A topic with little to say stays a short section of its summary until the summary
passes its budget; then it moves to its own file.

**Splitting**: a budget holds only because every file has somewhere to grow. Past one, a file
splits as the table says, never by dropping items; a piece past its own budget becomes two
pieces beside it (two areas, `lifecycle/<stage>-<part>.md`, `decisions/D-NN-<part>.md`). A
piece opens with its parent's title (`# P<n> <part> — <component>`; a decision piece
`# <title> — <part> (D-NN)`, so it does not define the ID twice), the parent keeps one line
and a link per piece, and IDs never change. Doctor checks that every link resolves, that every
spec file is reached from `product.md` or `architecture.md` through links (a folder link reaches
the files directly in it; a retired file is exempt), and that `observed.md` links its pieces.
Links in `specs/archive.md` must resolve too; a git path is code (`git show <sha>:<path>`), not
a link.

## Levels

| Level | Answers | Never contains |
|-------|---------|----------------|
| L0 product | what problem, for whom, what users do and see, what must be true | technology (except an owner-sourced C-), code names, file paths |
| L1 architecture | how the parts make the journeys work, and why that works | field-level schemas, step-by-step logic |
| L2 part | what this part guarantees | step-by-step logic, copied schemas |

L0 may name the surfaces users touch (a phone app, a web page, an agent): they are part of the
experience. Inherited platform rules are not copied: `architecture.md` links the platform
document and names the rules it uses.

## Journeys come first

A journey is what a persona is trying to get done, in their situation, and what they see. It is
the most important part of the spec: requirements and qualities cite the journeys they serve,
and architecture exists to make the journeys work. The chain runs pain → journey → part: each
journey solves a pain (or supports one that does), and each part exists for journeys.

- **Personas** are the real target users whose problem the product solves. A persona is a
  different person with different needs; people who do the same thing at different points of
  the loop (one starts it, another continues it) are one persona, with one experience. Whoever
  tests, deploys or hosts it (testers, a deploying team, self-hosters) is not a persona: their
  needs are constraints, Operations or requirements.
- **Pains** are what goes wrong for a persona today, each with its evidence (assumed, desk
  research, observed or measured) and the journeys that solve it.
- **Core loop**: the few journeys the bet depends on. The rest support them.
- Write from the persona's side: their intent, where they are, what they do, what they see.
- **Situation** is their context, never delivery status: what is built or planned lives in R
  and Q markers and in changes.
- **Variations** cover what changes the experience: no or intermittent connection, an error,
  a missing permission, a limit reached, first use, a mistake to undo, and the project's own.
- **Success** is observable, for the user and for the business.
- Group journeys by product area; the same area names group requirements.

## The lifecycle decides what is needed now

A product moves through stages, and each needs less than the next. Skip the ones it does not need.

| Stage | Means |
|-------|-------|
| Idea | the problem and the bet; nothing built |
| Demo | the core loop on sample data, to show |
| MVP | the core loop in real use by the owner, on real data |
| Private preview | invited users on real data; it may change |
| Public preview | anyone can join; it may still change |
| GA | supported; quality bands enforced; breaking changes need a migration |
| Sunset | no new users; users export or migrate; the end date is announced |
| Retired | stopped; data deleted or handed over |

- `lifecycle.md` names the stage **Now** and **Next**, and for each stage its goal, audience,
  environment, promise and exit criteria. Only the owner moves Now.
- An item needed only from a later stage carries `from: <stage>` with its maturity:
  `**R-12 Choose a plan** (hypothesis, from: ga)`, `# J-07 Choose my plan (hypothesis, from:
  ga)`, or in a part's Maturity cell. Without one it is needed now. Remove a marker once its
  stage is reached.
- Build for the current stage. A later item is built early only on the owner's direction;
  `bounds.sh` lists later items apart.
- A stage runs in an environment, which its **Environment** line names. An environment is not a
  stage: one serves several stages and can outlast them (early access keeps taking new features
  first once production opens). The environments topic says where each is provisioned (its
  own account, subscription or project; a small product's production may start in the
  early-access one) and how a release reaches it.
- A stage's audience (testers, a demo audience) is not a persona: it lives in `lifecycle.md`.
- The constitution's engineering rules follow the same stages: a rule deferred for now names
  the stage it is due before (`before Public preview`), and the constitution names no stage of
  its own.
- `product.md` shows journeys by area and stage; each sits in the column of its `from:` stage,
  or the current one.

## Parts are the boxes of the system diagram

- A part runs, ships or stores on its own: an app, a service, a worker, a store. Name it by what
  it is (phone app, backend, web client), not by a concern.
- Each part says why it exists: the journeys, R and Q that need it. A part no journey needs is a
  question for the owner.
- Components (API, storage, identity, sync) are sections of the part file. Each component's
  behaviors and invariants sit under it and keep the part's numbering (`P2.B-07`).
- External systems are not parts: the interfaces topic lists each with what it is used for and
  what users see when it fails.
- Build and delivery tooling (CI, migrations, setup jobs, developer utilities) is not a part:
  the operations topic, one line each.

## Diagrams

People read a simple diagram faster than the paragraph it replaces. Draw flows, states and
structure in Mermaid (GitHub renders it), and cut the prose a diagram makes redundant:

- `architecture.md` Shape opens with a `flowchart` of users, parts and external systems and what
  flows between them.
- The core loop's path through the parts is a `sequenceDiagram`; so is any other key journey's.
- A journey with branches shows its scenario and variations as a `flowchart`.
- Anything with a lifecycle (a record, an order, an account) is a `stateDiagram-v2`.
- A part with several components opens with their `flowchart`.

Keep each diagram small, about a dozen nodes; split rather than grow. Quote a label that holds
punctuation (`A["Saved; not sent"]`): a bare `;` or `:` breaks the render. Diagrams show; text items
define: IDs, promises and failure meanings stay in text, where agents and checks read them.

## Writing

- **Crisp**: the shortest wording that keeps the meaning. One fact once; no filler, no
  restating, no jargon. A reader should never wonder what a sentence adds.
- **Roles, never people**: the owner, an operator, a worker. Where tooling needs an account,
  name its handle (`@account`), not the person.
- **Paths are repo-relative**. Another repository is `org/repo` plus a path inside it. Never a
  local machine path.
- **Sources** are short: `Source: owner, 2026-10-12: "<the operative words>"`, verbatim, trimmed
  with `…`. One source per item.

## What earns a line

Keep a line only if both hold:

1. It rules out a meaningful set of wrong implementations a competent engineer might build.
2. It rejects no valid real input, environment or use. For an external system's shape, cite the
   upstream contract or a sample in `observed.md`.

A line that only describes how the code currently does something is deleted: the code says it.

## Elements

- **IDs**: `J-`, `R-`, `Q-`, `C-`, `N-` (L0); `P<n>` parts and `D-` decisions (L1); `P<n>.B-`
  and `P<n>.I-` (L2), e.g. `P3.B-02`. Two or more digits. Never renumber; retire with
  `retired: <reason>` on the same line (a heading: `## D-12 <title> (retired: <reason>)`). An ID
  lives on one line: retired in place while its file stays; when its part or file goes, it is
  listed once under `## Retired` in `architecture/retired.md`, which `architecture.md` links:
  `- P3 Store: retired: now a component of P1.` and
  `- **P3.B-01 <title>**: retired: now P1.B-21.` Lower levels cite upper ones; tests cite the
  IDs they prove.
- **Maturity**: each J, R, Q and part is `hypothesis` or `validated`; build no strict validators
  on hypothesis items. Validated needs evidence: for a J, R or Q, an owner-stated need or
  observed use; for a part, it runs and its checks pass in a real environment. Only the owner
  promotes.
- **From**: `from: <stage>` marks an item needed only from a later stage (The lifecycle, above).
- **Planned**: `planned: <change>` marks owner-decided intent not built yet, written with the
  maturity: `**R-04 Title** (validated, planned: 012-export)`, or in a part's Maturity cell
  `hypothesis, planned: 012-export`. Undecided intent stays in the change record. Remove the
  marker in the PR that builds it.
- **Enforcement**: each Q and I names what keeps it true (a test, an analyzer, a metric,
  `owner review`) or says `unenforced`.
- **Qualities are bands** with a method (`p95 sync under 5 s on a slow network`), never a tuning
  number.
- **Constraints**: rule, why, scope, source, revisit when, and how to ask for an exception. Scope
  names the parts and journeys it binds, or `all`. A C- says what must hold; the constitution's
  Action policy says who may act and links the C- for a limit such as a spending ceiling.
- **Bounds**: constraints, non-goals and qualities bind every change. `bounds.sh <change>` lists
  those in its scope; the change marks each it could break (change.md, The record).
- **Why it works**: for each key journey, R or Q, the why-it-works topic gives the claim, the
  mechanism, its assumptions (linked to observed facts), the alternatives considered (at least
  one managed or off-the-shelf) with why each was rejected, and when to revisit.
- **Interfaces**: L2 states guarantees (owner, compatibility promise, failure meaning); the schema
  stays in code and is linked.
- **Left open**: each level ends with deliberately unresolved details or deferred opportunities.
  Agents choose technical details within existing bounds; owner deferrals keep their limits and
  revisit triggers. Anything that needs the owner goes to the inbox instead. The executive page
  (`product.md`) has no Left open; its Left open items live in the detail files.

## Resolve owner answers

`specs/inbox.md` is an open-items queue. Process an answer in the same change that incorporates
it; implementation or merge need not be complete before the answered item leaves the inbox.

1. Read the question and answer together. A number selects that item's option; preserve its
   meaning and any qualifications. An ambiguous, conflicting or partial answer leaves only the
   unresolved question open, linked to the resolved portion.
2. Apply the decision to its canonical home: product intent in L0, design in L1/L2, authority in
   the constitution's Action policy, holds/waivers in `specs/records.md`. Record the disposition,
   operative owner words verbatim, date and source (conversation reference, PR/comment URL or
   recorded git path). Relayed words keep their original source. A bare option number also needs
   the selected option's meaning. The canonical entry is the record; do not duplicate a transcript
   or create a second decision journal. Agent defaults must be labeled as agent decisions.
3. Transfer remaining work to a change's Tasks/State and link it with `planned: <change>` for
   unbuilt intent. Preserve acceptance, attempt/fix-round limits and explicit holds; an answered
   question does not grant implementation, promotion or delivery authority. An owner decline or
   deferral lives at the relevant non-goal, decision or Left open item, with its reason and revisit
   trigger; a proposal to decline is still open until decided under the applicable authority.
4. Verify the destination contains the full decision and provenance and the remaining obligations
   have a durable home, then remove the answered item and stale links to it in the same edit.
   If folding is blocked, keep the item open with that blocker. Never delete solely because an
   old item says `folded`, `declined` or `deferred`; reconcile those legacy items the same way.

## One-way doors

Data ownership, a breaking interface change, a technology choice, a trust boundary, a product
constraint, anything that deletes user data. These take tier T3: a design review plus the owner's
verdict ([protocol.md](protocol.md)). Everything else is a two-way door: decide, record it in the
change.

## Budgets

Budgets are reading budgets, never targets: never pad toward one, and never drop an item that
passes the line test to fit. Past a budget, tighten the wording, move detail to code or tests,
or split (Layout). `observed.md` 150 lines, split into `observed/<area>.md`; an open change
150, split into changes that each ship on their own.

## Organizing

Organize architecture by part. A concern that crosses parts (security, observability, data) is a
topic of `architecture/`; one that belongs to a part stays in the part file. Edit in place: no amendment journals,
history sections or "reviewed, no change" notes.
