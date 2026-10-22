using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// FR-001 (008 phase 0), AC-01..03 over a real Kestrel socket: the built daemon on a random
/// loopback port, with a temp store and a deliberately missing Blender (no worker ever
/// starts, so this runs in the unit lane). Every route — REST /api/ops, MCP streamable
/// HTTP POST, GET (SSE) and DELETE, and /healthz — is rejected for a foreign Host (421)
/// or a foreign Origin (403), and served for loopback callers without Origin.
/// </summary>
public sealed class HostGuardSocketTests(HostGuardSocketTests.Daemon daemon) : IClassFixture<HostGuardSocketTests.Daemon>
{
    public static TheoryData<string> ForeignHosts => new()
    {
        "evil.example:{port}", "0.0.0.0:{port}", "127.0.0.1.evil.example:{port}", "localhost.:{port}",
    };

    public static TheoryData<string> ForeignOrigins => new()
    {
        "https://evil.example", "null", "http://localhost:3000", "https://127.0.0.1:{port}", "http://evil.example:{port}",
    };

    [Theory]
    [MemberData(nameof(ForeignHosts))]
    public async Task ForeignHostIs421OnEveryRoute(string host)
    {
        foreach (var route in Routes)
        {
            using var response = await daemon.SendAsync(route(), host: host.Replace("{port}", daemon.Port.ToString()));
            Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        }
        Assert.Equal(0, await daemon.SessionCountAsync());
    }

    [Theory]
    [MemberData(nameof(ForeignOrigins))]
    public async Task ForeignOriginIs403OnEveryRoute(string origin)
    {
        foreach (var route in Routes)
        {
            using var response = await daemon.SendAsync(route(), origin: origin.Replace("{port}", daemon.Port.ToString()));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(0, await daemon.SessionCountAsync());
    }

    [Theory]
    [InlineData("127.0.0.1:{port}")]
    [InlineData("localhost:{port}")]
    [InlineData("[::1]:{port}")]
    [InlineData("127.0.0.1:9000")] // a port-remapping tunnel still reaches the daemon
    public async Task LoopbackHostIsServed(string host)
    {
        using var response = await daemon.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/healthz"), host: host.Replace("{port}", daemon.Port.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SameLoopbackOriginIsServed()
    {
        using var health = await daemon.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/healthz"), origin: $"http://127.0.0.1:{daemon.Port}");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var ops = await daemon.SendAsync(Op("session_list"), origin: $"http://localhost:{daemon.Port}");
        Assert.Equal(HttpStatusCode.OK, ops.StatusCode);
    }

    [Fact]
    public async Task CliStyleRestAndMcpSdkClientWithoutOriginAreUnchanged()
    {
        using var ops = await daemon.SendAsync(Op("session_list"));
        Assert.Equal(HttpStatusCode.OK, ops.StatusCode);
        Assert.NotNull(JsonNode.Parse(await ops.Content.ReadAsStringAsync())!["items"]);

        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri($"http://127.0.0.1:{daemon.Port}/mcp"),
        }));
        var tools = await mcp.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "session_open");
    }

    [Fact]
    public async Task RawSocketCurlStyleForeignHostIs421()
    {
        // Byte-exact equivalent of: curl -X POST -H 'Host: evil.example:7811' .../api/ops/session_open
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, daemon.Port);
        var stream = tcp.GetStream();
        const string body = """{"project":"rebind"}""";
        var request = "POST /api/ops/session_open HTTP/1.1\r\nHost: evil.example:7811\r\n" +
            $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync();
        Assert.StartsWith("HTTP/1.1 421", status);
        Assert.Equal(0, await daemon.SessionCountAsync());
    }

    private static readonly Func<HttpRequestMessage>[] Routes =
    [
        () => Op("session_open", """{"project":"rebind"}"""),
        () => Op("scene_exec", """{"session":"x","script":"print(1)"}"""),
        () => Mcp(HttpMethod.Post, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"rebind","version":"0"}}}"""),
        () => Mcp(HttpMethod.Get, null),
        () => Mcp(HttpMethod.Delete, null),
        () => new HttpRequestMessage(HttpMethod.Get, "/healthz"),
        () => new HttpRequestMessage(HttpMethod.Get, "/no-such-route"),
    ];

    private static HttpRequestMessage Op(string op, string body = "{}") =>
        new(HttpMethod.Post, "/api/ops/" + op) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpRequestMessage Mcp(HttpMethod method, string? body)
    {
        var request = new HttpRequestMessage(method, "/mcp");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Mcp-Session-Id", "rebind-session");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>The built daemon, isolated: random port, temp store, no Blender, no telemetry.</summary>
    public sealed class Daemon : IAsyncLifetime
    {
        private Process? _process;
        private string _root = "";
        private HttpClient _http = null!;
        public int Port { get; private set; }

        public async Task InitializeAsync()
        {
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
            }
            _root = Path.Combine(Path.GetTempPath(), "abl-hostguard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _process = Process.Start(Isolated(AblCli.Daemon(_root)))!;
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(30) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                Assert.False(_process.HasExited, "daemon exited during startup");
                try { if ((await _http.GetAsync("/healthz", deadline.Token)).IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
        }

        /// <summary>The fixture's root (store, renders, state dir).</summary>
        public string Root => _root;

        /// <summary>
        /// <c>abl &lt;args&gt;</c> with the daemon's own environment, so the CLI resolves the same
        /// effective configuration (008 AC-28: no drift unless a test adds a variable).
        /// </summary>
        public ProcessStartInfo Cli(params string[] args)
        {
            var start = Isolated(AblCli.Start(args));
            start.Environment["XDG_STATE_HOME"] = Path.Combine(_root, "state");
            return start;
        }

        private ProcessStartInfo Isolated(ProcessStartInfo start)
        {
            start.Environment["ABL_PORT"] = Port.ToString();
            start.Environment["ABL_STORE"] = Path.Combine(_root, "store");
            start.Environment["ABL_RENDERS"] = Path.Combine(_root, "renders");
            start.Environment["ABL_BLENDER"] = Path.Combine(_root, "no-blender"); // never runs Blender
            start.Environment["ABL_CONFIG"] = Path.Combine(_root, "config.json"); // absent: never the user's file
            foreach (var name in new[] { "ABL_TELEMETRY", "ABL_DESKTOP_ENDPOINT", "ABL_DESKTOP_TOKEN", "ABL_URL" })
                start.Environment.Remove(name);
            return start;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? host = null, string? origin = null)
        {
            if (host is not null) request.Headers.Host = host;
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            return _http.SendAsync(request);
        }

        public async Task<int> SessionCountAsync()
        {
            using var health = await _http.GetAsync("/healthz");
            health.EnsureSuccessStatusCode();
            return JsonNode.Parse(await health.Content.ReadAsStringAsync())!["sessions"]!.GetValue<int>();
        }

        public async Task DisposeAsync()
        {
            _http.Dispose();
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _process?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "agentblender.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("repository root");
        }
    }
}
