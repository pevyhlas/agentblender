# Deploying the daemon as a user service

`abl daemon install-service` writes the service for the `abl` you run it with; there is no
unit file to copy by hand:

- Linux: a systemd user unit, `~/.config/systemd/user/abl-daemon.service`;
- macOS: a launchd agent, `dev.agentblender.daemon` (its plist stays in the state dir and
  `abl daemon start` loads it; `--enable-at-login` puts it in `~/Library/LaunchAgents`).

```bash
abl setup                                    # pin Blender and the paths in the config file first
abl daemon install-service --dry-run         # print the unit or plist; changes nothing
abl daemon install-service [--enable-at-login]
abl daemon start                             # starts it through the service manager
abl doctor --check service.state             # the installed unit runs this abl and a usable .NET runtime
abl daemon uninstall-service                 # refused while it runs; stop it first
```

The unit runs this `abl` as `daemon run --log-file <the store's log>` through
`/usr/bin/env -i`, with only `HOME`, `USER`, `LOGNAME`, `SHELL`, `PATH`, `TMPDIR`,
`XDG_RUNTIME_DIR` and `DOTNET_ROOT`. Its settings therefore come from the config file alone:
`ABL_*` and `OTEL_*` variables, `Environment=` lines and systemd drop-ins never reach the
daemon. `install-service` warns about each such variable in your shell and about a
`blender.path` the file does not pin. Re-run it after moving `abl` or the .NET runtime.

The daemon binds `127.0.0.1` only and guards `Host` and `Origin` on every route; do not put
a proxy in front of it. Blender workers are its children, so stopping the service stops
them.
