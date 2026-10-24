using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abl.Core.Delivery;

/// <summary>Bounded host-only verification of a completed producer generation, not admission.</summary>
public static class DeliveryPayload
{
    public static DeliveryVerifyResult Verify(string directory)
    {
        var descriptor = ReadDescriptor(directory);
        var item = descriptor.Items[0];
        var payload = Path.Combine(directory, DeliveryService.PayloadDirName);
        var problems = new List<string>();
        try
        {
            RefuseLinks(payload);
            var entries = Directory.EnumerateFileSystemEntries(payload).Take(3).ToArray();
            if (entries.Length != 2 || entries.Any(p => Path.GetFileName(p) is not (AgdStillDescriptor.FileName or AgdStillDescriptor.ImageName)))
                problems.Add("payload must contain exactly artifact.json and still.png");
            var metadata = ReadBounded(Path.Combine(payload, AgdStillDescriptor.FileName), 1024 * 1024);
            var expected = AgdStillDescriptor.Serialize(descriptor.Name, item, descriptor.Producer.Build, descriptor.RecipeSnapshotSha256);
            if (!metadata.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)) || AgdStillDescriptor.Hash(metadata) != descriptor.DescriptorSha256)
                problems.Add("AGD metadata differs from the producer's bound still claims");
            var image = ReadBounded(Path.Combine(payload, item.File), DeliveryService.MaxImageBytes);
            if (image.LongLength != item.Bytes || AgdStillDescriptor.Hash(image) != item.Sha256)
                problems.Add("PNG length/hash mismatch");
            var header = PngReader.Parse(image);
            if (header.Width != item.Framing.Width || header.Height != item.Framing.Height)
                problems.Add("PNG framing mismatch");
            var recipe = ReadBounded(Path.Combine(directory, "recipe-snapshot.json"), 8 * 1024 * 1024);
            if (AgdStillDescriptor.Hash(recipe) != descriptor.RecipeSnapshotSha256)
                problems.Add("preserved recipe snapshot hash mismatch");
            RejectDuplicates(recipe);
            var snapshot = JsonNode.Parse(recipe) as JsonObject ?? throw Bad("invalid recipe snapshot");
            var frozen = ReadBounded(Path.Combine(directory, DeliveryService.ReplaySourceDirName, descriptor.Name, "artifact.json"), 4 * 1024 * 1024);
            if (snapshot["profile"]?.GetValue<string>() != "abl.still-recipe-snapshot.v1" ||
                !JsonNode.DeepEquals(snapshot["frozenArtifact"], JsonNode.Parse(frozen)) ||
                snapshot["frozenArtifact"]?["baseCheckpoint"]?.GetValue<string>() != item.SourceCheckpoint ||
                snapshot["frozenArtifact"]?["name"]?.GetValue<string>() != descriptor.Name ||
                !JsonNode.DeepEquals(snapshot["overlay"], item.Recipe.Params) ||
                !JsonNode.DeepEquals(snapshot["render"], item.Render.EffectiveSettings) ||
                !JsonNode.DeepEquals(snapshot["renderDeclaration"], JsonSerializer.SerializeToNode(item.Render, JsonSerializerOptions.Web)) ||
                snapshot["producerBuild"]?.GetValue<string>() != descriptor.Producer.Build ||
                snapshot["blenderVersion"]?.GetValue<string>() != descriptor.Producer.BlenderVersion ||
                snapshot["blenderBuildHash"]?.GetValue<string>() != descriptor.Producer.BlenderBuildHash ||
                snapshot["hostAssemblySha256"]?.GetValue<string>() != descriptor.Producer.HostAssemblySha256 ||
                snapshot["workerSourceSha256"]?.GetValue<string>() != descriptor.Producer.WorkerSourceSha256)
                problems.Add("recipe/source/settings claims disagree with the preserved snapshot");
            var basis = ReadBounded(Path.Combine(directory, DeliveryService.ReplaySourceDirName, descriptor.Name, "base.blend"), 256L * 1024 * 1024);
            if (AgdStillDescriptor.Hash(basis) != item.SourceCheckpoint)
                problems.Add("preserved base checkpoint hash mismatch");
        }
        catch (Exception ex) when (ex is AblException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            problems.Add(ex is AblException a ? a.Message : "delivery file missing or unreadable");
        }
        return new(problems.Count == 0, 1, problems,
            problems.Count == 0 ? null : "restore this generation or export a new one; verification does not repair files");
    }

    public static DeliveryListResult List(string directory)
    {
        var d = ReadDescriptor(directory);
        return new(d.Name, d.Profile, d.Producer.BlenderVersion,
            [.. d.Items.Select(i => new DeliveryListItem(i.File, i.Framing.Width, i.Framing.Height, i.Sha256, i.SceneDigest, i.Recipe.Artifact))]);
    }

    public static DeliveryDescriptor ReadDescriptor(string directory)
    {
        try { return ReadDescriptorCore(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AblException("io_error", "delivery receipt is unreadable",
                hint: "restore the generation or correct its path/access before retrying");
        }
    }

    private static DeliveryDescriptor ReadDescriptorCore(string directory)
    {
        var bytes = ReadBounded(Path.Combine(directory, DeliveryService.DescriptorName), 1024 * 1024);
        RejectDuplicates(bytes);
        DeliveryDescriptor? d;
        try { d = DeliveryJson.Deserialize(Encoding.UTF8.GetString(bytes)); }
        catch (JsonException) { throw Bad("invalid producer receipt"); }
        if (d is null || d.FormatVersion != 2 || d.Profile != AgdStillDescriptor.Profile || !Id(d.Name) ||
            d.Producer is null || d.Producer.Name != "agentblender" || !Version(d.Producer.Build) ||
            string.IsNullOrWhiteSpace(d.Producer.BlenderVersion) || d.Producer.BlenderVersion.Length > 128 ||
            !Hex(d.Producer.BlenderBuildHash, 12) || !Hex(d.DescriptorSha256, 64) || !Hex(d.RecipeSnapshotSha256, 64) ||
            !Hex(d.Producer.HostAssemblySha256, 64) || !Hex(d.Producer.WorkerSourceSha256, 64) ||
            d.SceneDigestCoverage != "r3-object-transform-mesh-basic-camera-light" ||
            d.Items is null || d.Items.Count != 1 || d.Items[0] is not { } item ||
            item.File != AgdStillDescriptor.ImageName || item.Bytes is < 45 or > DeliveryService.MaxImageBytes ||
            !Hex(item.Sha256, 64) || !Hex(item.SourceCheckpoint, 64) || !Hex(item.SceneDigest, 64) ||
            item.Framing is null || item.Framing.Width is < 1 or > 4096 || item.Framing.Height is < 1 or > 4096 ||
            item.Recipe is null || item.Recipe.Artifact != d.Name || item.Render is null ||
            item.Render.ColorSpace != "srgb" || item.Render.Alpha != "straight" || item.Render.EffectiveSettings is null ||
            item.Render.Engine is not ("eevee" or "cycles_cpu" or "cycles_gpu") || item.Render.Res is < 1 or > 4096 ||
            item.Render.Samples is null or < 1 or > 4096 || item.Render.Seed < 0 ||
            item.Framing.Width != item.Render.Res || item.Framing.Height != item.Render.Res)
            throw Bad("unsupported or incomplete single-still producer receipt");
        return d;
    }

    internal static bool Id(string? value) => value is { Length: > 0 and <= 64 } && value[0] is >= 'a' and <= 'z' &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    private static bool Version(string? value) => value is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '_' or '-');
    private static bool Hex(string? value, int size) => value?.Length == size && value.All(char.IsAsciiHexDigitLower);

    public static byte[] ReadBounded(string path, long limit)
    {
        RefuseLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0 || info.Length < 1 || info.Length > limit || info.Length > int.MaxValue)
            throw Bad("delivery input must be a bounded nonempty regular file");
        using var input = File.OpenRead(path);
        var bytes = new byte[(int)info.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw Bad("delivery input changed during capture");
        return bytes;
    }

    /// <summary>
    /// Refuses a delivery path with a symlink or reparse point in any component, except a
    /// system alias (008 T020): a symlink whose containing directory is owned by root and
    /// not writable by group or others, so only root could have placed or replaced it, such
    /// as macOS <c>/tmp</c>, <c>/var</c> and <c>/etc</c> → <c>/private/…</c>. A link the
    /// user or anyone else could create, anywhere in the path, is still refused.
    /// </summary>
    internal static void RefuseLinks(string path) => RefuseLinks(path, IsSystemAliasContainer);

    internal static void RefuseLinks(string path, Func<string, bool> systemAliasContainer)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0 &&
                    (Path.GetDirectoryName(part) is not { } container || !systemAliasContainer(container)))
                    throw Bad("delivery paths must not contain symlinks/reparse points");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// Root-owned, not group/other-writable and (macOS) without an <c>allow</c> ACL entry, the
    /// discovery trust rule's tests; followed, as the walk resolves it. Never on Windows.
    /// </summary>
    internal static bool IsSystemAliasContainer(string directory)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            const UnixFileMode othersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            var facts = Config.UnixConfigFileSystem.Instance.Inspect(directory);
            return facts is { TargetExists: true, IsDirectory: true, OwnerUserId: 0, HasAllowAcl: false }
                && (facts.Mode & othersWrite) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RejectDuplicates(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 24 });
            var properties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!)) throw Bad("duplicate producer receipt property");
            }
        }
        catch (JsonException) { throw Bad("invalid producer receipt JSON"); }
    }
    private static AblException Bad(string text) => new("bad_request", text);
}
