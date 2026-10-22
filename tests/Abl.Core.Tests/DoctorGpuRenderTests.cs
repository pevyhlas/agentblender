using System.Collections.Concurrent;
using Abl.Cli.Host;
using Abl.Core.Config;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 009 T022 (FR-006, AC-12): the opt-in doctor check <c>gpu.render</c> over a fake render
/// worker (<see cref="FakeWorkerProtocol"/>, no Blender). Without <c>--check gpu.render</c> it
/// is <c>skip</c> with reason <c>opt-in</c> and nothing is spawned; with it, a disposable
/// worker renders 32 px, 1 sample, <c>cycles_gpu</c> twice into a private directory that is
/// gone afterwards, and the store and renders dirs are unchanged.
/// </summary>
public sealed partial class DoctorTests
{
    private readonly ConcurrentQueue<FakeFrame> _renderFrames = new();
    private readonly List<Task> _renderServers = [];
    private int _renderSpawns;

    /// <summary>What the <c>gpu.render</c> worker answers.</summary>
    private FakeWorkerScript RenderScript { get; } = new() { Gpu = FakeGpu.Metal() };

    /// <summary>The options the last <c>gpu.render</c> spawn received.</summary>
    private AblOptions? RenderOptions { get; set; }

    private async Task<WorkerHandle> FakeRenderWorkerAsync(AblOptions options, CancellationToken ct)
    {
        RenderOptions = options;
        var id = $"render-{Interlocked.Increment(ref _renderSpawns)}";
        var (handle, server) = await FakeWorkerProtocol.ConnectAsync(id, RenderScript, _renderFrames, prewarm: false, ct);
        lock (_renderServers)
        {
            _renderServers.Add(server);
        }

        return handle;
    }

    private IReadOnlyList<FakeFrame> Renders => [.. _renderFrames.Where(f => f.Op == "render")];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC12_WithoutCheckGpuRenderIsAnOptInSkipAndSpawnsNothing(bool quick)
    {
        var report = await RunAsync(quick: quick);

        var render = Check(report, "gpu.render");
        Assert.Equal(CheckStatus.Skip, render.Status);
        Assert.Equal("opt-in", render.Reason);
        Assert.Contains("abl doctor --check gpu.render", render.Summary);
        Assert.Equal(0, _renderSpawns);
        Assert.Empty(_renderFrames);

        // A prerequisite that fails does not change the reason: the check is opt-in first.
        var missing = await RunAsync(env: new() { ["ABL_BLENDER"] = Path.Combine(_root, "deleted", "blender") });
        Assert.Equal("opt-in", Check(missing, "gpu.render").Reason);
        Assert.Equal(0, _renderSpawns);
    }

