# AgentBlender — security

- **Trust boundaries**: loopback is the daemon's boundary (C-03): it binds IPv4 loopback only,
  and a `Host`/`Origin` guard before routing rejects anything but loopback names and its own
  origin, closing DNS rebinding from a local browser. The desktop launcher is the only
  non-loopback listener: one specific private address, a constant-time token check before any
  Blender starts. The worker process holds the GPL code (C-02). The project repository receives
  only bundles and deliveries.
- **Identity and access**: none beyond the boundary; any local client of the daemon is trusted,
  and `scene_exec` runs arbitrary Python in the worker by design. `render_final`'s
  caller-supplied `out` path is not confined, which adds nothing a caller with `scene_exec`
  lacks; endpoint authentication and that confinement land together (when: an
  [inbox](../inbox.md) question).
- **Local files**: the config file is refused unless it and its directory are the user's and
  not writable by others; the token file must be private or the desktop tier stays off; the
  runtime directory's final component must be a private, user-owned directory; workers start
  with an allowlisted environment (no `PYTHON*`); delivery paths refuse symlinks except system
  aliases. Detail: [P1 operability](../parts/p1-host/operability.md).
- **Encryption**: none in transit on loopback; the desktop link relies on the private network
  and the token. Nothing is encrypted at rest.
- **Personal data**: none collected; telemetry is opt-in and carries no content or paths (C-05).
- **Reporting**: `SECURITY.md` (GitHub private vulnerability reporting or the project contact).
