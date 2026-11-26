# Optional settings

Optional unset values use the defaults. Review publisher policy must be configured explicitly
on the trusted default branch before the online gate can pass.

## Configuration

- context-budget-bytes: 16384

## Meaning

`context-budget-bytes` bounds each agent context file (`CLAUDE.md`, `AGENTS.md`, ...); doctor
reports a larger file, and 0 disables the check. Optional adoption-gate keys for
`.prdkit/scripts/bash/check-adoption.sh`: `adoption-pin` (the expected kit commit),
`adoption-manifest-sha256` and `adoption-source` (when the kit is not at `.product-kit`); update
them together on every kit bump.

`review-integration-branches` optionally lists additional remote-tracking integration branches,
comma-separated (for example `origin/develop, origin/release`). The review check accepts only
`origin/HEAD` and this list, read from its committed config, never a change-local edit, as the
base it reads its policy from; without `origin/HEAD` it fails.

`review-t0-paths` lists the paths a T0 (docs-only) change may touch, comma-separated literal
paths, a trailing `/` naming a directory (for example `docs/, README.md`); no patterns. It is
read from the trusted integration branch. A T0 change passes only when every path it changes,
outside its record and closing metadata, is a regular non-executable file under one of them,
and never `.prdkit/`, another change record or the L0/L1 spec. Unset, T0 covers only the record
and closing metadata; other shared-log edits are not exempt.

The review gate also requires these trusted `## Configuration` entries (merge the real numeric
identities first; these placeholders are not usable policy):

```text
- review-host: github
- review-publishers: bot:<numeric account id>
- review-owner: user:<numeric owner id>
```

`review-publishers` may list comma-separated `bot:` and `user:` numeric ids; `review-owner`
lists personal `user:` ids. Use `gh api users/<login> --jq .id` or
`gh api 'users/<slug>[bot]' --jq .id`. Do not list the shared `github-actions[bot]` identity:
a branch can add workflows using it. The native gate rejects `review-publisher-isolation`.
An ID listed in both keys counts only as owner. The PR author is always ineligible.
Reviewers submit normal reviews under their own accounts.

Optionally use `review-lenses` in `## Configuration` to assign accounts to the existing
reviewer identities in trusted `.prdkit/memory/agents.md`. Use each lowercase name
once; makers, unregistered names and ambiguous registrations cannot be mapped. For
example, if `code` and `design` are registered reviewers:

```text
- review-lenses: code=bot:1001, design=bot:1001|user:1004
```

Either account can cover `design`; one approval from `1001` covers both names.
Use separate IDs if separate Apps perform the reviews. You may omit `review-publishers`
with lenses; if present it must list exactly the same set of typed accounts as the
mapping. T3 requires a `design` mapping and a distinct owner approval. Without lenses
the publisher list and tier minimums work as before.

Keep one level-two heading named Configuration, and never repeat that heading
name at any level, even in an example. Write policy entries exactly as shown;
the section ends at the next ATX `#` or `##` heading, or the end of the file.
Underlined headings are refused anywhere outside fenced examples and HTML comments.
Leave a blank line before a `---` thematic break. Put comments and fenced examples
after the section, never above or inside it.
