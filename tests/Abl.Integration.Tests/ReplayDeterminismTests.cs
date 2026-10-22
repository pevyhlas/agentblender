using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class ReplayDeterminismTests(BlenderFixture fixture)
{
    [Fact]
    public async Task FrozenTrajectoryReplaysDigestEqualThreeTimes()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("replay-test", ct);

        string[] trajectory =
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
        foreach (var script in trajectory)
        {
            await fixture.Sessions.ExecAsync(session.Id, script, null, ct);
        }

        var artifact = fixture.Trajectories.Freeze(session.Id, "replay-test-artifact", null, null);
        Assert.Equal(3, artifact.Ops.Count);
        await fixture.Sessions.CloseAsync(session.Id);

        // FR-007/SC-004: three fresh workers, three digest-equal finals (10x in the SC evidence run).
        for (var i = 0; i < 3; i++)
        {
            var result = await fixture.Trajectories.ReplayAsync("replay-test-artifact", ct);
            Assert.True(result.Pass,
                $"replay {i}: got {result.FinalDigest[..12]} expected {result.ExpectedDigest[..12]}");
        }
    }
}
