# Writing and editing the spec

**Inputs**: the change's intent, current spec files, code and tests, observed facts.
**Writes**: the files in Layout, or `specs/SPEC.md`: one core form at a time (a split moves
the combined spec in one change and deletes `SPEC.md`). The optional UI summary can stand
beside either form.
**Result**: a spec an owner reads quickly, that states current truth and narrows what matters.

## Layout

| File | Level | Holds | Budget | Past its budget |
|------|-------|-------|--------|-----------------|
| `specs/product.md` | L0 summary | the executive page, read at a glance: one sentence, the stage, the problem and its pains, a diagram, who it is for, the core loop and supporting journeys, the most important items, the bet, a link to the inbox | 80 | detail moves down to `specs/product/` (all pains and personas to `users.md`); a line and a link stay |
| `specs/product/users.md` | L0 | every pain and persona, once `product.md` shows only the main ones | 150 | `users/<area>.md`, 150 each |
| `specs/product/journeys/j-NN-<slug>.md` | L0 | one journey: who, intent, situation, scenario, variations, success | 150 each | it is two journeys: one intent each, the first keeps its ID, each links the other |
| `specs/product/requirements.md` | L0 | R- items by area | 300 | `requirements/<area>.md`, 300 each |
| `specs/product/qualities.md` | L0 | Q- items | 300 | `qualities/<area>.md`, 300 each |
| `specs/product/constraints.md` | L0 | C- items | 300 | `constraints/<area>.md`, 300 each |
| `specs/product/non-goals.md` | L0 | N- items | 150 | `non-goals/<area>.md`, 150 each |
| `specs/product/lifecycle.md` | L0 | the current and next stage; per stage its goal, audience, environment, promise and exit criteria (`lifecycle-template.md`) | 150 | `lifecycle/<stage>.md`, 150 each; Now and Next stay |
| `specs/architecture.md` | L1 summary | the overview, read at a glance: one sentence, the system diagram, parts and why each exists, the core loop's path, key decisions, key areas in a line each, risks, left open | 100 | detail moves to a topic or a part; a line and a link stay |
| `specs/design.md` | L1 summary (optional UI) | Direction, Composition, Copy and Decisions (`design-template.md`); cite the J-/Q- IDs it serves | 100 | tighten the summary and link longer decisions; implementation values live in code |
| `specs/architecture/<topic>.md` | L1 | one topic in detail: `security`, `observability`, `data`, `interfaces`, `why-it-works`, `environments`, `operations` (`architecture-topics-template.md`) | 300 each | `architecture/<topic>/<area>.md`, 300 each |
| `specs/architecture/decisions.md` | L1 | D- decisions, a `## D-NN <title>` section each (`decision-template.md`) | 300 | `architecture/decisions/<area>.md`, 300 each; one long decision gets `specs/decisions/D-NN.md` |
| `specs/architecture/retired.md` | L1 | the `## Retired` list: parts that went and IDs that moved, a line each; linked from `architecture.md` | 300 | `architecture/retired/<area>.md`, each under `## Retired` |
| `specs/parts/p<n>-<slug>.md` | L2 summary | one part: why it exists, components with their behaviors and invariants, interfaces, failure, budgets | 200 each | a component moves to `parts/p<n>-<slug>/<component>.md` |
| `specs/parts/p<n>-<slug>/<component>.md` | L2 | one component, once its part outgrows its budget | 200 each | it is two components |
| `specs/SPEC.md` | L0–L2 | the combined spec in one file, for a small project | 300 | the layout above, in one change |

Each level opens with a one-page summary that links its detail: `product.md`, `architecture.md`,
optional `design.md` for projects with a UI, and each part file. A topic with little to say stays
a short section of its summary until the summary passes its budget; then it moves to its own
file. Each file starts from its template in
`.prdkit/templates/`: the one its row names, or `product-template.md`, `journey-template.md`,
`requirements-template.md`, `qualities-template.md`, `constraints-template.md`,
`non-goals-template.md`, `architecture-template.md`, `part-template.md` and, for `SPEC.md`,
`spec-template.md`; `users.md` and `retired.md` follow the comments in the product and
architecture templates.

