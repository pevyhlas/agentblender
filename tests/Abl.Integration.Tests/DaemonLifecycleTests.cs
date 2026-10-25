using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Lifecycle;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 T038: the daemon lifecycle through the built CLI (<c>dotnet abl.dll</c>, so every
/// detached start also proves the N2 re-spawn) with the <see cref="StubWorker"/> as Blender
/// (unit lane: no Blender ever runs). Each test is isolated (plan M10): its own random port,
/// <c>HOME</c>, <c>XDG_STATE_HOME</c>, <c>ABL_CONFIG</c> (absent), store and runtime dir.
/// AC-17 detach and group-kill survival; AC-18 preflight fail-fast and a crashing worker;
/// AC-19 the store lock; AC-20 the session refusal and no orphans; AC-21 status when not
/// running; N3 the foreground log rule.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class DaemonLifecycleTests : IAsyncLifetime
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root;
    private readonly int _port;
    private StubWorker? _stub;

    public DaemonLifecycleTests()
    {
        // Short: the runtime dir's socket paths must fit sun_path (103 bytes on macOS).
        _root = Path.Combine(Path.GetTempPath(), "abl-lc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root, Private);
        Directory.CreateDirectory(Path.Combine(_root, "home"), Private);
        _port = FreePort();
    }

    private string Store => Path.Combine(_root, "store");

    private string RuntimeDir => Path.Combine(_root, "run");

    private StubWorker Stub => _stub ??= new StubWorker(PrivateDir("stub"));

    [Fact(Timeout = 120_000)]
    public async Task StartReturnsOnceWarmDetachesAndSurvivesTheStarterGroupKill()
    {
        // AC-17: the starter runs in its own process group with stdout/stderr on pipes that
        // are read to EOF; EOF and exit arrive within the start timeout plus 1 s.
        var clock = Stopwatch.StartNew();
        var (code, stdout, stderr, starterGroup) = await RunInNewGroupAsync(["daemon", "start", "--json", "--timeout", "60"]);
        Assert.True(clock.Elapsed < StartTimeout + TimeSpan.FromSeconds(1), $"start took {clock.Elapsed}");
        Assert.True(code == 0, stderr);
        var started = JsonNode.Parse(stdout)!;
        var pid = started["pid"]!.GetValue<int>();
        Assert.Equal($"http://127.0.0.1:{_port}", started["url"]!.GetValue<string>());
        Assert.False(started["alreadyRunning"]!.GetValue<bool>());

        // The daemon left the starter's session and group (setsid: pgid == pid), and N2: the
        // re-spawn under the dotnet muxer names the entry assembly.
        var daemon = Processes().Single(p => p.Pid == pid);
        Assert.Equal(pid, daemon.Pgid);
        Assert.Contains($"{AblCli.Dll} daemon run --detached --log-file ", daemon.Args);

        // Kill the starter's whole group: the daemon keeps serving with a warm worker.
        UnixIdentity.Signal(-starterGroup, UnixIdentity.SigKill);
        var health = await HealthAsync();
        Assert.Equal(pid, health["pid"]!.GetValue<int>());
        Assert.True(health["readyWorkers"]!.GetValue<int>() >= 1);

        // The log rule (N3): fds 1/2 point at the per-store log, created 0600.
        var logFile = started["logFile"]!.GetValue<string>();
        Assert.Equal(DaemonPaths.LogFile(Path.Combine(_root, "state", "agentblender"), Store), logFile);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(logFile));
        Assert.True(new FileInfo(logFile).Length > 0, "the daemon's console output lands in its log");
        var (logsCode, logs, _) = await RunAsync(["daemon", "logs", "--lines", "3"]);
        Assert.Equal(0, logsCode);
        Assert.InRange(logs.TrimEnd('\n').Split('\n').Length, 1, 3);

        var (statusCode, status, _) = await RunAsync(["daemon", "status", "--json"]);
        Assert.Equal(0, statusCode);
        Assert.True(JsonNode.Parse(status)!["running"]!.GetValue<bool>());

        // A graceful stop (no sessions) leaves nothing behind (AC-20).
        var (stopCode, _, stopErr) = await RunAsync(["daemon", "stop", "--json"]);
        Assert.True(stopCode == 0, stopErr);
        AssertNoOrphans(pid);

        // AC-21 after a run: lastPid names the stopped daemon.
        var (afterCode, after, _) = await RunAsync(["daemon", "status", "--json"]);
        Assert.Equal(3, afterCode);
        var stopped = JsonNode.Parse(after)!;
        Assert.False(stopped["running"]!.GetValue<bool>());
        Assert.Equal(pid, stopped["lastPid"]!.GetValue<int>());
    }

    public static TheoryData<string> PreflightFailures => new() { "blender.found", "blender.version", "runtime.dir symlink", "runtime.dir mode" };

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(PreflightFailures))]
    public async Task PreflightFailsFastWithTheCheckAndSpawnsNothing(string failure)
    {
        // AC-18, first half: under 2 s, the check ID and remedy, nothing spawned.
        var env = new Dictionary<string, string>();
        await using var unsupported = new StubWorker(PrivateDir("old-blender"), version: "5.1.0");
        switch (failure)
        {
            case "blender.found":
                env["ABL_BLENDER"] = Path.Combine(_root, "no-blender");
                break;
            case "blender.version":
                env["ABL_BLENDER"] = unsupported.ScriptPath; // answers --version with an unsupported 5.1.0
                break;
            case "runtime.dir symlink":
                Directory.CreateDirectory(Path.Combine(_root, "real-run"), Private);
                File.CreateSymbolicLink(RuntimeDir, Path.Combine(_root, "real-run"));
                break;
            default:
                Directory.CreateDirectory(RuntimeDir, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                File.SetUnixFileMode(RuntimeDir, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                break;
        }

        var clock = Stopwatch.StartNew();
        var (code, stdout, stderr) = await RunAsync(["daemon", "start", "--json"], env);
        var elapsed = clock.Elapsed;

        Assert.Equal(1, code);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"preflight took {elapsed}");
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal(failure.Split(' ')[0], error["check"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(error["hint"]?.GetValue<string>()), "a remedy is given");
        Assert.Contains(failure.Split(' ')[0], stderr);
        Assert.False(File.Exists(DaemonPaths.StateFile(Store)), "no daemon ran");
        Assert.False(PortAnswers(), "nothing listens on the port");
        Assert.DoesNotContain(Processes(), p => p.Args.Contains(_root, StringComparison.Ordinal)); // no daemon was spawned
    }

    [Fact(Timeout = 120_000)]
    public async Task CrashingWorkerFailsStartWithLastWorkerErrorAndATail()
    {
        // AC-18, second half: a worker that dies after spawn fails start quickly with
        // lastWorkerError and at most 20 log lines, and no daemon is left running.
        await using var crashing = new StubWorker(PrivateDir("crash"), crash: true);
        var clock = Stopwatch.StartNew();
        var (code, stdout, stderr) = await RunAsync(
            ["daemon", "start", "--json"], new() { ["ABL_BLENDER"] = crashing.ScriptPath });

        Assert.Equal(1, code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"start took {clock.Elapsed}, not a bare timeout");
        var body = JsonNode.Parse(stdout)!;
        Assert.Equal("worker.roundtrip", body["error"]!["check"]!.GetValue<string>());
        Assert.Contains(StubWorker.CrashMessage, body["error"]!["message"]!.GetValue<string>());
        Assert.Contains("exited with code 3 before connecting", body["error"]!["message"]!.GetValue<string>());
        Assert.InRange(body["logTail"]!.AsArray().Count, 1, 20);
        Assert.Contains("worker.roundtrip", stderr);

        var pid = DaemonState.Read(Store)!.Pid;
        Assert.False(UnixIdentity.ProcessExists(pid), "the failed daemon was stopped");
        Assert.False(UnixIdentity.GroupExists(pid), "and nothing is left in its group");
        Assert.False(StoreLock.IsHeld(Store));
    }

    [Fact(Timeout = 30_000)]
    public async Task SupervisorReportsAWorkerThatExitsBeforeConnecting()
    {
        // The AC-18 mechanism in-process: no 60 s spawn timeout for a dead worker.
        await using var crashing = new StubWorker(PrivateDir("crash"), crash: true);
        var supervisor = new WorkerSupervisor(
            new AblOptions { BlenderPath = crashing.ScriptPath, WorkerDir = _root, RuntimeDir = RuntimeDir, SpawnTimeout = TimeSpan.FromSeconds(60) },
            NullLogger<WorkerSupervisor>.Instance);

        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<WorkerDeadException>(() => supervisor.SpawnAsync(prewarm: true, default));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Contains("exited with code 3 before connecting: " + StubWorker.CrashMessage, error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(RuntimeDir)); // the socket was removed
    }

    [Fact(Timeout = 120_000)]
    public async Task TheStoreLockAdmitsOneDaemonWhateverThePortOrRuntimeDir()
    {
        var pid = await StartAsync();

        // Another port and runtime dir on the same store: refused, naming the running port.
        var otherPort = FreePort();
        var other = new Dictionary<string, string>
        {
            ["ABL_PORT"] = otherPort.ToString(CultureInfo.InvariantCulture),
            ["ABL_RUNTIME_DIR"] = Path.Combine(_root, "run2"),
        };
        var (code, stdout, _) = await RunAsync(["daemon", "start", "--json"], other);
        Assert.Equal(1, code);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("daemon.port", error["check"]!.GetValue<string>());
        Assert.Contains($"at port {_port}", error["message"]!.GetValue<string>());

        // The same port: the running instance is reported, exit 0.
        var (sameCode, same, _) = await RunAsync(["daemon", "start", "--json"]);
        Assert.Equal(0, sameCode);
        Assert.True(JsonNode.Parse(same)!["alreadyRunning"]!.GetValue<bool>());
        Assert.Equal(pid, JsonNode.Parse(same)!["pid"]!.GetValue<int>());

        // `daemon run` itself cannot take the lock either (after its ~2 s retry).
        var (runCode, _, runErr) = await RunAsync(["daemon", "run"], other);
        Assert.Equal(1, runCode);
        Assert.Contains("daemon.port", runErr);
        Assert.False(PortAnswers(otherPort), "the refused daemon never bound its port");

        Assert.Equal(pid, (await HealthAsync())["pid"]!.GetValue<int>());
        Assert.Equal(0, (await RunAsync(["daemon", "stop"])).Code);
        AssertNoOrphans(pid);
    }

    [Fact(Timeout = 120_000)]
    public async Task StopRefusesActiveSessionsUnlessForcedAndLeavesNoOrphans()
    {
        var pid = await StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        using var opened = await http.PostAsync("/api/ops/session_open", Json(new JsonObject { ["project"] = "lifecycle" }));
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
        var session = JsonNode.Parse(await opened.Content.ReadAsStringAsync())!;

        // AC-20: refused while a session is active, listing its last checkpoint.
        var (code, stdout, stderr) = await RunAsync(["daemon", "stop", "--json"]);
        Assert.Equal(1, code);
        var refused = JsonNode.Parse(stdout)!;
        var listed = Assert.Single(refused["sessions"]!.AsArray());
        Assert.Equal(session["id"]!.GetValue<string>(), listed!["session"]!.GetValue<string>());
        Assert.Equal(session["head"]!.GetValue<string>(), listed["lastCheckpoint"]!.GetValue<string>());
        Assert.Contains("checkpoint branch", refused["error"]!["hint"]!.GetValue<string>());
        Assert.Contains(session["id"]!.GetValue<string>(), stderr);
        Assert.Equal(pid, (await HealthAsync())["pid"]!.GetValue<int>()); // still serving

        // --force stops it and its whole group: no process left, no worker naming the runtime dir.
        var (forced, forcedOut, forcedErr) = await RunAsync(["daemon", "stop", "--json", "--force"]);
        Assert.True(forced == 0, forcedErr);
        var forcedBody = JsonNode.Parse(forcedOut)!;
        Assert.Single(forcedBody["droppedSessions"]!.AsArray());
        Assert.True(forcedBody["workersVerified"]!.GetValue<bool>()); // round-4 eng-test L-1
        AssertNoOrphans(pid);
    }

    [Fact(Timeout = 180_000)]
    public async Task AHungDaemonIsRunningButUnhealthyAndStopForceKillsIt()
    {
        // eng-code M-1: a daemon that holds the store lock but does not answer /healthz gets
        // one consistent answer (running, not healthy, exit 1), and `stop --force` stops it.
        var pid = await StartAsync();
        UnixIdentity.Signal(pid, SigStop);

        var (statusCode, status, _) = await RunAsync(["daemon", "status", "--json"]);
        Assert.Equal(1, statusCode);
        var unhealthy = JsonNode.Parse(status)!;
        Assert.True(unhealthy["running"]!.GetValue<bool>());
        Assert.False(unhealthy["healthy"]!.GetValue<bool>());
        Assert.Equal(pid, unhealthy["pid"]!.GetValue<int>());
        Assert.Equal("daemon.reachable", unhealthy["error"]!["check"]!.GetValue<string>());
        Assert.Contains("stop --force", unhealthy["error"]!["hint"]!.GetValue<string>());

        // start never calls it running (exit 0) on the lock and the record alone.
        var (startCode, started, _) = await RunAsync(["daemon", "start", "--json", "--timeout", "3"]);
        Assert.Equal(1, startCode);
        var startError = JsonNode.Parse(started)!["error"]!;
        Assert.Equal("daemon.reachable", startError["check"]!.GetValue<string>());
        Assert.Contains($"daemon pid {pid} holds the store lock", startError["message"]!.GetValue<string>());

        // A plain stop cannot list its sessions, so it refuses and signals nothing.
        var (stopCode, stopOut, _) = await RunAsync(["daemon", "stop", "--json"]);
        Assert.Equal(1, stopCode);
        Assert.Equal("daemon.reachable", JsonNode.Parse(stopOut)!["error"]!["check"]!.GetValue<string>());
        Assert.True(UnixIdentity.ProcessExists(pid), "a refused stop signals nothing");

        var (forced, forcedOut, forcedErr) = await RunAsync(["daemon", "stop", "--json", "--force"]);
        Assert.True(forced == 0, forcedErr);
        var stopped = JsonNode.Parse(forcedOut)!;
        Assert.True(stopped["forced"]!.GetValue<bool>());
        Assert.Null(stopped["droppedSessions"]); // unknown: the daemon could not list them
        AssertNoOrphans(pid);
    }

    [Fact(Timeout = 60_000)]
    public async Task AHeldLockWithAStaleRecordOfThisPortIsNeverReportedAsRunning()
    {
        // eng-arch MEDIUM-2 (AC-19): the lock is held (here by the test, as by a daemon still
        // starting) while daemon.json still names a previous run on the same port.
        Directory.CreateDirectory(Store, Private);
        using var held = StoreLock.TryAcquire(Store, TimeSpan.Zero);
        Assert.NotNull(held);
        var gone = ExitedPid();
        new DaemonState(gone, _port, $"http://127.0.0.1:{_port}", "0.0.0", DateTimeOffset.UtcNow.AddHours(-1), "-", null, gone)
            .Write(Store);

        var clock = Stopwatch.StartNew();
        var (code, stdout, _) = await RunAsync(["daemon", "start", "--json", "--timeout", "3"]);
        Assert.Equal(1, code);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(3), $"start waited only {clock.Elapsed}");
        var body = JsonNode.Parse(stdout)!;
        Assert.Null(body["alreadyRunning"]);
        Assert.Equal("daemon.reachable", body["error"]!["check"]!.GetValue<string>());
        Assert.Contains("holds the store lock", body["error"]!["message"]!.GetValue<string>());

        var (statusCode, status, _) = await RunAsync(["daemon", "status", "--json"]);
        Assert.Equal(1, statusCode);
        var unhealthy = JsonNode.Parse(status)!;
        Assert.True(unhealthy["running"]!.GetValue<bool>());
        Assert.False(unhealthy["healthy"]!.GetValue<bool>());
        Assert.Null(unhealthy["pid"]); // the record names no live lock holder

        // Nothing is safe to signal: stop --force refuses rather than trusting the stale pid.
        Assert.Equal(1, (await RunAsync(["daemon", "stop", "--force"])).Code);
        Assert.False(PortAnswers(), "no daemon was spawned");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingAForegroundDaemonKillsAWorkerStillConnecting(bool force)
    {
        // eng-code M-2 (AC-20, foreground path: no process group to sweep): a pool spawn in
        // flight at shutdown is cancelled, its worker killed and its socket removed. Round-2
        // eng-code M-1: `stop --force` too, which gives such a daemon a SIGTERM grace first.
        await using var silent = new StubWorker(PrivateDir("silent"), silent: true);
        using var daemon = Process.Start(Prepare(["daemon", "run"], new() { ["ABL_BLENDER"] = silent.ScriptPath }))!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        try
        {
            Assert.True(
                await WaitUntilAsync(() => Processes().Any(p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal)), TimeSpan.FromSeconds(60)),
                "the worker was spawned");
            Assert.True(
                await WaitUntilAsync(async () => (await TryHealthAsync())?["pid"]?.GetValue<int>() == daemon.Id, TimeSpan.FromSeconds(60)),
                "the daemon answers /healthz");

            var (code, stdout, stderr) = await RunAsync(force ? ["daemon", "stop", "--json", "--force"] : ["daemon", "stop", "--json"]);
            Assert.True(code == 0, stderr);
            var stopped = JsonNode.Parse(stdout)!;
            Assert.Equal(force, stopped["forced"]!.GetValue<bool>());
            Assert.True(stopped["workersVerified"]!.GetValue<bool>()); // round-4 eng-test L-1
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            // Killed before the daemon exited; only the kernel's reaping may lag a moment.
            Assert.True(
                await WaitUntilAsync(() => !Processes().Any(p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)),
                "a worker naming the runtime dir outlived the daemon");
            Assert.Empty(Directory.EnumerateFileSystemEntries(RuntimeDir));
        }
        finally
        {
            if (!daemon.HasExited)
            {
                daemon.Kill(entireProcessTree: true);
            }
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopForceKillsAStoppedForegroundDaemonAndItsWorkers(bool connecting, bool runtimeDirWidened)
    {
        // Round-2 eng-code M-1 (AC-20): a foreground daemon has no group of its own, and a
        // SIGSTOPped one cannot dispose anything, so `stop --force` kills its worker tree: a
        // warm worker, or one still connecting, whose listening socket it also removes.
        // Round-3 eng-security LOW-1: from a runtime dir that fails the A5 rule nothing is
        // removed, and stop says so (daemon.workers) instead of claiming AC-20.
        await using var silent = new StubWorker(PrivateDir("silent"), silent: true);
        using var shell = StartBehindShell(["daemon", "run"], connecting ? new() { ["ABL_BLENDER"] = silent.ScriptPath } : null);
        var pid = 0;
        try
        {
            Assert.True(
                await WaitUntilAsync(async () => (pid = (await TryHealthAsync())?["pid"]?.GetValue<int>() ?? 0) != 0, TimeSpan.FromSeconds(60)),
                "the daemon answers /healthz");
            Assert.Equal(shell.Id, Processes().Single(p => p.Pid == pid).Ppid);
            if (connecting)
            {
                Assert.True(
                    await WaitUntilAsync(() => Directory.EnumerateFileSystemEntries(RuntimeDir).Any(), TimeSpan.FromSeconds(30)),
                    "the spawn in flight listens on its socket");
            }
            else
            {
                await HealthAsync();
            }

            Assert.Contains(Processes(), p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal));
            UnixIdentity.Signal(pid, SigStop);
            if (runtimeDirWidened)
            {
                File.SetUnixFileMode(RuntimeDir, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }

            var (code, stdout, stderr) = await RunAsync(["daemon", "stop", "--json", "--force"]);
            var body = JsonNode.Parse(stdout)!;
            Assert.True(body["forced"]!.GetValue<bool>());
            await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(UnixIdentity.ProcessExists(pid), "the daemon still runs");
            Assert.DoesNotContain(Processes(), p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal));
            if (runtimeDirWidened)
            {
                Assert.Equal(1, code);
                Assert.Equal("daemon.workers", body["error"]!["check"]!.GetValue<string>());
                Assert.Contains("worker sockets were not removed", body["error"]!["message"]!.GetValue<string>());
                Assert.True(body["stopped"]!.GetValue<bool>());
                Assert.False(body["workersVerified"]!.GetValue<bool>());
                Assert.NotEmpty(Directory.EnumerateFileSystemEntries(RuntimeDir));
            }
            else
            {
                Assert.True(code == 0, stderr);
                Assert.True(body["workersVerified"]!.GetValue<bool>());
                Assert.Empty(Directory.EnumerateFileSystemEntries(RuntimeDir));
            }
        }
        finally
        {
            if (runtimeDirWidened)
            {
                File.SetUnixFileMode(RuntimeDir, Private);
            }

            if (pid != 0 && UnixIdentity.ProcessExists(pid))
            {
                UnixIdentity.Signal(pid, UnixIdentity.SigKill);
            }

            await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task StartReportsARunningDaemonWhoseLastWorkerErrorPredatesIt()
    {
        // Round-2 eng-arch MEDIUM-1 (AC-19): the first pool spawn fails and nothing retries it
        // until a take, so the daemon serves with readyWorkers 0 and an old lastWorkerError.
        await using var flaky = new StubWorker(PrivateDir("flaky"), flaky: true);
        using var daemon = Process.Start(Prepare(["daemon", "run"], new() { ["ABL_BLENDER"] = flaky.ScriptPath }))!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        try
        {
            Assert.True(
                await WaitUntilAsync(
                    async () => await TryHealthAsync() is { } h && h["pid"]?.GetValue<int>() == daemon.Id && h["lastWorkerError"] is JsonObject,
                    TimeSpan.FromSeconds(60)),
                "the first worker failed");
            Assert.Equal(0, (await TryHealthAsync())!["readyWorkers"]!.GetValue<int>());

            // At the deadline the old error is named as the likely cause; the hint is the log,
            // not a longer --timeout.
            var clock = Stopwatch.StartNew();
            var (code, stdout, _) = await RunAsync(["daemon", "start", "--json", "--timeout", "3"]);
            Assert.Equal(1, code);
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(3), $"start failed on the old error after {clock.Elapsed}");
            var error = JsonNode.Parse(stdout)!["error"]!;
            Assert.Equal("worker.roundtrip", error["check"]!.GetValue<string>());
            Assert.Contains(StubWorker.CrashMessage, error["message"]!.GetValue<string>());
            Assert.Contains("abl daemon logs", error["hint"]!.GetValue<string>());
            Assert.DoesNotContain("--timeout", error["hint"]!.GetValue<string>());

            // A start that waits while the pool is empty reports the running instance once a
            // session take refills it, though the old error is still recorded (exit 0).
            var starting = RunAsync(["daemon", "start", "--json", "--timeout", "60"]);
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.False(starting.IsCompleted, "start failed on an error older than itself");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
            using var opened = await http.PostAsync("/api/ops/session_open", Json(new JsonObject { ["project"] = "flaky" }));
            Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
            var (startCode, started, startErr) = await starting;
            Assert.True(startCode == 0, startErr);
            Assert.True(JsonNode.Parse(started)!["alreadyRunning"]!.GetValue<bool>());
            Assert.Equal(daemon.Id, JsonNode.Parse(started)!["pid"]!.GetValue<int>());
            Assert.NotNull((await TryHealthAsync())!["lastWorkerError"]);

            Assert.Equal(0, (await RunAsync(["daemon", "stop", "--force"])).Code);
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain(Processes(), p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal));
        }
        finally
        {
            if (!daemon.HasExited)
            {
                daemon.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task StartFailsFastWhenTheRunningDaemonsWorkerFailsAfterItBegan()
    {
        // 2a round-3 (AC-18, AC-19): the other half of the older-error rule. The daemon serves
        // with no lastWorkerError when `start` begins; its first worker then crashes, so `start`
        // fails on that new error at once, not at its 60 s deadline, and says to fix the cause.
        await using var late = new StubWorker(PrivateDir("late"), crashAfterSeconds: 6);
        using var daemon = Process.Start(Prepare(["daemon", "run"], new() { ["ABL_BLENDER"] = late.ScriptPath }))!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        try
        {
            Assert.True(
                await WaitUntilAsync(async () => await TryHealthAsync() is { } h && h["pid"]?.GetValue<int>() == daemon.Id, TimeSpan.FromSeconds(60)),
                "the daemon answers /healthz");
            Assert.Null((await TryHealthAsync())!["lastWorkerError"]);

            var clock = Stopwatch.StartNew();
            var (code, stdout, stderr) = await RunAsync(["daemon", "start", "--json", "--timeout", "60"]);
            Assert.True(code == 1, $"exit {code}: {stdout} {stderr}");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"start waited {clock.Elapsed} for a worker that had already failed");
            var error = JsonNode.Parse(stdout)!["error"]!;
            Assert.Equal("worker.roundtrip", error["check"]!.GetValue<string>());
            Assert.Contains($"the running daemon (pid {daemon.Id}) has no warm worker", error["message"]!.GetValue<string>());
            Assert.Contains(StubWorker.CrashMessage, error["message"]!.GetValue<string>());
            Assert.StartsWith("fix the cause", error["hint"]!.GetValue<string>());
            Assert.DoesNotContain("abl daemon logs", error["hint"]!.GetValue<string>());
            Assert.NotNull((await TryHealthAsync())!["lastWorkerError"]);

            Assert.Equal(0, (await RunAsync(["daemon", "stop", "--force"])).Code);
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!daemon.HasExited)
            {
                daemon.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task StopForceNeverSignalsAPidThatStartedAfterTheRecord()
    {
        // Round-2 eng-test M-1: the lock is held and daemon.json names a live process that
        // started after the record (a recycled pid). NamesLockHolder is all that keeps
        // `stop --force` from killing it.
        Directory.CreateDirectory(Store, Private);
        using var held = StoreLock.TryAcquire(Store, TimeSpan.Zero);
        Assert.NotNull(held);
        using var victim = Process.Start(new ProcessStartInfo("sleep", "60"))!;
        try
        {
            new DaemonState(victim.Id, _port, $"http://127.0.0.1:{_port}", "0.0.0", DateTimeOffset.UtcNow.AddHours(-1), "-", null, null)
                .Write(Store);

            var (code, _, _) = await RunAsync(["daemon", "stop", "--force", "--json"]);
            Assert.False(victim.HasExited, "stop --force signalled a pid that started after the record");
            Assert.Equal(1, code);

            var (statusCode, status, _) = await RunAsync(["daemon", "status", "--json"]);
            Assert.Equal(1, statusCode);
            Assert.Null(JsonNode.Parse(status)!["pid"]); // unconfirmed: the record names no live holder
        }
        finally
        {
            victim.Kill();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task StopForceNeverSignalsAGroupOtherThanTheRecordedPid()
    {
        // Round-2 eng-test L-1 (eng-security round-1 LOW-1): the record names a plausible lock
        // holder (it started before startedAt) but a processGroup that is another live group.
        Directory.CreateDirectory(Store, Private);
        using var held = StoreLock.TryAcquire(Store, TimeSpan.Zero);
        Assert.NotNull(held);
        using var holder = Process.Start(new ProcessStartInfo("sleep", "60"))!;
        var leaderStart = new ProcessStartInfo("perl");
        foreach (var arg in new[] { "-e", "setpgrp(0, 0) or die; exec 'sleep', '60' or die", })
        {
            leaderStart.ArgumentList.Add(arg);
        }

        using var leader = Process.Start(leaderStart)!;
        try
        {
            Assert.True(await WaitUntilAsync(() => Processes().Any(p => p.Pid == leader.Id && p.Pgid == leader.Id), TimeSpan.FromSeconds(10)));
            new DaemonState(holder.Id, _port, $"http://127.0.0.1:{_port}", "0.0.0", DateTimeOffset.UtcNow.AddSeconds(1), "-", null, leader.Id)
                .Write(Store);

            var (code, _, _) = await RunAsync(["daemon", "stop", "--force", "--json"]);
            Assert.False(leader.HasExited, "stop --force signalled a group the record does not own");
            Assert.False(holder.HasExited, "stop --force signalled an unusable record's pid");
            Assert.Equal(1, code);
        }
        finally
        {
            holder.Kill();
            leader.Kill();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task StopForceNeverSignalsTheProcessGroupItRunsIn()
    {
        // A daemon's workers, and anything their scripts start, share the daemon's group: a
        // `stop --force` run from there must not signal its own group (it would kill itself and
        // whatever shares that group, an agent harness included). The record names a live
        // group leader, and the CLI joins that group before it runs.
        Directory.CreateDirectory(Store, Private);
        using var held = StoreLock.TryAcquire(Store, TimeSpan.Zero);
        Assert.NotNull(held);
        var leaderStart = new ProcessStartInfo("perl");
        foreach (var arg in new[] { "-e", "setpgrp(0, 0) or die; exec 'sleep', '60' or die", })
        {
            leaderStart.ArgumentList.Add(arg);
        }

        using var leader = Process.Start(leaderStart)!;
        try
        {
            Assert.True(await WaitUntilAsync(() => Processes().Any(p => p.Pid == leader.Id && p.Pgid == leader.Id), TimeSpan.FromSeconds(10)));
            new DaemonState(leader.Id, _port, $"http://127.0.0.1:{_port}", "0.0.0", DateTimeOffset.UtcNow.AddSeconds(1), "-", null, leader.Id)
                .Write(Store);

            var (code, stdout, stderr) = await RunInGroupAsync(leader.Id, ["daemon", "stop", "--force", "--json"]);
            Assert.False(leader.HasExited, $"stop --force signalled the group it runs in (exit {code}): {stderr}");
            Assert.Equal(1, code);
            var error = JsonNode.Parse(stdout)!["error"]!;
            Assert.Equal("daemon.workers", error["check"]!.GetValue<string>());
            Assert.Contains($"process group {leader.Id}", error["message"]!.GetValue<string>());
        }
        finally
        {
            leader.Kill();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task NoDaemonVerbTrustsTheLockOrRecordInAStoreOthersCanWrite()
    {
        // Round-2 eng-security LOW-1: in a group- or other-writable store another account could
        // hold the lock and name a pid; stop and status refuse before signalling anything. One
        // gate for every verb (2a round 4): `run` refuses before it takes the lock or writes a
        // record, and `logs` never prints a file such a record names.
        var secret = Path.Combine(PrivateDir("elsewhere"), "secret.txt");
        File.WriteAllText(secret, "SECRET-LINE\n");
        Directory.CreateDirectory(Store, Private);
        File.SetUnixFileMode(Store, Private | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        using var held = StoreLock.TryAcquire(Store, TimeSpan.Zero);
        Assert.NotNull(held);
        using var victim = Process.Start(new ProcessStartInfo("sleep", "60"))!;
        try
        {
            new DaemonState(victim.Id, _port, $"http://127.0.0.1:{_port}", "0.0.0", DateTimeOffset.UtcNow.AddSeconds(1), "-", secret, null)
                .Write(Store);

            foreach (var verb in new[] { "stop", "status" })
            {
                var (code, stdout, _) = await RunAsync(["daemon", verb, "--json", .. verb == "stop" ? new[] { "--force" } : []]);
                Assert.Equal(1, code);
                Assert.Equal("paths.store", JsonNode.Parse(stdout)!["error"]!["check"]!.GetValue<string>());
            }

            var (startCode, started, _) = await RunAsync(["daemon", "start", "--json"]);
            Assert.Equal(1, startCode);
            Assert.Equal("paths.store", JsonNode.Parse(started)!["error"]!["check"]!.GetValue<string>());
            Assert.False(victim.HasExited, "a refused stop signals nothing");

            // The lock is held here, so without the gate `run` would refuse with daemon.port.
            var (runCode, _, runErr) = await RunAsync(["daemon", "run"]);
            Assert.Equal(1, runCode);
            Assert.Contains("abl: paths.store:", runErr);
            Assert.Equal(victim.Id, DaemonState.Read(Store)?.Pid);

            var (logsCode, logs, logsErr) = await RunAsync(["daemon", "logs"]);
            Assert.Equal(1, logsCode);
            Assert.DoesNotContain("SECRET-LINE", logs);
            Assert.Contains("abl: paths.store:", logsErr);
        }
        finally
        {
            victim.Kill();
            File.SetUnixFileMode(Store, Private);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task StopReturnsOnceTheStoreLockIsFreeEvenIfTheDaemonIsNotReaped()
    {
        // Round-3 eng-code L-2: the daemon's parent never reaps it, so its exited pid still
        // answers kill(pid, 0). The free store lock says it is gone: stop verifies its workers
        // and returns at once, rather than waiting 20 s and failing.
        using var parent = StartBehindShell(["daemon", "run"], script: "\"$@\" & exec sleep 120");
        var pid = 0;
        try
        {
            Assert.True(
                await WaitUntilAsync(async () => (pid = (await TryHealthAsync())?["pid"]?.GetValue<int>() ?? 0) != 0, TimeSpan.FromSeconds(60)),
                "the daemon answers /healthz");
            await HealthAsync();

            var clock = Stopwatch.StartNew();
            var (code, stdout, stderr) = await RunAsync(["daemon", "stop", "--json"]);
            Assert.True(code == 0, stderr);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"stop took {clock.Elapsed}");
            Assert.True(JsonNode.Parse(stdout)!["workersVerified"]!.GetValue<bool>());
            Assert.True(UnixIdentity.ProcessExists(pid), "the exited daemon was reaped, so this case proved nothing");
            Assert.False(StoreLock.IsHeld(Store));
            Assert.DoesNotContain(Processes(), p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal));
        }
        finally
        {
            if (pid != 0 && UnixIdentity.ProcessExists(pid))
            {
                UnixIdentity.Signal(pid, UnixIdentity.SigKill);
            }

            parent.Kill();
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task StopThatTimesOutStillPrintsItsResultWithACheck()
    {
        // Round-4 eng-arch LOW-2, eng-code L-2: a daemon that ignores SIGTERM. Here the test holds
        // the store lock and answers /healthz for a shell that ignores it. After the 20 s wait,
        // `stop --json` still prints its result with a check ID, and nothing is killed.
        Directory.CreateDirectory(Store, Private);
        using var held = StoreLock.TryAcquire(Store, TimeSpan.Zero);
        Assert.NotNull(held);
        using var stubborn = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "trap '' TERM; while :; do sleep 1; done"]))!;
        using var serving = new CancellationTokenSource();
        var fake = ServeFakeDaemonAsync(stubborn.Id, serving.Token);
        try
        {
            new DaemonState(stubborn.Id, _port, $"http://127.0.0.1:{_port}", "0.0.0", DateTimeOffset.UtcNow.AddSeconds(1), "-", null, null)
                .Write(Store);

            var (code, stdout, stderr) = await RunAsync(["daemon", "stop", "--json"]);
            Assert.Equal(1, code);
            var body = JsonNode.Parse(stdout)!;
            Assert.Equal("daemon.reachable", body["error"]!["check"]!.GetValue<string>());
            Assert.Contains("did not exit within 20 s", body["error"]!["message"]!.GetValue<string>());
            Assert.Contains("stop --force", body["error"]!["hint"]!.GetValue<string>());
            Assert.False(body["stopped"]!.GetValue<bool>());
            Assert.Equal(stubborn.Id, body["pid"]!.GetValue<int>());
            Assert.Contains("abl: daemon.reachable:", stderr);
            Assert.False(stubborn.HasExited, "a graceful stop only sends SIGTERM");
        }
        finally
        {
            await serving.CancelAsync();
            await fake;
            stubborn.Kill();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task StartResolvesARelativeStoreAgainstTheCallersDirectory()
    {
        // Round-2 eng-security LOW-2: the detached child runs in the state dir, so a relative
        // ABL_STORE must name the caller's store there too.
        var env = new Dictionary<string, string> { ["ABL_STORE"] = "store" };
        var (code, stdout, stderr) = await RunAsync(["daemon", "start", "--json", "--timeout", "60"], env, _root);
        Assert.True(code == 0, stderr);
        var pid = JsonNode.Parse(stdout)!["pid"]!.GetValue<int>();
        Assert.Equal(pid, DaemonState.Read(Store)?.Pid);
        Assert.True(StoreLock.IsHeld(Store));

        var (statusCode, _, _) = await RunAsync(["daemon", "status", "--json"], env, _root);
        Assert.Equal(0, statusCode);
        Assert.Equal(0, (await RunAsync(["daemon", "stop"], env, _root)).Code);
        AssertNoOrphans(pid);
    }

    [Fact(Timeout = 120_000)]
    public async Task ForegroundRunRefusesAnExistingWiderLogWithoutRepair()
    {
        // 015 replaces the old chmod-repair contract: refusal preserves existing bytes/mode.
        var logFile = Path.Combine(PrivateDir("logs"), "daemon.log");
        File.WriteAllText(logFile, "an earlier run\n");
        File.SetUnixFileMode(logFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var (code, _, stderr) = await RunAsync(["daemon", "run", "--log-file", logFile]);
        Assert.NotEqual(0, code);
        Assert.Contains("unsafe log", stderr);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(logFile));
        Assert.Equal("an earlier run\n", File.ReadAllText(logFile));
        Assert.False(File.Exists(logFile + ".1"));
    }

    [Fact(Timeout = 120_000)]
    public async Task StartPassesTheWorkerPassthroughNamesAndNoOtherVariable()
    {
        // eng-arch MEDIUM-1, eng-security MEDIUM-1 (AC-14 through `daemon start`) and eng-test
        // M-1 (the detached daemon gets the A6 allowlist): a listed name reaches the worker, an
        // unlisted one reaches neither the worker nor (where it can be read) the daemon.
        var config = Path.Combine(_root, "config.json");
        File.WriteAllText(config, """{"version":1,"worker":{"envPassthrough":["AB_LC_CANARY"]}}""");
        File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var env = new Dictionary<string, string> { ["AB_LC_CANARY"] = "listed", ["AB_LC_STRAY_API_KEY"] = "stray" };

        var (code, stdout, stderr) = await RunAsync(["daemon", "start", "--json", "--timeout", "60"], env);
        Assert.True(code == 0, stderr);
        var pid = JsonNode.Parse(stdout)!["pid"]!.GetValue<int>();
        Assert.True(await WaitUntilAsync(() => Stub.HasEnvironment, TimeSpan.FromSeconds(30)), "a worker started");

        var worker = Stub.EnvironmentNames();
        Assert.Contains("AB_LC_CANARY", worker);
        Assert.DoesNotContain("AB_LC_STRAY_API_KEY", worker);

        // macOS does not show another process's environment; Linux (the CI lane) does.
        if (OperatingSystem.IsLinux())
        {
            var daemonEnv = File.ReadAllText($"/proc/{pid}/environ").Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('=')[0]).ToList();
            Assert.Contains("AB_LC_CANARY", daemonEnv);
            Assert.DoesNotContain("AB_LC_STRAY_API_KEY", daemonEnv);
        }

        Assert.Equal(0, (await RunAsync(["daemon", "stop"])).Code);
        AssertNoOrphans(pid);
    }

    [Fact(Timeout = 30_000)]
    public async Task StatusWithNoDaemonIsNotRunningWithExit3()
    {
        // AC-21.
        var (code, stdout, _) = await RunAsync(["daemon", "status", "--json"]);

        Assert.Equal(3, code);
        var status = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal(["running", "stateFile", "lastPid"], status.Select(p => p.Key));
        Assert.False(status["running"]!.GetValue<bool>());
        Assert.Equal(DaemonPaths.StateFile(Store), status["stateFile"]!.GetValue<string>());
        Assert.Null(status["lastPid"]);
        Assert.Equal(3, (await RunAsync(["daemon", "stop"])).Code);
    }

    [Fact(Timeout = 120_000)]
    public async Task ForegroundRunWithALogFileRotatesItAndKeepsItsProcessGroup()
    {
        // N3: `daemon run --log-file` rotates and redirects in every mode; without --detached
        // it keeps its process group (a service manager owns it).
        var logFile = Path.Combine(_root, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!, Private);
        await using (var old = new FileStream(logFile, FileMode.CreateNew))
        {
            old.SetLength(10 * 1024 * 1024 + 1);
        }
        File.SetUnixFileMode(logFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var start = Prepare(["daemon", "run", "--log-file", logFile]);
        using var daemon = Process.Start(start)!;
        var console = new System.Collections.Concurrent.ConcurrentQueue<string>();
        daemon.OutputDataReceived += (_, e) => { if (e.Data is { } line) console.Enqueue(line); };
        daemon.ErrorDataReceived += (_, e) => { if (e.Data is { } line) console.Enqueue(line); };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        try
        {
            var health = await HealthAsync();
            Assert.Equal(daemon.Id, health["pid"]!.GetValue<int>());

            // Every console write after the redirect went to the log, none to the pipes. (The
            // runtime keeps its own close-on-exec duplicates of the original fds 0-2 from
            // start-up, so these pipes stay open; the caller of `daemon start` never shares them.)
            Assert.Empty(console);
            Assert.Equal(10 * 1024 * 1024 + 1, new FileInfo(logFile + ".1").Length);
            Assert.InRange(new FileInfo(logFile).Length, 1, 1024 * 1024);
            Assert.NotEqual(daemon.Id, Processes().Single(p => p.Pid == daemon.Id).Pgid);

            var state = DaemonState.Read(Store)!;
            Assert.Equal(logFile, state.LogFile);
            Assert.Null(state.ProcessGroup);
            var (logsCode, logs, _) = await RunAsync(["daemon", "logs", "--lines", "2"]);
            Assert.Equal(0, logsCode);
            Assert.Equal(DaemonLogTail(logFile, 2), logs.TrimEnd('\n').Split('\n'));
        }
        finally
        {
            UnixIdentity.Signal(daemon.Id, UnixIdentity.SigTerm);
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(0, daemon.ExitCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task RunIgnoresAppsettingsAndRefusesForeignFlags()
    {
        // FR-007 / plan A6: no ambient file or argument steers Kestrel. An appsettings.json
        // naming a 0.0.0.0 endpoint in the content root (the state dir) and in the working
        // directory is not read: the daemon serves loopback instead of refusing to start.
        var (usage, _, usageErr) = await RunAsync(["daemon", "run", "--urls", "http://0.0.0.0:1"]);
        Assert.Equal(2, usage);
        Assert.Contains("--urls", usageErr);

        var hostile = new JsonObject
        {
            ["Kestrel"] = new JsonObject
            {
                ["Endpoints"] = new JsonObject { ["Http"] = new JsonObject { ["Url"] = $"http://0.0.0.0:{_port}" } },
            },
        }.ToJsonString();
        var stateDir = PrivateDir("state");
        Directory.CreateDirectory(Path.Combine(stateDir, "agentblender"), Private);
        File.WriteAllText(Path.Combine(stateDir, "agentblender", "appsettings.json"), hostile);
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), hostile);
        var start = Prepare(["daemon", "run"]);
        start.WorkingDirectory = _root;
        using var daemon = Process.Start(start)!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        try
        {
            Assert.Equal(daemon.Id, (await HealthAsync())["pid"]!.GetValue<int>());
            Assert.False(daemon.HasExited);
        }
        finally
        {
            UnixIdentity.Signal(daemon.Id, UnixIdentity.SigTerm);
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // IAsyncLifetime: xunit 2 does not call IAsyncDisposable on test classes.
    public async Task DisposeAsync()
    {
        // A failed test must not leave its daemon or stub workers running: every process
        // whose argv names this test's root is this test's.
        foreach (var process in Processes().Where(p => p.Args.Contains(_root, StringComparison.Ordinal)))
        {
            try
            {
                UnixIdentity.Signal(process.Pid, UnixIdentity.SigKill);
            }
            catch (IOException)
            {
            }
        }

        if (_stub is not null)
        {
            await _stub.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string PrivateDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir, Private);
        return dir;
    }

    private static string[] DaemonLogTail(string logFile, int lines) =>
        [.. File.ReadAllLines(logFile).TakeLast(lines)];

    private async Task<int> StartAsync()
    {
        var (code, stdout, stderr) = await RunAsync(["daemon", "start", "--json", "--timeout", "60"]);
        Assert.True(code == 0, stderr);
        return JsonNode.Parse(stdout)!["pid"]!.GetValue<int>();
    }

    /// <summary>No process in the daemon's group and none whose argv names its runtime dir.</summary>
    private void AssertNoOrphans(int pid)
    {
        Assert.False(UnixIdentity.ProcessExists(pid), $"daemon {pid} still runs");
        Assert.False(UnixIdentity.GroupExists(pid), $"processes remain in group {pid}");
        Assert.DoesNotContain(Processes(), p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal));
    }

    private ProcessStartInfo Prepare(string[] args, Dictionary<string, string>? env = null, string? cwd = null)
    {
        var start = AblCli.Start(args);
        if (cwd is not null)
        {
            start.WorkingDirectory = cwd;
        }

        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("ABL_", StringComparison.Ordinal)
            || k.StartsWith("OTEL_", StringComparison.Ordinal) || k.StartsWith("XDG_", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["HOME"] = Path.Combine(_root, "home");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(_root, "state");
        start.Environment["ABL_CONFIG"] = Path.Combine(_root, "config.json"); // absent: never the user's file
        start.Environment["ABL_STORE"] = Store;
        start.Environment["ABL_RENDERS"] = Path.Combine(_root, "renders");
        start.Environment["ABL_RUNTIME_DIR"] = RuntimeDir;
        start.Environment["ABL_PORT"] = _port.ToString(CultureInfo.InvariantCulture);
        if (env is null || !env.ContainsKey("ABL_BLENDER"))
        {
            start.Environment["ABL_BLENDER"] = Stub.ScriptPath;
        }

        foreach (var (key, value) in env ?? [])
        {
            start.Environment[key] = value;
        }

        return start;
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(string[] args, Dictionary<string, string>? env = null, string? cwd = null)
    {
        using var process = Process.Start(Prepare(args, env, cwd))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Starts the CLI as a grandchild, behind <c>sh -c '"$@" &amp; wait'</c>, with its output
    /// drained. A test that SIGSTOPs a foreground daemon needs this: a stopped direct child of
    /// the test host stalls the runtime's child-process handling, and later starts hang.
    /// </summary>
    /// <summary>
    /// A minimal stand-in for a daemon's HTTP surface on this test's port: <c>/healthz</c> names
    /// <paramref name="pid"/> with a ready worker, and <c>session_list</c> is empty.
    /// </summary>
    private async Task ServeFakeDaemonAsync(int pid, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                // The whole request, body included, so closing never resets the connection.
                var request = "";
                var buffer = new byte[4096];
                while (!IsComplete(request))
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0)
                    {
                        break;
                    }

                    request += Encoding.ASCII.GetString(buffer, 0, read);
                }

                var path = request.Split(' ') is { Length: > 1 } parts ? parts[1] : "";
                var json = path == "/healthz"
                    ? $"{{\"pid\":{pid},\"version\":\"0.0.0\",\"readyWorkers\":1,\"sessions\":0}}"
                    : """{"items":[]}""";
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
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

    private Process StartBehindShell(string[] args, Dictionary<string, string>? env = null, string script = "\"$@\" & wait")
    {
        var start = Prepare(args, env);
        var inner = start.ArgumentList.ToList();
        var program = start.FileName;
        start.FileName = "/bin/sh";
        start.ArgumentList.Clear();
        foreach (var arg in new[] { "-c", script, "sh", program }.Concat(inner))
        {
            start.ArgumentList.Add(arg);
        }

        var shell = Process.Start(start)!;
        shell.OutputDataReceived += (_, _) => { };
        shell.ErrorDataReceived += (_, _) => { };
        shell.BeginOutputReadLine();
        shell.BeginErrorReadLine();
        return shell;
    }

    /// <summary>
    /// Runs the CLI as the leader of a new process group (as an agent harness's tool call
    /// would be), reading both pipes to EOF. perl's <c>setpgrp</c> then <c>exec</c> keeps the pid.
    /// </summary>
    private async Task<(int Code, string Stdout, string Stderr, int Group)> RunInNewGroupAsync(string[] args)
    {
        var start = Prepare(args);
        var inner = start.ArgumentList.ToList();
        start.FileName = "perl";
        start.ArgumentList.Clear();
        foreach (var arg in new[] { "-e", "setpgrp(0, 0) or die \"setpgrp: $!\"; exec @ARGV or die \"exec: $!\"", "dotnet" }.Concat(inner))
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var output = await stdout.WaitAsync(StartTimeout + TimeSpan.FromSeconds(1));
        var errors = await stderr.WaitAsync(TimeSpan.FromSeconds(1));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        return (process.ExitCode, output, errors, process.Id);
    }

    /// <summary>Runs the CLI as a member of the existing process group <paramref name="group"/>, reading both pipes to EOF.</summary>
    private async Task<(int Code, string Stdout, string Stderr)> RunInGroupAsync(int group, string[] args)
    {
        var start = Prepare(args);
        var inner = start.ArgumentList.ToList();
        start.FileName = "perl";
        start.ArgumentList.Clear();
        var script = "my $g = shift @ARGV; setpgrp(0, $g) or die \"setpgrp: $!\"; exec @ARGV or die \"exec: $!\"";
        foreach (var arg in new[] { "-e", script, group.ToString(CultureInfo.InvariantCulture), "dotnet" }.Concat(inner))
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        return (process.ExitCode, await stdout, await stderr);
    }

    private async Task<JsonNode> HealthAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (await TryHealthAsync() is { } health && health["readyWorkers"]?.GetValue<int>() >= 1)
            {
                return health;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(60))
            {
                var log = DaemonState.Read(Store)?.LogFile is { } file && File.Exists(file)
                    ? string.Join('\n', File.ReadLines(file).TakeLast(40))
                    : "(no log)";
                Assert.Fail($"the daemon did not become healthy; last health {await TryHealthAsync()}; log:\n{log}");
            }

            await Task.Delay(100);
        }
    }

    private async Task<JsonNode?> TryHealthAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{_port}/healthz"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private bool PortAnswers(int? port = null)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port ?? _port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>SIGSTOP (17 on macOS, 19 on Linux).</summary>
    private static int SigStop => OperatingSystem.IsMacOS() ? 17 : 19;

    /// <summary>The pid of a process that has already exited.</summary>
    private static int ExitedPid()
    {
        using var process = Process.Start(new ProcessStartInfo("true"))!;
        process.WaitForExit();
        return process.Id;
    }

    private static Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(100);
        }

        return true;
    }

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record ProcessRow(int Pid, int Ppid, int Pgid, string Args);

    /// <summary>
    /// Every process on the host (<c>ps -A</c>, POSIX options on Linux and macOS), from <c>ps</c>
    /// at an absolute system path only (never a bare name, which the current directory and PATH
    /// would resolve; 2a round-3).
    /// </summary>
    private static List<ProcessRow> Processes()
    {
        var program = ProcessTree.PsPaths.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException($"no ps at {string.Join(" or ", ProcessTree.PsPaths)}");
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true };
        foreach (var arg in new[] { "-A", "-ww", "-o", "pid=", "-o", "ppid=", "-o", "pgid=", "-o", "args=" })
        {
            start.ArgumentList.Add(arg);
        }

        using var ps = Process.Start(start)!;
        var rows = new List<ProcessRow>();
        while (ps.StandardOutput.ReadLine() is { } line)
        {
            if (Regex.Match(line, @"^\s*(\d+)\s+(\d+)\s+(\d+)\s*(.*)$") is { Success: true } row)
            {
                rows.Add(new ProcessRow(
                    int.Parse(row.Groups[1].Value), int.Parse(row.Groups[2].Value), int.Parse(row.Groups[3].Value), row.Groups[4].Value));
            }
        }

        ps.WaitForExit();
        return rows;
    }
}
