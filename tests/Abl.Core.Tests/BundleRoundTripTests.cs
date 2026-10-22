using System.Security.Cryptography;
using System.Text;
using Abl.Core;
using Abl.Core.Store;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// Feature 002 FR-001..004: bundle export/import on the store level, with fabricated
/// .blend bytes (no Blender needed) — round-trip, determinism, integrity, idempotence.
/// </summary>
public sealed class BundleRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"abl-bundle-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CheckpointStore NewStore(string name) => new(Path.Combine(_root, name));

    private static string IngestFakeBlend(CheckpointStore store, string content)
    {
        var tmp = store.TmpBlendPath();
        File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes(content));
        return store.Ingest(tmp, parent: null, sessionId: "s1", digest: "d1", blenderVersion: "5.2.1");
    }

    private static FrozenArtifact FreezeFake(CheckpointStore store, string baseHash, string name)
    {
        var artifact = new FrozenArtifact(
            name, baseHash,
            [new FrozenOp("import bpy\nprint(params['size'])", new() { ["size"] = 2 })],
            baseHash, "digest-expected", DateTimeOffset.Parse("2026-09-12T10:00:00Z"), "5.2.1");
        store.SaveFrozen(artifact);
        return artifact;
    }

    [Fact]
    public void RoundTripIntoFreshStoreReconstitutesTheArtifact()
    {
        var source = NewStore("source");
        var baseHash = IngestFakeBlend(source, "fake blend bytes");
        FreezeFake(source, baseHash, "hero");

        var paths = source.ExportBundle("hero", Path.Combine(_root, "repo"));
        Assert.True(File.Exists(paths.ArtifactJson));
        Assert.True(File.Exists(paths.BaseBlend));
        Assert.Equal(2, Directory.GetFiles(paths.Directory).Length); // SC-002: exactly 2 files

        var dest = NewStore("dest");
        var imported = dest.ImportBundle(paths.Directory);

        Assert.Equal("hero", imported.Name);
        Assert.Equal(baseHash, imported.BaseCheckpoint);
        Assert.Equal("digest-expected", imported.ExpectedDigest);
        Assert.Equal("5.2.1", imported.BlenderVersion);
        Assert.Single(imported.Ops);
        Assert.Contains("params['size']", imported.Ops[0].Script);
        // The object landed content-addressed and byte-identical.
        Assert.Equal(
            File.ReadAllBytes(source.ObjectPath(baseHash)),
            File.ReadAllBytes(dest.ObjectPath(baseHash)));
        // And is registered with provenance meta.
        var meta = dest.Get(baseHash);
        Assert.NotNull(meta);
        Assert.Equal("import", meta!.SessionId);
        Assert.Contains("imported", meta.Tags);
    }

    [Fact]
    public void ReExportIsByteIdentical()
    {
        var store = NewStore("det");
        var baseHash = IngestFakeBlend(store, "stable bytes");
        FreezeFake(store, baseHash, "stable");

        var first = store.ExportBundle("stable", Path.Combine(_root, "out1"));
        var second = store.ExportBundle("stable", Path.Combine(_root, "out2"));
        Assert.Equal(File.ReadAllBytes(first.ArtifactJson), File.ReadAllBytes(second.ArtifactJson));
        Assert.Equal(File.ReadAllBytes(first.BaseBlend), File.ReadAllBytes(second.BaseBlend));
        // LF-only, trailing newline — git-clean text (FR-002).
        var text = File.ReadAllText(first.ArtifactJson);
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void ReImportIsANoOpAndDifferentContentIsRefused()
    {
        var source = NewStore("source2");
        var baseHash = IngestFakeBlend(source, "blend v1");
        FreezeFake(source, baseHash, "twice");
        var paths = source.ExportBundle("twice", Path.Combine(_root, "repo2"));

        var dest = NewStore("dest2");
        dest.ImportBundle(paths.Directory);
        dest.ImportBundle(paths.Directory); // FR-004: identical re-import = clean no-op

        // A locally different artifact under the same name must not be overwritten.
        var conflicting = new FrozenArtifact(
            "twice", baseHash, [new FrozenOp("print('other')", null)], baseHash,
            "other-digest", DateTimeOffset.UtcNow);
        var dest3 = NewStore("dest3");
        dest3.SaveFrozen(conflicting);
        var ex = Assert.Throws<AblException>(() => dest3.ImportBundle(paths.Directory));
        Assert.Equal("bad_reference", ex.Kind);
    }

    [Fact]
    public void CorruptedBlendIsRefusedWithZeroStoreMutations()
    {
        var source = NewStore("source3");
        var baseHash = IngestFakeBlend(source, "pristine");
        FreezeFake(source, baseHash, "fragile");
        var paths = source.ExportBundle("fragile", Path.Combine(_root, "repo3"));

        // Simulate git/LFS mangling.
        File.AppendAllText(paths.BaseBlend, "\ncorruption");

        var dest = NewStore("dest4");
        var ex = Assert.Throws<AblException>(() => dest.ImportBundle(paths.Directory));
        Assert.Equal("bad_reference", ex.Kind);
        Assert.Contains("hashes to", ex.Message);
        // SC-003: nothing landed.
        Assert.Null(dest.GetFrozen("fragile"));
        Assert.Null(dest.Get(baseHash));
        Assert.Empty(dest.List());
    }

    [Fact]
    public void ArtifactJsonIsHumanReviewableWithPinnedKeyOrder()
    {
        var store = NewStore("golden");
        var baseHash = IngestFakeBlend(store, "golden bytes");
        FreezeFake(store, baseHash, "golden");

        var text = File.ReadAllText(
            store.ExportBundle("golden", Path.Combine(_root, "repo6")).ArtifactJson);
        // Script text survives verbatim — no \uXXXX escaping of quotes (SC-002 reviewability).
        Assert.Contains("print(params['size'])", text);
        // Top-level key order is part of the on-disk format: bundles already committed to
        // consumer repos must not reorder if the serializer or record shape drifts.
        var order = new[]
        {
            "\"formatVersion\"", "\"name\"", "\"baseCheckpoint\"", "\"ops\"",
            "\"expectedFinal\"", "\"expectedDigest\"", "\"blenderVersion\"", "\"createdAt\"",
        };
        var positions = order.Select(k => text.IndexOf(k, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p), positions);
    }

    [Fact]
    public void SymlinkedBundleEntryIsRefused()
    {
        Directory.CreateDirectory(_root);
        var secret = Path.Combine(_root, "victim-secret.txt");
        File.WriteAllText(secret, "AWS_SECRET=hunter2");

        var source = NewStore("source5");
        var baseHash = IngestFakeBlend(source, "real bytes");
        FreezeFake(source, baseHash, "linked");
        var paths = source.ExportBundle("linked", Path.Combine(_root, "repo7"));

        File.Delete(paths.BaseBlend);
        File.CreateSymbolicLink(paths.BaseBlend, secret);

        var dest = NewStore("dest7");
        var ex = Assert.Throws<AblException>(() => dest.ImportBundle(paths.Directory));
        Assert.Equal("bad_reference", ex.Kind);
        Assert.Contains("symlink", ex.Message);
        Assert.DoesNotContain("hashes to", ex.Message); // the victim file was never hashed
        Assert.Empty(dest.List());
    }

    [Fact]
    public void ExportRefusesToWriteThroughAPlantedSymlink()
    {
        Directory.CreateDirectory(_root);
        var victim = Path.Combine(_root, "victim-keep.txt");
        File.WriteAllText(victim, "do not truncate");

        var store = NewStore("source6");
        var baseHash = IngestFakeBlend(store, "outbound");
        FreezeFake(store, baseHash, "trap");

        var dest = Path.Combine(_root, "repo8");
        Directory.CreateDirectory(Path.Combine(dest, "trap"));
        File.CreateSymbolicLink(Path.Combine(dest, "trap", "artifact.json"), victim);

        var ex = Assert.Throws<AblException>(() => store.ExportBundle("trap", dest));
        Assert.Equal("bad_reference", ex.Kind);
        Assert.Equal("do not truncate", File.ReadAllText(victim));
    }

    [Fact]
    public void OversizedArtifactJsonIsRefusedBeforeReading()
    {
        var dir = Path.Combine(_root, "huge");
        Directory.CreateDirectory(dir);
        using (var f = File.Create(Path.Combine(dir, "artifact.json")))
        {
            f.SetLength(5 * 1024 * 1024); // sparse — the gate must fire on size alone
        }

        File.WriteAllBytes(Path.Combine(dir, "base.blend"), [1]);

        var dest = NewStore("dest8");
        var ex = Assert.Throws<AblException>(() => dest.ImportBundle(dir));
        Assert.Equal("bad_reference", ex.Kind);
        Assert.Contains("import cap", ex.Message);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"formatVersion\": 99, \"name\": \"x\", \"baseCheckpoint\": \"h\", \"ops\": [{\"script\": \"s\"}], \"expectedFinal\": \"h\", \"expectedDigest\": \"d\", \"createdAt\": \"2026-09-12T10:00:00Z\"}")]
    [InlineData("{\"formatVersion\": 1, \"name\": \"x\", \"baseCheckpoint\": \"h\", \"ops\": [], \"expectedFinal\": \"h\", \"expectedDigest\": \"d\", \"createdAt\": \"2026-09-12T10:00:00Z\"}")]
    [InlineData("{\"formatVersion\": 1, \"name\": \"x\", \"baseCheckpoint\": \"h\", \"ops\": [{\"script\": \"s\"}], \"expectedDigest\": \"d\", \"createdAt\": \"2026-09-12T10:00:00Z\"}")]
    [InlineData("{\"formatVersion\": 1, \"name\": \"x\", \"baseCheckpoint\": \"abc\", \"ops\": [{\"script\": \"s\"}], \"expectedFinal\": \"h\", \"expectedDigest\": \"d\", \"createdAt\": \"2026-09-12T10:00:00Z\"}")]
    public void MalformedArtifactJsonIsRefused(string json)
    {
        var dir = Path.Combine(_root, $"bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "artifact.json"), json);
        File.WriteAllBytes(Path.Combine(dir, "base.blend"), [1, 2, 3]);

        var dest = NewStore($"destm-{Guid.NewGuid():N}");
        var ex = Assert.Throws<AblException>(() => dest.ImportBundle(dir));
        Assert.Equal("bad_reference", ex.Kind);
        Assert.Empty(dest.List());
    }

    [Fact]
    public void MissingBundleFilesAreRefused()
    {
        var dir = Path.Combine(_root, "not-a-bundle");
        Directory.CreateDirectory(dir);
        var dest = NewStore("dest5");
        var ex = Assert.Throws<AblException>(() => dest.ImportBundle(dir));
        Assert.Equal("bad_reference", ex.Kind);
        Assert.NotNull(ex.Hint);
    }

    [Fact]
    public void ImportedBaseIsPruneProtected()
    {
        var source = NewStore("source4");
        var baseHash = IngestFakeBlend(source, "keep me");
        FreezeFake(source, baseHash, "keeper");
        var paths = source.ExportBundle("keeper", Path.Combine(_root, "repo4"));

        var dest = NewStore("dest6");
        dest.ImportBundle(paths.Directory);
        var result = dest.Prune(new HashSet<string>(StringComparer.Ordinal), force: true);
        Assert.Equal(0, result.Deleted);
        Assert.Contains(baseHash, result.Refused);
        Assert.True(File.Exists(dest.ObjectPath(baseHash)));
    }

    [Fact]
    public void ExportedBlendHashMatchesRecordedBaseCheckpoint()
    {
        var store = NewStore("hash");
        var baseHash = IngestFakeBlend(store, "hash me");
        FreezeFake(store, baseHash, "hashed");
        var paths = store.ExportBundle("hashed", Path.Combine(_root, "repo5"));

        using var stream = File.OpenRead(paths.BaseBlend);
        Assert.Equal(baseHash, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
}
