using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Abl.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// The expansion's full journey via MCP against a real spawned daemon (T026):
/// open → exec → delta → preview → checkpoint → branch → freeze → replay-verify →
/// final render (disk-only) → batch + contact sheet → close; plus the loopback bind
/// (FR-013) and end-to-end telemetry flow + hygiene (FR-012) through a local OTLP sink.
/// </summary>
[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class EndToEndTests
{
    private const string SecretMarker = "E2E-SECRET-MARKER";

    [Fact(Timeout = 600_000)]
    public async Task FullJourneyOverMcp()
    {
        var blender = TestOptions.FromEnvironment().BlenderPath;
        if (!File.Exists(blender))
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var ct = CancellationToken.None;
        var root = Path.Combine(Path.GetTempPath(), $"abl-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var port = FreePort();

        // A tiny OTLP sink: counts exports, keeps bodies for the hygiene sweep.
        var otlpPort = FreePort();
        using var otlp = new HttpListener();
        otlp.Prefixes.Add($"http://127.0.0.1:{otlpPort}/");
        otlp.Start();
        var otlpBodies = new List<byte[]>();
        var otlpPaths = new List<string>();
        var sinkLoop = Task.Run(async () =>
        {
            while (otlp.IsListening)
            {
                try
                {
                    var context = await otlp.GetContextAsync();
                    using var memory = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(memory);
                    lock (otlpBodies)
                    {
                        otlpBodies.Add(memory.ToArray());
                        otlpPaths.Add(context.Request.Url!.AbsolutePath);
                    }

                    context.Response.ContentType = "application/x-protobuf";
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
            }
        });

        var startInfo = AblCli.Daemon(root);
        startInfo.WorkingDirectory = Path.GetDirectoryName(AblCli.Dll)!;
        startInfo.Environment["ABL_PORT"] = port.ToString();
        startInfo.Environment["ABL_STORE"] = Path.Combine(root, "store");
        startInfo.Environment["ABL_RENDERS"] = Path.Combine(root, "renders");
        startInfo.Environment["ABL_CONFIG"] = Path.Combine(root, "config.json"); // absent: never the user's file
        startInfo.Environment["ABL_TELEMETRY"] = "1";
        startInfo.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://127.0.0.1:{otlpPort}";
        startInfo.Environment["OTEL_METRIC_EXPORT_INTERVAL"] = "2000";
        using var daemon = Process.Start(startInfo)!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitHealthyAsync(http, ct);

            // FR-013: loopback serves; a non-loopback interface on the same port refuses.
            foreach (var address in await Dns.GetHostAddressesAsync(Dns.GetHostName(), ct))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                {
                    continue;
                }

                using var probe = new TcpClient();
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await probe.ConnectAsync(address, port, cts.Token);
                });
                break;
            }

            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                }), cancellationToken: ct);

            // FR-009: the MCP tool list IS the canonical surface.
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            Assert.Equal(OpSurface.All.ToHashSet(), tools.Select(t => t.Name).ToHashSet());

            // FR-001: open a session on a pre-warmed worker.
            var open = await CallAsync(client, "session_open", new() { ["project"] = "e2e" }, ct);
            var session = open["id"]!.GetValue<string>();

            // FR-003 + FR-002: atomic exec returning a delta; params reach the script.
            var exec = await CallAsync(client, "scene_exec", new()
            {
                ["session"] = session,
                ["script"] = $"""
                    import bpy, bmesh
                    size = float(params["size"])  # marker: {SecretMarker}
                    mesh = bpy.data.meshes.new("Hero")
                    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=size); bm.to_mesh(mesh); bm.free()
                    bpy.context.scene.collection.objects.link(bpy.data.objects.new("Hero", mesh))
                    bpy.context.scene.eevee.taa_render_samples = 1  # one sample: the assertions check outputs, not quality
                    """,
                ["parameters"] = new Dictionary<string, object?> { ["size"] = 2 },
            }, ct);
            Assert.Contains("+ Hero", exec["delta"]!.GetValue<string>());
            var checkpoint = exec["checkpoint"]!.GetValue<string>();

            // FR-002: a second read is delta — nothing changed.
            var state = await CallTextAsync(client, "scene_state", new() { ["session"] = session }, ct);
            Assert.Equal("(no changes)", state);

            // FR-004: ONE preview image, inline, bounded size.
            var preview = await client.CallToolAsync(
                "scene_preview",
                new Dictionary<string, object?> { ["session"] = session, ["size"] = 256 },
                cancellationToken: ct);
            Assert.False(preview.IsError == true);
            var image = Assert.Single(preview.Content!.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);

            // FR-006: tag the head, branch a second session from it.
            await CallTextAsync(client, "checkpoint_save",
                new() { ["session"] = session, ["tags"] = new[] { "hero-v1" } }, ct);
            var branch = await CallAsync(client, "checkpoint_branch", new() { ["checkpoint"] = "hero-v1" }, ct);
            var branched = branch["id"]!.GetValue<string>();
            Assert.NotEqual(session, branched);
            var branchedState = await CallTextAsync(client, "scene_state",
                new() { ["session"] = branched, ["mode"] = "full" }, ct);
            Assert.Contains("Hero", branchedState);

            // FR-007: freeze the trajectory, replay-verify on a fresh worker.
            await CallAsync(client, "trajectory_freeze", new() { ["session"] = session, ["name"] = "e2e-hero" }, ct);
            var replay = await CallAsync(client, "replay_run", new() { ["artifact"] = "e2e-hero" }, ct);
            Assert.True(replay["pass"]!.GetValue<bool>(), replay.ToJsonString());

            // FR-005: final render — path + metadata only, pixels never inline.
            var render = await client.CallToolAsync(
                "render_final",
                new Dictionary<string, object?>
                {
                    ["session"] = session, ["engine"] = "eevee", ["res"] = 256,
                },
                cancellationToken: ct);
            Assert.False(render.IsError == true);
            Assert.Empty(render.Content!.OfType<ImageContentBlock>());
            var renderStructured = ParseResult(render);
            var renderPath = renderStructured["path"]!.GetValue<string>();
            Assert.True(new FileInfo(renderPath).Length > 0);
            Assert.True(File.Exists(renderPath + ".json"), "provenance sidecar missing");

            // FR-008: batch — renders on disk, ONE contact sheet inline.
            var batch = await client.CallToolAsync(
                "batch_run",
                new Dictionary<string, object?>
                {
                    ["artifact"] = "e2e-hero",
                    ["paramTable"] = new[]
                    {
                        new Dictionary<string, object?> { ["size"] = 1.0 },
                        new Dictionary<string, object?> { ["size"] = 1.5 },
                        new Dictionary<string, object?> { ["size"] = 2.0 },
                        new Dictionary<string, object?> { ["size"] = 2.5 },
                    },
                    ["res"] = 96,
                    ["columns"] = 2,
                },
                cancellationToken: ct);
            Assert.False(batch.IsError == true);
            Assert.Single(batch.Content!.OfType<ImageContentBlock>());

            await CallTextAsync(client, "session_close", new() { ["session"] = branched }, ct);
            await CallTextAsync(client, "session_close", new() { ["session"] = session }, ct);

            // FR-012: telemetry flowed to the collector and carries no payload contents.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (otlpBodies)
                {
                    if (otlpPaths.Contains("/v1/traces") && otlpPaths.Contains("/v1/metrics"))
                    {
                        break;
                    }
                }

                await Task.Delay(500, ct);
            }

            lock (otlpBodies)
            {
                Assert.Contains("/v1/traces", otlpPaths);
                Assert.Contains("/v1/metrics", otlpPaths);
                Assert.DoesNotContain(otlpBodies, body =>
                    System.Text.Encoding.Latin1.GetString(body).Contains(SecretMarker));
            }
        }
        finally
        {
            try
            {
                daemon.Kill(entireProcessTree: true);
                daemon.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }

            otlp.Stop();
            await Task.WhenAny(sinkLoop, Task.Delay(2000));
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task DaemonRefusesNonLoopbackBindOverride()
    {
        // FR-013 AC-1: no configuration may widen the bind. Kestrel endpoint config
        // (Kestrel__Endpoints__*) silently OVERRIDES UseUrls, so the daemon asserts the
        // bound addresses at startup and must exit instead of serving 0.0.0.0.
        var root = Path.Combine(Path.GetTempPath(), $"abl-bind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var port = FreePort();
        using var daemon = StartDaemon(FreePort(), root, new Dictionary<string, string>
        {
            ["Kestrel__Endpoints__Http__Url"] = $"http://0.0.0.0:{port}",
        });
        try
        {
            var exited = daemon.WaitForExit(60_000);
            if (!exited)
            {
                daemon.Kill(entireProcessTree: true);
            }

            Assert.True(exited, "daemon kept serving despite a non-loopback bind override");
            Assert.Equal(1, daemon.ExitCode);
        }
        finally
        {
            TryDelete(root);
        }

        await Task.CompletedTask;
    }

    [Fact(Timeout = 300_000)]
    public async Task RestJourneyMatchesTheContract()
    {
        var blender = TestOptions.FromEnvironment().BlenderPath;
        if (!File.Exists(blender))
        {
            return; // no Blender on this host; the Blender-integration CI lane always sets $ABL_BLENDER
        }

        var root = Path.Combine(Path.GetTempPath(), $"abl-rest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var port = FreePort();
        using var daemon = StartDaemon(port, root);
        try
        {
            var ct = CancellationToken.None;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitHealthyAsync(http, ct);

            // Unknown ops get the structured 404 (and never become telemetry labels).
            var unknown = await PostOpAsync(http, "definitely_not_an_op", []);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            var unknownBody = System.Text.Json.Nodes.JsonNode
                .Parse(await unknown.Content.ReadAsStringAsync(ct))!.AsObject();
            Assert.Equal("not_found", unknownBody["kind"]!.GetValue<string>());

            // FR-009: project is required on REST exactly as it is on MCP.
            var missing = await PostOpAsync(http, "session_open", []);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

            var open = await PostOkAsync(http, "session_open",
                new System.Text.Json.Nodes.JsonObject { ["project"] = "rest-e2e" });
            var session = open["id"]!.GetValue<string>();

            var exec = await PostOkAsync(http, "scene_exec", new System.Text.Json.Nodes.JsonObject
            {
                ["session"] = session,
                ["script"] = """
                    import bpy, bmesh
                    mesh = bpy.data.meshes.new("RestHero")
                    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0); bm.to_mesh(mesh); bm.free()
                    bpy.context.scene.collection.objects.link(bpy.data.objects.new("RestHero", mesh))
                    """,
            });
            Assert.Contains("+ RestHero", exec["delta"]!.GetValue<string>());

            var state = await PostOkAsync(http, "scene_state",
                new System.Text.Json.Nodes.JsonObject { ["session"] = session });
            Assert.Equal("(no changes)", state["text"]!.GetValue<string>());

            // Rows carry createdAt — the CLI renders the same age: line the MCP tool does.
            var list = await PostOkAsync(http, "checkpoint_list", []);
            var items = list["items"]!.AsArray();
            Assert.True(items.Count >= 2, list.ToJsonString());
            Assert.All(items, row => Assert.NotNull(row!["createdAt"]));

            // Prune returns the refused HASHES (same shape as MCP); the live head is refused.
            var prune = await PostOkAsync(http, "checkpoint_prune", []);
            Assert.NotNull(prune["refused"]);

            var closed = await PostOkAsync(http, "session_close",
                new System.Text.Json.Nodes.JsonObject { ["session"] = session });
            Assert.Equal(session, closed["closed"]!.GetValue<string>());
        }
        finally
        {
            try
            {
                daemon.Kill(entireProcessTree: true);
                daemon.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }

            TryDelete(root);
        }
    }

    private static Process StartDaemon(
        int port, string root, IDictionary<string, string>? extraEnv = null)
    {
        var startInfo = AblCli.Daemon(root);
        startInfo.WorkingDirectory = Path.GetDirectoryName(AblCli.Dll)!;
        startInfo.Environment["ABL_PORT"] = port.ToString();
        startInfo.Environment["ABL_STORE"] = Path.Combine(root, "store");
        startInfo.Environment["ABL_RENDERS"] = Path.Combine(root, "renders");
        startInfo.Environment["ABL_CONFIG"] = Path.Combine(root, "config.json"); // absent: never the user's file
        startInfo.Environment.Remove("ABL_TELEMETRY"); // never inherit live enablement in off tests
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
            {
                startInfo.Environment[key] = value;
            }
        }

        var daemon = Process.Start(startInfo)!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        return daemon;
    }

    [Fact(Timeout = 120_000)]
    public async Task TelemetryUnsetExportsNothingEvenWithCollectorEndpointsConfigured()
    {
        if (!File.Exists(TestOptions.FromEnvironment().BlenderPath)) return;
        var root = Path.Combine(Path.GetTempPath(), $"abl-off-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sinkPort = FreePort();
        using var sink = new HttpListener();
        sink.Prefixes.Add($"http://127.0.0.1:{sinkPort}/");
        sink.Start();
        var exports = 0;
        var loop = Task.Run(async () =>
        {
            try
            {
                while (sink.IsListening)
                {
                    var request = await sink.GetContextAsync();
                    Interlocked.Increment(ref exports);
                    request.Response.StatusCode = 200;
                    request.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        });
        var port = FreePort();
        using var daemon = StartDaemon(port, root, new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://127.0.0.1:{sinkPort}",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = $"http://127.0.0.1:{sinkPort}/v1/traces",
            ["OTEL_METRIC_EXPORT_INTERVAL"] = "200",
        });
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitHealthyAsync(http, default);
            var open = await PostOkAsync(http, "session_open", new System.Text.Json.Nodes.JsonObject { ["project"] = "telemetry-off" });
            await PostOkAsync(http, "session_close", new System.Text.Json.Nodes.JsonObject { ["session"] = open["id"]!.DeepClone() });
            await Task.Delay(1500);
            Assert.Equal(0, Volatile.Read(ref exports));
        }
        finally
        {
            daemon.Kill(entireProcessTree: true);
            daemon.WaitForExit(5000);
            sink.Stop();
            await loop;
            TryDelete(root);
        }
    }

    private static async Task<HttpResponseMessage> PostOpAsync(
        HttpClient http, string op, System.Text.Json.Nodes.JsonObject body) =>
        await http.PostAsync(
            $"/api/ops/{op}",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

    private static async Task<System.Text.Json.Nodes.JsonObject> PostOkAsync(
        HttpClient http, string op, System.Text.Json.Nodes.JsonObject body)
    {
        var response = await PostOpAsync(http, op, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{op}: {text}");
        return System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<System.Text.Json.Nodes.JsonObject> CallAsync(
        McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.False(result.IsError == true,
            $"{tool}: {string.Join(" ", result.Content!.OfType<TextContentBlock>().Select(t => t.Text))}");
        return ParseResult(result);
    }

    /// <summary>Structured payload of a tool result — structuredContent, else the JSON text block.</summary>
    private static System.Text.Json.Nodes.JsonObject ParseResult(CallToolResult result)
    {
        var raw = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : result.Content!.OfType<TextContentBlock>().First().Text;
        return System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
    }

    private static async Task<string> CallTextAsync(
        McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.False(result.IsError == true,
            $"{tool}: {string.Join(" ", result.Content!.OfType<TextContentBlock>().Select(t => t.Text))}");
        return string.Join("\n", result.Content!.OfType<TextContentBlock>().Select(t => t.Text));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitHealthyAsync(HttpClient http, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                var response = await http.GetAsync("/healthz", ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("daemon never became healthy");
            }

            await Task.Delay(250, ct);
        }
    }
}
