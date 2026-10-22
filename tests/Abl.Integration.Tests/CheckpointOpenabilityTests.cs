using System.Text.Json.Nodes;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class CheckpointOpenabilityTests(BlenderFixture fixture)
{
    /// <summary>
    /// SC-006 proxy: every checkpoint is a plain .blend a fresh headless Blender loads
    /// cleanly — which is exactly what the operator's GUI open does (US5).
    /// </summary>
    [Fact]
    public async Task EveryStoredCheckpointLoadsInAFreshWorker()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("openability", ct);
        await fixture.Sessions.ExecAsync(session.Id, """
            import bpy, bmesh
            mesh = bpy.data.meshes.new("Openable")
            bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1); bm.to_mesh(mesh); bm.free()
            bpy.context.scene.collection.objects.link(bpy.data.objects.new("Openable", mesh))
            """, null, ct);
        await fixture.Sessions.CloseAsync(session.Id);

        var checkpoints = fixture.Store.List();
        Assert.NotEmpty(checkpoints);

        var worker = await fixture.Pool.TakeAsync(ct);
        try
        {
            foreach (var meta in checkpoints)
            {
                var response = await worker.RequestAsync(
                    "load",
                    new JsonObject { ["path"] = fixture.Store.ObjectPath(meta.Hash) },
                    TimeSpan.FromSeconds(60), ct);
                Assert.True(response["ok"]!.GetValue<bool>(), $"checkpoint {meta.Hash[..12]} failed to load");
            }
        }
        finally
        {
            await worker.ShutdownAsync();
            await worker.DisposeAsync();
        }
    }
}
