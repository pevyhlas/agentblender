using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Abl.Integration.Tests.Support;
using Xunit;

namespace Abl.Integration.Tests;

/// <summary>
/// Feature 009 T031 (plan D11): known-vector tests for the test-only PNG decoder and the
/// parity measures behind the Metal qualification (AC-15). They need no Blender and run in
/// the CI unit lane. Two kinds of vector: a PNG written by an independent encoder (macOS
/// ImageIO, through <c>sips</c>) from known pixels, and hand-filtered scanlines whose
/// filtered bytes were worked out by hand from the PNG specification, one per filter type.
/// </summary>
public class PngRgba8Tests
{
    /// <summary>
    /// An 8x4 RGBA8 PNG produced by macOS ImageIO (<c>sips -s format png</c>) from an
    /// uncompressed TIFF holding <see cref="EncoderVectorPixel"/>. It carries ancillary
    /// <c>sRGB</c>, <c>eXIf</c> and <c>iTXt</c> chunks, and its rows use the Sub and Paeth filters.
    /// </summary>
    private const string EncoderVector =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAECAYAAACzzX7wAAAAAXNSR0IArs4c6QAAAFBlWElmTU0AKgAAAAgAAgEGAAMAAAABAAIA" +
        "AIdpAAQAAAABAAAAJgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAABAAAAAAbNGs0AAABpWlUWHRYTUw6" +
        "Y29tLmFkb2JlLnhtcAAAAAAAPHg6eG1wbWV0YSB4bWxuczp4PSJhZG9iZTpuczptZXRhLyIgeDp4bXB0az0iWE1QIENvcmUgNi4w" +
        "LjAiPgogICA8cmRmOlJERiB4bWxuczpyZGY9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkvMDIvMjItcmRmLXN5bnRheC1ucyMiPgog" +
        "ICAgICA8cmRmOkRlc2NyaXB0aW9uIHJkZjphYm91dD0iIgogICAgICAgICAgICB4bWxuczp0aWZmPSJodHRwOi8vbnMuYWRvYmUu" +
        "Y29tL3RpZmYvMS4wLyI+CiAgICAgICAgIDx0aWZmOkNvbXByZXNzaW9uPjE8L3RpZmY6Q29tcHJlc3Npb24+CiAgICAgICAgIDx0" +
        "aWZmOlBob3RvbWV0cmljSW50ZXJwcmV0YXRpb24+MjwvdGlmZjpQaG90b21ldHJpY0ludGVycHJldGF0aW9uPgogICAgICA8L3Jk" +
        "ZjpEZXNjcmlwdGlvbj4KICAgPC9yZGY6UkRGPgo8L3g6eG1wbWV0YT4KFwqI4gAAACRJREFUCB1jZPjP8F/hEbMjEO/HRrMwMtgw" +
        "MDIwawAxVppyBQAzLg9b04f+iQAAAABJRU5ErkJggg==";

    private static byte[] EncoderVectorPixel(int x, int y) =>
        [(byte)(x * 32 + y), (byte)(255 - x * 30), (byte)(y * 60 + x * 3), (byte)(x % 2 == 0 ? 255 : 64 + y * 40)];

    // The 2x2 image every hand-filtered vector decodes to: A B on row 0, C D on row 1. The
    // values make Paeth pick a (R), b (G) and c (B) at D. Alpha has a = b = 128, which no
    // tie order can tell apart; the decisive ties are in PaethTieRows.
    private static readonly byte[] HandPixels =
    [
        10, 20, 30, 255, /* A */ 12, 100, 50, 128, /* B */
        15, 25, 10, 128, /* C */ 190, 120, 40, 129, /* D */
    ];

    // Row 0 is unfiltered; row 1 uses the filter under test. Each filtered byte is
    // (target − predictor) mod 256, worked out by hand from the PNG specification.
    private static readonly byte[] Row0 = [0, 10, 20, 30, 255, 12, 100, 50, 128];

    public static TheoryData<byte, byte[]> FilteredRows => new()
    {
        { 0, [0, 15, 25, 10, 128, 190, 120, 40, 129] },
        { 1, [1, 15, 25, 10, 128, 175, 95, 30, 1] },     // Sub: C − 0, D − C
        { 2, [2, 5, 5, 236, 129, 178, 20, 246, 1] },     // Up: C − A, D − B
        { 3, [3, 10, 15, 251, 1, 177, 58, 10, 1] },      // Average: C − ⌊A/2⌋, D − ⌊(C+B)/2⌋
        { 4, [4, 5, 5, 236, 129, 175, 20, 10, 1] },      // Paeth: C − A; D − (15, 100, 30, 128)
    };

