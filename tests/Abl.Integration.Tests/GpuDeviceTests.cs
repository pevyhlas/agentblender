using System.Text.Json.Nodes;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// Feature 009 T009, the real-Blender half, on a Linux host with an OptiX GPU: the hello reports
/// the backend without changing device preferences, even on a second, mid-life hello
/// (AC-02, the <c>use</c> part of AC-03); a <c>cycles_gpu</c> render on 5.2.x assigns its
/// denoiser without hitting the <c>--background</c> enum gotcha (plan D5, Risks) and restores
/// the device preferences after it succeeds (the OptiX success half of AC-09); a
/// <c>cycles_cpu</c> render reports the CPU device and leaves the GPU preferences untouched
/// (AC-06). Preferences are read by an independent exec script, because add-on preferences
/// are not saved in a checkpoint. Devices are identified only by a per-type label
/// (<c>OPTIX#0</c>), never by id or name. Category <c>OptixQualification</c>: run by hand on
/// that host; the hosted lane has no GPU and excludes it.
/// </summary>
[Collection("blender")]
[Trait("Category", "OptixQualification")]
public class GpuDeviceTests(BlenderFixture fixture)
{
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(60);

    // Independent of cycles_devices: reads the preferences directly. With params.flip it first
    // inverts every existing entry's `use`, so the snapshot is not the enumeration default and
    // a hello that enables devices (as an older worker did at startup) would show up as a difference.
    // Also used by MetalQualificationTests (009 T030, AC-09 Metal half).
    internal const string PreferenceSnapshot = """
        import bpy, json
        prefs = bpy.context.preferences.addons["cycles"].preferences
        if params.get("flip"):
            for d in prefs.devices:
                d.use = not d.use
        counts = {}
        entries = {}
        for d in prefs.devices:
            counts[d.type] = counts.get(d.type, 0) + 1
            entries[f"{d.type}#{counts[d.type] - 1}"] = d.use
        with open(params["out"], "w", encoding="utf-8") as out:
            json.dump({"computeDeviceType": prefs.compute_device_type, "entries": entries}, out)
        """;

