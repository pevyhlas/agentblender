using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Cli;
using Abl.Core.Config;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 009 T008 (AC-07, AC-08, AC-10, AC-13): the host GPU contract over scripted fake workers.
/// Every guarded path is driven on MCP, REST and the CLI's text lines, with the host
/// platform injected as linux-x64 and darwin-arm64. Hints are compared against literal
/// strings here, never against the <c>GpuGuard</c> constants; refused cells are proven by
/// the fakes' frame log (no <c>render</c>) and the lease's attempt counter (no acquisition).
/// </summary>
public sealed class GpuContractTests
{
    private const string RenderFinalHint = "use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback";
    private const string EngineHint = "pass engine=cycles_cpu or engine=eevee";
    private const string MacSuffix = "; on macOS a daemon started inside an agent sandbox cannot see Metal, see abl doctor gpu.backend";

    public static TheoryData<string, string, HostPlatform> GuardedPaths()
    {
        var data = new TheoryData<string, string, HostPlatform>();
        foreach (var platform in new[] { HostPlatform.Linux, HostPlatform.MacOS })
        {
            data.Add("render_final/session", "wait", platform);
            data.Add("render_final/session", "fail", platform);
            data.Add("render_final/checkpoint", "wait", platform);
            data.Add("render_final/checkpoint", "fail", platform);
            data.Add("scene_preview", "-", platform);
            data.Add("batch_run", "-", platform);
            data.Add("delivery_export", "-", platform);
        }

        return data;
    }

    private static string ExpectedHint(string path, HostPlatform platform) =>
        (path.StartsWith("render_final", StringComparison.Ordinal) ? RenderFinalHint : EngineHint)
        + (platform == HostPlatform.MacOS ? MacSuffix : "");

    /// <summary>AC-07: hello backend <c>none</c> → <c>gpu_unavailable</c> and the row's hint, no render, no lease.</summary>
    [Theory]
    [MemberData(nameof(GuardedPaths))]
    public async Task NoBackendRefusesBeforeRenderAndLease(string path, string lease, HostPlatform platform)
    {
        await using var h = new GpuHarness(platform);
        var call = await PrepareAsync(h, path, lease);
        var execsBefore = h.FrameCount("exec");

        var expected = ExpectedHint(path, platform);
        var mcp = await GpuHarness.McpErrorAsync(call.Mcp);
        Assert.Equal(("gpu_unavailable", expected), mcp);
        var (status, body) = await h.RestAsync(call.Op, call.Rest);
        Assert.Equal(503, status);
        Assert.Equal("gpu_unavailable", body["kind"]!.GetValue<string>());
        Assert.Equal(expected, body["hint"]!.GetValue<string>());
        Assert.Equal(("gpu_unavailable", expected), GpuHarness.CliError(body));

        Assert.Equal(0, h.RenderFrames());
        Assert.Equal(0, h.Lease.Attempts);
        Assert.Equal(execsBefore, h.FrameCount("exec")); // batch: refused before any variant runs
        if (path == "delivery_export")
        {
            Assert.Empty(Directory.Exists(call.OutDir) ? Directory.GetDirectories(call.OutDir) : []); // before staging
        }

        if (path == "batch_run")
        {
            Assert.False(Directory.Exists(Path.Combine(h.Options.RendersDir, "batch")), "a refused batch creates no output directory");
        }
    }

