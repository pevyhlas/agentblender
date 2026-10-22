using System.Text.Json.Nodes;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class RemoteTempCleanupTests(BlenderFixture fixture)
{
    [Fact(Timeout = 120_000)]
    public async Task FailedRemoteRenderSheetAndKeptLoadRemainConnectionOwned()
    {
        if (!fixture.BlenderAvailable) return;
        var supervisor = new WorkerSupervisor(fixture.Options, NullLogger<WorkerSupervisor>.Instance);
        await using var worker = await supervisor.SpawnAsync(false, default);
        await worker.RequestAsync("exec", new JsonObject
        {
            ["source"] = "import bpy, __main__\n__main__.HANDLERS[\"test_temps\"] = lambda req: {\"paths\": list(__main__._kept_temps)}\nbpy.context.scene.camera = None\n",
        }, fixture.Options.ExecTimeout, default);
        await Assert.ThrowsAsync<WorkerOpException>(() => worker.RequestAsync("render", new JsonObject
        {
            ["engine"] = "eevee", ["res"] = 64, ["return_blob"] = true,
        }, fixture.Options.RenderTimeout, default));
        await Assert.ThrowsAsync<WorkerOpException>(() => worker.RequestAsync("contact_sheet", new JsonObject
        {
            ["inputs"] = new JsonArray("/nonexistent/abl-test-fixture.png"), ["return_blob"] = true,
        }, fixture.Options.ExecTimeout, default));
        await Assert.ThrowsAsync<WorkerOpException>(() => worker.RequestAsync("load_bytes", new JsonObject
        {
            ["blend_b64"] = Convert.ToBase64String("not-a-blend"u8), ["keep"] = true,
        }, fixture.Options.ExecTimeout, default));
        var result = await worker.RequestAsync("test_temps", null, fixture.Options.ExecTimeout, default);
        var paths = result["paths"]!.AsArray().Select(p => p!.GetValue<string>()).Distinct().ToList();
        Assert.Equal(3, paths.Count);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        await worker.ShutdownAsync();
        Assert.All(paths, p => Assert.False(File.Exists(p), $"connection-owned temp leaked: {p}"));
    }
}
