# Review the owner inbox

**Inputs**: `specs/inbox.md`, the owner's selection of items/tags and answers, current canonical
spec/policy, linked changes/records, and actual repository state.
**Writes**: inbox metadata and pending answers, substantive canonical spec/policy edits with
provenance, and remaining obligations in changes/records. A read-only preview writes nothing.
**Result**: only unresolved owner questions remain waiting for the owner; answers are incorporated
or explicitly awaiting agent processing, with a destination or a named incorporation blocker.

## What belongs here

`specs/inbox.md` is a short input queue, not a backlog or decision archive. Every question must
say what the owner needs to decide, clarify, supply or report and why the agent cannot resolve
it from existing direction and authority. Routine implementation, scheduling within approved
scope, verification and already-decided follow-up belong in a change's Tasks/State. A request
for an owner-only observation is a real question; merely running an agent-owned check is not.

Use `## Waiting for you` for unresolved questions (`agent · open`). Use `## To incorporate` only
as a temporary handoff for answers or new owner direction (`owner|agent · processing`). An
answer in an otherwise open item also needs processing first. Processing is agent work, not a
request to repeat an answer. Preserve an unprocessed answer if interrupted; do not leave it only
in chat.

## Item contract

Keep the existing heading and date/actor/state line. Add these fields (see the installed
inbox template for a complete example):

- **ID**: `IN-` followed by 12 lowercase hexadecimal characters. Generate a fresh token, for
  example with `od -An -N6 -tx1 /dev/urandom | tr -d ' \n'`; check for a collision before use.
  Never renumber, reuse or derive an ID from the title or list position. Resolve duplicate
  identities before accepting an ID-based answer.
- **Revision**: a positive integer, initially 1. Increment when the question, option meaning,
  option order, relevant context or action scope changes. Option order matters for numeric
  answers. Moving the whole item, tags and cosmetic edits do not change its revision or ID.
- **Kind**: `decision`, `clarification`, `feedback`, or `direction` (incoming owner direction).
- **Tags**: comma-separated lowercase kebab-case topics, such as `product, ux`. Reuse project
  vocabulary; add a topic when useful. Status, authority and repository identity are not tags.
- **Related**: links to affected requirements, decisions, changes or records when known.
- Numbered choices use stable keys: `1. **keep — Keep the current behavior (Recommended)**: ...`.
  Keys are lowercase kebab-case, unique within the question, and never reassigned to a different
  meaning. A number is only a shortcut for the displayed revision's option. Feedback can use
  free text instead of artificial choices. Give a recommendation and a reversible default where
  applicable; silence, a dismissed dialog and a skipped item are not owner answers.
- **Answer**, **Answer date**, **Answer source**, **Answer revision**: initially blank. Preserve
  the owner's operative words, qualifications and source; a multiline answer uses `>` quotes.
  Record the question revision the owner answered, not a newer revision encountered on resume.
  Free-text answers and explanations are first-class; never infer an answer from an empty field.

The ID plus repository identifies the item across projects. A stable ID alone does not prove
that an old selection still means the same thing. Compare the answered revision and actual
question/options before incorporation; clarify stale or ambiguous selections rather than
applying a current option by its old number. Do not invent dates, sources or revision matches
for imported answers. Preserve known provenance and name any uncertainty.

## Review loop

1. **Establish the source.** Read current checkout status, branch/revision and inbox, then the
   applicable policy and linked canonical destinations. Do not label an old checkout latest
   main. If latest-main review was requested, verify that remote snapshot and reconcile any
   newer local answers before editing in the appropriate workspace. Cross-project review uses
   only the repositories requested, with their own policies, revisions and qualified IDs; no
   home-directory scan or implicit write to every repository.
2. **Reconcile before asking.** Inspect pending answers, owner direction and resolved entries.
   Read destinations, not just dispositions. Apply spec.md, Resolve owner answers. Keep work that still
   needs processing visible separately; if incorporation needs clarification, ask only that gap.
   A topic/ID filter limits the review and edits to matching items and their linked destinations;
   report other known pending processing without silently widening the scope.
3. **Present the next question.** Default to one at a time, following dependency order then oldest
   first. Show its ID/revision, tags, concise context, options and recommendation. Use the native
   question tool if the current client supports it; otherwise ask in conversation and wait.
   The owner may choose, qualify, explain, ask for context, skip or stop. Skip leaves it open;
   an actual deferral is incorporated with its reason and revisit trigger. A recommendation
   batch applies only to the exact displayed IDs, revisions and option meanings.
4. **Process each answer before the next question.** Re-read the item to detect concurrent edits.
   Persist the answer fields if incorporation cannot finish immediately, then follow the resolver
   ([spec.md](spec.md)); a filled field or changed metadata is not incorporation. A readonly
   session can collect an answer in conversation but must say it is not persisted; never claim a
   file update or bypass the session's write limits.
5. **Acknowledge and continue.** State the selected meaning and qualifications, canonical path/ID,
   remaining work/holds and inbox disposition. If blocked, name the processing blocker and next
   actor instead of claiming resolution. Finish with counts of owner questions and processing
   items in the reviewed scope, and the next action. Do not begin implementation solely because
   a product choice was answered; use the existing action policy and any explicit request.

`status.sh --json` exposes recorded queue state and metadata; `doctor.sh` flags structural
problems and pending processing. A known answer/question revision mismatch remains an owner
clarification in status, with its old answer preserved; it is not ready for incorporation.
These helpers cannot verify the meaning of a decision, its
truth, or complete incorporation. Review the actual spec diff and linked obligations.
