using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Rendering;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class RenderStateIsolationTests(BlenderFixture fixture)
{
    // Also used by MetalQualificationTests (009 T030, the Metal half of AC-09).
    internal const string Configure = """
        import bpy
        s = bpy.context.scene
        s.render.engine = "BLENDER_WORKBENCH"
        s.render.resolution_x = 173
        s.render.resolution_y = 97
        s.render.resolution_percentage = 61
        s.render.filepath = params["authoredPath"]
        s.render.image_settings.file_format = "OPEN_EXR"
        s.render.image_settings.color_mode = "RGBA"
        s.render.image_settings.color_depth = "32"
        s.cycles.device = "CPU"
        s.cycles.samples = 7
        s.cycles.seed = 31
        s.cycles.use_denoising = False
        s.cycles.property_unset("denoiser")
        if hasattr(s.cycles, "denoising_use_gpu"):
            s.cycles.denoising_use_gpu = True  # not the default, so a reset is visible
        if hasattr(s, "eevee") and hasattr(s.eevee, "taa_render_samples"):
            s.eevee.taa_render_samples = 17
        s.view_settings.view_transform = "Standard"
        s.view_settings.exposure = 0.25
        s.view_settings.gamma = 1.1
        s.render.film_transparent = True
        s.camera.data.lens = 47
        if params.get("noCamera"):
            s.camera = None
        """;

    // Independent observable snapshot, intentionally broader than fingerprint.digest().
    // Also used by MetalQualificationTests.
    internal const string Capture = """
        import bpy, json
        s = bpy.context.scene
        state = {
            "engine": s.render.engine,
            "resolution": [s.render.resolution_x, s.render.resolution_y, s.render.resolution_percentage],
            "path": s.render.filepath,
            "image": [s.render.image_settings.file_format, s.render.image_settings.color_mode,
                      s.render.image_settings.color_depth, s.render.image_settings.compression,
                      s.render.image_settings.quality],
            "cycles": [s.cycles.device, s.cycles.samples, s.cycles.seed,
                       s.cycles.denoiser, s.cycles.is_property_set("denoiser"), s.cycles.use_denoising,
                       getattr(s.cycles, "denoising_use_gpu", None)],
            # Named copy for callers that assert this one field (MetalQualificationTests AC-09).
            "denoisingUseGpu": getattr(s.cycles, "denoising_use_gpu", None),
            "eeveeSamples": getattr(getattr(s, "eevee", None), "taa_render_samples", None),
            "color": [s.view_settings.view_transform, s.view_settings.look, s.view_settings.exposure,
                      s.view_settings.gamma, s.display_settings.display_device, s.render.film_transparent],
            "camera": s.camera.name if s.camera else None,
            "cameras": {o.name: {"matrix": [list(row) for row in o.matrix_world],
                                  "lens": o.data.lens, "type": o.data.type,
                                  "shift": [o.data.shift_x, o.data.shift_y], "ortho": o.data.ortho_scale}
                        for o in bpy.data.objects if o.type == "CAMERA"},
            "edit": s.get("isolation_edit", 0),
        }
        with open(params["capture"], "w", encoding="utf-8") as output:
            json.dump(state, output, sort_keys=True)
        """;

    // The cases that need a GPU backend carry OptixQualification: the hosted lane has no GPU
    // and excludes them; a host without a backend refuses cycles_gpu before any worker change.
    [Fact(Timeout = 180_000)]
    [Trait("Category", "OptixQualification")]
    public Task GpuRenderThenEdit_PreservesSavedSettingsLikeEditOnly() =>
        RenderThenEdit_PreservesSavedSettingsLikeEditOnly("gpu-final");

    [Theory(Timeout = 180_000)]
    [InlineData("preview")]
    [InlineData("final")]
    [InlineData("eevee-final")]
    [InlineData("workbench-final")]
    [InlineData("failed-final")]
    public async Task RenderThenEdit_PreservesSavedSettingsLikeEditOnly(string operation)
    {
        Assert.True(fixture.BlenderAvailable, "This qualification requires ABL_BLENDER pointing to native Blender");
        var session = await fixture.Sessions.OpenAsync("render-isolation", default);
        var initial = await fixture.Sessions.ExecAsync(session.Id, Configure, new JsonObject
        {
            ["authoredPath"] = Path.Combine(fixture.Root, "authored.exr"),
            ["noCamera"] = operation == "failed-final",
        }, default);
        var control = await fixture.Sessions.BranchAsync(initial.Checkpoint, "edit-only-control", default);
        try
        {
            var expected = await fixture.Sessions.ExecAsync(control.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            if (operation == "preview")
            {
                var preview = await fixture.Sessions.PreviewAsync(session.Id, 32, "eevee", default);
                Assert.True(new FileInfo(preview.Path).Length > 0);
            }
            else if (operation != "failed-final")
            {
                var engine = operation == "eevee-final" ? "eevee"
                    : operation == "workbench-final" ? "workbench"
                    : operation == "gpu-final" ? "cycles_gpu" : "cycles_cpu";
                var render = await fixture.Sessions.RenderFinalAsync(session.Id, null, engine, 32, 1,
                    null, "wait", 123, default);
                Assert.True(new FileInfo(render.Path).Length > 0);
                Assert.Equal("local", render.Tier);
            }
            else
            {
                var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.RenderFinalAsync(
                    session.Id, null, "cycles_cpu", 32, 1, null, "wait", 123, default));
                Assert.Equal("exec_failed", error.Kind);
                Assert.Equal(initial.Checkpoint, error.LastCheckpoint);
                Assert.Equal("ready", Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).State);
            }
            var actual = await fixture.Sessions.ExecAsync(session.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            var expectedState = await CaptureCheckpointAsync(expected.Checkpoint);
            var actualState = await CaptureCheckpointAsync(actual.Checkpoint);
            Assert.True(JsonNode.DeepEquals(expectedState, actualState),
                $"Saved settings changed: expected {expectedState}; actual {actualState}");
        }
        finally
        {
            await fixture.Sessions.CloseAsync(control.Id);
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRender_DiscardsSessionAndPreservesCheckpoint(bool final)
    {
        Assert.True(fixture.BlenderAvailable, "This qualification requires ABL_BLENDER");
        var session = await fixture.Sessions.OpenAsync("render-cancel", default);
        var entered = Path.Combine(fixture.Root, $"entered-{Guid.NewGuid():N}");
        var initial = await fixture.Sessions.ExecAsync(session.Id, Configure + "\n" + """
            import time
            marker = params["entered"]
            def hold_render(scene):
                with open(marker, "w") as output:
                    output.write("render entered after overrides")
                time.sleep(30)
            bpy.app.handlers.render_pre.append(hold_render)
            """, new JsonObject
        {
            ["authoredPath"] = Path.Combine(fixture.Root, "authored-cancel.exr"),
            ["entered"] = entered,
        }, default);
        try
        {
            using var cancel = new CancellationTokenSource();
            Task render = final
                ? fixture.Sessions.RenderFinalAsync(session.Id, null, "cycles_cpu", 32, 1, null, "wait", 123, cancel.Token)
                : fixture.Sessions.PreviewAsync(session.Id, 32, "eevee", cancel.Token);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(entered) && !render.IsCompleted)
                    await Task.Delay(20, deadline.Token);
                Assert.True(File.Exists(entered), "Render did not reach the injected hold");
            }
            finally
            {
                await cancel.CancelAsync();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await render);
            var dead = Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id);
            Assert.Equal("dead", dead.State);
            Assert.Equal(initial.Checkpoint, dead.Head);
            var state = await CaptureCheckpointAsync(initial.Checkpoint);
            Assert.Equal("BLENDER_WORKBENCH", state["engine"]!.GetValue<string>());
            Assert.Equal(173, state["resolution"]![0]!.GetValue<int>());
            Assert.Equal("OPEN_EXR", state["image"]![0]!.GetValue<string>());
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", "OptixQualification")]
    public async Task FailedGpuSetup_RestoresWorkerDevicePreferences()
    {
        Assert.True(fixture.BlenderAvailable, "This qualification requires ABL_BLENDER");
        var session = await fixture.Sessions.OpenAsync("gpu-setup-isolation", default);
        var initial = await fixture.Sessions.ExecAsync(session.Id, """
            import bpy, __main__
            prefs = bpy.context.preferences.addons["cycles"].preferences
            __main__.before_gpu = (prefs.compute_device_type,
                {(d.id, d.type): d.use for d in prefs.devices})
            __main__.before_denoise_gpu = bpy.context.scene.cycles.denoising_use_gpu
            # Hello assigns no compute_device_type (009 plan D1), so the hook must write a
            # value that differs from the captured one or the restore check is vacuous. The
            # enum depends on the build, so take the first other value this build accepts.
            __main__.type_changed = False
            def fail_gpu_setup(scene):
                # 009 FR-004: the Metal selection writes denoising_use_gpu; flip it too.
                scene.cycles.denoising_use_gpu = not __main__.before_denoise_gpu
                for kind in ("OPTIX", "CUDA", "HIP", "METAL", "ONEAPI", "NONE"):
                    if kind == __main__.before_gpu[0]:
                        continue
                    try:
                        prefs.compute_device_type = kind
                    except TypeError:
                        continue
                    __main__.type_changed = True
                    break
                for device in prefs.devices:
                    device.use = not device.use
                discovered = prefs.devices.add()
                discovered.id = "abl-injected-device"
                discovered.type = "OPTIX"
                discovered.use = True
                raise RuntimeError("injected GPU setup failure after preference changes")
            __main__._enable_gpu = fail_gpu_setup
            """, null, default);
        try
        {
            var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.RenderFinalAsync(
                session.Id, null, "cycles_gpu", 32, 1, null, "wait", 123, default));
            Assert.Equal("exec_failed", error.Kind);
            Assert.Equal(initial.Checkpoint, error.LastCheckpoint);
            await fixture.Sessions.ExecAsync(session.Id, """
                import bpy, __main__
                prefs = bpy.context.preferences.addons["cycles"].preferences
                known = {(d.id, d.type): d.use for d in prefs.devices if d.id != "abl-injected-device"}
                assert __main__.type_changed
                assert (prefs.compute_device_type, known) == __main__.before_gpu
                discovered = [d for d in prefs.devices if d.id == "abl-injected-device"]
                assert len(discovered) == 1 and not discovered[0].use
                assert bpy.context.scene.cycles.denoising_use_gpu == __main__.before_denoise_gpu
                """, null, default);
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    /// <summary>
    /// 009 T009, AC-05 (worker half, real Blender): the worker's pre-render device check
    /// fails although the hello reported a backend. The worker reports the trusted kind
    /// <c>gpu_unavailable</c> before any render (no output file), the 006 settings snapshot
    /// is unchanged, and the session keeps working. Kills the mutants that drop the
    /// <c>GpuUnavailable</c> translation in <c>_enable_gpu</c> or move <c>_enable_gpu</c>
    /// after <c>bpy.ops.render.render</c>.
    /// </summary>
    [Fact(Timeout = 180_000)]
    [Trait("Category", "OptixQualification")]
    public async Task WorkerRaisedGpuUnavailable_RendersNothingAndPreservesSavedSettings()
    {
        Assert.True(fixture.BlenderAvailable, "This qualification requires ABL_BLENDER");
        var session = await fixture.Sessions.OpenAsync("gpu-unavailable-isolation", default);
        var initial = await fixture.Sessions.ExecAsync(session.Id, Configure + "\n" + """
            import sys
            devices = sys.modules["cycles_devices"]
            def unavailable(prefs, backend):
                raise devices.GpuUnavailable("cycles_gpu: injected device check failure")
            devices.check = unavailable
            """, new JsonObject
        {
            ["authoredPath"] = Path.Combine(fixture.Root, "authored-gpu.exr"),
        }, default);
        var control = await fixture.Sessions.BranchAsync(initial.Checkpoint, "edit-only-gpu-control", default);
        try
        {
            var expected = await fixture.Sessions.ExecAsync(control.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            var outPath = Path.Combine(fixture.Root, $"gpu-unavailable-{Guid.NewGuid():N}.png");

            var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.RenderFinalAsync(
                session.Id, null, "cycles_gpu", 32, 1, outPath, "wait", 123, default));

            Assert.Equal("gpu_unavailable", error.Kind);
            Assert.Contains("injected device check failure", error.Message);
            Assert.Equal(GpuGuard.HintFor(GpuPath.RenderFinal, fixture.Options.Platform), error.Hint);
            Assert.Equal(initial.Checkpoint, error.LastCheckpoint);
            Assert.False(File.Exists(outPath), "the worker rendered before its device check");
            Assert.Equal("ready", Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).State);

            var actual = await fixture.Sessions.ExecAsync(session.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            var expectedState = await CaptureCheckpointAsync(expected.Checkpoint);
            var actualState = await CaptureCheckpointAsync(actual.Checkpoint);
            Assert.True(JsonNode.DeepEquals(expectedState, actualState),
                $"Saved settings changed: expected {expectedState}; actual {actualState}");
        }
        finally
        {
            await fixture.Sessions.CloseAsync(control.Id);
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRestoration_KillsWorkerRatherThanReportingAReadySession(bool closedStderr)
    {
        Assert.True(fixture.BlenderAvailable, "This qualification requires ABL_BLENDER");
        var session = await fixture.Sessions.OpenAsync("restore-fault", default);
        var initial = await fixture.Sessions.ExecAsync(session.Id, Configure + "\n" + """
            import __main__
            def fail_restore(*args):
                raise RuntimeError("injected restoration failure")
            __main__._restore_render_settings = fail_restore
            if params["closedStderr"]:
                import io, sys
                sys.stderr = io.StringIO()
                sys.stderr.close()
            """, new JsonObject
        {
            ["authoredPath"] = Path.Combine(fixture.Root, "authored-fault.exr"),
            ["closedStderr"] = closedStderr,
        }, default);
        try
        {
            var error = await Assert.ThrowsAsync<AblException>(() =>
                fixture.Sessions.PreviewAsync(session.Id, 32, "eevee", default));
            Assert.Equal("worker_died", error.Kind);
            Assert.Equal(initial.Checkpoint, error.LastCheckpoint);
            Assert.Equal("dead", Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).State);
            var state = await CaptureCheckpointAsync(initial.Checkpoint);
            Assert.Equal("BLENDER_WORKBENCH", state["engine"]!.GetValue<string>());
            Assert.Equal("OPEN_EXR", state["image"]![0]!.GetValue<string>());
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    private async Task<JsonNode> CaptureCheckpointAsync(string checkpoint)
    {
        var branch = await fixture.Sessions.BranchAsync(checkpoint, "inspect-saved-render-state", default);
        try
        {
            var output = Path.Combine(fixture.Root, $"state-{Guid.NewGuid():N}.json");
            await fixture.Sessions.ExecAsync(branch.Id, Capture, new JsonObject { ["capture"] = output }, default);
            return JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        }
        finally
        {
            await fixture.Sessions.CloseAsync(branch.Id);
        }
    }
}
