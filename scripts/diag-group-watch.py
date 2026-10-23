"""TEMPORARY CI diagnostic (removed before merge): watches the runner's process group while
the unit tests run in it, logs any stop or catchable signal, and resumes a stopped group."""
import os, signal, subprocess, sys, time

mode, log_path = sys.argv[1], sys.argv[2]
log = open(log_path, "a", buffering=1)


def note(msg):
    log.write(f"{time.strftime('%H:%M:%S')} [{mode}] {msg}\n")


def ps(*args):
    return subprocess.run(["ps", *args], capture_output=True, text=True).stdout


if mode == "canary":
    # Stays in the runner's group and logs every catchable signal sent to it.
    def handler(sig, frame):
        note(f"canary pid {os.getpid()} got {signal.Signals(sig).name}")
    for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP, signal.SIGQUIT, signal.SIGUSR1,
              signal.SIGUSR2, signal.SIGCONT, signal.SIGTSTP, signal.SIGTTIN, signal.SIGTTOU):
        signal.signal(s, handler)
    note(f"canary pid {os.getpid()} pgid {os.getpgid(0)} sid {os.getsid(0)}")
    while True:
        time.sleep(3600)

# watcher: its own session, so a signal to the runner's group never reaches it
os.setsid()
worker, group, step = int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
for pid in (1, group, worker, step):
    try:
        note(f"pid {pid} pgid {os.getpgid(pid)} sid {os.getsid(pid)}")
    except OSError as e:
        note(f"pid {pid}: {e}")
seen = set()
runner = {worker, step, group}
while True:
    try:
        os.kill(worker, 0)
    except ProcessLookupError:
        note("Runner.Worker is gone")
        break
    rows = [line.split(None, 3) for line in ps("-o", "pid=,ppid=,stat=,command=", "-g", str(group)).splitlines()]
    for pid, ppid, stat, command in (r for r in rows if len(r) == 4):
        pid = int(pid)
        if not stat.startswith("T"):
            continue
        critical = pid in runner or "Runner." in command or "dotnet test" in command or "testhost" in command
        if pid not in seen or critical:
            note(f"STOPPED pid {pid} ppid {ppid} {'RUNNER-SIDE ' if critical else ''}{command[:160]}")
            seen.add(pid)
        if critical:
            note("group table:\n" + ps("-o", "pid=,ppid=,pgid=,stat=,command=", "-g", str(group)))
            for target in [int(r[0]) for r in rows if len(r) == 4 and ("Runner." in r[3] or int(r[0]) in runner
                                                                     or "dotnet" in r[3] or "testhost" in r[3])]:
                try:
                    os.kill(target, signal.SIGCONT)
                except OSError:
                    pass
            time.sleep(1)
    time.sleep(0.05)
