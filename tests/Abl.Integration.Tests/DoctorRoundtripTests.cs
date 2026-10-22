using System.Text.Json.Nodes;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 T054, AC-25 with real Blender (Blender lane): <c>abl doctor --check gpu.backend</c>
/// spawns a worker in-process (hello, 64 px eevee warm, dispose) and reports its Blender
/// version, build hash, worker digest, GPU backend and elapsed time. The CLI runs with its
/// own state dir, store and absent config file; <c>ABL_BLENDER</c> comes from the lane.
/// 009 T022 (AC-12 with real Blender, on a host with an OptiX GPU): <c>abl doctor --check gpu.render</c>
/// spawns its disposable worker through the real supervisor, renders twice with
/// <c>cycles_gpu</c>, reads the worker's own <c>duration_ms</c> and <c>device</c>, and leaves
/// nothing behind in the runtime, store or renders dirs. That case also carries
/// <c>OptixQualification</c>: the hosted lane has no GPU and excludes it.
/// </summary>
[Trait("Category", "BlenderIntegration")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class DoctorRoundtripTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("abl-doctor-").FullName;

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

    [Fact]
    public async Task AC25_TheRoundTripSpawnsBlenderAndReportsItsIdentity()
    {
        var (code, stdout, stderr) = await RunDoctorAsync("gpu.backend", TimeSpan.FromMinutes(3));

        Assert.True(code == 0, $"doctor exited {code}: {stdout}{stderr}");
        var checks = Checks(stdout);
        var roundtrip = checks["worker.roundtrip"];
        Assert.Equal("pass", roundtrip["status"]!.GetValue<string>());
        var detail = roundtrip["detail"]!;
        Assert.StartsWith("5.2.", detail["blenderVersion"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{6,}$", detail["buildHash"]!.GetValue<string>());
        Assert.Equal(WorkerSource.Digest(Path.Combine(Path.GetDirectoryName(AblCli.Dll)!, "worker")), detail["workerSourceSha256"]!.GetValue<string>());
        Assert.Equal(checks["worker.files"]["detail"]!["digest"]!.GetValue<string>(), detail["workerSourceSha256"]!.GetValue<string>());
        Assert.True(detail["elapsedMs"]!.GetValue<long>() > 0);
        Assert.Contains(detail["gpu"]!["backend"]!.GetValue<string>(), new[] { "optix", "metal", "none" });
        Assert.Contains(checks["gpu.backend"]["status"]!.GetValue<string>(), new[] { "pass", "warn" });

        // The round-trip leaves no worker socket behind in its runtime dir.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "run")));
    }

    [Fact]
    [Trait("Category", "OptixQualification")]
    public async Task AC12_GpuRenderRendersTwiceOnOptixAndLeavesNothingBehind()
    {
        var store = Path.Combine(_root, "store");
        var renders = Path.Combine(_root, "renders");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(renders);
        File.WriteAllText(Path.Combine(store, "keep.txt"), "store");
        File.WriteAllText(Path.Combine(renders, "keep.txt"), "renders");
        var before = Listing(store, renders);

        var (code, stdout, stderr) = await RunDoctorAsync("gpu.render", TimeSpan.FromMinutes(5));

        // A slow second render is a warn, which still exits 0.
        Assert.True(code == 0, $"doctor exited {code}: {stdout}{stderr}");
        var checks = Checks(stdout);
        Assert.Equal("pass", checks["worker.roundtrip"]["status"]!.GetValue<string>());
        var render = checks["gpu.render"];
        Assert.True(render["status"]!.GetValue<string>() is "pass" or "warn", $"gpu.render: {render.ToJsonString()}");
        var detail = render["detail"]!;
        Assert.Equal("optix", detail["device"]!["backend"]!.GetValue<string>());
        Assert.True(detail["firstMs"]!.GetValue<long>() >= 0, detail.ToJsonString());
        Assert.True(detail["secondMs"]!.GetValue<long>() >= 0, detail.ToJsonString());
        Assert.Equal(32, detail["res"]!.GetValue<int>());
        Assert.Equal(1, detail["samples"]!.GetValue<int>());

        // The render dir, both PNGs and both workers' sockets are gone; the store and the
        // renders dir are as they were.
        var run = Path.Combine(_root, "run");
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(run), p => Path.GetFileName(p).StartsWith("d-gpu-", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(run));
        Assert.Equal(before, Listing(store, renders));
    }

    /// <summary>
    /// <c>abl doctor --json --check &lt;id&gt;</c> with its own state dir, store, renders and
    /// runtime dir and an absent config file; the lane's <c>ABL_BLENDER</c> is kept.
    /// </summary>
    private Task<(int Code, string Stdout, string Stderr)> RunDoctorAsync(string check, TimeSpan timeout)
    {
        var start = AblCli.Start("doctor", "--json", "--check", check);
        start.Environment["XDG_STATE_HOME"] = Path.Combine(_root, "state");
        start.Environment["ABL_STORE"] = Path.Combine(_root, "store");
        start.Environment["ABL_RENDERS"] = Path.Combine(_root, "renders");
        start.Environment["ABL_CONFIG"] = Path.Combine(_root, "config.json"); // absent
        start.Environment["ABL_RUNTIME_DIR"] = Path.Combine(_root, "run");
        foreach (var name in new[] { "ABL_WORKER_DIR", "ABL_DESKTOP_ENDPOINT", "ABL_DESKTOP_TOKEN" })
        {
            start.Environment.Remove(name);
        }

        return AblCli.RunAsync(start, timeout);
    }

    private static Dictionary<string, JsonObject> Checks(string stdout) =>
        JsonNode.Parse(stdout)!["checks"]!.AsArray().ToDictionary(c => c!["id"]!.GetValue<string>(), c => c!.AsObject());

    /// <summary>Every file under the dirs, with its size, sorted (the AC-12 before/after listing).</summary>
    private static List<string> Listing(params string[] dirs) =>
        [.. dirs.SelectMany(d => Directory.Exists(d)
                ? Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories)
                    .Select(p => $"{p} {(File.Exists(p) ? new FileInfo(p).Length : -1)}")
                : [$"{d} absent"])
            .Order(StringComparer.Ordinal)];
}
