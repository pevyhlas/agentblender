# Change: <short name>

**Opened**: <date> · **Parts**: <P-ids> · **Journeys**: <J-ids> · **Tier**: <T0–T3> · **Parent**: none

<!-- Past its budget, a change splits into changes that each ship on their own (Parent: <id>). -->

## Intent

<Why now, for whom. Inbox lines addressed, one sentence of disposition each.>

## Spec edits

| Level | ID | Edit | Note |
|-------|----|------|------|

## Bounds

<!-- From `.prdkit/scripts/bash/bounds.sh <id>`: each constraint, non-goal or quality this change
could break, with how it holds; then "Others in scope: hold". A conflict goes to the owner inbox
with both sides, never resolved here. -->

- <C-01>: holds: <how>.

## Approach

<Approved design/contract references and spec/code baseline. Only real execution choices:
options, pick, why. Unsettled material product/design choices return to Product-Kit.>

## Tasks

- [ ] T001 <contribution/acceptance; paths; dependency outcome; check>

<!-- Tiny fixes stay inline. Optional detail: .prdkit/cache/implementation/<id>.md;
useful context/progress on the same PR. Product/programme discovery uses workflows/planning.md. -->

## Checks

<Line test, consistency with L0/L1, enforcement for new Q/I. A few lines.>

## Acceptance

<!-- Required PR obligations. Distinguish later non-gating runtime observations on the PR before
execution; existing required checks cannot be demoted to finish. Live-only claims need live proof.
Keep these five columns for status helpers, including on a parent programme. Result is exactly
NOT_RUN/PASS/FAIL/BLOCKED/NOT_APPLICABLE; evidence/revision/environment go in Check or below the table. -->

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | <ID> | <test, CI job, live run or owner trial> | 0 | NOT_RUN |

## State

**Step**: intent
**Waiting on**: nothing
**Next**: <next action>
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
