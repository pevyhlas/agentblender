# Diagnostics

**Inputs**: installed kit, `specs/`, tests, agent context files.
**Writes**: none.
**Result**: concrete findings and the smallest repair for each.

Run `.prdkit/scripts/bash/doctor.sh` (`--json`, `--strict`): 0.x layout or two canons, a
`specs/archive/` folder on main, a `specs/archive.md` whose base commit or last 0.x feature
cannot be read, agent context naming removed 0.x paths, files over their reading budgets, cited
IDs that are undefined or retired, malformed records, a closed change without its `specs/log.md`
line, expired holds and waivers, open inbox items by age, one-line inbox items, local machine
paths, links to missing files in the spec and `specs/archive.md`, spec files no summary links
(1.1 layout), `observed/` pieces `observed.md` does not link, a lifecycle stage naming no
environment the environments topic lists, raw evidence under `specs/`, oversized agent context
files and agent projection drift. Malformed IDs and records and an unlogged close are errors; the rest are advisories that
`--strict` fails. The kit's `scripts/init.sh --check` reports installation drift. Never repair
during diagnosis or read a clean report as readiness.
