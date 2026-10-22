using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Abl.Integration.Tests;

/// <summary>
/// 008 T019, AC-14 (real-Blender half): a canary variable and <c>PYTHONPATH</c> in the
/// daemon's environment are absent from the worker's <c>os.environ</c> (read through
/// <c>scene_exec</c>), on any host. On a Linux host with an OptiX GPU
/// (<c>OptixQualification</c>, run by hand; the hosted lane has no GPU and excludes it), the
/// scrubbed worker still reports OptiX: its hello says so, and a <c>cycles_gpu</c> render
/// passes the <c>gpu_unavailable</c> guard and reports the OptiX device. Only variable names
/// are recorded, never values.
/// </summary>
[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class WorkerEnvironmentTests(BlenderFixture fixture, ITestOutputHelper output)
{
    private const string Canary = "ABL_AC14_CANARY";

    private const string Probe = """
        import json, os, __main__
        with open(params["out"], "w", encoding="utf-8") as out:
            json.dump({
                "names": sorted(os.environ.keys()),
                "backend": __main__.op_hello({})["gpu"]["backend"],
            }, out)
        """;

    [Fact(Timeout = 300_000)]
    public async Task CanaryNeverReachesTheWorker()
    {
        var record = await ProbeScrubbedWorkerAsync("worker-env", gpuRender: false);
        AssertScrubbed(record);
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "OptixQualification")]
    public async Task CanaryNeverReachesTheWorkerAndOptixIsStillReported()
    {
        var record = await ProbeScrubbedWorkerAsync("worker-env-optix", gpuRender: true);
        AssertScrubbed(record);
        Assert.Equal("optix", record["backend"]!.GetValue<string>()); // the scrubbed worker must still report OptiX
    }

    /// <summary>
    /// Opens a session with the canary and <c>PYTHONPATH</c> set in this process, records the
    /// worker's variable names and hello backend, and with <paramref name="gpuRender"/> also
    /// renders <c>cycles_gpu</c> on that worker and asserts the OptiX device.
    /// </summary>
    private async Task<JsonNode> ProbeScrubbedWorkerAsync(string name, bool gpuRender)
    {
        Assert.True(fixture.BlenderAvailable, "AC-14 requires ABL_BLENDER");
        var outPath = Path.Combine(fixture.Root, $"{name}.json");
        Environment.SetEnvironmentVariable(Canary, "must-not-leak");
        Environment.SetEnvironmentVariable("PYTHONPATH", Path.Combine(fixture.Root, "no-such-dir"));
        try
        {
            var session = await fixture.Sessions.OpenAsync(name, default); // spawns now (pool floor 0)
            try
            {
                await fixture.Sessions.ExecAsync(session.Id, Probe, new JsonObject { ["out"] = outPath }, default);
                if (gpuRender)
                {
                    var render = await fixture.Sessions.RenderFinalAsync(
                        session.Id, null, "cycles_gpu", 32, 1, null, "wait", 0, default);
                    Assert.True(new FileInfo(render.Path).Length > 0);
                    Assert.Equal("optix", render.Device?.Backend);
                }
            }
            finally
            {
                await fixture.Sessions.CloseAsync(session.Id);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(Canary, null);
            Environment.SetEnvironmentVariable("PYTHONPATH", null);
        }

        return JsonNode.Parse(await File.ReadAllTextAsync(outPath))!;
    }

    private void AssertScrubbed(JsonNode record)
    {
        var names = record["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        output.WriteLine("worker env names: " + string.Join(" ", names));
        Assert.DoesNotContain(Canary, names);
        Assert.DoesNotContain("PYTHONPATH", names);
        Assert.Contains("PATH", names);
        Assert.Contains("HOME", names);
    }
}
