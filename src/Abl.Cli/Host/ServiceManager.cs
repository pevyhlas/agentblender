using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Abl.Core.Config;
using Abl.Core.Lifecycle;

namespace Abl.Cli.Host;

/// <summary>What the service manager knows about the AgentBlender user service.</summary>
/// <param name="Kind"><c>systemd</c> or <c>launchd</c>.</param>
/// <param name="UnitFile">The installed unit or plist, or null when none is installed.</param>
/// <param name="Loaded">The manager has the job loaded.</param>
/// <param name="Active">The job's process runs.</param>
/// <param name="MainPid">The job's main pid, when it runs and the manager names it.</param>
/// <param name="ExecStart">The installed unit's program and arguments.</param>
/// <param name="DotnetRoot">The installed unit's <c>DOTNET_ROOT</c>.</param>
/// <param name="Domain">The launchd domain the job is loaded in (<c>gui/&lt;uid&gt;</c> or <c>user/&lt;uid&gt;</c>).</param>
public sealed record ServiceState(
    string Kind,
    string? UnitFile,
    bool Loaded,
    bool Active,
    int? MainPid,
    IReadOnlyList<string>? ExecStart,
    string? DotnetRoot,
    string? Domain)
{
    public bool Installed => UnitFile is not null;

    /// <summary>
    /// The log the installed unit passes to <c>daemon run</c>. It names the store and state dir
    /// as they were at install; the config file the unit reads decides the store it serves now
    /// (plan B3 "Service handoff", T035 review round 3).
    /// </summary>
    public string? LogFile => ServiceUnits.LogFileOf(ExecStart);

    /// <summary>
    /// Why the manager reports the job down after a failure, or null: systemd
    /// <c>ActiveState=failed</c> or a pending <c>auto-restart</c>; launchd a loaded job with no
    /// pid whose last run exited non-zero or by a signal. <c>start</c> fails fast on it (T035
    /// review round 1, eng-code MEDIUM-1).
    /// </summary>
    public string? Failure { get; init; }

    /// <summary>A launchd job explicitly not running after exit zero; not a readiness signal.</summary>
    internal bool StoppedAfterExit { get; init; }

    /// <summary>Where the manager writes the job's own output (launchd's <c>StandardOutPath</c>), or null (systemd: the journal).</summary>
    public string? OutputFile { get; init; }

    /// <summary>True when the installed service runs the daemon <paramref name="pid"/> (the handoff rule of <c>stop</c>).</summary>
    public bool Runs(int pid) => Active && MainPid == pid;

    public JsonObject ToJson(int? daemonPid) => new()
    {
        ["kind"] = Kind,
        ["installed"] = Installed,
        ["unitFile"] = UnitFile,
        ["loaded"] = Loaded,
        ["active"] = Active,
        ["mainPid"] = MainPid,
        ["domain"] = Domain,
        ["runsThisDaemon"] = daemonPid is { } pid && Runs(pid),
        ["failure"] = Failure,
    };
}

/// <summary>The result of one service-manager action; <see cref="Error"/> names what failed.</summary>
public sealed record ServiceOutcome(bool Ok, string? Domain = null, string? Error = null)
{
    public static ServiceOutcome Failed(string error) => new(false, null, error);
}

/// <summary>
/// The user service manager behind <c>abl daemon start|stop</c> when a service is installed
/// (008 FR-008, plan B3). The manager only starts and stops the job; the lifecycle core
/// decides when to use it: <c>start</c> only for a service that serves this store and port
/// (plan B3 "Service handoff"), <c>stop</c> only when the service runs the daemon, after the
/// session check. A fake stands in for it in unit tests (AC-20, 2a eng-arch LOW-1).
/// </summary>
public interface IServiceManager
{
    /// <summary><c>systemd</c> or <c>launchd</c>.</summary>
    string Kind { get; }

    /// <summary>The service's state; never throws (an unreachable manager reports it unloaded).</summary>
    ServiceState Query();

    /// <summary>Starts the installed service (launchd: loads it first).</summary>
    ServiceOutcome Start();

    /// <summary>A graceful stop that the manager does not undo (systemd stop, launchd bootout).</summary>
    ServiceOutcome Stop();

    /// <summary>Before a forced kill (M11): the manager lets go of the job, so it cannot restart it.</summary>
    ServiceOutcome BootOut();
}

