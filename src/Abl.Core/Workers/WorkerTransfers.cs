using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Abl.Core.Workers;

/// <summary>Bounded, integrity-checked file transfers. The control-frame cap is not a file cap.</summary>
public static class WorkerTransfers
{
    public const int ChunkBytes = 1024 * 1024;
    public const string UpgradeHint = "update the desktop launcher together with its bundled worker/ to enable chunked transfers";
    private const long LegacyBytes = (WireCodec.MaxFrameBytes - 8192L) * 3 / 4;

    public static async Task<string?> LoadAsync(
        WorkerHandle worker, string path, bool keep, TimeSpan timeout, CancellationToken ct)
    {
        if (!worker.SupportsBlobChunks)
        {
            if (new FileInfo(path).Length > LegacyBytes)
            {
                throw new AblException("worker_upgrade_required", "desktop worker cannot accept this checkpoint with its legacy single-frame protocol",
                    hint: "update the desktop launcher and bundled worker scripts to a version supporting blob_chunks");
            }

            var legacy = await worker.RequestAsync("load_bytes", new JsonObject
            {
                ["blend_b64"] = Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)),
                ["keep"] = keep,
            }, timeout, ct).ConfigureAwait(false);
            return legacy["path"]?.GetValue<string>();
        }

        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ct).ConfigureAwait(false));
        source.Position = 0;
        var begin = await worker.RequestAsync("blob_put_begin", new JsonObject
        {
            ["bytes"] = source.Length, ["sha256"] = hash,
        }, timeout, ct).ConfigureAwait(false);
        var id = RequireId(begin);
        try
        {
            var buffer = new byte[ChunkBytes];
            long offset = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                var ack = await worker.RequestAsync("blob_put_chunk", new JsonObject
                {
                    ["transfer_id"] = id, ["offset"] = offset,
                    ["data"] = Convert.ToBase64String(buffer, 0, count),
                }, timeout, ct).ConfigureAwait(false);
                offset += count;
                if (RequireLong(ack, "offset") != offset) throw Invalid("upload acknowledgement has an incorrect offset");
            }

            var result = await worker.RequestAsync("blob_put_commit", new JsonObject
            {
                ["transfer_id"] = id, ["keep"] = keep,
            }, timeout, ct).ConfigureAwait(false);
            return keep ? RequireString(result, "path", 4096) : null;
        }
        catch
        {
            await AbortAsync(worker, id).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task WritePixelsAsync(
        WorkerHandle worker, JsonObject response, string destination, TimeSpan timeout, CancellationToken ct)
    {
        if (response["blob"] is not JsonObject blob)
        {
            var bytes = Convert.FromBase64String(RequireString(response, "png_b64", WireCodec.MaxFrameBytes));
            var stage = destination + $".partial-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllBytesAsync(stage, bytes, ct).ConfigureAwait(false);
                File.Move(stage, destination, overwrite: true);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
            return;
        }

        await DownloadAsync(worker, blob, destination, timeout, ct).ConfigureAwait(false);
    }

    public static async Task DownloadAsync(
        WorkerHandle worker, JsonObject blob, string destination, TimeSpan timeout, CancellationToken ct)
    {
        var id = RequireId(blob);
        var length = RequireLong(blob, "bytes");
        var expectedHash = RequireString(blob, "sha256", 64);
        if (length < 0 || expectedHash.Length != 64 || !expectedHash.All(char.IsAsciiHexDigitLower))
            throw Invalid("download descriptor has invalid length/SHA-256");
        // Only the host chooses this path; the worker's out/path values are never write targets.
        var stage = destination + $".partial-{Guid.NewGuid():N}";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long offset = 0;
                while (offset < length)
                {
                    var chunk = await worker.RequestAsync("blob_get_chunk", new JsonObject
                    {
                        ["transfer_id"] = id, ["offset"] = offset,
                    }, timeout, ct).ConfigureAwait(false);
                    var encoded = RequireString(chunk, "data", ((ChunkBytes + 2) / 3) * 4);
                    if (encoded.Length > ((ChunkBytes + 2) / 3) * 4) throw Invalid("download chunk exceeds 1 MiB");
                    var data = Convert.FromBase64String(encoded);
                    if (RequireLong(chunk, "offset") != offset || data.Length is 0 or > ChunkBytes
                        || data.Length > length - offset) throw Invalid("download chunk has invalid offset/length");
                    await output.WriteAsync(data, ct).ConfigureAwait(false);
                    hash.AppendData(data);
                    offset += data.Length;
                }

                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash)
                    throw Invalid("download SHA-256 verification failed");
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            await worker.RequestAsync("blob_get_finish", new JsonObject { ["transfer_id"] = id }, timeout, ct)
                .ConfigureAwait(false);
            File.Move(stage, destination, overwrite: true);
        }
        catch
        {
            await AbortAsync(worker, id).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
    }

    private static async Task AbortAsync(WorkerHandle worker, string id)
    {
        if (!worker.IsAlive) return; // disposal reaps connection-owned files
        try
        {
            await worker.RequestAsync("blob_abort", new JsonObject { ["transfer_id"] = id },
                TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkerDeadException or WorkerTimeoutException or WorkerOpException) { }
    }

    private static string RequireString(JsonObject obj, string field, int maxLength = 128) =>
        obj[field] is JsonValue value && value.TryGetValue<string>(out var text)
            && text is not null && text.Length <= maxLength
            ? text : throw Invalid($"transfer response has invalid {field}");

    private static long RequireLong(JsonObject obj, string field) =>
        obj[field] is JsonValue value && value.TryGetValue<long>(out var number)
            ? number : throw Invalid($"transfer response has invalid {field}");

    private static string RequireId(JsonObject obj)
    {
        var id = RequireString(obj, "transfer_id");
        return id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id : throw Invalid("transfer response has invalid transfer_id");
    }

    private static WorkerOpException Invalid(string message) => new("worker_error", message, null);
}
