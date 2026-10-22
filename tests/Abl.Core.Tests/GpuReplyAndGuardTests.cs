using System.Text.Json.Nodes;
using Abl.Core.Config;
using Abl.Core.Rendering;
using Abl.Core.Workers;
using Abl.Daemon.Api;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// 009 T008: the <c>WorkerReplies</c> hello/render validation matrix (plan contract table),
/// the <c>GpuGuard</c> decision table and hints, the trusted <c>gpu_unavailable</c> kind with
/// its path hint, and the REST status. Hints are literal strings, not the constants.
/// </summary>
public sealed class GpuReplyAndGuardTests
{
    private static JsonObject Hello(string gpu) => new()
    {
        ["blender_version"] = "5.2.1",
        ["python_version"] = "3.13.0",
        ["gpu"] = JsonNode.Parse(gpu),
    };

    private static void ValidateHello(string gpu) => WorkerReplies.Validate("hello", [], Hello(gpu));

    [Theory]
    [InlineData("""{"backend":"optix","devices":[{"name":"RTX","type":"OPTIX"}],"metalrt":null}""")]
    [InlineData("""{"backend":"metal","devices":[{"name":"Apple M<n>","type":"METAL"}],"metalrt":"AUTO"}""")]
    [InlineData("""{"backend":"metal","devices":[{"name":"Apple","type":"METAL"}],"metalrt":null}""")]
    [InlineData("""{"backend":"metal","devices":[{"name":"Apple","type":"METAL"}],"metalrt":"OFF"}""")]
    [InlineData("""{"backend":"none","devices":[],"metalrt":null}""")]
    [InlineData("""{"backend":"none","devices":[]}""")]
    public void ValidHellosPass(string gpu)
    {
        ValidateHello(gpu);
        var capability = WorkerReplies.Gpu(Hello(gpu));
        Assert.Equal(JsonNode.Parse(gpu)!["backend"]!.GetValue<string>(), capability.Backend);
    }

