using Abl.Core;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class ExecAtomicityTests(BlenderFixture fixture)
{
    private const string AddSphere = """
        import bpy, bmesh
        mesh = bpy.data.meshes.new("Orb")
        bm = bmesh.new(); bmesh.ops.create_uvsphere(bm, u_segments=8, v_segments=8, radius=1); bm.to_mesh(mesh); bm.free()
        bpy.context.scene.collection.objects.link(bpy.data.objects.new("Orb", mesh))
        """;

    private const string MutateThenFail = """
        import bpy
        bpy.data.objects.remove(bpy.data.objects["Orb"])
        raise RuntimeError("boom after mutation")
        """;

    [Fact]
    public async Task FailedExecRestoresPreOpStateAndHead()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("itest", ct);

        var ok = await fixture.Sessions.ExecAsync(session.Id, AddSphere, null, ct);
        Assert.Contains("Orb", ok.StateDelta["added"]!.AsArray().Select(n => (string)n!));
        var headBefore = ok.Checkpoint;

        var error = await Assert.ThrowsAsync<AblException>(
            () => fixture.Sessions.ExecAsync(session.Id, MutateThenFail, null, ct));
        Assert.Equal("exec_failed", error.Kind);
        Assert.Equal(headBefore, error.LastCheckpoint);
        Assert.Contains("boom after mutation", error.Message);
        Assert.NotNull(error.WorkerTraceback);

        // FR-003: the failed mutation is not visible — Orb survived, head unchanged.
        var state = await fixture.Sessions.StateAsync(session.Id, "full", ct);
        Assert.True(state["objects"]!.AsObject().ContainsKey("Orb"));
        Assert.Equal(headBefore, fixture.Sessions.List().Single(s => s.Id == session.Id).Head);

        await fixture.Sessions.CloseAsync(session.Id);
    }

    [Fact]
    public async Task ConcurrentOpsOnOneSessionAreRejectedBusy()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("itest-busy", ct);

        var slow = fixture.Sessions.ExecAsync(
            session.Id, "import time\ntime.sleep(2)", null, ct);
        await Task.Delay(300, ct); // let the slow exec take the gate
        var error = await Assert.ThrowsAsync<AblException>(
            () => fixture.Sessions.StateAsync(session.Id, "delta", ct));
        Assert.Equal("session_busy", error.Kind);

        await slow; // the in-flight op completes normally
        await fixture.Sessions.CloseAsync(session.Id);
    }

    [Fact]
    public async Task AutoCheckpointChainsParentLinks()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("itest-chain", ct);
        var first = await fixture.Sessions.ExecAsync(session.Id, AddSphere, null, ct);
        var second = await fixture.Sessions.ExecAsync(
            session.Id, "import bpy\nbpy.data.objects['Orb'].location.x = 2.0", null, ct);

        Assert.NotEqual(first.Checkpoint, second.Checkpoint);
        var meta = fixture.Store.Get(second.Checkpoint)!;
        Assert.Equal(first.Checkpoint, meta.Parent);

        await fixture.Sessions.CloseAsync(session.Id);
    }
}
