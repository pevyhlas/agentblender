using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Workers;
using Abl.Launcher;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// The desktop remote worker over a LOOPBACK "remote" (feature 003, SC-001/002/005 without a
/// physical desktop): a real launcher + headless Blender worker on 127.0.0.1 over TCP,
/// exercising connect → token → spawn → checkpoint-ship → render → pixels-back, plus the
/// graceful fallback to the local tier when the desktop is stopped, rejects the token, or
/// accepts TCP but never completes the handshake (the intermittent-desktop failure modes).
/// </summary>
[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class RemoteWorkerTests(BlenderFixture fixture)
{
    private const string Token = "loopback-remote-token";

    [Fact(Timeout = 600_000)]
    public async Task RoutesRenderToDesktopThenFallsBackToLocal()
    {
        if (!fixture.BlenderAvailable)
        {
            return;
        }

        var ct = CancellationToken.None;
        var head = await MakeCheckpointAsync(ct);

        var launcher = StartLauncher(Token, out var port);
        using var launcherCts = new CancellationTokenSource();
        var launcherLoop = launcher.RunAsync(launcherCts.Token);

        await using var sessions = RemoteSessions($"127.0.0.1:{port}", Token);

        // (1) launcher running → the desktop tier serves it, pixels ship back.
        var onDesktop = Path.Combine(fixture.Root, "on-desktop.png");
        var desktopRender = await sessions.RenderFinalAsync(null, head, "cycles_cpu", 128, 16, onDesktop, "wait", 0, ct);
        Assert.Equal("desktop", desktopRender.Tier);
        Assert.True(File.Exists(onDesktop), "desktop-rendered pixels should land on the host's disk");
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(onDesktop + ".json", ct))!;
        Assert.Equal("desktop", (string)sidecar["tier"]!);

        // (2) launcher stopped → the SAME render falls back to local and still succeeds.
        await launcherCts.CancelAsync();
        try
        {
            await launcherLoop;
        }
        catch (OperationCanceledException)
        {
        }

        var onLocal = Path.Combine(fixture.Root, "fallback-local.png");
        var localRender = await sessions.RenderFinalAsync(null, head, "cycles_cpu", 128, 16, onLocal, "wait", 0, ct);
        Assert.Equal("local", localRender.Tier);
        Assert.True(File.Exists(onLocal), "fallback render should still produce pixels locally");
    }

    [Fact(Timeout = 600_000)]
    public async Task FallsBackToLocalWhenTheTokenIsRejected()
    {
        if (!fixture.BlenderAvailable)
        {
            return;
        }

        var ct = CancellationToken.None;
        var head = await MakeCheckpointAsync(ct);

        var launcher = StartLauncher("the-right-token", out var port);
        using var launcherCts = new CancellationTokenSource();
        var launcherLoop = launcher.RunAsync(launcherCts.Token);

        // the host holds the WRONG token → handshake refused → bringup fails → local fallback.
        await using var sessions = RemoteSessions($"127.0.0.1:{port}", "the-wrong-token");
        var outPath = Path.Combine(fixture.Root, "auth-reject.png");
        var render = await sessions.RenderFinalAsync(null, head, "cycles_cpu", 128, 16, outPath, "wait", 0, ct);

        Assert.Equal("local", render.Tier);
        Assert.True(File.Exists(outPath));

        await launcherCts.CancelAsync();
        try
        {
            await launcherLoop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task FallsBackToLocalWhenBringupConnectTimesOut()
    {
        if (!fixture.BlenderAvailable)
        {
            return;
        }

        var ct = CancellationToken.None;
        var head = await MakeCheckpointAsync(ct);

        // A "black hole": the OS accepts the TCP connect (so the probe passes), but nothing
        // ever answers the handshake — the H1 regression case. Bringup must time out fast
        // (OperationCanceledException) and fall back to local, not stall or throw a raw 500.
        var blackHole = new TcpListener(IPAddress.Loopback, 0);
        blackHole.Start();
        try
        {
            var port = ((IPEndPoint)blackHole.LocalEndpoint).Port;
            await using var sessions = RemoteSessions(
                $"127.0.0.1:{port}", Token, connectTimeout: TimeSpan.FromMilliseconds(800));

            var outPath = Path.Combine(fixture.Root, "connect-timeout.png");
            var render = await sessions.RenderFinalAsync(null, head, "cycles_cpu", 128, 16, outPath, "wait", 0, ct);

            Assert.Equal("local", render.Tier);
            Assert.True(File.Exists(outPath));
        }
        finally
        {
            blackHole.Stop();
        }
    }

    private async Task<string> MakeCheckpointAsync(CancellationToken ct)
    {
        var session = await fixture.Sessions.OpenAsync("remote-test", ct);
        var exec = await fixture.Sessions.ExecAsync(
            session.Id, "import bpy\nbpy.ops.mesh.primitive_cube_add()", null, ct);
        await fixture.Sessions.CloseAsync(session.Id);
        return exec.Checkpoint;
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

    private SessionService RemoteSessions(string endpoint, string token, TimeSpan? connectTimeout = null)
    {
        var options = TestOptions.FromEnvironment() with
        {
            StoreDir = fixture.Options.StoreDir,
            RendersDir = fixture.Options.RendersDir,
            PoolFloor = 0,
            DesktopEndpoint = endpoint,
            DesktopToken = token,
            DesktopProbeTtl = TimeSpan.FromMilliseconds(200),
            DesktopConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
        };
        return new SessionService(
            options, fixture.Pool, fixture.Store, new GpuLease(),
            new DesktopTier(options, NullLogger<DesktopTier>.Instance), NullLogger<SessionService>.Instance);
    }
}