    [Fact]
    public async Task AC12_CheckGpuRenderWithASlowFirstAndFastSecondRenderPassesWithFirstCompileSuspected()
    {
        var store = Path.Combine(_root, "store");
        var renders = Path.Combine(_root, "renders");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(renders);
        File.WriteAllText(Path.Combine(store, "keep.txt"), "store");
        var before = Listing(store, renders);
        RenderScript.RenderDurationsMs.Enqueue(45_000.4);
        RenderScript.RenderDurationsMs.Enqueue(2_000);

        var report = await RunAsync(env: new() { ["ABL_STORE"] = store, ["ABL_RENDERS"] = renders }, only: "gpu.render");

        // Only the named check and its prerequisites, in catalogue order.
        Assert.Equal(
            ["config.file", "blender.found", "blender.version", "worker.files", "runtime.dir", "runtime.socket-path", "worker.roundtrip", "gpu.backend", "gpu.render"],
            report.Checks.Select(c => c.Id));
        var render = Check(report, "gpu.render");
        Assert.True(render.Status == CheckStatus.Pass, render.Summary);
        Assert.Equal(45_000, render.Detail!["firstMs"]!.GetValue<long>());
        Assert.Equal(2_000, render.Detail["secondMs"]!.GetValue<long>());
        Assert.True(render.Detail["firstCompileSuspected"]!.GetValue<bool>());
        Assert.Equal("metal", render.Detail["device"]!["backend"]!.GetValue<string>());
        Assert.Equal(FakeGpu.MetalName, render.Detail["device"]!["name"]!.GetValue<string>());
        Assert.Equal($"cycles_gpu on metal ({FakeGpu.MetalName}): first render 45000 ms, second 2000 ms (the first compiled the GPU kernels; the cache is now primed)", render.Summary);
        Assert.Equal(0, report.ExitCode);

        // One disposable worker, no eevee prewarm, two 32 px 1-sample cycles_gpu renders into
        // one private dir inside the runtime dir; the PNGs and the dir are gone afterwards.
        Assert.Equal(1, _renderSpawns);
        Assert.False(Assert.Single(_renderFrames, f => f.Op == "warm").Request!["prewarm_render"]!.GetValue<bool>());
        Assert.Equal(2, Renders.Count);
        Assert.All(Renders, r =>
        {
            Assert.Equal("cycles_gpu", r.Engine);
            Assert.Equal(32, r.Request!["res"]!.GetValue<int>());
            Assert.Equal(1, r.Request["samples"]!.GetValue<int>());
        });
        var outputs = Renders.Select(r => r.Request!["out"]!.GetValue<string>()).ToList();
        var dir = Path.GetDirectoryName(outputs[0])!;
        Assert.Equal(dir, Path.GetDirectoryName(outputs[1]));
        Assert.NotEqual(outputs[0], outputs[1]);
        Assert.Equal(RenderOptions!.RuntimeDir, Path.GetDirectoryName(dir));
        Assert.StartsWith("d-gpu-", Path.GetFileName(dir), StringComparison.Ordinal); // never a worker socket name
        Assert.False(Directory.Exists(dir), $"{dir} was left behind");
        Assert.DoesNotContain(_renderFrames, f => f.Op == "render" && f.Engine != "cycles_gpu");
        Assert.Contains(_renderFrames, f => f.Op == "quit"); // the worker was shut down
        Assert.Equal(before, Listing(store, renders));
        await Task.WhenAll(_renderServers).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AFastFirstRenderPassesWithoutFirstCompileSuspected()
    {
        RenderScript.RenderDurationsMs.Enqueue(30_000); // the boundary: not "over 30 s"
        RenderScript.RenderDurationsMs.Enqueue(10_000); // the boundary: still within 10 s

        var render = Check(await RunAsync(only: "gpu.render"), "gpu.render");

        Assert.Equal(CheckStatus.Pass, render.Status);
        Assert.False(render.Detail!["firstCompileSuspected"]!.GetValue<bool>());
        Assert.DoesNotContain("compiled", render.Summary);
    }

    [Fact]
    public async Task ASlowSecondRenderWarns()
    {
        RenderScript.RenderDurationsMs.Enqueue(50_000);
        RenderScript.RenderDurationsMs.Enqueue(10_001);

        var report = await RunAsync(only: "gpu.render");

        var render = Check(report, "gpu.render");
        Assert.Equal(CheckStatus.Warn, render.Status);
        Assert.Equal(10_001, render.Detail!["secondMs"]!.GetValue<long>());
        Assert.True(render.Detail["firstCompileSuspected"]!.GetValue<bool>());
        Assert.Contains("within 10 s", render.Summary);
        Assert.Contains("abl doctor --check gpu.render", render.Remedy);
        Assert.Equal(0, report.ExitCode); // a warning, not a failure
    }

    [Theory]
    [InlineData(true, "agent-harness sandbox")]
    [InlineData(false, "lease=cpu_fallback")]
    public async Task AWorkerRaisedGpuUnavailableFailsWithThePlatformRemedyAndLeavesNothing(bool appleSilicon, string remedy)
    {
        RenderScript.GpuCheckFails = true;

        var report = await RunAsync(only: "gpu.render", appleSilicon: appleSilicon);

        var render = Check(report, "gpu.render");
        Assert.Equal(CheckStatus.Fail, render.Status);
        Assert.Equal("gpu_unavailable", render.Detail!["kind"]!.GetValue<string>());
        Assert.StartsWith("gpu_unavailable: ", render.Summary, StringComparison.Ordinal);
        Assert.Contains(remedy, render.Remedy);
        Assert.DoesNotContain("install-service", render.Remedy);
        Assert.Equal(1, report.ExitCode);
        Assert.Single(Renders);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Renders[0].Request!["out"]!.GetValue<string>())));
    }

    [Fact]
    public async Task NoGpuBackendFailsGpuRenderWithoutSpawningAWorker()
    {
        Gpu = GpuCapability.None;

        var report = await RunAsync(only: "gpu.render");

        Assert.Equal(CheckStatus.Warn, Check(report, "gpu.backend").Status);
        var render = Check(report, "gpu.render");
        Assert.Equal(CheckStatus.Fail, render.Status);
        Assert.Equal("gpu_unavailable", render.Detail!["kind"]!.GetValue<string>());
        Assert.Equal(0, _renderSpawns);
    }

    [Fact]
    public async Task AFailedGpuBackendPrerequisiteSkipsGpuRender()
    {
        var report = await RunAsync(env: new() { ["ABL_BLENDER"] = "/nonexistent/blender" }, only: "gpu.render");

        Assert.Equal("requires gpu.backend", Check(report, "gpu.render").Reason);
        Assert.Equal(0, _renderSpawns);
    }

    [Fact]
    public async Task AnotherRenderErrorFailsWithItsKind()
    {
        RenderScript.RenderErrorKind = "RuntimeError";

        var render = Check(await RunAsync(only: "gpu.render"), "gpu.render");

        Assert.Equal(CheckStatus.Fail, render.Status);
        Assert.Equal("RuntimeError", render.Detail!["kind"]!.GetValue<string>());
        Assert.Equal("render", render.Detail["stage"]!.GetValue<string>());
        Assert.Contains("abl doctor --check gpu.render", render.Remedy);
    }

    [Theory]
    [InlineData(true, "Metal is hidden from this process, usually by an agent-harness sandbox; the owner can start the daemon outside the sandbox (README); meanwhile use engine=cycles_cpu or engine=eevee")]
    [InlineData(false, "use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback")]
    public async Task GpuBackendRemedyTextIsExactPerHost(bool appleSilicon, string remedy)
    {
        // The darwin-arm64 host names the sandbox; linux-x64 the engine and lease choices.
        Gpu = GpuCapability.None;

        var gpu = Check(await RunAsync(appleSilicon: appleSilicon, only: "gpu.backend"), "gpu.backend");

        Assert.Equal(CheckStatus.Warn, gpu.Status);
        Assert.Equal(remedy, gpu.Remedy);
    }

    /// <summary>Every file under the dirs, with its size, sorted (the AC-12 before/after listing).</summary>
    private static IReadOnlyList<string> Listing(params string[] dirs) =>
        [.. dirs.SelectMany(d => Directory.Exists(d)
                ? Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories)
                    .Select(p => $"{p} {(File.Exists(p) ? new FileInfo(p).Length : -1)}")
                : [$"{d} absent"])
            .Order(StringComparer.Ordinal)];
}
