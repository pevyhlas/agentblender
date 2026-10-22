using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Security.Cryptography;
using Abl.Core;
using Abl.Core.Delivery;
using Abl.Core.Workers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Abl.Integration.Tests;

[Collection("blender")]
[Trait("Category", "BlenderIntegration")]
public class DeliveryExportTests(BlenderFixture fixture)
{
    private const string Artifact = "shared-still";
    internal const string Recipe = """
        import bpy
        bpy.ops.object.select_all(action='SELECT')
        bpy.ops.object.delete(use_global=False)
        scene = bpy.context.scene
        camera_data = bpy.data.cameras.new('StillCamera')
        camera_data.type = 'ORTHO'
        camera_data.ortho_scale = 4
        camera = bpy.data.objects.new('StillCamera', camera_data)
        scene.collection.objects.link(camera)
        camera.location = (0,0,10)
        scene.camera = camera
        for label, x, color, alpha in [('Red',-1,(1,0,0,1),1),('Green',1,(0,.25,0,1),.5)]:
            mesh = bpy.data.meshes.new(label)
            mesh.from_pydata([(x-.5,-.5,0),(x+.5,-.5,0),(x+.5,.5,0),(x-.5,.5,0)],[],[(0,1,2,3)])
            obj = bpy.data.objects.new(label,mesh)
            scene.collection.objects.link(obj)
            material = bpy.data.materials.new(label)
            material.use_nodes = True
            nodes = material.node_tree.nodes
            nodes.clear()
            output = nodes.new('ShaderNodeOutputMaterial')
            emission = nodes.new('ShaderNodeEmission')
            emission.inputs['Color'].default_value = color
            transparent = nodes.new('ShaderNodeBsdfTransparent')
            mix = nodes.new('ShaderNodeMixShader')
            mix.inputs[0].default_value = alpha
            material.node_tree.links.new(transparent.outputs[0],mix.inputs[1])
            material.node_tree.links.new(emission.outputs[0],mix.inputs[2])
            material.node_tree.links.new(mix.outputs[0],output.inputs['Surface'])
            mesh.materials.append(material)
        if params.get('fail'):
            scene.camera = None
        if params.get('publicationConflict'):
            import os
            def conflict(scene):
                stage = os.path.dirname(scene.render.filepath)
                completed = os.path.join(os.path.dirname(stage),os.path.basename(stage).removeprefix('.stage-'))
                os.mkdir(completed)
                with open(os.path.join(completed,'conflict-marker'),'w') as marker: marker.write('preserve')
            bpy.app.handlers.render_pre.append(conflict)
        """;

    private async Task FreezeAsync(string artifact = Artifact)
    {
        var session = await fixture.Sessions.OpenAsync("shared-still-fixture", default);
        try
        {
            await fixture.Sessions.ExecAsync(session.Id, Recipe, null, default);
            fixture.Trajectories.Freeze(session.Id, artifact, null, null);
        }
        finally { await fixture.Sessions.CloseAsync(session.Id); }
    }

