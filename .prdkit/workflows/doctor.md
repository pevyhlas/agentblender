# Diagnostics

**Inputs**: installed kit, `specs/`, tests, agent context files.
**Writes**: none.
**Result**: concrete findings and the smallest repair for each.

Run `.prdkit/scripts/bash/doctor.sh` (`--json`, `--strict`): unsupported formats, competing spec forms,
files over their reading budgets, cited
IDs that are undefined or retired, malformed records, a closed change without its log entry,
a malformed `.prdkit/history/log/<id>.md`, a close row over 200 characters, a spec link to
history, old history paths (move them as the diagnostic says), a reviewed record merged without closing,
past-due hold reminders and expired waivers, open inbox items by age, pending answers, retained resolved and
missing inbox metadata/provenance, duplicate IDs/option keys and stale answer
revisions, local machine
paths, links to missing files in the spec and `.prdkit/history/archive.md`, spec files no summary links
(1.1 layout), `observed/` pieces `observed.md` does not link, a lifecycle stage naming no
environment the environments topic lists, raw evidence under `specs/`, oversized agent context
files and agent projection drift. Trace advisories, counted by code under `trace` in `--json`:
missing `Code:` paths, `planned:` markers naming a closed or no open change, retired IDs cited in
spec text, R/Q items no part or architecture file cites, parts citing no J/R/Q, and change
records restating spec items or citing undefined IDs. Malformed spec IDs/records, an unlogged
close, duplicate inbox identities/fields/option keys and mismatched answer revisions are errors;
the rest are advisories that `--strict` fails. The kit's `scripts/init.sh --check` reports
installation drift. Never repair during diagnosis or read a clean report as readiness.

If present, `specs/design.md` is checked for the four headings in `design-template.md`, a
100-reading-line budget and resolvable links and citations, not for the quality of its direction.

For a checked `ref=` reason, doctor reports the verified row's scope and end condition
(`authority_records` in JSON, when present), not the quote. A broken reference is a malformed
record, reported at its line in `specs/records.md`.

Text and JSON (`budget_excess_reading_lines`, a nonnegative integer) report the total
reading lines over budget: sum `max(reading lines - budget, 0)` over the files checked
for reading budgets, not the number of advisories or the files' whole lengths. The same
100-character accounting drives each Budget advisory: validated generated navigation is
excluded, malformed blocks still count, and a reader failure is a finding, not evidence
of zero excess. Byte-based agent context budgets are separate.
Compaction due is an advisory at ten closes since the latest compaction
line, in either log order, plus every pending `.prdkit/history/log/<id>.md` entry. If the folded
log cannot be read, doctor reports a finding instead of a count.
On older Git in a partial clone, doctor warns if the legacy history paths cannot be
checked in HEAD using local objects. Working paths and the index are still checked.

The "Merged without closing" advisory needs the default-branch ref and its tree and blob
objects locally, not full history. CI checkouts should fetch full history (for example,
`fetch-depth: 0` with `actions/checkout`) for the separate history-based
"Closed without a log entry" check.

Ordinary text and JSON distinguish the recorded install version from observed spec layout.

For configuration/provenance questions add `--inventory --json`. It reports installed hashes,
project-owned customizations, source pins and resolved versus projected agent models with winning
rules. Unobserved active harness/model and runtime capabilities remain unknown. This is opt-in:
do not load the full inventory for ordinary status questions. See the findings as well as the
inventory; a recorded pin or projected model does not prove the running session used it.
