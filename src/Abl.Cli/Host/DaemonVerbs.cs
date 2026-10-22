using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Abl.Daemon;

namespace Abl.Cli.Host;

/// <summary>
/// The daemon host verbs (008 FR-007, FR-008, plan B3): argument parsing for
/// <c>start</c>, <c>stop</c> and <c>status</c>, which run in <see cref="DaemonLifecycle"/>,
/// plus <c>run</c> (the daemon itself), <c>logs</c>, <c>install-service</c> and
/// <c>uninstall-service</c>. Exit codes: 0 ok, 1 failed, 2 usage, 3 not running. Unix only:
/// Blender's CPython has no AF_UNIX on Windows (D-15). This is the only CLI type that names
/// <c>Abl.Daemon</c> (<c>run</c> hosts it in-process); op verbs only post to <c>/api/ops</c>.
/// </summary>
/// <remarks>
/// <c>run</c> must not touch <see cref="Console"/> before <see cref="DaemonLog.Redirect"/>:
/// the console streams duplicate fds 1 and 2 when first used, so an early write would keep
/// the parent's pipe open and the agent's tool call would never see EOF (AC-17).
/// </remarks>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static class DaemonVerbs
{
    /// <summary>The verbs this class runs: the lifecycle host verbs and <c>status</c> (the CLI form of <c>daemon_status</c>).</summary>
    public static bool Handles(string verb) => verb == "status" || HostVerbs.Verbs.Contains(("daemon", verb));

    public static async Task<int> RunAsync(string verb, IReadOnlyList<string> rest) => verb switch
    {
        "run" => await RunDaemonAsync(rest),
        "start" => await StartAsync(rest),
        "stop" => await StopAsync(rest),
        "status" => await StatusAsync(rest),
        "logs" => Logs(rest),
        "install-service" => InstallService(rest, Console.Out, Console.Error),
        "uninstall-service" => UninstallService(rest, Console.Out, Console.Error),
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "not a daemon verb"),
    };

    /// <summary>The lifecycle core over this process: its inputs, console, process table and service manager.</summary>
    public static DaemonLifecycle Lifecycle()
    {
        var inputs = ConfigInputs.FromProcess();
        return new DaemonLifecycle(new LifecycleHost(
            inputs, Console.Out, Console.Error, UnixProcessControl.Instance,
            ServiceManagers.ForService(inputs.Home, inputs.Env)));
    }

    // ---- run ---------------------------------------------------------------------------

    private static async Task<int> RunDaemonAsync(IReadOnlyList<string> rest)
    {
        // Own flags only (plan A6): nothing else can steer Kestrel.
        var detached = false;
        string? logFile = null;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--detached":
                    detached = true;
                    break;
                case "--log-file" when i + 1 < rest.Count:
                    logFile = Path.GetFullPath(rest[++i]);
                    break;
                default:
                    return Usage($"daemon run takes only --detached and --log-file FILE (got '{rest[i]}')");
            }
        }

        if (detached && logFile is null)
        {
            return Usage("daemon run --detached needs --log-file FILE");
        }

        // Until the redirect, stderr is still the starter's pipe, which reports it.
        var inputs = ConfigInputs.FromProcess().WithBlenderSearch();
        AblConfig config;
        try
        {
            config = AblConfig.Resolve(inputs);
        }
        catch (ConfigException error)
        {
            HostVerbs.WriteError(Console.Error, error);
            return 1;
        }

        var options = config.Options;
        var stateDir = DaemonPaths.StateDir(inputs.Home, inputs.Env);
        if (DaemonLifecycle.PrepareDirs(stateDir, options.StoreDir) is { } dirProblem)
        {
            HostVerbs.WriteError(Console.Error, dirProblem);
            return 1;
        }

        using var storeLock = StoreLock.TryAcquire(options.StoreDir, DaemonLifecycle.LockRetry);
        if (storeLock is null)
        {
            HostVerbs.WriteError(Console.Error, DaemonLifecycle.HeldBy(options.StoreDir, DaemonState.Read(options.StoreDir), options.Port));
            return 1;
        }

        if (detached)
        {
            // Before the redirect, so a refusal still reaches the caller's stderr (eng-code L-3).
            try
            {
                UnixIdentity.CreateSession();
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"abl: daemon run --detached cannot leave the caller's process group ({ex.Message})");
                Console.Error.WriteLine("  hint: --detached is for `abl daemon start`; run `abl daemon run` in the foreground instead");
                return 1;
            }
        }

        if (logFile is not null)
        {
            try
            {
                DaemonLog.Redirect(logFile, detached, stateDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"abl: cannot open the daemon log {logFile}: {ex.Message}");
                return 1;
            }
        }

        // One instance value for the record, /healthz and daemon_status (2a eng-arch LOW-2, LOW-3).
        var instance = new DaemonInstance(
            Environment.ProcessId, DateTimeOffset.UtcNow, stateDir, logFile, options.Port, config.EffectiveDigest, detached);
        instance.ToState().Write(options.StoreDir);
        return await DaemonHost.RunAsync(config, instance, CancellationToken.None);
    }

    // ---- start, stop, status -----------------------------------------------------------

    private static async Task<int> StartAsync(IReadOnlyList<string> rest)
    {
        var json = false;
        var timeout = DaemonLifecycle.DefaultStartTimeout;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--timeout" when i + 1 < rest.Count:
                    if (!double.TryParse(rest[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        || !double.IsFinite(seconds) || seconds <= 0 || seconds > 3600)
                    {
                        return Usage("--timeout needs a number of seconds between 0 and 3600");
                    }

                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    return Usage($"daemon start takes only --json and --timeout SECONDS (got '{rest[i]}')");
            }
        }

        return await Lifecycle().StartAsync(json, timeout);
    }

    private static async Task<int> StopAsync(IReadOnlyList<string> rest)
    {
        var json = false;
        var force = false;
        foreach (var arg in rest)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    return Usage($"daemon stop takes only --json and --force (got '{arg}')");
            }
        }

        return await Lifecycle().StopAsync(json, force);
    }

    private static async Task<int> StatusAsync(IReadOnlyList<string> rest)
    {
        var json = false;
        foreach (var arg in rest)
        {
            if (arg != "--json")
            {
                return Usage($"daemon status takes only --json (got '{arg}')");
            }

            json = true;
        }

        return await Lifecycle().StatusAsync(json);
    }

    // ---- logs --------------------------------------------------------------------------

    private static int Logs(IReadOnlyList<string> rest)
    {
        var lines = 50;
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--lines" && i + 1 < rest.Count && int.TryParse(rest[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && n is >= 1 and <= DaemonLog.MaxTailLines)
            {
                lines = n;
                continue;
            }

            return Usage($"daemon logs takes only --lines N (1..{DaemonLog.MaxTailLines})");
        }

        var inputs = ConfigInputs.FromProcess();
        AblConfig config;
        try
        {
            config = AblConfig.Resolve(inputs);
        }
        catch (ConfigException error)
        {
            HostVerbs.WriteError(Console.Error, error);
            return 1;
        }

        // The log the store's record names, behind the store-root gate (round-4 eng-security
        // LOW-2), and only when it is a regular file the user owns; else the derived per-store
        // log, with a note (008 T021, the reader half).
        var store = config.Options.StoreDir;
        if (Directory.Exists(store) && DaemonLifecycle.UntrustedStore(store) is { } untrusted)
        {
            HostVerbs.WriteError(Console.Error, untrusted);
            return 1;
        }

        var (logFile, note) = DaemonLog.Readable(
            DaemonState.Read(store)?.LogFile, DaemonPaths.LogFile(DaemonPaths.StateDir(inputs.Home, inputs.Env), store));
        if (note is not null)
        {
            Console.Error.WriteLine(DaemonLog.Clean("abl: " + note));
        }

        if (!File.Exists(logFile) && new FileInfo(logFile).LinkTarget is null)
        {
            Console.Error.WriteLine($"abl: no daemon log at {logFile}");
            return 1;
        }

        if (!DaemonLog.IsOwnRegularFile(logFile))
        {
            Console.Error.WriteLine($"abl: {logFile} is not a regular file you own; not reading it");
            return 1;
        }

        foreach (var line in DaemonLog.Tail(logFile, lines))
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    // ---- install-service, uninstall-service ----------------------------------------------

    /// <summary>
    /// <c>abl daemon install-service [--dry-run] [--enable-at-login] [--json]</c> (FR-008):
    /// writes the systemd user unit or launchd agent for this <c>abl</c>, whose only
    /// environment is <c>DOTNET_ROOT</c>. <c>--dry-run</c> prints it and changes no file (AC-22).
    /// </summary>
    public static int InstallService(IReadOnlyList<string> rest, TextWriter stdout, TextWriter stderr)
    {
        bool dryRun = false, atLogin = false, json = false;
        foreach (var arg in rest)
        {
            switch (arg)
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--enable-at-login":
                    atLogin = true;
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    return Usage(stderr, $"daemon install-service takes only --dry-run, --enable-at-login and --json (got '{arg}')");
            }
        }

        var (program, prefix) = SelfCommand.Current();
        var inputs = ConfigInputs.FromProcess();
        var plan = ServicePlan.For(
            inputs, program, prefix, RuntimeEnvironment.GetRuntimeDirectory(),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"), Directory.Exists,
            OperatingSystem.IsMacOS() ? HostPlatform.MacOS : HostPlatform.Linux);
        if (plan.Error is { } error)
        {
            return Fail(stdout, stderr, json, error);
        }

        var services = ServiceManagers.ForService(inputs.Home, inputs.Env);
        var path = services switch
        {
            LaunchdAgent launchd => launchd.PlistPath(atLogin),
            SystemdUserService systemd => systemd.UnitPath(),
            _ => throw new InvalidOperationException("no service manager on this host"),
        };

        var problems = new List<string>();
        if (!dryRun)
        {
            try
            {
                // The unit's working directory, and launchd's output file next to its state-dir
                // plist, must exist before the manager starts the job (0700, like every state dir).
                Directory.CreateDirectory(plan.StateDir!, PrivateDir);
                if (services is LaunchdAgent)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ServiceUnits.LaunchdOutput(plan.StateDir!))!, PrivateDir);
                }

                problems.AddRange(services switch
                {
                    LaunchdAgent launchd => launchd.Install(plan.Text!, atLogin),
                    SystemdUserService systemd => systemd.Install(plan.Text!, atLogin),
                    _ => [],
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(stdout, stderr, json, new ConfigException("service.state", $"cannot write {path}: {ex.Message}", "fix the directory's permissions"));
            }
        }

        if (json)
        {
            stdout.WriteLine(new JsonObject
            {
                ["kind"] = services.Kind,
                ["path"] = path,
                ["dryRun"] = dryRun,
                ["enableAtLogin"] = atLogin,
                ["execStart"] = new JsonArray([.. plan.Definition!.ExecStart.Select(a => (JsonNode)a)]),
                ["dotnetRoot"] = plan.Definition.DotnetRoot,
                ["logFile"] = plan.Definition.LogFile,
                ["contents"] = plan.Text,
                ["warnings"] = new JsonArray([.. plan.Warnings.Concat(problems).Select(w => (JsonNode)w)]),
            }.ToJsonString());
        }
        else
        {
            stdout.WriteLine(dryRun ? $"# would write {path} (dry run: nothing written)" : $"# wrote {path}");
            stdout.Write(plan.Text);
            if (!dryRun)
            {
                stdout.WriteLine(services.Kind == "launchd" && !atLogin
                    ? "start it with `abl daemon start` (it loads the job); it does not start at login without --enable-at-login"
                    : "start it with `abl daemon start`; `abl daemon stop` stops it");
            }
        }

        foreach (var warning in plan.Warnings.Concat(problems))
        {
            stderr.WriteLine($"abl: warning: {warning}");
        }

        return 0;
    }

    /// <summary><c>abl daemon uninstall-service [--json]</c>: removes the unit or plist; refuses while the service runs.</summary>
    public static int UninstallService(IReadOnlyList<string> rest, TextWriter stdout, TextWriter stderr)
    {
        var json = false;
        foreach (var arg in rest)
        {
            if (arg != "--json")
            {
                return Usage(stderr, $"daemon uninstall-service takes only --json (got '{arg}')");
            }

            json = true;
        }

        var inputs = ConfigInputs.FromProcess();
        var services = ServiceManagers.ForService(inputs.Home, inputs.Env);
        var state = services.Query();
        if (state.Active)
        {
            return Fail(stdout, stderr, json, new ConfigException(
                "service.state",
                $"the {services.Kind} service runs (pid {state.MainPid?.ToString(CultureInfo.InvariantCulture) ?? "?"}); it was not removed",
                "`abl daemon stop` first (it refuses while sessions are active)"));
        }

        IReadOnlyList<string> removed;
        try
        {
            removed = services switch
            {
                LaunchdAgent launchd => launchd.Uninstall(),
                SystemdUserService systemd => systemd.Uninstall(),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(stdout, stderr, json, new ConfigException("service.state", $"cannot remove the service: {ex.Message}", "fix the directory's permissions"));
        }

        if (json)
        {
            stdout.WriteLine(new JsonObject { ["kind"] = services.Kind, ["removed"] = new JsonArray([.. removed.Select(r => (JsonNode)r)]) }.ToJsonString());
        }
        else
        {
            stdout.WriteLine(removed.Count == 0 ? $"no {services.Kind} service was installed" : string.Join('\n', removed.Select(r => $"removed {r}")));
        }

        return 0;
    }

    private const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static int Fail(TextWriter stdout, TextWriter stderr, bool json, ConfigException error)
    {
        if (json)
        {
            stdout.WriteLine(new JsonObject
            {
                ["error"] = new JsonObject { ["check"] = error.Check, ["message"] = error.Message, ["hint"] = error.Hint },
            }.ToJsonString());
        }

        HostVerbs.WriteError(stderr, error);
        return 1;
    }

    private static int Usage(string message) => Usage(Console.Error, message);

    private static int Usage(TextWriter stderr, string message)
    {
        stderr.WriteLine("abl: " + message);
        return 2;
    }
}

