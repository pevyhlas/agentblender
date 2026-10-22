using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Cli;
using Abl.Core.Config;
using Abl.Core.Delivery;
using Abl.Core.Lifecycle;
using Abl.Core.Rendering;
using Abl.Core.Sessions;
using Abl.Core.Store;
using Abl.Core.Trajectories;
using Abl.Core.Workers;
using Abl.Daemon.Api;
using Abl.Daemon.Tools;
using Abl.Launcher;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>One frame a fake worker received: which worker, which op, and the render engine.</summary>
public sealed record FakeFrame(string Worker, string Op, string? Engine)
{
    /// <summary>The request as received (009 T022 reads the <c>gpu.render</c> resolution, samples and output path).</summary>
    public JsonObject? Request { get; init; }
}

/// <summary>
/// What a scripted fake worker answers (009 plan D10): the hello <c>gpu</c> object, and
/// whether a <c>cycles_gpu</c> render fails the worker's device check
/// (<c>gpu_unavailable</c>, as <c>cycles_devices.check</c> raises it).
/// </summary>
public sealed class FakeWorkerScript
{
    public JsonObject Gpu { get; set; } = FakeGpu.None();

    public bool GpuCheckFails { get; set; }

    /// <summary>When set, every <c>render</c> fails with this worker error kind (e.g. a Python exception name).</summary>
    public string? RenderErrorKind { get; set; }

    /// <summary>
    /// Runs while the worker holds a <c>render</c> frame, before it answers, so a test can
    /// observe host state (e.g. the GPU lease) during that render.
    /// </summary>
    public Func<FakeFrame, Task>? OnRender { get; set; }

    /// <summary>The <c>duration_ms</c> successive renders report (1 ms once it is empty): a slow first, fast second compile.</summary>
    public Queue<double> RenderDurationsMs { get; } = new();
}

public static class FakeGpu
{
    public const string OptixName = "Test OptiX GPU";
    public const string MetalName = "Test Metal GPU";

    public static JsonObject None() => new() { ["backend"] = "none", ["devices"] = new JsonArray(), ["metalrt"] = null };

    public static JsonObject Optix() => new()
    {
        ["backend"] = "optix",
        ["devices"] = new JsonArray(new JsonObject { ["name"] = OptixName, ["type"] = "OPTIX" }),
        ["metalrt"] = null,
    };

    public static JsonObject Metal() => new()
    {
        ["backend"] = "metal",
        ["devices"] = new JsonArray(new JsonObject { ["name"] = MetalName, ["type"] = "METAL" }),
        ["metalrt"] = "AUTO",
    };

    /// <summary>The older hello shape a not-yet-redeployed desktop worker sends.</summary>
    public static JsonObject Legacy() => new() { ["optix"] = true };
}

/// <summary>
/// The worker protocol over <see cref="WireCodec"/>, answered from a <see cref="FakeWorkerScript"/>
/// (no Blender). Records every frame in a shared log, so tests count <c>render</c> frames
/// per worker and engine.
/// </summary>
internal static class FakeWorkerProtocol
{
    private const string SceneDigest = "5ce4e0000000000000000000000000000000000000000000000000000000d16e";
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static int _saves;

