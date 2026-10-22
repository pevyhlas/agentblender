using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

public class WireCodecTests
{
    [Fact]
    public async Task RoundTripsAMessage()
    {
        using var stream = new MemoryStream();
        var message = new JsonObject { ["id"] = 7, ["op"] = "exec", ["params"] = new JsonObject { ["size"] = 2 } };
        await WireCodec.WriteAsync(stream, message, CancellationToken.None);

        stream.Position = 0;
        var read = await WireCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(7, (int)read["id"]!);
        Assert.Equal("exec", (string)read["op"]!);
        Assert.Equal(2, (int)read["params"]!["size"]!);
    }

    [Fact]
    public async Task FrameLengthIsBigEndianByteCount()
    {
        using var stream = new MemoryStream();
        await WireCodec.WriteAsync(stream, new JsonObject { ["a"] = "é" }, CancellationToken.None);
        var frame = stream.ToArray();
        var declared = BinaryPrimitives.ReadUInt32BigEndian(frame);
        Assert.Equal(frame.Length - 4, (int)declared);

        stream.Position = 0;
        var read = await WireCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal("é", (string)read["a"]!); // non-ASCII survives the frame either escaped or raw
    }

    [Fact]
    public async Task RejectsOversizedFrames()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, uint.MaxValue);
        stream.Write(header);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(
            () => WireCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedFrameThrowsEndOfStream()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 100);
        stream.Write(header);
        stream.Write("{}"u8);
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => WireCodec.ReadAsync(stream, CancellationToken.None));
    }
}
