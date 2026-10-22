using System.Net;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Rendering;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using Abl.Launcher;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// batch_run on the desktop remote worker over a LOOPBACK "remote" (feature 003 / T005b):
/// a real launcher + headless Blender worker on 127.0.0.1 over TCP, exercising the batch
/// path — base shipped once and kept, ops shipped by SOURCE, each variant's pixels shipped
/// back inline, and the contact sheet composed on the worker and shipped back — plus the
/// transparent fallback to the local tier when the desktop is stopped or rejects the token.
/// </summary>
[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class RemoteBatchTests(BlenderFixture fixture)
{
    private const string Token = "loopback-remote-token";

    [Fact(Timeout = 600_000)]
    public async Task ConcurrentBatchesAndFinalsNeverShareDefaultOutputs()
    {
        if (!fixture.BlenderAvailable) return;
        var ct = CancellationToken.None;
        await FreezeArtifactAsync("concurrent-output", ct);
        var batches = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
            fixture.Trajectories.BatchAsync("concurrent-output",
                [new JsonObject { ["size"] = 0.6 + i }], 64, 1, "eevee", ct)));
        Assert.NotEqual(batches[0].Renders[0], batches[1].Renders[0]);
        Assert.NotEqual(batches[0].ContactSheet, batches[1].ContactSheet);
        Assert.All(batches, b => Assert.True(new FileInfo(b.Renders[0]).Length > 0));
        var checkpoint = fixture.Store.GetFrozen("concurrent-output")!.BaseCheckpoint;
        var finals = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            fixture.Sessions.RenderFinalAsync(null, checkpoint, "eevee", 64, null, null, "wait", 0, ct)));
        Assert.NotEqual(finals[0].Path, finals[1].Path);
        Assert.All(finals, r =>
        {
            Assert.True(new FileInfo(r.Path).Length > 0);
            Assert.True(new FileInfo(r.SidecarPath).Length > 0);
        });
    }

    [Fact(Timeout = 600_000)]
    public async Task RoutesBatchToDesktopThenFallsBackToLocal()
    {
        if (!fixture.BlenderAvailable)
        {
            return;
        }

        var ct = CancellationToken.None;
        await FreezeArtifactAsync("remote-batch-desktop", ct);
        var table = Enumerable.Range(1, 3)
            .Select(i => new JsonObject { ["size"] = 0.5 + (i * 0.3) })
            .ToList();

        var launcher = StartLauncher(Token, out var port);
        using var launcherCts = new CancellationTokenSource();
        var launcherLoop = launcher.RunAsync(launcherCts.Token);

        // (1) launcher running → the desktop serves the batch; every variant's pixels and the
        // sheet ship back to the host's disk.
        var trajectories = RemoteTrajectories($"127.0.0.1:{port}", Token);
        var onDesktop = await trajectories.BatchAsync(
            "remote-batch-desktop", table, res: 64, columns: 2, engine: "eevee", ct);

        Assert.Equal("desktop", onDesktop.Tier);
        Assert.Equal(3, onDesktop.Renders.Count);
        Assert.All(onDesktop.Renders, p => Assert.True(new FileInfo(p).Length > 0, $"{p} has no pixels"));
        Assert.True(new FileInfo(onDesktop.ContactSheet).Length > 0, "the remote contact sheet should ship back");
        var outDir = Path.GetDirectoryName(onDesktop.ContactSheet)!;
        Assert.Equal(4, Directory.GetFiles(outDir, "*.png").Length); // 3 variants + 1 sheet

        // (2) launcher stopped → the SAME batch falls back to local and still produces a sheet.
        await launcherCts.CancelAsync();
        try
        {
            await launcherLoop;
        }
        catch (OperationCanceledException)
        {
        }

        var onLocal = await trajectories.BatchAsync(
            "remote-batch-desktop", table, res: 64, columns: 2, engine: "eevee", ct);
        Assert.Equal("local", onLocal.Tier);
        Assert.Equal("desktop_unavailable", onLocal.FallbackReason);
        Assert.Equal(3, onLocal.Renders.Count);
        Assert.True(new FileInfo(onLocal.ContactSheet).Length > 0, "the fallback batch should still produce a sheet");
    }

    [Fact(Timeout = 600_000)]
    public async Task FallsBackToLocalWhenTheTokenIsRejected()
    {
        if (!fixture.BlenderAvailable)
        {
            return;
        }

        var ct = CancellationToken.None;
        await FreezeArtifactAsync("remote-batch-authreject", ct);
        var table = new List<JsonObject> { new() { ["size"] = 1.0 }, new() { ["size"] = 1.4 } };

        var launcher = StartLauncher("the-right-token", out var port);
        using var launcherCts = new CancellationTokenSource();
        var launcherLoop = launcher.RunAsync(launcherCts.Token);

        // the host holds the WRONG token → handshake refused → bringup fails → local fallback.
        var trajectories = RemoteTrajectories($"127.0.0.1:{port}", "the-wrong-token");
        var result = await trajectories.BatchAsync(
            "remote-batch-authreject", table, res: 64, columns: 2, engine: "eevee", ct);

        Assert.Equal("local", result.Tier);
        Assert.Equal("desktop_bringup_failed", result.FallbackReason);
        Assert.Equal(2, result.Renders.Count);
        Assert.True(new FileInfo(result.ContactSheet).Length > 0);

        await launcherCts.CancelAsync();
        try
        {
            await launcherLoop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FreezeArtifactAsync(string name, CancellationToken ct)
    {
        var session = await fixture.Sessions.OpenAsync("remote-batch", ct);
        await fixture.Sessions.ExecAsync(session.Id, """
            import bpy, bmesh
            size = float(params.get("size", 1))
            mesh = bpy.data.meshes.new("Variant")
            bm = bmesh.new(); bmesh.ops.create_cube(bm, size=size); bm.to_mesh(mesh); bm.free()
            bpy.context.scene.collection.objects.link(bpy.data.objects.new("Variant", mesh))
            """, new JsonObject { ["size"] = 1 }, ct);
        fixture.Trajectories.Freeze(session.Id, name, null, null);
        await fixture.Sessions.CloseAsync(session.Id);
    }

    private LauncherHost StartLauncher(string token, out int port)
    {
        var launcher = new LauncherHost(
            new LauncherOptions
            {
                BindEndpoint = new IPEndPoint(IPAddress.Loopback, 0),
                Token = token,
                BlenderPath = fixture.Options.BlenderPath ?? throw new InvalidOperationException("ABL_BLENDER must select real Blender"),
                WorkerDir = TestOptions.FromEnvironment().WorkerDir,
            },
            NullLogger<LauncherHost>.Instance);
        launcher.Start();
        port = launcher.BoundEndpoint.Port;
        return launcher;
    }

    private TrajectoryService RemoteTrajectories(string endpoint, string token)
    {
        var options = TestOptions.FromEnvironment() with
        {
            StoreDir = fixture.Options.StoreDir,
            RendersDir = fixture.Options.RendersDir,
            PoolFloor = 0,
            DesktopEndpoint = endpoint,
            DesktopToken = token,
            DesktopProbeTtl = TimeSpan.FromMilliseconds(200),
            DesktopConnectTimeout = TimeSpan.FromSeconds(5),
        };
        return new TrajectoryService(
            options, fixture.Pool, fixture.Store, new GpuLease(),
            new DesktopTier(options, NullLogger<DesktopTier>.Instance), NullLogger<TrajectoryService>.Instance);
    }
}
