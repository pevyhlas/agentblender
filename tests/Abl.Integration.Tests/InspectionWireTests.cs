using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Abl.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class InspectionWireTests
{
    [Fact(Timeout = 180_000)]
    public async Task ActualCliRestAndMcpShareStructuredReadContract()
    {
        Assert.True(File.Exists(TestOptions.FromEnvironment().BlenderPath), "ABL_BLENDER must select real Blender");
        var root = Path.Combine(Path.GetTempPath(), "abl-inspect-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var daemonStart = AblCli.Daemon(root);
        daemonStart.Environment["ABL_PORT"] = port.ToString();
        daemonStart.Environment["ABL_STORE"] = Path.Combine(root, "store");
        daemonStart.Environment["ABL_RENDERS"] = Path.Combine(root, "renders");
        daemonStart.Environment["ABL_CONFIG"] = Path.Combine(root, "config.json"); // absent: never the user's file
        daemonStart.Environment.Remove("ABL_TELEMETRY");
        using var daemon = Process.Start(daemonStart)!;
        daemon.OutputDataReceived += (_, _) => { };
        daemon.ErrorDataReceived += (_, _) => { };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                try { if ((await http.GetAsync("/healthz", deadline.Token)).IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
            var session = (await Post("session_open", new() { ["project"] = "inspection-wire" }))["id"]!.GetValue<string>();
            var edit = await Post("scene_exec", new() { ["session"] = session, ["script"] = SceneInspectionTests.Setup });
            var request = JsonNode.Parse("""{"objects":["Cube"],"landmarks":[{"name":"offset","world":[1,0.5,0]}]}""")!;
            var rest = await Post("scene_inspect", new() { ["session"] = session, ["request"] = request.DeepClone() });
            SceneInspectionTests.AssertVector(rest["landmarks"]![0]!["pixels"]!, [150, 25]);
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            }));
            var tools = await mcp.ListToolsAsync();
            var tool = Assert.Single(tools, t => t.Name == "scene_inspect");
            Assert.Contains("objects", tool.JsonSchema.GetRawText());
            var call = await mcp.CallToolAsync("scene_inspect", new Dictionary<string, object?>
            {
                ["session"] = session,
                ["request"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(request.ToJsonString()),
            });
            Assert.False(call.IsError == true, string.Join(" ", call.Content!.OfType<TextContentBlock>().Select(t => t.Text)));
            var actual = JsonNode.Parse(call.StructuredContent!.Value.GetRawText());
            Assert.True(JsonNode.DeepEquals(rest, actual));
            var queryFile = Path.Combine(root, "request.json");
            await File.WriteAllTextAsync(queryFile, request.ToJsonString());
            var cliStart = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            cliStart.ArgumentList.Add(AblCli.Dll);
            foreach (var arg in new[] { "scene", "inspect", session, "--request", queryFile }) cliStart.ArgumentList.Add(arg);
            cliStart.Environment["ABL_URL"] = $"http://127.0.0.1:{port}";
            using var cli = Process.Start(cliStart)!;
            var output = cli.StandardOutput.ReadToEndAsync();
            var error = cli.StandardError.ReadToEndAsync();
            await cli.WaitForExitAsync();
            Assert.True(cli.ExitCode == 0, await error);
            Assert.True(JsonNode.DeepEquals(rest, JsonNode.Parse(await output)));
            var listed = await Post("session_list", new());
            Assert.Equal(edit["checkpoint"]!.GetValue<string>(), listed["items"]![0]!["head"]!.GetValue<string>());
            var invalid = await http.PostAsync("/api/ops/scene_inspect", new StringContent(
                new JsonObject { ["session"] = session, ["request"] = new JsonObject { ["objects"] = new JsonArray("absent") } }.ToJsonString(),
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            foreach (var malformed in new[]
            {
                "{\"geometry\":true,\"geometry\":false}",
                "{\"geometry\":true,\"\\u0067eometry\":false}",
                "{\"Geometry\":true}", "{\"objects\":null}",
                "{\"landmarks\":[{\"name\":\"p\",\"world\":[1,2]}]}",
            })
            {
                using var rejected = await http.PostAsync("/api/ops/scene_inspect", new StringContent(
                    "{\"session\":\"" + session + "\",\"request\":" + malformed + "}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                using var literal = System.Text.Json.JsonDocument.Parse(malformed);
                var rejectedMcp = await mcp.CallToolAsync("scene_inspect", new Dictionary<string, object?>
                {
                    ["session"] = session,
                    ["request"] = literal.RootElement,
                });
                Assert.True(rejectedMcp.IsError == true);
                Assert.Contains("bad_request", string.Join(" ", rejectedMcp.Content!.OfType<TextContentBlock>().Select(t => t.Text)));
            }
            cliStart.ArgumentList[^1] = Path.Combine(root, "missing.json");
            using var missing = Process.Start(cliStart)!;
            var missingOutput = missing.StandardError.ReadToEndAsync();
            await missing.WaitForExitAsync();
            Assert.Equal(2, missing.ExitCode);
            Assert.Contains("correct its path", await missingOutput);
            await Post("session_close", new() { ["session"] = session });

            async Task<JsonObject> Post(string op, JsonObject body)
            {
                using var response = await http.PostAsync("/api/ops/" + op,
                    new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
                var json = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, op + ": " + json);
                return JsonNode.Parse(json)!.AsObject();
            }
        }
        finally
        {
            if (!daemon.HasExited) { daemon.Kill(entireProcessTree: true); await daemon.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
    }
}
