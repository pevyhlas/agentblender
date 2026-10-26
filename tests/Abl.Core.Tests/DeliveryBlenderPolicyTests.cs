using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core.Delivery;
using Xunit;

namespace Abl.Core.Tests;

public sealed class DeliveryBlenderPolicyTests
{
    [Theory]
    [InlineData("5.2.1", false, "reference")]
    [InlineData("5.2.1", true, "reference")]
    [InlineData("5.2.9 LTS", false, "supported")]
    [InlineData("5.2.9 LTS", true, "supported")]
    [InlineData("4.5.3", true, "unsupported")]
    [InlineData("custom build", true, "unknown")]
    public void CaptureUsesEffectiveFlagIndependentlyOfSupport(string version, bool allow, string support)
    {
        var captured = DeliveryBlenderPolicy.Capture(version, new() { AllowUnsupportedBlender = allow });
        Assert.Equal(new("5.2.x", "5.2.1", support, allow), captured);
    }

    [Theory]
    [InlineData("4.5.3")]
    [InlineData("custom build")]
    [InlineData("")]
    public void CaptureCannotPublishRefusedOrMissingVersionAsDisabledOverride(string version)
    {
        var error = Assert.Throws<AblException>(() => DeliveryBlenderPolicy.Capture(version, new()));
        Assert.Equal("worker_error", error.Kind);
    }

    [Fact]
    public void PolicyHasPinnedBytesAndMemberOrderInBothSerializers()
    {
        var policy = new DeliveryBlenderPolicy("5.2.x", "5.2.1", "reference", false);
        const string expected = "{\"supportedLine\":\"5.2.x\",\"referenceVersion\":\"5.2.1\",\"support\":\"reference\",\"allowUnsupported\":false}";
        Assert.Equal(expected, JsonSerializer.Serialize(policy));
        Assert.Equal(expected, JsonSerializer.SerializeToNode(policy)!.ToJsonString());
        var descriptor = Sample(policy);
        var text = DeliveryJson.Serialize(descriptor);
        using var json = JsonDocument.Parse(text);
        var actual = json.RootElement.GetProperty("producer").GetProperty("blenderPolicy");
        Assert.Equal(["supportedLine", "referenceVersion", "support", "allowUnsupported"], actual.EnumerateObject().Select(p => p.Name));
        Assert.Equal(expected, JsonNode.Parse(actual.GetRawText())!.ToJsonString());
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
        Assert.Equal(text, DeliveryJson.Serialize(DeliveryJson.Deserialize(text)!));
        Assert.Equal(text, DeliveryJson.Serialize(descriptor));
    }

    [Fact]
    public void HistoricalProducerSerializesExactlyAsBeforeWithoutNullOrFalseDefault()
    {
        const string expected = """
            {"name":"agentblender","blenderVersion":"5.2.1","build":"","blenderBuildHash":"","hostAssemblySha256":"","workerSourceSha256":""}
            """;
        var text = DeliveryJson.Serialize(Sample(null));
        var producer = JsonNode.Parse(text)!["producer"]!;
        Assert.Equal(expected, producer.ToJsonString());
        Assert.DoesNotContain("blenderPolicy", text);
        var back = DeliveryJson.Deserialize(text)!;
        Assert.Null(back.Producer.BlenderPolicy);
        Assert.Equal(text, DeliveryJson.Serialize(back));
    }

    private static DeliveryDescriptor Sample(DeliveryBlenderPolicy? policy) => new(2, "hero",
        new("agentblender", "5.2.1") { BlenderPolicy = policy }, AgdStillDescriptor.Profile,
        DateTimeOffset.Parse("2026-09-29T00:00:00Z"), []);

    [Theory]
    [InlineData("0.0.x", "0.0.0", "0.0.0", "reference", false)]
    [InlineData("999999.999999.x", "999999.999999.999999", "999999.999999.999999", "reference", false)]
    [InlineData("4.5.x", "4.5.7", "4.5.7", "reference", false)]
    [InlineData("4.5.x", "4.5.7", "4.5.3", "supported", false)]
    [InlineData("4.5.x", "4.5.7", "5.2.1", "unsupported", true)]
    [InlineData("5.2.x", "5.2.1", "  Blender 5.2.1 LTS  ", "reference", false)]
    [InlineData("5.2.x", "5.2.1", "05.02.01 custom", "reference", false)]
    [InlineData("5.2.x", "5.2.1", "5.2.1234567", "supported", false)]
    [InlineData("5.2.x", "5.2.1", "blender 5.2.1", "unknown", true)]
    [InlineData("5.2.x", "5.2.1", "1000000.2.1", "unknown", true)]
    [InlineData("5.2.x", "5.2.1", "5.1000000.1", "unknown", true)]
    [InlineData("5.2.x", "5.2.1", "5.2", "unknown", true)]
    [InlineData("5.2.x", "5.2.1", "custom", "unknown", true)]
    [InlineData("5.2.x", "5.2.1", "٥.2.1", "unsupported", true)]
    public void RecordedGrammarAndGateParserBoundariesAccept(string line, string reference, string version, string support, bool allow)
        => Assert.True(new DeliveryBlenderPolicy(line, reference, support, allow).IsConsistent(version));

    [Theory]
    [InlineData("05.2.x", "5.2.1")]
    [InlineData("5.02.x", "5.2.1")]
    [InlineData("5.2.X", "5.2.1")]
    [InlineData("5.2.x\n", "5.2.1")]
    [InlineData(" 5.2.x", "5.2.1")]
    [InlineData("٥.2.x", "5.2.1")]
    [InlineData("+5.2.x", "5.2.1")]
    [InlineData("1000000.2.x", "5.2.1")]
    [InlineData("5.2.x", "05.2.1")]
    [InlineData("5.2.x", "5.2.01")]
    [InlineData("5.2.x", "5.2.1000000")]
    [InlineData("5.2.x", "5.2.1 LTS")]
    [InlineData("5.2.x", "5.2.1\n")]
    [InlineData("5.2.x", "Blender 5.2.1")]
    [InlineData("5.2.x", "5.2.١")]
    [InlineData("5.2.x", "5.3.1")]
    [InlineData("", "5.2.1")]
    [InlineData("5.2.x", "")]
    public void NoncanonicalOrOutOfBoundsRecordedVersionsReject(string line, string reference)
        => Assert.False(new DeliveryBlenderPolicy(line, reference, "reference", true).IsConsistent("5.2.1"));

    [Fact]
    public void ProducingVersionRetainsItsNonblank128CharacterBound()
    {
        var policy = new DeliveryBlenderPolicy("5.2.x", "5.2.1", "unknown", true);
        Assert.True(policy.IsConsistent(new string('x', 128)));
        Assert.False(policy.IsConsistent(new string('x', 129)));
        Assert.False(policy.IsConsistent("  "));
        Assert.False(policy.IsConsistent(null));
    }
}
