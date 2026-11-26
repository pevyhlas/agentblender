# Optional external scanners

The script runs on the Python standard library and git alone. Two scanners can add their results
when they are installed; both are off by default, and a missing tool is reported, not an error.

| flag | tool | how it runs | what is kept |
|---|---|---|---|
| `--external gitleaks` | gitleaks | `detect --redact`, over the same range as the script (`--log-opts <merge-base>..<head>`); in the other modes `--no-git` over the working tree under `--root` | rule id, file, line, commit |
| `--external trufflehog` | trufflehog | `git file://<root> --since-commit <merge-base> --no-verification` in a range (it may also read commits since the merge base on other branches); in the other modes `filesystem` over the working tree under `--root` | detector name, file, line |

Outside a range, both tools read the working tree, not the `--head` tree or the paths you passed, so
their results can cover other files or another revision than the script's facts.

Neither tool's secret, match or raw fields are kept: the script reads their JSON and drops every
field except the ones above, so `--json`, `--out` and `--json-out` stay value-free.

Results go under `external` in `--json`; the summary shows a count per tool. They are facts like
the script's own: judge them the same way, and say in the report which tool produced one.

Use one when the repository already runs it, or when the owner asks for it; installing a scanner
is outside a review. trufflehog runs with `--no-verification`, so it never sends a value it found
to a provider to check it.
