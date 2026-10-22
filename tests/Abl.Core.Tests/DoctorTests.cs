using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Abl.Cli.Host;
using Abl.Core.Config;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 008 T051/T054 (FR-009, AC-24..26, AC-28): doctor over a temp home with fake
/// <c>blender</c> scripts, a fake worker round-trip and a fake service manager. Nothing here
/// reads the real config, store, Blender or service. The real round-trip is the Blender lane's
/// <c>DoctorRoundtripTests</c>.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class DoctorTests : IDisposable
{
    private readonly string _root;
    private int _roundtrips;

    public DoctorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "abl-doc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Home);
        File.SetUnixFileMode(_root, (UnixFileMode)0b111_000_000);
        File.SetUnixFileMode(Home, (UnixFileMode)0b111_101_101);
        Directory.CreateDirectory(Cwd);
    }

    private string Home => Path.Combine(_root, "home");

    private string Cwd => Path.Combine(_root, "cwd");

    private int Port { get; } = FreePort();

    private GpuCapability Gpu { get; set; } = new("metal", [new GpuDevice(FakeGpu.MetalName, "METAL")], "auto");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- AC-26: the contract snapshot -------------------------------------------------------

    [Fact]
    public void AC26_TheCatalogueIdsAndTheirOrderAreTheContract()
    {
        Assert.Equal(
            [
                "config.file", "config.value", "blender.found", "blender.version", "worker.files", "runtime.dir",
                "runtime.socket-path", "paths.store", "paths.renders", "worker.roundtrip", "gpu.backend", "gpu.render",
                "daemon.port", "daemon.reachable", "daemon.workers", "daemon.config-drift", "service.state", "desktop.config",
                "mcp.clients",
            ],
            DoctorCatalogue.Ids);
        Assert.Equal(["config.value", "daemon.workers"], DoctorCatalogue.Entries.Where(e => e.LifecycleOnly).Select(e => e.Id));
        Assert.Equal(["gpu.render"], DoctorCatalogue.Entries.Where(e => e.OptIn).Select(e => e.Id)); // 009 FR-006, AC-12
        Assert.Equal("opt-in", DoctorCatalogue.OptInReason);
        Assert.Equal("doctor.v1", DoctorCatalogue.Schema);

        // Prerequisites come before their dependants, so one pass in catalogue order suffices.
        foreach (var (entry, index) in DoctorCatalogue.Entries.Select((e, i) => (e, i)))
        {
            Assert.All(entry.Requires, r => Assert.True(DoctorCatalogue.Ids.ToList().IndexOf(r) < index, $"{entry.Id} requires a later {r}"));
        }
    }

    [Fact]
    public async Task AC26_TheEnvelopeShapeIsTheContract()
    {
        var report = await RunAsync(quick: true);
        var json = report.ToJson();

        Assert.Equal(["tool", "version", "schema", "summary", "checks"], json.Select(p => p.Key));
        Assert.Equal("abl", json["tool"]!.GetValue<string>());
        Assert.Equal("doctor.v1", json["schema"]!.GetValue<string>());
        Assert.Equal(["pass", "warn", "fail", "skip"], json["summary"]!.AsObject().Select(p => p.Key));
        Assert.Equal(report.Checks.Count, json["summary"]!.AsObject().Sum(p => p.Value!.GetValue<int>()));
        foreach (var check in json["checks"]!.AsArray())
        {
            var keys = check!.AsObject().Select(p => p.Key).ToList();
            Assert.Equal(["id", "status", "summary"], keys.Take(3));
            Assert.Subset(new HashSet<string> { "id", "status", "summary", "detail", "remedy", "reason" }, keys.ToHashSet());
            Assert.Contains(check["status"]!.GetValue<string>(), new[] { "pass", "warn", "fail", "skip" });
            Assert.Equal(check["status"]!.GetValue<string>() == "skip", check["reason"] is not null);
        }

        // Every doctor check (not the lifecycle-only ones) is reported on a full run.
        Assert.Equal(DoctorCatalogue.Entries.Where(e => !e.LifecycleOnly).Select(e => e.Id), report.Checks.Select(c => c.Id));
    }

    [Fact]
    public void AC26_NoSourceEmitsAnUncataloguedCheckId()
    {
        // Plan B4: one namespace; no verb emits an unlisted ID (2a round-4 eng-arch LOW-4).
        var emitted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in EmittedId().Matches(text))
            {
                emitted.Add(match.Groups["id"].Value);
            }
        }

        Assert.Contains("config.value", emitted);
        Assert.Contains("daemon.workers", emitted);
        Assert.Contains("worker.roundtrip", emitted);
        Assert.Empty(emitted.Except(DoctorCatalogue.Ids));
    }

    // ---- AC-24, AC-25, status rules -------------------------------------------------------------

    [Fact]
    public async Task AC24_AMissingBlenderFailsFoundWithTheSetupRemedyAndDependantsSkip()
    {
        var report = await RunAsync(env: new() { ["ABL_BLENDER"] = Path.Combine(_root, "deleted", "blender") });

        var found = Check(report, "blender.found");
        Assert.Equal(CheckStatus.Fail, found.Status);
        Assert.Contains("abl setup --blender <path>", found.Remedy);
        Assert.Contains(SupportedBlender.DownloadPage, found.Remedy);
        Assert.Equal("requires blender.found", Check(report, "blender.version").Reason);
        Assert.Equal("requires blender.found", Check(report, "worker.roundtrip").Reason);
        Assert.Equal("requires worker.roundtrip", Check(report, "gpu.backend").Reason);
        Assert.Equal(0, _roundtrips);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(CheckStatus.Pass, Check(report, "worker.files").Status); // independent checks still run
    }

    [Fact]
    public async Task AC25_TheRoundTripReportsVersionHashDigestGpuAndElapsedTime()
    {
        var report = await RunAsync();

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal(CheckStatus.Pass, roundtrip.Status);
        Assert.Equal(1, _roundtrips);
        Assert.Equal("5.2.1", roundtrip.Detail!["blenderVersion"]!.GetValue<string>());
        Assert.Equal("feedc0ffee12", roundtrip.Detail["buildHash"]!.GetValue<string>());
        Assert.Equal(WorkerSource.Digest(AblDefaults.WorkerDir()), roundtrip.Detail["workerSourceSha256"]!.GetValue<string>());
        Assert.Equal("metal", roundtrip.Detail["gpu"]!["backend"]!.GetValue<string>());
        Assert.Equal(1234, roundtrip.Detail["elapsedMs"]!.GetValue<long>());
        Assert.Contains("1234 ms", roundtrip.Summary);

        var gpu = Check(report, "gpu.backend");
        Assert.Equal(CheckStatus.Pass, gpu.Status);
        Assert.Equal($"metal ({FakeGpu.MetalName})", gpu.Summary);
        Assert.Equal("auto", gpu.Detail!["metalrt"]!.GetValue<string>());
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task AWorkerDigestOtherThanTheInstalledWorkerWarns()
    {
        var report = await RunAsync(roundtrip: _ => new RoundtripResult("5.2.1", "h", new string('0', 64), Gpu, 5));

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal(CheckStatus.Warn, roundtrip.Status);
        Assert.Contains("not worker/ on disk", roundtrip.Summary);
    }

    [Fact]
    public async Task AFailedRoundTripNamesTheStageAndTheConfigRemedy()
    {
        // The stage is the supervisor's tag (WorkerSupervisor.Refused), never read from the message.
        var report = await RunAsync(roundtrip: _ => throw WorkerSpawnStage.Tag(
            new WorkerDeadException("runtime.dir: runtime dir /x is a symlink (remove it)", new ConfigException("runtime.dir", "runtime dir /x is a symlink", "remove it")),
            WorkerSpawnStage.Preflight));

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal(CheckStatus.Fail, roundtrip.Status);
        Assert.Equal("preflight", roundtrip.Detail!["stage"]!.GetValue<string>());
        Assert.Equal("remove it", roundtrip.Remedy);
        Assert.Equal("requires worker.roundtrip", Check(report, "gpu.backend").Reason);
    }

    [Fact]
    public async Task AWarmThatTheWorkerReportsAsFailedFailsTheRoundTripAtWarm()
    {
        // worker.py turns a handler exception (a failed 64 px eevee prewarm) into ok:false.
        var traceback = "Traceback (most recent call last):\n" + new string('x', 5000) + "\n\u001b[31mRuntimeError: eevee prewarm failed";
        var report = await RunAsync(roundtrip: _ => throw WorkerSpawnStage.Tag(
            new WorkerOpException("render_failed", "eevee prewarm failed", traceback), WorkerSpawnStage.Warm));

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal(CheckStatus.Fail, roundtrip.Status);
        Assert.Equal("warm", roundtrip.Detail!["stage"]!.GetValue<string>());
        Assert.Equal("render_failed", roundtrip.Detail["kind"]!.GetValue<string>());
        Assert.Equal("warm failed (render_failed): eevee prewarm failed", roundtrip.Summary);
        var tail = roundtrip.Detail["traceback"]!.GetValue<string>();
        Assert.True(tail.Length <= 2000, $"traceback tail is {tail.Length} chars");
        Assert.EndsWith("RuntimeError: eevee prewarm failed", tail);
        Assert.DoesNotContain('\u001b', tail);
        Assert.Contains("detail.traceback", roundtrip.Remedy);
        Assert.Equal("requires worker.roundtrip", Check(report, "gpu.backend").Reason);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public async Task AHelloPastTheSpawnTimeoutFailsTheRoundTripAtHello()
    {
        // The host killed it after the timeout: no exit code of its own, but its output stays.
        string[] output = [.. Enumerable.Range(0, 40).Select(i => $"line {i} " + new string('x', 80)), "Compiling shaders\u001b[2K", ""];
        var report = await RunAsync(roundtrip: _ => throw WorkerSpawnStage.Tag(
            new WorkerTimeoutException("hello", TimeSpan.FromSeconds(60)), WorkerSpawnStage.Hello, new WorkerExit(null, output)));

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal(CheckStatus.Fail, roundtrip.Status);
        Assert.Equal("hello", roundtrip.Detail!["stage"]!.GetValue<string>());
        Assert.Equal("hello", roundtrip.Detail["op"]!.GetValue<string>());
        Assert.Null(roundtrip.Detail["exitCode"]);
        var tail = roundtrip.Detail["blenderOutput"]!.GetValue<string>();
        Assert.True(tail.Length <= 2000, $"output tail is {tail.Length} chars");
        Assert.EndsWith("Compiling shaders?[2K", tail);
        Assert.DoesNotContain('\u001b', tail);
        Assert.Equal("worker op 'hello' exceeded its 60s timeout; Blender's last output: Compiling shaders?[2K", roundtrip.Summary);
        Assert.Contains("detail.blenderOutput", roundtrip.Remedy);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public async Task AWorkerThatDiesDuringWarmReportsItsExitCodeAndOutput()
    {
        var report = await RunAsync(roundtrip: _ => throw WorkerSpawnStage.Tag(
            new WorkerDeadException("worker socket failed during 'warm'"),
            WorkerSpawnStage.Warm,
            new WorkerExit(134, ["Read prefs: defaults", "EEVEE: no GPU context, aborting"])));

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal(CheckStatus.Fail, roundtrip.Status);
        Assert.Equal("warm", roundtrip.Detail!["stage"]!.GetValue<string>());
        Assert.Equal(134, roundtrip.Detail["exitCode"]!.GetValue<int>());
        Assert.Equal("Read prefs: defaults\nEEVEE: no GPU context, aborting", roundtrip.Detail["blenderOutput"]!.GetValue<string>());
        Assert.Equal(
            "worker socket failed during 'warm'; Blender exited with code 134; Blender's last output: EEVEE: no GPU context, aborting",
            roundtrip.Summary);
        Assert.StartsWith("fix the cause Blender's last output (detail.blenderOutput in --json) names", roundtrip.Remedy);
    }

    [Fact]
    public async Task AWorkerThatPrintedNothingGetsTheByHandRemedy()
    {
        var report = await RunAsync(roundtrip: _ => throw WorkerSpawnStage.Tag(
            new WorkerDeadException("worker socket failed during 'hello'"), WorkerSpawnStage.Hello, new WorkerExit(139, [])));

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Null(roundtrip.Detail!["blenderOutput"]);
        Assert.Equal("worker socket failed during 'hello'; Blender exited with code 139", roundtrip.Summary);
        Assert.StartsWith("Blender printed nothing: start Blender by hand with --background --factory-startup", roundtrip.Remedy);
    }

    [Fact]
    public async Task AnUnexpectedThrowFailsOnlyThatCheckAndTheCommandStillReports()
    {
        var host = Host(roundtrip: _ => throw new ArgumentException("surprise\nsecond line"));
        var report = await new Doctor(host).RunAsync(quick: false, only: null, CancellationToken.None);

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal(CheckStatus.Fail, roundtrip.Status);
        Assert.Equal("the check failed unexpectedly (ArgumentException): surprise second line", roundtrip.Summary);
        Assert.Equal("System.ArgumentException", roundtrip.Detail!["exception"]!.GetValue<string>());
        Assert.Contains(report.Checks, c => c.Id == "mcp.clients"); // later checks still ran

        var stdout = new StringWriter();
        var code = await Doctor.MainAsync(["--json"], host, stdout, new StringWriter());
        Assert.Equal(1, code);
        Assert.Equal("doctor.v1", JsonNode.Parse(stdout.ToString())!["schema"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, "agent-harness sandbox")]
    [InlineData(false, "lease=cpu_fallback")]
    public async Task GpuBackendNoneWarnsWithThePlatformRemedy(bool appleSilicon, string remedy)
    {
        Gpu = GpuCapability.None;
        var report = await RunAsync(appleSilicon: appleSilicon);

        var gpu = Check(report, "gpu.backend");
        Assert.Equal(CheckStatus.Warn, gpu.Status);
        Assert.Contains(remedy, gpu.Remedy);
        Assert.DoesNotContain("install-service", gpu.Remedy); // an owner choice (008 M11)
    }

    [Fact]
    public async Task QuickSkipsTheRoundTripAndItsDependantAndIsFast()
    {
        var clock = Stopwatch.StartNew();
        var report = await RunAsync(quick: true);

        Assert.Equal(0, _roundtrips);
        Assert.Equal("--quick", Check(report, "worker.roundtrip").Reason);
        Assert.Equal("requires worker.roundtrip", Check(report, "gpu.backend").Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"--quick took {clock.Elapsed}");
    }

    [Fact]
    public async Task CheckRunsTheNamedCheckAndItsPrerequisitesOnlyEvenUnderQuick()
    {
        var report = await RunAsync(quick: true, only: "gpu.backend");

        Assert.Equal(
            ["config.file", "blender.found", "blender.version", "worker.files", "runtime.dir", "runtime.socket-path", "worker.roundtrip", "gpu.backend"],
            report.Checks.Select(c => c.Id));
        Assert.Equal(1, _roundtrips);
        Assert.Equal(CheckStatus.Pass, Check(report, "gpu.backend").Status);
    }

    [Fact]
    public async Task CheckWithAFailedPrerequisiteSkipsTheNamedCheck()
    {
        var report = await RunAsync(env: new() { ["ABL_BLENDER"] = "/nonexistent/blender" }, only: "worker.roundtrip");

        Assert.Equal("requires blender.found", Check(report, "worker.roundtrip").Reason);
        Assert.DoesNotContain(report.Checks, c => c.Id == "daemon.port");
    }

    [Theory]
    [InlineData("no.such", "no doctor check 'no.such'")]
    [InlineData("config.value", "reported by the lifecycle verbs")]
    [InlineData("daemon.workers", "reported by the lifecycle verbs")]
    public async Task CheckRefusesAnUnknownOrLifecycleOnlyId(string id, string message)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Doctor.MainAsync(["--check", id], Host(), stdout, stderr);

        Assert.Equal(2, code);
        Assert.Contains(message, stderr.ToString());
        Assert.Equal("", stdout.ToString());
    }

    [Fact]
    public async Task AMalformedVariableFailsConfigValueAndEveryOtherCheckSkips()
    {
        var report = await RunAsync(env: new() { ["ABL_PORT"] = "seventy" });

        Assert.Equal(CheckStatus.Pass, Check(report, "config.file").Status);
        var value = Check(report, "config.value");
        Assert.Equal(CheckStatus.Fail, value.Status);
        Assert.Contains("ABL_PORT", value.Summary);
        Assert.All(report.Checks.Skip(2), c => Assert.Equal(c.Id == "gpu.render" ? "opt-in" : "requires config.value", c.Reason)); // 009 FR-006: opt-in first
        Assert.Equal(0, _roundtrips);
    }

    [Fact]
    public async Task AnInvalidConfigFileFailsConfigFile()
    {
        var config = Path.Combine(_root, "config.json");
        File.WriteAllText(config, """{"version":1,"daemon":{"prot":1}}""");
        File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var report = await RunAsync();

        var file = Check(report, "config.file");
        Assert.Equal(CheckStatus.Fail, file.Status);
        Assert.Contains("unknown key 'daemon.prot'", file.Summary);
        Assert.Contains("abl setup --reset", file.Remedy);
        Assert.All(report.Checks.Skip(1), c => Assert.Equal(c.Id == "gpu.render" ? "opt-in" : "requires config.file", c.Reason)); // 009 FR-006: opt-in first
    }

    [Fact]
    public async Task ASymlinkedRuntimeDirFailsAndBlocksTheRoundTrip()
    {
        var real = Path.Combine(_root, "real-run");
        Directory.CreateDirectory(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var link = Path.Combine(_root, "run");
        File.CreateSymbolicLink(link, real);

        var report = await RunAsync(env: new() { ["ABL_RUNTIME_DIR"] = link });

        Assert.Equal(CheckStatus.Fail, Check(report, "runtime.dir").Status);
        Assert.Equal("not run", Check(report, "runtime.socket-path").Detail!["bindProbe"]!.GetValue<string>());
        Assert.Equal("requires runtime.dir", Check(report, "worker.roundtrip").Reason);
    }

    [Fact]
    public async Task AnExistingPrivateRuntimeDirGetsARealBindProbeThatLeavesNothing()
    {
        var run = Path.Combine(_root, "run");
        Directory.CreateDirectory(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var report = await RunAsync(env: new() { ["ABL_RUNTIME_DIR"] = run });

        var socket = Check(report, "runtime.socket-path");
        Assert.Equal(CheckStatus.Pass, socket.Status);
        Assert.Equal("ok", socket.Detail!["bindProbe"]!.GetValue<string>());
        Assert.Empty(Directory.EnumerateFileSystemEntries(run));
    }

    [Fact]
    public async Task ATooLongRuntimeDirFailsSocketPath()
    {
        var report = await RunAsync(env: new() { ["ABL_RUNTIME_DIR"] = "/" + new string('r', 120) });

        var socket = Check(report, "runtime.socket-path");
        Assert.Equal(CheckStatus.Fail, socket.Status);
        Assert.Contains("set a shorter paths.runtime", socket.Remedy);
    }

    [Fact]
    public async Task AStoreWritableByOthersFailsAndLowSpaceWarns()
    {
        var store = Path.Combine(_root, "store");
        Directory.CreateDirectory(store);
        File.SetUnixFileMode(store, (UnixFileMode)0b111_111_101);

        var report = await RunAsync(env: new() { ["ABL_STORE"] = store }, freeBytes: _ => 1024L * 1024 * 1024);

        Assert.Equal(CheckStatus.Fail, Check(report, "paths.store").Status);
        var renders = Check(report, "paths.renders");
        Assert.Equal(CheckStatus.Warn, renders.Status);
        Assert.Contains("1.0 GB free", renders.Summary);
        Assert.Equal(CheckStatus.Fail, Check(report, "daemon.reachable").Status); // the store-root gate
    }

    [Fact]
    public async Task NoDaemonWarnsReachableAndSkipsDrift()
    {
        var report = await RunAsync();

        Assert.Equal(CheckStatus.Pass, Check(report, "daemon.port").Status);
        var reachable = Check(report, "daemon.reachable");
        Assert.Equal(CheckStatus.Warn, reachable.Status);
        Assert.Equal("`abl daemon start`", reachable.Remedy);
        Assert.Equal("no daemon is running", Check(report, "daemon.config-drift").Reason);
    }

    [Fact]
    public async Task APortHeldByAnotherProgramFailsDaemonPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();

        var report = await RunAsync();

        var port = Check(report, "daemon.port");
        Assert.Equal(CheckStatus.Fail, port.Status);
        Assert.Contains($"port {Port} is in use by another program", port.Summary);
    }

    [Fact]
    public async Task APortHeldWhereThisStoresRecordPointsButNothingAnswersNamesTheRecord()
    {
        // A hung or still-starting daemon of this store: not "another program" (eng-code LOW-3).
        using var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        var store = Path.Combine(_root, "store");
        Directory.CreateDirectory(store, (UnixFileMode)0b111_000_000);
        new Abl.Core.Lifecycle.DaemonState(4242, Port, $"http://127.0.0.1:{Port}", "0", DateTimeOffset.UtcNow, "d", null, null).Write(store);

        var report = await RunAsync(env: new() { ["ABL_STORE"] = store }, only: "daemon.port");

        var port = Check(report, "daemon.port");
        Assert.Equal(CheckStatus.Fail, port.Status);
        Assert.Contains("this store's daemon record (pid 4242) names it, but nothing answers /healthz there", port.Summary);
        Assert.Contains("abl daemon stop --force", port.Remedy);
    }

    [Fact]
    public async Task ASpawnFailureIsReportedAtTheSpawnStage()
    {
        var report = await RunAsync(roundtrip: _ => throw WorkerSpawnStage.Tag(
            new WorkerDeadException("worker 1 did not connect within 60s"), WorkerSpawnStage.Spawn));

        var roundtrip = Check(report, "worker.roundtrip");
        Assert.Equal("spawn", roundtrip.Detail!["stage"]!.GetValue<string>());
        Assert.Null(roundtrip.Detail["check"]);
    }

    [Fact]
    public async Task DesktopConfigWithoutATokenFails()
    {
        var report = await RunAsync(env: new() { ["ABL_DESKTOP_ENDPOINT"] = "127.0.0.1:1" });

        var desktop = Check(report, "desktop.config");
        Assert.Equal(CheckStatus.Fail, desktop.Status);
        Assert.Contains("no token is", desktop.Summary);
    }

    // ---- AC-28: drift -----------------------------------------------------------------------

    [Fact]
    public void AC28_AWhitespaceOnlyEditIsNoDriftButAnEnvironmentOverrideIs()
    {
        const string path = "/home/u/.config/agentblender/config.json";
        var compact = Effective(new FakeConfigFileSystem().File(path, """{"version":1,"daemon":{"logLevel":"warning"}}"""), []);
        var spaced = Effective(new FakeConfigFileSystem().File(path, "{\n  \"version\": 1,\n  \"daemon\": { \"logLevel\" : \"warning\" }\n}\n"), []);
        Assert.Empty(Doctor.Drift(compact, spaced));

        var shell = Effective(new FakeConfigFileSystem().File(path, """{"version":1,"daemon":{"logLevel":"warning"}}"""), new() { ["ABL_LOG_LEVEL"] = "debug" });
        var (key, cli, daemon) = Assert.Single(Doctor.Drift(shell, compact));
        Assert.Equal("daemon.logLevel", key);
        Assert.Equal("env", cli!["source"]!.GetValue<string>());
        Assert.Equal("file", daemon!["source"]!.GetValue<string>());
    }

    [Fact]
    public void AC28_ASecretDiffersOnlyByBeingSetNeverByValue()
    {
        var a = Effective(new FakeConfigFileSystem(), new() { ["ABL_DESKTOP_TOKEN"] = "one" });
        var b = Effective(new FakeConfigFileSystem(), new() { ["ABL_DESKTOP_TOKEN"] = "two" });
        var none = Effective(new FakeConfigFileSystem(), []);

        Assert.Empty(Doctor.Drift(a, b));
        Assert.Equal("desktop.token", Assert.Single(Doctor.Drift(a, none)).Key);
    }

    // ---- service.state, desktop.config, mcp.clients ---------------------------------------------

    [Fact]
    public async Task AnInstalledServiceWithoutAspNetCoreFails()
    {
        var report = await RunAsync(services: new FakeServices(DotnetRoot: Path.Combine(_root, "no-dotnet")));

        var service = Check(report, "service.state");
        Assert.Equal(CheckStatus.Fail, service.Status);
        Assert.Contains("ASP.NET Core 10", service.Summary);
        Assert.Contains("install-service", service.Remedy);
    }

    [Fact]
    public async Task AFailedServiceJobAndLingerOffWarn()
    {
        var dotnet = FakeDotnetRoot();
        var failed = await RunAsync(services: new FakeServices(dotnet, Failure: "exit-code"));
        Assert.Equal(CheckStatus.Warn, Check(failed, "service.state").Status);
        Assert.Contains("down after a failure (exit-code)", Check(failed, "service.state").Summary);

        var lingerCalls = new List<string>();
        var linger = await RunAsync(
            services: new FakeServices(dotnet),
            run: (program, args) =>
            {
                lingerCalls.Add(string.Join(' ', args));
                return (0, "Linger=no\n", "");
            });
        var service = Check(linger, "service.state");
        Assert.Equal(CheckStatus.Warn, service.Status);
        Assert.Contains("linger is off", service.Summary);
        Assert.Equal(["show-user tester -p Linger"], lingerCalls);

        var healthy = await RunAsync(services: new FakeServices(dotnet), run: (_, _) => (0, "Linger=yes\n", ""));
        Assert.Equal(CheckStatus.Pass, Check(healthy, "service.state").Status);
    }

    [Fact]
    public async Task AServiceThatRunsAnotherAblWarns()
    {
        var report = await RunAsync(services: new FakeServices(FakeDotnetRoot(), Program: "/elsewhere/abl"), run: (_, _) => (0, "Linger=yes", ""));

        var service = Check(report, "service.state");
        Assert.Equal(CheckStatus.Warn, service.Status);
        Assert.Contains("not this abl (/this/abl)", service.Summary);
    }

    [Theory]
    [InlineData("0.0.0.0:7812", CheckStatus.Fail, "wildcard")]
    [InlineData("desk:notaport", CheckStatus.Fail, "is not host:port")]
    [InlineData("127.0.0.1:1", CheckStatus.Warn, "does not accept TCP")]
    public async Task DesktopConfigChecksTheEndpointAndProbes(string endpoint, CheckStatus status, string summary)
    {
        var report = await RunAsync(env: new() { ["ABL_DESKTOP_ENDPOINT"] = endpoint, ["ABL_DESKTOP_TOKEN"] = "t" });

        var desktop = Check(report, "desktop.config");
        Assert.Equal(status, desktop.Status);
        Assert.Contains(summary, desktop.Summary);
        Assert.DoesNotContain("\"t\"", report.ToJson().ToJsonString());
    }

    [Fact]
    public async Task DesktopConfigIsUnsupportedOnMacOS()
    {
        var unset = await RunAsync(platform: HostPlatform.MacOS);
        Assert.Equal("unsupported on macOS", Check(unset, "desktop.config").Reason);

        var set = await RunAsync(platform: HostPlatform.MacOS, env: new() { ["ABL_DESKTOP_ENDPOINT"] = "desk:7812" });
        Assert.Equal(CheckStatus.Warn, Check(set, "desktop.config").Status);
    }

    [Fact]
    public async Task McpClientsComparesTheProjectEntriesWithTheEffectiveUrl()
    {
        Assert.Equal($"no .mcp.json or opencode.json in {Cwd}", Check(await RunAsync(), "mcp.clients").Reason);

        File.WriteAllText(Path.Combine(Cwd, ".mcp.json"), """{"mcpServers":{"agentblender":{"type":"http","url":"http://127.0.0.1:""" + Port + """/mcp"}}}""");
        var matching = Check(await RunAsync(), "mcp.clients");
        Assert.Equal(CheckStatus.Pass, matching.Status);

        File.WriteAllText(Path.Combine(Cwd, "opencode.json"), """
            {
              // OpenCode allows comments
              "mcp": { "agentblender": { "type": "remote", "url": "http://127.0.0.1:1/mcp", "enabled": true }, },
            }
            """);
        var stale = Check(await RunAsync(), "mcp.clients");
        Assert.Equal(CheckStatus.Warn, stale.Status);
        Assert.Contains("opencode.json names http://127.0.0.1:1/mcp", stale.Summary);
        Assert.Contains("abl mcp config --client opencode", stale.Summary);

        File.Delete(Path.Combine(Cwd, "opencode.json"));
        File.WriteAllText(Path.Combine(Cwd, ".mcp.json"), """{"mcpServers":{}}""");
        var missing = Check(await RunAsync(), "mcp.clients");
        Assert.Equal(CheckStatus.Warn, missing.Status);
        Assert.Contains(".mcp.json has no agentblender entry (`abl mcp config --client claude`)", missing.Summary);

        // JsonNode accepts a duplicate key and throws on access: a warning, not a crashed run.
        File.WriteAllText(Path.Combine(Cwd, ".mcp.json"), """{"mcpServers":{},"mcpServers":{}}""");
        var duplicate = Check(await RunAsync(), "mcp.clients");
        Assert.Equal(CheckStatus.Warn, duplicate.Status);
        Assert.StartsWith(".mcp.json cannot be read as JSON", duplicate.Summary);

        // A repository-chosen string is relayed bounded, never at the read bound's length.
        File.WriteAllText(Path.Combine(Cwd, ".mcp.json"), "{\"mcpServers\":{\"agentblender\":{\"url\":\"" + new string('u', 100_000) + "\"}}}");
        var flood = Check(await RunAsync(), "mcp.clients");
        Assert.Equal(CheckStatus.Warn, flood.Status);
        Assert.True(flood.Summary.Length < 500, $"summary is {flood.Summary.Length} chars");
        Assert.Contains("abl mcp config --client claude", flood.Summary);
    }

    [Fact(Timeout = 30_000)] // an open of the FIFO would block forever: fail, never hang the lane
    public async Task McpClientsReadsAtMostOneMebibyteOfARegularFile()
    {
        // A cloned repository can commit .mcp.json as a link: the link's own length is tiny.
        var mcp = Path.Combine(Cwd, ".mcp.json");
        var big = Path.Combine(_root, "big.json");
        File.WriteAllText(big, "{\"pad\":\"" + new string('a', 1024 * 1024) + "\"}");
        File.CreateSymbolicLink(mcp, big);
        var tooBig = Check(await RunAsync(), "mcp.clients");
        Assert.Equal(CheckStatus.Warn, tooBig.Status);
        Assert.Equal(".mcp.json is larger than 1 MiB", tooBig.Summary);

        File.Delete(mcp);
        File.CreateSymbolicLink(mcp, "/dev/zero");
        var clock = Stopwatch.StartNew();
        var device = Check(await RunAsync(), "mcp.clients");
        Assert.Equal(CheckStatus.Warn, device.Status);
        Assert.Contains("is not a regular file", device.Summary);

        File.Delete(mcp);
        using (var mkfifo = Process.Start("mkfifo", [mcp]))
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var fifo = Check(await RunAsync(), "mcp.clients"); // no writer: an open would block forever
        Assert.Equal(CheckStatus.Warn, fifo.Status);
        Assert.Contains("is not a regular file", fifo.Summary);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task TextOutputIsOneLinePerCheckWithRemediesAndASummary()
    {
        var stdout = new StringWriter();
        var code = await Doctor.MainAsync(["--quick"], Host(env: new() { ["ABL_BLENDER"] = "/nonexistent/blender" }), stdout, new StringWriter());

        Assert.Equal(1, code);
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("fail  blender.found", lines.Single(l => l.Contains("  blender.found ", StringComparison.Ordinal) && !l.Contains("requires", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("remedy: abl setup --blender <path>", StringComparison.Ordinal));
        Assert.Matches(@"^\d+ pass, \d+ warn, 1 fail, \d+ skip$", lines[^1]);
    }

    // ---- worker.files against the package manifest (008 FR-014, AC-32) --------------------------

    [Fact]
    public async Task AC32_ThePackagedWorkerPassesAgainstItsManifest()
    {
        var package = PackageCopy();

        var check = Check(await RunPackagedAsync(package, Path.Combine(package, "worker")), "worker.files");

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Contains("equal to the package manifest", check.Summary);
        Assert.True(check.Detail!["packaged"]!.GetValue<bool>());
        Assert.Equal(check.Detail["digest"]!.GetValue<string>(), check.Detail["manifest"]!.GetValue<string>());
        Assert.Equal(WorkerSource.Digest(AblDefaults.WorkerDir()), check.Detail["manifest"]!.GetValue<string>());
        Assert.Empty(check.Detail["changed"]!.AsArray());
    }

    [Fact]
    public async Task AC32_APackagedModuleChangedAfterInstallFailsNamingItWithTheReinstallRemedy()
    {
        var package = PackageCopy();
        File.AppendAllText(Path.Combine(package, "worker", "sheet.py"), "\n# edited\n");

        var check = Check(await RunPackagedAsync(package, Path.Combine(package, "worker")), "worker.files");

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("sheet.py differ from the package manifest", check.Summary);
        Assert.StartsWith("reinstall abl", check.Remedy);
        Assert.Equal(["sheet.py"], check.Detail!["changed"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotEqual(check.Detail["digest"]!.GetValue<string>(), check.Detail["manifest"]!.GetValue<string>());
    }

    [Fact]
    public async Task AC32_AMissingManifestWarnsThatTheWorkerIsUnproved()
    {
        var package = PackageCopy();
        File.Delete(WorkerManifest.PathIn(package));

        var check = Check(await RunPackagedAsync(package, Path.Combine(package, "worker")), "worker.files");

        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("no package manifest", check.Summary);
        Assert.Null(check.Detail!["manifest"]);
    }

    [Fact]
    public async Task AC32_AMalformedManifestFails()
    {
        var package = PackageCopy();
        File.WriteAllText(WorkerManifest.PathIn(package), "not a manifest\n");

        var check = Check(await RunPackagedAsync(package, Path.Combine(package, "worker")), "worker.files");

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("is not `<sha256>  <module>`", check.Summary);
        Assert.StartsWith("reinstall abl", check.Remedy);
        Assert.Equal(Path.Combine(package, "worker"), check.Detail!["dir"]!.GetValue<string>());
        Assert.Null(check.Detail["exception"]);
    }

    [Fact]
    public async Task AC32_AWorkerDirOverrideIsComparedButOnlyWarnsWhenItDiffers()
    {
        var package = PackageCopy();
        var same = PackageCopy("override-same");
        var edited = PackageCopy("override-edited");
        File.AppendAllText(Path.Combine(edited, "worker", "worker.py"), "\n# local change\n");

        var equal = Check(await RunPackagedAsync(package, Path.Combine(same, "worker")), "worker.files");
        var differs = Check(await RunPackagedAsync(package, Path.Combine(edited, "worker")), "worker.files");

        Assert.Equal(CheckStatus.Pass, equal.Status);
        Assert.False(equal.Detail!["packaged"]!.GetValue<bool>());
        Assert.Contains("paths.workerDir overrides the packaged worker/", equal.Summary);
        Assert.Equal(CheckStatus.Warn, differs.Status);
        Assert.Contains("worker.py differ", differs.Summary);
        Assert.Contains("unset paths.workerDir", differs.Remedy);
    }

    /// <summary>Doctor with <paramref name="package"/> as this abl's package directory and <paramref name="workerDir"/> as paths.workerDir.</summary>
    private Task<DoctorReport> RunPackagedAsync(string package, string workerDir) =>
        new Doctor(Host(env: new() { ["ABL_WORKER_DIR"] = workerDir }) with { PackageDirectory = package })
            .RunAsync(quick: true, only: null, CancellationToken.None);

    /// <summary>A package directory: a copy of the built worker/ and a manifest written like the build writes it.</summary>
    private string PackageCopy(string name = "package")
    {
        var package = Path.Combine(_root, name);
        var worker = Directory.CreateDirectory(Path.Combine(package, "worker")).FullName;
        var lines = new List<string>();
        foreach (var source in Directory.GetFiles(AblDefaults.WorkerDir(), "*.py").Order(StringComparer.Ordinal))
        {
            var target = Path.Combine(worker, Path.GetFileName(source));
            File.Copy(source, target);
            lines.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))) + "  " + Path.GetFileName(source));
        }

        File.WriteAllLines(WorkerManifest.PathIn(package), lines);
        return package;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static DoctorCheck Check(DoctorReport report, string id) => Assert.Single(report.Checks, c => c.Id == id);

    private static JsonObject Effective(FakeConfigFileSystem fs, Dictionary<string, string> env) =>
        AblConfig.Resolve(new ConfigInputs { Env = env, Home = "/home/u", FileSystem = fs })
            .ToJson()["effective"]!.AsObject();

    private Task<DoctorReport> RunAsync(
        Dictionary<string, string>? env = null, bool quick = false, string? only = null,
        Func<AblOptions, RoundtripResult>? roundtrip = null, bool appleSilicon = false, IServiceManager? services = null,
        CommandRunner? run = null, Func<string, long?>? freeBytes = null, HostPlatform platform = HostPlatform.Linux) =>
        new Doctor(Host(env, roundtrip, appleSilicon, services, run, freeBytes, platform)).RunAsync(quick, only, CancellationToken.None);

    private DoctorHost Host(
        Dictionary<string, string>? env = null, Func<AblOptions, RoundtripResult>? roundtrip = null, bool appleSilicon = false,
        IServiceManager? services = null, CommandRunner? run = null, Func<string, long?>? freeBytes = null,
        HostPlatform platform = HostPlatform.Linux)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ABL_CONFIG"] = Path.Combine(_root, "config.json"),
            ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
            ["ABL_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ABL_BLENDER"] = FakeBlender("5.2.1"),
            ["USER"] = "tester",
        };
        foreach (var (key, value) in env ?? [])
        {
            merged[key] = value;
        }

        var inputs = new ConfigInputs
        {
            Env = merged,
            Home = Home,
            Platform = platform,
            UserId = UnixIdentity.EffectiveUserId,
            Blender = new BlenderSearch { Platform = platform, Home = Home, SystemRoot = Path.Combine(_root, "sys"), Spotlight = _ => [] },
        };
        roundtrip ??= _ => new RoundtripResult("5.2.1", "feedc0ffee12", WorkerSource.Digest(AblDefaults.WorkerDir()), Gpu, 1234);
        return new DoctorHost(inputs, Cwd, services, (options, _) =>
        {
            _roundtrips++;
            return Task.FromResult(roundtrip(options));
        })
        {
            AppleSilicon = appleSilicon,
            Run = run ?? ((_, _) => null),
            ThisAbl = ["/this/abl"],
            FreeBytes = freeBytes ?? (_ => 100L * 1024 * 1024 * 1024),
            RenderWorker = FakeRenderWorkerAsync,
        };
    }

    private string FakeBlender(string version)
    {
        var path = Path.Combine(_root, "blender-" + version, "blender");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"#!/bin/sh\nprintf 'Blender {version}\\n\\tbuild hash: feedc0ffee12\\n'\n");
            File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        }

        return path;
    }

    private string FakeDotnetRoot()
    {
        var root = Path.Combine(_root, "dotnet");
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.AspNetCore.App", "10.0.1"));
        return root;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "agentblender.slnx")))
                {
                    return d.FullName;
                }
            }

            throw new DirectoryNotFoundException("repository root (agentblender.slnx) not found above the test dir");
        }
    }

    /// <summary>A check ID as code emits it: a <c>ConfigException</c>, a resolver error, a resolution or a doctor check.</summary>
    [GeneratedRegex("""(?:ConfigException\(\s*|Check = |DoctorCheck\.(?:Pass|Warn|Fail|Skip)\(\s*|new\((?="config\.))"(?<id>[a-z]+\.[a-z][a-z-]*)"[,)]""")]
    private static partial Regex EmittedId();

    private sealed record FakeServices(string DotnetRoot, string? Failure = null, string Program = "/this/abl") : IServiceManager
    {
        public string Kind => "fake";

        public ServiceState Query() => new(Kind, "/fake/unit", true, false, null, [Program, "daemon", "run"], DotnetRoot, null) { Failure = Failure };

        public ServiceOutcome Start() => throw new InvalidOperationException("doctor never starts a service");

        public ServiceOutcome Stop() => throw new InvalidOperationException("doctor never stops a service");

        public ServiceOutcome BootOut() => throw new InvalidOperationException("doctor never boots a service out");
    }
}
