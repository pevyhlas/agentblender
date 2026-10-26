using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Delivery;
using Xunit;

namespace Abl.Core.Tests;

public sealed class DeliveryVerifyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-delivery-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string WriteGeneration() => DeliveryGenerationFixture.Write(_root);

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
        var before = DeliveryGenerationFixture.Observe(directory);
        var error = Assert.Throws<AblException>(() => DeliveryPayload.List(directory));
        Assert.Equal("bad_request", error.Kind);
        Assert.Equal(result.Hint, error.Hint);
        Assert.Equal(before, DeliveryGenerationFixture.Observe(directory));
    }

    [Fact]
    public void ExtraMissingSymlinkAndOversizedInputsFail()
    {
        var directory = WriteGeneration();
        Directory.CreateDirectory(Path.Combine(directory, "payload", "extra"));
        Assert.False(DeliveryPayload.Verify(directory).Pass);
        Assert.Throws<AblException>(() => DeliveryPayload.List(directory));
        Directory.Delete(Path.Combine(directory, "payload", "extra"));
        var image = Path.Combine(directory, "payload", "still.png");
        File.Delete(image);
        Assert.False(DeliveryPayload.Verify(directory).Pass);
        Assert.Throws<AblException>(() => DeliveryPayload.List(directory));
        File.CreateSymbolicLink(image, "/etc/passwd");
        Assert.False(DeliveryPayload.Verify(directory).Pass);
        Assert.Throws<AblException>(() => DeliveryPayload.List(directory));
        File.Delete(image);
        using (var file = File.Create(image)) file.SetLength(DeliveryService.MaxImageBytes + 1);
        Assert.False(DeliveryPayload.Verify(directory).Pass);
        Assert.Throws<AblException>(() => DeliveryPayload.List(directory));
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
