using Abl.Core;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// Feature 002 SC-001/004 + FR-005/007: a live trajectory exported as a bundle and
/// imported into a store that has NEVER seen the session reconstitutes digest-verified —
/// the "clone the repo on another machine" path, minus the machine.
/// </summary>
[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class BundlePortabilityTests(BlenderFixture fixture)
{
    [Fact]
    public async Task BundleImportedIntoFreshStoreReplaysDigestEqual()
    {
        if (!fixture.BlenderAvailable)
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;

        // Build a small real trajectory and freeze it.
        var session = await fixture.Sessions.OpenAsync("bundle-test", ct);
        await fixture.Sessions.ExecAsync(session.Id, """
            import bpy, bmesh
            mesh = bpy.data.meshes.new("Crate")
            bm = bmesh.new(); bmesh.ops.create_cube(bm, size=float(params["size"])); bm.to_mesh(mesh); bm.free()
            bpy.context.scene.collection.objects.link(bpy.data.objects.new("Crate", mesh))
            """, new() { ["size"] = 1.5 }, ct);
        await fixture.Sessions.ExecAsync(session.Id, """
            import bpy
            mat = bpy.data.materials.new("Oak")
            mat.diffuse_color = (0.45, 0.3, 0.15, 1.0)
            bpy.data.objects["Crate"].data.materials.append(mat)
            """, null, ct);
        var frozen = fixture.Trajectories.Freeze(session.Id, "bundle-crate", null, null);
        await fixture.Sessions.CloseAsync(session.Id);
        Assert.NotNull(frozen.BlenderVersion); // FR-007: provenance captured at save

        // Export → the "repo" → import into a FRESH store with its own services.
        var repoDir = Path.Combine(fixture.Root, "fake-repo");
        var export = fixture.Trajectories.Export("bundle-crate", repoDir);
        Assert.Equal(2, Directory.GetFiles(export.Directory).Length);

        // SC-004: bundle weight — demo-scale bundles stay small enough for git.
        var totalBytes = Directory.GetFiles(export.Directory).Sum(f => new FileInfo(f).Length);
        Assert.True(totalBytes < 1_000_000, $"bundle is {totalBytes} B (SC-004 bound 1 MB)");
        Assert.True(new FileInfo(export.ArtifactJson).Length < 65_536, "artifact.json over 64 KB");

        var freshStoreDir = Path.Combine(fixture.Root, "fresh-store");
        var freshOptions = TestOptions.FromEnvironment() with
        {
            StoreDir = freshStoreDir,
            RendersDir = fixture.Options.RendersDir,
            PoolFloor = 0,
        };
        var freshStore = new CheckpointStore(freshStoreDir);
        var freshTrajectories = new TrajectoryService(
            freshOptions, fixture.Pool, freshStore, new GpuLease(),
            new DesktopTier(freshOptions, NullLogger<DesktopTier>.Instance),
            NullLogger<TrajectoryService>.Instance);

        var import = freshTrajectories.Import(export.Directory);
        Assert.Equal("bundle-crate", import.Name);
        Assert.Equal(2, import.Ops);
        Assert.Equal(frozen.ExpectedDigest, import.ExpectedDigest);
        Assert.Equal(frozen.BlenderVersion, import.BlenderVersion);

        // SC-001: the reconstitution proof — replay in the fresh store passes the gate.
        var replay = await freshTrajectories.ReplayAsync("bundle-crate", ct);
        Assert.True(replay.Pass,
            $"got {replay.FinalDigest[..12]} expected {replay.ExpectedDigest[..12]}");
        // Same Blender here, so no mismatch is surfaced (FR-005).
        Assert.Null(replay.ArtifactBlenderVersion);

        // The imported base checkpoint is a first-class store citizen: render from it.
        var freshSessions = new SessionService(
            freshOptions, fixture.Pool, freshStore, new GpuLease(),
            new DesktopTier(freshOptions, NullLogger<DesktopTier>.Instance),
            NullLogger<SessionService>.Instance);
        var render = await freshSessions.RenderFinalAsync(
            null, import.BaseCheckpoint, "workbench", 128, null,
            Path.Combine(fixture.Root, "bundle-render.png"), "wait", 0, ct);
        Assert.True(new FileInfo(render.Path).Length > 0);
    }
}
