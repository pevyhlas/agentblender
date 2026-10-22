using Abl.Core;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// A worker failure on the render path (a transient pooled worker, not the session's)
/// must surface as the structured error contract — not escape as a raw worker exception
/// that a surface then returns as an unparseable empty-body 500. Regression for the
/// native-install finding (2026-09-12).
/// </summary>
[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class RenderErrorTests(BlenderFixture fixture)
{
    [Fact]
    public async Task UnknownEngineIsBadRequestNotACrash()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("itest-render-err", ct);
        await fixture.Sessions.ExecAsync(
            session.Id, "import bpy\nbpy.ops.mesh.primitive_cube_add()", null, ct);

        var error = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.RenderFinalAsync(
            session.Id, null, engine: "cycles", res: 256, samples: 8,
            outPath: null, leaseMode: "wait", seed: 0, ct));

        // A caller typo is a client error, and the message names the valid set — not a
        // cryptic Python KeyError.
        Assert.Equal("bad_request", error.Kind);
        Assert.Contains("unknown engine 'cycles'", error.Message);
        Assert.Contains("cycles_gpu", error.Message);

        // The worker survived: a real render on the same session still works.
        var ok = await fixture.Sessions.RenderFinalAsync(
            session.Id, null, engine: "eevee", res: 128, samples: 4,
            outPath: null, leaseMode: "cpu_fallback", seed: 0, ct);
        Assert.True(new FileInfo(ok.Path).Length > 0);

        await fixture.Sessions.CloseAsync(session.Id);
    }

    [Fact]
    public async Task UnknownEngineIsBadRequestOnEverySurfacePath()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var session = await fixture.Sessions.OpenAsync("itest-render-err2", ct);
        var head = (await fixture.Sessions.ExecAsync(
            session.Id, "import bpy\nbpy.ops.mesh.primitive_cube_add()", null, ct)).Checkpoint;

        // Transient checkpoint-render path (pooled worker, no session).
        var fromCheckpoint = await Assert.ThrowsAsync<AblException>(() => fixture.Sessions.RenderFinalAsync(
            null, head, engine: "cycles", res: 128, samples: 4,
            outPath: null, leaseMode: "cpu_fallback", seed: 0, ct));
        Assert.Equal("bad_request", fromCheckpoint.Kind);

        // Preview renders through GuardedAsync — the worker's bad_request must be honored
        // there too, so the same mistake reads the same on every surface.
        var fromPreview = await Assert.ThrowsAsync<AblException>(
            () => fixture.Sessions.PreviewAsync(session.Id, 128, "cycles", ct));
        Assert.Equal("bad_request", fromPreview.Kind);

        await fixture.Sessions.CloseAsync(session.Id);
    }
}
