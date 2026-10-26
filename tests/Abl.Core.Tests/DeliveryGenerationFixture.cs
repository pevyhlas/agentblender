using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core.Delivery;
using Xunit;

namespace Abl.Core.Tests;

internal static class DeliveryGenerationFixture
{
    // Intentionally historical: no Blender policy in either file.
    internal static string Write(string root)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(directory, "payload");
        var replay = Path.Combine(directory, "replay-source", "hero");
        Directory.CreateDirectory(payload); Directory.CreateDirectory(replay);
        var png = PngProfileTests.Png(width: 1, height: 1, srgb: true);
        byte[] basis = [1, 2, 3];
        var source = new JsonObject { ["name"] = "hero", ["baseCheckpoint"] = AgdStillDescriptor.Hash(basis) };
        File.WriteAllText(Path.Combine(replay, "artifact.json"), source.ToJsonString());
        var recipe = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["profile"] = "abl.still-recipe-snapshot.v1",
            ["frozenArtifact"] = source.DeepClone(),
            ["overlay"] = new JsonObject(),
            ["render"] = new JsonObject { ["view"] = "Standard" },
            ["renderDeclaration"] = new JsonObject
            {
                ["engine"] = "cycles_cpu", ["res"] = 1, ["samples"] = 1, ["seed"] = 0,
                ["colorSpace"] = "srgb", ["alpha"] = "straight",
                ["effectiveSettings"] = new JsonObject { ["view"] = "Standard" },
            },
            ["producerBuild"] = "test.1",
            ["blenderVersion"] = "5.2.1 LTS",
            ["blenderBuildHash"] = "9e2066aef7ef",
            ["hostAssemblySha256"] = new string('b', 64),
            ["workerSourceSha256"] = new string('c', 64),
        }.ToJsonString());
        File.WriteAllBytes(Path.Combine(directory, "recipe-snapshot.json"), recipe);
        File.WriteAllBytes(Path.Combine(replay, "base.blend"), basis);
        File.WriteAllBytes(Path.Combine(payload, "still.png"), png);
        var item = new DeliveryItem("still.png", png.LongLength, AgdStillDescriptor.Hash(png), AgdStillDescriptor.Hash(basis), new string('a', 64),
            new("hero", new()), new("cycles_cpu", 1, 1, 0, "srgb", "straight") { EffectiveSettings = new() { ["view"] = "Standard" } }, new(1, 1));
        var metadata = AgdStillDescriptor.Serialize("hero", item, "test.1", AgdStillDescriptor.Hash(recipe));
        File.WriteAllText(Path.Combine(payload, "artifact.json"), metadata);
        var receipt = new DeliveryDescriptor(2, "hero", new("agentblender", "5.2.1 LTS")
        {
            Build = "test.1", BlenderBuildHash = "9e2066aef7ef",
            HostAssemblySha256 = new string('b', 64), WorkerSourceSha256 = new string('c', 64),
        }, AgdStillDescriptor.Profile, DateTimeOffset.Parse("2026-09-29T00:00:00Z"), [item])
        {
            DescriptorSha256 = AgdStillDescriptor.Hash(Encoding.UTF8.GetBytes(metadata)),
            RecipeSnapshotSha256 = AgdStillDescriptor.Hash(recipe),
        };
        File.WriteAllText(Path.Combine(directory, "delivery.json"), DeliveryJson.Serialize(receipt));
        return directory;
    }

    // Raw policy text permits duplicate-key/null fixtures. Rebind *all* hash links so a
    // semantic negative cannot pass merely because the verifier caught an obsolete hash.
    internal static void SetPolicy(string directory, string? receiptPolicy, string? snapshotPolicy, string version = "5.2.1 LTS")
    {
        var receiptPath = Path.Combine(directory, "delivery.json");
        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "recipe-snapshot.json")))!.AsObject();
        receipt["producer"]!["blenderVersion"] = version;
        snapshot["blenderVersion"] = version;
        if (receiptPolicy is not null) receipt["producer"]!["blenderPolicy"] = "__POLICY__";
        if (snapshotPolicy is not null) snapshot["blenderPolicy"] = "__POLICY__";
        var recipe = Encoding.UTF8.GetBytes(snapshot.ToJsonString().Replace("\"__POLICY__\"", snapshotPolicy ?? "null", StringComparison.Ordinal));
        File.WriteAllBytes(Path.Combine(directory, "recipe-snapshot.json"), recipe);
        receipt["recipeSnapshotSha256"] = AgdStillDescriptor.Hash(recipe);
        var item = receipt["items"]![0]!.Deserialize<DeliveryItem>(JsonSerializerOptions.Web)!;
        var metadata = AgdStillDescriptor.Serialize("hero", item, "test.1", AgdStillDescriptor.Hash(recipe));
        File.WriteAllText(Path.Combine(directory, "payload", "artifact.json"), metadata);
        receipt["descriptorSha256"] = AgdStillDescriptor.Hash(Encoding.UTF8.GetBytes(metadata));
        File.WriteAllText(receiptPath, receipt.ToJsonString().Replace("\"__POLICY__\"", receiptPolicy ?? "null", StringComparison.Ordinal));
        AssertBindings(directory);
    }

    internal static void AssertBindings(string directory)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "delivery.json")));
        var recipeHash = AgdStillDescriptor.Hash(File.ReadAllBytes(Path.Combine(directory, "recipe-snapshot.json")));
        var metadata = File.ReadAllBytes(Path.Combine(directory, "payload", "artifact.json"));
        using var agd = JsonDocument.Parse(metadata);
        Assert.Equal(recipeHash, receipt.RootElement.GetProperty("recipeSnapshotSha256").GetString());
        Assert.Equal(recipeHash, agd.RootElement.GetProperty("provenance").GetProperty("recipeSnapshotSha256").GetString());
        Assert.Equal(AgdStillDescriptor.Hash(metadata), receipt.RootElement.GetProperty("descriptorSha256").GetString());
    }

    internal static string[] Observe(string directory) => [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" +
            (File.Exists(path) ? AgdStillDescriptor.Hash(File.ReadAllBytes(path)) : "directory"))];
}
