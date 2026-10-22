using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 T051/T054 (FR-009, plan B4) over the built <c>abl</c> and a stub worker (unit lane, no
/// Blender): a worker that fails at warm or exits before connecting is a <c>worker.roundtrip</c>
/// fail at its typed stage in a <c>doctor.v1</c> report with exit 1, never a crashed command.
/// A worker that dies or hangs after it connects carries Blender's exit code and output tail
/// (plan B4) as the supervisor's typed fact.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class DoctorStubTests : IDisposable
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(90);

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // Short: the runtime dir's socket paths must fit sun_path (103 bytes on macOS).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-ds-" + Guid.NewGuid().ToString("N")[..8]);

    public DoctorStubTests() => Directory.CreateDirectory(_root, Private);

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

    [Fact(Timeout = 120_000)]
    public async Task AWarmTheWorkerReportsAsFailedIsAFailAtTheWarmStage()
    {
        await using var stub = new StubWorker(Dir("stub")) { FailWarm = true };

        var (code, check) = await DoctorAsync(stub);

        Assert.Equal(1, code);
        Assert.Equal("fail", check["status"]!.GetValue<string>());
        Assert.Equal("warm", check["detail"]!["stage"]!.GetValue<string>());
        Assert.Equal("render_failed", check["detail"]!["kind"]!.GetValue<string>());
        Assert.Equal("warm failed (render_failed): stub: prewarm render failed", check["summary"]!.GetValue<string>());
        Assert.EndsWith("RuntimeError: stub: prewarm render failed", check["detail"]!["traceback"]!.GetValue<string>());
        Assert.Contains("hello", stub.Ops); // it got past hello
    }

    [Fact(Timeout = 120_000)]
    public async Task AWorkerThatExitsBeforeConnectingIsAFailAtTheSpawnStage()
    {
        await using var stub = new StubWorker(Dir("crash"), crash: true);

        var (code, check) = await DoctorAsync(stub);

        Assert.Equal(1, code);
        Assert.Equal("fail", check["status"]!.GetValue<string>());
        Assert.Equal("spawn", check["detail"]!["stage"]!.GetValue<string>());
        Assert.Contains("exited with code 3 before connecting", check["summary"]!.GetValue<string>());
    }

    [Fact(Timeout = 120_000)]
    public async Task AWorkerThatDiesDuringWarmReportsItsExitCodeAndBlenderOutput()
    {
        await using var stub = new StubWorker(Dir("dies"), dieAtWarm: true);

        var (code, check) = await DoctorAsync(stub);

        Assert.Equal(1, code);
        Assert.Equal("fail", check["status"]!.GetValue<string>());
        var detail = check["detail"]!;
        Assert.Equal("warm", detail["stage"]!.GetValue<string>());
        Assert.Equal(StubWorker.DeathExitCode, detail["exitCode"]!.GetValue<int>());
        Assert.Equal($"{StubWorker.StartMessage}\n{StubWorker.DeathMessage}", detail["blenderOutput"]!.GetValue<string>());
        var summary = check["summary"]!.GetValue<string>();
        Assert.Contains($"Blender exited with code {StubWorker.DeathExitCode}", summary);
        Assert.EndsWith($"Blender's last output: {StubWorker.DeathMessage}", summary);
        Assert.Contains("detail.blenderOutput", check["remedy"]!.GetValue<string>());
    }

    [Fact(Timeout = 120_000)]
    public async Task AHelloThatNeverAnswersIsATimeoutAtTheHelloStageWithBlenderOutput()
    {
        // The supervisor's own bookkeeping (doctor's rendering of it is DoctorTests).
        var dir = Dir("hangs");
        await using var stub = new StubWorker(dir, dieAtWarm: true) { HangHello = true };
        var options = new AblOptions
        {
            BlenderPath = stub.ScriptPath,
            WorkerDir = dir,
            RuntimeDir = Path.Combine(_root, "run"),
            StoreDir = Path.Combine(_root, "store"),
            RendersDir = Path.Combine(_root, "renders"),
            SpawnTimeout = TimeSpan.FromSeconds(2),
        };

        var error = await Assert.ThrowsAsync<WorkerTimeoutException>(
            () => new WorkerSupervisor(options, NullLogger<WorkerSupervisor>.Instance).SpawnAsync(prewarm: true, CancellationToken.None));

        Assert.Equal("hello", error.Op);
        Assert.Equal(WorkerSpawnStage.Hello, WorkerSpawnStage.Of(error));
        var exit = WorkerSpawnStage.ExitOf(error);
        Assert.NotNull(exit);
        Assert.Null(exit.ExitCode); // the host killed it
        Assert.Contains(StubWorker.StartMessage, exit.Output);
    }

    private async Task<(int Code, JsonObject Check)> DoctorAsync(StubWorker stub)
    {
        var start = AblCli.Start("doctor", "--json", "--check", "worker.roundtrip");
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("ABL_", StringComparison.Ordinal)
            || k.StartsWith("OTEL_", StringComparison.Ordinal) || k.StartsWith("XDG_", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["HOME"] = Dir("home");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(_root, "state");
        start.Environment["ABL_CONFIG"] = Path.Combine(_root, "config.json"); // absent: never the user's file
        start.Environment["ABL_STORE"] = Path.Combine(_root, "store");
        start.Environment["ABL_RENDERS"] = Path.Combine(_root, "renders");
        start.Environment["ABL_RUNTIME_DIR"] = Path.Combine(_root, "run");
        start.Environment["ABL_BLENDER"] = stub.ScriptPath;

        var (code, stdout, stderr) = await AblCli.RunAsync(start, CliTimeout);

        var report = JsonNode.Parse(stdout);
        Assert.True(report is not null, $"doctor printed no report (exit {code}): {stderr}");
        Assert.Equal("doctor.v1", report["schema"]!.GetValue<string>());
        var check = report["checks"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "worker.roundtrip")!.AsObject();
        return (code, check);
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path, Private);
        return path;
    }
}
