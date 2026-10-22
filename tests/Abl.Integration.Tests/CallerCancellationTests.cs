using Abl.Core;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class CallerCancellationTests(BlenderFixture fixture)
{
    [Theory(Timeout = 120_000)]
    [InlineData("save", "digest")]
    [InlineData("save", "blender_version")]
    [InlineData("exec", "duration_ms")]
    [InlineData("exec", "state_delta")]
    public async Task MalformedSuccessAfterMutationPreservesHeadAndJournal(string op, string field)
    {
        if (!fixture.BlenderAvailable) return;
        var session = await fixture.Sessions.OpenAsync("malformed-success", default);
        // Install the fault only during the next exec, so this setup still commits normally.
        var setup = await fixture.Sessions.ExecAsync(session.Id, $$"""
            import bpy, __main__
            original_exec = __main__.HANDLERS["exec"]
            def fault_next(req):
                result = original_exec(req)
                bpy.data.objects["Cube"].location.x = 10
                if "{{op}}" == "save":
                    original_save = __main__.HANDLERS["save"]
                    def bad_save(save_req):
                        saved = original_save(save_req)
                        saved["{{field}}"] = 3
                        return saved
                    __main__.HANDLERS["save"] = bad_save
                else:
                    result["{{field}}"] = "wrong-type"
                return result
            __main__.HANDLERS["exec"] = fault_next
            """, null, default);
        var entries = fixture.Store.ReadJournal(session.Id).Count;
        var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.ExecAsync(session.Id, "pass", null, default));
        Assert.Equal("worker_died", error.Kind);
        Assert.Equal(setup.Checkpoint, error.LastCheckpoint);
        Assert.Equal(entries, fixture.Store.ReadJournal(session.Id).Count);
        Assert.Equal("dead", Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).State);
        var branch = await fixture.Sessions.BranchAsync(setup.Checkpoint, "success-recovered", default);
        await fixture.Sessions.ExecAsync(branch.Id, "import bpy\nassert bpy.data.objects[\"Cube\"].location.x == 0\n", null, default);
        await fixture.Sessions.CloseAsync(branch.Id);
        await fixture.Sessions.CloseAsync(session.Id);
    }

    [Fact(Timeout = 120_000)]
    public async Task MalformedReplyAfterMutationCannotLeaveAReadyDivergentSession()
    {
        if (!fixture.BlenderAvailable) return;
        var session = await fixture.Sessions.OpenAsync("malformed-response", default);
        var initial = await fixture.Sessions.ExecAsync(session.Id, """
            import bpy, __main__
            def broken_exec(req):
                bpy.data.objects["Cube"].location.x = 10
                return {"ok": "yes"}
            __main__.HANDLERS["exec"] = broken_exec
            """, null, default);
        var error = await Assert.ThrowsAsync<AblException>(() =>
            fixture.Sessions.ExecAsync(session.Id, "pass", null, default));
        Assert.Equal("worker_died", error.Kind);
        Assert.Equal(initial.Checkpoint, error.LastCheckpoint);
        Assert.Equal("dead", Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id).State);
        var recovered = await fixture.Sessions.BranchAsync(initial.Checkpoint, "recovered-malformed", default);
        await fixture.Sessions.ExecAsync(recovered.Id,
            "import bpy\nassert bpy.data.objects[\"Cube\"].location.x == 0\n", null, default);
        await fixture.Sessions.CloseAsync(recovered.Id);
        await fixture.Sessions.CloseAsync(session.Id);
    }

    [Fact(Timeout = 120_000)]
    public async Task CancelledExecIsDeadAndRecoverableFromThePreservedHead()
    {
        if (!fixture.BlenderAvailable) return;
        var session = await fixture.Sessions.OpenAsync("caller-cancellation", default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Sessions.ExecAsync(session.Id, "import time\ntime.sleep(30)\n", null, cancel.Token));
        var dead = Assert.Single(fixture.Sessions.List(), s => s.Id == session.Id);
        Assert.Equal("dead", dead.State);
        Assert.Equal(session.Head, dead.Head);
        var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.StateAsync(session.Id, "full", default));
        Assert.Equal("worker_died", error.Kind);
        Assert.Equal(session.Head, error.LastCheckpoint);
        var recovered = await fixture.Sessions.BranchAsync(session.Head, "caller-recovered", default);
        Assert.Equal("ready", recovered.State);
        await fixture.Sessions.CloseAsync(recovered.Id);
        await fixture.Sessions.CloseAsync(session.Id);
    }
}