    [Fact(Timeout = 180_000)]
    public async Task StillPublishesCompleteSeparatePayloadAndPreservesPriorGeneration()
    {
        Assert.True(fixture.BlenderAvailable, "real Blender required");
        await FreezeAsync();
        var root = Path.Combine(fixture.Root, "deliveries");
        var first = await fixture.Deliveries.ExportAsync(Artifact, root, [], "cycles_cpu", 64, 16, 0, 1, default);
        var generation = Path.GetDirectoryName(first.PayloadDir)!;
        Assert.Equal(["artifact.json", "still.png"], Directory.GetFiles(first.PayloadDir).Select(Path.GetFileName).Order());
        Assert.True(DeliveryPayload.Verify(generation).Pass);
        var firstBytes = File.ReadAllBytes(first.Images[0]);
        var second = await fixture.Deliveries.ExportAsync(Artifact, root, [], "cycles_cpu", 64, 16, 0, 1, default);
        Assert.NotEqual(first.PayloadDir, second.PayloadDir);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.Images[0]));
        var failed = await Assert.ThrowsAsync<AblException>(() => fixture.Deliveries.ExportAsync(Artifact, root,
            [new JsonObject(), new JsonObject()], "cycles_cpu", 64, 16, 0, 1, default));
        Assert.Equal("bad_request", failed.Kind);
        var renderFailed = await Assert.ThrowsAsync<AblException>(() => fixture.Deliveries.ExportAsync(Artifact, root,
            [new JsonObject { ["fail"] = true }], "cycles_cpu", 64, 16, 0, 1, default));
        Assert.Equal("exec_failed", renderFailed.Kind);
        var publishFailed = await Assert.ThrowsAsync<AblException>(() => fixture.Deliveries.ExportAsync(Artifact, root,
            [new JsonObject { ["publicationConflict"] = true }], "cycles_cpu", 64, 16, 0, 1, default));
        Assert.Equal("io_error", publishFailed.Kind);
        Assert.Equal("preserve", File.ReadAllText(Assert.Single(Directory.GetFiles(root, "conflict-marker", SearchOption.AllDirectories))));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Deliveries.ExportAsync(
            Artifact, root, [], "cycles_cpu", 64, 16, 0, 1, canceled.Token));
        Assert.Equal(firstBytes, File.ReadAllBytes(first.Images[0]));
        Assert.DoesNotContain(Directory.GetDirectories(root), d => Path.GetFileName(d).StartsWith(".stage-"));
        var fresh = new BlenderFixture();
        await fresh.InitializeAsync();
        try
        {
            fresh.Trajectories.Import(Path.Combine(first.ReplaySourceDir, Artifact));
            Assert.True((await fresh.Trajectories.ReplayAsync(Artifact, default)).Pass);
        }
        finally { await fresh.DisposeAsync(); }
        File.AppendAllText(first.Images[0], "tamper");
        Assert.False(DeliveryPayload.Verify(generation).Pass);
    }

    /// <summary>
    /// 009 T009 (plan D12): the render reply's <c>device</c> sits outside its <c>settings</c>,
    /// so a <c>cycles_gpu</c> delivery (the default engine) leaves the receipt unchanged: the
    /// receipt's <c>render</c> keys, its effective settings and the recipe snapshot's
    /// <c>render</c> match a golden list, with no device key anywhere. It needs a GPU backend
    /// (<c>OptixQualification</c>; the hosted lane has no GPU and excludes it).
    /// </summary>
    [Fact(Timeout = 180_000)]
    [Trait("Category", "OptixQualification")]
    public async Task GpuDeliveryReceiptRenderKeysMatchTheGoldenList()
    {
        Assert.True(fixture.BlenderAvailable, "real Blender required");
        const string artifact = "device-golden-still";
        await FreezeAsync(artifact);
        var result = await fixture.Deliveries.ExportAsync(
            artifact, Path.Combine(fixture.Root, "golden-deliveries"), [], "cycles_gpu", 64, 4, 0, 1, default);
        var generation = Path.GetDirectoryName(result.PayloadDir)!;
        Assert.True(DeliveryPayload.Verify(generation).Pass);

        string[] goldenRender = ["alpha", "colorSpace", "effectiveSettings", "engine", "res", "samples", "seed"];
        string[] goldenSettings =
        [
            "border", "colorManagement", "crop", "depth", "display", "dither", "engine", "exposure", "format",
            "gamma", "height", "look", "mode", "percentage", "pixelAspect", "samples", "seed", "transparent",
            "view", "width",
        ];
        static string[] Keys(JsonNode? node) => [.. node!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];

        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(generation, DeliveryService.DescriptorName)))!;
        var render = Assert.Single(receipt["items"]!.AsArray())!["render"];
        Assert.Equal(goldenRender, Keys(render));
        Assert.Equal("cycles_gpu", render!["engine"]!.GetValue<string>());
        Assert.Equal(goldenSettings, Keys(render["effectiveSettings"]));
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(generation, "recipe-snapshot.json")))!;
        Assert.Equal(goldenSettings, Keys(snapshot["render"]));
    }

    [RealAgdFact(Timeout = 240_000)]
    public async Task PublicStillOutputPassesPinnedCliAndMcpReceiverAndIndependentSamples()
    {
        Assert.True(fixture.BlenderAvailable, "real Blender required");
        var cli = ToolPaths.VerifiedAssembly(false);
        var mcp = ToolPaths.VerifiedAssembly(true);
        var version = await Run("dotnet", cli, "--version");
        // The version string embeds the release build's source revision; the package SHA-256 check in
        // VerifiedAssembly pins the exact build, so only the release version is asserted here.
        Assert.StartsWith("0.3.0+", version.Trim(), StringComparison.Ordinal);
        // Public producer REST surface, not a missing private authoring step.
        var exported = await PublicDeliveryFixture.ExportAsync(Recipe, Artifact, Path.Combine(fixture.Root, "public-delivery"));
        var payload = exported["payloadDir"]!.GetValue<string>();
        var descriptor = exported["descriptorPath"]!.GetValue<string>();
        var actualPng = File.ReadAllBytes(Path.Combine(payload, "still.png"));
        var rgba = PngSampleFixture.Decode64(actualPng);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba.AsSpan((32 * 64 + 16) * 4, 4).ToArray());
        var green = rgba.AsSpan((32 * 64 + 48) * 4, 4).ToArray();
        Assert.Equal(0, green[0]); Assert.InRange(green[1], (byte)136, (byte)138); Assert.Equal(0, green[2]); Assert.InRange(green[3], (byte)127, (byte)129);
        Assert.Equal(0, rgba[3]);
        foreach (var operation in new[] { "preflight", "verify" })
        {
            var result = JsonNode.Parse(await Run("dotnet", cli, "assets", operation, descriptor, "-f", "json"))!;
            Assert.True(result["ok"]!.GetValue<bool>());
            Assert.False(result["data"]!["admitted"]!.GetValue<bool>());
            Assert.False(result["data"]!["provenanceVerified"]!.GetValue<bool>());
            if (operation == "verify") Assert.Equal(AgdStillDescriptor.Hash(rgba), result["data"]!["files"]![0]!["rgbaSha256"]!.GetValue<string>());
        }
        var receiver = Path.Combine(fixture.Root, "receiver");
        Directory.CreateDirectory(Path.Combine(receiver, "bundle"));
        foreach (var file in Directory.GetFiles(payload)) File.Copy(file, Path.Combine(receiver, "bundle", Path.GetFileName(file)));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = [mcp, "--working-directory", receiver],
        }));
        foreach (var operation in new[] { "preflight_artifact", "verify_artifact" })
        {
            var result = await client.CallToolAsync(operation, new Dictionary<string, object?> { ["descriptorPath"] = "bundle/artifact.json" });
            Assert.False(result.IsError == true, string.Join(" ", result.Content!.OfType<TextContentBlock>().Select(t => t.Text)));
            var data = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
            Assert.False(data["data"]!["admitted"]!.GetValue<bool>());
            Assert.False(data["data"]!["provenanceVerified"]!.GetValue<bool>());
        }
        var generation = Path.GetDirectoryName(payload)!;
        var abl = PublicDeliveryFixture.CliAssembly;
        var offline = JsonNode.Parse(await Run("dotnet", abl, "delivery", "verify", generation, "--json"))!;
        Assert.True(offline["pass"]!.GetValue<bool>());
        Assert.False(offline["admitted"]!.GetValue<bool>());
        var terse = await Run("dotnet", abl, "delivery", "list", generation);
        Assert.DoesNotContain("observationSha256", terse);
        Assert.Contains("still.png", terse);
    }

    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryProfileRestoresEveryOverriddenSetting(bool fail)
    {
        Assert.True(fixture.BlenderAvailable, "real Blender required");
        var worker = await fixture.Pool.TakeAsync(default);
        try
        {
            await worker.RequestAsync("exec", new()
            {
                ["source"] = """
                    import bpy, __main__
                    s = bpy.context.scene
                    s.render.engine = 'BLENDER_WORKBENCH'
                    s.render.resolution_x = 173
                    s.render.resolution_y = 97
                    s.render.resolution_percentage = 61
                    s.render.filepath = 'authored.exr'
                    s.render.image_settings.file_format = 'OPEN_EXR'
                    s.render.image_settings.color_mode = 'RGB'
                    s.render.image_settings.color_depth = '32'
                    s.render.image_settings.color_management = 'OVERRIDE'
                    s.render.film_transparent = False
                    s.render.use_border = True
                    s.render.use_crop_to_border = True
                    s.render.pixel_aspect_x = 2
                    s.render.dither_intensity = .5
                    s.view_settings.exposure = .25
                    s.view_settings.gamma = 1.1
                    s.view_settings.use_curve_mapping = True
                    def observed():
                        return (s.render.engine,s.render.resolution_x,s.render.resolution_y,s.render.resolution_percentage,
                          s.render.filepath,s.render.image_settings.file_format,s.render.image_settings.color_mode,
                          s.render.image_settings.color_depth,s.render.image_settings.color_management,s.render.film_transparent,
                          s.render.use_border,s.render.use_crop_to_border,s.render.pixel_aspect_x,s.render.pixel_aspect_y,
                          s.render.dither_intensity,s.display_settings.display_device,s.view_settings.view_transform,
                          s.view_settings.look,s.view_settings.exposure,s.view_settings.gamma,s.view_settings.use_curve_mapping)
                    __main__.delivery_observed = observed
                    __main__.delivery_expected = observed()
                    if params['fail']: s.camera = None
                    """,
                ["params"] = new JsonObject { ["fail"] = fail },
            }, TimeSpan.FromSeconds(60), default);
            var request = new JsonObject
            {
                ["engine"] = "cycles_cpu",
                ["res"] = 32,
                ["samples"] = 1,
                ["seed"] = 0,
                ["profile"] = true,
                ["out"] = Path.Combine(fixture.Root, "profile-restore-" + Guid.NewGuid().ToString("N") + ".png"),
            };
            if (fail) await Assert.ThrowsAsync<WorkerOpException>(() => worker.RequestAsync("render", request, TimeSpan.FromSeconds(60), default));
            else await worker.RequestAsync("render", request, TimeSpan.FromSeconds(60), default);
            await worker.RequestAsync("exec", new()
            {
                ["source"] = "import __main__\nassert __main__.delivery_observed() == __main__.delivery_expected",
            }, TimeSpan.FromSeconds(60), default);
        }
        finally { await worker.ShutdownAsync(); await worker.DisposeAsync(); }
    }

    internal static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await error + await output);
        return await output;
    }
}

