namespace Abl.Integration.Tests.Support;

/// <summary>The channels a parity measure counts.</summary>
internal enum SampleChannels
{
    /// <summary>R, G and B only: the gates use these, because the parity scenes render an opaque film.</summary>
    Rgb,

    /// <summary>All four channels, alpha included.</summary>
    Rgba,
}

/// <summary>
/// Feature 009 T031 (plan D11, FR-008): the parity measures over two decoded RGBA8 images
/// of the same size. <see cref="Psnr"/> and <see cref="FractionWithin"/> take the channels
/// they count. The Metal-vs-CPU gates count R, G and B only: the parity scenes render with
/// <c>film_transparent = False</c>, so alpha is 255 everywhere and counting it would loosen
/// both gates (PSNR by about 1.25 dB, the 99.9% fraction to about 99.87% of RGB samples).
/// The qualification tests assert separately that the alpha planes are identical. The
/// histograms and the per-channel maximum always cover all four channels. The gates (Metal
/// vs CPU: PSNR ≥ 40 dB and ≥ 99.9% of samples within 4/255; Metal vs Metal: PSNR ≥ 50 dB)
/// are applied by the qualification tests, not here.
/// </summary>
internal static class ImageMetrics
{
    private const int Channels = 4;
    private const int Alpha = 3;

    /// <summary>
    /// Peak signal-to-noise ratio in dB over the samples of <paramref name="channels"/>,
    /// peak 255: <c>10 · log10(255² / MSE)</c>. Identical images give positive infinity.
    /// </summary>
    public static double Psnr(Rgba8Image expected, Rgba8Image actual, SampleChannels channels)
    {
        RequireSameSize(expected, actual);
        double sum = 0;
        long count = 0;
        for (var i = 0; i < expected.Pixels.Length; i++)
        {
            if (!Counts(channels, i)) continue;
            var d = expected.Pixels[i] - actual.Pixels[i];
            sum += d * d;
            count++;
        }

        if (sum == 0) return double.PositiveInfinity;
        var mse = sum / count;
        return 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Per-channel counts of each sample value: <c>[channel][value]</c>, channels R, G, B, A.</summary>
    public static long[][] Histogram(Rgba8Image image)
    {
        var counts = NewCounts();
        for (var i = 0; i < image.Pixels.Length; i++)
            counts[i % Channels][image.Pixels[i]]++;
        return counts;
    }

    /// <summary>
    /// Per-channel counts of the absolute sample difference: <c>[channel][|a − b|]</c>,
    /// channels R, G, B, A. Index 0 counts equal samples.
    /// </summary>
    public static long[][] DifferenceHistogram(Rgba8Image expected, Rgba8Image actual)
    {
        RequireSameSize(expected, actual);
        var counts = NewCounts();
        for (var i = 0; i < expected.Pixels.Length; i++)
            counts[i % Channels][Math.Abs(expected.Pixels[i] - actual.Pixels[i])]++;
        return counts;
    }

    /// <summary>
    /// The fraction of the samples of <paramref name="channels"/> whose absolute difference
    /// is at most <paramref name="tolerance"/>.
    /// </summary>
    public static double FractionWithin(Rgba8Image expected, Rgba8Image actual, int tolerance, SampleChannels channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        var histogram = DifferenceHistogram(expected, actual);
        long within = 0, total = 0;
        for (var channel = 0; channel < Channels; channel++)
        {
            if (!Counts(channels, channel)) continue;
            for (var d = 0; d < histogram[channel].Length; d++)
            {
                total += histogram[channel][d];
                if (d <= tolerance) within += histogram[channel][d];
            }
        }

        return (double)within / total;
    }

    /// <summary>The largest absolute sample difference per channel (R, G, B, A).</summary>
    public static int[] MaxDifference(Rgba8Image expected, Rgba8Image actual)
    {
        var histogram = DifferenceHistogram(expected, actual);
        return [.. histogram.Select(channel => Array.FindLastIndex(channel, count => count > 0))];
    }

    private static bool Counts(SampleChannels channels, int sample) =>
        channels == SampleChannels.Rgba || sample % Channels != Alpha;

    private static long[][] NewCounts() => [.. Enumerable.Range(0, Channels).Select(_ => new long[256])];

    private static void RequireSameSize(Rgba8Image expected, Rgba8Image actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            throw new ArgumentException(
                $"image sizes differ: {expected.Width}x{expected.Height} vs {actual.Width}x{actual.Height}");
    }
}
