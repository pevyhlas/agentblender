using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Lifecycle;

namespace Abl.Cli.Host;

/// <summary>What <c>start</c>, <c>stop</c>, <c>status</c> and doctor know about the store's daemon.</summary>
public enum Liveness
{
    /// <summary>Nobody holds the store lock.</summary>
    NotRunning,

    /// <summary>The lock is held and <c>/healthz</c> confirms the record's pid.</summary>
    Running,

    /// <summary>The lock is held and the record names a live lock holder, but <c>/healthz</c> does not answer for it.</summary>
    Unresponsive,

    /// <summary>The lock is held, but the record is missing, unusable or names no live holder (a daemon still starting).</summary>
    Unconfirmed,
}

/// <summary><c>/healthz</c> as the CLI reads it.</summary>
public sealed record Health(int Pid, string? Version, int ReadyWorkers, int Sessions, JsonObject? LastWorkerError);

/// <summary>The store, its runtime dir, the daemon's liveness, its record and (when running) its health.</summary>
public sealed record Located(string Store, string RuntimeDir, Liveness Liveness, DaemonState? State, Health? Health);

/// <summary>What the lifecycle core runs against: the configuration inputs, the writers and the host seams.</summary>
/// <param name="Inputs">The configuration inputs (the process's, or a test's).</param>
/// <param name="Out">Standard output.</param>
/// <param name="Err">Standard error.</param>
/// <param name="Processes">Signals, liveness, start times and the process table.</param>
/// <param name="Services">The user service manager.</param>
public sealed record LifecycleHost(ConfigInputs Inputs, TextWriter Out, TextWriter Err, IProcessControl Processes, IServiceManager Services);

