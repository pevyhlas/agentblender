using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abl.Core.Workers;

/// <summary>
/// Worker-protocol framing: 4-byte big-endian length + UTF-8 JSON, both directions
/// (docs/worker-protocol.md).
/// </summary>
public static class WireCodec
{
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidDataException("control frame exceeds 64 MiB; bulk files must use chunk transfers");
        }
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<JsonObject> ReadAsync(
        Stream stream, CancellationToken ct, int maxBytes = MaxFrameBytes)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > (uint)maxBytes)
        {
            throw new InvalidDataException($"worker frame of {length} bytes exceeds the {maxBytes} limit");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidDataException("worker frame is not a JSON object");
    }
}
