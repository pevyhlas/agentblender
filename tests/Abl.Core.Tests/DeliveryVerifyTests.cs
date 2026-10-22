using System.Text;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Delivery;
using Xunit;

namespace Abl.Core.Tests;

public sealed class DeliveryVerifyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-delivery-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string WriteGeneration()
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
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
                ["engine"] = "cycles_cpu",
                ["res"] = 1,
                ["samples"] = 1,
                ["seed"] = 0,
                ["colorSpace"] = "srgb",
                ["alpha"] = "straight",
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
            Build = "test.1",
            BlenderBuildHash = "9e2066aef7ef",
            HostAssemblySha256 = new string('b', 64),
            WorkerSourceSha256 = new string('c', 64),
        },
            AgdStillDescriptor.Profile, DateTimeOffset.Parse("2026-09-29T00:00:00Z"), [item])
        {
            DescriptorSha256 = AgdStillDescriptor.Hash(Encoding.UTF8.GetBytes(metadata)),
            RecipeSnapshotSha256 = AgdStillDescriptor.Hash(recipe),
        };
        File.WriteAllText(Path.Combine(directory, "delivery.json"), DeliveryJson.Serialize(receipt));
        return directory;
    }

    [Fact]
    public void IntactGenerationVerifiesWithoutAdmissionClaims()
    {
        var directory = WriteGeneration();
        var result = DeliveryPayload.Verify(directory);
        Assert.True(result.Pass, string.Join(";", result.Problems));
        Assert.False(result.Admitted); Assert.False(result.ProvenanceVerified); Assert.False(result.PixelsDecoded); Assert.False(result.SourceReplayVerified);
        Assert.Equal("still.png", Assert.Single(DeliveryPayload.List(directory).Items).File);
    }

    [Theory]
    [InlineData("payload/still.png")]
    [InlineData("payload/artifact.json")]
    [InlineData("recipe-snapshot.json")]
    [InlineData("replay-source/hero/base.blend")]
    public void BoundFileTamperingFails(string relative)
    {
        var directory = WriteGeneration();
        File.AppendAllText(Path.Combine(directory, relative), "tamper");
        var result = DeliveryPayload.Verify(directory);
        Assert.False(result.Pass); Assert.NotNull(result.Hint);
    }

    [Fact]
    public void ExtraMissingSymlinkAndOversizedInputsFail()
    {
        var directory = WriteGeneration();
        Directory.CreateDirectory(Path.Combine(directory, "payload", "extra"));
        Assert.False(DeliveryPayload.Verify(directory).Pass);
        Directory.Delete(Path.Combine(directory, "payload", "extra"));
        var image = Path.Combine(directory, "payload", "still.png");
        File.Delete(image);
        Assert.False(DeliveryPayload.Verify(directory).Pass);
        File.CreateSymbolicLink(image, "/etc/passwd");
        Assert.False(DeliveryPayload.Verify(directory).Pass);
        File.Delete(image);
        using (var file = File.Create(image)) file.SetLength(DeliveryService.MaxImageBytes + 1);
        Assert.False(DeliveryPayload.Verify(directory).Pass);
    }

    [Theory]
    [InlineData("engine", "eevee")]
    [InlineData("samples", "32")]
    [InlineData("seed", "5")]
    public void ReceiptOnlyRenderMutationFails(string field, string value)
    {
        var directory = WriteGeneration();
        var receipt = Path.Combine(directory, "delivery.json");
        var json = JsonNode.Parse(File.ReadAllText(receipt))!;
        json["items"]![0]!["render"]![field] = field == "engine" ? JsonValue.Create(value) : JsonValue.Create(int.Parse(value));
        File.WriteAllText(receipt, json.ToJsonString());
        Assert.False(DeliveryPayload.Verify(directory).Pass);
    }

    [Fact]
    public void MissingPayloadDirectoryIsACompletedFailedAssessment()
    {
        var directory = WriteGeneration();
        Directory.Delete(Path.Combine(directory, "payload"), true);
        var result = DeliveryPayload.Verify(directory);
        Assert.False(result.Pass);
        Assert.Contains("restore", result.Hint);
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("other.png")]
    public void DescriptorControlledPathsRejectBeforeReads(string path)
    {
        var directory = WriteGeneration();
        var receipt = Path.Combine(directory, "delivery.json");
        var json = JsonNode.Parse(File.ReadAllText(receipt))!;
        json["items"]![0]!["file"] = path;
        File.WriteAllText(receipt, json.ToJsonString());
        Assert.Equal("bad_request", Assert.Throws<AblException>(() => DeliveryPayload.Verify(directory)).Kind);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"formatVersion\":2,\"formatVersion\":2}")]
    [InlineData("{\"formatVersion\":99,\"items\":null}")]
    public void MalformedIncompleteDuplicateVersionReceiptsReject(string json)
    {
        var directory = WriteGeneration();
        File.WriteAllText(Path.Combine(directory, "delivery.json"), json);
        Assert.Throws<AblException>(() => DeliveryPayload.Verify(directory));
    }

    // 008 T020: a symlink in a delivery path is refused unless it is a system alias, i.e. its
    // containing directory is root-owned and not group/other-writable (macOS /tmp, /var).
    [Fact]
    public void UserCreatedSymlinkInADeliveryPathIsRefused()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);

        var error = Assert.Throws<AblException>(() => DeliveryPayload.RefuseLinks(Path.Combine(link, "out")));
        Assert.Contains("symlinks", error.Message);
        Assert.False(DeliveryPayload.IsSystemAliasContainer(_root)); // the user owns the temp root
    }

    [Fact]
    public void SymlinkIsAllowedOnlyWhenItsContainerIsASystemDirectory()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);
        var target = Path.Combine(link, "out");

        // The real rule still judges the temp root's own ancestors (macOS /var → /private/var).
        DeliveryPayload.RefuseLinks(target, container => container == _root || DeliveryPayload.IsSystemAliasContainer(container)); // no throw
        Assert.Throws<AblException>(() => DeliveryPayload.RefuseLinks(target, container => container != _root && DeliveryPayload.IsSystemAliasContainer(container)));
    }

    [Fact]
    public void SystemTempAliasesAreAllowed()
    {
        // The macOS failure this fixes: /tmp and /var are root-owned links into /private.
        Assert.True(DeliveryPayload.IsSystemAliasContainer("/"));
        DeliveryPayload.RefuseLinks(Path.Combine("/tmp", "abl-t020-" + Guid.NewGuid().ToString("N"))); // no throw
        if (OperatingSystem.IsMacOS())
            DeliveryPayload.RefuseLinks(Path.Combine(Path.GetTempPath(), "abl-t020")); // /var/folders/…: no throw
        Assert.False(DeliveryPayload.IsSystemAliasContainer("/tmp")); // sticky 1777: others can write
    }
}
