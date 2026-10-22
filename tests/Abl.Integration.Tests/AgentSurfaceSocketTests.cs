using System.Text.Json.Nodes;
using Abl.Core;
using ModelContextProtocol.Client;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 T054 over the built daemon (unit lane: no Blender ever runs; the host-guard fixture's
/// isolated daemon): MCP initialize returns the D3 instructions (AC-31), doctor's
/// <c>daemon.reachable</c> and <c>daemon.config-drift</c> read the running daemon (AC-28),
/// and <c>abl mcp config</c> names <c>daemon_status.url</c> and leaves the file system
/// unchanged (AC-30).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class AgentSurfaceSocketTests(HostGuardSocketTests.Daemon daemon) : IClassFixture<HostGuardSocketTests.Daemon>
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AC31_InitializeReturnsTheServerInstructions()
    {
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri($"http://127.0.0.1:{daemon.Port}/mcp"),
        }));

        Assert.Equal(AgentGuidance.ServerInstructions, mcp.ServerInstructions);
    }

    [Fact]
    public async Task HealthzEncodesDesktopAvailabilityLikeDaemonStatus()
    {
        // T051 (2b round-1 eng-arch LOW-5): true/false/null, never the internal -1.
        using var health = await daemon.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/healthz"));
        var body = JsonNode.Parse(await health.Content.ReadAsStringAsync())!.AsObject();

        Assert.True(body.ContainsKey("desktopTcpAvailable"));
        Assert.Null(body["desktopTcpAvailable"]);
        Assert.Equal(AblVersion.Current, body["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task AC28_DoctorReadsTheRunningDaemonAndReportsNoDriftForTheSameEnvironment()
    {
        var (code, stdout, stderr) = await AblCli.RunAsync(daemon.Cli("doctor", "--json", "--check", "daemon.config-drift"), CliTimeout);

        Assert.True(code == 0, $"doctor exited {code}: {stdout}{stderr}");
        var checks = Checks(stdout);
        Assert.Equal(["config.file", "daemon.reachable", "daemon.config-drift"], checks.Keys);

        // This daemon has no Blender, so it has no ready worker: reachable warns with the
        // pool's last worker error, which names its check.
        var reachable = checks["daemon.reachable"];
        Assert.Equal("warn", reachable["status"]!.GetValue<string>());
        Assert.StartsWith("daemon pid ", reachable["summary"]!.GetValue<string>());
        Assert.Contains("blender.found", reachable["summary"]!.GetValue<string>());
        Assert.Equal("pass", checks["daemon.config-drift"]["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnOpVerbPrintsTheUnreachableHintWhenNothingAnswers()
    {
        // AC-31's golden line as the CLI prints it (CliText.Unreachable), then one cause line.
        int closed;
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            closed = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        var start = daemon.Cli("session", "list");
        start.Environment["ABL_URL"] = $"http://127.0.0.1:{closed}";

        var (code, _, stderr) = await AblCli.RunAsync(start, CliTimeout);

        Assert.Equal(1, code);
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            $"abl: cannot reach the daemon at http://127.0.0.1:{closed} — start it with 'abl daemon start' (diagnose: 'abl doctor')",
            lines[0]);
        Assert.StartsWith("  cause: ", lines[1]);
    }

    [Fact]
    public async Task DaemonPortPassesForThisStoresDaemonAndFailsUnderAnotherStore()
    {
        // The user's own running daemon must not fail daemon.port (every plain doctor run).
        var (code, stdout, stderr) = await AblCli.RunAsync(daemon.Cli("doctor", "--json", "--check", "daemon.port"), CliTimeout);

        Assert.True(code == 0, $"doctor exited {code}: {stdout}{stderr}");
        var mine = Checks(stdout)["daemon.port"];
        Assert.Equal("pass", mine["status"]!.GetValue<string>());
        Assert.StartsWith($"port {daemon.Port} is held by this store's daemon (pid ", mine["summary"]!.GetValue<string>());

        // Another store on the same port: the holder answers /healthz, so it is named as an
        // AgentBlender daemon, not "another program".
        var other = daemon.Cli("doctor", "--json", "--check", "daemon.port");
        other.Environment["ABL_STORE"] = Path.Combine(Path.GetTempPath(), "abl-other-store-" + Guid.NewGuid().ToString("N")[..12]);
        (code, stdout, _) = await AblCli.RunAsync(other, CliTimeout);

        Assert.Equal(1, code);
        var theirs = Checks(stdout)["daemon.port"];
        Assert.Equal("fail", theirs["status"]!.GetValue<string>());
        Assert.StartsWith($"port {daemon.Port} is held by another AgentBlender daemon (pid ", theirs["summary"]!.GetValue<string>());
        Assert.False(Directory.Exists(other.Environment["ABL_STORE"])); // doctor creates no store
    }

    [Fact]
    public async Task AC28_AShellOverrideIsReportedAsDriftWithBothSources()
    {
        var start = daemon.Cli("doctor", "--json", "--check", "daemon.config-drift");
        start.Environment["ABL_LOG_LEVEL"] = start.Environment.TryGetValue("ABL_LOG_LEVEL", out var level) && level == "debug" ? "trace" : "debug";

        var (code, stdout, _) = await AblCli.RunAsync(start, CliTimeout);

        Assert.Equal(0, code); // drift is a warning
        var drift = Checks(stdout)["daemon.config-drift"];
        Assert.Equal("warn", drift["status"]!.GetValue<string>());
        Assert.Contains($"daemon.logLevel: this shell {start.Environment["ABL_LOG_LEVEL"]} (env), daemon ", drift["summary"]!.GetValue<string>());
        var difference = Assert.Single(drift["detail"]!["differences"]!.AsArray())!;
        Assert.Equal("daemon.logLevel", difference["key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    public async Task AC30_McpConfigNamesDaemonStatusUrlAndChangesNoFile(string client)
    {
        var cwd = Directory.CreateTempSubdirectory("abl-mcpcfg-").FullName;
        try
        {
            using var status = await daemon.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/ops/daemon_status")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
            var url = JsonNode.Parse(await status.Content.ReadAsStringAsync())!["url"]!.GetValue<string>();
            var before = Snapshot(daemon.Root);

            var start = daemon.Cli("mcp", "config", "--client", client, "--json");
            start.WorkingDirectory = cwd;
            var (code, stdout, stderr) = await AblCli.RunAsync(start, CliTimeout);

            Assert.True(code == 0, stderr);
            Assert.Equal(url + "/mcp", JsonNode.Parse(stdout)!["url"]!.GetValue<string>());
            Assert.Empty(Directory.EnumerateFileSystemEntries(cwd));
            Assert.Equal(before, Snapshot(daemon.Root));
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    private static Dictionary<string, JsonObject> Checks(string stdout)
    {
        var report = JsonNode.Parse(stdout)!;
        Assert.Equal("doctor.v1", report["schema"]!.GetValue<string>());
        return report["checks"]!.AsArray().ToDictionary(c => c!["id"]!.GetValue<string>(), c => c!.AsObject());
    }

    /// <summary>Every path under <paramref name="root"/> with its size and write time, except the daemon's own log.</summary>
    private static List<string> Snapshot(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".log", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(p => $"{p} {(File.Exists(p) ? new FileInfo(p).Length : -1)} {File.GetLastWriteTimeUtc(p):O}")];
}
