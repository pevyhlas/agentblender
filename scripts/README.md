# Scripts

## Install, uninstall and the release bundle (008 FR-014)

- `install-local.sh [--tool-path DIR]` packs `src/Abl.Cli` (Release) into
  `artifacts/packages/` and installs it as the .NET tool `agentblender` (global by default),
  replacing an earlier install of the same version.
- `uninstall-local.sh [--tool-path DIR] [--force]` removes it; it refuses while a daemon
  runs or answers. The config file, store and renders are kept.
- `release-bundle.sh [--out DIR] [--allow-dirty]` needs the reference SDK (the
  `global.json` version) and a clean tree. It packs `agentblender.<version>.nupkg`,
  installs it into a temporary `--tool-path`, runs `abl --version --json` and
  `abl doctor --quick --json` in an empty environment with a temporary home, a free
  `daemon.port` and a temporary `paths.runtime`, so it never touches 7811 or a live
  daemon (`worker.files` must pass against the packaged manifest, from the packaged
  `worker/`), then writes `SHA256SUMS` and `provenance.json` into
  `artifacts/release-bundle/`. CI runs it on every change. It never tags, uploads or
  publishes.

All three run with the macOS system bash (3.2) and on Linux; `release-bundle.sh` also needs
`git` and `jq`.

## Optional Claude telemetry profile

This is an opt-in convenience for maintainers who run their own local OTLP collector on
`127.0.0.1:4318`. It is not part of the project infrastructure, and the repository's
`.claude/settings.json` does not enable telemetry. The profile lives in
`scripts/claude-telemetry.env.json`.

To explicitly launch Claude Code with this cumulative, metrics-only profile:

```sh
bash scripts/claude-with-telemetry.sh
```

The Bash 4+/jq launcher reads the profile environment and passes it to `claude` without eval;
remaining arguments are forwarded exactly. It targets a loopback OTLP collector and disables
logs, traces and high-cardinality session/account attributes. It accepts only the ten declared
telemetry keys and clears inherited `OTEL_*`, Claude OTel content and beta-tracing variables
before applying that profile. Other Claude/model authentication settings remain outside this
telemetry profile; managed administrator settings still have Claude's documented precedence.
Alternatively put the same `env` block in your untracked `.claude/settings.local.json`, in
user/managed settings or in your launch environment (see
[CONTRIBUTING.md](../CONTRIBUTING.md#optional-claude-telemetry)). Restart Claude Code after
changing startup configuration.

`bash scripts/test-claude-with-telemetry.sh` verifies the effective launch environment and
failure behavior with a fake CLI, without model calls or metric export, and checks that the
tracked `.claude/settings.json` leaves telemetry off. It does not prove backend delivery: after
a real session starts, check the backend for `claude_code.session.count` and cumulative
counters. Existing processes retain their startup configuration. Reference:
https://code.claude.com/docs/en/monitoring-usage.

## CI policy

`check-ci-policy.sh [GITHUB_DIR]` checks `.github` against C-09: every job on a pinned
GitHub-hosted image, every `uses:` pinned by a full commit SHA with a `# vX` version comment,
top-level read-only permissions, no secrets and no `pull_request_target`.
`test-check-ci-policy.sh` mutates a copy of `ci.yml` one breach at a time and asserts each
fails. CI runs both in the validate job; both run with the macOS system bash (3.2).
