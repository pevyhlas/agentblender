using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using Abl.Integration.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Abl.Integration.Tests;

/// <summary>
/// Feature 009 T030 (FR-002, FR-004, FR-008, FR-009): the Metal qualification harness, run
/// by hand on an Apple silicon Mac with Blender 5.2.x (<c>--filter
/// Category=MetalQualification</c>; T033 records the run). Both CI lanes exclude the
/// category. A host without a Metal backend fails every case; none returns early. Each case
/// prints its evidence and, when <c>ABL_EVIDENCE_DIR</c> is set, writes it there as
/// <c>metal-&lt;case&gt;.json</c>; evidence holds variable names only, never values.
/// Devices in preference snapshots are labelled by type and position only. The hello,
/// denoiser, AC-09 and Cycles-log records do carry the raw GPU device name (the chip model
/// and core count FR-009 asks for): T033 shows it in the redacted <c>Apple M&lt;n&gt;</c> form
/// in any committed record, under the public-flip scrub.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[Collection("blender")]
[Trait("Category", "MetalQualification")]
public class MetalQualificationTests(BlenderFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(60);

    private const string Canary = "ABL_009_METAL_CANARY";

    /// <summary>The doctor's rule (<c>firstCompileSuspected</c>): a first render this slow most likely compiled kernels.</summary>
    private const double CompileSuspectedMs = 30_000;

    // AC-01 oracle: a separate Blender process lists the Metal devices itself.
    private const string RawDeviceProbe = """
        import bpy, json
        prefs = bpy.context.preferences.addons["cycles"].preferences
        metal = [d.name for d in prefs.get_devices_for_type("METAL") if d.type == "METAL"]
        print("ABL_PROBE " + json.dumps({"metal": metal, "metalrt": getattr(prefs, "metalrt", None)}))
        """;

    private const string EnvironmentNames = """
        import json, os
        with open(params["out"], "w", encoding="utf-8") as out:
            json.dump(sorted(os.environ.keys()), out)
        """;

    // Q3 (plan "Determinism and tolerance"): the sequence of the one observed silent fallback.
    // read_factory_settings(use_empty=True) leaves no camera, so the script adds one, a light
    // and a cube, then the test renders cycles_cpu before cycles_gpu.
    private const string FactoryReset = """
        import bpy, bmesh
        bpy.ops.wm.read_factory_settings(use_empty=True)
        s = bpy.context.scene
        camera = bpy.data.objects.new("Q3Camera", bpy.data.cameras.new("Q3Camera"))
        camera.location = (0.0, -6.0, 2.0)
        camera.rotation_euler = (1.3, 0.0, 0.0)
        s.collection.objects.link(camera)
        s.camera = camera
        light = bpy.data.objects.new("Q3Light", bpy.data.lights.new("Q3Light", "SUN"))
        light.rotation_euler = (0.6, 0.2, 0.0)
        s.collection.objects.link(light)
        mesh = bpy.data.meshes.new("Q3Cube")
        bm = bmesh.new(); bmesh.ops.create_cube(bm, size=2.0); bm.to_mesh(mesh); bm.free()
        s.collection.objects.link(bpy.data.objects.new("Q3Cube", mesh))
        s.cycles.use_denoising = False
        """;

    // Shared by Q1 and Q2: the parity render settings (denoise off, opaque 8-bit RGBA) and a
    // ground plane under the default camera and light; mesh objects of the startup scene go.
    private const string ParityBase = """
        import bpy, bmesh
        s = bpy.context.scene
        s.cycles.use_denoising = False
        s.cycles.use_adaptive_sampling = False  # exactly the requested samples on both devices
        s.render.film_transparent = False
        s.render.image_settings.color_mode = "RGBA"
        s.render.image_settings.color_depth = "8"
        for o in [o for o in bpy.data.objects if o.type == "MESH"]:
            bpy.data.objects.remove(o, do_unlink=True)

        def mesh_object(name, build, location=(0.0, 0.0, 0.0)):
            mesh = bpy.data.meshes.new(name)
            bm = bmesh.new(); build(bm); bm.to_mesh(mesh); bm.free()
            obj = bpy.data.objects.new(name, mesh)
            obj.location = location
            s.collection.objects.link(obj)
            return obj

        def material(name):
            mat = bpy.data.materials.new(name)
            if getattr(mat, "node_tree", None) is None:
                mat.use_nodes = True
            tree = mat.node_tree
            for node in list(tree.nodes):
                tree.nodes.remove(node)
            return mat, tree, tree.nodes.new("ShaderNodeOutputMaterial")

        mesh_object("Ground", lambda bm: bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=6.0))
        sphere = mesh_object("Sphere", lambda bm: bmesh.ops.create_uvsphere(bm, u_segments=48, v_segments=24, radius=1.0), (0.0, 0.0, 1.0))
        for polygon in sphere.data.polygons:
            polygon.use_smooth = True
        """;

    private const string SceneQ1 = ParityBase + """

        mat, tree, out = material("Q1Principled")
        bsdf = tree.nodes.new("ShaderNodeBsdfPrincipled")
        bsdf.inputs["Base Color"].default_value = (0.8, 0.3, 0.2, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.3
        tree.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
        sphere.data.materials.append(mat)
        """;

    private const string SceneQ2 = ParityBase + """

        mat, tree, out = material("Q2Glass")
        glass = tree.nodes.new("ShaderNodeBsdfGlass")
        glass.inputs["Roughness"].default_value = 0.05
        tree.links.new(glass.outputs["BSDF"], out.inputs["Surface"])
        sphere.data.materials.append(mat)
        slab = mesh_object("VolumeSlab", lambda bm: bmesh.ops.create_cube(bm, size=1.0), (0.0, 0.0, 0.6))
        slab.scale = (4.0, 4.0, 0.5)
        fog, tree, out = material("Q2Volume")
        volume = tree.nodes.new("ShaderNodeVolumePrincipled")
        volume.inputs["Density"].default_value = 0.3
        tree.links.new(volume.outputs["Volume"], out.inputs["Volume"])
        slab.data.materials.append(fog)
        """;

    /// <summary>AC-01: the hello reports Metal with the devices a raw probe process lists.</summary>
    [Fact(Timeout = 300_000)]
    public async Task Hello_ReportsTheMetalDevicesARawProbeLists()
    {
        RequireMetalHost();
        var ct = CancellationToken.None;
        var worker = await fixture.Pool.TakeAsync(ct);
        GpuCapability gpu;
        try
        {
            gpu = worker.Gpu;
        }
        finally
        {
            await worker.ShutdownAsync();
            await worker.DisposeAsync();
        }

        var probe = await RawProbeAsync(ct);
        Evidence("hello", new JsonObject
        {
            ["hello"] = JsonSerializer.SerializeToNode(gpu),
            ["probe"] = probe.DeepClone(),
        });

        Assert.Equal("metal", gpu.Backend);
        Assert.NotEmpty(gpu.Devices);
        Assert.All(gpu.Devices, device => Assert.Equal("METAL", device.Type));
        var listed = probe["metal"]!.AsArray().Select(name => name!.GetValue<string>()).ToList();
        Assert.NotEmpty(listed);
        Assert.Equal(listed.Take(8).ToList(), gpu.Devices.Select(device => device.Name).ToList());
        Assert.Equal(probe["metalrt"]?.GetValue<string>(), gpu.MetalRt);
    }

    /// <summary>
    /// AC-04: a self-spawned <c>--debug-cycles</c> worker under the product environment
    /// replies <c>device.backend: metal</c>, and Cycles' log for that render says Metal.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task DebugCyclesWorker_TracesTheCyclesGpuRenderOnMetal()
    {
        RequireMetalHost();
        var ct = CancellationToken.None;
        // As the 008 AC-14 canary: both reach this process, neither may reach the worker.
        Environment.SetEnvironmentVariable(Canary, "must-not-leak");
        Environment.SetEnvironmentVariable("PYTHONPATH", Path.Combine(fixture.Root, "no-such-dir"));
        DebugCyclesWorker spawned;
        try
        {
            spawned = await DebugCyclesWorker.StartAsync(fixture.Options, ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Canary, null);
            Environment.SetEnvironmentVariable("PYTHONPATH", null);
        }

        await using var worker = spawned;
        AssertProductSpawn(worker);
        var hello = await StartAsync(worker, ct);
        Assert.Equal("metal", hello["gpu"]!["backend"]!.GetValue<string>());
        var names = await WorkerEnvironmentNamesAsync(worker, ct);
        Assert.DoesNotContain(Canary, names);

        var (render, lines) = await RenderAsync(worker, "cycles_gpu", 64, 16, ct);
        Evidence("cycles-log", new JsonObject
        {
            ["device"] = render["device"]?.DeepClone(),
            ["pathTracing"] = new JsonArray([.. lines.Select(line => JsonValue.Create(line))]),
            ["workerEnvironmentNames"] = new JsonArray([.. names.Select(name => JsonValue.Create(name))]),
        });

        Assert.True(render["ok"]!.GetValue<bool>(), $"render failed: {render["error"]}\n{worker.Tail}");
        Assert.Equal("metal", render["device"]!["backend"]!.GetValue<string>());
        AssertTracedOnMetal(lines, worker);
    }

    /// <summary>A <c>cycles_gpu</c> render on 5.2.x assigns the Metal denoiser without the <c>--background</c> gotcha.</summary>
    [Fact(Timeout = 900_000)]
    public async Task CyclesGpuRender_On52_AssignsTheMetalDenoiserWithoutTheBackgroundGotcha()
    {
        RequireMetalHost();
        var record = Path.Combine(fixture.Root, $"metal-denoiser-{Guid.NewGuid():N}.json");
        var session = await fixture.Sessions.OpenAsync("metal-denoiser", default);
        try
        {
            await fixture.Sessions.ExecAsync(session.Id, """
                import bpy, json, __main__
                bpy.context.scene.cycles.use_denoising = True
                original = __main__._enable_gpu
                def recording(scene):
                    device = original(scene)
                    with open(params["record"], "w", encoding="utf-8") as out:
                        json.dump({"denoiser": scene.cycles.denoiser, "device": scene.cycles.device,
                                   "denoisingUseGpu": scene.cycles.denoising_use_gpu}, out)
                    return device
                __main__._enable_gpu = recording
                """, new JsonObject { ["record"] = record }, default);
            var render = await fixture.Sessions.RenderFinalAsync(
                session.Id, null, "cycles_gpu", 32, 4, null, "wait", 0, default);

            var selected = JsonNode.Parse(await File.ReadAllTextAsync(record))!;
            var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(render.SidecarPath))!;
            Evidence("denoiser", new JsonObject
            {
                ["selected"] = selected.DeepClone(),
                ["blenderVersion"] = sidecar["blenderVersion"]?.DeepClone(),
                ["device"] = sidecar["device"]?.DeepClone(),
            });

            Assert.True(new FileInfo(render.Path).Length > 0);
            Assert.Equal("metal", render.Device?.Backend);
            Assert.NotNull(render.Device?.Name);
            Assert.Null(render.Device?.Fallback);
            Assert.StartsWith("5.2.", sidecar["blenderVersion"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("OPENIMAGEDENOISE", selected["denoiser"]!.GetValue<string>());
            Assert.True(selected["denoisingUseGpu"]!.GetValue<bool>());
            Assert.Equal("GPU", selected["device"]!.GetValue<string>());
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    /// <summary>
    /// Q3: <c>read_factory_settings</c>, a <c>cycles_cpu</c> render, then <c>cycles_gpu</c>.
    /// It passes when Cycles traces the GPU render on Metal, or when the request fails with
    /// <c>gpu_unavailable</c>; a reply of <c>metal</c> while Cycles traces on the CPU fails it.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Q3_FactoryResetThenCpuThenGpu_NeverReportsMetalWhileCyclesTracesElsewhere()
    {
        RequireMetalHost();
        var ct = CancellationToken.None;
        await using var worker = await DebugCyclesWorker.StartAsync(fixture.Options, ct);
        AssertProductSpawn(worker);
        var hello = await StartAsync(worker, ct);
        Assert.Equal("metal", hello["gpu"]!["backend"]!.GetValue<string>());
        var reset = await worker.RequestAsync("exec", new JsonObject { ["source"] = FactoryReset }, OpTimeout, ct);
        Assert.True(reset["ok"]!.GetValue<bool>(), $"factory reset failed: {reset["error"]}\n{worker.Tail}");

        // Each window closes only once its closing sentinel is read on both streams, so no
        // late CPU line can land in the GPU window.
        var (cpu, cpuLines) = await RenderAsync(worker, "cycles_cpu", 64, 16, ct);
        var (gpu, gpuLines) = await RenderAsync(worker, "cycles_gpu", 64, 16, ct);
        var gpuOk = gpu["ok"]!.GetValue<bool>();
        Evidence("q3", new JsonObject
        {
            ["cpuDevice"] = cpu["device"]?.DeepClone(),
            ["cpuPathTracing"] = new JsonArray([.. cpuLines.Select(line => JsonValue.Create(line))]),
            ["gpuOk"] = gpuOk,
            ["gpuDevice"] = gpu["device"]?.DeepClone(),
            ["gpuErrorKind"] = gpu["error"]?["kind"]?.DeepClone(),
            ["gpuPathTracing"] = new JsonArray([.. gpuLines.Select(line => JsonValue.Create(line))]),
        });

        Assert.True(cpu["ok"]!.GetValue<bool>(), $"cycles_cpu failed: {cpu["error"]}\n{worker.Tail}");
        Assert.Equal("cpu", cpu["device"]!["backend"]!.GetValue<string>());
        // Cycles logs the line for a CPU render too; none here means the harness lost it.
        Assert.True(cpuLines.Count > 0, $"Cycles logged no 'Path tracing on:' line for the cycles_cpu render\n{worker.Tail}");
        Assert.DoesNotContain(cpuLines, line => line.Contains("(Metal)", StringComparison.Ordinal));
        if (gpuOk)
        {
            Assert.Equal("metal", gpu["device"]!["backend"]!.GetValue<string>());
            AssertTracedOnMetal(gpuLines, worker);
        }
        else
        {
            // An honest refusal passes Q3: nothing claimed Metal for a CPU-traced render.
            Assert.Equal("gpu_unavailable", gpu["error"]!["kind"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// AC-09, Metal half: <c>denoising_use_gpu = False</c> before the render, so the Metal
    /// selection's write of <c>True</c> shows if the restore misses it. The saved settings
    /// after render-then-edit equal edit-only, and the device preferences are unchanged.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Ac09_MetalRenderRestoresSavedSettingsAndDevicePreferences()
    {
        RequireMetalHost();
        var session = await fixture.Sessions.OpenAsync("metal-isolation", default);
        var initial = await fixture.Sessions.ExecAsync(session.Id,
            RenderStateIsolationTests.Configure + "\ns.cycles.denoising_use_gpu = False\n", new JsonObject
            {
                ["authoredPath"] = Path.Combine(fixture.Root, "authored-metal.exr"),
            }, default);
        var control = await fixture.Sessions.BranchAsync(initial.Checkpoint, "metal-edit-only-control", default);
        try
        {
            var expected = await fixture.Sessions.ExecAsync(control.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            var before = await PreferenceSnapshotAsync(session.Id, "before", flip: true);
            var render = await fixture.Sessions.RenderFinalAsync(session.Id, null, "cycles_gpu", 32, 1,
                null, "wait", 123, default);
            var after = await PreferenceSnapshotAsync(session.Id, "after", flip: false);
            var actual = await fixture.Sessions.ExecAsync(session.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            var expectedState = await CaptureCheckpointAsync(expected.Checkpoint);
            var actualState = await CaptureCheckpointAsync(actual.Checkpoint);
            Evidence("ac09", new JsonObject
            {
                ["device"] = JsonSerializer.SerializeToNode(render.Device),
                ["preferencesBefore"] = before.DeepClone(),
                ["preferencesAfter"] = after.DeepClone(),
                ["savedCycles"] = actualState["cycles"]?.DeepClone(),
            });

            Assert.Equal("metal", render.Device?.Backend);
            AssertPreferencesUnchanged(before, after);
            Assert.False(actualState["denoisingUseGpu"]!.GetValue<bool>(), "denoising_use_gpu was left True");
            Assert.True(JsonNode.DeepEquals(expectedState, actualState),
                $"Saved settings changed: expected {expectedState}; actual {actualState}");
        }
        finally
        {
            await fixture.Sessions.CloseAsync(control.Id);
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    // AC-09 failure half: the real selection runs, its writes are recorded, then it raises.
    private const string FailAfterMetalSelection = """
        import bpy, json, __main__
        original = __main__._enable_gpu
        def select_then_fail(scene):
            original(scene)
            prefs = bpy.context.preferences.addons["cycles"].preferences
            counts = {}
            entries = {}
            for d in prefs.devices:
                counts[d.type] = counts.get(d.type, 0) + 1
                entries[f"{d.type}#{counts[d.type] - 1}"] = d.use
            with open(params["selected"], "w", encoding="utf-8") as out:
                json.dump({"computeDeviceType": prefs.compute_device_type, "entries": entries,
                           "denoisingUseGpu": scene.cycles.denoising_use_gpu,
                           "device": scene.cycles.device}, out)
            raise RuntimeError("injected failure after the real Metal selection")
        __main__._enable_gpu = select_then_fail
        """;

    /// <summary>
    /// AC-09, Metal failure half: a wrapper calls the real <c>__main__._enable_gpu</c>, so
    /// the Metal selection makes its writes (the CPU entry off, <c>METAL</c> entries on,
    /// <c>compute_device_type = METAL</c>, <c>denoising_use_gpu = True</c> over a configured
    /// <c>False</c>), and then raises. The render fails with <c>exec_failed</c> at the
    /// unchanged checkpoint, the flipped preference snapshot is unchanged, and the saved
    /// settings after render-then-edit equal edit-only.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Ac09_MetalSelectionThenFailureRestoresSavedSettingsAndDevicePreferences()
    {
        RequireMetalHost();
        var selectedPath = Path.Combine(fixture.Root, $"metal-failed-selection-{Guid.NewGuid():N}.json");
        var session = await fixture.Sessions.OpenAsync("metal-failed-isolation", default);
        var initial = await fixture.Sessions.ExecAsync(session.Id,
            RenderStateIsolationTests.Configure + "\ns.cycles.denoising_use_gpu = False\n", new JsonObject
            {
                ["authoredPath"] = Path.Combine(fixture.Root, "authored-metal-failed.exr"),
            }, default);
        var control = await fixture.Sessions.BranchAsync(initial.Checkpoint, "metal-failed-edit-only-control", default);
        try
        {
            var expected = await fixture.Sessions.ExecAsync(control.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            var before = await PreferenceSnapshotAsync(session.Id, "failed-before", flip: true);
            var armed = await fixture.Sessions.ExecAsync(session.Id, FailAfterMetalSelection,
                new JsonObject { ["selected"] = selectedPath }, default);
            AblException? error = null;
            try
            {
                await fixture.Sessions.RenderFinalAsync(session.Id, null, "cycles_gpu", 32, 1, null, "wait", 123, default);
            }
            catch (AblException failure)
            {
                error = failure;
            }

            var after = await PreferenceSnapshotAsync(session.Id, "failed-after", flip: false);
            var actual = await fixture.Sessions.ExecAsync(session.Id,
                "import bpy\nbpy.context.scene['isolation_edit'] = 1", null, default);
            var expectedState = await CaptureCheckpointAsync(expected.Checkpoint);
            var actualState = await CaptureCheckpointAsync(actual.Checkpoint);
            var selection = File.Exists(selectedPath) ? JsonNode.Parse(await File.ReadAllTextAsync(selectedPath)) : null;
            Evidence("ac09-failed", new JsonObject
            {
                ["errorKind"] = error?.Kind,
                ["checkpointUnchanged"] = error is not null && error.LastCheckpoint == armed.Checkpoint,
                ["preferencesBefore"] = before.DeepClone(),
                ["preferencesAtFailure"] = selection?.DeepClone(),
                ["preferencesAfter"] = after.DeepClone(),
                ["savedCycles"] = actualState["cycles"]?.DeepClone(),
            });

            Assert.NotNull(error);
            Assert.Equal("exec_failed", error.Kind);
            Assert.Equal(armed.Checkpoint, error.LastCheckpoint);
            // The real selection ran and wrote over the configured state, so the restore had work to do.
            Assert.NotNull(selection);
            Assert.Equal("METAL", selection["computeDeviceType"]!.GetValue<string>());
            Assert.True(selection["denoisingUseGpu"]!.GetValue<bool>(), "the Metal selection did not write denoising_use_gpu");
            AssertPreferencesUnchanged(before, after);
            Assert.False(actualState["denoisingUseGpu"]!.GetValue<bool>(), "denoising_use_gpu was left True");
            Assert.True(JsonNode.DeepEquals(expectedState, actualState),
                $"Saved settings changed: expected {expectedState}; actual {actualState}");
        }
        finally
        {
            await fixture.Sessions.CloseAsync(control.Id);
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    /// <summary>
    /// AC-14: a 3-op trajectory with <c>cycles_gpu</c> renders between its ops, frozen and
    /// replayed in a fresh store, gives the digest recorded at freeze.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Ac14_TrajectoryWithMetalRendersBetweenOpsReplaysDigestEqualInAFreshStore()
    {
        RequireMetalHost();
        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("metal-replay", ct);
        string[] ops =
        [
            """
            import bpy, bmesh
            mesh = bpy.data.meshes.new("Base")
            bm = bmesh.new(); bmesh.ops.create_cube(bm, size=2); bm.to_mesh(mesh); bm.free()
            bpy.context.scene.collection.objects.link(bpy.data.objects.new("Base", mesh))
            """,
            """
            import bpy
            mat = bpy.data.materials.new("Steel")
            mat.diffuse_color = (0.7, 0.72, 0.75, 1.0)
            bpy.data.objects["Base"].data.materials.append(mat)
            """,
            """
            import bpy, bmesh
            mesh = bpy.data.meshes.new("Antenna")
            bm = bmesh.new(); bmesh.ops.create_cone(bm, cap_ends=True, radius1=0.1, radius2=0.02, depth=1.5, segments=12); bm.to_mesh(mesh); bm.free()
            obj = bpy.data.objects.new("Antenna", mesh)
            obj.location = (0, 0, 1.75)
            bpy.context.scene.collection.objects.link(obj)
            """,
        ];
        var devices = new List<string?>();
        try
        {
            for (var i = 0; i < ops.Length; i++)
            {
                await fixture.Sessions.ExecAsync(session.Id, ops[i], null, ct);
                if (i < ops.Length - 1)
                {
                    var render = await fixture.Sessions.RenderFinalAsync(
                        session.Id, null, "cycles_gpu", 32, 1, null, "wait", 0, ct);
                    devices.Add(render.Device?.Backend);
                }
            }

            fixture.Trajectories.Freeze(session.Id, "metal-replay-artifact", null, null);
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }

        var export = fixture.Trajectories.Export("metal-replay-artifact", Path.Combine(fixture.Root, "metal-repo"));
        var freshOptions = TestOptions.FromEnvironment() with
        {
            StoreDir = Path.Combine(fixture.Root, "metal-fresh-store"),
            RendersDir = fixture.Options.RendersDir,
            PoolFloor = 0,
        };
        var freshTrajectories = new TrajectoryService(
            freshOptions, fixture.Pool, new CheckpointStore(freshOptions.StoreDir), new GpuLease(),
            new DesktopTier(freshOptions, NullLogger<DesktopTier>.Instance),
            NullLogger<TrajectoryService>.Instance);
        var import = freshTrajectories.Import(export.Directory);
        var replay = await freshTrajectories.ReplayAsync("metal-replay-artifact", ct);
        Evidence("ac14", new JsonObject
        {
            ["renderDevices"] = new JsonArray([.. devices.Select(device => JsonValue.Create(device))]),
            ["ops"] = import.Ops,
            ["expectedDigest"] = replay.ExpectedDigest,
            ["finalDigest"] = replay.FinalDigest,
        });

        Assert.Equal(new string?[] { "metal", "metal" }, devices);
        Assert.Equal(3, import.Ops);
        Assert.True(replay.Pass, $"got {replay.FinalDigest[..12]} expected {replay.ExpectedDigest[..12]}");
    }

    /// <summary>
    /// AC-15: Q1 (lit sphere and plane, Principled roughness 0.3) and Q2 (glass with a
    /// volume slab) at 256 px, 64 spp (adaptive sampling off), denoise off, seed 0, on Metal
    /// and on the CPU in one worker, plus a Metal repeat. Gates, over R, G and B: Metal vs CPU
    /// PSNR ≥ 40 dB and ≥ 99.9% of samples within 4/255; Metal vs Metal PSNR ≥ 50 dB,
    /// recording whether the two are identical. The opaque film's alpha planes must match.
    /// </summary>
    [Theory(Timeout = 1_800_000)]
    [InlineData("q1")]
    [InlineData("q2")]
    public async Task Ac15_QualificationScenesMeetTheParityGates(string scene)
    {
        RequireMetalHost();
        var session = await fixture.Sessions.OpenAsync($"metal-parity-{scene}", default);
        try
        {
            await fixture.Sessions.ExecAsync(session.Id, scene == "q1" ? SceneQ1 : SceneQ2, null, default);
            async Task<(Rgba8Image Image, string? Backend)> Render(string engine, string name)
            {
                var path = Path.Combine(fixture.Root, $"parity-{scene}-{name}-{Guid.NewGuid():N}.png");
                var result = await fixture.Sessions.RenderFinalAsync(
                    session.Id, null, engine, 256, 64, path, "wait", 0, default);
                return (PngRgba8.Decode(result.Path), result.Device?.Backend);
            }

            var cpu = await Render("cycles_cpu", "cpu");
            var metal = await Render("cycles_gpu", "metal");
            var repeat = await Render("cycles_gpu", "metal-repeat");

            // The gates count R, G and B: the film is opaque, so alpha is 255 in every image
            // and would loosen both Metal-vs-CPU gates. Alpha is asserted identical instead.
            var metalVsCpu = ImageMetrics.Psnr(cpu.Image, metal.Image, SampleChannels.Rgb);
            var within = ImageMetrics.FractionWithin(cpu.Image, metal.Image, 4, SampleChannels.Rgb);
            var metalVsMetal = ImageMetrics.Psnr(metal.Image, repeat.Image, SampleChannels.Rgb);
            var identical = metal.Image.Pixels.AsSpan().SequenceEqual(repeat.Image.Pixels);
            var maxDifference = ImageMetrics.MaxDifference(cpu.Image, metal.Image);
            var repeatMaxDifference = ImageMetrics.MaxDifference(metal.Image, repeat.Image);
            Evidence($"ac15-{scene}", new JsonObject
            {
                ["devices"] = new JsonArray(cpu.Backend, metal.Backend, repeat.Backend),
                ["size"] = $"{cpu.Image.Width}x{cpu.Image.Height}",
                ["gateChannels"] = "rgb",
                ["metalVsCpuPsnrRgb"] = Finite(metalVsCpu),
                ["metalVsCpuWithin4Rgb"] = within,
                ["metalVsCpuPsnrRgbaInformational"] = Finite(ImageMetrics.Psnr(cpu.Image, metal.Image, SampleChannels.Rgba)),
                ["metalVsCpuWithin4RgbaInformational"] = ImageMetrics.FractionWithin(cpu.Image, metal.Image, 4, SampleChannels.Rgba),
                ["metalVsCpuMaxDifference"] = new JsonArray([.. maxDifference.Select(d => JsonValue.Create(d))]),
                ["metalVsCpuDifferenceHistogram"] = NonZeroBins(ImageMetrics.DifferenceHistogram(cpu.Image, metal.Image)),
                ["metalVsMetalPsnrRgb"] = Finite(metalVsMetal),
                ["metalVsMetalIdentical"] = identical,
            });

            Assert.Equal("cpu", cpu.Backend);
            Assert.Equal("metal", metal.Backend);
            Assert.Equal("metal", repeat.Backend);
            Assert.Equal((256, 256), (cpu.Image.Width, cpu.Image.Height));
            Assert.True(maxDifference[3] == 0, $"Metal vs CPU alpha planes differ (max {maxDifference[3]})");
            Assert.True(repeatMaxDifference[3] == 0, $"Metal vs Metal alpha planes differ (max {repeatMaxDifference[3]})");
            Assert.True(metalVsCpu >= 40, $"Metal vs CPU RGB PSNR {metalVsCpu:F2} dB < 40 dB");
            Assert.True(within >= 0.999, $"Metal vs CPU: {within:P3} of RGB samples within 4/255 (< 99.9%)");
            Assert.True(metalVsMetal >= 50, $"Metal vs Metal RGB PSNR {metalVsMetal:F2} dB < 50 dB");
        }
        finally
        {
            await fixture.Sessions.CloseAsync(session.Id);
        }
    }

    /// <summary>
    /// AC-16: two separately spawned workers each run their first <c>cycles_gpu</c> render
    /// at 64 px; wall-clock time around the request, independent of the worker's
    /// <c>duration_ms</c>. The second worker, served from the kernel cache under the env
    /// allowlist, takes under 10 s; that holds in any case order.
    /// <para>
    /// The kernel cache is per user and survives processes, and xUnit does not run cases in
    /// file order, so the first worker's render is a kernel compile only on a cold cache. The
    /// record therefore names the times by worker order and adds <c>firstCompileSuspected</c>
    /// (the doctor's rule: first render over 30 s); it never labels a warm render a compile
    /// time. T033 runs this case alone and first, on a cold 5.2.x kernel cache, and takes the
    /// first-compile figure from it only when <c>firstCompileSuspected</c> is true. The 600 s
    /// bound is the request's own <c>RenderTimeout</c> deadline: a slower compile fails the
    /// case by timeout, so no separate assertion restates it.
    /// </para>
    /// </summary>
    [Fact(Timeout = 1_500_000)]
    public async Task Ac16_SecondWorkersFirstMetalRenderUsesTheKernelCache()
    {
        RequireMetalHost();
        var first = await TimedFirstRenderAsync("first");
        var second = await TimedFirstRenderAsync("second");
        Evidence("ac16", new JsonObject
        {
            ["firstWorkerWallMs"] = first.WallMs,
            ["firstWorkerDurationMs"] = first.WorkerMs,
            ["firstCompileSuspected"] = first.WallMs > CompileSuspectedMs,
            ["secondWorkerWallMs"] = second.WallMs,
            ["secondWorkerDurationMs"] = second.WorkerMs,
            ["renderTimeoutMs"] = fixture.Options.RenderTimeout.TotalMilliseconds,
            ["devices"] = new JsonArray(first.Backend, second.Backend),
        });

        Assert.Equal("metal", first.Backend);
        Assert.Equal("metal", second.Backend);
        Assert.True(second.WallMs < 10_000, $"second worker's first render took {second.WallMs:F0} ms (no kernel cache?)");
    }

    private async Task<(double WallMs, double WorkerMs, string Backend)> TimedFirstRenderAsync(string name)
    {
        var ct = CancellationToken.None;
        var worker = await fixture.Pool.TakeAsync(ct); // pool floor 0: a new process each time
        try
        {
            var path = Path.Combine(fixture.Root, $"cache-{name}-{Guid.NewGuid():N}.png");
            var clock = Stopwatch.StartNew();
            var reply = await worker.RequestAsync("render", new JsonObject
            {
                ["engine"] = "cycles_gpu",
                ["res"] = 64,
                ["samples"] = 16,
                ["seed"] = 0,
                ["out"] = path,
            }, fixture.Options.RenderTimeout, ct);
            clock.Stop();
            return (clock.Elapsed.TotalMilliseconds, reply["duration_ms"]!.GetValue<double>(),
                reply["device"]!["backend"]!.GetValue<string>());
        }
        finally
        {
            await worker.ShutdownAsync();
            await worker.DisposeAsync();
        }
    }

    // The category is not CI's: a missing Blender or the wrong host fails, never skips.
    private void RequireMetalHost()
    {
        Assert.True(fixture.BlenderAvailable, "Metal qualification requires ABL_BLENDER (Blender 5.2.x)");
        Assert.True(OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64,
            "Metal qualification runs on an Apple silicon Mac");
    }

    // The dir mode comes from product code (RuntimePaths.Prepare). The socket mode and the
    // environment names are harness self-checks: the harness set them one step earlier.
    // The independent environment evidence is the child-side read (WorkerEnvironmentNamesAsync).
    private void AssertProductSpawn(DebugCyclesWorker worker)
    {
        Assert.Equal(RuntimePaths.DirMode, worker.RuntimeDirMode);
        Assert.Equal(RuntimePaths.SocketMode, worker.SocketMode);
        Assert.DoesNotContain(worker.EnvironmentNames, name => name.StartsWith("PYTHON", StringComparison.Ordinal));
        Assert.All(worker.EnvironmentNames, name => Assert.True(
            ChildEnvironment.AllowsForWorker(name, fixture.Options.WorkerEnvPassthrough), $"{name} is outside the 008 allowlist"));
    }

    // The flipped snapshot taken before the render, compared with the one after it.
    private static void AssertPreferencesUnchanged(JsonObject before, JsonObject after)
    {
        Assert.Contains(before["entries"]!.AsObject(), entry => entry.Key.StartsWith("METAL#", StringComparison.Ordinal));
        Assert.Equal(before["computeDeviceType"]!.GetValue<string>(), after["computeDeviceType"]!.GetValue<string>());
        var afterEntries = after["entries"]!.AsObject();
        foreach (var (label, use) in before["entries"]!.AsObject())
        {
            Assert.True(afterEntries.ContainsKey(label), $"device entry {label} disappeared");
            Assert.Equal(use!.GetValue<bool>(), afterEntries[label]!.GetValue<bool>());
        }
    }

    private static void AssertTracedOnMetal(IReadOnlyList<string> lines, DebugCyclesWorker worker)
    {
        Assert.True(lines.Count > 0, $"Cycles logged no 'Path tracing on:' line for the render\n{worker.Tail}");
        Assert.All(lines, line => Assert.Contains("(Metal)", line, StringComparison.Ordinal));
    }

    private static async Task<JsonObject> StartAsync(DebugCyclesWorker worker, CancellationToken ct)
    {
        var hello = await worker.RequestAsync("hello", null, OpTimeout, ct);
        Assert.True(hello["ok"]!.GetValue<bool>(), $"hello failed: {hello["error"]}\n{worker.Tail}");
        // The supervisor's version check does not run on this spawn, so pin 5.2.x here.
        Assert.StartsWith("5.2.", hello["blender_version"]!.GetValue<string>(), StringComparison.Ordinal);
        var warm = await worker.RequestAsync("warm", new JsonObject { ["prewarm_render"] = false }, OpTimeout, ct);
        Assert.True(warm["ok"]!.GetValue<bool>(), $"warm failed: {warm["error"]}");
        return hello;
    }

    private async Task<List<string>> WorkerEnvironmentNamesAsync(DebugCyclesWorker worker, CancellationToken ct)
    {
        var path = Path.Combine(fixture.Root, $"debug-env-{Guid.NewGuid():N}.json");
        var reply = await worker.RequestAsync("exec", new JsonObject
        {
            ["source"] = EnvironmentNames,
            ["params"] = new JsonObject { ["out"] = path },
        }, OpTimeout, ct);
        Assert.True(reply["ok"]!.GetValue<bool>(), $"exec failed: {reply["error"]}");
        var names = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsArray()
            .Select(name => name!.GetValue<string>()).ToList();
        Assert.DoesNotContain("PYTHONPATH", names);
        Assert.DoesNotContain(names, name => name.StartsWith("PYTHON", StringComparison.Ordinal));
        Assert.Contains("HOME", names);
        Assert.Contains("PATH", names);
        return names;
    }

    // One render inside a sentinel window: the reply and every Cycles line it produced.
    private async Task<(JsonObject Reply, IReadOnlyList<string> PathTracing)> RenderAsync(
        DebugCyclesWorker worker, string engine, int res, int samples, CancellationToken ct) =>
        await worker.TracedAsync("render", new JsonObject
        {
            ["engine"] = engine,
            ["res"] = res,
            ["samples"] = samples,
            ["seed"] = 0,
            ["out"] = Path.Combine(fixture.Root, $"debug-{engine}-{Guid.NewGuid():N}.png"),
        }, fixture.Options.RenderTimeout, ct);

    private async Task<JsonObject> RawProbeAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo(fixture.Options.BlenderPath!)
        {
            ArgumentList = { "--background", "--factory-startup", "--python-exit-code", "1", "--python-expr", RawDeviceProbe },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ChildEnvironment.Restrict(
            start.Environment, name => ChildEnvironment.AllowsForWorker(name, fixture.Options.WorkerEnvPassthrough));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Blender did not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var line = (await stdout).ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(text => text.StartsWith("ABL_PROBE ", StringComparison.Ordinal));
        Assert.True(line is not null, $"raw probe printed no result (exit {process.ExitCode}): {await stderr}");
        return JsonNode.Parse(line!["ABL_PROBE ".Length..])!.AsObject();
    }

    // The same independent script GpuDeviceTests uses, on the session's own worker.
    private async Task<JsonObject> PreferenceSnapshotAsync(string sessionId, string phase, bool flip)
    {
        var path = Path.Combine(fixture.Root, $"metal-prefs-{phase}-{Guid.NewGuid():N}.json");
        await fixture.Sessions.ExecAsync(sessionId, GpuDeviceTests.PreferenceSnapshot,
            new JsonObject { ["out"] = path, ["flip"] = flip }, default);
        return JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    }

    private async Task<JsonNode> CaptureCheckpointAsync(string checkpoint)
    {
        var branch = await fixture.Sessions.BranchAsync(checkpoint, "inspect-metal-render-state", default);
        try
        {
            var path = Path.Combine(fixture.Root, $"metal-state-{Guid.NewGuid():N}.json");
            await fixture.Sessions.ExecAsync(branch.Id, RenderStateIsolationTests.Capture,
                new JsonObject { ["capture"] = path }, default);
            return JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        }
        finally
        {
            await fixture.Sessions.CloseAsync(branch.Id);
        }
    }

    private static JsonNode? Finite(double value) => double.IsFinite(value) ? JsonValue.Create(value) : JsonValue.Create("inf");

    private static JsonObject NonZeroBins(long[][] histogram)
    {
        var result = new JsonObject();
        foreach (var (channel, counts) in new[] { "r", "g", "b", "a" }.Zip(histogram))
        {
            var bins = new JsonObject();
            for (var d = 0; d < counts.Length; d++)
                if (counts[d] > 0) bins[d.ToString(System.Globalization.CultureInfo.InvariantCulture)] = counts[d];
            result[channel] = bins;
        }

        return result;
    }

    private void Evidence(string name, JsonObject record)
    {
        var text = record.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine($"== metal-{name}.json\n{text}");
        var dir = Environment.GetEnvironmentVariable("ABL_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"metal-{name}.json"), text);
    }
}
