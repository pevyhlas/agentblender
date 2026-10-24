using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Abl.Cli.Host;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T035/T039: the lifecycle core over fakes. A test holds the store lock and writes the
/// record for a fake pid; an in-test HTTP stand-in answers <c>/healthz</c>,
/// <c>session_list</c> and <c>daemon_status</c>; a fake process table and a fake service
/// manager record every signal and call, so no real process is ever signalled. Covers the
/// end-to-end "table unreadable" path of <c>stop</c> (with the dropped sessions' recovery
/// lines in text mode), AC-20 on the service path (the session refusal comes first; a forced
/// stop boots the service out before any signal), the CLI form of <c>daemon_status</c>, and the
/// service half of <c>start</c> (T035 review round 1, eng-test M-1, eng-code MEDIUM-1): the
/// handoff to an installed service for this store, the readiness wait against the lock holder,
/// the stop after a failed start and the fast failure of a job that dies before the lock.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LifecycleCoreTests : IAsyncLifetime
{
    private const int DaemonPid = 3_999_917;
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse("2026-10-21T09:00:00Z", CultureInfo.InvariantCulture);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-lcc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _events = [];
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _server;
    private StoreLock? _lock;
    private int _port;

    private string Store => Path.Combine(_root, "store");

    /// <summary>A stand-in Blender for <c>start</c>'s preflight (it answers <c>--version</c> only).</summary>
    private string Blender => Path.Combine(_root, "blender");

    /// <summary>What the stand-in daemon's session_list answers.</summary>
    private JsonArray Sessions { get; set; } = [];

    /// <summary>What the stand-in daemon's daemon_status answers.</summary>
    private JsonObject Status { get; } = new()
    {
        ["running"] = true,
        ["version"] = "0.1.0",
        ["pid"] = DaemonPid,
        ["workers"] = new JsonObject { ["ready"] = 1, ["floor"] = 1, ["lastSpawnAt"] = null, ["lastWorkerError"] = null },
        ["sessions"] = 0,
        ["blender"] = new JsonObject { ["version"] = "5.2.1", ["support"] = "reference", ["allowUnsupported"] = false, ["path"] = "/opt/blender/blender" },
    };

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root, Private);
        Directory.CreateDirectory(Store, Private);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _server = Task.Run(ServeAsync);
        File.WriteAllText(Blender, "#!/bin/sh\nprintf 'Blender 5.2.1\\n\\tbuild hash: 0123456789ab\\n'\n");
        File.SetUnixFileMode(Blender, Private);
        _lock = StoreLock.TryAcquire(Store, TimeSpan.Zero) ?? throw new InvalidOperationException("the test could not take the store lock");
        new DaemonState(DaemonPid, _port, DaemonPaths.UrlFor(_port), "0.1.0", StartedAt, "sha256:x", null, null).Write(Store);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _lock?.Dispose();
        await _stop.CancelAsync();
        _listener?.Stop();
        await (_server ?? Task.CompletedTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task StopWithAnUnreadableProcessTableReportsDaemonWorkersAndKeepsTheRecoveryLines()
    {
        // 2a round 3 / round-4 eng-code L-4: the daemon has no group of its own, so only the
        // process table can show its workers are gone; when it cannot be read, stop says so
        // (exit 1, daemon.workers) and still prints how to recover each dropped session.
        Sessions = [new JsonObject { ["id"] = "s1", ["head"] = "h1" }];
        var processes = new FakeProcesses(_events) { Table = ProcessTree.Table.Unknown("the process table cannot be read (no ps at /bin/ps)") };
        var (code, stdout, stderr) = await RunAsync(core => core.StopAsync(json: false, force: true), processes, new FakeServices(_events, processes));

        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Contains("  dropped session s1; recover with: abl checkpoint branch h1", stderr);
        Assert.Contains($"abl: daemon.workers: daemon pid {DaemonPid} stopped, but its workers could not be verified gone: the process table cannot be read", stderr);
        Assert.Equal([$"signal {DaemonPid} 15"], _events);

        // The --json form carries the same verdict.
        _events.Clear();
        processes.Alive = true;
        var (jsonCode, json, _) = await RunAsync(core => core.StopAsync(json: true, force: true), processes, new FakeServices(_events, processes));
        Assert.Equal(1, jsonCode);
        var body = JsonNode.Parse(json)!;
        Assert.Equal("daemon.workers", body["error"]!["check"]!.GetValue<string>());
        Assert.True(body["stopped"]!.GetValue<bool>());
        Assert.False(body["workersVerified"]!.GetValue<bool>());
        Assert.Equal("h1", body["droppedSessions"]![0]!["lastCheckpoint"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartRefusesUnsafeStateBeforePreparingItsRuntime(bool symlink)
    {
        var inputs = Inputs();
        var state = DaemonPaths.StateDir(inputs.Home, inputs.Env);
        Directory.CreateDirectory(Path.GetDirectoryName(state)!, Private);
        var target = symlink ? Path.Combine(_root, "state-target") : state;
        var mode = symlink ? Private : Private | UnixFileMode.GroupRead;
        Directory.CreateDirectory(target, mode);
        if (symlink) Directory.CreateSymbolicLink(state, target);
        inputs = inputs with
        {
            Env = inputs.Env.ToDictionary(pair => pair.Key,
                pair => pair.Key == "ABL_RUNTIME_DIR" ? Path.Combine(state, "run") : pair.Value),
        };
        using var stdout = new StringWriter();
        var processes = new FakeProcesses(_events);
        var lifecycle = new DaemonLifecycle(new LifecycleHost(inputs, stdout, TextWriter.Null,
            processes, new FakeServices(_events, processes)));

        var code = await lifecycle.StartAsync(json: true, TimeSpan.FromSeconds(1));

        Assert.Equal(1, code);
        Assert.Equal("runtime.dir", JsonNode.Parse(stdout.ToString())!["error"]!["check"]!.GetValue<string>());
        Assert.Contains(symlink ? "is a symlink" : "must be 0700", stdout.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Equal(mode, File.GetUnixFileMode(target));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task OnTheServicePathActiveSessionsRefuseStopBeforeTheManagerIsAsked()
    {
        // AC-20: the session check comes first, even when a service runs the daemon.
        Sessions = [new JsonObject { ["id"] = "s1", ["head"] = "h1" }, new JsonObject { ["id"] = "s2", ["head"] = "h2" }];
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = true };
        var (code, stdout, stderr) = await RunAsync(core => core.StopAsync(json: true, force: false), processes, services);

        Assert.Equal(1, code);
        var refused = JsonNode.Parse(stdout)!;
        Assert.Equal(["s1", "s2"], refused["sessions"]!.AsArray().Select(s => s!["session"]!.GetValue<string>()));
        Assert.Equal(["h1", "h2"], refused["sessions"]!.AsArray().Select(s => s!["lastCheckpoint"]!.GetValue<string>()));
        Assert.Contains("  s1 → h1", stderr);
        Assert.Empty(_events);
        Assert.True(processes.Alive);
    }

    [Fact]
    public async Task AServiceRunDaemonIsStoppedThroughItsManagerNotBySignal()
    {
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = true };
        var (code, stdout, _) = await RunAsync(core => core.StopAsync(json: true, force: false), processes, services);

        Assert.Equal(0, code);
        var stopped = JsonNode.Parse(stdout)!;
        Assert.True(stopped["workersVerified"]!.GetValue<bool>());
        Assert.Equal("fake", stopped["service"]!["kind"]!.GetValue<string>());
        Assert.Equal(["service stop"], _events);
    }

    [Fact]
    public async Task AForcedStopBootsTheServiceOutBeforeAnySignal()
    {
        // M11: otherwise a restart policy (KeepAlive, Restart=on-failure) brings the daemon back.
        Sessions = [new JsonObject { ["id"] = "s1", ["head"] = "h1" }];
        var processes = new FakeProcesses(_events) { IgnoreTerm = true };
        var services = new FakeServices(_events, processes) { RunsDaemon = true, BootOutKills = false };
        var (code, _, stderr) = await RunAsync(core => core.StopAsync(json: false, force: true), processes, services);

        Assert.Equal(0, code);
        Assert.Equal("service bootout", _events[0]);
        Assert.Contains($"signal {DaemonPid} 15", _events);
        Assert.Contains($"signal {DaemonPid} 9", _events);
        Assert.True(_events.IndexOf("service bootout") < _events.IndexOf($"signal {DaemonPid} 15"));
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task StopFromUnderTheDaemonSignalsNothing()
    {
        // A script one of the daemon's workers runs: stop would end itself before the daemon.
        var table = new ProcessTree.Table(
            [new(DaemonPid + 1, DaemonPid, "blender --background"), new(Environment.ProcessId, DaemonPid + 1, "abl daemon stop --force")],
            null);
        var processes = new FakeProcesses(_events) { Table = table };
        var services = new FakeServices(_events, processes) { RunsDaemon = false };
        var (code, stdout, _) = await RunAsync(core => core.StopAsync(json: true, force: true), processes, services);

        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("daemon.workers", error["check"]!.GetValue<string>());
        Assert.Contains($"under daemon pid {DaemonPid}", error["message"]!.GetValue<string>());
        var body = JsonNode.Parse(stdout)!;
        Assert.False(body["stopped"]!.GetValue<bool>());
        Assert.Equal(DaemonPid, body["pid"]!.GetValue<int>());
        Assert.True(body["forced"]!.GetValue<bool>());
        Assert.Empty(_events);
        Assert.True(processes.Alive);
    }

    [Fact]
    public async Task StopRunAsTheDaemonItselfSignalsNothing()
    {
        var processes = new FakeProcesses(_events) { CallerPid = DaemonPid };
        var services = new FakeServices(_events, processes) { RunsDaemon = false };
        var (code, stdout, _) = await RunAsync(core => core.StopAsync(json: true, force: false), processes, services);

        Assert.Equal(1, code);
        var body = JsonNode.Parse(stdout)!;
        Assert.Equal("daemon.workers", body["error"]!["check"]!.GetValue<string>());
        Assert.Contains($"as daemon pid {DaemonPid} itself", body["error"]!["message"]!.GetValue<string>());
        Assert.False(body["stopped"]!.GetValue<bool>());
        Assert.False(body["forced"]!.GetValue<bool>());
        Assert.Empty(_events);
        Assert.True(processes.Alive);
    }

    [Fact]
    public async Task StopFromTheDaemonsOwnGroupSignalsNothing()
    {
        // The record's group is the daemon's own (setsid), and this command runs in it.
        new DaemonState(DaemonPid, _port, DaemonPaths.UrlFor(_port), "0.1.0", StartedAt, "sha256:x", null, DaemonPid).Write(Store);
        var processes = new FakeProcesses(_events) { CallerGroup = DaemonPid };
        var services = new FakeServices(_events, processes) { RunsDaemon = false };
        var (code, stdout, _) = await RunAsync(core => core.StopAsync(json: true, force: true), processes, services);

        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("daemon.workers", error["check"]!.GetValue<string>());
        Assert.Contains($"in process group {DaemonPid}", error["message"]!.GetValue<string>());
        Assert.Empty(_events);
        Assert.True(processes.Alive);
    }

    [Fact]
    public async Task AProcessPathDaemonIsSignalledAndTheServiceIsLeftAlone()
    {
        // An installed service that does not run this daemon (another pid) is not this stop's business.
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = false };
        var (code, stdout, _) = await RunAsync(core => core.StopAsync(json: true, force: false), processes, services);

        Assert.Equal(0, code);
        Assert.Null(JsonNode.Parse(stdout)!["service"]);
        Assert.Equal([$"signal {DaemonPid} 15"], _events);
    }

    [Fact]
    public async Task StatusIsTheDaemonStatusOpPlusOnlyALocalObject()
    {
        // AC-27 on the CLI side: the op's answer, unchanged, plus `local` (lifecycle files and
        // the service state). The pid is confirmed against /healthz first.
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = true };
        var (code, stdout, stderr) = await RunAsync(core => core.StatusAsync(json: true), processes, services);

        Assert.True(code == 0, stderr);
        var status = JsonNode.Parse(stdout)!.AsObject();
        var local = status["local"]!.AsObject();
        status.Remove("local");
        Assert.True(JsonNode.DeepEquals(Status, status), status.ToJsonString());
        Assert.Equal(DaemonPaths.StateFile(Store), local["stateFile"]!.GetValue<string>());
        Assert.Equal(DaemonPaths.LockFile(Store), local["lockFile"]!.GetValue<string>());
        Assert.True(local["service"]!["runsThisDaemon"]!.GetValue<bool>());
        Assert.Empty(_events);

        var (textCode, text, _) = await RunAsync(core => core.StatusAsync(json: false), processes, services);
        Assert.Equal(0, textCode);
        Assert.Contains($"running pid {DaemonPid} at {DaemonPaths.UrlFor(_port)} (version 0.1.0, 1 ready worker(s), 0 session(s))", text);
        Assert.Contains("  blender 5.2.1 (reference) at /opt/blender/blender", text);
    }

    [Fact]
    public async Task AC11_TheCliCarriesTheDaemonStatusGpuObjectOfAMetalWorkerUnchanged()
    {
        // 009 T020/T022, the CLI half of AC-11: the op core's gpu object (a fake Metal hello)
        // is what `abl daemon status --json` prints, and the text form names backend and device.
        await using var h = new GpuHarness(HostPlatform.MacOS);
        h.LocalScript.Gpu = FakeGpu.Metal();
        var session = await h.Sessions.OpenAsync("status", default);
        var (_, rest) = await h.RestAsync("daemon_status", []);
        await h.Sessions.CloseAsync(session.Id);
        Status["gpu"] = rest["gpu"]!.DeepClone();

        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes);
        var (code, stdout, stderr) = await RunAsync(core => core.StatusAsync(json: true), processes, services);

        Assert.True(code == 0, stderr);
        var cli = JsonNode.Parse(stdout)!["gpu"]!;
        Assert.True(JsonNode.DeepEquals(rest["gpu"], cli), $"REST {rest["gpu"]!.ToJsonString()}\nCLI {cli.ToJsonString()}");
        Assert.Equal("metal", cli["backend"]!.GetValue<string>());
        Assert.Null(cli["metalrt"]);

        var (_, text, _) = await RunAsync(core => core.StatusAsync(json: false), processes, services);
        Assert.Contains($"  gpu metal ({FakeGpu.MetalName})", text);
    }

    [Fact]
    public async Task HostVerbsReadOnlyTheReadOps()
    {
        // Plan B1: a host verb posts to /api/ops only through ReadOpAsync, which admits the read
        // ops alone; the stand-in would answer anything.
        using var http = new HttpClient();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DaemonLifecycle.ReadOpAsync(http, DaemonPaths.UrlFor(_port), "session_open"));
        Assert.Contains("not session_open", error.Message);
        Assert.NotNull((await DaemonLifecycle.ReadOpAsync(http, DaemonPaths.UrlFor(_port), "daemon_status"))["version"]);
    }

    [Fact]
    public async Task StartHandsOffToTheServiceForThisStoreAndWaitsForItsLockHolder()
    {
        // eng-test M-1 (a): an installed unit that logs to this store's log is started through
        // its manager, nothing is spawned, and "ready" is read from the daemon that then holds
        // the lock (its record's pid confirmed by /healthz).
        await NoDaemonAsync();
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = true, LogFile = ServiceLog, OnStart = BecomeDaemon };
        var (code, stdout, stderr) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(30)), processes, services);

        Assert.True(code == 0, stderr);
        var started = JsonNode.Parse(stdout)!;
        Assert.Equal(DaemonPid, started["pid"]!.GetValue<int>());
        Assert.False(started["alreadyRunning"]!.GetValue<bool>());
        Assert.Equal("fake", started["service"]!["kind"]!.GetValue<string>());
        Assert.Equal(["service start"], _events);

        // The shell's ABL_* values do not reach a service-run daemon, and start says so.
        Assert.Contains("ABL_BLENDER, ABL_CONFIG, ABL_PORT, ABL_RUNTIME_DIR, ABL_STORE are set in this shell, but the fake service reads the config file only", stderr);
        Assert.Contains("so the daemon it starts does not see them", stderr);

        // eng-code LOW-4: a second start reports the running daemon with its service.
        _events.Clear();
        var (again, secondOut, secondErr) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(30)), processes, services);
        Assert.True(again == 0, secondErr);
        var running = JsonNode.Parse(secondOut)!;
        Assert.True(running["alreadyRunning"]!.GetValue<bool>());
        Assert.Equal("fake", running["service"]!["kind"]!.GetValue<string>());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task AServiceStartThatNeverTakesTheLockIsStoppedAgain()
    {
        // eng-test M-1 (b): the manager reports the job started, but no daemon takes the lock;
        // start fails with daemon.reachable and does not leave the service restarting.
        await NoDaemonAsync();
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = true, LogFile = ServiceLog };
        var (code, stdout, _) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(2)), processes, services);

        Assert.Equal(1, code);
        Assert.Equal("daemon.reachable", JsonNode.Parse(stdout)!["error"]!["check"]!.GetValue<string>());
        Assert.Equal(["service start", "service stop"], _events);
    }

    [Fact]
    public async Task AServiceJobThatDiesBeforeTheLockFailsFastWithItsOutput()
    {
        // eng-code MEDIUM-1: a daemon that exits before its own redirect (a moved ExecStart, a
        // runtime without ASP.NET Core 10) is reported by the manager as down; start fails at
        // once with service.state and the manager's output, not after the whole --timeout.
        await NoDaemonAsync();
        var output = Path.Combine(_root, "daemon.out");
        File.WriteAllText(output, "You must install or update .NET to run this application.\n");
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes)
        {
            RunsDaemon = true,
            LogFile = ServiceLog,
            Failure = "last exit code = 150",
            OutputFile = output,
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (code, stdout, stderr) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(60)), processes, services);

        Assert.Equal(1, code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        var error = JsonNode.Parse(stdout)!;
        Assert.Equal("service.state", error["error"]!["check"]!.GetValue<string>());
        Assert.Contains("exited before it took the store lock (last exit code = 150)", error["error"]!["message"]!.GetValue<string>());
        Assert.Contains(output, error["error"]!["hint"]!.GetValue<string>());
        Assert.Equal(["You must install or update .NET to run this application."], error["output"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Contains("You must install or update .NET", stderr);
        Assert.Equal(["service start", "service stop"], _events);
    }

    [Fact]
    public async Task AServiceWhoseConfigFileNamesThisStoreAndPortHandsOff()
    {
        // T035 review round 3 (eng-test MEDIUM-1): the production unit shape (env -i with the
        // identity variables and DOTNET_ROOT, no ABL_*), whose config file under the unit's
        // HOME names this store and port, hands off and waits for the service's daemon.
        await NoDaemonAsync();
        WriteServiceConfig(_port, Store);
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes)
        {
            RunsDaemon = true,
            LogFile = ServiceLog,
            ExecStart = ServiceExecStart(ServiceLog),
            OnStart = BecomeDaemon,
        };
        var (code, stdout, stderr) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(30)), processes, services);

        Assert.True(code == 0, stderr);
        var started = JsonNode.Parse(stdout)!;
        Assert.Equal(DaemonPid, started["pid"]!.GetValue<int>());
        Assert.Equal("fake", started["service"]!["kind"]!.GetValue<string>());
        Assert.Equal(["service start"], _events);
    }

    [Fact]
    public async Task AServiceWhoseConfigFileNamesAnotherPortIsRefusedBeforeItStarts()
    {
        // T035 review rounds 2 and 3 (eng-code MEDIUM, eng-test MEDIUM-1): the unit's log
        // matches this store, but the config file its env -i daemon reads (under the unit's
        // HOME; the unit carries no ABL_*) names another port than this shell's ABL_PORT.
        // start refuses at once, names both ports, and never asks the manager.
        await NoDaemonAsync();
        var otherPort = FreePort();
        WriteServiceConfig(otherPort, Store);
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes)
        {
            RunsDaemon = true,
            LogFile = ServiceLog,
            ExecStart = ServiceExecStart(ServiceLog),
            OnStart = () => BecomeDaemon(otherPort),
        };
        var (code, stdout, _) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(30)), processes, services);

        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("daemon.port", error["check"]!.GetValue<string>());
        Assert.Contains($"on port {otherPort}", error["message"]!.GetValue<string>());
        Assert.Contains($"names port {_port}", error["message"]!.GetValue<string>());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task AServiceInstalledForThisStoreWhoseConfigFileNowNamesAnotherStoreIsRefused()
    {
        // T035 review round 3 (eng-arch MEDIUM-1): the unit still logs to this store's log (it
        // was installed for it), but its config file now names another store on the same port.
        // Its daemon would lock that store and answer "started" on this port, so start refuses
        // with service.state before the manager is asked, naming both stores.
        await NoDaemonAsync();
        var otherStore = Path.Combine(_root, "other-store");
        Directory.CreateDirectory(otherStore, Private);
        WriteServiceConfig(_port, otherStore);
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes)
        {
            RunsDaemon = true,
            LogFile = ServiceLog,
            ExecStart = ServiceExecStart(ServiceLog),
            OnStart = () => BecomeDaemon(_port, otherStore),
        };
        var (code, stdout, stderr) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(30)), processes, services);

        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("service.state", error["check"]!.GetValue<string>());
        Assert.Contains($"installed for store {Store}", error["message"]!.GetValue<string>());
        Assert.Contains($"now names store {otherStore}", error["message"]!.GetValue<string>());
        Assert.Contains("unset ABL_STORE", error["hint"]!.GetValue<string>());

        // Nothing is started on a refusal, so the ABL_* warning says what the service would not see
        // (round-4 eng-code nit).
        Assert.Contains("so its daemon would not see them either", stderr);
        Assert.DoesNotContain("the daemon it starts", stderr);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task AServiceWhoseConfigFileNamesThisStoreHandsOffWhateverLogItWasInstalledWith()
    {
        // T035 review round 3: once the unit's configuration resolves, it decides the handoff,
        // not the log. A unit installed for another store (another log) whose config file now
        // names this store and port serves this store, so start hands off to it.
        await NoDaemonAsync();
        WriteServiceConfig(_port, Store);
        var otherLog = DaemonPaths.LogFile(Path.Combine(_root, "state", "agentblender"), Path.Combine(_root, "other-store"));
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes)
        {
            RunsDaemon = true,
            LogFile = otherLog,
            ExecStart = ServiceExecStart(otherLog),
            OnStart = BecomeDaemon,
        };
        var (code, _, stderr) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(30)), processes, services);

        Assert.True(code == 0, stderr);
        Assert.Equal(["service start"], _events);
        Assert.DoesNotContain("serves another store", stderr);
    }

    [Fact]
    public async Task AHealthyServiceDaemonOnAnotherPortIsNeverStoppedByTheStartThatStartedIt()
    {
        // T035 review round 2 (eng-code MEDIUM): when the unit's configuration cannot be
        // resolved ahead (no env -i here), the service's daemon binds a port other than this
        // shell's ABL_PORT. The readiness wait sees a healthy holder on another port: start
        // fails with daemon.port, says the daemon is left running, and does not stop it.
        await NoDaemonAsync();
        var otherPort = FreePort();
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = true, LogFile = ServiceLog, OnStart = () => BecomeDaemon(otherPort) };
        var (code, stdout, stderr) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(30)), processes, services);

        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("daemon.port", error["check"]!.GetValue<string>());
        Assert.Contains($"at port {otherPort}", error["message"]!.GetValue<string>());
        Assert.Contains("left running", error["message"]!.GetValue<string>());
        Assert.DoesNotContain("already runs", stderr);
        Assert.Equal(["service start"], _events);
        Assert.True(processes.Alive);
    }

    [Fact]
    public void AnotherStoresServiceNeitherHandsOffNorRefuses()
    {
        // T035 review round 4 (eng-test MEDIUM-1): the third outcome of the handoff rule. The
        // unit's env -i configuration resolves to another store, and the unit logs to that
        // store's log, so it is another store's service: no handoff (the readiness wait would
        // report the other store's daemon as "started") and no refusal (another store's
        // service never blocks this store's start). How StartAsync acts on this outcome (the
        // warning, then the process path) is carried under T051 (round-5 eng-test LOW-1).
        var otherStore = Path.Combine(_root, "other-store");
        Directory.CreateDirectory(otherStore, Private);
        WriteServiceConfig(_port, otherStore);
        var otherLog = DaemonPaths.LogFile(Path.Combine(_root, "state", "agentblender"), otherStore);
        var (handoff, options) = Decide(new FakeServices(_events, new FakeProcesses(_events)) { LogFile = otherLog, ExecStart = ServiceExecStart(otherLog) });

        Assert.Null(handoff.Options);
        Assert.Null(handoff.Refusal);
        Assert.Equal($"serves store {otherStore} (from the config file it reads), not this shell's {options.StoreDir}", handoff.Elsewhere);
        Assert.Empty(_events);
    }

    [Fact]
    public void AnUnresolvableServiceConfigurationFallsBackOnTheUnitsLog()
    {
        // T035 review round 3 LOW-1 (eng-test), carried under T051: when the unit's env -i
        // configuration does not resolve (here its config file is not strict JSON), the log
        // stands in for the store. A unit that logs to this store's log hands off with this
        // shell's options; one that logs elsewhere is another store's service.
        WriteServiceConfig(_port, Store);
        File.WriteAllText(Path.Combine(ServiceHome, ".config", "agentblender", "config.json"), "{\"version\": 1,}");
        var processes = new FakeProcesses(_events);

        var (here, options) = Decide(new FakeServices(_events, processes) { LogFile = ServiceLog, ExecStart = ServiceExecStart(ServiceLog) });
        Assert.Same(options, here.Options);
        Assert.Null(here.Refusal);
        Assert.Null(here.Elsewhere);

        var otherLog = DaemonPaths.LogFile(Path.Combine(_root, "state", "agentblender"), Path.Combine(_root, "other-store"));
        var (elsewhere, _) = Decide(new FakeServices(_events, processes) { LogFile = otherLog, ExecStart = ServiceExecStart(otherLog) });
        Assert.Null(elsewhere.Options);
        Assert.Null(elsewhere.Refusal);
        Assert.StartsWith($"logs to {otherLog}, not this store's {ServiceLog}", elsewhere.Elsewhere);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task AFailedHandoffToAServiceWithAnotherLogReportsThatLog()
    {
        // T035 review round 4 (eng-test LOW-1): a unit installed with another log whose config
        // file names this store hands off; when its job then never takes the lock, the failure
        // shows the log that service writes, not this shell's.
        await NoDaemonAsync();
        WriteServiceConfig(_port, Store);
        var otherLog = DaemonPaths.LogFile(Path.Combine(_root, "state", "agentblender"), Path.Combine(_root, "other-store"));
        var processes = new FakeProcesses(_events);
        var services = new FakeServices(_events, processes) { RunsDaemon = true, LogFile = otherLog, ExecStart = ServiceExecStart(otherLog) };
        var (code, stdout, _) = await RunAsync(core => core.StartAsync(json: true, TimeSpan.FromSeconds(2)), processes, services);

        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!;
        Assert.Equal("daemon.reachable", error["error"]!["check"]!.GetValue<string>());
        Assert.Equal(otherLog, error["logFile"]!.GetValue<string>());
        Assert.Equal(["service start", "service stop"], _events);
    }

    /// <summary>A loopback port nothing listens on (taken and released at once).</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The unit's own <c>HOME</c>: it anchors the config file the service's daemon reads.</summary>
    private string ServiceHome => Path.Combine(_root, "service-home");

    /// <summary>
    /// The <c>ExecStart</c> words <c>install-service</c> writes (<see cref="ServiceUnits.Define"/>
    /// over <see cref="ServicePlan.UnitVariables"/>: <c>env -i</c>, the identity variables and
    /// <c>DOTNET_ROOT</c>, no <c>ABL_*</c>), logging to <paramref name="logFile"/>.
    /// </summary>
    private IReadOnlyList<string> ServiceExecStart(string logFile) => ServiceUnits.Define(
        "/fake/abl", [], "/fake/dotnet", logFile, Path.Combine(_root, "state", "agentblender"),
        ServicePlan.UnitVariables(ServiceHome, new Dictionary<string, string> { ["USER"] = "u", ["LOGNAME"] = "u", ["PATH"] = "/usr/bin:/bin" })).ExecStart;

    /// <summary>The service's config file (<c>&lt;unit HOME&gt;/.config/agentblender/config.json</c>, 0600), naming <paramref name="store"/> and <paramref name="port"/>.</summary>
    private void WriteServiceConfig(int port, string store)
    {
        var dir = Path.Combine(ServiceHome, ".config", "agentblender");
        Directory.CreateDirectory(Path.Combine(ServiceHome, ".config"), Private);
        Directory.CreateDirectory(dir, Private);
        var file = Path.Combine(dir, "config.json");
        File.WriteAllText(file, new JsonObject
        {
            ["version"] = 1,
            ["daemon"] = new JsonObject { ["port"] = port },
            ["paths"] = new JsonObject { ["store"] = store },
        }.ToJsonString());
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The per-store log that <c>start</c> computes for <see cref="RunAsync"/>'s inputs.</summary>
    private string ServiceLog => DaemonPaths.LogFile(
        DaemonPaths.StateDir(Path.Combine(_root, "home"), new Dictionary<string, string> { ["XDG_STATE_HOME"] = Path.Combine(_root, "state") }), Store);

    /// <summary>For the start cases: no daemon yet (the lock free, no record, nothing on the port).</summary>
    private async Task NoDaemonAsync()
    {
        _lock?.Dispose();
        _lock = null;
        File.Delete(DaemonPaths.StateFile(Store));
        _listener!.Stop();
        await (_server ?? Task.CompletedTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>What a service-run daemon does once it is up: takes the lock, writes its record and answers on the port.</summary>
    private void BecomeDaemon() => BecomeDaemon(_port);

    /// <summary><see cref="BecomeDaemon()"/> on <paramref name="port"/> and <paramref name="store"/> (the ones the service's configuration names).</summary>
    private void BecomeDaemon(int port, string? store = null)
    {
        store ??= Store;
        _lock = StoreLock.TryAcquire(store, TimeSpan.Zero) ?? throw new InvalidOperationException("the test could not take the store lock");
        new DaemonState(DaemonPid, port, DaemonPaths.UrlFor(port), "0.1.0", StartedAt, "sha256:x", ServiceLog, null).Write(store);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _server = Task.Run(ServeAsync);
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        Func<DaemonLifecycle, Task<int>> verb, FakeProcesses processes, FakeServices services)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await verb(new DaemonLifecycle(new LifecycleHost(Inputs(), stdout, stderr, processes, services)));
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>The handoff rule of <c>start</c> for <paramref name="services"/>' unit, decided directly (no manager call, no spawn), and this shell's options.</summary>
    private (DaemonLifecycle.ServiceHandoff Handoff, AblOptions Options) Decide(FakeServices services)
    {
        var inputs = Inputs();
        var options = AblConfig.Resolve(inputs).Options;
        var core = new DaemonLifecycle(new LifecycleHost(inputs, TextWriter.Null, TextWriter.Null, new FakeProcesses(_events), services));
        return (core.Handoff(services.Query(), options, ServiceLog), options);
    }

    /// <summary>This shell's inputs: every path under the test root, the port of the stand-in daemon.</summary>
    private ConfigInputs Inputs() => new()
    {
        Env = new Dictionary<string, string>
        {
            ["ABL_CONFIG"] = Path.Combine(_root, "config.json"), // absent
            ["ABL_STORE"] = Store,
            ["ABL_RUNTIME_DIR"] = Path.Combine(_root, "run"),
            ["ABL_PORT"] = _port.ToString(CultureInfo.InvariantCulture),
            ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
            ["ABL_BLENDER"] = Blender,
        },
        Home = Path.Combine(_root, "home"),
        Platform = OperatingSystem.IsMacOS() ? HostPlatform.MacOS : HostPlatform.Linux,
        UserId = UnixIdentity.EffectiveUserId,
    };

    /// <summary>A minimal stand-in for the daemon's HTTP surface on this test's port.</summary>
    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            using (client)
            {
                await using var stream = client.GetStream();
                var request = "";
                var buffer = new byte[4096];
                while (!IsComplete(request))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    request += Encoding.ASCII.GetString(buffer, 0, read);
                }

                var path = request.Split(' ') is { Length: > 1 } parts ? parts[1] : "";
                var json = path switch
                {
                    "/healthz" => $"{{\"pid\":{DaemonPid},\"version\":\"0.1.0\",\"readyWorkers\":1,\"sessions\":{Sessions.Count}}}",
                    "/api/ops/session_list" => new JsonObject { ["items"] = Sessions.DeepClone() }.ToJsonString(),
                    "/api/ops/daemon_status" => Status.ToJsonString(),
                    _ => """{"kind":"not_found"}""",
                };
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            }
        }

        static bool IsComplete(string request)
        {
            var end = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            var length = Regex.Match(request[..end], @"(?im)^content-length:\s*(\d+)") is { Success: true } m
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0;
            return request.Length >= end + 4 + length;
        }
    }

    /// <summary>The daemon as a pid that only this fake knows: a signal is recorded, and SIGTERM or SIGKILL ends it.</summary>
    private sealed class FakeProcesses(List<string> events) : IProcessControl
    {
        public bool Alive { get; set; } = true;

        /// <summary>A daemon that does not exit on SIGTERM (only SIGKILL ends it).</summary>
        public bool IgnoreTerm { get; init; }

        public ProcessTree.Table Table { get; init; } = new([], null);

        public bool Signal(int pid, int signal)
        {
            events.Add($"signal {pid} {signal}");
            if (Math.Abs(pid) != DaemonPid)
            {
                return false;
            }

            if (signal == UnixIdentity.SigKill || (signal == UnixIdentity.SigTerm && !IgnoreTerm))
            {
                Alive = false;
            }

            return true;
        }

        public bool Exists(int pid) => pid == DaemonPid && Alive;

        public bool GroupExists(int pgid) => false;

        public DateTimeOffset? StartTime(int pid) => pid == DaemonPid ? StartedAt.AddSeconds(-1) : null;

        public ProcessTree.Table Snapshot() => Table;

        public int CallerPid { get; init; } = Environment.ProcessId;

        public int CallerGroup { get; init; } = Environment.ProcessId;
    }

    /// <summary>An installed service that runs the daemon (or another pid) and records each call.</summary>
    private sealed class FakeServices(List<string> events, FakeProcesses processes) : IServiceManager
    {
        public bool RunsDaemon { get; init; }

        /// <summary>A boot-out that leaves the process to the forced kill that follows.</summary>
        public bool BootOutKills { get; init; } = true;

        /// <summary>The <c>--log-file</c> the unit passes (null: none, so <c>start</c> never hands off).</summary>
        public string? LogFile { get; init; }

        /// <summary>What the service's daemon does when the manager starts it.</summary>
        public Action? OnStart { get; init; }

        /// <summary>What the manager reports about a job that is down after a failure.</summary>
        public string? Failure { get; init; }

        /// <summary>Where the manager writes the job's own output.</summary>
        public string? OutputFile { get; init; }

        /// <summary>The unit's whole <c>ExecStart</c> (null: <c>/fake/abl daemon run</c>, with <see cref="LogFile"/>, and no <c>env -i</c>).</summary>
        public IReadOnlyList<string>? ExecStart { get; init; }

        public string Kind => "fake";

        public ServiceState Query() => new(
            Kind, "/fake/unit", true, true, RunsDaemon ? DaemonPid : DaemonPid + 1,
            ExecStart ?? (LogFile is null ? ["/fake/abl", "daemon", "run"] : ["/fake/abl", "daemon", "run", "--log-file", LogFile]), "/fake/dotnet", null)
        {
            Failure = Failure,
            OutputFile = OutputFile,
        };

        public ServiceOutcome Start()
        {
            events.Add("service start");
            OnStart?.Invoke();
            return new ServiceOutcome(true);
        }

        public ServiceOutcome Stop()
        {
            events.Add("service stop");
            processes.Alive = false;
            return new ServiceOutcome(true);
        }

        public ServiceOutcome BootOut()
        {
            events.Add("service bootout");
            if (BootOutKills)
            {
                processes.Alive = false;
            }

            return new ServiceOutcome(true);
        }
    }
}
