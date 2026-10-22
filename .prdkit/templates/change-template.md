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

<Only real choices: options, pick, why.>

## Tasks

- [ ] <...>

## Checks

<Line test, consistency with L0/L1, enforcement for new Q/I. A few lines.>

## Acceptance

| Item | Proves | Check | Attempts | Result |
|------|--------|-------|----------|--------|
| A1 | <ID> | <test, CI job, live run or owner trial> | 0 | NOT_RUN |

## State

**Step**: intent
**Waiting on**: nothing
**Next**: <next action>
<!-- One line per verdict (workflows/change.md, Review):
**Review**: round <n> · <base sha>..<reviewed head sha> · <reviewer> · PASS|FAIL · <n> critical, <n> high, <n> medium, <n> low · <link> -->
