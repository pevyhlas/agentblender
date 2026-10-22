using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 T019 through the real <see cref="WorkerSupervisor"/> and <see cref="SessionService"/>
/// with a worker stub (unit lane: no Blender ever runs). The configured "Blender" is the
/// <see cref="StubWorker"/> script, which records its environment and hands the socket path
/// to an in-process protocol server, which speaks the worker protocol and by default reports no GPU backend in its hello
/// (the 009 shape: <c>gpu.backend="none"</c>, no devices).
/// AC-11 (hint per 009 FR-003): <c>cycles_gpu</c> is refused with <c>gpu_unavailable</c> and
/// the hint, no render reaches the worker; <c>lease=cpu_fallback</c> renders <c>cycles_cpu</c>
/// without the GPU lease and the sidecar records it; a Mac hello (<c>backend="metal"</c>)
/// passes the guard. AC-14 (stub half): a canary variable in the daemon's environment and a
/// <c>PYTHON*</c> variable never reach the worker; allowlisted and passthrough names do. The real-Blender half (OptiX still reported) is
/// <c>WorkerEnvironmentTests</c> on the CI Blender lane.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WorkerStubTests : IAsyncLifetime
{
    private const string Canary = "ABL_T019_STUB_CANARY";
    private const string Passthrough = "ABL_T019_PASSTHROUGH";
    // A distinctive PYTHON* name: WorkerEnvironmentTests owns PYTHONPATH, so an unfiltered
    // local run of both classes cannot clear this one mid-test.
    private const string PythonCanary = "PYTHONABL_T019_STUB";
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _root;
    private readonly StubWorker _stub;
    private readonly GpuLease _lease = new();
    private readonly WorkerPool _pool;
    private readonly SessionService _sessions;

    public WorkerStubTests()
    {
        // Short: the runtime dir's socket paths must fit sun_path (103 bytes on macOS).
        _root = Path.Combine(Path.GetTempPath(), "abl-stub-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root, Private);
        _stub = new StubWorker(_root);
        Environment.SetEnvironmentVariable(Canary, "must-not-leak");
        Environment.SetEnvironmentVariable(Passthrough, "allowed");
        Environment.SetEnvironmentVariable(PythonCanary, "/must/not/leak");
        var options = new AblOptions
        {
            BlenderPath = _stub.ScriptPath,
            WorkerDir = _root,
            RuntimeDir = Path.Combine(_root, "run"),
            StoreDir = Path.Combine(_root, "store"),
            RendersDir = Path.Combine(_root, "renders"),
            WorkerEnvPassthrough = [Passthrough, PythonCanary], // PYTHON* is stripped even when listed
            PoolFloor = 0,
            SpawnTimeout = TimeSpan.FromSeconds(30),
            GpuLeaseWait = TimeSpan.FromSeconds(60), // a lease wait would outlast the test
            Platform = Abl.Core.Config.HostPlatform.Linux, // the hint without the macOS suffix
        };
        _pool = new WorkerPool(options, new WorkerSupervisor(options, NullLogger<WorkerSupervisor>.Instance), NullLogger<WorkerPool>.Instance);
        _sessions = new SessionService(
            options, _pool, new CheckpointStore(options.StoreDir), _lease,
            new DesktopTier(options, NullLogger<DesktopTier>.Instance), NullLogger<SessionService>.Instance);
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("fail")]
    public async Task CyclesGpuWithoutAGpuBackendIsGpuUnavailable(string lease)
    {
        var session = await _sessions.OpenAsync("stub", default);
        var outPath = Path.Combine(_root, "renders", "refused", "refused.png");

        // The GPU lease is held: a guard that ran after the lease would wait or fail with
        // gpu_busy instead, so this proves the refusal comes before the lease.
        AblException error;
        using (var held = await _lease.TryAcquireAsync(TimeSpan.Zero, default))
        {
            Assert.NotNull(held);
            error = await Assert.ThrowsAsync<AblException>(() => _sessions.RenderFinalAsync(
                session.Id, null, "cycles_gpu", 64, null, outPath, lease, 0, default));
        }

        Assert.Equal("gpu_unavailable", error.Kind);
        Assert.Equal("use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback", error.Hint);
        Assert.Contains("reports no GPU backend", error.Message);
        Assert.DoesNotContain("render", _stub.Ops);
        Assert.False(Directory.Exists(Path.GetDirectoryName(outPath)), "a refusal creates no output directory");

        // The refusal leaves the session usable: a CPU render on the same worker succeeds.
        var cpu = await _sessions.RenderFinalAsync(session.Id, null, "cycles_cpu", 64, null, null, lease, 0, default);
        Assert.True(File.Exists(cpu.Path));
    }

    [Fact]
    public async Task CpuFallbackRendersCyclesCpuWithoutTheGpuLeaseAndRecordsIt()
    {
        var session = await _sessions.OpenAsync("stub", default);
        // A held GPU lease proves the fallback never waits for it (GpuLeaseWait is 60 s).
        using var held = await _lease.TryAcquireAsync(TimeSpan.Zero, default);
        Assert.NotNull(held);

        var started = DateTimeOffset.UtcNow;
        var result = await _sessions.RenderFinalAsync(
            session.Id, null, "cycles_gpu", 64, null, null, "cpu_fallback", 0, default);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "cycles_cpu" }, _stub.RenderedEngines);
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(result.SidecarPath))!;
        Assert.Equal("cycles_cpu", sidecar["engine"]!.GetValue<string>());
        Assert.Equal("local", sidecar["tier"]!.GetValue<string>());
        Assert.Equal("""{"backend":"cpu","name":null,"fallback":"gpu_unavailable"}""", sidecar["device"]!.ToJsonString());
    }

    [Fact]
    public async Task MetalBackendPassesTheGuard()
    {
        _stub.Gpu = new JsonObject
        {
            ["backend"] = "metal",
            ["devices"] = new JsonArray(new JsonObject { ["name"] = "Test Metal GPU", ["type"] = "METAL" }),
            ["metalrt"] = "AUTO",
        };
        var session = await _sessions.OpenAsync("stub", default);

        var result = await _sessions.RenderFinalAsync(
            session.Id, null, "cycles_gpu", 64, null, null, "fail", 0, default);

        Assert.Equal(new[] { "cycles_gpu" }, _stub.RenderedEngines);
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(result.SidecarPath))!;
        Assert.Equal("cycles_gpu", sidecar["engine"]!.GetValue<string>());
        Assert.Equal(new RenderDevice("metal", "Test Metal GPU"), result.Device);
    }

    [Fact]
    public async Task WorkerEnvironmentIsAllowlisted()
    {
        await _sessions.OpenAsync("stub", default);
        var names = _stub.EnvironmentNames();

        Assert.DoesNotContain(Canary, names);
        Assert.DoesNotContain(PythonCanary, names);
        Assert.DoesNotContain(names, name => name.StartsWith("PYTHON", StringComparison.Ordinal));
        Assert.Contains(Passthrough, names);
        Assert.Contains("PATH", names);
        Assert.Contains("HOME", names);
        Assert.All(names, name => Assert.True(
            Abl.Core.Config.ChildEnvironment.AllowsForWorker(name, [Passthrough]) || IsShellOwn(name), name));
    }

    // Variables /bin/sh itself sets before `env` runs; none of them came from the daemon.
    private static bool IsShellOwn(string name) => name is "PWD" or "OLDPWD" or "SHLVL" or "_";

    public Task InitializeAsync() => Task.CompletedTask;

    // IAsyncLifetime: xunit 2 does not call IAsyncDisposable on test classes, which left
    // the stub workers running.
    public async Task DisposeAsync()
    {
        await _sessions.DisposeAsync();
        await _pool.DisposeAsync();
        await _stub.DisposeAsync();
        Environment.SetEnvironmentVariable(Canary, null);
        Environment.SetEnvironmentVariable(Passthrough, null);
        Environment.SetEnvironmentVariable(PythonCanary, null);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
