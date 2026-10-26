using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abl.Core.Delivery;
using Abl.Core.Rendering;
using Abl.Core.Store;
using Abl.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>Actual ExportAsync, pool, framed transport and files; no Blender process required.</summary>
public sealed class DeliveryCaptureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abl-capture-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<FakeFrame> _frames = new();
    private readonly List<Task> _servers = [];
    private WorkerPool? _pool;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_pool is not null) await _pool.DisposeAsync();
        await Task.WhenAll(_servers).WaitAsync(TimeSpan.FromSeconds(10));
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private DeliveryService Create(string cached, string refreshed, string frozen, bool allow)
    {
        var options = new AblOptions { StoreDir = Path.Combine(_root, "store"), PoolFloor = 0, AllowUnsupportedBlender = allow };
        var store = new CheckpointStore(options.StoreDir);
        var basis = store.TmpBlendPath();
        File.WriteAllBytes(basis, [1, 2, 3]);
        var checkpoint = store.Ingest(basis, null, "test", digest: FakeWorkerProtocol.SceneDigest, blenderVersion: frozen);
        store.SaveFrozen(new("hero", checkpoint, [new("pass", new())], checkpoint, FakeWorkerProtocol.SceneDigest,
            DateTimeOffset.Parse("2026-09-29T00:00:00Z"), frozen));
        _pool = new(options, new WorkerSupervisor(options, NullLogger<WorkerSupervisor>.Instance), NullLogger<WorkerPool>.Instance)
        {
            Spawner = async ct =>
            {
                var script = new FakeWorkerScript { BlenderVersion = cached };
                var (handle, server) = await FakeWorkerProtocol.ConnectAsync("producer", script, _frames, false, ct);
                _servers.Add(server);
                Assert.Equal(cached, handle.BlenderVersion);
                script.BlenderVersion = refreshed; // The next hello disagrees with the cached handshake.
                return handle;
            },
        };
        return new(options, _pool, store, new GpuLease(), NullLogger<DeliveryService>.Instance);
    }

    [Theory]
    [InlineData("4.5.3", "5.2.1", false, "reference")]
    [InlineData("4.5.3", "5.2.1", true, "reference")]
    [InlineData("5.2.1", "5.2.9 LTS", false, "supported")]
    [InlineData("5.2.1", "5.2.9 LTS", true, "supported")]
    [InlineData("5.2.1", "4.5.3", true, "unsupported")]
    [InlineData("5.2.1", "custom build", true, "unknown")]
    public async Task FreshProducingHelloAloneDrivesBothFilesAndClassification(string cached, string refreshed, bool allow, string support)
    {
        var service = Create(cached, refreshed, refreshed, allow);
        var result = await service.ExportAsync("hero", Path.Combine(_root, "out"), [], "cycles_cpu", 1, 1, 0, 1, default);
        var directory = Path.GetDirectoryName(result.PayloadDir)!;
        var receipt = DeliveryPayload.ReadDescriptor(directory);
        Assert.Equal(refreshed, receipt.Producer.BlenderVersion);
        Assert.Equal(new("5.2.x", "5.2.1", support, allow), receipt.Producer.BlenderPolicy);
        var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "recipe-snapshot.json")))!;
        Assert.Equal(refreshed, snapshot["blenderVersion"]!.GetValue<string>());
        Assert.Equal(JsonSerializer.Serialize(receipt.Producer.BlenderPolicy), snapshot["blenderPolicy"]!.ToJsonString());
        Assert.Equal(["supportedLine", "referenceVersion", "support", "allowUnsupported"], snapshot["blenderPolicy"]!.AsObject().Select(p => p.Key));
        Assert.True(service.Verify(directory).Pass);
        Assert.Single(service.List(directory).Items);
        Assert.Equal(["artifact.json", "still.png"], Directory.GetFiles(result.PayloadDir).Select(Path.GetFileName).Order());
        Assert.DoesNotContain("blenderPolicy", File.ReadAllText(result.DescriptorPath));
        Assert.Equal(["hello", "warm", "load", "exec", "hello", "save", "render", "contact_sheet", "quit"], _frames.Select(f => f.Op));
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(_root, "out")), d => Path.GetFileName(d).StartsWith(".stage-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("5.2.1", "5.2.9", "5.2.1")]
    [InlineData("4.5.3", "5.2.1", "4.5.3")]
    public async Task FrozenVersionCheckUsesFreshHelloNotTheMatchingCachedVersion(string cached, string refreshed, string frozen)
    {
        var service = Create(cached, refreshed, frozen, true);
        var error = await Assert.ThrowsAsync<AblException>(() => service.ExportAsync("hero", Path.Combine(_root, "out"), [], "cycles_cpu", 1, 1, 0, 1, default));
        Assert.Equal("bad_request", error.Kind);
        Assert.Contains("differs from frozen source", error.Message);
        Assert.Equal(2, _frames.Count(f => f.Op == "hello"));
        Assert.DoesNotContain(_frames, f => f.Op == "render");
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "out")));
    }

    [Theory]
    [InlineData("4.5.3")]
    [InlineData("custom build")]
    public async Task RefreshedRefusedVersionCannotPublishWithDisabledOverride(string refreshed)
    {
        var service = Create("5.2.1", refreshed, refreshed, false);
        var error = await Assert.ThrowsAsync<AblException>(() => service.ExportAsync("hero", Path.Combine(_root, "out"), [], "cycles_cpu", 1, 1, 0, 1, default));
        Assert.Equal("worker_error", error.Kind);
        Assert.DoesNotContain(_frames, f => f.Op == "render");
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "out")));
    }
}
