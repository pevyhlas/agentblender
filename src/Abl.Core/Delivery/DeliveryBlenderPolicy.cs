using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Abl.Core.Config;

namespace Abl.Core.Delivery;

/// <summary>Recorded producer facts, not a verifier's current support policy.</summary>
[JsonConverter(typeof(DeliveryBlenderPolicyConverter))]
public sealed partial record DeliveryBlenderPolicy(
    string SupportedLine, string ReferenceVersion, string Support, bool AllowUnsupported)
{
    internal static DeliveryBlenderPolicy Capture(string producingVersion, AblOptions options)
    {
        var version = BlenderVersion.Parse(producingVersion);
        var policy = new DeliveryBlenderPolicy(SupportedBlender.Line, SupportedBlender.Reference.ToString(),
            version is null ? "unknown" : SupportedBlender.Name(SupportedBlender.Classify(version)),
            options.AllowUnsupportedBlender);
        if (!policy.IsConsistent(producingVersion))
            throw new AblException("worker_error", "producing Blender version/policy is inconsistent with effective options");
        return policy;
    }

    // Offline validation deliberately uses only the recorded line/reference and the shared
    // worker-gate parser. In particular, it must never call SupportedBlender.Classify.
    internal bool IsConsistent(string? producingVersion)
    {
        if (SupportedLine is not { Length: <= 15 } || !LinePattern().IsMatch(SupportedLine) ||
            ReferenceVersion is not { Length: <= 20 } || !ReferencePattern().IsMatch(ReferenceVersion) ||
            ReferenceVersion[..ReferenceVersion.LastIndexOf('.')] + ".x" != SupportedLine ||
            string.IsNullOrWhiteSpace(producingVersion) || producingVersion.Length > 128)
            return false;
        var reference = BlenderVersion.Parse(ReferenceVersion)!;
        var version = BlenderVersion.Parse(producingVersion);
        var expected = version is null ? "unknown"
            : version == reference ? "reference"
            : version.Major == reference.Major && version.Minor == reference.Minor ? "supported"
            : "unsupported";
        return Support == expected && (expected is "reference" or "supported" || AllowUnsupported);
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.x\z")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\z")]
    private static partial Regex ReferencePattern();
}

/// <summary>The same closed, presence-aware object reader in receipts and snapshots.</summary>
internal sealed class DeliveryBlenderPolicyConverter : JsonConverter<DeliveryBlenderPolicy>
{
    public override bool HandleNull => true; // Explicit null is not historical absence.

    public override DeliveryBlenderPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
        {
            if (!members.Add(member.Name) || member.Name is not ("supportedLine" or "referenceVersion" or "support" or "allowUnsupported"))
                throw Invalid();
        }
        if (members.Count != 4) throw Invalid();
        var flag = root.GetProperty("allowUnsupported");
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        return new(Text("supportedLine"), Text("referenceVersion"), Text("support"), flag.GetBoolean());

        string Text(string name) => root.GetProperty(name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()! : throw Invalid();
    }

    public override void Write(Utf8JsonWriter writer, DeliveryBlenderPolicy value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("supportedLine", value.SupportedLine);
        writer.WriteString("referenceVersion", value.ReferenceVersion);
        writer.WriteString("support", value.Support);
        writer.WriteBoolean("allowUnsupported", value.AllowUnsupported);
        writer.WriteEndObject();
    }

    private static JsonException Invalid() => new("invalid producing Blender policy object");
}