/// <summary>Runs a program at an absolute path: exit code, stdout, stderr; null when it could not start.</summary>
public delegate (int ExitCode, string Output, string Error)? CommandRunner(string program, IReadOnlyList<string> arguments);

/// <summary>The service manager of this host, and the one way its tools are run.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static class ServiceManagers
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// systemd on Linux, launchd on macOS, with the unit or plist where the service lives: the
    /// paths follow the service's environment (<see cref="ServicePlan.ServiceEnvironment"/>), not
    /// this shell's <c>XDG_*</c> values, so <c>install-service</c>, <c>uninstall-service</c>,
    /// <c>start</c>, <c>stop</c> and <c>status</c> all look in one place (T035 review round 1,
    /// eng-arch LOW-1, eng-code LOW-2 and LOW-4).
    /// </summary>
    public static IServiceManager ForService(string home, IReadOnlyDictionary<string, string> shellEnv)
    {
        var env = ServicePlan.ServiceEnvironment(shellEnv);
        return OperatingSystem.IsMacOS()
            ? new LaunchdAgent(LaunchdAgent.StatePlist(DaemonPaths.StateDir(home, env)), LaunchdAgent.LoginPlist(home), UnixIdentity.EffectiveUserId, Run)
            : new SystemdUserService(SystemdUserService.UnitPath(home, env), Run);
    }

    /// <summary>
    /// Runs <paramref name="program"/>, which must be an absolute path (like the process-table
    /// rule: a bare name would be resolved through the cwd and <c>PATH</c>), with a 30 s bound.
    /// </summary>
    public static (int ExitCode, string Output, string Error)? Run(string program, IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(program) || !File.Exists(program))
        {
            return null;
        }

        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "", $"{Path.GetFileName(program)} did not finish within {CommandTimeout.TotalSeconds:0} s");
            }

            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The first of <paramref name="candidates"/> that exists, else the first (so the error names it).</summary>
    internal static string Locate(params string[] candidates) => candidates.FirstOrDefault(File.Exists) ?? candidates[0];

    internal static string Describe((int ExitCode, string Output, string Error)? result, string what) => result switch
    {
        null => $"{what} could not be run",
        { ExitCode: var code, Error: var error, Output: var output } =>
            $"{what} exited with code {code}" + ((error.Trim() is { Length: > 0 } e ? e : output.Trim()) is { Length: > 0 } text
                ? $": {DaemonLog.Clean(text.Length > 300 ? text[..300] : text)}"
                : ""),
    };
}

