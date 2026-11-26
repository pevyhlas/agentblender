# Compaction

**Inputs**: current canon, code, constitution, agent memory and worklogs, owner direction.
**Writes**: the spec files ([spec.md](spec.md), Layout), inbox and `.prdkit/history/`.
**Result**: one current canon the owner reads quickly, plus owner questions.

## Compaction (on overflow, and every ten closed changes)

Preserve operative hold and waiver rows and their referenced authority entries. Move long quotes
out of the rows by checked reference ([protocol.md](protocol.md)), never by shortening the scope.

Rewrite within the budgets, by tightening and splitting, never by dropping items. Keep current truth, decisions with their alternatives
and revisit conditions, why-it-works assumptions, and retired IDs (one line each). Drop to git: history,
amendment notes, superseded wording and resolved questions. Keep a rejected alternative that is
still a plausible proposal; it prevents re-proposals. `.prdkit/history/archive.md` grows into git
instead: condense its older entries harder; their details stay at the referenced commits.
Start a missing `.prdkit/history/log.md` from `history-log-template.md`. Fold each
`.prdkit/history/log/<id>.md` into `.prdkit/history/log.md` at its newest end (on top, unless the log lists the
oldest change first), ordered like the log by date, then change number, and delete the entry
files. To find the original close commit and its PR locator after folding, run
`git log --diff-filter=A -- .prdkit/history/log/<id>.md`; compaction may retain that commit SHA.
For a renamed entry, `--follow` can trace the earlier path. Keep no task lists or acceptance tables.
`.prdkit/history/log.md` past its budget keeps its newest entries. Condense older entries under
a cutoff below every open or planned change and every retained live entry. Above that
cutoff, condense any older closed entries as an exact sparse set. Keep archive summaries
for notable outcomes and lessons. For example:

> **Log condensed through**: 002; also 004-061, 063; those entries: `git show <sha>:.prdkit/history/log.md`.
>
> **Allocated through**: 065

Use `none` when there is no cutoff. To use cutoff-only form, omit `; also 004-061, 063`
from the example. Both forms put the Git pointer after `; those entries:`. The sparse field
must be on the same physical line, between the cutoff and pointer, and consists of
comma-separated numbers or inclusive `N-M` ranges (one to nine decimal digits each).
Use ascending, non-overlapping ranges strictly above the cutoff. Do not span open or
planned numbers, such as 003 and 062 here. After the backticked pointer, an optional period
or prose introduced by a period or semicolon and a space is allowed, not another field
(for example, `; also see the migration notes` is prose, not a set).

On its own line, write `**Allocated through**: N` (one to nine decimal digits). Record
the largest allocated ID known at the pass: take the maximum of the previous floor,
the existing cutoff and archived IDs, live log and change record IDs, and every ID
being folded. Never lower this value or derive it from the rewritten sparse list.
The floor reserves numbers for allocation; it does not mark changes closed. Cutoff-only
archives in the supported format may omit it until the next pass.

Allocation and close check the working archive,
`HEAD`, and the locally available tips of `main`, `master`, `origin/main`, `origin/master`,
and the branch identified by `origin/HEAD` (remote-tracking and local, if present),
without fetching. Allocation also checks the other worktrees' working archives. Explicit
allocation must exceed the maximum of the checked floors, cutoffs, sparse IDs, live logs,
per-change log files and change records, even when the requested number is a gap. Automatic
allocation starts above that mark and accounts for planned and other reservations too. Status,
doctor, and the bounds CLI report against the current checkout's archive; update a stale
checkout before using them to assess current integration history.

Once indexed, remove the entry from both `.prdkit/history/log.md` and `.prdkit/history/log/`; doctor rejects
duplicates in the resulting live log, even if the text is identical. A stale copy of
an open record in another worktree does not reopen an archived change.

Before removal, commit a folded log that contains the entries being indexed, and use its SHA in
the pointer. The snapshot must remain reachable from a shared Git ref: if merges are squashed,
land the fold before the removal or preserve the snapshot on a durable ref. To retrieve an
entry, look up its number in the index and find its full ID in the log at that SHA. On every later
pass, carry every earlier sparse number forward in the index. It may move under the cutoff,
but must never become a gap. Verify that its entry is still retrievable. If
the new pointer would lose earlier entries, carry them forward from the previous snapshot
into the new Git snapshot only. Keep earlier cutoff-only pointers for any older history that
predates the snapshot. Doctor requires an allocation floor for sparse indexes, checks
it covers every indexed ID, and rejects a decrease when the previous committed floor
is available locally (HEAD for working edits, otherwise the first parent). Doctor also
checks that every sparse number has an entry in the pointed folded log. A missing snapshot
commit fails in a full clone; in a shallow clone it is reported as unavailable history,
not as verified. Fetch the history and rerun doctor.

A first `.prdkit/history/archive.md` starts from `archive-template.md`. End each pass
with `- <date> compaction: <what changed>` as the newest entry
in `.prdkit/history/log.md` (on top, unless the log lists the oldest change first). Archived entries are
from completed compactions; they do not count toward the next ten closes.