    /// <summary>
    /// AC-08: the hello reported OptiX, the worker's device check fails → the same kind and
    /// hint as the preflight refusal, through the transient (TryMap) and session
    /// (MapSessionFault) mappings; a session-owned worker stays usable.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedPaths))]
    public async Task WorkerRaisedGpuUnavailableCarriesThePathHint(string path, string lease, HostPlatform platform)
    {
        await using var h = new GpuHarness(platform);
        h.LocalScript.Gpu = FakeGpu.Optix();
        h.LocalScript.GpuCheckFails = true;
        var call = await PrepareAsync(h, path, lease);

        var expected = ExpectedHint(path, platform);
        Assert.Equal(("gpu_unavailable", expected), await GpuHarness.McpErrorAsync(call.Mcp));
        var (status, body) = await h.RestAsync(call.Op, call.Rest);
        Assert.Equal(503, status);
        Assert.Equal("gpu_unavailable", body["kind"]!.GetValue<string>());
        Assert.Equal(expected, body["hint"]!.GetValue<string>());
        Assert.Equal(("gpu_unavailable", expected), GpuHarness.CliError(body));
        Assert.Equal(2, h.RenderFrames("cycles_gpu")); // the request reached the worker on both surfaces

        if (call.Session is { } session)
        {
            var exec = await h.Sessions.ExecAsync(session, "pass", null, default);
            Assert.NotNull(exec.Checkpoint);
            Assert.Equal("ready", Assert.Single(h.Sessions.List(), s => s.Id == session).State);
        }
    }

