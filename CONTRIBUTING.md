# Contributing to AgentBlender

AgentBlender is experimental and has one maintainer. Issues and draft pull requests are
welcome; the maintainer reviews them as time allows and is the only person who merges. The
public surface and the storage, bundle and delivery formats can still change in any 0.x
release.

## Before you start

- **Open an issue first** for anything beyond a small fix such as a typo, a broken link or an
  obvious one-line bug. Use the bug report or proposal form, and agree on the problem and the
  smallest useful change before you build it.
- **Never report a vulnerability in an issue or pull request.** Report it privately as
  [SECURITY.md](SECURITY.md) describes.
- Everyone taking part follows the [code of conduct](CODE_OF_CONDUCT.md).
- **Scope.** 2D authoring (vector, pixel art, icon and platform kits) belongs to the companion
  [AgentGraphicDesign](https://github.com/pevyhlas/agentgraphicdesign) project, not here.

## Pull requests

- **Open the pull request as a draft** and link its issue. Mark it ready for review when CI
  passes and you consider it done. Only the maintainer merges, and may close a pull request that
  does not fit the project's direction, with a reason.
- **One concern per pull request.** Don't stack unrelated work onto an open branch. Add or
  update tests with every behavior change.
- Note user-visible changes in the unreleased section at the top of
  [CHANGELOG.md](CHANGELOG.md). Leave versions alone (`VersionPrefix` in
  `Directory.Build.props`): the maintainer decides when a change is released, and as which
  version.
- A behavior change also updates the spec lines it affects. The spec lives under `specs/`
  (`product.md` and `product/`, `architecture.md` and `architecture/`, `parts/`); the project's
  rules are in `.prdkit/memory/constitution.md` and [CLAUDE.md](CLAUDE.md). The maintainer can
  help with the spec side of a pull request.
- No secrets, tokens, host names, private-network addresses, private paths or private scene
  data in code, docs, examples or tests. Targets and endpoints are configuration.
- [CLAUDE.md](CLAUDE.md) and [AGENTS.md](AGENTS.md) carry the same guidance for coding agents.
  You are responsible for an agent-written contribution as for any other.

## Build and validate

You need the .NET SDK named in `global.json` (10.0.112; a later 10.0 feature band also builds)
and Python 3 for the worker checks. The integration lane also needs Blender 5.2.1 LTS: set
`ABL_BLENDER` to the binary or put `blender` on `PATH`.

```bash
dotnet restore agentblender.slnx --locked-mode
dotnet build agentblender.slnx --no-restore -warnaserror
# unit lane (no Blender needed); redirect test output to a file, never pipe it
MSBUILDDISABLENODEREUSE=1 dotnet test agentblender.slnx --no-build \
  --filter "Category!=BlenderIntegration&Category!=MetalQualification&Category!=OptixQualification" > testrun.log 2>&1
# integration lane (real Blender)
MSBUILDDISABLENODEREUSE=1 dotnet test agentblender.slnx --no-build \
  --filter "Category=BlenderIntegration&Category!=OptixQualification" > itestrun.log 2>&1
# worker and repository checks
python3 -m py_compile worker/*.py
python3 tests/test_blob_transfers.py
python3 tests/test_cycles_devices.py
bash scripts/test-claude-with-telemetry.sh
bash scripts/check-ci-policy.sh && bash scripts/test-check-ci-policy.sh
git diff --check 4b825dc642cb6eb9a060e54bf8d69288fbee4904 HEAD
```

On macOS, point `TMPDIR` at a directory without a symlinked parent (for example
`/private/tmp/ab-tmp/`); the default `/var/folders/...` temp path trips the delivery symlink
guard. `MetalQualification` tests run only by hand on an Apple silicon Mac, and
`OptixQualification` tests (the real-Blender cases that need a GPU backend) only by hand on a
Linux host with an NVIDIA OptiX GPU: `--filter "Category=OptixQualification"`. Hosted runners
have no GPU, so CI runs neither.

**CI.** [`ci.yml`](.github/workflows/ci.yml) runs on fresh GitHub-hosted runners for every
pull request and every push to main: a validate job (whitespace, the CI policy, shellcheck,
the GPL boundary, no Blender binaries, the worker checks and the Product-Kit gates), build and
unit tests on exactly the reference SDK with the vulnerable-package check and a package and
install smoke test, a real-Blender integration lane on the official Blender 5.2.1 Linux build
(SHA-256 checked), and an advisory macOS arm64 unit lane. The real-Blender lane is advisory
too until its first green run on a GitHub-hosted runner (it renders EEVEE on Mesa's software
drivers, which no hosted run has proved yet); if it fails on your pull request, say so in the
pull request and run the integration lane locally. Pull-request runs get a read-only token and
no secrets, and a run from a first-time contributor waits for the maintainer's approval. Every
required job must pass before a merge.

`scripts/check-ci-policy.sh` keeps the workflows on pinned GitHub-hosted images, every action
pinned by full commit SHA with a version comment, read-only permissions, no secrets and no
`pull_request_target`. Dependabot proposes GitHub Actions updates weekly; NuGet updates are made
by hand, because restore runs with `--locked-mode` and the lock files must change with them.

## Optional Claude telemetry

The tracked `.claude/settings.json` is empty: opening the repository in Claude Code turns on
no telemetry. Maintainers who run their own OTLP collector on `127.0.0.1:4318` can opt in to a
cumulative, metrics-only profile (no logs, no traces, no session or account attributes), either
per launch with `bash scripts/claude-with-telemetry.sh`, which reads
`scripts/claude-telemetry.env.json`, or for every session in the untracked
`.claude/settings.local.json`:

```json
{
  "env": {
    "CLAUDE_CODE_ENABLE_TELEMETRY": "1",
    "OTEL_METRICS_EXPORTER": "otlp",
    "OTEL_EXPORTER_OTLP_PROTOCOL": "http/protobuf",
    "OTEL_EXPORTER_OTLP_ENDPOINT": "http://127.0.0.1:4318",
    "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE": "cumulative",
    "OTEL_RESOURCE_ATTRIBUTES": "project=agentblender",
    "OTEL_METRICS_INCLUDE_SESSION_ID": "false",
    "OTEL_METRICS_INCLUDE_ACCOUNT_UUID": "false",
    "OTEL_LOGS_EXPORTER": "none",
    "OTEL_TRACES_EXPORTER": "none"
  }
}
```

Some Claude Code versions ignore OTel exporter variables in project settings; the launcher
always applies them. [scripts/README.md](scripts/README.md) has the details, and
`bash scripts/test-claude-with-telemetry.sh` checks the opted-in launch environment and that the
tracked settings stay telemetry-free.

## Ground rules

- **License.** AgentBlender is released under the [MIT License](LICENSE). By contributing,
  you agree that your contribution is licensed under the same license (inbound = outbound).
  Copy code verbatim only from MIT-licensed sources, and record it in
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); Apache-2.0 and copyleft sources are
  ideas-only.
- **Dependencies.** A new NuGet package must carry an MIT-compatible permissive license
  (no GPL, LGPL, AGPL or MPL), be added through `Directory.Packages.props` with its lock files
  regenerated by the reference SDK, and leave
  `dotnet list agentblender.slnx package --vulnerable --include-transitive` clean. Name the
  package, its license and why it is needed in the pull request; the maintainer decides
  license questions the text cannot settle.
- **GPL boundary.** Only `worker/` may import `bpy` or other Blender modules; CI enforces it.
  The C# host never links Blender. Never commit Blender binaries or archives.
- **Assets.** Procedural first; external assets must be CC0 with recorded provenance.
- **Network boundary.** Do not widen it: the daemon binds loopback only, and the desktop
  launcher binds one specific private-network address with a token.