**Splitting**: a budget holds only because every file has somewhere to grow. Past one, a file
splits as the table says, never by dropping items; a piece past its own budget becomes two
pieces beside it (two areas, `lifecycle/<stage>-<part>.md`, `decisions/D-NN-<part>.md`). A
piece opens with its parent's title (`# P<n> <part> — <component>`; a decision piece
`# <title> — <part> (D-NN)`, so it does not define the ID twice), the parent keeps one line
and a link per piece, and IDs never change. Doctor checks that every link resolves, that every
spec file in the split layout is reached from a summary (`product.md`, `architecture.md` or
optional `design.md`) through links (a folder link reaches the files directly in it; a retired
file is exempt), and that `observed.md` links its pieces.
The spec is current canon only; never cite `.prdkit/history/`. Read its archive only on
demand for past directions, lessons or outcomes ([compact.md](compact.md)). Links in the archive
must resolve too; a Git locator (`git show <sha>:<path>`) is code, not a link.

## Generated tracking navigation

Keep a short record ID near its source item, on a separate `Tracking:` line (below a heading,
so its anchor does not change). Use same-file reference links. The optional v1 convention is:

```markdown
Tracking: [REC-123][nav-rec-123]

<!-- prdkit:navigation:v1 begin -->
[nav-rec-123]: https://tracker.example.test/items/REC-123
<!-- prdkit:navigation:v1 end -->
```

The definitions are active Markdown, not commented out. This is navigation, never evidence,
authored citations, copied tracking status or a source-of-truth change. External exporters return
mappings; inserting links is an ordinary source edit/PR under project authority.

The recognizer deliberately accepts a narrow subset:
- One complete footer at end of file (only blank lines may follow), exact marker lines/version,
  and one or more single-line definitions; no blank lines, titles, prose or nested blocks inside.
- Keys are `nav-` followed by ASCII letters/digits/underscores/hyphens, unique across the whole
  file after case/whitespace normalization. URLs are absolute HTTP(S) detail URLs with a host,
  optional numeric port and nonempty path; no user information, whitespace or Markdown titles.
  Keep credentials and private target metadata out of published URLs.
- Every key is used only by a `Tracking: [ID][nav-key]` line, with zero to three leading spaces.
  More links on that line are space-separated; IDs use ASCII letters/digits and `._/-`.
  Repeated uses reuse one definition. These lines contain no prose or evidence assertions.
- Authored references remain outside the footer. Possible conflicting definitions or other
  uses, unsupported HTML context, malformed/mixed/unknown blocks stay visible and counted with
  a diagnostic. Fenced examples do not qualify. This is not a general Markdown parser.

Only validated footer definitions and their envelope are excluded from prose reading budgets.
Visible IDs, mappings and all substantive text still count. Auto-loaded instruction byte budgets
remain raw. An exemption alone saves no model input.

For routine spec context use `bash .prdkit/scripts/bash/read-spec.sh specs/<file>.md` before
loading text into the agent context. The compact view keeps original line numbers. It and `--stats`
report the projection version and raw and omitted sizes. All projected views report the source
path, observed HEAD and content blob. The working-tree snapshot may differ from HEAD; the blob
identifies the bytes read.

- `--index` lists column-zero ATX headings as a tree, with original line numbers and reading-line
  counts. A heading's count includes its own line and its subsections, at one line per 100 characters
  (rounded up, at least one per physical line), excluding only validated navigation.
- `--section 'Heading text'` returns one section, from its heading through the line before the next
  heading of equal or higher level (or EOF). Quote multiword headings. A leading heading ID (e.g.
  `D-01` or `P1`), a defined item ID (e.g. `R-01` in a bold requirement bullet or `P1` in a Parts
  table), or a record ID in a table selects its nearest enclosing heading. `Tracking:` links and
  other citations do not select sections. Matching ignores ASCII case and collapses whitespace.
  Zero or multiple matches exit 2 and list candidates with their source lines.
- `--stats` shows accounting; `--target nav-key` resolves one validated target; `--raw` returns exact
  source bytes. All modes are mutually exclusive; use `--help` for syntax.

**Supported spec grammar for `--section` and `--index`:**

