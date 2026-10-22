using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Abl.Integration.Tests;

internal static class PublicDeliveryFixture
{
    private static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "agentblender.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("repository root");
        }
    }
    private static string Config => AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
    public static string CliAssembly => Path.Combine(Root, "src/Abl.Cli/bin", Config, "net10.0/abl.dll");

    public static async Task<JsonObject> ExportAsync(string recipe, string name, string destination)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var state = Path.Combine(Path.GetDirectoryName(destination)!, "public-state-" + Guid.NewGuid().ToString("N"));
        var start = AblCli.Daemon(state);
        start.Environment["ABL_PORT"] = port.ToString(); start.Environment["ABL_STORE"] = state;
        start.Environment["ABL_CONFIG"] = Path.Combine(state, "config.json"); // absent: never the user's file
        start.Environment.Remove("ABL_TELEMETRY"); start.Environment.Remove("ABL_DESKTOP_ENDPOINT"); start.Environment.Remove("ABL_DESKTOP_TOKEN");
        using var daemon = Process.Start(start)!;
        daemon.OutputDataReceived += (_, _) => { }; daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine(); daemon.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(120) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                try { if ((await http.GetAsync("/healthz", deadline.Token)).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
            var opened = await Post("session_open", new() { ["project"] = "qualified-still" });
            var session = opened["id"]!.GetValue<string>();
            await Post("scene_exec", new() { ["session"] = session, ["script"] = recipe });
            await Post("trajectory_freeze", new() { ["session"] = session, ["name"] = name });
            await Post("session_close", new() { ["session"] = session });
            var exported = await Post("delivery_export", new()
            {
                ["artifact"] = name,
                ["out"] = destination,
                ["engine"] = "cycles_cpu",
                ["res"] = 64,
                ["samples"] = 16,
                ["columns"] = 1,
            });
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            }));
            var call = await mcp.CallToolAsync("delivery_export", new Dictionary<string, object?>
            {
                ["artifact"] = name,
                ["out"] = destination,
                ["engine"] = "cycles_cpu",
                ["res"] = 64,
                ["samples"] = 16,
                ["columns"] = 1,
            });
            Assert.False(call.IsError == true, string.Join(" ", call.Content!.OfType<TextContentBlock>().Select(c => c.Text)));
            Assert.Single(call.Content!.OfType<ImageContentBlock>());
            var textResult = Assert.Single(call.Content!.OfType<TextContentBlock>()).Text;
            var mcpPayload = textResult.Split('\n').Single(line => line.StartsWith("payload:")).Split(':', 2)[1].Trim();
            Assert.NotEqual(exported["payloadDir"]!.GetValue<string>(), mcpPayload);
            Assert.True(Abl.Core.Delivery.DeliveryPayload.Verify(Path.GetDirectoryName(mcpPayload)!).Pass);
            using var missing = await http.PostAsync("/api/ops/delivery_verify", new StringContent(
                new JsonObject { ["payload"] = Path.Combine(destination, "absent-generation") }.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            var blocked = Path.Combine(Path.GetDirectoryName(destination)!, "blocked-output");
            await File.WriteAllTextAsync(blocked, "not a directory");
            using var failedExport = await http.PostAsync("/api/ops/delivery_export", new StringContent(
                new JsonObject { ["artifact"] = name, ["out"] = blocked }.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.InternalServerError, failedExport.StatusCode);
            Assert.Contains("prior generations", await failedExport.Content.ReadAsStringAsync());
            var failedMcp = await mcp.CallToolAsync("delivery_export", new Dictionary<string, object?> { ["artifact"] = name, ["out"] = blocked });
            Assert.True(failedMcp.IsError == true);
            Assert.Contains("choose a writable", string.Join(" ", failedMcp.Content!.OfType<TextContentBlock>().Select(c => c.Text)));
            if (!OperatingSystem.IsWindows())
            {
                var receipt = Path.Combine(Path.GetDirectoryName(exported["payloadDir"]!.GetValue<string>())!, "delivery.json");
                var originalMode = File.GetUnixFileMode(receipt);
                File.SetUnixFileMode(receipt, UnixFileMode.None);
                try
                {
                    foreach (var operation in new[] { "delivery_verify", "delivery_list" })
                    {
                        var directory = Path.GetDirectoryName(receipt)!;
                        using var unreadable = await http.PostAsync("/api/ops/" + operation, new StringContent(
                            new JsonObject { ["payload"] = directory }.ToJsonString(), Encoding.UTF8, "application/json"));
                        Assert.Equal(HttpStatusCode.InternalServerError, unreadable.StatusCode);
                        Assert.Contains("restore the generation", await unreadable.Content.ReadAsStringAsync());
                        var unreadableMcp = await mcp.CallToolAsync(operation, new Dictionary<string, object?> { ["payload"] = directory });
                        Assert.True(unreadableMcp.IsError == true);
                        Assert.Contains("correct its path/access", string.Join(" ", unreadableMcp.Content!.OfType<TextContentBlock>().Select(c => c.Text)));
                        var offlineStart = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
                        offlineStart.ArgumentList.Add(CliAssembly);
                        foreach (var argument in new[] { "delivery", operation == "delivery_verify" ? "verify" : "list", directory }) offlineStart.ArgumentList.Add(argument);
                        using var offline = Process.Start(offlineStart)!;
                        var offlineError = offline.StandardError.ReadToEndAsync();
                        await offline.WaitForExitAsync();
                        Assert.Equal(2, offline.ExitCode);
                        Assert.Contains("restore the generation", await offlineError);
                    }
                }
                finally { File.SetUnixFileMode(receipt, originalMode); }
            }
            foreach (var malformed in new[] { "missing", "invalid" })
            {
                var file = Path.Combine(Path.GetDirectoryName(destination)!, malformed + "-params.json");
                if (malformed == "invalid") await File.WriteAllTextAsync(file, "{invalid");
                var cliStart = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
                cliStart.ArgumentList.Add(CliAssembly);
                foreach (var argument in new[] { "delivery", "export", name, "--out", destination, "--params-file", file }) cliStart.ArgumentList.Add(argument);
                using var cli = Process.Start(cliStart)!;
                var cliError = cli.StandardError.ReadToEndAsync();
                await cli.WaitForExitAsync();
                Assert.Equal(2, cli.ExitCode);
                Assert.Contains("zero/one parameter row", await cliError);
            }
            return exported;
            async Task<JsonObject> Post(string op, JsonObject body)
            {
                using var response = await http.PostAsync("/api/ops/" + op, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, op + ": " + text);
                return JsonNode.Parse(text)!.AsObject();
            }
        }
        finally
        {
            if (!daemon.HasExited) { daemon.Kill(true); await daemon.WaitForExitAsync(); }
            if (Directory.Exists(state)) Directory.Delete(state, true);
        }
    }
}
