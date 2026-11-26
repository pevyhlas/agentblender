# secrets-scan: local settings (example)

Copy this file to `references/local.md` in the vendored skill and edit it. Library updates never
read, write or remove `local.md`.

## Allowlist

Two kinds of lines. An allowlisted fact is still counted and listed under `allowlisted` with the
entry that matched; it is never dropped silently.

`allow-path:` a path glob relative to the repository root (`*` within a segment, `**` across
segments; `dir/**` also matches everything under `dir`). Every fact in a matching file is
allowlisted:

- allow-path: tests/fixtures/**
- allow-path: docs/examples/*.env

`allow-fingerprint:` a fingerprint from a report, with or without the 4-character prefix. Only the
12 hex digits are compared, so the line holds no part of the value that the report did not
already show:

- allow-fingerprint: …0123456789ab
- allow-fingerprint: abcd…ba9876543210

Prefer fingerprints for single known values (a published emulator key, a public test vector) and
paths for fixture directories. Add a short reason after `#` on the same line when it helps the
next reviewer.