    [Theory]
    [InlineData("""{"optix":true}""")]
    [InlineData("""{"optix":false}""")]
    [InlineData("""{"optix":true,"backend":null}""")]
    public void LegacyHelloShapeIsWorkerUpgradeRequired(string gpu)
    {
        var error = Assert.Throws<AblException>(() => ValidateHello(gpu));
        Assert.Equal("worker_upgrade_required", error.Kind);
        Assert.Equal("update the desktop launcher together with its bundled worker/ to enable chunked transfers", error.Hint);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"backend":"cuda","devices":[]}""")]
    [InlineData("""{"backend":7,"devices":[]}""")]
    [InlineData("""{"backend":"optix"}""")]
    [InlineData("""{"backend":"optix","devices":[]}""")] // empty iff none
    [InlineData("""{"backend":"none","devices":[{"name":"CPU","type":"CPU"}]}""")]
    [InlineData("""{"backend":"optix","devices":[{"name":"x","type":"METAL"}]}""")]
    [InlineData("""{"backend":"optix","devices":[{"name":"x"}]}""")]
    [InlineData("""{"backend":"optix","devices":[{"type":"OPTIX"}]}""")]
    [InlineData("""{"backend":"optix","devices":["x"]}""")]
    [InlineData("""{"backend":"optix","devices":[{"name":"bell\u0007","type":"OPTIX"}]}""")] // C0
    [InlineData("""{"backend":"optix","devices":[{"name":"del\u007f","type":"OPTIX"}]}""")]
    [InlineData("""{"backend":"optix","devices":[{"name":"nel\u0085","type":"OPTIX"}]}""")] // C1
    [InlineData("""{"backend":"optix","devices":[{"name":"x","type":"OPTIX"}],"metalrt":"AUTO"}""")] // metalrt only on metal
    [InlineData("""{"backend":"metal","devices":[{"name":"x","type":"METAL"}],"metalrt":"SOMETIMES"}""")]
    public void MalformedGpuIsAnInvalidReply(string gpu) =>
        Assert.Throws<InvalidDataException>(() => ValidateHello(gpu));

    [Fact]
    public void DeviceListAndNameAreBounded()
    {
        var nine = new JsonArray([.. Enumerable.Range(0, 9).Select(i => (JsonNode)new JsonObject { ["name"] = $"g{i}", ["type"] = "OPTIX" })]);
        Assert.Throws<InvalidDataException>(() => ValidateHello(new JsonObject { ["backend"] = "optix", ["devices"] = nine }.ToJsonString()));

        ValidateHello(Device(new string('n', 128)));
        Assert.Throws<InvalidDataException>(() => ValidateHello(Device(new string('n', 129))));

        static string Device(string name) => new JsonObject
        {
            ["backend"] = "optix",
            ["devices"] = new JsonArray(new JsonObject { ["name"] = name, ["type"] = "OPTIX" }),
        }.ToJsonString();
    }

    private static void ValidateRender(string engine, string? device, string? helloBackend)
    {
        var reply = new JsonObject { ["duration_ms"] = 1.0, ["out"] = "/r/x.png" };
        if (device is not null) reply["device"] = JsonNode.Parse(device);
        WorkerReplies.Validate("render", new JsonObject { ["engine"] = engine }, reply, helloBackend);
    }

    [Theory]
    [InlineData("cycles_gpu", """{"backend":"optix","name":"RTX"}""", "optix")]
    [InlineData("cycles_gpu", """{"backend":"metal","name":"Apple"}""", "metal")]
    [InlineData("cycles_cpu", """{"backend":"cpu","name":null}""", "optix")]
    [InlineData("cycles_cpu", """{"backend":"cpu"}""", "none")]
    [InlineData("eevee", null, "optix")]
    [InlineData("workbench", null, "none")]
    public void ValidRenderDevicesPass(string engine, string? device, string? helloBackend) =>
        ValidateRender(engine, device, helloBackend);

    [Theory]
    [InlineData("cycles_gpu", null, "optix")] // required for Cycles
    [InlineData("cycles_cpu", null, "optix")]
    [InlineData("eevee", """{"backend":"cpu","name":null}""", "optix")] // absent for eevee/workbench
    [InlineData("workbench", """{"backend":"optix","name":"RTX"}""", "optix")]
    [InlineData("cycles_gpu", """{"backend":"metal","name":"Apple"}""", "optix")] // must equal the hello backend
    [InlineData("cycles_gpu", """{"backend":"optix","name":"RTX"}""", null)]
    [InlineData("cycles_gpu", """{"backend":"cpu","name":null}""", "optix")]
    [InlineData("cycles_cpu", """{"backend":"optix","name":"RTX"}""", "optix")] // cpu for cycles_cpu
    [InlineData("cycles_gpu", """{"backend":"optix"}""", "optix")] // a GPU device has a name
    [InlineData("cycles_gpu", """{"backend":"optix","name":"tab\u0009"}""", "optix")]
    [InlineData("cycles_gpu", """{"backend":"optix","name":"csi\u009b"}""", "optix")]
    [InlineData("cycles_gpu", """{"backend":"cuda","name":"x"}""", "optix")]
    public void MalformedRenderDeviceIsAnInvalidReply(string engine, string? device, string? helloBackend) =>
        Assert.Throws<InvalidDataException>(() => ValidateRender(engine, device, helloBackend));

    // --- GpuGuard ---

    private static readonly GpuCapability None = GpuCapability.None;
    private static readonly GpuCapability Optix = new("optix", [new GpuDevice("RTX", "OPTIX")], null);
    private static readonly GpuCapability Metal = new("metal", [new GpuDevice("Apple", "METAL")], "AUTO");

    [Theory]
    [InlineData(GpuPath.RenderFinal, "wait", GpuDecisionKind.Refuse)]
    [InlineData(GpuPath.RenderFinal, "fail", GpuDecisionKind.Refuse)]
    [InlineData(GpuPath.RenderFinal, "cpu_fallback", GpuDecisionKind.RenderCpuFallback)]
    [InlineData(GpuPath.ScenePreview, null, GpuDecisionKind.Refuse)]
    [InlineData(GpuPath.BatchRun, null, GpuDecisionKind.Refuse)]
    [InlineData(GpuPath.DeliveryExport, null, GpuDecisionKind.Refuse)]
    [InlineData(GpuPath.DeliveryExport, "cpu_fallback", GpuDecisionKind.Refuse)] // only render_final takes lease
    public void NoBackendDecisions(GpuPath path, string? lease, GpuDecisionKind expected)
    {
        Assert.Equal(expected, GpuGuard.Decide(None, "cycles_gpu", lease, path, HostPlatform.Linux).Kind);
        foreach (var engine in new[] { "eevee", "cycles_cpu", "workbench" })
            Assert.Equal(GpuDecisionKind.Render, GpuGuard.Decide(None, engine, lease, path, HostPlatform.Linux).Kind);
        foreach (var gpu in new[] { Optix, Metal })
            Assert.Equal(GpuDecisionKind.Render, GpuGuard.Decide(gpu, "cycles_gpu", lease, path, HostPlatform.Linux).Kind);
    }

    [Theory]
    [InlineData(GpuPath.RenderFinal, HostPlatform.Linux, "use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback")]
    [InlineData(GpuPath.ScenePreview, HostPlatform.Linux, "pass engine=cycles_cpu or engine=eevee")]
    [InlineData(GpuPath.BatchRun, HostPlatform.Linux, "pass engine=cycles_cpu or engine=eevee")]
    [InlineData(GpuPath.DeliveryExport, HostPlatform.Linux, "pass engine=cycles_cpu or engine=eevee")]
    [InlineData(GpuPath.RenderFinal, HostPlatform.MacOS, "use engine=cycles_cpu or engine=eevee, or lease=cpu_fallback; on macOS a daemon started inside an agent sandbox cannot see Metal, see abl doctor gpu.backend")]
    [InlineData(GpuPath.ScenePreview, HostPlatform.MacOS, "pass engine=cycles_cpu or engine=eevee; on macOS a daemon started inside an agent sandbox cannot see Metal, see abl doctor gpu.backend")]
    [InlineData(GpuPath.BatchRun, HostPlatform.MacOS, "pass engine=cycles_cpu or engine=eevee; on macOS a daemon started inside an agent sandbox cannot see Metal, see abl doctor gpu.backend")]
    [InlineData(GpuPath.DeliveryExport, HostPlatform.MacOS, "pass engine=cycles_cpu or engine=eevee; on macOS a daemon started inside an agent sandbox cannot see Metal, see abl doctor gpu.backend")]
    public void HintsAreExact(GpuPath path, HostPlatform host, string hint)
    {
        Assert.Equal(hint, GpuGuard.HintFor(path, host));
        if (path != GpuPath.RenderFinal) Assert.DoesNotContain("lease", hint);
        var decision = GpuGuard.Decide(None, "cycles_gpu", path == GpuPath.RenderFinal ? "wait" : null, path, host);
        Assert.Equal(hint, decision.Hint);
        Assert.Equal(hint, GpuGuard.Unavailable(decision, WorkerTier.Local).Hint);
    }

    // --- trusted kind, path hint, REST status ---

    [Fact]
    public void GpuUnavailableIsTrustedAndCarriesThePathHint()
    {
        Assert.Equal("gpu_unavailable", WorkerErrors.OpKind("gpu_unavailable"));
        var raised = new WorkerOpException("gpu_unavailable", "cycles_gpu: no enabled METAL device", null);

        Assert.True(WorkerErrors.TryMap(raised, out var unguarded));
        Assert.Equal("gpu_unavailable", unguarded.Kind);
        Assert.Null(unguarded.Hint);

        Assert.True(WorkerErrors.TryMap(raised, out var batch, GpuPath.BatchRun, HostPlatform.MacOS));
        Assert.Equal("pass engine=cycles_cpu or engine=eevee; on macOS a daemon started inside an agent sandbox cannot see Metal, see abl doctor gpu.backend", batch.Hint);

        // Other kinds keep their mapping and get no GPU hint on a guarded path.
        Assert.True(WorkerErrors.TryMap(new WorkerOpException("KeyError", "x", null), out var other, GpuPath.RenderFinal, HostPlatform.Linux));
        Assert.Equal("exec_failed", other.Kind);
        Assert.Null(other.Hint);
    }

    [Fact]
    public void RestMapsGpuUnavailableTo503() => Assert.Equal(503, ApiEndpoints.StatusFor("gpu_unavailable"));
}
