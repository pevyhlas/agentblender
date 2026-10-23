using System.Text.Json.Nodes;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class WorkerProtocolTests(BlenderFixture fixture)
{
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task FullProtocolRoundTrip()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var worker = await fixture.Pool.TakeAsync(ct);
        try
        {
            // spawn already ran hello+warm; exec a script that reads params
            var scriptPath = Path.Combine(fixture.Root, "cube.py");
            await File.WriteAllTextAsync(scriptPath, """
                import bpy, bmesh
                size = float(params["size"])
                mesh = bpy.data.meshes.new("ProtoCube")
                bm = bmesh.new(); bmesh.ops.create_cube(bm, size=size); bm.to_mesh(mesh); bm.free()
                obj = bpy.data.objects.new("ProtoCube", mesh)
                bpy.context.scene.collection.objects.link(obj)
                """, ct);
            var exec = await worker.RequestAsync("exec", new JsonObject
            {
                ["script"] = scriptPath,
                ["params"] = new JsonObject { ["size"] = 2 },
            }, OpTimeout, ct);
            Assert.Contains("ProtoCube", exec["state_delta"]!["added"]!.AsArray().Select(n => (string)n!));

            // save → load round-trip resets the delta journal
            var blend = Path.Combine(fixture.Root, "proto.blend");
            var save = await worker.RequestAsync("save", new JsonObject { ["path"] = blend }, OpTimeout, ct);
            Assert.True(save["bytes"]!.GetValue<long>() > 0);

            await worker.RequestAsync("load", new JsonObject { ["path"] = blend }, OpTimeout, ct);
            var state = await worker.RequestAsync(
                "state", new JsonObject { ["mode"] = "full" }, OpTimeout, ct);
            Assert.True(state["state"]!["objects"]!.AsObject().ContainsKey("ProtoCube"));

            // render + contact sheet land on disk; no samples, so this render keeps the product
            // default EEVEE sample count (the batch and journey tests render one sample)
            var png = Path.Combine(fixture.Root, "proto.png");
            await worker.RequestAsync("render", new JsonObject
            {
                ["engine"] = "eevee",
                ["res"] = 96,
                ["out"] = png,
            }, TimeSpan.FromSeconds(120), ct);
            Assert.True(new FileInfo(png).Length > 0);

            var sheet = Path.Combine(fixture.Root, "proto-sheet.png");
            await worker.RequestAsync("contact_sheet", new JsonObject
            {
                ["inputs"] = new JsonArray(png),
                ["labels"] = new JsonArray("v0"),
                ["columns"] = 1,
                ["cell"] = 96,
                ["out"] = sheet,
            }, OpTimeout, ct);
            Assert.True(new FileInfo(sheet).Length > 0);
        }
        finally
        {
            await worker.ShutdownAsync();
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task SaveIsByteStable()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var worker = await fixture.Pool.TakeAsync(ct);
        try
        {
            var a = Path.Combine(fixture.Root, "stable-a.blend");
            var b = Path.Combine(fixture.Root, "stable-b.blend");
            await worker.RequestAsync("save", new JsonObject { ["path"] = a }, OpTimeout, ct);
            await worker.RequestAsync("save", new JsonObject { ["path"] = b }, OpTimeout, ct);
            Assert.Equal(await File.ReadAllBytesAsync(a, ct), await File.ReadAllBytesAsync(b, ct));
        }
        finally
        {
            await worker.ShutdownAsync();
            await worker.DisposeAsync();
        }
    }
}