Column zero means the line starts without a space or tab. Only column-zero headings (`#` through
`######` followed by a space or end of line) start sections. A `-`, `*`, or `+` bullet at column
zero defines an item ID only with exactly one space after the marker, then `**`, the ID, and an
optional plain text title separated by a space or tab. The first following `**` must be on the same
line. There are no links, code, emphasis or HTML inside the bold ID: the span allows none of `*`,
a backtick, `[`, `]`, `<`, `>` or `\`, and does not end in whitespace, including Unicode spaces.
After the closing `**`, only end of line, a space, or `:` `.` `,` `;` `(` is allowed as the next
character. Other bullets are opaque; indented and nested bullets never define IDs.

A column-zero table header must be at the start of the file or immediately after a heading or blank
line, with its matching delimiter on the next line. A header/delimiter pair elsewhere is refused.
While a table is open, all its rows are body rows, even rows that look like delimiters.
Outer pipes are optional and `\|` stays in its cell. `Part` in the first header cell enables part
IDs; well-formed records in first cells supply record IDs. Other text is opaque to section lookup,
including indented code and list continuations. Plain record rows still work for status and doctor.

Column-zero backtick or tilde fences and HTML comments opened with zero to three spaces are literal:
they supply no headings or IDs. A fence closer uses the same character, at least as many markers,
and no trailing text. For column-zero openers the closer can have up to three leading spaces;
comments end at `-->`, even on an indented line. An indented fence is treated as possibly literal
until a matching closer at any indentation. A column-zero heading, ID bullet, table row, fence, or
comment opener inside it is refused, except for the matching closer.

Both views check the entire file first. They also refuse ATX headings after one to three spaces or
tabs, Setext underlines after paragraphs, block quotes, and CommonMark HTML block openers other than
comments (with zero to three leading spaces), even directly after paragraph text. Put inline HTML
inside a sentence, not alone on a line. Angle-bracket prose that does not start an HTML block is
allowed. Refusals exit 2 at the first offending source line with a `--raw` hint.
Thematic breaks with zero to three leading spaces end paragraphs without defining a heading or ID.
A break uses at least three identical `-`, `*`, or `_` markers, optionally separated by spaces or tabs.

Section output keeps the original line numbers and omits a footer only when legacy navigation
validates it and the focused scanner sees it outside literal examples. Raw Git diffs/blobs remain
the integrity and review artifacts; use them for link changes, source review and revision evidence.
A projection is neither a replacement specification nor a hash/review input.

## Levels

| Level | Answers | Never contains |
|-------|---------|----------------|
| L0 product | what problem, for whom, what users do and see, what must be true | technology (except an owner-sourced C-), code names, file paths |
| L1 architecture and optional UI design | how the parts and UI make the journeys work, and why that works | field-level schemas, step-by-step logic, implementation values |
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
- **Situation** is their context, never delivery status.
- **Variations** cover what changes the experience: no or intermittent connection, an error,
  a missing permission, a limit reached, first use, a mistake to undo, and the project's own.
- **Success** is observable, for the user and for the business.
- Group journeys by product area; the same area names group requirements.

## The lifecycle decides what is needed now

A product moves through owner-chosen stages. The default order is below; skip stages it does
not need. To replace the vocabulary and order, put `**Stages**: Idea > Experimental > Stable`
in the header of `specs/product/lifecycle.md` (or under `## Lifecycle` in `specs/SPEC.md`),
before any stage subheadings. Keep that declaration with Now/Next when splitting stage details.
Headings, diagrams and summary columns do not declare an order.

| Stage | Means |
|-------|-------|
| Idea | the problem and the bet; nothing built |
| Demo | the core loop on sample data, to show |
| MVP | the core loop in real use by the owner, on real data |
| Private preview | invited users on real data; it may change |
| Public preview | anyone can join; it may still change |
| Paid preview | anyone can join, for a fee; it may still change |
| GA | supported; quality bands enforced; breaking changes need a migration |
| Sunset | no new users; users export or migrate; the end date is announced |
| Retired | stopped; data deleted or handed over |

- `lifecycle.md` names the stage **Now** and **Next**, and for each stage its goal, audience,
  environment, promise and exit criteria. Only the owner moves Now.
- Stage names start with an ASCII letter and end with a letter/digit; letters, digits, spaces
  and hyphens are allowed. Separate them with `>`; comparison ignores case and repeated
  whitespace. Declare each name once. No custom-name aliases are inferred; the default alone
  accepts `General availability` for `GA`. Now and Next name whole stages; a `since <date>`,
  parenthesized or semicolon-delimited note may follow. Next must be later than Now; omit it
  when there is no next stage. Separate Now/Next lines and the inline `·` form both work.
- Malformed or duplicate declarations and unknown Now/Next values fail diagnostics; no fallback
  order is selected. With an explicit order, unknown `from:` values also fail doctor. Without
  one they retain the legacy source-note advisory, but status/bounds refuse classification for
  unknown markers in either form (nonzero exit, including JSON mode). With no lifecycle at all,
  the existing all-needed-now default remains; doctor advises when known markers lack Now.
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
- A part no journey needs is a question for the owner.
- Components (API, storage, identity, sync) are sections of the part file. Each component's
  behaviors and invariants sit under it and keep the part's numbering (`P2.B-07`).
- A separately tracked subpart has its own architecture parts-table row and main part file,
  e.g. `# P2.1 Index`, with `**Parent**: P2` in the header before the first section (on its own
  line or as a ` · ` field). The field, not the ID's spelling, declares the relationship.
  The parent must be a known part with no parent of its own: only one level, no cycles.
  Keep each obligation with the part or subpart that owns it (`P2.1.B-07`, `P2.1.I-01`);
  the parent does not repeat its subparts' obligations.
- External systems are not parts; the interfaces topic lists them.
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

- **Crisp**: the shortest wording that keeps the meaning. One fact once, in its home
  (Organizing); no filler, no jargon. A reader should never wonder what a sentence adds.
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

- **IDs**: `J-`, `R-`, `Q-`, `C-`, `N-` (L0); `P<n>` or `P<n>.<n>` parts and `D-` decisions
  (L1); `<part>.B-` and `<part>.I-` (L2), e.g. `P3.B-02`. Item numbers after `-` use two
  or more digits. Never renumber; retire with
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
- **Planned**: `planned: <change>` marks owner-decided intent not built yet, written with the
  maturity: `**R-04 Title** (validated, planned: 012-export)`, or in a part's Maturity cell
  `hypothesis, planned: 012-export`. Undecided intent stays in the change record. Remove the
  marker in the PR that builds it.
- **Enforcement**: each Q and I names, after `Enforced by:`, what keeps it true (a test, an
  analyzer, a metric, `owner review`) or `unenforced`.
- **Qualities are bands** with a method (`p95 sync under 5 s on a slow network`), never a tuning
  number.
- **Constraints**: Scope names the parts and journeys a C- binds, or `all`. A C- says what must
  hold; the constitution's Action policy says who may act and links the C- for a limit such as a
  spending ceiling.
- **Bounds**: constraints, non-goals and qualities bind every change. `bounds.sh <change>` lists
  those in its scope; the change marks each it could break (change.md, The record).
- **Left open**: each level ends with the details agents choose within existing bounds. An owner
  deferral is not one: it keeps its limit and revisit trigger in its home, and no agent overrides
  it. `product.md` has none.

### Optional change metadata

- In a change header before the first section, use `**Tags**: cleanup, small batch` for
  grouping or filtering only. Separate tags by commas; each tag uses only ASCII lowercase
  letters, digits, spaces, `-`, `_`, and `.`.
  Omit the line or leave it blank if not stated. The open helper rejects malformed tags
  in a customized template before creating a record or branch. Tags do not affect scope,
  status signals, review, or acceptance. On close, the dated log line ends with ` · **Tags**: cleanup, small batch`
  when valid tags are present. Omit `**Tags**` from the close helper's description argument;
  the helper adds the suffix outside the 300-character description limit and escapes `_` and
  `www.` to keep them literal in Markdown. Do the same when writing a close entry by hand.
  Doctor advises on malformed tags; status omits them and close refuses them. Duplicate `Parts`,
  `Journeys`, or `Tags` fields are errors in doctor, status, bounds, and close.
- In `## Tasks`, use `- [ ] (P2.1) T003 <outcome; dependency; check name>` to assign a task.
  The part must exist and be explicitly named in the header, e.g.
  `**Parts**: P2, P2.1`; listing a parent does not implicitly list its children. A task without
  the prefix is unassigned. Delete task lines on delivery per [change.md](change.md).
- `status.sh --json` lists explicit relationships in `part_parents` (`id`, `parent`). For
  each open change, `tags` is a list, `tasks_by_part` has `part`, `checked`, `total` for parts
  with recorded assignments, and `unassigned_tasks` has `checked`, `total` for unassigned
  tasks. Invalid assignments are reported separately in `invalid_task_parts` (`part`, `line`,
  `reason`: `unknown`, `outside`, or `malformed`) and do not count toward task progress.
  Part IDs are resolved in the checkout supplying the change record. An unstated list or tally
  is `null`, not zero. Counts stay with the named part;
  readers can roll up by following `part_parents` without counting any task twice.
- These fields are optional. Do not infer a tag, parent, or task assignment from prose.

## Resolve owner answers

`specs/inbox.md` is an owner-input queue with a temporary processing handoff, as defined in
[inbox.md](inbox.md). Process an answer when received and on resume before asking it again;
implementation or merge need not be complete before the answered item leaves the inbox.

1. Read the question and answer together, checking identity, revision and option meaning against
   what the owner actually saw. A number selects that revision's option, never a newly reordered
   list. Preserve the meaning and qualifications. An ambiguous, conflicting or partial answer
   leaves only the unresolved question open, linked to the resolved portion. Record a received
   answer durably before moving on if incorporation cannot finish immediately.
2. Apply the decision to its canonical home: product intent in L0, design in L1/L2, authority in
   the constitution's Action policy, holds/waivers in `specs/records.md`. Record the disposition,
    operative owner words verbatim, date and source (conversation reference, PR/comment URL or
    recorded git revision/path). For a long hold or waiver quote, use the checked authority
    reference in [protocol.md](protocol.md), without shortening the operative row. The authority
    file is exempt from reading budgets: it is verbatim evidence read only on demand.
    Relayed words keep their original source. A bare option number also needs the selected
    option's meaning. The canonical entry is the record; do not duplicate a
   transcript. Retain the inbox ID as plain provenance text or an
   immutable source link, not a live link back to an entry that will be removed. Update the actual
   governing requirement, design or policy, reconcile superseded contradictory text and affected
   references, and retain concise rationale where it explains the choice. An appended quote or
   `folded` label alone is not incorporation. Agent defaults must be labeled as agent decisions.
   Missing historical provenance stays explicitly unknown with the available evidence; ask the
   owner only when that uncertainty affects meaning or authority, not merely to fill a field.
3. Transfer remaining work to a change's Tasks/State and link it with `planned: <change>` for
   unbuilt intent. Preserve acceptance, attempt/fix-round limits and explicit holds; an answered
   question does not grant implementation, promotion or delivery authority. An owner decline or
   deferral goes to its home (Organizing) with its reason and revisit trigger; a proposal to
   decline is still open until decided under the applicable authority.
4. Re-read the destination and diff: verify they contain the full decision and provenance and the
   remaining obligations have a durable home, then remove the answered item and stale links to it
   in the same edit.
   If folding is blocked, keep the answer under To incorporate as `processing`, with the blocker,
   next actor and destination; only an actual unanswered clarification belongs in Waiting for you.
   Never delete solely because an item says `folded`, `declined` or `deferred`.
   Acknowledge the decision, canonical path/ID, remaining work/holds and
   whether the inbox item was removed, narrowed or awaits processing. Never claim incorporated
   without that verification.

Git keeps committed inbox content, not uncommitted conversations: preserve the answer's
provenance in the same durable edit. Create no `specs/inbox-archive.md` or parallel answers
database unless the owner asks for an audit record with an agreed purpose and retention; it never
becomes a competing source of current intent.

## One-way doors

Data ownership, a breaking interface change, a technology choice, a trust boundary, a product
constraint, anything that deletes user data. These take tier T3 ([protocol.md](protocol.md)).
Everything else is a two-way door: decide, then record a decision that binds later work in its
L1/L2 home now, one that only explains this diff in the PR.

## Budgets

Budgets are reading budgets, never targets: never pad toward one, and never drop an item that
passes the line test to fit. A line counts once per 100 characters, so a long row counts as the
lines it takes to read. Past a budget, tighten the wording, move detail to code or tests, or
split (Layout). `observed.md` 150 lines, split into `observed/<area>.md`; an open change 150,
split into changes that each ship on their own; `.prdkit/history/log.md` and
`.prdkit/history/archive.md` 150 each, older closes indexed in the archive
even past a long-open change (compact.md); `inbox.md` 150, answers incorporated and the rest
reviewed with the owner; `records.md` 150, lifted, expired and superseded lines removed (git keeps them).

## Organizing

Organize architecture by part. A concern that crosses parts (security, observability, data) is a
topic of `architecture/`; one that belongs to a part stays in the part file. Edit in place: no amendment journals,
history sections or "reviewed, no change" notes.

**One home per fact.** Elsewhere a fact is cited by ID or link, never restated; a summary (What
must be true, Key decisions, Key areas, Risks) is a line and an ID or link per item. A change that
edits a fact turns its other copies into citations.

| Fact | Home |
|------|------|
| The journeys, R and Q a part serves | its row in the architecture parts table (`bounds.sh` reads its journeys) |
| An interface's promise: compatibility, failure meaning | the owner part's Interfaces, linking the schema in code; the interfaces topic is an index |
| A quality band | the Q-; a part's Budgets states only its share |
| What users see when something fails | the journey's Variations; a part's Failure gives the mechanism |
| Why the design works: claim, mechanism, assumptions | the why-it-works topic, citing its D- |
| Alternatives (one managed or off-the-shelf) and why each lost, when to revisit | the D-, with its source; the design it shapes lives in its part or topic |
| A technology choice | owner-imposed: a C-; otherwise a D- |
| A deferral | owner decline: an N-; an owner-deferred design choice: its D-, with the limit and revisit trigger; a later stage: `from:`; a detail agents choose: Left open; needs the owner: the inbox. Never a change record |
| Live status: built, in flight, waiting on the owner | generated by `status.sh`; `product.md` links the inbox |
