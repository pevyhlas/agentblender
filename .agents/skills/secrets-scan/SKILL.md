---
name: secrets-scan
description: "Lists where a change adds secret-shaped values (private keys, GitHub, Azure storage, Amazon and Slack tokens, JSON web tokens, password or secret assignments, high-entropy config values, newly tracked .env or key files) as file:line, rule id and a redacted fingerprint, never the value. Use when reviewing a change for leaked credentials; not for deciding whether a value is a real or acceptable secret."
metadata:
  prdkit/needs: python3 git
  estate/requires-kit: ">=3.1.5"
---

# secrets-scan

A script reads the lines a change adds and lists secret-shaped values with `file:line`, a rule
id and a fingerprint. It never prints a value, and it does not decide whether a value is a live
secret: you judge each fact against the change, and you own the verdict.

## Run it

Run the script itself (not through `python3`), on the change under review. In a git checkout it
reads the lines added between the merge base of `--base` (default `origin/main`) and `--head`
(default `HEAD`), from git objects. Without git history (a copied tree or a review package), pass
the files the change adds or edits instead, and it reads every line of each:

```bash
scripts/secrets_scan.py --root <repo> --base origin/main --head <head-sha>
scripts/secrets_scan.py --root <repo> path/to/changed-file other/changed-file
```

`--diff change.diff` reads the lines a unified diff adds, and `--all` every file tracked at
`--head`. Add `--json` for every fact with its blob ids.

The markdown summary is at most 60 lines; `--json` holds every fact. Exit 0 means the scan ran,
whatever it found; exit 2 is a usage error, a path that does not exist or a git error.

To hand the facts to a reviewer that cannot run scripts, write both files into the change's
evidence directory under these file names, which the reviewer looks for:
`--out .prdkit/cache/evidence/<id>/facts/secrets-scan.md --json-out .prdkit/cache/evidence/<id>/facts/secrets-scan.json`.
Attach them to the Product-Kit review package, which keeps them under `evidence/`:
`build-review-package.sh <id> <base> <head> --evidence .prdkit/cache/evidence/<id>/facts/secrets-scan.md --evidence .prdkit/cache/evidence/<id>/facts/secrets-scan.json`.
Neither file holds a value, so both are safe to attach.

Every report names what it read: in a range, `revision.diff_from` (the merge base) and
`revision.head_commit`, plus each file's git blob id (`files[].blob`); for paths, the commit and
the files that differ from it; for `--diff`, the diff's sha256. When the facts are already in the
review package, cite them only if that head is the head under review or the blob ids match;
otherwise run the script again, or say under Unverified that the facts are for another revision.

## Facts

| id | what the script saw |
|---|---|
| `private-key-block` | a `-----BEGIN ... PRIVATE KEY-----` block with a base64 body (a header alone, such as a format string, is not reported) |
| `github-token` | a GitHub token prefix: `ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_` or `github_pat_` |
| `azure-account-key` | an Azure storage `AccountKey=` or a `SharedAccessKey=` value; the detail says when it is the published storage-emulator key |
| `azure-sas-signature` | a `sig=` signature of an Azure shared access signature |
| `aws-access-key-id` | an `AKIA`/`ASIA` style access key id |
| `aws-secret-access-key` | an `aws_secret_access_key` assigned a 40-character value |
| `slack-token` | an `xox?-` Slack token or a Slack webhook URL |
| `jwt` | three base64url parts whose header and payload start `eyJ` |
| `generic-secret-assignment` | a name ending in password, secret, token, api key or credentials assigned a literal (quoted anywhere; unquoted in config and shell files); the detail names the key and flags placeholder-looking values |
| `high-entropy-literal` | a long random-looking value of a config entry (appsettings and other settings JSON, `.env`, YAML, `.toml`, `.ini`, `.tfvars`, `.bicepparam`); the detail says when it looks like a public key or a commit id |
| `sensitive-file` | a `.env` file (not `.env.example`) or a key, keystore, publish-settings or state file added in the range (or tracked, with `--all`) |

The fingerprint is the first 4 characters of the value (only for values of 20 or more
characters) and the first 12 hex digits of its sha256. Equal fingerprints mean the same value,
so you can tell one value repeated from several. For a short or common password, someone holding
the report could find the value by hashing guesses, so share reports only where the code itself
can be read. Facts in test paths carry `test_path: true` and
are listed last.

A fact is evidence, not a finding. Decide from the change whether the value is a credential that
works somewhere: a test fixture, a published emulator key, a public key and a placeholder are not
leaks; a key that was ever pushed needs rotating, whatever the next commit does. Never quote a
value in your report; cite the fingerprint.

The `limits` section lists what the script could not read (binary files, files over 2 megabytes,
symlinks and submodules). Name the ones the change touches under Unverified.

No fact is not the same as safe. The script does not see:
- values split across lines, built at run time, or encoded (base64 of a token is not a token);
- secrets in binary files, archives, images or notebooks' output cells;
- names it does not know (a `connstr` holding a password without `Password=` in it, or a
  `scheme://user:password@host` address under a name such as `url`);
- lines a change removes: a secret that was committed before the range is outside it (use `--all`);
- whether a credential is live, revoked or scoped.

## Allowlist

The repository's accepted values live in `references/local.md` inside this skill, as
`allow-path:` path globs and `allow-fingerprint:` fingerprints (see
`references/local.example.md`). Allowlisted facts are still counted and listed separately with
the entry that matched, so say in your report when one carried weight. Without the file there is
no allowlist.

## Optional scanners

`--external gitleaks` and `--external trufflehog` add those tools' rule, file and line when they
are installed; both are off by default, and their values are never kept. See
`references/hooks.md`.

<!-- rendered from skills/secrets-scan/SKILL.md · do not hand-edit (source: agent-library) -->
