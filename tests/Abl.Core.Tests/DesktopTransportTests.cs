using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Workers;
using Abl.Launcher;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// Feature 003 local-only-safe unit coverage (no Blender, no desktop): the WireCodec framing
/// over a real TCP pair (FR-001), the token handshake accept/reject (FR-003/SC-003), the
/// launcher bind guard (SC-003), and the availability probe's timeout→fallback (FR-004/SC-002).
/// </summary>
public class DesktopTransportTests
{
    [Fact]
    public async Task WireCodecRoundTripsOverTcp()
    {
        var (client, server) = await TcpPairAsync();
        using (client)
        using (server)
        {
            await using var cs = new NetworkStream(client, ownsSocket: false);
            await using var ss = new NetworkStream(server, ownsSocket: false);

            await WireCodec.WriteAsync(cs, new JsonObject { ["op"] = "render", ["res"] = 512 }, default);
            var read = await WireCodec.ReadAsync(ss, default);

            Assert.Equal("render", (string)read["op"]!);
            Assert.Equal(512, (int)read["res"]!);
        }
    }

    [Fact]
    public async Task HandshakeAcceptsMatchingTokenAndRejectsWrongOne()
    {
        // matching token
        var (client, server) = await TcpPairAsync();
        using (client)
        using (server)
        {
            await using var cs = new NetworkStream(client, ownsSocket: false);
            await using var ss = new NetworkStream(server, ownsSocket: false);
            var serverTask = DesktopHandshake.ServerAsync(ss, "s3cret", default);
            await DesktopHandshake.ClientAsync(cs, "s3cret", default); // no throw
            Assert.True(await serverTask);
        }

        // wrong token: client throws, server reports unauthorized
        var (client2, server2) = await TcpPairAsync();
        using (client2)
        using (server2)
        {
            await using var cs = new NetworkStream(client2, ownsSocket: false);
            await using var ss = new NetworkStream(server2, ownsSocket: false);
            var serverTask = DesktopHandshake.ServerAsync(ss, "s3cret", default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => DesktopHandshake.ClientAsync(cs, "wrong", default));
            Assert.False(await serverTask);
        }
    }

    [Theory]
    [InlineData("0.0.0.0:7812")]
    [InlineData(":::7812")]
    [InlineData("*:7812")]
    [InlineData("+:7812")]
    public void BindGuardRefusesWildcardInterfaces(string bind) =>
        Assert.Throws<ArgumentException>(() => LauncherOptions.ResolveBind(bind));

    [Fact]
    public void BindGuardAcceptsASpecificAddress()
    {
        var endpoint = LauncherOptions.ResolveBind("127.0.0.1:7812");
        Assert.Equal(IPAddress.Loopback, endpoint.Address);
        Assert.Equal(7812, endpoint.Port);
    }

    [Fact]
    public async Task ProbeIsFalseWhenUnconfigured()
    {
        var tier = new DesktopTier(new AblOptions(), NullLogger<DesktopTier>.Instance);
        Assert.False(tier.IsConfigured);
        Assert.False(await tier.ProbeAvailableAsync(default));
    }

    [Fact]
    public async Task ProbeIsFalseWhenNothingListens()
    {
        // Reserve then close a port so the connect is refused fast (timeout→fallback path).
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var options = new AblOptions
        {
            DesktopEndpoint = $"127.0.0.1:{deadPort}",
            DesktopToken = "t",
            DesktopProbeTimeout = TimeSpan.FromSeconds(1),
        };
        var tier = new DesktopTier(options, NullLogger<DesktopTier>.Instance);
        Assert.True(tier.IsConfigured);
        Assert.False(await tier.ProbeAvailableAsync(default));
    }

