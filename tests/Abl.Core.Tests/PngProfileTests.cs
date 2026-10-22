using System.Buffers.Binary;
using System.Text;
using System.IO.Compression;
using Abl.Core;
using Abl.Core.Delivery;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// Feature 004 FR-001 (host-side verify half): the pure-byte PNG reader accepts a well-formed
/// still, rejects malformed bytes, and its profile check accepts only RGBA8.
/// </summary>
public sealed class PngProfileTests
{
    /// <summary>
    /// Minimal but structurally valid PNG bytes: real signature + IHDR (carrying the given
    /// dims/bit-depth/colour-type) + optional sRGB + compressed zero samples + IEND,
    /// with independently produced framing/CRC. Sample decoding remains a separate check.
    /// </summary>
    internal static byte[] Png(int width = 64, int height = 48, int bitDepth = 8, int colorType = 6, bool srgb = false, byte srgbIntent = 0)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        WriteChunk(ms, "IHDR", ihdr);
        if (srgb)
        {
            WriteChunk(ms, "sRGB", [srgbIntent]);
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(new byte[(width * 4 + 1) * height]);
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        uint crc = uint.MaxValue;
        foreach (var b in Encoding.ASCII.GetBytes(type).Concat(data))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        s.Write(len);
    }

    [Fact]
    public void ParsesAWellFormedRgbaStill()
    {
        var header = PngReader.Parse(Png(width: 100, height: 200, srgb: true));
        Assert.Equal(100, header.Width);
        Assert.Equal(200, header.Height);
        Assert.Equal(8, header.BitDepth);
        Assert.Equal(6, header.ColorType);
        Assert.True(header.HasSrgbChunk);
        Assert.Equal(["IHDR", "sRGB", "IDAT", "IEND"], header.Chunks);
    }

    [Fact]
    public void Rgba8ConformsToTheProfile()
    {
        Assert.True(PngReader.Parse(Png(colorType: 6, bitDepth: 8, srgb: true)).ConformsToProfile(out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void NonRgbaColorTypeIsRejected()
    {
        // Color type 2 = truecolour WITHOUT alpha — a delivery still must carry alpha.
        Assert.Contains("RGBA", Assert.Throws<AblException>(() => PngReader.Parse(Png(colorType: 2))).Message);
    }

    [Fact]
    public void SixteenBitDepthIsRejected()
    {
        Assert.Contains("RGBA8", Assert.Throws<AblException>(() => PngReader.Parse(Png(bitDepth: 16))).Message);
    }

    [Fact]
    public void BadSignatureIsRejected()
    {
        var bytes = Png();
        bytes[1] = 0x00; // corrupt the signature
        var ex = Assert.Throws<AblException>(() => PngReader.Parse(bytes));
        Assert.Equal("bad_request", ex.Kind);
    }

    [Fact]
    public void CrcTrailingDataAndNormalizationAreExplicit()
    {
        var original = Png(srgb: true);
        var corrupt = (byte[])original.Clone();
        corrupt[29] ^= 1;
        Assert.Contains("CRC", Assert.Throws<AblException>(() => PngReader.Parse(corrupt)).Message);
        Assert.Throws<AblException>(() => PngReader.Parse([.. original, 0]));
        var normalized = PngReader.Normalize(original);
        Assert.Equal(original, normalized);
        Assert.Equal(["IHDR", "sRGB", "IDAT", "IEND"], PngReader.Parse(normalized).Chunks);
        Assert.Equal(original, PngReader.Normalize(Png(srgb: true, srgbIntent: 3)));
        Assert.Throws<AblException>(() => PngReader.Parse(Png()));
    }

    [Fact]
    public void TruncatedFileWithNoIendIsRejected()
    {
        var full = Png();
        // Drop the final IEND chunk (12 bytes) — a truncated download/copy.
        var truncated = full[..(full.Length - 12)];
        var ex = Assert.Throws<AblException>(() => PngReader.Parse(truncated));
        Assert.Equal("bad_request", ex.Kind);
    }

    [Fact]
    public void ChunkLengthRunningPastEndIsRejected()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 8);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 8);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(ms, "IHDR", ihdr); // a VALID 13-byte IHDR, so the walk proceeds past it

        // A second chunk whose 12-byte header is fully present but whose declared data length
        // runs past the buffer — this trips the chunk-walk bounds check, not the IHDR check.
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, 1_000_000);
        ms.Write(len);
        ms.Write(Encoding.ASCII.GetBytes("IDAT"));
        ms.Write([0, 0, 0, 0]); // CRC slot — header complete, but the 1 MB of data never follows

        var ex = Assert.Throws<AblException>(() => PngReader.Parse(ms.ToArray()));
        Assert.Equal("bad_request", ex.Kind);
        Assert.Contains("past end", ex.Message);
    }

    [Fact]
    public void FirstChunkMustBeIhdr()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IDAT", new byte[13]); // 13 bytes but the wrong type
        WriteChunk(ms, "IEND", []);
        var ex = Assert.Throws<AblException>(() => PngReader.Parse(ms.ToArray()));
        Assert.Equal("bad_request", ex.Kind);
        Assert.Contains("IHDR", ex.Message);
    }
}
