using System.Diagnostics;
using Abl.Core;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class SessionIsolationTests(BlenderFixture fixture)
{
    private const string AddMarker = """
        import bpy, bmesh
        name = params["name"]
        mesh = bpy.data.meshes.new(name)
        bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1); bm.to_mesh(mesh); bm.free()
        bpy.context.scene.collection.objects.link(bpy.data.objects.new(name, mesh))
        """;

    [Fact]
    public async Task InterleavedSessionsNeverCrossObserve()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var a = await fixture.Sessions.OpenAsync("iso-a", ct);
        var b = await fixture.Sessions.OpenAsync("iso-b", ct);

        await fixture.Sessions.ExecAsync(a.Id, AddMarker, new() { ["name"] = "OnlyInA" }, ct);
        await fixture.Sessions.ExecAsync(b.Id, AddMarker, new() { ["name"] = "OnlyInB" }, ct);

        var stateA = await fixture.Sessions.StateAsync(a.Id, "full", ct);
        var stateB = await fixture.Sessions.StateAsync(b.Id, "full", ct);
        Assert.True(stateA["objects"]!.AsObject().ContainsKey("OnlyInA"));
        Assert.False(stateA["objects"]!.AsObject().ContainsKey("OnlyInB"));
        Assert.True(stateB["objects"]!.AsObject().ContainsKey("OnlyInB"));
        Assert.False(stateB["objects"]!.AsObject().ContainsKey("OnlyInA"));

        await fixture.Sessions.CloseAsync(a.Id);
        await fixture.Sessions.CloseAsync(b.Id);
    }

    [Fact]
    public async Task KilledWorkerIsContainedAndRecoverableWithinTenSeconds()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var victim = await fixture.Sessions.OpenAsync("iso-victim", ct);
        var bystander = await fixture.Sessions.OpenAsync("iso-bystander", ct);

        var exec = await fixture.Sessions.ExecAsync(
            victim.Id, AddMarker, new() { ["name"] = "Precious" }, ct);
        var lastGood = exec.Checkpoint;
        await fixture.Sessions.ExecAsync(
            bystander.Id, AddMarker, new() { ["name"] = "Bystander" }, ct);

        // SIGKILL the victim's Blender process mid-life (simulated crash).
        Process.GetProcessById(fixture.Sessions.WorkerPid(victim.Id)).Kill(entireProcessTree: true);

        var error = await Assert.ThrowsAsync<AblException>(
            () => fixture.Sessions.StateAsync(victim.Id, "full", ct));
        Assert.Equal("worker_died", error.Kind);
        Assert.Equal(lastGood, error.LastCheckpoint);
        Assert.Contains("checkpoint_branch", error.Hint!);

        // FR-010: the bystander is untouched.
        var stateB = await fixture.Sessions.StateAsync(bystander.Id, "full", ct);
        Assert.True(stateB["objects"]!.AsObject().ContainsKey("Bystander"));

        // SC-005: recovery to the last checkpoint in under 10 s.
        var stopwatch = Stopwatch.StartNew();
        var recovered = await fixture.Sessions.BranchAsync(error.LastCheckpoint!, "iso-recovered", ct);
        var stateR = await fixture.Sessions.StateAsync(recovered.Id, "full", ct);
        stopwatch.Stop();
        Assert.True(stateR["objects"]!.AsObject().ContainsKey("Precious"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"recovery took {stopwatch.Elapsed.TotalSeconds:0.0}s");

        await fixture.Sessions.CloseAsync(recovered.Id);
        await fixture.Sessions.CloseAsync(bystander.Id);
        await fixture.Sessions.CloseAsync(victim.Id);
    }
}
