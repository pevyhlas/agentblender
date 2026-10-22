using System.Buffers.Binary;
using System.Text;

namespace Abl.Core.Delivery;

public static class DeliveryProfile
{
    public const int ColorType = 6;
    public const int BitDepth = 8;
    public const string ColorSpace = "srgb";
    public const string Alpha = "straight";
}

public sealed record PngHeader(int Width, int Height, int BitDepth, int ColorType, IReadOnlyList<string> Chunks)
{
    public bool HasSrgbChunk => Chunks.Contains("sRGB");
    public bool ConformsToProfile(out string reason)
    {
        reason = ColorType == 6 && BitDepth == 8 ? "" : "PNG must be noninterlaced RGBA8";
        return reason.Length == 0;
    }
}

/// <summary>Bounded encoded checks/container normalization only; IDAT samples remain opaque.</summary>
public static class PngReader
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PngHeader Parse(ReadOnlySpan<byte> bytes) => Read(bytes, false).Header;
    public static PngHeader ParseFile(string path) => Parse(DeliveryPayload.ReadBounded(path, DeliveryService.MaxImageBytes));

    // Used only for worker output whose explicit render profile has been set.
    // Drops ancillary container metadata, inserts sRGB, preserves original IDAT.
    public static byte[] Normalize(ReadOnlySpan<byte> bytes)
    {
        var parsed = Read(bytes, true);
        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", parsed.Ihdr);
        WriteChunk(output, "sRGB", [0]);
        foreach (var idat in parsed.Idats) WriteChunk(output, "IDAT", idat);
        WriteChunk(output, "IEND", []);
        if (output.Length > DeliveryService.MaxImageBytes) throw Bad("normalized PNG exceeds encoded limit");
        return output.ToArray();
    }

    private static (PngHeader Header, byte[] Ihdr, List<byte[]> Idats) Read(ReadOnlySpan<byte> bytes, bool normalize)
    {
        if (bytes.Length > DeliveryService.MaxImageBytes || bytes.Length < 45 || !bytes[..8].SequenceEqual(Signature)) throw Bad("invalid or oversized PNG signature");
        var offset = 8;
        var count = 0;
        var names = new List<string>();
        var idats = new List<byte[]>();
        byte[]? ihdr = null;
        var idatStarted = false;
        var idatEnded = false;
        var ended = false;
        var srgb = false;
        long totalIdat = 0;
        while (offset < bytes.Length)
        {
            if (++count > 4096 || bytes.Length - offset < 12) throw Bad("PNG chunk bound/truncation");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || length + 12L > bytes.Length - offset) throw Bad("PNG chunk length runs past end");
            var typeBytes = bytes.Slice(offset + 4, 4);
            if (!typeBytes.ToArray().All(b => b is >= 65 and <= 90 or >= 97 and <= 122)) throw Bad("invalid PNG chunk type");
            var type = Encoding.ASCII.GetString(typeBytes);
            var data = bytes.Slice(offset + 8, (int)length);
            if (Crc(bytes.Slice(offset + 4, (int)length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + (int)length, 4))) throw Bad("PNG CRC mismatch");
            offset += 12 + (int)length;
            names.Add(type);
            if (ihdr is null && type != "IHDR") throw Bad("IHDR must be first");
            if (type == "IHDR")
            {
                if (ihdr is not null || length != 13) throw Bad("invalid/duplicate IHDR");
                ihdr = data.ToArray();
                var width = BinaryPrimitives.ReadUInt32BigEndian(data);
                var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
                if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16777216 ||
                    data[8] != 8 || data[9] != 6 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    throw Bad("PNG requires bounded full-canvas noninterlaced RGBA8");
            }
            else if (type == "IDAT")
            {
                if (idatEnded) throw Bad("IDAT chunks must be consecutive");
                idatStarted = true;
                totalIdat += length;
                if (normalize) idats.Add(data.ToArray());
            }
            else if (type == "IEND")
            {
                if (length != 0 || !idatStarted || totalIdat == 0 || offset != bytes.Length) throw Bad("invalid IEND or trailing data");
                ended = true; break;
            }
            else
            {
                if (idatStarted) idatEnded = true;
                if (type == "sRGB")
                {
                    if (srgb || idatStarted || length != 1 || data[0] > 3 || !normalize && data[0] != 0) throw Bad("invalid/duplicate sRGB");
                    srgb = true;
                }
                else if (!normalize) throw Bad("PNG delivery container has unsupported chunk " + type);
                else if ((typeBytes[0] & 32) == 0 || type is "acTL" or "fcTL" or "fdAT") throw Bad("unsupported critical/animated PNG chunk");
                // Worker output may contain Blender ancillary tags. They are stripped
                // only under the explicitly configured producer profile, never converted.
            }
        }
        if (!ended || ihdr is null) throw Bad("PNG missing IEND");
        if (!normalize && !srgb) throw Bad("delivery container requires canonical sRGB tag");
        return (new(BinaryPrimitives.ReadInt32BigEndian(ihdr), BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4)), 8, 6, names), ihdr, idats);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type); output.Write(typeBytes); output.Write(data);
        var crc = Crc(typeBytes, data); BinaryPrimitives.WriteUInt32BigEndian(number, crc); output.Write(number);
    }

    private static uint Crc(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        uint value = uint.MaxValue;
        foreach (var b in first) value = Update(value, b);
        foreach (var b in second) value = Update(value, b);
        return ~value;
        static uint Update(uint value, byte b)
        {
            value ^= b;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            return value;
        }
    }
    private static AblException Bad(string message) => new("bad_request", message);
}
