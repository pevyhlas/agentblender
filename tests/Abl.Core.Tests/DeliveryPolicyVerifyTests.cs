using System.Text.Json.Nodes;
using Abl.Core.Delivery;
using Xunit;

namespace Abl.Core.Tests;

public sealed class DeliveryPolicyVerifyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-policy-" + Guid.NewGuid().ToString("N"));
    private const string Reference = """
        {"supportedLine":"5.2.x","referenceVersion":"5.2.1","support":"reference","allowUnsupported":false}
        """;
    private const string Supported = """
        {"supportedLine":"5.2.x","referenceVersion":"5.2.0","support":"supported","allowUnsupported":false}
        """;
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData("5.2.1", "5.2.x", "5.2.1", "reference", false)]
    [InlineData("5.2.1", "5.2.x", "5.2.1", "reference", true)]
    [InlineData("5.2.9 LTS", "5.2.x", "5.2.1", "supported", false)]
    [InlineData("5.2.9 LTS", "5.2.x", "5.2.1", "supported", true)]
    [InlineData("4.5.3", "5.2.x", "5.2.1", "unsupported", true)]
    [InlineData("custom", "5.2.x", "5.2.1", "unknown", true)]
    [InlineData("4.5.7", "4.5.x", "4.5.7", "reference", false)]
    [InlineData("4.5.3", "4.5.x", "4.5.7", "supported", false)]
    [InlineData("5.2.1", "4.5.x", "4.5.7", "unsupported", true)]
    public void ConsistentClaimsUseHistoricalPolicyNotTodaysConstants(string version, string line, string reference, string support, bool allow)
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        var policy = new JsonObject
        {
            ["supportedLine"] = line, ["referenceVersion"] = reference, ["support"] = support, ["allowUnsupported"] = allow,
        }.ToJsonString();
        DeliveryGenerationFixture.SetPolicy(directory, policy, policy, version);
        var before = DeliveryGenerationFixture.Observe(directory);
        var result = DeliveryPayload.Verify(directory);
        Assert.True(result.Pass, string.Join(";", result.Problems));
        Assert.False(result.Admitted); Assert.False(result.ProvenanceVerified);
        Assert.False(result.PixelsDecoded); Assert.False(result.SourceReplayVerified);
        Assert.Equal(version, DeliveryPayload.List(directory).BlenderVersion);
        Assert.Equal(before, DeliveryGenerationFixture.Observe(directory));
    }

    [Theory]
    [InlineData("5.2.1 LTS")]
    [InlineData("4.5.3")]
    [InlineData("custom build")]
    public void HistoricalAbsenceIsUnknownAndIsNeverRewritten(string version)
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, null, null, version);
        var before = DeliveryGenerationFixture.Observe(directory);
        Assert.Null(DeliveryPayload.ReadDescriptor(directory).Producer.BlenderPolicy);
        Assert.True(DeliveryPayload.Verify(directory).Pass);
        Assert.Single(DeliveryPayload.List(directory).Items);
        Assert.Equal(before, DeliveryGenerationFixture.Observe(directory));
        Assert.DoesNotContain("blenderPolicy", File.ReadAllText(Path.Combine(directory, "recipe-snapshot.json")));
    }

    public static TheoryData<string> MalformedPolicies => new()
    {
        "null", "true", "[]", "\"reference\"", "5", "{}",
        "{\"supportedLine\":\"5.2.x\",\"referenceVersion\":\"5.2.1\",\"support\":\"reference\"}",
        "{\"supportedLine\":\"5.2.x\",\"support\":\"reference\",\"allowUnsupported\":false}",
        "{\"referenceVersion\":\"5.2.1\",\"support\":\"reference\",\"allowUnsupported\":false}",
        "{\"supportedLine\":\"5.2.x\",\"referenceVersion\":\"5.2.1\",\"allowUnsupported\":false}",
        Reference.Replace("false", "null", StringComparison.Ordinal),
        Reference.Replace("false", "\"false\"", StringComparison.Ordinal),
        Reference.Replace("false", "0", StringComparison.Ordinal),
        Reference.Replace("\"5.2.x\"", "null", StringComparison.Ordinal),
        Reference.Replace("\"5.2.x\"", "52", StringComparison.Ordinal),
        Reference.Replace("\"5.2.1\"", "[]", StringComparison.Ordinal),
        Reference.Replace("\"5.2.1\"", "null", StringComparison.Ordinal),
        Reference.Replace("\"reference\"", "{}", StringComparison.Ordinal),
        Reference.Replace("\"reference\"", "null", StringComparison.Ordinal),
        Reference.Replace("\"reference\"", "\"REFERENCE\"", StringComparison.Ordinal),
        Reference.Replace("\"reference\"", "\"future\"", StringComparison.Ordinal),
        Reference.Replace("\"support\":", "\"Support\":", StringComparison.Ordinal),
        Reference.Replace("}", ",\"extra\":true}", StringComparison.Ordinal),
        Reference.Replace("}", ",\"allowUnsupported\":false}", StringComparison.Ordinal),
        Reference.Replace("}", ",\"support\":\"reference\"}", StringComparison.Ordinal),
        Reference.Replace("}", ",\"supportedLine\":\"5.2.x\"}", StringComparison.Ordinal),
        Reference.Replace("}", ",\"referenceVersion\":\"5.2.1\"}", StringComparison.Ordinal),
        Reference.Replace("}", ",\"allow\\u0055nsupported\":false}", StringComparison.Ordinal),
        Reference.Replace("\"5.2.x\"", "\"05.2.x\"", StringComparison.Ordinal),
        Reference.Replace("\"5.2.1\"", "\"5.3.1\"", StringComparison.Ordinal),
        Reference.Replace("\"5.2.1\"", "\"5.2.01\"", StringComparison.Ordinal),
        Reference.Replace("\"5.2.x\"", "\"1000000.2.x\"", StringComparison.Ordinal),
    };

    [Theory]
    [MemberData(nameof(MalformedPolicies))]
    public void MalformedReceiptPolicyRejectsWithTypedError(string policy)
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, policy, Reference);
        var before = DeliveryGenerationFixture.Observe(directory);
        Assert.Equal("bad_request", Assert.Throws<AblException>(() => DeliveryPayload.Verify(directory)).Kind);
        Assert.Equal("bad_request", Assert.Throws<AblException>(() => DeliveryPayload.List(directory)).Kind);
        Assert.Equal(before, DeliveryGenerationFixture.Observe(directory));
    }

    [Theory]
    [MemberData(nameof(MalformedPolicies))]
    public void MalformedSnapshotPolicyRejectsEvenWithReboundHashes(string policy)
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, Reference, policy);
        AssertSemanticRejectionWithoutWrites(directory);
    }

    [Theory]
    [InlineData(null, Reference)]
    [InlineData(Reference, null)]
    [InlineData("null", "null")]
    [InlineData(Reference, Supported)]
    [InlineData(Supported, Reference)]
    public void MissingNullOrMismatchedPairRejects(string? receipt, string? snapshot)
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, receipt, snapshot);
        if (receipt == "null")
            Assert.Equal("bad_request", Assert.Throws<AblException>(() => DeliveryPayload.Verify(directory)).Kind);
        else AssertSemanticRejectionWithoutWrites(directory);
    }

    [Fact]
    public void EnabledFlagMustMatchEvenWhenBothClaimsAreIndividuallyValid()
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, Reference, Reference.Replace("false", "true", StringComparison.Ordinal));
        AssertSemanticRejectionWithoutWrites(directory);
    }

    [Fact]
    public void DifferentHistoricalLineOrReferenceMustMatchEvenWhenIndividuallyValid()
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, Supported, Supported.Replace("5.2.0", "5.2.2", StringComparison.Ordinal));
        Assert.Contains("receipt/snapshot Blender policy claims disagree", DeliveryPayload.Verify(directory).Problems);
        AssertSemanticRejectionWithoutWrites(directory);

        directory = DeliveryGenerationFixture.Write(_root);
        const string historical = """
            {"supportedLine":"4.5.x","referenceVersion":"4.5.7","support":"unsupported","allowUnsupported":true}
            """;
        DeliveryGenerationFixture.SetPolicy(directory, Reference.Replace("false", "true", StringComparison.Ordinal), historical);
        Assert.Contains("receipt/snapshot Blender policy claims disagree", DeliveryPayload.Verify(directory).Problems);
        AssertSemanticRejectionWithoutWrites(directory);
    }

    [Fact]
    public void ReadOrderDoesNotMatterAndValidClaimsAreNotRewritten()
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        const string reordered = """
            {"allowUnsupported":false,"support":"reference","referenceVersion":"5.2.1","supportedLine":"5.2.x"}
            """;
        DeliveryGenerationFixture.SetPolicy(directory, Reference, reordered);
        var before = DeliveryGenerationFixture.Observe(directory);
        Assert.True(DeliveryPayload.Verify(directory).Pass);
        Assert.Single(DeliveryPayload.List(directory).Items);
        Assert.Equal(before, DeliveryGenerationFixture.Observe(directory));
    }

    [Theory]
    [InlineData("5.2.1", "supported", false)]
    [InlineData("5.2.1", "unsupported", true)]
    [InlineData("5.2.9", "reference", false)]
    [InlineData("4.5.3", "supported", true)]
    [InlineData("4.5.3", "unsupported", false)]
    [InlineData("custom", "unknown", false)]
    [InlineData("custom", "reference", true)]
    public void IdenticalReboundClaimsStillMustAgreeWithVersionAndOverride(string version, string support, bool allow)
    {
        var policy = JsonNode.Parse(Reference)!;
        policy["support"] = support;
        policy["allowUnsupported"] = allow;
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, policy.ToJsonString(), policy.ToJsonString(), version);
        var before = DeliveryGenerationFixture.Observe(directory);
        var error = Assert.Throws<AblException>(() => DeliveryPayload.Verify(directory));
        Assert.Equal("bad_request", error.Kind);
        Assert.Contains("policy is inconsistent", error.Message);
        Assert.Equal("bad_request", Assert.Throws<AblException>(() => DeliveryPayload.List(directory)).Kind);
        Assert.Equal(before, DeliveryGenerationFixture.Observe(directory));
    }

    [Fact]
    public void PresentSnapshotIsValidatedEvenWhenReceiptPolicyIsAbsent()
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        DeliveryGenerationFixture.SetPolicy(directory, null, Reference.Replace("reference\"", "unsupported\"", StringComparison.Ordinal));
        var result = DeliveryPayload.Verify(directory);
        Assert.False(result.Pass);
        Assert.Contains(result.Problems, p => p.Contains("snapshot Blender policy is inconsistent", StringComparison.Ordinal));
        AssertSemanticRejectionWithoutWrites(directory);
    }

    [Theory]
    [InlineData("4.5.3", "unsupported")]
    [InlineData("custom build", "unknown")]
    public void SnapshotCannotDefaultANeededOverrideToFalse(string version, string support)
    {
        var directory = DeliveryGenerationFixture.Write(_root);
        var policy = JsonNode.Parse(Reference)!;
        policy["support"] = support;
        DeliveryGenerationFixture.SetPolicy(directory, null, policy.ToJsonString(), version);
        Assert.Contains("recipe snapshot Blender policy is inconsistent with its producing version", DeliveryPayload.Verify(directory).Problems);
        AssertSemanticRejectionWithoutWrites(directory);
    }

    private static void AssertSemanticRejectionWithoutWrites(string directory)
    {
        DeliveryGenerationFixture.AssertBindings(directory);
        var before = DeliveryGenerationFixture.Observe(directory);
        var result = DeliveryPayload.Verify(directory);
        Assert.False(result.Pass);
        Assert.NotEmpty(result.Problems);
        Assert.DoesNotContain(result.Problems, p => p.Contains("hash mismatch", StringComparison.Ordinal) || p.Contains("AGD metadata differs", StringComparison.Ordinal));
        Assert.Equal("bad_request", Assert.Throws<AblException>(() => DeliveryPayload.List(directory)).Kind);
        Assert.Equal(before, DeliveryGenerationFixture.Observe(directory));
    }
}
