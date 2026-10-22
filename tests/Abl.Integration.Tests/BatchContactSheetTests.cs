using System.Text.Json.Nodes;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class BatchContactSheetTests(BlenderFixture fixture)
{
    [Fact]
    public async Task NineVariantsYieldNineRendersAndExactlyOneSheet()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("batch-test", ct);
        await fixture.Sessions.ExecAsync(session.Id, """
            import bpy, bmesh
            size = float(params.get("size", 1))
            mesh = bpy.data.meshes.new("Variant")
            bm = bmesh.new(); bmesh.ops.create_cube(bm, size=size); bm.to_mesh(mesh); bm.free()
            bpy.context.scene.collection.objects.link(bpy.data.objects.new("Variant", mesh))
            bpy.context.scene.eevee.taa_render_samples = 1  # one sample: the assertions check outputs, not quality
            """, new JsonObject { ["size"] = 1 }, ct);
        fixture.Trajectories.Freeze(session.Id, "batch-test-artifact", null, null);
        await fixture.Sessions.CloseAsync(session.Id);

        var table = Enumerable.Range(1, 9)
            .Select(i => new JsonObject { ["size"] = 0.5 + (i * 0.2) })
            .ToList();
        var result = await fixture.Trajectories.BatchAsync(
            "batch-test-artifact", table, res: 96, columns: 3, engine: "eevee", ct);

        // SC-003: nine disk renders, ONE returned montage.
        Assert.Equal(9, result.Renders.Count);
        Assert.All(result.Renders, p => Assert.True(new FileInfo(p).Length > 0));
        Assert.True(new FileInfo(result.ContactSheet).Length > 0);
        var outDir = Path.GetDirectoryName(result.ContactSheet)!;
        Assert.Equal(10, Directory.GetFiles(outDir, "*.png").Length); // 9 variants + 1 sheet
    }
}
