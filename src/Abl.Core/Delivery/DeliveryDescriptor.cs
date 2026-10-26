using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abl.Core.Delivery;

/// <summary>What produced a delivery — the AgentBlender build and its Blender.</summary>
public sealed record DeliveryProducer(string Name, string? BlenderVersion)
{
    public string Build { get; init; } = "";
    public string BlenderBuildHash { get; init; } = "";
    public string HostAssemblySha256 { get; init; } = "";
    public string WorkerSourceSha256 { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DeliveryBlenderPolicy? BlenderPolicy { get; init; }
}

/// <summary>The recipe that made one delivered image: the frozen artifact + the variant's params.</summary>
public sealed record DeliveryRecipe(string Artifact, JsonObject? Params);

/// <summary>The render settings that produced one delivered image (declares the profile's colour/alpha).</summary>
public sealed record DeliveryRender(
    string Engine, int Res, int? Samples, int Seed, string ColorSpace, string Alpha)
{
    public JsonObject? EffectiveSettings { get; init; }
}

/// <summary>Canvas facts read back from the delivered PNG (FR-002 framing).</summary>
public sealed record DeliveryFraming(int Width, int Height);

/// <summary>
/// One delivered image, bound to its provenance (FR-002). <see cref="Sha256"/> + <see cref="Bytes"/>
/// guard delivery integrity (a mangled/edited file is caught offline); <see cref="SceneDigest"/> is
/// the R-03 replay-truth link (the canonical scene digest, computed on the host, never re-derived
/// by the consumer).
/// </summary>
public sealed record DeliveryItem(
    string File,
    long Bytes,
    string Sha256,
    string SourceCheckpoint,
    string SceneDigest,
    DeliveryRecipe Recipe,
    DeliveryRender Render,
    DeliveryFraming Framing);

/// <summary>
/// The delivery descriptor (`delivery.json`) — the strict payload directory holds this file plus
/// only the declared PNGs (FR-003). Serialized with the same deterministic byte policy as 002
/// bundles (fixed member order, indented, LF, relaxed escaping) so re-exports diff clean.
/// </summary>
public sealed record DeliveryDescriptor(
    int FormatVersion,
    string Name,
    DeliveryProducer Producer,
    string Profile,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DeliveryItem> Items)
{
    public string DescriptorSha256 { get; init; } = "";
    public string RecipeSnapshotSha256 { get; init; } = "";
    public string SceneDigestCoverage { get; init; } = "r3-object-transform-mesh-basic-camera-light";
}

/// <summary>Serialization for delivery descriptors — deterministic bytes, matching the 002 bundle policy.</summary>
public static class DeliveryJson
{
    public const int FormatVersion = 2;

    /// <summary>The declared profile string recorded in the descriptor.</summary>
    public const string ProfileTag = AgdStillDescriptor.Profile;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
    };

    /// <summary>Deterministic bytes: unchanged descriptors re-serialize identically (trailing LF).</summary>
    public static string Serialize(DeliveryDescriptor descriptor) =>
        JsonSerializer.Serialize(descriptor, Options) + "\n";

    public static DeliveryDescriptor? Deserialize(string json) =>
        JsonSerializer.Deserialize<DeliveryDescriptor>(json, ReadOptions);
}
