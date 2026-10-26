using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Config;
using ModelContextProtocol.Client;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 T039 through the built CLI and a real daemon with the <see cref="StubWorker"/> (unit
/// lane): AC-27 <c>daemon_status</c> parity over MCP, REST and CLI with no token value;
/// <c>daemon_status</c> reporting <c>blender.allowUnsupported</c> and a spawn refusal's check;
/// AC-29 quiet logs; AC-22/AC-23 <c>install-service --dry-run</c> writing nothing and its
/// <c>ExecStart</c> running <c>abl --version</c> under an empty environment.
/// </summary>
public sealed partial class DaemonLifecycleTests
{
    [Fact(Timeout = 120_000)]
    public async Task DaemonStatusIsOneObjectOverMcpRestAndCliAndCarriesNoTokenValue()
    {
        var token = "tok-" + Guid.NewGuid().ToString("N");
        var env = new Dictionary<string, string> { ["ABL_DESKTOP_TOKEN"] = token };
        var (startCode, started, startErr) = await RunAsync(["daemon", "start", "--json", "--timeout", "60"], env);
        Assert.True(startCode == 0, startErr);
        var pid = JsonNode.Parse(started)!["pid"]!.GetValue<int>();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        using var response = await http.PostAsync("/api/ops/daemon_status", Json([]));
        var restText = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, restText);
        var rest = JsonNode.Parse(restText)!;

        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp") }));
        var call = await mcp.CallToolAsync("daemon_status", new Dictionary<string, object?>());
        var mcpText = call.StructuredContent!.Value.GetRawText();
        var viaMcp = JsonNode.Parse(mcpText)!;

        var (cliCode, cliText, cliErr) = await RunAsync(["daemon", "status", "--json"], env);
        Assert.True(cliCode == 0, cliErr);
        var cli = JsonNode.Parse(cliText)!.AsObject();
        var local = cli["local"]!.AsObject();
        cli.Remove("local");

        // AC-27: identical shared fields; only the CLI adds `local`.
        Assert.True(JsonNode.DeepEquals(rest, viaMcp), $"REST {restText}\nMCP {mcpText}");
        Assert.True(JsonNode.DeepEquals(rest, cli), $"REST {restText}\nCLI {cli.ToJsonString()}");
        Assert.Equal(pid, rest["pid"]!.GetValue<int>());
        Assert.Equal($"http://127.0.0.1:{_port}", rest["url"]!.GetValue<string>());
        Assert.Contains(rest["logFile"]!.GetValue<string>(), started);
        Assert.Equal("5.2.1", rest["blender"]!["version"]!.GetValue<string>());
        Assert.Equal("reference", rest["blender"]!["support"]!.GetValue<string>());
        Assert.False(rest["blender"]!["allowUnsupported"]!.GetValue<bool>());
        Assert.True(rest["workers"]!["ready"]!.GetValue<int>() >= 1);
        Assert.Equal("<redacted>", rest["config"]!["effective"]!["desktop.token"]!["value"]!.GetValue<string>());
        Assert.Equal(Abl.Core.Lifecycle.DaemonPaths.StateFile(Store), local["stateFile"]!.GetValue<string>());
        Assert.False(local["service"]!["runsThisDaemon"]!.GetValue<bool>());

        // No output carries the token value.
        Assert.DoesNotContain(token, restText);
        Assert.DoesNotContain(token, mcpText);
        Assert.DoesNotContain(token, cliText);
        var (_, statusText, _) = await RunAsync(["daemon", "status"], env);
        Assert.Contains($"running pid {pid}", statusText);
        Assert.DoesNotContain(token, statusText);

        var (stopCode, _, stopErr) = await RunAsync(["daemon", "stop", "--json"]);
        Assert.True(stopCode == 0, stopErr);
        AssertNoOrphans(pid);
    }

    [Fact(Timeout = 120_000)]
    public async Task DaemonStatusShowsAllowUnsupportedAndASpawnRefusalsCheck()
    {
        // FR-003, plan A4: unsupported use is visible. The 5.1.0 stub is admitted by
        // blender.allowUnsupported, then its hello (5.2.1) disagrees with the probe, so every
        // spawn is refused with blender.version, which lastWorkerError names (T036).
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """{"version":1,"blender":{"allowUnsupported":true}}""");
        File.SetUnixFileMode(Path.Combine(_root, "config.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await using var old = new StubWorker(PrivateDir("old-blender"), version: "5.1.0");
        var logFile = Path.Combine(_root, "foreground.log");
        using var daemon = Process.Start(Prepare(["daemon", "run", "--log-file", logFile], new() { ["ABL_BLENDER"] = old.ScriptPath }))!;
        try
        {
            JsonNode? status = null;
            Assert.True(await WaitUntilAsync(async () =>
            {
                status = await TryStatusAsync();
                return status?["workers"]?["lastWorkerError"] is JsonObject;
            }, TimeSpan.FromSeconds(60)), $"no lastWorkerError; last status {status?.ToJsonString()}");

            Assert.Equal(daemon.Id, status!["pid"]!.GetValue<int>());
            Assert.True(status["blender"]!["allowUnsupported"]!.GetValue<bool>());
            Assert.Equal("unsupported", status["blender"]!["support"]!.GetValue<string>());
            Assert.Equal("5.1.0", status["blender"]!["version"]!.GetValue<string>());
            Assert.Equal("blender.version", status["workers"]!["lastWorkerError"]!["check"]!.GetValue<string>());
            Assert.Equal(0, status["workers"]!["ready"]!.GetValue<int>());
        }
        finally
        {
            UnixIdentity.Signal(daemon.Id, UnixIdentity.SigTerm);
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task AtTheDefaultLevelTheLogHasOneStartupSummaryAndNoRequestLines()
    {
        // AC-29: start, one session open/preview/close (REST) plus an MCP client, stop.
        var pid = await StartAsync();
        using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") })
        {
            using var opened = await http.PostAsync("/api/ops/session_open", Json(new JsonObject { ["project"] = "quiet" }));
            Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
            var session = JsonNode.Parse(await opened.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
            using var preview = await http.PostAsync("/api/ops/scene_preview", Json(new JsonObject { ["session"] = session, ["size"] = 64 }));
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            using var closed = await http.PostAsync("/api/ops/session_close", Json(new JsonObject { ["session"] = session }));
            Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());

            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp") }));
            Assert.Contains(await mcp.ListToolsAsync(), t => t.Name == "daemon_status");
        }

        var logFile = Abl.Core.Lifecycle.DaemonState.Read(Store)!.LogFile!;
        var (stopCode, _, stopErr) = await RunAsync(["daemon", "stop", "--json"]);
        Assert.True(stopCode == 0, stopErr);

        var lines = await File.ReadAllLinesAsync(logFile);
        var log = string.Join('\n', lines);
        Assert.Single(lines, l => l.Contains($"daemon {AblVersion.Current} started: pid {pid}, http://127.0.0.1:{_port}", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Microsoft.AspNetCore", StringComparison.Ordinal)
            || l.Contains("ModelContextProtocol", StringComparison.Ordinal)
            || l.Contains("Microsoft.Hosting", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(" closed", StringComparison.Ordinal) && l.Contains("session ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"daemon pid {pid} stopping", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z (info|warn|fail|crit|dbug|trce): ", l));
        Assert.True(lines.Length < 40, log);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningSuppressesInformationAndStartupUnlessFrameworkRequestsAreExplicitlyEnabled(bool frameworkOverride)
    {
        var logFile = Path.Combine(_root, "warning.log");
        var start = Prepare(["daemon", "run", "--log-file", logFile], new() { ["ABL_LOG_LEVEL"] = "Warning" });
        // Only this case's explicit framework setting may override the quiet-log defaults.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        if (frameworkOverride)
        {
            start.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Information";
        }

        using var daemon = Process.Start(start)!;
        var stdout = daemon.StandardOutput.ReadToEndAsync();
        var stderr = daemon.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal(daemon.Id, (await HealthAsync())["pid"]!.GetValue<int>());
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{_port}"),
                Timeout = TimeSpan.FromSeconds(10),
            };
            using var status = await http.PostAsync("/api/ops/daemon_status", Json([]));
            var statusText = await status.Content.ReadAsStringAsync();
            Assert.True(status.IsSuccessStatusCode, statusText);
            Assert.Equal("warning", JsonNode.Parse(statusText)!["config"]!["effective"]!["daemon.logLevel"]!["value"]!.GetValue<string>());

            using var opened = await http.PostAsync("/api/ops/session_open", Json(new JsonObject { ["project"] = "warning" }));
            var openedText = await opened.Content.ReadAsStringAsync();
            Assert.True(opened.IsSuccessStatusCode, openedText);
            var session = JsonNode.Parse(openedText)!["id"]!.GetValue<string>();
            using var closed = await http.PostAsync("/api/ops/session_close", Json(new JsonObject { ["session"] = session }));
            Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());

            // A positive Warning control proves an empty/misdirected log cannot pass the test.
            using var rejected = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            rejected.Headers.Add("Origin", "null");
            using var refusal = await http.SendAsync(rejected);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, refusal.StatusCode);
        }
        finally
        {
            if (!daemon.HasExited) UnixIdentity.Signal(daemon.Id, UnixIdentity.SigTerm);
            try
            {
                await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                daemon.Kill(entireProcessTree: true);
                await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }

            // Process exit includes logger disposal; drain the original pipes before inspecting bytes.
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(daemon.ExitCode == 0, await stderr);
        Assert.DoesNotContain(Processes(), p => p.Args.Contains(RuntimeDir + "/", StringComparison.Ordinal));
        var lines = await File.ReadAllLinesAsync(logFile);
        Assert.Contains(lines, line => line.Contains(" warn: Abl.Daemon.HostGuard", StringComparison.Ordinal)
            && line.Contains("host guard rejected a request with 403", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(" info: Abl.", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains($"daemon {AblVersion.Current} started:", StringComparison.Ordinal));

        if (frameworkOverride)
        {
            Assert.Contains(lines, line => line.Contains(" info: Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)
                && line.Contains($"Request starting HTTP/1.1 POST http://127.0.0.1:{_port}/api/ops/daemon_status", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains(" info: Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)
                && line.Contains($"Request finished HTTP/1.1 POST http://127.0.0.1:{_port}/api/ops/daemon_status", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(lines, line => line.Contains(" info: ", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("Request starting", StringComparison.Ordinal)
                || line.Contains("Request finished", StringComparison.Ordinal));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task InstallServiceDryRunWritesNothingAndItsExecStartRunsUnderAnEmptyEnvironment()
    {
        // AC-22: the unit (or plist) carries DOTNET_ROOT and no ABL_* key or secret; a dry run
        // changes no file. AC-23: its ExecStart runs `abl --version` under `env -i`.
        var home = Path.Combine(_root, "home");
        var (code, stdout, stderr) = await RunAsync(["daemon", "install-service", "--dry-run", "--json"],
            new() { ["ABL_DESKTOP_TOKEN"] = "s3cret-token" });
        Assert.True(code == 0, stderr);
        var plan = JsonNode.Parse(stdout)!;
        Assert.True(plan["dryRun"]!.GetValue<bool>());
        Assert.Empty(Directory.EnumerateFileSystemEntries(home, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(plan["path"]!.GetValue<string>()));

        var expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")));
        var root = plan["dotnetRoot"]!.GetValue<string>();
        Assert.Equal(expectedRoot, root);
        var contents = plan["contents"]!.GetValue<string>();
        Assert.Contains(root, contents);
        Assert.DoesNotContain(contents.Split('\n'), l => !l.TrimStart().StartsWith('#') && l.Contains("ABL_", StringComparison.Ordinal));
        Assert.DoesNotContain("s3cret", contents);
        Assert.DoesNotContain("s3cret", stdout + stderr);
        Assert.Contains("ABL_DESKTOP_TOKEN", stderr); // the warning names what stays behind, never its value

        // The unit's own words (eng-test L-1, eng-security MEDIUM-1): ExecStart begins with
        // `env -i` and carries every variable the daemon gets, so running exactly those words
        // is the AC-23 shape ("env -i HOME=… plus only the unit's environment").
        var execStart = plan["execStart"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal(["/usr/bin/env", "-i", $"HOME={home}"], execStart[..3]);
        Assert.Contains($"DOTNET_ROOT={root}", execStart);
        Assert.Equal(["daemon", "run", "--log-file"], execStart[^4..^1]);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(string.Join('\n', execStart.Select(w => $"    <string>{System.Security.SecurityElement.Escape(w)}</string>")), contents);
        }
        else
        {
            var line = contents.Split('\n').Single(l => l.StartsWith("ExecStart=", StringComparison.Ordinal));
            Assert.StartsWith("ExecStart=/usr/bin/env -i ", line);
            Assert.All(execStart, word => Assert.Contains(word, line));
        }
        var env = new ProcessStartInfo(execStart[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in execStart[1..^4].Append("--version"))
        {
            env.ArgumentList.Add(arg);
        }

        using var version = Process.Start(env)!;
        var output = await version.StandardOutput.ReadToEndAsync();
        var errors = await version.StandardError.ReadToEndAsync();
        await version.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(version.ExitCode == 0, errors);
        Assert.Equal($"abl {AblVersion.Current}\n", output);
    }

    private async Task<JsonNode?> TryStatusAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.PostAsync($"http://127.0.0.1:{_port}/api/ops/daemon_status", Json([]));
            return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
