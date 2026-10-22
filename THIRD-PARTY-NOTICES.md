# Third-party notices

AgentBlender's own code is MIT-licensed (see [LICENSE](LICENSE)). This file lists the
third-party software it uses or contains. Each component keeps its own license.

## Blender (external program, not included)

[Blender](https://www.blender.org/) is licensed under the GNU General Public License
(see <https://www.blender.org/about/license/>). **AgentBlender does not include, bundle
or redistribute Blender** in source or binary form. Users install Blender themselves
(AgentBlender targets Blender 5.2.1 LTS) and point `ABL_BLENDER` at it.

AgentBlender's C# host never links or imports Blender code. It starts Blender as a
separate process (`blender --background ... --python worker/worker.py`) and talks to it
over a socket. Only the scripts under `worker/` are loaded by Blender and import `bpy`;
CI enforces that no other code does. The `worker/` scripts are MIT-licensed, which is
GPL-compatible; anyone who redistributes them *together with* Blender takes on Blender's
GPL obligations for that combination.

CI downloads the official Blender build from download.blender.org at test time (pinned
version and SHA-256). That download is never committed to this repository or published as
part of any AgentBlender artifact.

"Blender" is a trademark of the Blender Foundation. AgentBlender is a community project
and is not affiliated with or endorsed by the Blender Foundation.

## NuGet dependencies (restored at build time, not vendored)

| Package | License | Copyright | In the `agentblender` package |
|---------|---------|-----------|-------------------------------|
| ModelContextProtocol, ModelContextProtocol.AspNetCore, ModelContextProtocol.Core | Apache-2.0 | Model Context Protocol a Series of LF Projects, LLC | yes |
| OpenTelemetry, OpenTelemetry.Api, OpenTelemetry.Api.ProviderBuilderExtensions, OpenTelemetry.Extensions.Hosting, OpenTelemetry.Exporter.OpenTelemetryProtocol | Apache-2.0 | OpenTelemetry Authors | yes |
| Microsoft.Extensions.AI.Abstractions | MIT | .NET Foundation and Contributors | yes |
| Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions | MIT | .NET Foundation and Contributors | no (the shared framework provides them) |
| Test only: xunit, xunit.runner.visualstudio | Apache-2.0 | .NET Foundation and Contributors | no |
| Test only: Microsoft.NET.Test.Sdk | MIT | Microsoft Corporation | no |

Exact versions are pinned in `Directory.Packages.props` and the `packages.lock.json` files.
These packages are used as dependencies only; no source code from them is copied into this
repository.

The `agentblender` tool package redistributes the assemblies marked "yes" unmodified, and
carries their notices with it: this file and [LICENSE](LICENSE) at the package root, and under
[`licenses/`](licenses/):

- [`Apache-2.0.txt`](licenses/Apache-2.0.txt): the Apache License 2.0, which applies to the
  bundled ModelContextProtocol and OpenTelemetry assemblies. Neither project ships a NOTICE
  file in its package.
- [`OpenTelemetry-THIRD-PARTY-NOTICES.txt`](licenses/OpenTelemetry-THIRD-PARTY-NOTICES.txt):
  OpenTelemetry .NET's own third-party notices, verbatim from its packages; they apply to the
  bundled OpenTelemetry assemblies.
- [`MIT-dotnet.txt`](licenses/MIT-dotnet.txt): the MIT license of the .NET Foundation, which
  applies to the bundled Microsoft.Extensions.AI.Abstractions assembly.

`scripts/release-bundle.sh` refuses a package that lacks any of these files. Anyone who
redistributes the package or its assemblies keeps them alongside.

## Vendored Product-Kit workflow files

`.prdkit/`, `.claude/commands/prdkit*.md` and `.opencode/commands/prdkit*.md` are a
vendored copy of the Product-Kit workflow (MIT). Its license and attribution are in
[.opencode/LICENSE.product-kit](.opencode/LICENSE.product-kit). Portions of Product-Kit are
derived from [Spec Kit](https://github.com/github/spec-kit), Copyright (c) GitHub, Inc.,
used under the MIT License.

## Verbatim code borrowed from MIT sources

None so far. Any future verbatim reuse from an MIT-licensed source is listed here with its
copyright notice (C-01, constitution Principle 9).

## External assets

Scenes are procedural; no third-party assets are committed. Any future external asset must
be CC0 with its provenance recorded.