internal static class ToolPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Cli => Environment.GetEnvironmentVariable("AGD_CLI") ?? Path.Combine(Home, ".local/share/agd/agd-0.3.0/cli/agd");
    public static string Mcp => Environment.GetEnvironmentVariable("AGD_MCP") ?? Path.Combine(Home, ".local/share/agd/agd-0.3.0/mcp/agd-mcp");
    public static string VerifiedAssembly(bool mcp)
    {
        var package = mcp ? "ftplus.agentgraphicdesign.mcp" : "ftplus.agentgraphicdesign.cli";
        var installed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mcp ? Mcp : Cli))!, ".store", package, "0.3.0", package, "0.3.0");
        var archivePath = Path.Combine(installed, package + ".0.3.0.nupkg");
        var expectedHash = mcp ? "9e1fa0d23caefcdcc354d68dd7651ac49a8d349a7d631fd264f7384521b8668a"
            : "b029f9338809a7a720690a3ac79d4f220556b5ba66ab0eec72d7201cbcb88ed6";
        using (var archiveBytes = File.OpenRead(archivePath))
            Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(archiveBytes)));
        var payload = Path.Combine(installed, "tools/net10.0/any");
        using var archive = ZipFile.OpenRead(archivePath);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            const string prefix = "tools/net10.0/any/";
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName[prefix.Length..];
            Assert.False(Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is "." or ".."));
            var file = Path.Combine(payload, relative);
            for (var parent = file; parent is not null && parent != Path.GetDirectoryName(installed); parent = Path.GetDirectoryName(parent))
                Assert.False((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0, "link in approved package payload");
            using var installedBytes = File.OpenRead(file);
            using var packagedBytes = entry.Open();
            Assert.Equal(SHA256.HashData(packagedBytes), SHA256.HashData(installedBytes));
            declared.Add(relative);
        }
        Assert.NotEmpty(declared);
        Assert.Equal(declared.Order(), Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(payload, p).Replace('\\', '/')).Order());
        return Path.Combine(payload, mcp ? "agd-mcp.dll" : "agd.dll");
    }
}
public sealed class RealAgdFactAttribute : FactAttribute
{
    public RealAgdFactAttribute()
    {
        if (!File.Exists(ToolPaths.Cli) || !File.Exists(ToolPaths.Mcp)) Skip = "AgentGraphicDesign 0.3.0 CLI/MCP not installed (set AGD_CLI/AGD_MCP); the receiver qualification is optional and separate from AB CI";
    }
}
