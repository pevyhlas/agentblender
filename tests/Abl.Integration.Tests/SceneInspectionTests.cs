using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Sessions;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class SceneInspectionTests(BlenderFixture fixture)
{
    internal const string Setup = """
        import bpy, __main__, fingerprint
        s = bpy.context.scene
        c = s.camera
        c.location = (0,0,10)
        c.rotation_euler = (0,0,0)
        c.data.type = 'ORTHO'
        c.data.ortho_scale = 4
        s.render.resolution_x = 200
        s.render.resolution_y = 100
        s.render.resolution_percentage = 100
        s.render.pixel_aspect_x = 1
        s.render.pixel_aspect_y = 1
        cube = bpy.data.objects['Cube']
        cube.location = (1,2,3)
        m = bpy.data.materials.new('InspectionMaterial')
        m.use_nodes = True
        cube.data.materials.clear()
        cube.data.materials.append(m)
        cube.modifiers.new('Bevel','BEVEL').width = .1
        bpy.context.view_layer.update()
        __main__.inspection_before = fingerprint.snapshot()
        """;

    [Fact(Timeout = 180_000)]
    public async Task NumericProjectionReadIsolationAndChangesAreObservable()
    {
        Assert.True(fixture.BlenderAvailable, "ABL_BLENDER must select real Blender");
        var session = await fixture.Sessions.OpenAsync("inspection", default);
        try
        {
            var edit = await fixture.Sessions.ExecAsync(session.Id, Setup, null, default);
            var before = File.ReadAllBytes(fixture.Store.ObjectPath(edit.Checkpoint));
            var journalCount = fixture.Store.ReadJournal(session.Id).Count;
            var query = new SceneInspectionRequest
            {
                Objects = ["Cube"],
                Geometry = true,
                Materials = true,
                Modifiers = true,
                Landmarks = [new("origin", [0, 0, 0]), new("offset", [1, .5, 0]), new("behind", [0, 0, 20])],
            };
            var result = await fixture.Sessions.InspectAsync(session.Id, query, default);
            Assert.Equal(edit.Checkpoint, result["sourceCheckpoint"]!.GetValue<string>());
            Assert.Equal(200, result["framing"]!["width"]!.GetValue<int>());
            Assert.Equal(100, result["framing"]!["height"]!.GetValue<int>());
            AssertVector(result["camera"]!["right"]!, [1, 0, 0]);
            AssertVector(result["camera"]!["up"]!, [0, 1, 0]);
            AssertVector(result["camera"]!["forward"]!, [0, 0, -1]);
            AssertVector(result["landmarks"]![0]!["pixels"]!, [100, 50]);
            AssertVector(result["landmarks"]![1]!["pixels"]!, [150, 25]);
            Assert.False(result["landmarks"]![2]!["inFront"]!.GetValue<bool>());
            Assert.Equal(1, result["objects"]![0]!["matrixWorld"]![0]![3]!.GetValue<double>());
            Assert.Equal(edit.Checkpoint, Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).Head);
            Assert.Equal(journalCount, fixture.Store.ReadJournal(session.Id).Count);
            Assert.Equal(before, File.ReadAllBytes(fixture.Store.ObjectPath(edit.Checkpoint)));
            var delta = await fixture.Sessions.StateAsync(session.Id, "delta", default);
            Assert.Empty(delta["added"]!.AsArray());
            Assert.Empty(delta["changed"]!.AsArray());
            await fixture.Sessions.ExecAsync(session.Id, """
                import bpy, __main__, fingerprint
                assert fingerprint.snapshot() == __main__.inspection_before
                assert __main__.journal._last == __main__.inspection_before
                assert bpy.context.scene.render.resolution_x == 200
                assert bpy.context.scene.camera.data.ortho_scale == 4
                bpy.data.objects['Cube'].data.vertices[0].co.x += .25
                bpy.data.materials['InspectionMaterial'].node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value = .25
                bpy.data.objects['Cube'].modifiers['Bevel'].width = .2
                bpy.context.scene.camera.data.ortho_scale = 8
                """, null, default);
            var changed = await fixture.Sessions.InspectAsync(session.Id, query, default);
            Assert.NotEqual(result["objects"]![0]!["geometry"]!["sha256"]!.GetValue<string>(),
                changed["objects"]![0]!["geometry"]!["sha256"]!.GetValue<string>());
            Assert.NotEqual(result["objects"]![0]!["materials"]![0]!["observationSha256"]!.GetValue<string>(),
                changed["objects"]![0]!["materials"]![0]!["observationSha256"]!.GetValue<string>());
            Assert.Equal(.2, changed["objects"]![0]!["modifiers"]![0]!["values"]!["width"]!.GetValue<double>(), 6);
            AssertVector(changed["landmarks"]![1]!["pixels"]!, [125, 37.5]);
        }
        finally { await fixture.Sessions.CloseAsync(session.Id); }
    }

    [Fact(Timeout = 120_000)]
    public async Task InspectionDoesNotConsumePendingDeltaAndObservesLooseEdges()
    {
        Assert.True(fixture.BlenderAvailable, "ABL_BLENDER must select real Blender");
        var session = await fixture.Sessions.OpenAsync("inspection-delta-edges", default);
        try
        {
            await fixture.Sessions.ExecAsync(session.Id, """
                import bpy, __main__
                mesh = bpy.data.meshes.new('LooseMesh')
                mesh.from_pydata([(0,0,0),(1,0,0),(0,1,0)], [(0,1)], [])
                obj = bpy.data.objects.new('LooseMesh', mesh)
                bpy.context.scene.collection.objects.link(obj)
                original_advance = __main__.journal.advance
                def install_pending_after_this_exec():
                    delta = original_advance()
                    __main__.journal._last['objects'].pop('Cube',None)
                    __main__.journal.advance = original_advance
                    return delta
                __main__.journal.advance = install_pending_after_this_exec
                """, null, default);
            var query = new SceneInspectionRequest { Objects = ["LooseMesh"], Geometry = true };
            var before = await fixture.Sessions.InspectAsync(session.Id, query, default);
            var delta = await fixture.Sessions.StateAsync(session.Id, "delta", default);
            Assert.Contains("Cube", delta["added"]!.AsArray().Select(n => n!.GetValue<string>()));
            await fixture.Sessions.ExecAsync(session.Id, """
                import bpy
                mesh = bpy.data.meshes['LooseMesh']
                mesh.clear_geometry()
                mesh.from_pydata([(0,0,0),(1,0,0),(0,1,0)], [(0,2)], [])
                """, null, default);
            var after = await fixture.Sessions.InspectAsync(session.Id, query, default);
            Assert.Equal(1, before["objects"]![0]!["geometry"]!["edges"]!.GetValue<int>());
            Assert.Equal(1, after["objects"]![0]!["geometry"]!["edges"]!.GetValue<int>());
            Assert.NotEqual(before["objects"]![0]!["geometry"]!["sha256"]!.GetValue<string>(),
                after["objects"]![0]!["geometry"]!["sha256"]!.GetValue<string>());
        }
        finally { await fixture.Sessions.CloseAsync(session.Id); }
    }

    [Fact(Timeout = 120_000)]
    public async Task PerspectiveProjectionAndRawWorkLimits()
    {
        Assert.True(fixture.BlenderAvailable, "ABL_BLENDER must select real Blender");
        var session = await fixture.Sessions.OpenAsync("inspection-bounds", default);
        try
        {
            await fixture.Sessions.ExecAsync(session.Id, Setup + "\n" + """
                c.data.type = 'PERSP'
                c.data.sensor_fit = 'HORIZONTAL'
                c.data.sensor_width = 36
                c.data.lens = 36
                """, null, default);
            var projected = await fixture.Sessions.InspectAsync(session.Id, new()
            {
                Landmarks = [new("offset", [1, .5, 0]), new("on-camera-plane", [0, 0, 10])],
            }, default);
            AssertVector(projected["landmarks"]![0]!["pixels"]!, [120, 40]);
            Assert.Null(projected["landmarks"]![1]!["pixels"]);
            var head = await fixture.Sessions.ExecAsync(session.Id, """
                import bpy
                mesh = bpy.data.meshes.new('too-many-vertices')
                mesh.from_pydata([(0,0,0)] * 100001, [], [])
                obj = bpy.data.objects.new('LargeRawMesh', mesh)
                bpy.context.scene.collection.objects.link(obj)
                """, null, default);
            var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.InspectAsync(session.Id,
                new() { Objects = ["LargeRawMesh"], Geometry = true }, default));
            Assert.Equal("bad_request", error.Kind);
            Assert.Contains("vertices", error.Message);
            Assert.Contains("split", error.Message);
            Assert.Null(error.WorkerTraceback);
            Assert.Equal(head.Checkpoint, error.LastCheckpoint);
            Assert.Equal("ready", Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).State);
        }
        finally { await fixture.Sessions.CloseAsync(session.Id); }
    }

    [Fact(Timeout = 120_000)]
    public async Task ShiftPercentageBorderClippingAndEvaluatedConstraint()
    {
        Assert.True(fixture.BlenderAvailable, "ABL_BLENDER must select real Blender");
        var session = await fixture.Sessions.OpenAsync("inspection-framing", default);
        try
        {
            await fixture.Sessions.ExecAsync(session.Id, Setup + "\n" + """
                c.data.shift_x = .25
                s.render.resolution_percentage = 50
                s.render.pixel_aspect_x = 2
                s.render.use_border = True
                s.render.use_crop_to_border = True
                s.render.border_min_x = .25
                s.render.border_max_x = .75
                target = bpy.data.objects.new('Target',None)
                s.collection.objects.link(target)
                target.location = (4,5,6)
                constraint = cube.constraints.new('COPY_LOCATION')
                constraint.target = target
                """, null, default);
            var result = await fixture.Sessions.InspectAsync(session.Id, new()
            {
                Objects = ["Cube"],
                Landmarks = [new("origin", [0, 0, 0]), new("clipped", [0, 0, 9.95])],
            }, default);
            Assert.Equal(100, result["framing"]!["width"]!.GetValue<int>());
            Assert.Equal(50, result["framing"]!["height"]!.GetValue<int>());
            AssertVector(result["framing"]!["pixelAspect"]!, [2, 1]);
            Assert.True(result["framing"]!["border"]!.GetValue<bool>());
            Assert.True(result["framing"]!["cropToBorder"]!.GetValue<bool>());
            AssertVector(result["landmarks"]![0]!["pixels"]!, [25, 25]);
            Assert.False(result["landmarks"]![1]!["inFrame"]!.GetValue<bool>());
            var matrix = result["objects"]![0]!["matrixWorld"]!;
            Assert.Equal(4, matrix[0]![3]!.GetValue<double>());
            Assert.Equal(5, matrix[1]![3]!.GetValue<double>());
            Assert.Equal(6, matrix[2]![3]!.GetValue<double>());
        }
        finally { await fixture.Sessions.CloseAsync(session.Id); }
    }

    [Theory(Timeout = 120_000)]
    [InlineData("matrix")]
    [InlineData("pixels")]
    [InlineData("camera")]
    [InlineData("geometry")]
    [InlineData("duplicate")]
    public async Task MalformedMeasurementsAreWorkerFatal(string fault)
    {
        Assert.True(fixture.BlenderAvailable, "ABL_BLENDER must select real Blender");
        var session = await fixture.Sessions.OpenAsync("inspection-invalid-facts", default);
        try
        {
            var saved = await fixture.Sessions.ExecAsync(session.Id, Setup + "\n" + """
                original = __main__.HANDLERS['inspect']
                fault = params['fault']
                def corrupt(req):
                    result = original(req)
                    data = result['inspection']
                    if fault == 'matrix': data['objects'][0]['matrixWorld'][0][0] = 'invalid'
                    if fault == 'pixels': data['landmarks'][0]['pixels'] = [None, 'invalid']
                    if fault == 'camera': data['camera']['name'] = 'wrong-camera'
                    if fault == 'geometry': del data['objects'][0]['geometry']
                    return result
                __main__.HANDLERS['inspect'] = corrupt
                if fault == 'duplicate':
                    import json, struct
                    original_send = __main__.send_message
                    def duplicate_send(conn, payload):
                        if 'inspection' not in payload: return original_send(conn, payload)
                        raw = json.dumps(payload).replace('"profile": "abl.scene-inspection.v1"',
                            '"profile": "abl.scene-inspection.v1", "profile": "abl.scene-inspection.v1"').encode('utf-8')
                        conn.sendall(struct.pack('>I',len(raw)) + raw)
                    __main__.send_message = duplicate_send
                """, new() { ["fault"] = fault }, default);
            var count = fixture.Store.ReadJournal(session.Id).Count;
            var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.InspectAsync(session.Id,
                new() { Objects = ["Cube"], Camera = "Camera", Geometry = true, Landmarks = [new("p", [0, 0, 0])] }, default));
            Assert.Equal("worker_died", error.Kind);
            Assert.Equal(saved.Checkpoint, error.LastCheckpoint);
            Assert.Equal(count, fixture.Store.ReadJournal(session.Id).Count);
            Assert.Equal("dead", Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).State);
        }
        finally { await fixture.Sessions.CloseAsync(session.Id); }
    }

    [Fact(Timeout = 120_000)]
    public async Task InvalidFactsAndMalformedReplyPreserveCheckpoint()
    {
        Assert.True(fixture.BlenderAvailable, "ABL_BLENDER must select real Blender");
        var session = await fixture.Sessions.OpenAsync("inspection-errors", default);
        try
        {
            var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.InspectAsync(session.Id,
                new() { Objects = ["absent"] }, default));
            Assert.Equal("bad_request", error.Kind);
            Assert.Equal(session.Head, error.LastCheckpoint);
            await fixture.Sessions.ExecAsync(session.Id, """
                import bpy
                bpy.context.scene.camera.data.type = 'PANO'
                """, null, default);
            Assert.Equal("bad_request", (await Assert.ThrowsAsync<AblException>(() =>
                fixture.Sessions.InspectAsync(session.Id, new(), default))).Kind);
            var saved = await fixture.Sessions.ExecAsync(session.Id, """
                import __main__
                __main__.HANDLERS['inspect'] = lambda req: {'inspection': {'profile': 3}}
                """, null, default);
            var dead = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.InspectAsync(session.Id, new(), default));
            Assert.Equal("worker_died", dead.Kind);
            Assert.Equal(saved.Checkpoint, dead.LastCheckpoint);
        }
        finally { await fixture.Sessions.CloseAsync(session.Id); }
    }

    internal static void AssertVector(JsonNode node, double[] expected)
    {
        Assert.Equal(expected.Length, node.AsArray().Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], node[i]!.GetValue<double>(), 5);
    }
}