/// <summary>A systemd user unit (<c>~/.config/systemd/user/abl-daemon.service</c>) driven by <c>systemctl --user</c>.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SystemdUserService(string unitPath, CommandRunner run) : IServiceManager
{
    private readonly string _systemctl = ServiceManagers.Locate("/usr/bin/systemctl", "/bin/systemctl");

    public string Kind => "systemd";

    public string UnitPath() => unitPath;

    /// <summary><c>$XDG_CONFIG_HOME/systemd/user</c> (an absolute value only), else <c>~/.config/systemd/user</c>, plus the unit name.</summary>
    public static string UnitPath(string home, IReadOnlyDictionary<string, string> env)
    {
        var config = env.TryGetValue("XDG_CONFIG_HOME", out var xdg) && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(home, ".config");
        return Path.Combine(config, "systemd", "user", ServiceUnits.SystemdName);
    }

    public ServiceState Query()
    {
        string? text = null;
        try
        {
            text = File.Exists(unitPath) ? File.ReadAllText(unitPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var shown = Systemctl(
            "show", ServiceUnits.SystemdName, "-p", "LoadState", "-p", "ActiveState", "-p", "SubState", "-p", "Result", "-p", "MainPID");
        var values = shown is { ExitCode: 0, Output: var output }
            ? output.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.Ordinal)
            : [];
        var activeState = values.GetValueOrDefault("ActiveState");
        var subState = values.GetValueOrDefault("SubState");
        var active = activeState is "active" or "reloading" or "deactivating";
        var pid = int.TryParse(values.GetValueOrDefault("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : (int?)null;
        return new ServiceState(
            Kind,
            text is null ? null : unitPath,
            values.GetValueOrDefault("LoadState") == "loaded",
            active,
            active ? pid : null,
            text is null ? null : ServiceUnits.ReadSystemdExecStart(text),
            text is null ? null : ServiceUnits.ReadSystemdDotnetRoot(text),
            null)
        {
            Failure = activeState == "failed" || subState == "auto-restart"
                ? $"ActiveState={activeState}, SubState={subState}, Result={values.GetValueOrDefault("Result") ?? "?"}"
                : null,
        };
    }

    public ServiceOutcome Start() => Act("start", ServiceUnits.SystemdName);

    public ServiceOutcome Stop() => Act("stop", ServiceUnits.SystemdName);

    // A queued stop marks the unit as stopping, so Restart=on-failure does not restart it
    // after the kill that follows.
    public ServiceOutcome BootOut() => Act("stop", "--no-block", ServiceUnits.SystemdName);

    /// <summary>Writes the unit, reloads the manager and enables (or disables) it at login.</summary>
    public IReadOnlyList<string> Install(string unit, bool enableAtLogin)
    {
        ServiceFiles.WriteAtomically(unitPath, unit);
        var problems = new List<string>();
        if (Act("daemon-reload") is { Ok: false, Error: { } reload })
        {
            problems.Add(reload);
        }

        if (Act(enableAtLogin ? "enable" : "disable", ServiceUnits.SystemdName) is { Ok: false, Error: { } enable } && enableAtLogin)
        {
            problems.Add(enable);
        }

        return problems;
    }

    /// <summary>Disables and removes the unit; returns the removed path, if any.</summary>
    public IReadOnlyList<string> Uninstall()
    {
        _ = Act("disable", ServiceUnits.SystemdName);
        var removed = ServiceFiles.Remove(unitPath) ? new[] { unitPath } : [];
        _ = Act("daemon-reload");
        return removed;
    }

    private (int ExitCode, string Output, string Error)? Systemctl(params string[] arguments) => run(_systemctl, ["--user", .. arguments]);

    private ServiceOutcome Act(params string[] arguments)
    {
        var result = Systemctl(arguments);
        return result is { ExitCode: 0 }
            ? new ServiceOutcome(true)
            : ServiceOutcome.Failed(ServiceManagers.Describe(result, $"systemctl --user {string.Join(' ', arguments)}"));
    }
}

/// <summary>
/// A launchd agent driven by <c>launchctl</c> (plan B3, M11). Without <c>--enable-at-login</c>
/// the plist lives in the state dir and is loaded by <c>start</c>; with it, in
/// <c>~/Library/LaunchAgents</c>, which launchd loads at login. <c>start</c> bootstraps into
/// <c>gui/&lt;uid&gt;</c> and falls back to <c>user/&lt;uid&gt;</c> (a session without a GUI, such as SSH).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class LaunchdAgent(string statePlist, string loginPlist, uint uid, CommandRunner run) : IServiceManager
{
    private const string Launchctl = "/bin/launchctl";

    public string Kind => "launchd";

    private string[] Domains => [$"gui/{uid}", $"user/{uid}"];

    public static string StatePlist(string stateDir) => Path.Combine(stateDir, "launchd", ServiceUnits.LaunchdLabel + ".plist");

    public static string LoginPlist(string home) => Path.Combine(home, "Library", "LaunchAgents", ServiceUnits.LaunchdLabel + ".plist");

    /// <summary>Where <c>install-service</c> writes the plist.</summary>
    public string PlistPath(bool enableAtLogin) => enableAtLogin ? loginPlist : statePlist;

    public ServiceState Query()
    {
        var file = File.Exists(loginPlist) ? loginPlist : File.Exists(statePlist) ? statePlist : null;
        string? text = null;
        try
        {
            text = file is null ? null : File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var output = text is null ? null : ServiceUnits.ReadLaunchdOutput(text);
        foreach (var domain in Domains)
        {
            if (run(Launchctl, ["print", $"{domain}/{ServiceUnits.LaunchdLabel}"]) is { ExitCode: 0, Output: var printed })
            {
                var pid = PidLine().Match(printed) is { Success: true } m
                    && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : (int?)null;
                return new ServiceState(Kind, file, true, pid is not null, pid, Exec(text), Root(text), domain)
                {
                    Failure = pid is null ? LastFailure(printed) : null,
                    StoppedAfterExit = pid is null && StoppedLine().IsMatch(printed)
                        && LastExitLine().Match(printed) is { Success: true } exit && exit.Groups[1].Value == "0",
                    OutputFile = output,
                };
            }
        }

        return new ServiceState(Kind, file, false, false, null, Exec(text), Root(text), null) { OutputFile = output };
    }

    /// <summary>A non-zero <c>last exit code</c> or a <c>last terminating signal</c> in <c>launchctl print</c>, or null.</summary>
    internal static string? LastFailure(string printed) =>
        LastExitLine().Match(printed) is { Success: true } exit && exit.Groups[1].Value != "0"
            ? DaemonLog.Clean($"last exit code = {exit.Groups[1].Value}{exit.Groups[2].Value.TrimEnd()}")
            : LastSignalLine().Match(printed) is { Success: true } signal
                ? DaemonLog.Clean($"last terminating signal = {signal.Groups[1].Value.Trim()}")
                : null;

    public ServiceOutcome Start()
    {
        var state = Query();
        if (state.Loaded)
        {
            return Kickstart(state.Domain!);
        }

        if (state.UnitFile is not { } plist)
        {
            return ServiceOutcome.Failed("no launchd plist is installed");
        }

        string? error = null;
        foreach (var domain in Domains)
        {
            var result = run(Launchctl, ["bootstrap", domain, plist]);
            if (result is { ExitCode: 0 })
            {
                return new ServiceOutcome(true, domain);
            }

            error ??= ServiceManagers.Describe(result, $"launchctl bootstrap {domain}");
        }

        return ServiceOutcome.Failed(error!);
    }

    public ServiceOutcome Stop() => BootOut();

    public ServiceOutcome BootOut()
    {
        var state = Query();
        if (!state.Loaded)
        {
            return new ServiceOutcome(true);
        }

        var result = run(Launchctl, ["bootout", $"{state.Domain}/{ServiceUnits.LaunchdLabel}"]);
        return result is { ExitCode: 0 }
            ? new ServiceOutcome(true, state.Domain)
            : ServiceOutcome.Failed(ServiceManagers.Describe(result, $"launchctl bootout {state.Domain}/{ServiceUnits.LaunchdLabel}"));
    }

    /// <summary>Writes the plist where <paramref name="enableAtLogin"/> puts it and removes one left at the other place.</summary>
    public IReadOnlyList<string> Install(string plist, bool enableAtLogin)
    {
        ServiceFiles.WriteAtomically(PlistPath(enableAtLogin), plist);
        ServiceFiles.Remove(PlistPath(!enableAtLogin));
        return [];
    }

    /// <summary>Unloads the job when it is loaded and removes both plists; returns the removed paths.</summary>
    public IReadOnlyList<string> Uninstall()
    {
        _ = BootOut();
        return [.. new[] { loginPlist, statePlist }.Where(ServiceFiles.Remove)];
    }

    private ServiceOutcome Kickstart(string domain)
    {
        var result = run(Launchctl, ["kickstart", $"{domain}/{ServiceUnits.LaunchdLabel}"]);
        return result is { ExitCode: 0 }
            ? new ServiceOutcome(true, domain)
            : ServiceOutcome.Failed(ServiceManagers.Describe(result, $"launchctl kickstart {domain}/{ServiceUnits.LaunchdLabel}"));
    }

    private static IReadOnlyList<string>? Exec(string? plist) => plist is null ? null : ServiceUnits.ReadLaunchdExecStart(plist);

    private static string? Root(string? plist) => plist is null ? null : ServiceUnits.ReadLaunchdDotnetRoot(plist);

    [GeneratedRegex(@"(?m)^\s*pid = (\d+)\s*$")]
    private static partial Regex PidLine();

    [GeneratedRegex(@"(?m)^\s*state = not running\s*$")]
    private static partial Regex StoppedLine();

    [GeneratedRegex(@"(?m)^\s*last exit code = (-?\d+)([^\n]*)$")]
    private static partial Regex LastExitLine();

    [GeneratedRegex(@"(?m)^\s*last terminating signal = ([^\n]+)$")]
    private static partial Regex LastSignalLine();
}

/// <summary>Writing and removing unit files: a temp file in the same directory, renamed into place.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class ServiceFiles
{
    public static void WriteAtomically(string path, string text)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = $"{path}.tmp-{Environment.ProcessId}";
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        }))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static bool Remove(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