/// <summary>
/// What <c>install-service</c> writes, computed without touching the host (golden-tested,
/// AC-22): the configuration as the service will see it (the service manager's environment,
/// not this shell's), the per-store log, the <c>DOTNET_ROOT</c> rule and the warnings about
/// what does not reach a service-run daemon (2a eng-arch MEDIUM-1).
/// </summary>
public sealed record ServicePlan(ServiceDefinition? Definition, string? Text, string? StateDir, IReadOnlyList<string> Warnings, ConfigException? Error)
{
    /// <summary>
    /// The login identity variables the unit gives the daemon besides <c>DOTNET_ROOT</c>, with
    /// this shell's values at install (the unit starts it through <c>env -i</c>, so nothing else
    /// reaches it); everything else in this shell stays behind.
    /// </summary>
    private static readonly string[] ServiceVariables = ["HOME", "USER", "LOGNAME", "SHELL", "PATH", "TMPDIR", "XDG_RUNTIME_DIR"];

    /// <summary>The environment the service-run daemon resolves its configuration from.</summary>
    public static IReadOnlyDictionary<string, string> ServiceEnvironment(IReadOnlyDictionary<string, string> env) =>
        env.Where(v => ServiceVariables.Contains(v.Key, StringComparer.Ordinal)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    /// <summary>
    /// The unit's variables in a fixed order: <c>HOME</c> is <paramref name="home"/> (what the
    /// configuration was resolved with), the rest this shell's non-empty values.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> UnitVariables(string home, IReadOnlyDictionary<string, string> env) =>
        [.. ServiceVariables
            .Select(name => new KeyValuePair<string, string>(name, name == "HOME" ? home : env.GetValueOrDefault(name) ?? ""))
            .Where(v => v.Value.Length > 0)];

    /// <summary>The plan for this CLI (<paramref name="program"/> + <paramref name="prefix"/>) and runtime.</summary>
    public static ServicePlan For(
        ConfigInputs shell, string program, IReadOnlyList<string> prefix, string runtimeDirectory, string? inheritedDotnetRoot,
        Func<string, bool> directoryExists, HostPlatform platform)
    {
        if (!Path.IsPathFullyQualified(shell.Home) || !Path.IsPathFullyQualified(program))
        {
            // A unit names absolute paths only; an unknown home (HOME unset or missing) has none.
            return new ServicePlan(null, null, null, [], new ConfigException(
                "service.state", $"the home directory ('{shell.Home}') or this abl ('{program}') is not an absolute path", "set HOME to your existing home directory"));
        }

        var variables = UnitVariables(shell.Home, shell.Env);
        var serviceEnv = variables.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        AblConfig config;
        try
        {
            config = AblConfig.Resolve(shell with { Env = serviceEnv, Blender = null });
        }
        catch (ConfigException error)
        {
            return new ServicePlan(null, null, null, [], new ConfigException(
                error.Check, $"the service would refuse to start: {error.Message}", error.Hint));
        }

        var (root, problem) = DotnetRoot.Resolve(runtimeDirectory, inheritedDotnetRoot, directoryExists);
        if (root is null)
        {
            return new ServicePlan(null, null, null, [], new ConfigException(
                "service.state", problem!, "run install-service from the abl you want the service to use"));
        }

        var stateDir = DaemonPaths.StateDir(shell.Home, serviceEnv);
        var logFile = DaemonPaths.LogFile(stateDir, config.Options.StoreDir);
        var definition = ServiceUnits.Define(DotnetRoot.Stable(program, directoryExists), prefix, root, logFile, stateDir, variables);
        string text;
        try
        {
            text = platform == HostPlatform.MacOS ? ServiceUnits.Launchd(definition) : ServiceUnits.Systemd(definition);
        }
        catch (ArgumentException ex)
        {
            return new ServicePlan(null, null, null, [], new ConfigException("service.state", ex.Message, "install abl under a plain path"));
        }

        var warnings = new List<string>();
        var left = shell.Env.Where(v => v.Value.Length > 0).Select(v => v.Key)
            .Where(k => k.StartsWith("ABL_", StringComparison.Ordinal) || k.StartsWith("OTEL_", StringComparison.Ordinal)
                || k is "XDG_CONFIG_HOME" or "XDG_STATE_HOME")
            .Order(StringComparer.Ordinal)
            .ToList();
        if (left.Count > 0)
        {
            warnings.Add($"{string.Join(", ", left)} {(left.Count == 1 ? "is set in this shell but never reaches" : "are set in this shell but never reach")} the service, which reads the config file only " +
                $"(store {config.Options.StoreDir}); put lasting settings in `abl config path`");
        }

        if (config.Options.WorkerEnvPassthrough.Count > 0)
        {
            warnings.Add($"worker.envPassthrough names {string.Join(", ", config.Options.WorkerEnvPassthrough)}, but a service-run daemon has only the unit's " +
                $"{string.Join(", ", definition.Variables.Select(v => v.Key))}, so those values do not reach its workers; start the daemon with `abl daemon start` when a worker needs them");
        }

        if (config[AblConfig.BlenderKey].Source != ConfigSource.File)
        {
            warnings.Add("blender.path is not set in the config file, so the service discovers Blender with the PATH recorded in the unit; pin it there (`abl config show` names the Blender in use)");
        }

        if (!DotnetRoot.HasAspNetCore10(root))
        {
            warnings.Add($"DOTNET_ROOT {root} has no shared/Microsoft.AspNetCore.App/10.* runtime, so the service cannot start abl");
        }

        return new ServicePlan(definition, text, stateDir, warnings, null);
    }
}