    [Fact]
    public void Crc32MatchesTheKnownIendValue() =>
        Assert.Equal(0xAE426082u, PngRgba8.Crc32("IEND"u8));

    [Fact]
    public void IndependentEncoderVectorDecodesToItsSourcePixels()
    {
        var image = PngRgba8.Decode(Convert.FromBase64String(EncoderVector));

        Assert.Equal(8, image.Width);
        Assert.Equal(4, image.Height);
        var expected = new List<byte>();
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 8; x++)
                expected.AddRange(EncoderVectorPixel(x, y));
        Assert.Equal(expected.ToArray(), image.Pixels);
    }

    [Theory]
    [MemberData(nameof(FilteredRows))]
    public void EachFilterTypeReconstructsTheHandComputedPixels(byte filter, byte[] row1)
    {
        Assert.Equal(filter, row1[0]);
        var image = PngRgba8.Decode(Png(2, 2, [.. Row0, .. row1]));

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(HandPixels, image.Pixels);
    }

    // Paeth tie order (PNG specification 9.4: a before b before c). At D (a = C, b = B,
    // c = A): R ties b with c (a 0, b 30, c 10: p 20, pa 20, pb = pc = 10; b gives 30, c
    // would give 10); G ties a with c (a 10, b 25, c 20: p 15, pa = pc = 5, pb 10; a gives
    // 10, c would give 20). An a/b tie with a ≠ b forces pc = 0, so c wins it and the a/b
    // order is never decisive. Row 1 is Paeth throughout; at C (no left byte) it predicts b.
    private static readonly byte[] PaethTiePixels =
    [
        10, 20, 10, 255, /* A */ 30, 25, 25, 255, /* B */
        0, 10, 10, 255, /* C */ 40, 50, 60, 255, /* D */
    ];

    private static readonly byte[] PaethTieRows =
    [
        0, 10, 20, 10, 255, 30, 25, 25, 255,
        4, 246, 246, 0, 0, 10, 40, 35, 0, // C − A; D − (30, 10, 25, 255)
    ];

    [Fact]
    public void PaethBreaksTiesInTheSpecifiedOrder()
    {
        var image = PngRgba8.Decode(Png(2, 2, PaethTieRows));
        Assert.Equal(PaethTiePixels, image.Pixels);
    }

    [Fact]
    public void AllFiltersInOneImageAndSplitImageDataDecode()
    {
        // Rows 1..5 repeat the C D row under filters 0..4, each over the row above it, which
        // reconstructs to the same pixels; a filter that reads the wrong row breaks it.
        var rows = new List<byte>(Row0);
        foreach (var row in new byte[][]
        {
            [0, 15, 25, 10, 128, 190, 120, 40, 129],
            [1, 15, 25, 10, 128, 175, 95, 30, 1],
            [2, 0, 0, 0, 0, 0, 0, 0, 0],                  // Up over an equal row
            [3, 8, 13, 5, 64, 88, 48, 15, 1],              // C − ⌊C/2⌋; D − ⌊(C+D)/2⌋
            [4, 0, 0, 0, 0, 0, 0, 0, 0],                  // Paeth over an equal row picks b
        })
        {
            rows.AddRange(row);
        }

        var image = PngRgba8.Decode(Png(2, 6, [.. rows], splitIdat: true));

        var expected = new List<byte>(HandPixels);
        for (var y = 2; y < 6; y++) expected.AddRange(HandPixels[8..]);
        Assert.Equal(expected.ToArray(), image.Pixels);
    }

    [Fact]
    public void AncillaryChunksAreSkipped()
    {
        var image = PngRgba8.Decode(Png(2, 2, Rows(),
            extra: ("tEXt", "Comment\0x"u8.ToArray())));
        Assert.Equal(HandPixels, image.Pixels);
    }

    public static TheoryData<string, Func<byte[]>> Malformed => new()
    {
        { "signature", () => [.. Png(2, 2, Rows()).Select((b, i) => i == 1 ? (byte)'Q' : b)] },
        { "CRC mismatch", () => Corrupt(Png(2, 2, Rows()), 33 + 8) },
        { "colour type 2", () => Png(2, 2, Rows(), colorType: 2) },
        { "bit depth 16", () => Png(2, 2, Rows(), bitDepth: 16) },
        { "interlaced", () => Png(2, 2, Rows(), interlace: 1) },
        { "unknown filter type 5", () => Png(2, 2, [.. Row0, 5, 0, 0, 0, 0, 0, 0, 0, 0]) },
        { "shorter than the IHDR size", () => Png(2, 2, Row0) },
        { "longer than the IHDR size", () => Png(2, 2, [.. Rows(), 0]) },
        { "no IEND", () => Png(2, 2, Rows(), end: false) },
        { "unsupported critical chunk", () => Png(2, 2, Rows(), extra: ("PLTE", [0, 0, 0])) },
        { "dimensions", () => Png(0, 2, Rows()) },
        { "first chunk is not IHDR", () => Png(2, 2, Rows(), leading: ("tEXt", "Comment\0x"u8.ToArray())) },
        { "second IHDR", () => Png(2, 2, Rows(), extra: ("IHDR", Header(2, 2))) },
        { "no IDAT", () => Png(2, 2, Rows(), idat: false) },
        { "IHDR is not 13 bytes", () => Png(2, 2, Rows(), header: Header(2, 2)[..12]) },
        { "unknown compression or filter method", () => Png(2, 2, Rows(), header: [.. Header(2, 2)[..10], 1, 0, 0]) },
        { "compression or filter method", () => Png(2, 2, Rows(), header: [.. Header(2, 2)[..11], 1, 0]) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedOrUnsupportedInputIsRefused(string reason, Func<byte[]> png)
    {
        var error = Assert.Throws<InvalidDataException>(() => PngRgba8.Decode(png()));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataAfterIendIsRefused()
    {
        var png = Png(2, 2, Rows());
        var trailing = Png(2, 2, Rows())[^12..]; // a second IEND chunk
        Assert.Throws<InvalidDataException>(() => PngRgba8.Decode([.. png, .. trailing]));
    }

    [Fact]
    public void PsnrMatchesWorkedValues()
    {
        var black = Image(1, 1, 0, 0, 0, 0);
        Assert.Equal(double.PositiveInfinity, ImageMetrics.Psnr(black, black, SampleChannels.Rgba));
        Assert.Equal(double.PositiveInfinity, ImageMetrics.Psnr(black, black, SampleChannels.Rgb));
        // R differs by 255: RGBA MSE = 255²/4, PSNR = 10·log10(4); RGB MSE = 255²/3, 10·log10(3).
        Assert.Equal(6.0206, ImageMetrics.Psnr(black, Image(1, 1, 255, 0, 0, 0), SampleChannels.Rgba), 4);
        Assert.Equal(4.7712, ImageMetrics.Psnr(black, Image(1, 1, 255, 0, 0, 0), SampleChannels.Rgb), 4);
        // Every sample differs by 1: MSE = 1, PSNR = 20·log10(255), whichever channels count.
        var ramp = Image(2, 1, 10, 20, 30, 40, 50, 60, 70, 80);
        var shifted = Image(2, 1, 11, 19, 31, 39, 51, 59, 71, 79);
        Assert.Equal(48.1308, ImageMetrics.Psnr(ramp, shifted, SampleChannels.Rgba), 4);
        Assert.Equal(48.1308, ImageMetrics.Psnr(ramp, shifted, SampleChannels.Rgb), 4);
        // Two samples (R, then the second pixel's B) differ by 4: RGBA MSE = 32/8 = 4,
        // PSNR = 10·log10(65025/4); RGB MSE = 32/6, PSNR = 10·log10(65025·6/32).
        var zero = Image(2, 1, 0, 0, 0, 0, 0, 0, 0, 0);
        var two = Image(2, 1, 4, 0, 0, 0, 0, 0, 4, 0);
        Assert.Equal(42.1102, ImageMetrics.Psnr(zero, two, SampleChannels.Rgba), 4);
        Assert.Equal(40.8608, ImageMetrics.Psnr(zero, two, SampleChannels.Rgb), 4);
        // Only alpha differs: RGB sees identical images; RGBA MSE = 255²/4.
        Assert.Equal(double.PositiveInfinity, ImageMetrics.Psnr(black, Image(1, 1, 0, 0, 0, 255), SampleChannels.Rgb));
        Assert.Equal(6.0206, ImageMetrics.Psnr(black, Image(1, 1, 0, 0, 0, 255), SampleChannels.Rgba), 4);
    }

    [Fact]
    public void HistogramsCountEachChannelSeparately()
    {
        var a = Image(2, 1, 1, 2, 3, 255, 1, 7, 3, 0);
        var b = Image(2, 1, 1, 9, 0, 250, 6, 7, 3, 0);

        var histogram = ImageMetrics.Histogram(a);
        Assert.Equal(4, histogram.Length);
        Assert.Equal(2, histogram[0][1]);
        Assert.Equal(1, histogram[1][2]);
        Assert.Equal(1, histogram[1][7]);
        Assert.Equal(2, histogram[2][3]);
        Assert.Equal(1, histogram[3][255]);
        Assert.Equal(1, histogram[3][0]);
        Assert.All(histogram, channel => Assert.Equal(2, channel.Sum()));

        var difference = ImageMetrics.DifferenceHistogram(a, b);
        Assert.Equal(1, difference[0][0]);
        Assert.Equal(1, difference[0][5]);
        Assert.Equal(1, difference[1][7]);
        Assert.Equal(1, difference[1][0]);
        Assert.Equal(1, difference[2][3]);
        Assert.Equal(1, difference[3][5]);
        Assert.Equal(new[] { 5, 7, 3, 5 }, ImageMetrics.MaxDifference(a, b));

        // RGBA samples within 4: the four equal ones and the difference of 3, so 5 of 8.
        Assert.Equal(5 / 8.0, ImageMetrics.FractionWithin(a, b, 4, SampleChannels.Rgba));
        Assert.Equal(1.0, ImageMetrics.FractionWithin(a, b, 7, SampleChannels.Rgba));
        Assert.Equal(4 / 8.0, ImageMetrics.FractionWithin(a, b, 0, SampleChannels.Rgba));
        // RGB only: the alpha differences (5 and 0) drop out, so 4 of 6, all 6, and 3 of 6.
        Assert.Equal(4 / 6.0, ImageMetrics.FractionWithin(a, b, 4, SampleChannels.Rgb));
        Assert.Equal(1.0, ImageMetrics.FractionWithin(a, b, 7, SampleChannels.Rgb));
        Assert.Equal(3 / 6.0, ImageMetrics.FractionWithin(a, b, 0, SampleChannels.Rgb));
    }

    [Fact]
    public void ImagesOfDifferentSizesAreNotCompared()
    {
        Assert.Throws<ArgumentException>(() => ImageMetrics.Psnr(
            Image(1, 1, 0, 0, 0, 0), Image(1, 2, 0, 0, 0, 0, 0, 0, 0, 0), SampleChannels.Rgb));
        Assert.Throws<ArgumentException>(() => ImageMetrics.DifferenceHistogram(
            Image(2, 1, 0, 0, 0, 0, 0, 0, 0, 0), Image(1, 2, 0, 0, 0, 0, 0, 0, 0, 0)));
    }

    private static Rgba8Image Image(int width, int height, params byte[] pixels) => new(width, height, pixels);

    private static byte[] Rows() => [.. Row0, 0, 15, 25, 10, 128, 190, 120, 40, 129];

    private static byte[] Corrupt(byte[] png, int at)
    {
        var copy = (byte[])png.Clone();
        copy[at] ^= 0xFF;
        return copy;
    }

    /// <summary>An IHDR payload: RGBA8 by default, compression, filter method and interlace 0.</summary>
    private static byte[] Header(int width, int height, int colorType = 6, int bitDepth = 8, int interlace = 0)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        header[12] = (byte)interlace;
        return header;
    }

    /// <summary>
    /// Wraps already-filtered scanlines (filter byte first) in a PNG. <paramref name="header"/>
    /// replaces the IHDR payload; <paramref name="leading"/> is written before IHDR and
    /// <paramref name="extra"/> after it.
    /// </summary>
    private static byte[] Png(
        int width, int height, byte[] filteredRows, int colorType = 6, int bitDepth = 8, int interlace = 0,
        bool splitIdat = false, (string Type, byte[] Data)? extra = null, bool end = true,
        byte[]? header = null, (string Type, byte[] Data)? leading = null, bool idat = true)
    {
        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        if (leading is { } first) Chunk(file, first.Type, first.Data);
        Chunk(file, "IHDR", header ?? Header(width, height, colorType, bitDepth, interlace));
        if (extra is { } chunk) Chunk(file, chunk.Type, chunk.Data);

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(filteredRows);
        var data = compressed.ToArray();
        if (idat && splitIdat)
        {
            Chunk(file, "IDAT", data[..(data.Length / 2)]);
            Chunk(file, "IDAT", data[(data.Length / 2)..]);
        }
        else if (idat)
        {
            Chunk(file, "IDAT", data);
        }

        if (end) Chunk(file, "IEND", []);
        return file.ToArray();
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        file.Write(number);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        file.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, PngRgba8.Crc32(typed));
        file.Write(number);
    }
}