    public static async Task ServeAsync(Stream stream, string worker, FakeWorkerScript script, ConcurrentQueue<FakeFrame> log)
    {
        while (true)
        {
            JsonObject request;
            try
            {
                request = await WireCodec.ReadAsync(stream, default);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
            {
                return;
            }

            var op = request["op"]!.GetValue<string>();
            var frame = new FakeFrame(worker, op, request["engine"]?.GetValue<string>()) { Request = (JsonObject)request.DeepClone() };
            log.Enqueue(frame);
            if (op == "render" && script.OnRender is { } onRender) await onRender(frame);
            var reply = Answer(op, request, script);
            reply["id"] = request["id"]!.GetValue<long>();
            try
            {
                await WireCodec.WriteAsync(stream, reply, default);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return;
            }

            if (op == "quit") return;
        }
    }

    private static JsonObject Answer(string op, JsonObject request, FakeWorkerScript script)
    {
        switch (op)
        {
            case "hello":
                return new JsonObject
                {
                    ["ok"] = true,
                    ["blender_version"] = "5.2.1",
                    ["blender_build_hash"] = "0123456789ab",
                    ["worker_source_sha256"] = new string('a', 64),
                    ["python_version"] = "3.13.0",
                    ["gpu"] = script.Gpu.DeepClone(),
                };
            case "warm":
                return new JsonObject { ["ok"] = true, ["warmed_ms"] = 1.0 };
            case "save":
                var blend = System.Text.Encoding.ASCII.GetBytes($"BLEND-{Interlocked.Increment(ref _saves)}");
                File.WriteAllBytes(request["path"]!.GetValue<string>(), blend);
                return new JsonObject
                {
                    ["ok"] = true, ["bytes"] = blend.Length, ["digest"] = SceneDigest, ["blender_version"] = "5.2.1",
                };
            case "exec":
                return new JsonObject
                {
                    ["ok"] = true,
                    ["duration_ms"] = 1.0,
                    ["state_delta"] = new JsonObject { ["added"] = new JsonArray(), ["removed"] = new JsonArray(), ["changed"] = new JsonArray() },
                };
            case "load_bytes":
                return request["keep"]?.GetValue<bool>() == true
                    ? new JsonObject { ["ok"] = true, ["path"] = "C:/desktop/base.blend" }
                    : new JsonObject { ["ok"] = true };
            case "render":
                var engine = request["engine"]!.GetValue<string>();
                if (script.RenderErrorKind is { } renderError)
                {
                    return Error(renderError, "fake worker: render failed");
                }

                if (engine == "cycles_gpu" && script.GpuCheckFails)
                {
                    return Error("gpu_unavailable", "cycles_gpu: no enabled OPTIX device (has_active_device=false)");
                }

                var rendered = request["profile"]?.GetValue<bool>() == true ? ProfileRender(request) : Pixels(request);
                rendered["duration_ms"] = script.RenderDurationsMs.TryDequeue(out var ms) ? ms : 1.0;
                if (engine == "cycles_cpu") rendered["device"] = new JsonObject { ["backend"] = "cpu", ["name"] = null };
                if (engine == "cycles_gpu")
                {
                    rendered["device"] = new JsonObject
                    {
                        ["backend"] = script.Gpu["backend"]!.DeepClone(),
                        ["name"] = script.Gpu["devices"]![0]!["name"]!.DeepClone(),
                    };
                }

                return rendered;
            case "contact_sheet":
                return Pixels(request);
            case "load":
            case "quit":
                return new JsonObject { ["ok"] = true };
            default:
                return Error("bad_request", $"fake worker: no op {op}");
        }
    }

    /// <summary>A local worker writes the file; a remote one ships the bytes back inline.</summary>
    private static JsonObject Pixels(JsonObject request)
    {
        var outPath = request["out"]!.GetValue<string>();
        if (request["return_bytes"]?.GetValue<bool>() == true)
        {
            return new JsonObject { ["ok"] = true, ["out"] = "C:/desktop/out.png", ["png_b64"] = Convert.ToBase64String(Png) };
        }

        File.WriteAllBytes(outPath, Png);
        return new JsonObject { ["ok"] = true, ["out"] = outPath };
    }

    /// <summary>
    /// A delivery (<c>profile</c>) render: a structurally valid res x res PNG and the
    /// effective settings the host's delivery profile check requires.
    /// </summary>
    private static JsonObject ProfileRender(JsonObject request)
    {
        var res = request["res"]!.GetValue<int>();
        var outPath = request["out"]!.GetValue<string>();
        File.WriteAllBytes(outPath, PngProfileTests.Png(res, res));
        return new JsonObject
        {
            ["ok"] = true,
            ["out"] = outPath,
            ["settings"] = new JsonObject
            {
                ["format"] = "PNG", ["mode"] = "RGBA", ["depth"] = "8", ["display"] = "sRGB", ["view"] = "Standard",
                ["look"] = "None", ["colorManagement"] = "FOLLOW_SCENE", ["transparent"] = true, ["border"] = false,
                ["crop"] = false, ["width"] = res, ["height"] = res, ["percentage"] = 100, ["exposure"] = 0.0,
                ["gamma"] = 1.0, ["dither"] = 0.0, ["pixelAspect"] = new JsonArray(1.0, 1.0),
            },
        };
    }

    /// <summary>
    /// A local worker as <see cref="WorkerSupervisor"/> hands it over (hello validated, then a
    /// warm), served by this protocol over a loopback TCP pair. The server task ends when the
    /// handle is disposed.
    /// </summary>
    public static async Task<(WorkerHandle Handle, Task Server)> ConnectAsync(
        string worker, FakeWorkerScript script, ConcurrentQueue<FakeFrame> log, bool prewarm, CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = listener.AcceptAsync(ct);
        await client.ConnectAsync(listener.LocalEndPoint!, ct);
        var server = await accept;
        var serving = Task.Run(async () =>
        {
            using (server)
            {
                await using var stream = new NetworkStream(server, ownsSocket: false);
                await ServeAsync(stream, worker, script, log);
            }
        }, CancellationToken.None);

        var handle = new WorkerHandle(worker, new WorkerConnection(client), WorkerTier.Local);
        handle.ApplyHello(await handle.RequestAsync("hello", null, TimeSpan.FromSeconds(10), ct));
        await handle.RequestAsync("warm", new JsonObject { ["prewarm_render"] = prewarm }, TimeSpan.FromSeconds(10), ct);
        handle.State = WorkerState.Ready;
        return (handle, serving);
    }

    private static JsonObject Error(string kind, string message) =>
        new() { ["ok"] = false, ["error"] = new JsonObject { ["kind"] = kind, ["message"] = message } };
}

/// <summary>
/// The daemon's op core over scripted fake workers (009 T008): local workers come from a
/// <see cref="WorkerPool"/> spawner over a loopback TCP pair and the internal remote
/// <see cref="WorkerHandle"/> constructor; the optional desktop is a fake launcher behind
/// the real token handshake. The host platform is injected. Every surface is reachable:
/// MCP (<see cref="AblTools"/>), REST (<see cref="ApiEndpoints.DispatchAsync"/>) and the
/// CLI's text lines (<see cref="CliText"/> over the REST payload).
/// </summary>
public sealed class GpuHarness : IAsyncDisposable
{
    private readonly TcpListener? _launcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _launcherLoop;
    private readonly List<Task> _servers = [];
    private int _workers;

