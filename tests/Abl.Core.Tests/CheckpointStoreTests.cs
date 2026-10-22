using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Store;
using Xunit;

namespace Abl.Core.Tests;

public class CheckpointStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"abl-store-test-{Guid.NewGuid():N}");
    private readonly CheckpointStore _store;

    public CheckpointStoreTests() => _store = new CheckpointStore(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string IngestBytes(string content, string? parent = null, string session = "s1", string[]? tags = null)
    {
        var tmp = _store.TmpBlendPath();
        File.WriteAllText(tmp, content);
        return _store.Ingest(tmp, parent, session, tags);
    }

    [Fact]
    public void AddressingIsSha256OfBytes()
    {
        var hash = IngestBytes("hello blend");
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hello blend")));
        Assert.Equal(expected, hash);
        Assert.True(File.Exists(_store.ObjectPath(hash)));
        Assert.Equal("hello blend", File.ReadAllText(_store.ObjectPath(hash)));
    }

    [Fact]
    public void IdenticalBytesDeduplicate()
    {
        var first = IngestBytes("same");
        var second = IngestBytes("same");
        Assert.Equal(first, second);
        Assert.Single(_store.List());
    }

    [Fact]
    public void ParentLinksAndTagsPersist()
    {
        var parent = IngestBytes("base");
        var child = IngestBytes("child", parent, tags: ["v1"]);
        var meta = _store.Get(child)!;
        Assert.Equal(parent, meta.Parent);
        Assert.Equal(["v1"], meta.Tags);

        _store.AddTags(child, ["best", "v1"]);
        Assert.Equal(["v1", "best"], _store.Get(child)!.Tags);
    }

    [Fact]
    public void ResolvesPrefixesAndTags()
    {
        var hash = IngestBytes("target", tags: ["hero"]);
        IngestBytes("other");
        Assert.Equal(hash, _store.Resolve(hash[..10]));
        Assert.Equal(hash, _store.Resolve("hero"));
        Assert.Null(_store.Resolve("nonexistent"));
    }

    [Fact]
    public void PruneRefusesProtectedTaggedAndFrozenReferencedHashes()
    {
        var head = IngestBytes("live-head");
        var tagged = IngestBytes("tagged", tags: ["keep"]);
        var frozenBase = IngestBytes("frozen-base");
        var frozenFinal = IngestBytes("frozen-final");
        var garbage = IngestBytes("garbage");
        _store.SaveFrozen(new FrozenArtifact(
            "demo", frozenBase, [new FrozenOp("pass", null)], frozenFinal, "digest0", DateTimeOffset.UtcNow));

        var result = _store.Prune(new HashSet<string> { head });

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(_store.ObjectPath(garbage)));
        Assert.Contains(head, result.Refused);
        Assert.Contains(tagged, result.Refused);
        Assert.Contains(frozenBase, result.Refused);
        Assert.Contains(frozenFinal, result.Refused);

        // force removes tagged, still never frozen-referenced or protected
        var forced = _store.Prune(new HashSet<string> { head }, force: true);
        Assert.Equal(1, forced.Deleted);
        Assert.False(File.Exists(_store.ObjectPath(tagged)));
        Assert.True(File.Exists(_store.ObjectPath(frozenBase)));
        Assert.True(File.Exists(_store.ObjectPath(head)));
    }

    [Fact]
    public void JournalAppendsAndReadsBack()
    {
        var entry = new TrajectoryEntry(
            1, "exec", "import bpy", new JsonObject { ["size"] = 2 }, "abc", 12.5, DateTimeOffset.UtcNow);
        _store.AppendJournal("s1", entry);
        _store.AppendJournal("s1", entry with { Seq = 2, Script = "pass", Params = null });

        var read = _store.ReadJournal("s1");
        Assert.Equal(2, read.Count);
        Assert.Equal("import bpy", read[0].Script);
        Assert.Equal(2, (int)read[0].Params!["size"]!);
        Assert.Equal(2, read[1].Seq);
        Assert.Null(read[1].Params);
        Assert.Empty(_store.ReadJournal("nosuch"));
    }

    [Fact]
    public void FrozenArtifactsRoundTrip()
    {
        var artifact = new FrozenArtifact(
            "robot-v1", "base123",
            [new FrozenOp("import bpy", new JsonObject { ["n"] = 1 }), new FrozenOp("pass", null)],
            "final456", "digest789", DateTimeOffset.UtcNow);
        _store.SaveFrozen(artifact);
        var read = _store.GetFrozen("robot-v1")!;
        Assert.Equal("base123", read.BaseCheckpoint);
        Assert.Equal(2, read.Ops.Count);
        Assert.Equal(1, (int)read.Ops[0].Params!["n"]!);
        Assert.Equal("final456", read.ExpectedFinal);
        Assert.Equal("digest789", read.ExpectedDigest);
        Assert.Single(_store.ListFrozen());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void PathUnsafeReferencesAreRejected(string reference)
    {
        Assert.Throws<AblException>(() => _store.ObjectPath(reference));
    }
}
