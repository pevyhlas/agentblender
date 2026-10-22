using System.Buffers.Binary;
using System.IO.Compression;

namespace Abl.Integration.Tests.Support;

/// <summary>A decoded RGBA8 image: rows top to bottom, four samples (R, G, B, A) per pixel.</summary>
internal sealed record Rgba8Image(int Width, int Height, byte[] Pixels);

/// <summary>
/// Feature 009 T031 (plan D11): the test-only PNG decoder behind the Metal parity oracle.
/// It reads only what Blender's still output is: 8-bit RGBA (colour type 6), not
/// interlaced, with any of the five scanline filters, inflated with <see cref="ZLibStream"/>.
/// Anything else is refused with <see cref="InvalidDataException"/>, never approximated. It
/// shares no code with the host's <c>PngReader</c> or with Blender, so the comparison it feeds
/// is independent of the system under test. Every chunk CRC is checked; ancillary chunks are
/// skipped; <c>IDAT</c> may be split across chunks.
/// </summary>
internal static class PngRgba8
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The largest image the decoder accepts (parity scenes are 256 px).</summary>
    public const int MaxDimension = 8192;

    private const int Channels = 4;

    public static Rgba8Image Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG signature");

        int width = 0, height = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var compressed = new MemoryStream();
        var offset = Signature.Length;
        while (offset < png.Length)
        {
            if (sawEnd) throw new InvalidDataException("data after IEND");
            if (png.Length - offset < 12) throw new InvalidDataException("truncated chunk");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            if (length > int.MaxValue || length > (uint)(png.Length - offset - 12))
                throw new InvalidDataException("chunk length past the end of the file");
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, (int)length);
            var stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + (int)length));
            if (Crc32(png.AsSpan(offset + 4, (int)length + 4)) != stored)
                throw new InvalidDataException($"{type} CRC mismatch");

            if (!sawHeader && type != "IHDR") throw new InvalidDataException("first chunk is not IHDR");
            switch (type)
            {
                case "IHDR":
                    if (sawHeader) throw new InvalidDataException("second IHDR");
                    (width, height) = Header(data);
                    sawHeader = true;
                    break;
                case "IDAT":
                    compressed.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
                default:
                    // Bit 5 of the first type byte clear: a critical chunk this decoder cannot honour.
                    if ((png[offset + 4] & 0x20) == 0) throw new InvalidDataException($"unsupported critical chunk {type}");
                    break;
            }

            offset += (int)length + 12;
        }

        if (!sawEnd) throw new InvalidDataException("no IEND");
        if (compressed.Length == 0) throw new InvalidDataException("no IDAT");

        var stride = width * Channels;
        var filtered = new byte[(long)height * (stride + 1)];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            try
            {
                inflater.ReadExactly(filtered);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("image data shorter than the IHDR size");
            }

            if (inflater.ReadByte() != -1) throw new InvalidDataException("image data longer than the IHDR size");
        }

        return new Rgba8Image(width, height, Unfilter(filtered, width, height));
    }

    public static Rgba8Image Decode(string path) => Decode(File.ReadAllBytes(path));

    private static (int Width, int Height) Header(ReadOnlySpan<byte> data)
    {
        if (data.Length != 13) throw new InvalidDataException("IHDR is not 13 bytes");
        var width = BinaryPrimitives.ReadUInt32BigEndian(data);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension)
            throw new InvalidDataException($"unsupported dimensions {width}x{height}");
        if (data[8] != 8) throw new InvalidDataException($"bit depth {data[8]}; only 8 is supported");
        if (data[9] != 6) throw new InvalidDataException($"colour type {data[9]}; only RGBA (6) is supported");
        if (data[10] != 0 || data[11] != 0) throw new InvalidDataException("unknown compression or filter method");
        if (data[12] != 0) throw new InvalidDataException("interlaced PNG is not supported");
        return ((int)width, (int)height);
    }

    // PNG specification, section 9: each byte is reconstructed from the filtered byte plus
    // a predictor from the already reconstructed left (a), up (b) and up-left (c) bytes.
    private static byte[] Unfilter(byte[] filtered, int width, int height)
    {
        var stride = width * Channels;
        var pixels = new byte[(long)height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (stride + 1)];
            if (filter > 4) throw new InvalidDataException($"unknown filter type {filter} on row {y}");
            var source = y * (stride + 1) + 1;
            var row = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= Channels ? pixels[row + x - Channels] : 0;
                int b = y > 0 ? pixels[row - stride + x] : 0;
                int c = y > 0 && x >= Channels ? pixels[row - stride + x - Channels] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    _ => Paeth(a, b, c),
                };
                pixels[row + x] = unchecked((byte)(filtered[source + x] + predictor));
            }
        }

        return pixels;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>CRC-32 (ISO 3309, reflected polynomial 0xEDB88320) over a chunk's type and data.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}