    public GpuHarness(HostPlatform platform, JsonObject? desktopGpu = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "abl-gpu-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        string? endpoint = null;
        if (desktopGpu is not null)
        {
            DesktopScript.Gpu = desktopGpu;
            _launcher = new TcpListener(IPAddress.Loopback, 0);
            _launcher.Start();
            endpoint = $"127.0.0.1:{((IPEndPoint)_launcher.LocalEndpoint).Port}";
            _launcherLoop = Task.Run(LauncherLoopAsync);
        }

        Options = new AblOptions
        {
            Platform = platform,
            StoreDir = Path.Combine(Root, "store"),
            RendersDir = Path.Combine(Root, "renders"),
            RuntimeDir = Path.Combine(Root, "run"),
            PoolFloor = 0,
            SpawnTimeout = TimeSpan.FromSeconds(10),
            ExecTimeout = TimeSpan.FromSeconds(10),
            RenderTimeout = TimeSpan.FromSeconds(10),
            GpuLeaseWait = TimeSpan.FromMilliseconds(200),
            DesktopEndpoint = endpoint,
            DesktopToken = endpoint is null ? null : "fake-token",
        };
        Store = new CheckpointStore(Options.StoreDir);
        Desktop = new DesktopTier(Options, NullLogger<DesktopTier>.Instance);
        Pool = new WorkerPool(Options, new WorkerSupervisor(Options, NullLogger<WorkerSupervisor>.Instance), NullLogger<WorkerPool>.Instance)
        {
            Spawner = SpawnLocalAsync,
        };
        Sessions = new SessionService(Options, Pool, Store, Lease, Desktop, NullLogger<SessionService>.Instance);
        Trajectories = new TrajectoryService(Options, Pool, Store, Lease, Desktop, NullLogger<TrajectoryService>.Instance);
        Deliveries = new DeliveryService(Options, Pool, Store, Lease, NullLogger<DeliveryService>.Instance);
        // daemon_status (008 T036) over a hermetic configuration: a token in the environment
        // proves no surface prints it (AC-27).
        Config = AblConfig.Resolve(new ConfigInputs
        {
            Env = new Dictionary<string, string> { ["ABL_DESKTOP_TOKEN"] = TokenValue, ["ABL_STORE"] = Options.StoreDir },
            Home = Root,
            FileSystem = new FakeConfigFileSystem(),
            Platform = platform,
        });
        Instance = new DaemonInstance(4242, DateTimeOffset.Parse("2026-10-21T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture), Root, null, 7811, Config.EffectiveDigest, Detached: false);
        Status = new DaemonStatus(Instance, Config, Pool, Sessions, Desktop);
        Tools = new AblTools(Sessions, Trajectories, Deliveries, Status);
    }

    public string Root { get; }
    public AblOptions Options { get; }
    public CheckpointStore Store { get; }
    public GpuLease Lease { get; } = new();
    public DesktopTier Desktop { get; }
    public WorkerPool Pool { get; }
    public SessionService Sessions { get; }
    public TrajectoryService Trajectories { get; }
    public DeliveryService Deliveries { get; }
    public AblTools Tools { get; }

    /// <summary>A desktop token value no surface may print.</summary>
    public const string TokenValue = "tok-never-printed-0123";

    public AblConfig Config { get; }
    public DaemonInstance Instance { get; }
    public DaemonStatus Status { get; }

    /// <summary>What every local worker spawned from now on answers.</summary>
    public FakeWorkerScript LocalScript { get; } = new();

    /// <summary>What the desktop worker answers.</summary>
    public FakeWorkerScript DesktopScript { get; } = new();

    public ConcurrentQueue<FakeFrame> Frames { get; } = new();

    public int RenderFrames(string? engine = null, bool desktop = false) =>
        Frames.Count(f => f.Op == "render" && (engine is null || f.Engine == engine) && f.Worker.StartsWith(desktop ? "desktop" : "local", StringComparison.Ordinal));

    public int FrameCount(string op) => Frames.Count(f => f.Op == op);

    /// <summary>Open a session and freeze a one-op artifact named <paramref name="name"/> from it.</summary>
    public async Task<SessionInfo> FreezeAsync(string name)
    {
        var session = await Sessions.OpenAsync("gpu", default);
        await Sessions.ExecAsync(session.Id, "import bpy\nbpy.context.scene['v'] = params.get('v', 0)", null, default);
        Trajectories.Freeze(session.Id, name, null, null);
        return session;
    }

    // --- surfaces ---

    /// <summary>MCP: the tool's error text (<c>kind: message</c>, then <c>hint: …</c>), parsed.</summary>
    public static async Task<(string Kind, string? Hint)> McpErrorAsync(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<McpException>(call);
        var lines = error.Message.Split('\n');
        return (lines[0][..lines[0].IndexOf(':')],
            lines.FirstOrDefault(l => l.StartsWith("hint: ", StringComparison.Ordinal))?["hint: ".Length..]);
    }

    /// <summary>REST: status and parsed body of one <c>POST /api/ops/{op}</c>.</summary>
    public async Task<(int Status, JsonObject Body)> RestAsync(string op, JsonObject body)
    {
        using var document = JsonDocument.Parse(body.ToJsonString());
        var (status, json) = await ApiEndpoints.DispatchAsync(op, document.RootElement.Clone(), Sessions, Trajectories, Deliveries, Status, default);
        return (status, JsonNode.Parse(json)!.AsObject());
    }

    /// <summary>CLI: the stderr lines <c>abl</c> prints for a REST error body, parsed back to kind and hint.</summary>
    public static (string Kind, string? Hint) CliError(JsonObject restBody)
    {
        var lines = CliText.ErrorLines(restBody).ToList();
        var head = lines[0]["abl: ".Length..];
        return (head[..head.IndexOf(':')],
            lines.FirstOrDefault(l => l.StartsWith("  hint: ", StringComparison.Ordinal))?["  hint: ".Length..]);
    }

    public static string McpJson<T>(T result) => JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);

    // --- workers ---

    private async Task<WorkerHandle> SpawnLocalAsync(CancellationToken ct)
    {
        var id = $"local-{Interlocked.Increment(ref _workers)}";
        var (client, server) = await TcpPairAsync();
        lock (_servers)
        {
            _servers.Add(Task.Run(async () =>
            {
                using (server)
                {
                    await using var stream = new NetworkStream(server, ownsSocket: false);
                    await FakeWorkerProtocol.ServeAsync(stream, id, LocalScript, Frames);
                }
            }));
        }

        // As WorkerSupervisor does after the rendezvous: hello (validated), then warm.
        var handle = new WorkerHandle(id, new WorkerConnection(client), WorkerTier.Local);
        var hello = await handle.RequestAsync("hello", null, Options.SpawnTimeout, ct);
        handle.ApplyHello(hello);
        await handle.RequestAsync("warm", new JsonObject { ["prewarm_render"] = true }, Options.SpawnTimeout, ct);
        handle.State = WorkerState.Ready;
        return handle;
    }

    private async Task LauncherLoopAsync()
    {
        var connections = 0;
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _launcher!.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            var id = $"desktop-{Interlocked.Increment(ref connections)}";
            lock (_servers)
            {
                _servers.Add(Task.Run(async () =>
                {
                    using (socket)
                    {
                        await using var stream = new NetworkStream(socket, ownsSocket: false);
                        bool authenticated;
                        try
                        {
                            authenticated = await DesktopHandshake.ServerAsync(stream, "fake-token", default);
                        }
                        catch (Exception ex) when (ex is EndOfStreamException or IOException)
                        {
                            return; // the availability probe connects and closes
                        }

                        if (authenticated) await FakeWorkerProtocol.ServeAsync(stream, id, DesktopScript, Frames);
                    }
                }));
            }
        }
    }

    private static async Task<(Socket Client, Socket Server)> TcpPairAsync()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = listener.AcceptAsync();
        await client.ConnectAsync(listener.LocalEndPoint!);
        return (client, await accept);
    }

    public async ValueTask DisposeAsync()
    {
        await Sessions.DisposeAsync();
        await Pool.DisposeAsync();
        await _stop.CancelAsync();
        _launcher?.Stop();
        if (_launcherLoop is not null) await _launcherLoop;
        Task[] servers;
        lock (_servers) servers = [.. _servers];
        await Task.WhenAll(servers).WaitAsync(TimeSpan.FromSeconds(10));
        _stop.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