    [Fact(Timeout = 300_000)]
    public async Task SecondHello_ReportsOptixAndLeavesExistingPreferenceEntriesUnchanged()
    {
        Assert.True(fixture.BlenderAvailable, "T009 requires ABL_BLENDER (a Blender 5.2.1 with an OptiX GPU)");
        var ct = CancellationToken.None;
        var script = Path.Combine(fixture.Root, "prefs-snapshot.py");
        await File.WriteAllTextAsync(script, PreferenceSnapshot, ct);
        var worker = await fixture.Pool.TakeAsync(ct); // spawn ran the first hello
        try
        {
            Assert.Equal("optix", worker.Gpu.Backend);
            Assert.NotEmpty(worker.Gpu.Devices);

            var before = await SnapshotAsync(worker, script, "before", flip: true, ct);
            var hello = await worker.RequestAsync("hello", null, OpTimeout, ct);
            var after = await SnapshotAsync(worker, script, "after", flip: false, ct);

            Assert.Equal("optix", hello["gpu"]!["backend"]!.GetValue<string>());
            // Hello assigns no preference property (plan D1).
            AssertPreferencesUnchanged(before, after);
        }
        finally
        {
            await worker.ShutdownAsync();
            await worker.DisposeAsync();
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task CyclesGpuRender_On52_AssignsTheDenoiserWithoutTheBackgroundGotcha()
    {
        Assert.True(fixture.BlenderAvailable, "T009 requires ABL_BLENDER (a Blender 5.2.1 with an OptiX GPU)");
        var record = Path.Combine(fixture.Root, $"denoiser-{Guid.NewGuid():N}.json");
        var session = await fixture.Sessions.OpenAsync("gpu-denoiser", default);
        try
        {
            // Denoising on, so the render uses the denoiser that _enable_gpu assigned. The
            // wrapper records the value the selection left on scene.cycles.
            await fixture.Sessions.ExecAsync(session.Id, """
                import bpy, json, __main__
                bpy.context.scene.cycles.use_denoising = True
                original = __main__._enable_gpu
                def recording(scene):
                    device = original(scene)
                    with open(params["record"], "w", encoding="utf-8") as out:
                        json.dump({"denoiser": scene.cycles.denoiser, "device": scene.cycles.device}, out)
                    return device
                __main__._enable_gpu = recording
                """, new JsonObject { ["record"] = record }, default);
            var before = await SessionSnapshotAsync(session.Id, "gpu-before", flip: true);
            var render = await fixture.Sessions.RenderFinalAsync(
                session.Id, null, "cycles_gpu", 32, 4, null, "wait", 0, default);
            var after = await SessionSnapshotAsync(session.Id, "gpu-after", flip: false);

            Assert.True(new FileInfo(render.Path).Length > 0);
            Assert.Equal("optix", render.Device?.Backend);
            Assert.Null(render.Device?.Fallback);
            var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(render.SidecarPath))!;
            Assert.StartsWith("5.2.", sidecar["blenderVersion"]!.GetValue<string>(), StringComparison.Ordinal);
            var selected = JsonNode.Parse(await File.ReadAllTextAsync(record))!;
            Assert.Equal("OPTIX", selected["denoiser"]!.GetValue<string>());
            Assert.Equal("GPU", selected["device"]!.GetValue<string>());
            // AC-09, success half: select set compute_device_type = OPTIX and enabled every
            // entry for this render; the worker's finally restores both on success too.
            AssertPreferencesUnchanged(before, after);
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task CyclesCpuRender_ReportsTheCpuDevice()
    {
        Assert.True(fixture.BlenderAvailable, "T009 requires ABL_BLENDER (a Blender 5.2.1 with an OptiX GPU)");
        var session = await fixture.Sessions.OpenAsync("cpu-device", default);
        try
        {
            var before = await SessionSnapshotAsync(session.Id, "cpu-before", flip: true);
            var render = await fixture.Sessions.RenderFinalAsync(
                session.Id, null, "cycles_cpu", 32, 1, null, "wait", 0, default);
            var after = await SessionSnapshotAsync(session.Id, "cpu-after", flip: false);

            Assert.True(new FileInfo(render.Path).Length > 0);
            Assert.Equal("cpu", render.Device?.Backend);
            Assert.Null(render.Device?.Name);
            Assert.Null(render.Device?.Fallback);
            var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(render.SidecarPath))!;
            Assert.Equal("""{"backend":"cpu","name":null}""", sidecar["device"]!.ToJsonString());
            Assert.Equal("cycles_cpu", sidecar["engine"]!.GetValue<string>());
            // AC-06: the CPU path selects no GPU, and nothing restores preferences for it, so
            // any write there would persist for the worker's life.
            AssertPreferencesUnchanged(before, after);
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    private async Task<JsonObject> SnapshotAsync(
        Abl.Core.Workers.WorkerHandle worker, string script, string phase, bool flip, CancellationToken ct)
    {
        var output = Path.Combine(fixture.Root, $"prefs-{phase}-{Guid.NewGuid():N}.json");
        await worker.RequestAsync("exec", new JsonObject
        {
            ["script"] = script,
            ["params"] = new JsonObject { ["out"] = output, ["flip"] = flip },
        }, OpTimeout, ct);
        return JsonNode.Parse(await File.ReadAllTextAsync(output, ct))!.AsObject();
    }

    // The same independent script on a session's own worker, which render_final uses when no
    // desktop is configured. The session worker is shut down on close, so a flip never leaks.
    private async Task<JsonObject> SessionSnapshotAsync(string sessionId, string phase, bool flip)
    {
        var output = Path.Combine(fixture.Root, $"prefs-{phase}-{Guid.NewGuid():N}.json");
        await fixture.Sessions.ExecAsync(
            sessionId, PreferenceSnapshot, new JsonObject { ["out"] = output, ["flip"] = flip }, default);
        return JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject();
    }

    // The type stays and every entry that existed before keeps its (flipped) value.
    // Enumeration may append entries. The OPTIX guard stops an empty snapshot passing vacuously.
    private static void AssertPreferencesUnchanged(JsonObject before, JsonObject after)
    {
        Assert.Contains(before["entries"]!.AsObject(), entry => entry.Key.StartsWith("OPTIX#", StringComparison.Ordinal));
        Assert.Equal(before["computeDeviceType"]!.GetValue<string>(), after["computeDeviceType"]!.GetValue<string>());
        var afterEntries = after["entries"]!.AsObject();
        foreach (var (label, use) in before["entries"]!.AsObject())
        {
            Assert.True(afterEntries.ContainsKey(label), $"device entry {label} disappeared");
            Assert.Equal(use!.GetValue<bool>(), afterEntries[label]!.GetValue<bool>());
        }
    }
}
