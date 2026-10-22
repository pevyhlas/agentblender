# Security policy

AgentBlender is experimental and has one maintainer. Security reports are welcome and are
handled on a best-effort basis.

## Supported versions

Only the latest release and `main` receive security fixes. A fix lands on `main` and ships in
the next release; earlier versions are not patched.

## Reporting a vulnerability

Report vulnerabilities privately. Do not open a public issue, discussion or pull request for
one.

- **Preferred:** GitHub's private vulnerability reporting. Open the repository's **Security**
  tab and choose **Report a vulnerability**
  (<https://github.com/pevyhlas/agentblender/security/advisories/new>).
- **Or email** ab@petrvyhlas.com.

Include the affected version or commit, what an attacker could do, how to reproduce it, and a
suggested fix if you have one. Share only what the report needs: no real secrets, tokens,
host names, private-network addresses or private scene data.

## What to expect

- An acknowledgement within 7 days.
- An assessment, and for a confirmed vulnerability a fix on `main` and in a new release as soon
  as the maintainer can make one. The advisory is published with or after the fix, and credits
  you unless you prefer otherwise.

This is a solo maintainer's best effort, not a guaranteed service level. If you have heard
nothing after 7 days, send a reminder through the other channel.

## Trust model

Read this before deploying AgentBlender:

- **The daemon is for trusted local agents only.** `scene_exec` runs arbitrary Python
  inside Blender by design. Anyone who can reach the daemon can run code as the daemon's
  user. There is no authentication on the daemon endpoints yet; the network boundary is the
  security boundary.
- **Loopback only.** The daemon binds `127.0.0.1` and refuses to start on any other address.
  It rejects requests whose `Host` is not a loopback name (421) and requests carrying a
  foreign `Origin` (403), which blocks DNS-rebinding attacks from web pages in a local
  browser. Never expose the daemon port publicly or through a reverse proxy.
- **Desktop launcher (optional).** The remote GPU launcher binds one specific
  private-network address (wildcard binds are refused) and requires a shared bearer token
  before it starts a worker. Keep the token secret, and restrict the port with network
  ACLs so only the daemon host can reach it.
- **Telemetry is off by default.** When enabled, metrics carry only bounded labels, and no
  prompts, scripts, scene content or images are exported.
- **Known limitation:** `render_final`'s caller-supplied output path is not confined to
  the renders directory. This is harmless inside the current trust boundary (the same
  caller can already run `scene_exec`), but it will be confined before any authentication
  model is added.

## Scope

In scope:

- The daemon and its surfaces: the MCP server (`/mcp`), the REST API (`/api`), the health
  endpoint, the bind rules and the Host/Origin guard.
- The `abl` CLI and the `agentblender` tool package, including `abl setup`, the configuration
  file it writes, the user services and the `abl mcp config` output.
- The worker protocol and the scripts under `worker/`, the checkpoint store, trajectory
  bundles and replay, and still delivery into a consumer directory (path and symlink
  handling).
- The optional desktop launcher, its token check and its transfers.
- Repository scripts (`scripts/`) and this repository's GitHub Actions workflows
  (`.github/workflows/`).

Out of scope:

- Anything that needs an attacker who can already reach the daemon locally: that access is
  equivalent to code execution by design.
- Vulnerabilities in Blender itself (report those to the Blender project), or in agent
  harnesses, models, .NET, GitHub or other third-party tools (report those to their vendors).
  If AgentBlender makes one exploitable, that part is in scope.
- A deployment that exposes the daemon beyond loopback, or the launcher without the network
  restriction above, against this policy.