    public static TheoryData<string, string> OtherEngines()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { "render_final", "scene_preview", "batch_run", "delivery_export" })
        {
            foreach (var engine in new[] { "eevee", "cycles_cpu" })
            {
                data.Add(path, engine);
            }
        }

        return data;
    }

    /// <summary>
    /// AC-07 last guard-matrix row ("another engine → unaffected") through the services: with
    /// the default hello backend <c>none</c>, an <c>eevee</c> or <c>cycles_cpu</c> request on
    /// every guarded path succeeds and sends exactly one <c>render</c> frame with that engine.
    /// </summary>
    [Theory]
    [MemberData(nameof(OtherEngines))]
    public async Task NoBackendLeavesOtherEnginesUnaffected(string path, string engine)
    {
        await using var h = new GpuHarness(HostPlatform.Linux);
        Assert.Equal("none", h.LocalScript.Gpu["backend"]!.GetValue<string>());
        JsonObject body;
        switch (path)
        {
            case "render_final":
            case "scene_preview":
            {
                var session = await h.Sessions.OpenAsync("gpu", default);
                body = path == "render_final"
                    ? new JsonObject { ["session"] = session.Id, ["engine"] = engine, ["res"] = 64 }
                    : new JsonObject { ["session"] = session.Id, ["size"] = 64, ["engine"] = engine };
                break;
            }
            case "batch_run":
                await h.FreezeAsync("art");
                body = new JsonObject { ["artifact"] = "art", ["paramTable"] = JsonNode.Parse("""[{"v":1}]"""), ["res"] = 64, ["engine"] = engine };
                break;
            default:
                await h.FreezeAsync("art");
                body = new JsonObject { ["artifact"] = "art", ["out"] = Path.Combine(h.Root, "deliveries"), ["res"] = 64, ["engine"] = engine };
                break;
        }

        var (status, rest) = await h.RestAsync(path, body);

        Assert.True(status == 200, rest.ToJsonString());
        Assert.Equal(1, h.RenderFrames());
        Assert.Equal(1, h.RenderFrames(engine));
        if (path is "render_final" or "batch_run")
        {
            var expected = engine == "cycles_cpu" ? """{"backend":"cpu","name":null}""" : null;
            Assert.Equal(expected, rest["device"]?.ToJsonString());
            Assert.Equal(expected is not null, rest.ContainsKey("device"));
        }
    }

    /// <summary>
    /// Plan D6: only <c>gpu_unavailable</c> takes the GPU hint. Any other worker failure on a
    /// guarded session path keeps the caller's hint, and the session stays usable.
    /// </summary>
    [Theory]
    [InlineData("render_final", "adjust the render arguments and retry; the checkpoint is unchanged")]
    [InlineData("scene_preview", "retry the operation; the durable checkpoint is unchanged")]
    public async Task OtherSessionRenderFailuresKeepTheCallerHint(string path, string hint)
    {
        await using var h = new GpuHarness(HostPlatform.MacOS);
        h.LocalScript.Gpu = FakeGpu.Optix();
        h.LocalScript.RenderErrorKind = "RuntimeError";
        var session = await h.Sessions.OpenAsync("gpu", default);
        var body = path == "render_final"
            ? new JsonObject { ["session"] = session.Id, ["engine"] = "cycles_gpu", ["res"] = 64 }
            : new JsonObject { ["session"] = session.Id, ["size"] = 64, ["engine"] = "cycles_gpu" };

        var (status, rest) = await h.RestAsync(path, body);

        Assert.NotEqual(200, status);
        Assert.Equal("exec_failed", rest["kind"]!.GetValue<string>());
        Assert.Equal(hint, rest["hint"]!.GetValue<string>());
        Assert.Equal(1, h.RenderFrames("cycles_gpu"));
        Assert.Equal("ready", Assert.Single(h.Sessions.List(), s => s.Id == session.Id).State);
    }

    /// <summary>AC-07 <c>cpu_fallback</c> row: <c>cycles_cpu</c> without the lease, <c>device.fallback: gpu_unavailable</c>.</summary>
    [Theory]
    [InlineData("session")]
    [InlineData("checkpoint")]
    public async Task NoBackendCpuFallbackRendersCyclesCpuWithoutTheLease(string source)
    {
        await using var h = new GpuHarness(HostPlatform.Linux);
        var session = await h.Sessions.OpenAsync("gpu", default);
        var result = await h.Sessions.RenderFinalAsync(
            source == "session" ? session.Id : null, source == "checkpoint" ? session.Head : null,
            "cycles_gpu", 64, null, null, "cpu_fallback", 0, default);

        Assert.Equal(1, h.RenderFrames("cycles_cpu"));
        Assert.Equal(0, h.RenderFrames("cycles_gpu"));
        Assert.Equal(0, h.Lease.Attempts);
        Assert.Equal("""{"backend":"cpu","name":null,"fallback":"gpu_unavailable"}""", Json(result.Device));
    }

    /// <summary>
    /// AC-08 / plan D7: <c>render_final</c> + <c>cpu_fallback</c> with a worker-raised
    /// <c>gpu_unavailable</c> → one GPU frame (lease held), then one CPU frame on the same
    /// worker with the lease already released, <c>fallback: gpu_unavailable</c> on every surface.
    /// </summary>
    [Theory]
    [InlineData("session")]
    [InlineData("checkpoint")]
    public async Task WorkerRaisedGpuUnavailableUnderCpuFallbackRetriesOnceOnTheCpu(string source)
    {
        await using var h = new GpuHarness(HostPlatform.Linux);
        h.LocalScript.Gpu = FakeGpu.Optix();
        h.LocalScript.GpuCheckFails = true;
        var session = await h.Sessions.OpenAsync("gpu", default);
        var target = source == "session" ? new JsonObject { ["session"] = session.Id } : new JsonObject { ["checkpoint"] = session.Head };
        target["engine"] = "cycles_gpu";
        target["res"] = 64;
        target["lease"] = "cpu_fallback";

        // Probe the lease while each render frame is on the worker (plan D7: the CPU retry
        // must not hold it). The probe takes the slot only if it is free and returns it at once.
        var probes = new List<(string? Engine, bool LeaseFree, int AttemptsBefore)>();
        h.LocalScript.OnRender = async frame =>
        {
            var attemptsBefore = h.Lease.Attempts;
            using var probe = await h.Lease.TryAcquireAsync(TimeSpan.Zero, default);
            probes.Add((frame.Engine, probe is not null, attemptsBefore));
        };

        var (status, body) = await h.RestAsync("render_final", target);
        h.LocalScript.OnRender = null;

        Assert.Equal(200, status);
        var renders = h.Frames.Where(f => f.Op == "render").ToList();
        Assert.Equal(new[] { "cycles_gpu", "cycles_cpu" }, renders.Select(f => f.Engine));
        Assert.Single(renders.Select(f => f.Worker).Distinct());
        // GPU frame: the op's one acquisition is held. CPU frame: released, and not re-taken
        // (the only extra attempt is the GPU-frame probe).
        Assert.Equal(new (string?, bool, int)[] { ("cycles_gpu", false, 1), ("cycles_cpu", true, 2) }, probes);
        Assert.Equal(3, h.Lease.Attempts); // one op acquisition + two probes
        using (var free = await h.Lease.TryAcquireAsync(TimeSpan.Zero, default))
        {
            Assert.NotNull(free); // released after the retry
        }

        const string expected = """{"backend":"cpu","name":null,"fallback":"gpu_unavailable"}""";
        Assert.Equal(expected, body["device"]!.ToJsonString());
        Assert.Equal("device cpu (fallback gpu_unavailable)", CliText.DeviceLine(body));
        Assert.Equal(expected, Sidecar(body["sidecarPath"]!.GetValue<string>())["device"]!.ToJsonString());
        var mcp = await h.Tools.RenderFinal(
            session: target["session"]?.GetValue<string>(), checkpoint: target["checkpoint"]?.GetValue<string>(),
            engine: "cycles_gpu", res: 64, lease: "cpu_fallback");
        Assert.Equal(expected, JsonNode.Parse(GpuHarness.McpJson(mcp))!["device"]!.ToJsonString());
    }

    public static TheoryData<string, string, string, bool, string?, string?> RenderDevices() => new()
    {
        // hello backend, engine, lease, lease held, device JSON (null = absent), CLI line
        { "optix", "cycles_gpu", "wait", false, """{"backend":"optix","name":"Test OptiX GPU"}""", "device optix (Test OptiX GPU)" },
        { "metal", "cycles_gpu", "wait", false, """{"backend":"metal","name":"Test Metal GPU"}""", "device metal (Test Metal GPU)" },
        { "optix", "cycles_cpu", "wait", false, """{"backend":"cpu","name":null}""", "device cpu" },
        { "none", "cycles_gpu", "cpu_fallback", false, """{"backend":"cpu","name":null,"fallback":"gpu_unavailable"}""", "device cpu (fallback gpu_unavailable)" },
        { "optix", "cycles_gpu", "cpu_fallback", true, """{"backend":"cpu","name":null,"fallback":"gpu_busy"}""", "device cpu (fallback gpu_busy)" },
        { "optix", "eevee", "wait", false, null, null },
        { "optix", "workbench", "wait", false, null, null },
    };

    /// <summary>AC-10: <c>render_final</c>'s <c>device</c> is the same on MCP, REST, the CLI line and the sidecar.</summary>
    [Theory]
    [MemberData(nameof(RenderDevices))]
    public async Task RenderFinalDeviceParity(string backend, string engine, string lease, bool held, string? device, string? cliLine)
    {
        await using var h = new GpuHarness(HostPlatform.Linux);
        h.LocalScript.Gpu = Gpu(backend);
        var session = await h.Sessions.OpenAsync("gpu", default);
        using var hold = held ? await h.Lease.TryAcquireAsync(TimeSpan.Zero, default) : null;

        var mcp = JsonNode.Parse(GpuHarness.McpJson(
            await h.Tools.RenderFinal(session: session.Id, engine: engine, res: 64, lease: lease)))!.AsObject();
        var (status, rest) = await h.RestAsync("render_final", new JsonObject
        {
            ["session"] = session.Id, ["engine"] = engine, ["res"] = 64, ["lease"] = lease,
        });

        Assert.Equal(200, status);
        foreach (var payload in new[] { mcp, rest, Sidecar(rest["sidecarPath"]!.GetValue<string>()) })
        {
            Assert.Equal(device, payload["device"]?.ToJsonString());
            Assert.Equal(device is not null, payload.ContainsKey("device"));
        }

        Assert.Equal(cliLine, CliText.DeviceLine(rest));
    }

    public static TheoryData<string, string?, string?> BatchDevices() => new()
    {
        { "cycles_gpu", """{"backend":"optix","name":"Test OptiX GPU"}""", "optix (Test OptiX GPU)" },
        { "cycles_cpu", """{"backend":"cpu","name":null}""", "cpu" },
        { "eevee", null, null },
    };

    /// <summary>AC-10: <c>batch_run</c>'s <c>device</c> on REST, the MCP text block and the CLI line.</summary>
    [Theory]
    [MemberData(nameof(BatchDevices))]
    public async Task BatchRunDeviceParity(string engine, string? device, string? text)
    {
        await using var h = new GpuHarness(HostPlatform.Linux);
        h.LocalScript.Gpu = FakeGpu.Optix();
        await h.FreezeAsync("art");

        using var table = JsonDocument.Parse("""[{"v":1},{"v":2}]""");
        var blocks = (await h.Tools.BatchRun("art", table.RootElement, 64, 2, engine)).ToList();
        var lines = Assert.IsType<TextContentBlock>(blocks[1]).Text.Split('\n');
        Assert.StartsWith("sheet: ", lines[0]);
        if (text is null)
        {
            Assert.DoesNotContain(lines, l => l.StartsWith("device", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal("device: " + text, lines[1]);
        }

        var (status, rest) = await h.RestAsync("batch_run", new JsonObject
        {
            ["artifact"] = "art", ["paramTable"] = JsonNode.Parse("""[{"v":1}]"""), ["res"] = 64, ["engine"] = engine,
        });
        Assert.Equal(200, status);
        Assert.Equal(device, rest["device"]?.ToJsonString());
        Assert.Equal(text is null ? null : "device " + text, CliText.DeviceLine(rest));
    }

    /// <summary>
    /// AC-13 (FR-007): for <c>cycles_gpu</c>, a desktop worker with backend <c>none</c> is a
    /// bringup failure and the legacy hello shape is <c>worker_upgrade_required</c>; both fall
    /// back to the local worker, which renders. An <c>eevee</c> render stays on the <c>none</c> desktop.
    /// </summary>
    [Theory]
    [InlineData("none", "cycles_gpu", "local", "desktop_bringup_failed")]
    [InlineData("legacy", "cycles_gpu", "local", "worker_upgrade_required")]
    [InlineData("none", "eevee", "desktop", null)]
    [InlineData("legacy", "eevee", "local", "worker_upgrade_required")]
    [InlineData("optix", "cycles_gpu", "desktop", null)]
    public async Task DesktopRenderRouting(string desktopBackend, string engine, string tier, string? fallback)
    {
        await using var h = new GpuHarness(HostPlatform.Linux, desktopBackend == "legacy" ? FakeGpu.Legacy() : Gpu(desktopBackend));
        h.LocalScript.Gpu = FakeGpu.Optix();
        var session = await h.Sessions.OpenAsync("gpu", default);

        var result = await h.Sessions.RenderFinalAsync(session.Id, null, engine, 64, null, null, "wait", 0, default);

        Assert.Equal(tier, result.Tier);
        Assert.Equal(fallback, result.FallbackReason);
        Assert.Equal(tier == "desktop" ? 1 : 0, h.RenderFrames(engine, desktop: true));
        Assert.Equal(tier == "local" ? 1 : 0, h.RenderFrames(engine));
        if (tier == "local")
        {
            Assert.Equal(0, h.FrameCount("load_bytes")); // bringup stopped before shipping the checkpoint
        }

        if (desktopBackend == "optix")
        {
            Assert.Equal("""{"backend":"optix","name":"Test OptiX GPU"}""", Json(result.Device));
        }
    }

    /// <summary>AC-13 for <c>batch_run</c>: the same desktop bringup rule as <c>render_final</c>.</summary>
    [Theory]
    [InlineData("none", "cycles_gpu", "local", "desktop_bringup_failed")]
    [InlineData("legacy", "cycles_gpu", "local", "worker_upgrade_required")]
    [InlineData("none", "eevee", "desktop", null)]
    public async Task DesktopBatchRouting(string desktopBackend, string engine, string tier, string? fallback)
    {
        await using var h = new GpuHarness(HostPlatform.Linux, desktopBackend == "legacy" ? FakeGpu.Legacy() : Gpu(desktopBackend));
        h.LocalScript.Gpu = FakeGpu.Optix();
        await h.FreezeAsync("art");

        var result = await h.Trajectories.BatchAsync("art", [new JsonObject { ["v"] = 1 }], 64, 1, engine, default);

        Assert.Equal(tier, result.Tier);
        Assert.Equal(fallback, result.FallbackReason);
        Assert.Equal(tier == "desktop" ? 1 : 0, h.RenderFrames(engine, desktop: true));
        Assert.Equal(tier == "local" ? 1 : 0, h.RenderFrames(engine));
    }

    // --- helpers ---

    private sealed record Call(string Op, JsonObject Rest, Func<Task> Mcp, string? Session = null, string? OutDir = null);

    /// <summary>Set up one guarded path's state, returning its MCP call and REST body.</summary>
    private static async Task<Call> PrepareAsync(GpuHarness h, string path, string lease)
    {
        switch (path)
        {
            case "render_final/session":
            {
                var session = await h.Sessions.OpenAsync("gpu", default);
                return new Call("render_final",
                    new JsonObject { ["session"] = session.Id, ["engine"] = "cycles_gpu", ["res"] = 64, ["lease"] = lease },
                    () => h.Tools.RenderFinal(session: session.Id, engine: "cycles_gpu", res: 64, lease: lease), session.Id);
            }
            case "render_final/checkpoint":
            {
                var session = await h.Sessions.OpenAsync("gpu", default);
                return new Call("render_final",
                    new JsonObject { ["checkpoint"] = session.Head, ["engine"] = "cycles_gpu", ["res"] = 64, ["lease"] = lease },
                    () => h.Tools.RenderFinal(checkpoint: session.Head, engine: "cycles_gpu", res: 64, lease: lease));
            }
            case "scene_preview":
            {
                var session = await h.Sessions.OpenAsync("gpu", default);
                return new Call("scene_preview",
                    new JsonObject { ["session"] = session.Id, ["size"] = 64, ["engine"] = "cycles_gpu" },
                    () => h.Tools.ScenePreview(session.Id, 64, "cycles_gpu"), session.Id);
            }
            case "batch_run":
            {
                await h.FreezeAsync("art");
                return new Call("batch_run",
                    new JsonObject { ["artifact"] = "art", ["paramTable"] = JsonNode.Parse("""[{"v":1}]"""), ["res"] = 64, ["engine"] = "cycles_gpu" },
                    async () =>
                    {
                        using var table = JsonDocument.Parse("""[{"v":1}]""");
                        await h.Tools.BatchRun("art", table.RootElement.Clone(), 64, 1, "cycles_gpu");
                    });
            }
            case "delivery_export":
            {
                await h.FreezeAsync("art");
                var outDir = Path.Combine(h.Root, "deliveries");
                return new Call("delivery_export",
                    new JsonObject { ["artifact"] = "art", ["out"] = outDir, ["res"] = 64 }, // cycles_gpu is the default
                    () => h.Tools.DeliveryExport("art", outDir, res: 64), OutDir: outDir);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    private static JsonObject Gpu(string backend) => backend switch
    {
        "optix" => FakeGpu.Optix(),
        "metal" => FakeGpu.Metal(),
        _ => FakeGpu.None(),
    };

    private static string? Json(object? value) => value is null ? null : JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static JsonObject Sidecar(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
}
