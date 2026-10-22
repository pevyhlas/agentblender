using System.Buffers.Binary;
using System.IO.Compression;

namespace Abl.Integration.Tests;

/// <summary>Independent fixture-only sample decoder, not a host runtime import/export dependency.</summary>
internal static class PngSampleFixture
{
    public static byte[] Decode64(byte[] png)
    {
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IHDR" && (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 8)) != 64 ||
                BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 12)) != 64)) throw new InvalidDataException("fixture dimensions");
            if (type == "IDAT") compressed.Write(png, offset + 8, length);
            offset += length + 12;
        }
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var rows = new byte[64 * (64 * 4 + 1)];
        inflater.ReadExactly(rows);
        if (inflater.ReadByte() != -1) throw new InvalidDataException("extra fixture samples");
        var result = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        {
            var filter = rows[y * 257];
            for (var x = 0; x < 256; x++)
            {
                var at = y * 256 + x;
                var left = x >= 4 ? result[at - 4] : 0;
                var up = y > 0 ? result[at - 256] : 0;
                var corner = y > 0 && x >= 4 ? result[at - 260] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => throw new InvalidDataException("fixture filter"),
                };
                result[at] = unchecked((byte)(rows[y * 257 + x + 1] + predictor));
            }
        }
        return result;
    }
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var da = Math.Abs(p - a); var db = Math.Abs(p - b); var dc = Math.Abs(p - c);
        return da <= db && da <= dc ? a : db <= dc ? b : c;
    }
}