/// <summary>
/// The daemon lifecycle core (008 FR-007, FR-008, plan B3; T035 split it from the argument
/// parsing in <see cref="DaemonVerbs"/>, 2a eng-arch LOW-1): <c>start</c>, <c>stop</c> and
/// <c>status</c> over <see cref="LifecycleHost"/>, so unit tests drive them with a fake
/// process table and a fake service manager. It owns the liveness rule (<see cref="LocateAsync"/>:
/// the store-root gate, the lock, the record, the start-time check and <c>/healthz</c>), which
/// <c>start</c> polls and <c>stop</c>, <c>status</c> and later doctor read. Exit codes: 0 ok,
/// 1 failed, 3 not running.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class DaemonLifecycle(LifecycleHost host)
{
    /// <summary>The default readiness deadline of <c>start</c> (plan B3).</summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long <c>stop</c> waits for a graceful exit.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long <c>stop --force</c> lets a responsive daemon without its own group dispose its workers before a SIGKILL.</summary>
    public static readonly TimeSpan ForceGrace = TimeSpan.FromSeconds(5);

    /// <summary>The longest failure tail <c>start</c> prints (AC-18).</summary>
    public const int FailureTailLines = 20;

    /// <summary>The store lock retry of <c>daemon run</c> (L-d).</summary>
    internal static readonly TimeSpan LockRetry = TimeSpan.FromSeconds(2);

    /// <summary>How long <c>stop</c> waits for SIGKILLed workers or a group to be reaped before it reports them.</summary>
    private static readonly TimeSpan SweepWait = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How often <c>start</c> asks the service manager about a job that has not taken the lock yet.</summary>
    private static readonly TimeSpan ServiceCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>How much later than the record's <c>startedAt</c> a pid's start time may read (clock-tick rounding).</summary>
    private static readonly TimeSpan StartTimeSlack = TimeSpan.FromSeconds(2);

    private const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const string StopHint =
        "close them (abl session close <session>), or stop anyway with --force; a dropped session is recovered with abl checkpoint branch <lastCheckpoint>";

    private TextWriter Out => host.Out;

    private TextWriter Err => host.Err;

    private IProcessControl Processes => host.Processes;

    // ---- start -------------------------------------------------------------------------

    public async Task<int> StartAsync(bool json, TimeSpan timeout)
    {
        // A running daemon's lastWorkerError older than this start is context, not the verdict
        // (round-2 eng-arch MEDIUM-1). The CLI and the daemon read one host clock.
        var began = DateTimeOffset.UtcNow;

        // Preflight (plan B3 step 1): cheap checks, each failing in well under 2 s with its
        // check ID and remedy before anything is spawned (AC-18).
        var inputs = host.Inputs.WithBlenderSearch();
        AblConfig config;
        try
        {
            config = AblConfig.Resolve(inputs);
        }
        catch (ConfigException error)
        {
            return Fail(json, error);
        }

        var options = config.Options;
        var stateDir = DaemonPaths.StateDir(inputs.Home, inputs.Env);
        var logFile = DaemonPaths.LogFile(stateDir, options.StoreDir);
        var url = DaemonPaths.UrlFor(options.Port);
        var preflight = Preflight(config, inputs, stateDir);
        if (preflight.Failure is { } failure)
        {
            return Fail(json, failure);
        }

        if (preflight.Held)
        {
            // AC-19: the lock holder is this instance once /healthz confirms the record's pid
            // with a warm worker; liveness is never read from the file alone (Principle III).
            return await ReportExistingAsync(json, options, timeout, Stopwatch.StartNew(), began, service: null);
        }

        // An installed service that serves this store and port is started through its manager
        // (plan B3 "Service handoff", FR-008); the store lock, not this handoff, keeps the
        // daemon single.
        var service = host.Services.Query();
        var handoff = Handoff(service, options, logFile);
        if (handoff.Options is not null || handoff.Refusal is not null)
        {
            // The service reads the config file only (T035 review round 1, eng-arch LOW-2). On a
            // refusal nothing is started, so the warning says what the service would not see
            // (round-4 eng-code nit).
            var ignored = host.Inputs.Env.Where(v => v.Value.Length > 0 && (v.Key.StartsWith("ABL_", StringComparison.Ordinal) || v.Key.StartsWith("OTEL_", StringComparison.Ordinal)))
                .Select(v => v.Key).Order(StringComparer.Ordinal).ToList();
            if (ignored.Count > 0)
            {
                var consequence = handoff.Refusal is null
                    ? $"so the daemon it starts does not see {(ignored.Count == 1 ? "it" : "them")}"
                    : $"so its daemon would not see {(ignored.Count == 1 ? "it" : "them")} either";
                Err.WriteLine($"abl: warning: {string.Join(", ", ignored)} {(ignored.Count == 1 ? "is" : "are")} set in this shell, but the {service.Kind} service " +
                    $"reads the config file only (`abl config path`), {consequence}");
            }

            if (handoff.Refusal is { } refusal)
            {
                return Fail(json, refusal);
            }

            return await StartServiceAsync(json, handoff.Options!, timeout, began, service.LogFile ?? logFile);
        }

        if (handoff.Elsewhere is { } elsewhere)
        {
            // Not this store's service: say why it is not used.
            Err.WriteLine($"abl: warning: the installed {service.Kind} service ({service.UnitFile}) {elsewhere}; this start runs the daemon itself");
        }

        // Plan B3 step 2: re-run this CLI (N2) with all three streams redirected, so the
        // child never inherits the caller's pipes, and only the allowlisted environment
        // (plan A6, plus the worker.envPassthrough names so they still reach workers).
        var (program, prefix) = SelfCommand.Current();
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = stateDir,
        };
        foreach (var arg in prefix)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var arg in new[] { "daemon", "run", "--detached", "--log-file", logFile })
        {
            start.ArgumentList.Add(arg);
        }

        ChildEnvironment.Restrict(
            start.Environment, name => ChildEnvironment.AllowsForDetachedDaemon(name, options.WorkerEnvPassthrough));

        // The child's cwd is the state dir: a relative path variable is resolved here, against
        // this cwd, so the child locks the same store this preflight checked (round-2
        // eng-security LOW-2), by the resolver's own rule (round-3 eng-arch LOW-3).
        foreach (var (name, value) in AblConfig.AbsolutePathVariables(start.Environment, Environment.CurrentDirectory))
        {
            start.Environment[name] = value;
        }

        using var child = Process.Start(start) ?? throw new InvalidOperationException("the daemon process did not start");

        // Step 3: close stdin, drain stdout/stderr (anything written before the redirect).
        child.StandardInput.Close();
        var early = new EarlyOutput();
        var drains = Task.WhenAll(early.DrainAsync(child.StandardOutput), early.DrainAsync(child.StandardError));

        // Step 4: poll /healthz until ready, the child exits, a worker fails, or the deadline.
        using var http = HealthClient();
        var deadline = Stopwatch.StartNew();
        ConfigException? problem = null;
        while (problem is null)
        {
            if (child.HasExited)
            {
                await Task.WhenAny(drains, Task.Delay(TimeSpan.FromSeconds(1)));

                // A concurrent start won the store lock (eng-code L-4): report that instance.
                if (IsHeldOrUnknown(options.StoreDir))
                {
                    return await ReportExistingAsync(json, options, timeout, deadline, began, service: null);
                }

                problem = new ConfigException(
                    "daemon.reachable", $"the daemon exited with code {child.ExitCode} during startup", "see the output and log tail below");
                break;
            }

            if (await ProbeAsync(http, url, ProbeCap(timeout, deadline)) is { } health && health.Pid == child.Id)
            {
                if (health.ReadyWorkers >= 1)
                {
                    var state = DaemonState.Read(options.StoreDir)
                        ?? new DaemonState(child.Id, options.Port, url, AblVersion.Current, DateTimeOffset.UtcNow, config.EffectiveDigest, logFile, child.Id);
                    return Report(json, "started", state, alreadyRunning: false, service: null);
                }

                if (health.LastWorkerError is { } workerError)
                {
                    problem = WorkerFailure(workerError, "the first worker failed", "fix the cause and run `abl daemon start` again");
                    break;
                }
            }

            if (deadline.Elapsed >= timeout)
            {
                problem = new ConfigException(
                    "daemon.reachable",
                    $"the daemon was not ready within {timeout.TotalSeconds:0} s (no warm worker yet)",
                    "raise --timeout, or see the log tail below");
                break;
            }

            await Task.Delay(PollInterval);
        }

        // A daemon that failed to start is not left behind (no orphan, and a retry is not
        // refused as "already running").
        await StopChildAsync(child);
        return FailStart(json, problem, early.Lines(FailureTailLines), logFile);
    }

    /// <summary>The service path of <c>start</c>: the manager starts the daemon, then the readiness loop runs against the lock holder.</summary>
    private async Task<int> StartServiceAsync(bool json, AblOptions options, TimeSpan timeout, DateTimeOffset began, string logFile)
    {
        var services = host.Services;
        var outcome = services.Start();
        if (!outcome.Ok)
        {
            return Fail(json, new ConfigException(
                "service.state",
                $"the {services.Kind} service did not start: {outcome.Error}",
                "see `abl daemon logs`; `abl daemon uninstall-service` makes `abl daemon start` run the daemon itself"));
        }

        var (state, problem) = await AwaitExistingAsync(options, timeout, Stopwatch.StartNew(), began, waitForLock: true, watch: services);
        if (problem is null)
        {
            return Report(json, "started", state!, alreadyRunning: false, service: ServiceJson(services.Kind, outcome.Domain));
        }

        if (state is not null)
        {
            // A healthy daemon holds the store on another port (the service's configuration
            // could not be resolved ahead, or another start won the lock): it is never stopped
            // here (T035 review round 2, eng-code MEDIUM).
            return Fail(json, new ConfigException(
                "daemon.port",
                $"daemon pid {state.Pid} serves store {options.StoreDir} at port {state.Port}, but this configuration names port {options.Port}; it is left running",
                $"use daemon.port {state.Port} in this shell (unset ABL_PORT), or make daemon.port agree in the config file the {services.Kind} service reads, then `abl daemon stop` and `abl daemon start`"));
        }

        // Not left behind (the manager would keep restarting it). A daemon that failed before
        // its own redirect wrote to the manager's output, not the store log (AC-18: the cause,
        // not a bare timeout): show launchd's output file, or name the journal.
        var down = services.Query();
        _ = services.Stop();
        var output = down.OutputFile is { } file && File.Exists(file) ? DaemonLog.Tail(file, FailureTailLines) : [];
        var where = down.OutputFile is { } outputFile
            ? $"its output before the daemon's own log is in {outputFile}"
            : services.Kind == "systemd"
                ? $"its output before the daemon's own log is in `journalctl --user -u {ServiceUnits.SystemdName}`"
                : null;
        return FailStart(json, where is null ? problem : new ConfigException(problem.Check, problem.Message, $"{where}; {problem.Hint}"), output, logFile);
    }

    /// <summary>What <c>start</c> does with the installed service: hand off with the service's options, refuse, or run the daemon itself.</summary>
    /// <param name="Options">Hand off; the readiness wait runs with these (the options the service's daemon resolves).</param>
    /// <param name="Refusal">Refuse before the manager is asked: the service was installed for this store but now serves another store or port.</param>
    /// <param name="Elsewhere">Run the daemon itself; when a service is installed, why it is not this store's (for a warning).</param>
    internal sealed record ServiceHandoff(AblOptions? Options, ConfigException? Refusal, string? Elsewhere);

    /// <summary>
    /// The service handoff rule of <c>start</c> (plan B3 "Service handoff"): does the installed
    /// <paramref name="service"/> serve the store and port this shell resolves? When the
    /// unit's own <c>env -i</c> configuration resolves (<see cref="ServiceOptions"/>), that
    /// answers it: the same store (<see cref="DaemonPaths.SameStore"/>) and the same port hand
    /// off. Another store is refused with <c>service.state</c> when the unit logs to this
    /// store's log (it was installed for this store, and its configuration has moved since:
    /// <c>daemon run</c> does not tie its <c>--log-file</c> to its store), and is otherwise
    /// another store's service. This store on another port is refused with <c>daemon.port</c>.
    /// Only when the unit's configuration cannot be resolved does the log stand in for the
    /// store: a unit that logs to this store's log hands off with this shell's options (T035
    /// review rounds 2 and 3).
    /// </summary>
    internal ServiceHandoff Handoff(ServiceState service, AblOptions options, string logFile)
    {
        if (!service.Installed)
        {
            return new ServiceHandoff(null, null, null);
        }

        var installedHere = service.LogFile == logFile;
        if (ServiceOptions(service) is not { } served)
        {
            return installedHere
                ? new ServiceHandoff(options, null, null)
                : new ServiceHandoff(null, null,
                    $"logs to {service.LogFile ?? "no --log-file"}, not this store's {logFile}, so it serves another store or state dir");
        }

        if (!DaemonPaths.SameStore(served.StoreDir, options.StoreDir))
        {
            return installedHere
                ? new ServiceHandoff(null, new ConfigException(
                    "service.state",
                    $"the installed {service.Kind} service ({service.UnitFile}) was installed for store {options.StoreDir} (it logs to {logFile}), " +
                        $"but the config file it reads now names store {served.StoreDir}, so its daemon would serve that store, not this shell's",
                    $"make them agree: unset ABL_STORE (or ABL_CONFIG, XDG_CONFIG_HOME) in this shell to use store {served.StoreDir}, " +
                        $"or set paths.store {options.StoreDir} in the config file the service reads; `abl daemon install-service` rewrites the unit " +
                        $"for store {served.StoreDir}, and `abl daemon uninstall-service` makes `abl daemon start` run the daemon itself"), null)
                : new ServiceHandoff(null, null, $"serves store {served.StoreDir} (from the config file it reads), not this shell's {options.StoreDir}");
        }

        if (served.Port != options.Port)
        {
            return new ServiceHandoff(null, new ConfigException(
                "daemon.port",
                $"the installed {service.Kind} service for store {options.StoreDir} runs its daemon on port {served.Port} " +
                    $"(from the config file it reads), but this shell's configuration names port {options.Port}",
                $"make them agree: unset ABL_PORT (or ABL_CONFIG, XDG_CONFIG_HOME) in this shell, or set daemon.port {options.Port} in the config file the service reads; " +
                    "`abl daemon uninstall-service` makes `abl daemon start` run the daemon itself"), null);
        }

        return new ServiceHandoff(served, null, null);
    }

    /// <summary>
    /// The options the installed <paramref name="service"/>'s daemon resolves: this host's
    /// inputs under the unit's exact <c>env -i</c> environment (its <c>HOME</c> anchors the
    /// config file), without flags or a Blender search. Null when the unit does not start
    /// through <c>env -i</c> or its configuration does not resolve (<see cref="Handoff"/> then
    /// falls back on the unit's log, the readiness wait reports the job, and a healthy daemon
    /// on another port is never stopped).
    /// </summary>
    private AblOptions? ServiceOptions(ServiceState service)
    {
        if (ServiceUnits.EnvironmentOf(service.ExecStart) is not { } env)
        {
            return null;
        }

        try
        {
            return AblConfig.Resolve(host.Inputs with
            {
                Env = env,
                Home = env.GetValueOrDefault("HOME") ?? host.Inputs.Home,
                Flags = new Dictionary<string, string>(),
                Blender = null,
            }).Options;
        }
        catch (ConfigException)
        {
            return null;
        }
    }

    private (ConfigException? Failure, bool Held) Preflight(AblConfig config, ConfigInputs inputs, string stateDir)
    {
        var options = config.Options;
        if (config.Blender is { Check: { } check } blender)
        {
            return (new ConfigException(check, blender.Problem ?? "Blender is not usable", blender.Hint), false);
        }

        try
        {
            RuntimePaths.Prepare(options.RuntimeDir, options.Platform, inputs.FileSystem);
        }
        catch (ConfigException error)
        {
            return (error, false);
        }

        if (PrepareDirs(stateDir, options.StoreDir) is { } dirProblem)
        {
            return (dirProblem, false);
        }

        bool held;
        try
        {
            held = StoreLock.IsHeld(options.StoreDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new ConfigException("paths.store", $"cannot open the store lock: {ex.Message}", "fix the store's permissions"), false);
        }

        if (held)
        {
            // A live lock holder recorded on another port refuses at once. Anything else (this
            // port, or a record a starting daemon has not rewritten yet) is waited on.
            var state = DaemonState.Read(options.StoreDir);
            return state is not null && state.Port != options.Port && IsUsable(state) && NamesLockHolder(state)
                ? (HeldBy(options.StoreDir, state, options.Port), false)
                : (null, true);
        }

        try
        {
            var probe = new TcpListener(IPAddress.Loopback, options.Port);
            probe.Start();
            probe.Stop();
        }
        catch (SocketException ex)
        {
            return (new ConfigException(
                "daemon.port",
                $"port {options.Port} on 127.0.0.1 is in use by another program ({ex.SocketErrorCode})",
                "stop that program, or set daemon.port (ABL_PORT)"), false);
        }

        return (null, false);
    }

    private async Task<int> ReportExistingAsync(
        bool json, AblOptions options, TimeSpan timeout, Stopwatch clock, DateTimeOffset began, JsonObject? service)
    {
        var (state, problem) = await AwaitExistingAsync(options, timeout, clock, began, waitForLock: false, watch: null);
        if (problem is not null)
        {
            return Fail(json, problem);
        }

        // A daemon the service already runs is reported with it (T035 review round 1, eng-code LOW-4).
        if (service is null && host.Services.Query() is { } running && running.Runs(state!.Pid))
        {
            service = ServiceJson(running.Kind, running.Domain);
        }

        return Report(json, "already running", state!, alreadyRunning: true, service);
    }

    /// <summary>
    /// The readiness loop of <c>start</c>, run against the daemon that holds the store lock
    /// (AC-19, eng-arch MEDIUM-2): ready once <c>/healthz</c> at the record's port confirms the
    /// record's pid with a warm worker. The record is re-read every poll, because a starting
    /// daemon rewrites it right after it takes the lock. Fails on a <c>lastWorkerError</c>
    /// recorded after this start <paramref name="began"/>, on another port, when the lock is
    /// released (or, with <paramref name="waitForLock"/>, never taken by the deadline), or at
    /// the deadline. An older error is sticky by design and says nothing about now: the pool is
    /// also empty for a moment after every session take, so it is only reported as the likely
    /// cause if the deadline passes (round-2 eng-arch MEDIUM-1, AC-19). While the lock is not
    /// taken yet, <paramref name="watch"/> (the service that was just started) is asked each
    /// second whether its job is down after a failure; seen twice in a row, that fails at once
    /// with <c>service.state</c> instead of waiting out the deadline (T035 review round 1,
    /// eng-code MEDIUM-1).
    /// </summary>
    private async Task<(DaemonState? State, ConfigException? Problem)> AwaitExistingAsync(
        AblOptions options, TimeSpan timeout, Stopwatch clock, DateTimeOffset began, bool waitForLock, IServiceManager? watch)
    {
        var store = options.StoreDir;
        using var http = HealthClient();
        DaemonState? state = null;
        var answered = false;
        var locked = false;
        JsonObject? olderError = null;
        var nextServiceCheck = TimeSpan.Zero;
        var failedChecks = 0;
        while (true)
        {
            if (!IsHeldOrUnknown(store))
            {
                if (!waitForLock || locked || clock.Elapsed >= timeout)
                {
                    return (null, new ConfigException(
                        "daemon.reachable",
                        locked || !waitForLock
                            ? "the daemon that held the store lock exited before it had a warm worker"
                            : $"the service started no daemon on this store within {timeout.TotalSeconds:0} s",
                        "run `abl daemon start` again, or see `abl daemon logs`"));
                }

                if (watch is not null && clock.Elapsed >= nextServiceCheck)
                {
                    nextServiceCheck = clock.Elapsed + ServiceCheckInterval;
                    var job = watch.Query();
                    var failure = job.Failure;
                    failedChecks = failure is null ? 0 : failedChecks + 1;
                    if (failedChecks >= 2)
                    {
                        return (null, new ConfigException(
                            "service.state",
                            $"the {watch.Kind} service's daemon exited before it took the store lock ({failure})",
                            "fix the cause, then `abl daemon start` again; `abl daemon install-service` rewrites a unit whose abl or .NET runtime moved"));
                    }
                }

                await Task.Delay(PollInterval);
                continue;
            }

            locked = true;
            state = DaemonState.Read(store);
            if (state is not null && IsUsable(state)
                && await ProbeAsync(http, DaemonPaths.UrlFor(state.Port), ProbeCap(timeout, clock)) is { } health && health.Pid == state.Pid)
            {
                answered = true;
                if (state.Port != options.Port)
                {
                    // The state comes back with the problem: a healthy holder on another port.
                    return (state, HeldBy(store, state, options.Port));
                }

                if (health.ReadyWorkers >= 1)
                {
                    return (state, null);
                }

                olderError = null;
                if (health.LastWorkerError is { } workerError)
                {
                    if (!(ErrorTime(workerError) < began))
                    {
                        return (null, WorkerFailure(
                            workerError, $"the running daemon (pid {state.Pid}) has no warm worker; its last worker failed",
                            "fix the cause, then `abl daemon stop` and `abl daemon start`"));
                    }

                    olderError = workerError;
                }
            }

            if (clock.Elapsed >= timeout)
            {
                var waited = $"{timeout.TotalSeconds:0} s";
                return (null, answered && olderError is not null
                    ? WorkerFailure(
                        olderError,
                        $"the running daemon (pid {state!.Pid}) had no warm worker within {waited}; its last worker failed at {olderError["at"]}",
                        "see `abl daemon logs` for the cause; a session open retries the spawn, or fix it and `abl daemon stop`, then `abl daemon start`")
                    : answered
                    ? new ConfigException(
                        "daemon.reachable",
                        $"the running daemon (pid {state!.Pid}) had no warm worker within {waited}",
                        "raise --timeout, or see `abl daemon status` and `abl daemon logs`")
                    : state is not null && IsUsable(state) && NamesLockHolder(state)
                        ? new ConfigException(
                            "daemon.reachable",
                            $"daemon pid {state.Pid} holds the store lock but did not answer /healthz at {DaemonPaths.UrlFor(state.Port)} within {waited}",
                            "see `abl daemon logs`; `abl daemon stop --force` stops it and its workers")
                        : new ConfigException(
                            "daemon.reachable",
                            $"another process holds the store lock {DaemonPaths.LockFile(store)}, and no daemon became ready within {waited}",
                            "wait for it to finish starting and run `abl daemon status`"));
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>When a <c>lastWorkerError</c> happened, or null when its <c>at</c> does not parse.</summary>
    private static DateTimeOffset? ErrorTime(JsonObject workerError) =>
        workerError["at"] is JsonValue at && at.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : null;

    private static ConfigException WorkerFailure(JsonObject workerError, string what, string hint) => new(
        workerError["check"]?.GetValue<string>() ?? "worker.roundtrip",
        $"{what} ({workerError["stage"]}): {workerError["message"]}",
        hint);

    private async Task StopChildAsync(Process child)
    {
        try
        {
            if (child.HasExited)
            {
                return;
            }

            // A short grace (eng-code L-1): the child is being discarded anyway.
            Processes.Signal(child.Id, UnixIdentity.SigTerm);
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await child.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                Processes.Signal(child.Id, UnixIdentity.SigKill);
                await child.WaitForExitAsync();
            }
        }
        catch (IOException)
        {
            // not ours to signal any more
        }
        finally
        {
            // The child leads its own group once it called setsid; reap any worker left in it.
            try
            {
                Processes.Signal(-child.Id, UnixIdentity.SigKill);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- stop --------------------------------------------------------------------------

    public async Task<int> StopAsync(bool json, bool force)
    {
        var (located, locateError) = await LocateAsync();
        if (locateError is not null)
        {
            return Fail(json, locateError);
        }

        switch (located!.Liveness)
        {
            case Liveness.NotRunning:
                return NotRunning(json, located.Store, located.State);
            case Liveness.Unconfirmed:
                return Fail(json, UnhealthyError(located)); // nothing names a pid that is safe to signal
            case Liveness.Unresponsive when !force:
                return Fail(json, new ConfigException(
                    "daemon.reachable",
                    $"daemon pid {located.State!.Pid} does not answer /healthz at {DaemonPaths.UrlFor(located.State.Port)}, so its sessions cannot be listed; it was not stopped",
                    "`abl daemon stop --force` kills it and its workers (unsaved session work is lost)"));
        }

        var state = located.State!;

        // Sessions first, even with a service installed (plan L1, AC-20): they hold unsaved
        // work. An unresponsive daemon (--force) has none known.
        IReadOnlyList<(string Id, string Head)>? sessions = null;
        if (located.Liveness == Liveness.Running)
        {
            using var http = HealthClient();
            try
            {
                sessions = SessionsOf(await ReadOpAsync(http, DaemonPaths.UrlFor(state.Port), "session_list"));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                or System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                if (!force)
                {
                    return Fail(json, new ConfigException(
                        "daemon.reachable",
                        $"cannot list the daemon's sessions ({ex.Message}); it was not stopped",
                        "retry, or `abl daemon stop --force` (unsaved session work is lost)"));
                }
            }
        }

        if (sessions is { Count: > 0 } && !force)
        {
            if (json)
            {
                Out.WriteLine(new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["check"] = null,
                        ["message"] = $"{sessions.Count} active session(s); the daemon was not stopped",
                        ["hint"] = StopHint,
                    },
                    ["sessions"] = SessionsJson(sessions),
                }.ToJsonString());
            }

            Err.WriteLine($"abl: {sessions.Count} active session(s); the daemon was not stopped");
            foreach (var (id, head) in sessions)
            {
                Err.WriteLine($"  {DaemonLog.Clean(id)} → {DaemonLog.Clean(head)}");
            }

            Err.WriteLine($"  hint: {StopHint}");
            return 1;
        }

        // The pid was confirmed against /healthz, or (unresponsive) as the live lock holder
        // that wrote the record, so a reused pid is never signalled. IsUsable admits only a
        // group that is null or the pid itself (eng-security LOW-1).
        var group = state.ProcessGroup;

        // A daemon the installed service runs is stopped through its manager, and with
        // --force booted out first, so a restart policy cannot bring it back (FR-008, M11).
        var services = host.Services;
        var managed = services.Query().Runs(state.Pid);
        var service = managed ? ServiceJson(services.Kind, null) : null;
        string? serviceProblem = null;

        // Its workers, taken while it still runs (round-2 eng-code M-1): a daemon without its
        // own group (a foreground `daemon run`, or a service) has no group to sweep after a SIGKILL.
        var before = Processes.Snapshot();
        var workers = before.DescendantsOf(state.Pid);

        // AC-20 is checked, never assumed (2a round-3): by the group when the daemon has one,
        // else by the process table. Success is reported only once the check passed.
        var unverified = before.UncheckedFor(group);

        // Gone once its pid is, or once the store lock is free: the daemon releases the lock
        // only by exiting, so an unreaped (zombie) daemon is gone too (round-3 eng-code L-2).
        bool Exited() => !Processes.Exists(state.Pid) || !IsHeldOrUnknown(located.Store);
        if (force && managed && services.BootOut() is { Ok: false, Error: var bootOut })
        {
            serviceProblem = bootOut;
        }

        if (force && group is { } g)
        {
            Processes.Signal(-g, UnixIdentity.SigKill);
        }
        else if (force && located.Liveness == Liveness.Running)
        {
            // A short SIGTERM grace first: the host then disposes its sessions and pool, which
            // kill their workers. Only a daemon that does not exit in time is killed with them.
            TrySignal(state.Pid, UnixIdentity.SigTerm);
            if (!await WaitUntilAsync(Exited, ForceGrace))
            {
                var killed = KillTree(state.Pid);
                workers = [.. workers, .. killed.DescendantsOf(state.Pid)];
                unverified ??= killed.UncheckedFor(group);
            }
        }
        else if (force)
        {
            var killed = KillTree(state.Pid);
            workers = [.. workers, .. killed.DescendantsOf(state.Pid)];
            unverified ??= killed.UncheckedFor(group);
        }
        else if (managed && services.Stop() is { Ok: false, Error: var stopError })
        {
            // A clean exit after SIGTERM is not restarted (Restart=on-failure, KeepAlive
            // SuccessfulExit=false), so the process path still stops it.
            serviceProblem = stopError;
            TrySignal(state.Pid, UnixIdentity.SigTerm);
        }
        else if (!managed)
        {
            Processes.Signal(state.Pid, UnixIdentity.SigTerm);
        }

        if (!await WaitUntilAsync(Exited, StopTimeout))
        {
            // The one result shape of stop, with its check ID (round-4 eng-arch LOW-2).
            var timedOut = new ConfigException(
                "daemon.reachable",
                $"daemon pid {state.Pid} did not exit within {StopTimeout.TotalSeconds:0} s" + (serviceProblem is null ? "" : $" ({serviceProblem})"),
                force ? "it may be stuck in the kernel: see `abl daemon logs`" : "`abl daemon stop --force` kills it and its workers (unsaved session work is lost)");
            if (json)
            {
                var body = ErrorJson(timedOut);
                body["stopped"] = false;
                body["pid"] = state.Pid;
                body["forced"] = force;
                body["service"] = service;
                Out.WriteLine(body.ToJsonString());
            }

            HostVerbs.WriteError(Err, timedOut);
            return 1;
        }

        // Workers share the daemon's group: none may outlive it (AC-20).
        if (group is { } pgid && !await WaitUntilAsync(() => !Processes.GroupExists(pgid), TimeSpan.FromSeconds(5)))
        {
            Processes.Signal(-pgid, UnixIdentity.SigKill);
            if (!await WaitUntilAsync(() => !Processes.GroupExists(pgid), SweepWait))
            {
                unverified ??= $"processes remain in its process group {pgid} after SIGKILL";
            }
        }

        // Without a group, or after a kill: no worker it started may outlive it, and no
        // worker socket is left behind in its runtime dir (AC-20).
        unverified ??= await SweepAsync(workers, located.RuntimeDir);

        var dropped = sessions is null ? null : SessionsJson(sessions);
        if (unverified is not null)
        {
            // It is stopped, but AC-20 was not shown to hold: say so, never claim it (exit 1).
            var problem = new ConfigException(
                "daemon.workers",
                $"daemon pid {state.Pid} stopped, but its workers could not be verified gone: {unverified}",
                $"look for processes whose argv names {located.RuntimeDir}/ and kill them; then `abl daemon status`");
            if (json)
            {
                var body = ErrorJson(problem);
                body["stopped"] = true;
                body["pid"] = state.Pid;
                body["forced"] = force;
                body["service"] = service;
                body["workersVerified"] = false;
                body["droppedSessions"] = dropped;
                Out.WriteLine(body.ToJsonString());
            }
            else
            {
                // Its sessions are dropped all the same: keep their recovery lines (round-4 eng-code L-4).
                WriteDropped(Err, sessions);
            }

            HostVerbs.WriteError(Err, problem);
            return 1;
        }

        if (json)
        {
            Out.WriteLine(new JsonObject
            {
                ["stopped"] = true,
                ["pid"] = state.Pid,
                ["forced"] = force,
                ["service"] = service,
                ["workersVerified"] = true,
                ["droppedSessions"] = dropped,
            }.ToJsonString());
        }
        else
        {
            Out.WriteLine($"stopped pid {state.Pid}" + (managed ? $" (through {services.Kind})" : ""));
            WriteDropped(Out, sessions);
        }

        if (serviceProblem is not null)
        {
            Err.WriteLine($"abl: warning: service.state: {serviceProblem}");
        }

        return 0;
    }

    /// <summary>The sessions a stop dropped, each with its recovery command, or that they are unknown.</summary>
    private static void WriteDropped(TextWriter output, IReadOnlyList<(string Id, string Head)>? sessions)
    {
        if (sessions is null)
        {
            output.WriteLine("  its sessions are unknown (the daemon did not list them)");
        }

        foreach (var (id, head) in sessions ?? [])
        {
            output.WriteLine($"  dropped session {DaemonLog.Clean(id)}; recover with: abl checkpoint branch {DaemonLog.Clean(head)}");
        }
    }

    /// <summary>
    /// SIGSTOPs <paramref name="pid"/> so it starts no new child, reads the process table,
    /// SIGKILLs its descendants while the stopped daemon still holds their pids (an unreaped
    /// child keeps its pid, so none is reused), then SIGKILLs the daemon. Returns that table.
    /// </summary>
    private ProcessTree.Table KillTree(int pid)
    {
        TrySignal(pid, UnixIdentity.SigStop);
        var table = Processes.Snapshot();
        foreach (var row in table.DescendantsOf(pid))
        {
            TrySignal(row.Pid, UnixIdentity.SigKill);
        }

        TrySignal(pid, UnixIdentity.SigKill);
        return table;
    }

    /// <summary>
    /// After the daemon exited: SIGKILLs every worker of <paramref name="workers"/> still
    /// running with the same argv (so a pid reused since the snapshot is never signalled), then
    /// removes the worker sockets their argv named in <paramref name="runtimeDir"/>. Returns
    /// null once none of them runs, else why that is not known (round-3: never silent).
    /// </summary>
    private async Task<string?> SweepAsync(IReadOnlyList<ProcessTree.Row> workers, string runtimeDir)
    {
        if (workers.Count == 0)
        {
            return null;
        }

        var named = workers.Select(w => (w.Pid, w.Args)).ToHashSet();
        string? why = null;
        int[]? Survivors()
        {
            var table = Processes.Snapshot();
            why = table.Unreadable;
            return table.Rows?.Where(r => named.Contains((r.Pid, r.Args))).Select(r => r.Pid).ToArray();
        }

        foreach (var pid in Survivors() ?? [])
        {
            TrySignal(pid, UnixIdentity.SigKill);
        }

        // Only the kernel's reaping may lag a moment.
        int[]? left = null;
        if (!await WaitUntilAsync(() => (left = Survivors()) is { Length: 0 }, SweepWait))
        {
            // A socket is never removed while a process naming it remains.
            return why ?? $"pid(s) {string.Join(", ", left ?? [])} still run after SIGKILL";
        }

        var prefix = Path.TrimEndingDirectorySeparator(runtimeDir) + "/";
        var sockets = workers.SelectMany(w => WorkerSockets(w.Args, prefix)).Distinct(StringComparer.Ordinal)
            .Where(socket => new FileInfo(socket) is { Exists: true } or { LinkTarget: not null })
            .ToArray();

        // The daemon checked its runtime dir, this CLI did not (round-3 eng-security LOW-1):
        // nothing is removed from a dir that fails the A5 rule, and that is reported.
        if (sockets.Length > 0 && RuntimePaths.CheckDir(runtimeDir, UnixConfigFileSystem.Instance) is { } badDir)
        {
            return $"its worker sockets were not removed: {badDir}";
        }

        foreach (var socket in sockets)
        {
            try
            {
                if (new FileInfo(socket) is { Exists: true, LinkTarget: null })
                {
                    File.Delete(socket);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>The worker socket paths (<see cref="RuntimePaths.IsSocketFileName"/> in the runtime dir) named in an argv.</summary>
    private static IEnumerable<string> WorkerSockets(string args, string prefix)
    {
        for (var at = args.IndexOf(prefix, StringComparison.Ordinal); at >= 0; at = args.IndexOf(prefix, at + 1, StringComparison.Ordinal))
        {
            var name = new string([.. args[(at + prefix.Length)..].TakeWhile(c => c != ' ')]);
            if (RuntimePaths.IsSocketFileName(name))
            {
                yield return prefix + name;
            }
        }
    }

    private void TrySignal(int pid, int signal)
    {
        try
        {
            Processes.Signal(pid, signal);
        }
        catch (IOException)
        {
            // not ours to signal
        }
    }

    private static JsonArray SessionsJson(IReadOnlyList<(string Id, string Head)> sessions) =>
        new([.. sessions.Select(s => (JsonNode)new JsonObject { ["session"] = s.Id, ["lastCheckpoint"] = s.Head })]);

    private static IReadOnlyList<(string Id, string Head)> SessionsOf(JsonObject list) =>
        [.. (list["items"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (
            s["id"]?.GetValue<string>() ?? throw new InvalidOperationException("a session without an id"),
            s["head"]?.GetValue<string>() ?? "-"))];

    // ---- status ------------------------------------------------------------------------

    /// <summary>
    /// <c>abl daemon status</c>, the CLI form of the <c>daemon_status</c> op (FR-010, AC-27):
    /// the op's result with only a <c>local</c> object added (the store's lifecycle files and
    /// the service state). Not running: <c>{running:false, stateFile, lastPid}</c>, exit 3
    /// (AC-21); held but unconfirmed: <c>{running:true, healthy:false}</c>, exit 1.
    /// </summary>
    public async Task<int> StatusAsync(bool json)
    {
        var (located, locateError) = await LocateAsync();
        if (locateError is not null)
        {
            return Fail(json, locateError);
        }

        switch (located!.Liveness)
        {
            case Liveness.NotRunning:
                return NotRunning(json, located.Store, located.State);
            case Liveness.Unresponsive or Liveness.Unconfirmed:
                return Unhealthy(json, located);
        }

        var state = located.State!;
        JsonObject status;
        using (var http = HealthClient())
        {
            try
            {
                status = await ReadOpAsync(http, DaemonPaths.UrlFor(state.Port), "daemon_status");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                or System.Text.Json.JsonException or InvalidOperationException)
            {
                return Fail(json, new ConfigException(
                    "daemon.reachable",
                    $"daemon pid {state.Pid} answers /healthz, but daemon_status failed ({ex.Message})",
                    "a daemon older than this CLI has no daemon_status: `abl daemon stop`, then `abl daemon start`"));
            }
        }

        var service = host.Services.Query();
        status["local"] = new JsonObject
        {
            ["stateFile"] = DaemonPaths.StateFile(located.Store),
            ["lockFile"] = DaemonPaths.LockFile(located.Store),
            ["store"] = located.Store,
            ["service"] = service.ToJson(state.Pid),
        };

        if (json)
        {
            Out.WriteLine(status.ToJsonString());
            return 0;
        }

        var workers = status["workers"] as JsonObject;
        Out.WriteLine(DaemonLog.Clean(
            $"running pid {state.Pid} at {DaemonPaths.UrlFor(state.Port)} (version {Text(status["version"])}, " +
            $"{Text(workers?["ready"])} ready worker(s), {Text(status["sessions"])} session(s))"));
        if (status["logFile"] is JsonValue log)
        {
            Out.WriteLine(DaemonLog.Clean($"  log {log}"));
        }

        if (status["blender"] is JsonObject blender)
        {
            Out.WriteLine(DaemonLog.Clean(blender["check"] is { } check
                ? $"  blender refused ({check}): {blender["problem"]}"
                : $"  blender {Text(blender["version"])} ({Text(blender["support"])}" +
                    (blender["allowUnsupported"]?.GetValue<bool>() == true ? ", allowUnsupported" : "") + $") at {Text(blender["path"])}"));
        }

        // 009 T020: the last ready local worker's GPU backend and its first device.
        if (status["gpu"] is JsonObject gpu)
        {
            var device = gpu["devices"] is JsonArray { Count: > 0 } devices ? $" ({Text(devices[0]?["name"])})" : "";
            Out.WriteLine(DaemonLog.Clean($"  gpu {Text(gpu["backend"])}{device}"));
        }

        if (status["config"] is JsonObject config)
        {
            Out.WriteLine(DaemonLog.Clean($"  config {Text(config["file"])} (digest {Text(config["effectiveDigest"])})"));
        }

        if (workers?["lastWorkerError"] is JsonObject error)
        {
            Out.WriteLine(DaemonLog.Clean($"  last worker error ({error["stage"]}, {error["at"]}): {error["message"]}"));
        }

        if (service.Installed)
        {
            Out.WriteLine(DaemonLog.Clean(
                $"  service {service.Kind} {service.UnitFile} ({(service.Active ? "active" : service.Loaded ? "loaded" : "not loaded")}" +
                (service.Runs(state.Pid) ? ", runs this daemon" : "") + ")"));
        }

        return 0;
    }

    private static string Text(JsonNode? node) =>
        node is null ? "-" : node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();

    /// <summary>
    /// The store lock is held, but <c>/healthz</c> does not confirm the record's pid (eng-code
    /// M-1): a hung or busy daemon, or one still starting. Running, not healthy, exit 1.
    /// </summary>
    private int Unhealthy(bool json, Located located)
    {
        var error = UnhealthyError(located);
        if (json)
        {
            var named = located.Liveness == Liveness.Unresponsive ? located.State : null;
            Out.WriteLine(new JsonObject
            {
                ["running"] = true,
                ["healthy"] = false,
                ["pid"] = named?.Pid,
                ["url"] = named is null ? null : DaemonPaths.UrlFor(named.Port),
                ["logFile"] = named?.LogFile,
                ["stateFile"] = DaemonPaths.StateFile(located.Store),
                ["error"] = ErrorBody(error),
            }.ToJsonString());
        }

        HostVerbs.WriteError(Err, error);
        return 1;
    }

    private static ConfigException UnhealthyError(Located located) => located.Liveness == Liveness.Unresponsive
        ? new ConfigException(
            "daemon.reachable",
            $"daemon pid {located.State!.Pid} holds the store lock but does not answer /healthz at {DaemonPaths.UrlFor(located.State.Port)}",
            "it may be busy or hung: see `abl daemon logs`; `abl daemon stop --force` stops it and its workers")
        : new ConfigException(
            "daemon.reachable",
            $"a process holds the store lock {DaemonPaths.LockFile(located.Store)}, but daemon.json does not name a live daemon yet",
            "a daemon may be starting: wait, then run `abl daemon status` again");

    // ---- the liveness rule ---------------------------------------------------------------

    /// <summary>
    /// The store, its instance record and the daemon's liveness, or the error that stopped the
    /// lookup. The store-root gate comes first; a held lock is required before the record is
    /// trusted (eng-security LOW-2: another account cannot hold a lock inside the user's 0700
    /// store); the URL is built from the record's port, never read from the file; and a record
    /// whose pid started after it was written is never trusted (a recycled pid).
    /// </summary>
    public async Task<(Located? Located, ConfigException? Error)> LocateAsync()
    {
        AblConfig config;
        try
        {
            config = AblConfig.Resolve(host.Inputs);
        }
        catch (ConfigException error)
        {
            return (null, error);
        }

        var store = config.Options.StoreDir;
        var runtimeDir = config.Options.RuntimeDir;
        if (!Directory.Exists(store))
        {
            return (new Located(store, runtimeDir, Liveness.NotRunning, DaemonState.Read(store), null), null);
        }

        if (UntrustedStore(store) is { } untrusted)
        {
            return (null, untrusted);
        }

        var state = DaemonState.Read(store);

        bool held;
        try
        {
            held = StoreLock.IsHeld(store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, new ConfigException("paths.store", $"cannot open the store lock: {ex.Message}", "fix the store's permissions"));
        }

        if (!held)
        {
            return (new Located(store, runtimeDir, Liveness.NotRunning, state, null), null);
        }

        if (state is null || !IsUsable(state))
        {
            return (new Located(store, runtimeDir, Liveness.Unconfirmed, state, null), null);
        }

        using var http = HealthClient();
        if (await ProbeAsync(http, DaemonPaths.UrlFor(state.Port)) is { } health && health.Pid == state.Pid)
        {
            return (new Located(store, runtimeDir, Liveness.Running, state, health), null);
        }

        return (new Located(store, runtimeDir, NamesLockHolder(state) ? Liveness.Unresponsive : Liveness.Unconfirmed, state, null), null);
    }

    /// <summary>A record whose pid can be signalled: a real pid, and a group that is null or the pid itself.</summary>
    private static bool IsUsable(DaemonState state) =>
        state.Pid > 1 && state.Port is > 0 and <= 65535 && (state.ProcessGroup is null || state.ProcessGroup == state.Pid);

    /// <summary>
    /// True when the record plausibly names the lock holder: its pid is alive and started no
    /// later than the record says the daemon took the lock, so a pid recycled since then is
    /// never trusted. Callers have seen the lock held.
    /// </summary>
    private bool NamesLockHolder(DaemonState state) =>
        Processes.StartTime(state.Pid) is { } started && started <= state.StartedAt.ToUniversalTime() + StartTimeSlack;

    /// <summary>True when another process holds the store lock, or the lock cannot be probed.</summary>
    private static bool IsHeldOrUnknown(string store)
    {
        try
        {
            return StoreLock.IsHeld(store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The store-root gate: the store's lock and record are trusted only in a store root this
    /// user owns and no one else can write (round-2 eng-security LOW-1): otherwise another
    /// account could hold the lock and name a pid for <c>stop</c> to signal, or a log for
    /// <c>logs</c> to print. One rule for every daemon verb, before it takes, probes or reads
    /// either (2a round 4): <c>run</c> and <c>start</c> through <see cref="PrepareDirs"/>,
    /// <c>stop</c> and <c>status</c> through <see cref="LocateAsync"/>, and <c>logs</c>
    /// before it reads the record. Null when trusted.
    /// </summary>
    internal static ConfigException? UntrustedStore(string store)
    {
        try
        {
            var mode = File.GetUnixFileMode(store);
            if (UnixIdentity.OwnerOf(store) == UnixIdentity.EffectiveUserId
                && (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            {
                return null;
            }

            return new ConfigException(
                "paths.store",
                $"the store {store} is not owned by this user or is writable by others ({Convert.ToString((int)mode, 8)}); its lock and daemon.json are not trusted",
                "use a store you own with mode 0700 (paths.store, ABL_STORE)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigException("paths.store", $"cannot inspect the store {store}: {ex.Message}", "fix paths.store (ABL_STORE) or its permissions");
        }
    }

    /// <summary>Creates the state dir and the store (0700), then passes the store-root gate.</summary>
    internal static ConfigException? PrepareDirs(string stateDir, string storeDir)
    {
        try
        {
            Directory.CreateDirectory(stateDir, PrivateDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigException("runtime.dir", $"cannot create the state dir {stateDir}: {ex.Message}", "fix XDG_STATE_HOME or its permissions");
        }

        try
        {
            Directory.CreateDirectory(storeDir, PrivateDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigException("paths.store", $"cannot create the store {storeDir}: {ex.Message}", "fix paths.store (ABL_STORE) or its permissions");
        }

        // The writers of the lock and the record pass the same gate as their readers.
        return UntrustedStore(storeDir);
    }

    internal static ConfigException HeldBy(string store, DaemonState? state, int port) => state is null
        ? new ConfigException(
            "daemon.port",
            $"another daemon holds the store lock {DaemonPaths.LockFile(store)}",
            "wait for it to finish starting, or run `abl daemon status`")
        : new ConfigException(
            "daemon.port",
            $"a daemon already runs on store {store} at port {state.Port} (pid {state.Pid})" +
                (state.Port == port ? "" : $"; this configuration names port {port}"),
            state.Port == port ? "`abl daemon stop` first" : $"use daemon.port {state.Port}, or `abl daemon stop` first");

    // ---- rendering -------------------------------------------------------------------------

    /// <summary>AC-21: <c>{running:false, stateFile, lastPid}</c>, exit 3.</summary>
    private int NotRunning(bool json, string store, DaemonState? state)
    {
        var stateFile = DaemonPaths.StateFile(store);
        if (json)
        {
            Out.WriteLine(new JsonObject
            {
                ["running"] = false,
                ["stateFile"] = stateFile,
                ["lastPid"] = state?.Pid,
            }.ToJsonString());
        }
        else
        {
            Out.WriteLine(state is null
                ? $"not running (no daemon has run on store {store})"
                : $"not running (last pid {state.Pid}; {stateFile})");
        }

        return 3;
    }

    private int Report(bool json, string what, DaemonState state, bool alreadyRunning, JsonObject? service)
    {
        if (json)
        {
            Out.WriteLine(new JsonObject
            {
                ["pid"] = state.Pid,
                ["url"] = DaemonPaths.UrlFor(state.Port),
                ["logFile"] = state.LogFile,
                ["startedAt"] = state.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ["alreadyRunning"] = alreadyRunning,
                ["service"] = service,
            }.ToJsonString());
        }
        else
        {
            Out.WriteLine($"{what}: pid {state.Pid} at {DaemonPaths.UrlFor(state.Port)}" +
                (state.LogFile is null ? "" : $" (log {state.LogFile})") +
                (service is null ? "" : $" through {service["kind"]}" + (service["domain"] is { } domain ? $" ({domain})" : "")));
        }

        return 0;
    }

    private static JsonObject ServiceJson(string kind, string? domain) => new() { ["kind"] = kind, ["domain"] = domain };

    private int Fail(bool json, ConfigException error)
    {
        if (json)
        {
            Out.WriteLine(ErrorJson(error).ToJsonString());
        }

        HostVerbs.WriteError(Err, error);
        return 1;
    }

    private int FailStart(bool json, ConfigException error, IReadOnlyList<string> early, string logFile)
    {
        IReadOnlyList<string> tail = File.Exists(logFile) ? DaemonLog.Tail(logFile, FailureTailLines) : [];
        if (json)
        {
            var body = ErrorJson(error);
            body["output"] = new JsonArray([.. early.Select(l => (JsonNode)l)]);
            body["logFile"] = logFile;
            body["logTail"] = new JsonArray([.. tail.Select(l => (JsonNode)l)]);
            Out.WriteLine(body.ToJsonString());
        }

        HostVerbs.WriteError(Err, error);
        if (early.Count > 0)
        {
            Err.WriteLine("  output before the log:");
            foreach (var line in early)
            {
                Err.WriteLine("    " + line);
            }
        }

        if (tail.Count > 0)
        {
            Err.WriteLine($"  last {tail.Count} line(s) of {logFile}:");
            foreach (var line in tail)
            {
                Err.WriteLine("    " + line);
            }
        }

        return 1;
    }

    private static JsonObject ErrorJson(ConfigException error) => new() { ["error"] = ErrorBody(error) };

    private static JsonObject ErrorBody(ConfigException error) =>
        new() { ["check"] = error.Check, ["message"] = error.Message, ["hint"] = error.Hint };

    // ---- HTTP --------------------------------------------------------------------------

    /// <summary>A 2 s client whose responses are capped (eng-security LOW-2: whoever answers is not trusted to be small).</summary>
    internal static HttpClient HealthClient() => new() { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 1024 * 1024 };

    /// <summary>A probe's timeout within a deadline (eng-code L-1): at most 2 s, at least 250 ms.</summary>
    private static TimeSpan ProbeCap(TimeSpan timeout, Stopwatch clock) => TimeSpan.FromTicks(Math.Clamp(
        (timeout - clock.Elapsed).Ticks, TimeSpan.FromMilliseconds(250).Ticks, TimeSpan.FromSeconds(2).Ticks));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(100);
        }

        return true;
    }

    /// <summary><c>/healthz</c>, or null when nothing answers there.</summary>
    internal static async Task<Health?> ProbeAsync(HttpClient http, string url, TimeSpan? cap = null)
    {
        try
        {
            using var cancel = new CancellationTokenSource(cap ?? http.Timeout);
            using var response = await http.GetAsync(url + "/healthz", cancel.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel.Token)) as JsonObject;
            return body?["pid"] is null
                ? null
                : new Health(
                    body["pid"]!.GetValue<int>(),
                    body["version"]?.GetValue<string>(),
                    body["readyWorkers"]?.GetValue<int>() ?? 0,
                    body["sessions"]?.GetValue<int>() ?? 0,
                    body["lastWorkerError"] as JsonObject);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one way a host verb reaches <c>/api/ops</c> (plan B1): a read-only op from
    /// <see cref="HostVerbs.ReadOps"/>, never a mutating one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The op is not a read op, or the answer is not a JSON object.</exception>
    /// <exception cref="HttpRequestException">The daemon refused or did not answer.</exception>
    internal static async Task<JsonObject> ReadOpAsync(HttpClient http, string url, string op)
    {
        if (!HostVerbs.ReadOps.Contains(op))
        {
            throw new InvalidOperationException($"host verbs may read through /api/ops only ({string.Join(", ", HostVerbs.ReadOps)}), not {op}");
        }

        using var response = await http.PostAsync(url + "/api/ops/" + op, new StringContent("{}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
            ?? throw new InvalidOperationException($"{op} did not answer with a JSON object");
    }

    /// <summary>The child's stdout/stderr before its redirect: kept bounded, shown only on failure.</summary>
    private sealed class EarlyOutput
    {
        private const int MaxLines = 200;
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public async Task DrainAsync(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    lock (_gate)
                    {
                        _lines.Enqueue(line.Length > 500 ? line[..500] : line);
                        if (_lines.Count > MaxLines)
                        {
                            _lines.Dequeue();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        public IReadOnlyList<string> Lines(int count)
        {
            lock (_gate)
            {
                return [.. _lines.TakeLast(count).Select(l => new string([.. l.Select(c => char.IsControl(c) && c != '\t' ? '?' : c)]))];
            }
        }
    }
}
