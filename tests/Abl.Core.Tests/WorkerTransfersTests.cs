using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Abl.Core;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

public class WorkerTransfersTests
{
    private sealed class Peer : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"abl-blob-{Guid.NewGuid():N}");
        public WorkerHandle Worker { get; private set; } = null!;
        public bool CorruptHash { get; init; }
        public bool Aborted { get; private set; }
        public int LargestChunk { get; private set; }
        public string UploadPath => Path.Combine(Root, "uploaded.blend");
        public long Bytes { get; private set; }
        public string Hash { get; private set; } = "";
        private Task _loop = Task.CompletedTask;

        public async Task StartAsync()
        {
            Directory.CreateDirectory(Root);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var connect = socket.ConnectAsync(listener.LocalEndpoint);
            var accepted = await listener.AcceptSocketAsync();
            await connect;
            Worker = new WorkerHandle("test-remote", new WorkerConnection(socket), WorkerTier.Desktop)
            {
                State = WorkerState.Ready, SupportsBlobChunks = true,
            };
            _loop = ServeAsync(accepted);
        }

        private async Task ServeAsync(Socket socket)
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream? output = null;
            long offset = 0;
            try
            {
                while (true)
                {
                    var req = await WireCodec.ReadAsync(stream, default);
                    var result = new JsonObject { ["id"] = req["id"]!.DeepClone(), ["ok"] = true };
                    switch (req["op"]!.GetValue<string>())
                    {
                        case "blob_put_begin":
                            Bytes = req["bytes"]!.GetValue<long>();
                            Hash = req["sha256"]!.GetValue<string>();
                            // CreateNew, like the real worker's mkstemp: FileMode.Create makes .NET ftruncate the new
                            // file, so ext4 (auto_da_alloc) flushes all 72 MiB synchronously on close and stalls commit.
                            output = new FileStream(UploadPath, FileMode.CreateNew);
                            result["transfer_id"] = "upload";
                            result["chunk_bytes"] = WorkerTransfers.ChunkBytes;
                            break;
                        case "load_bytes":
                            var legacy = Convert.FromBase64String(req["blend_b64"]!.GetValue<string>());
                            Bytes = legacy.Length;
                            Hash = Convert.ToHexStringLower(SHA256.HashData(legacy));
                            await File.WriteAllBytesAsync(UploadPath, legacy);
                            if (req["keep"]?.GetValue<bool>() == true) result["path"] = "/worker/base.blend";
                            break;
                        case "blob_put_chunk":
                            Assert.Equal(offset, req["offset"]!.GetValue<long>());
                            var data = Convert.FromBase64String(req["data"]!.GetValue<string>());
                            LargestChunk = Math.Max(LargestChunk, data.Length);
                            Assert.InRange(data.Length, 1, WorkerTransfers.ChunkBytes);
                            await output!.WriteAsync(data);
                            hash.AppendData(data);
                            offset += data.Length;
                            result["offset"] = offset;
                            break;
                        case "blob_put_commit":
                            await output!.DisposeAsync();
                            output = null;
                            Assert.Equal(Bytes, offset);
                            Assert.Equal(Hash, Convert.ToHexStringLower(hash.GetHashAndReset()));
                            result["path"] = "/worker/base.blend";
                            break;
                        case "blob_get_chunk":
                            var at = req["offset"]!.GetValue<long>();
                            using (var input = File.OpenRead(UploadPath))
                            {
                                input.Position = at;
                                var buffer = new byte[WorkerTransfers.ChunkBytes];
                                var count = await input.ReadAsync(buffer);
                                result["offset"] = at;
                                result["data"] = Convert.ToBase64String(buffer, 0, count);
                            }
                            break;
                        case "blob_abort": Aborted = true; break;
                        case "blob_get_finish": break;
                        case "oversized_legacy":
                            await stream.WriteAsync(new byte[] { 4, 0, 0, 1 }); // 64 MiB + 1, no huge allocation
                            return;
                        case "malformed_id": result["id"] = "1"; break;
                        case "malformed_ok": result["ok"] = "yes"; break;
                        case "malformed_error": result["ok"] = false; result["error"] = "bad"; break;
                        case "malformed_kind":
                            result["ok"] = false;
                            result["error"] = new JsonObject { ["kind"] = true, ["message"] = "bad" };
                            break;
                        case "malformed_message":
                            result["ok"] = false;
                            result["error"] = new JsonObject { ["kind"] = "bad_request", ["message"] = new JsonArray() };
                            break;
                        case "malformed_traceback":
                            result["ok"] = false;
                            result["error"] = new JsonObject { ["kind"] = "bad_request", ["message"] = "bad", ["traceback"] = 3 };
                            break;
                        default: throw new InvalidOperationException(req.ToJsonString());
                    }

                    await WireCodec.WriteAsync(stream, result, default);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException) { }
            finally { if (output is not null) await output.DisposeAsync(); }
        }

        public JsonObject Descriptor() => new()
        {
            ["transfer_id"] = "download", ["bytes"] = Bytes,
            ["sha256"] = CorruptHash ? new string('0', 64) : Hash,
        };

        public async ValueTask DisposeAsync()
        {
            if (Worker is not null) await Worker.DisposeAsync();
            await _loop;
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<string> WriteSourceAsync(string root, int chunks)
    {
        var path = Path.Combine(root, "source.bin");
        var buffer = new byte[WorkerTransfers.ChunkBytes];
        new Random(42).NextBytes(buffer);
        await using var file = new FileStream(path, FileMode.CreateNew); // no truncate-triggered flush on close
        for (var i = 0; i < chunks; i++) await file.WriteAsync(buffer);
        return path;
    }

    [Fact(Timeout = 120_000)]
    public async Task UploadAndDownloadLargerThanFrameCapUseBoundedChunks()
    {
        await using var peer = new Peer();
        await peer.StartAsync();
        var source = await WriteSourceAsync(peer.Root, 72);
        Assert.True(new FileInfo(source).Length > WireCodec.MaxFrameBytes);
        await WorkerTransfers.LoadAsync(peer.Worker, source, true, TimeSpan.FromSeconds(5), default);
        var destination = Path.Combine(peer.Root, "result.png");
        await WorkerTransfers.DownloadAsync(peer.Worker, peer.Descriptor(), destination, TimeSpan.FromSeconds(5), default);
        using var received = File.OpenRead(destination);
        Assert.Equal(peer.Hash, Convert.ToHexStringLower(await SHA256.HashDataAsync(received)));
        Assert.Equal(72L * WorkerTransfers.ChunkBytes, received.Length);
        Assert.Equal(WorkerTransfers.ChunkBytes, peer.LargestChunk);
        Assert.Empty(Directory.GetFiles(peer.Root, "*.partial-*"));
    }

    [Fact(Timeout = 30_000)]
    public async Task CorruptDownloadNeverReplacesExistingDestination()
    {
        await using var peer = new Peer { CorruptHash = true };
        await peer.StartAsync();
        var source = await WriteSourceAsync(peer.Root, 2);
        await WorkerTransfers.LoadAsync(peer.Worker, source, true, TimeSpan.FromSeconds(5), default);
        var destination = Path.Combine(peer.Root, "selected.png");
        await File.WriteAllTextAsync(destination, "preserve-selected-output");
        await Assert.ThrowsAsync<WorkerOpException>(() =>
            WorkerTransfers.DownloadAsync(peer.Worker, peer.Descriptor(), destination, TimeSpan.FromSeconds(5), default));
        Assert.Equal("preserve-selected-output", await File.ReadAllTextAsync(destination));
        Assert.True(peer.Aborted);
        Assert.Empty(Directory.GetFiles(peer.Root, "*.partial-*"));
    }

    [Fact]
    public async Task SmallLegacyUploadAndPixelsRemainCompatible()
    {
        await using var peer = new Peer();
        await peer.StartAsync();
        peer.Worker.SupportsBlobChunks = false;
        var source = Path.Combine(peer.Root, "small.blend");
        byte[] data = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(source, data);
        var kept = await WorkerTransfers.LoadAsync(peer.Worker, source, true, TimeSpan.FromSeconds(5), default);
        Assert.Equal("/worker/base.blend", kept);
        Assert.Equal(data, await File.ReadAllBytesAsync(peer.UploadPath));
        var target = Path.Combine(peer.Root, "legacy.png");
        await WorkerTransfers.WritePixelsAsync(peer.Worker,
            new JsonObject { ["png_b64"] = Convert.ToBase64String(data) }, target, TimeSpan.FromSeconds(5), default);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.Empty(Directory.GetFiles(peer.Root, "*.partial-*"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("bytes")]
    [InlineData("hash")]
    public async Task MalformedDescriptorsAreStructuredErrors(string field)
    {
        await using var peer = new Peer();
        await peer.StartAsync();
        var descriptor = new JsonObject { ["transfer_id"] = "valid-id", ["bytes"] = 4L, ["sha256"] = new string('0', 64) };
        if (field == "id") descriptor["transfer_id"] = new string('x', 129);
        if (field == "bytes") descriptor["bytes"] = "not-a-number";
        if (field == "hash") descriptor["sha256"] = new JsonArray();
        await Assert.ThrowsAsync<WorkerOpException>(() => WorkerTransfers.DownloadAsync(peer.Worker, descriptor,
            Path.Combine(peer.Root, "target.png"), TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task OversizedLegacyUploadRequiresUpgradeBeforeReadingFile()
    {
        await using var peer = new Peer();
        await peer.StartAsync();
        peer.Worker.SupportsBlobChunks = false;
        var source = Path.Combine(peer.Root, "large.blend");
        using (var file = File.Create(source)) file.SetLength(WireCodec.MaxFrameBytes);
        var error = await Assert.ThrowsAsync<AblException>(() =>
            WorkerTransfers.LoadAsync(peer.Worker, source, false, TimeSpan.FromSeconds(5), default));
        Assert.Equal("worker_upgrade_required", error.Kind);
        Assert.Contains("update", error.Hint);
    }

    [Fact]
    public async Task LegacyOversizedResponseHasAnActionableUpgradeError()
    {
        await using var peer = new Peer();
        await peer.StartAsync();
        peer.Worker.SupportsBlobChunks = false;
        var failure = await Assert.ThrowsAsync<WorkerDeadException>(() =>
            peer.Worker.RequestAsync("oversized_legacy", null, TimeSpan.FromSeconds(5), default));
        Assert.True(WorkerErrors.TryLegacyFrameLimit(failure, false, out var upgrade));
        Assert.Equal("worker_upgrade_required", upgrade.Kind);
        Assert.Contains("bundled worker", upgrade.Hint);
        Assert.False(peer.Worker.IsAlive);
    }

    [Theory]
    [InlineData("malformed_id")]
    [InlineData("malformed_ok")]
    [InlineData("malformed_error")]
    [InlineData("malformed_kind")]
    [InlineData("malformed_message")]
    [InlineData("malformed_traceback")]
    public async Task MalformedEnvelopePoisonsAndKillsTheWorker(string op)
    {
        await using var peer = new Peer();
        await peer.StartAsync();
        var error = await Assert.ThrowsAsync<WorkerDeadException>(() =>
            peer.Worker.RequestAsync(op, null, TimeSpan.FromSeconds(5), default));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.False(peer.Worker.IsAlive);
    }
}