    [Fact]
    public async Task ProbeIsTrueWhenALauncherPortIsListening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = new AblOptions { DesktopEndpoint = $"127.0.0.1:{port}", DesktopToken = "t" };
            var tier = new DesktopTier(options, NullLogger<DesktopTier>.Instance);
            Assert.True(await tier.ProbeAvailableAsync(default));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 009 T004: the desktop handle's GPU capability comes from its own hello, through the
    /// same validator and parser as a local worker. A fake launcher authenticates, then
    /// answers hello with the given <c>gpu</c> object and warm. The older hello shape
    /// (<c>gpu.optix</c> without <c>gpu.backend</c>) is <c>worker_upgrade_required</c> with the
    /// existing upgrade hint, so a not-yet-redeployed desktop falls back with that reason.
    /// </summary>
    [Theory]
    [InlineData("""{"backend":"optix","devices":[{"name":"Test OptiX GPU","type":"OPTIX"}],"metalrt":null}""", "optix")]
    [InlineData("""{"backend":"metal","devices":[{"name":"Test Metal GPU","type":"METAL"}],"metalrt":"AUTO"}""", "metal")]
    [InlineData("""{"backend":"none","devices":[],"metalrt":null}""", "none")]
    [InlineData("""{"optix":true}""", "worker_upgrade_required")] // the older hello shape
    [InlineData("""{"optix":false}""", "worker_upgrade_required")]
    [InlineData("""{"backend":7,"devices":[]}""", "invalid")]
    public async Task DesktopHelloSetsGpu(string gpu, string expected)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var launcher = Task.Run(async () =>
            {
                using var socket = await listener.AcceptSocketAsync();
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                Assert.True(await DesktopHandshake.ServerAsync(stream, "t", default));
                while (true)
                {
                    JsonObject request;
                    try
                    {
                        request = await WireCodec.ReadAsync(stream, default);
                    }
                    catch (Exception ex) when (ex is EndOfStreamException or IOException)
                    {
                        return;
                    }

                    var reply = (string)request["op"]! switch
                    {
                        "hello" => new JsonObject
                        {
                            ["ok"] = true,
                            ["blender_version"] = "5.2.1",
                            ["python_version"] = "3.13.0",
                            ["gpu"] = JsonNode.Parse(gpu),
                        },
                        "warm" => new JsonObject { ["ok"] = true, ["warmed_ms"] = 1.0 },
                        _ => new JsonObject { ["ok"] = true },
                    };
                    reply["id"] = request["id"]!.GetValue<long>();
                    await WireCodec.WriteAsync(stream, reply, default);
                }
            });

            var options = new AblOptions
            {
                DesktopEndpoint = $"127.0.0.1:{port}",
                DesktopToken = "t",
                SpawnTimeout = TimeSpan.FromSeconds(10),
            };
            var tier = new DesktopTier(options, NullLogger<DesktopTier>.Instance);
            switch (expected)
            {
                case "worker_upgrade_required":
                    var upgrade = await Assert.ThrowsAsync<AblException>(() => tier.ConnectWorkerAsync(default));
                    Assert.Equal("worker_upgrade_required", upgrade.Kind);
                    Assert.Equal(WorkerTransfers.UpgradeHint, upgrade.Hint);
                    break;
                case "invalid":
                    await Assert.ThrowsAsync<WorkerDeadException>(() => tier.ConnectWorkerAsync(default));
                    break;
                default:
                    await using (var handle = await tier.ConnectWorkerAsync(default))
                    {
                        Assert.Equal(WorkerTier.Desktop, handle.Tier);
                        Assert.Equal(expected, handle.Gpu.Backend);
                        Assert.Equal(expected == "none" ? 0 : 1, handle.Gpu.Devices.Count);
                        Assert.Equal(expected == "metal" ? "AUTO" : null, handle.Gpu.MetalRt);
                    }

                    break;
            }

            await launcher.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(Socket Client, Socket Server)> TcpPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var acceptTask = listener.AcceptSocketAsync();
            var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var server = await acceptTask;
            return (client, server);
        }
        finally
        {
            listener.Stop();
        }
    }
}
