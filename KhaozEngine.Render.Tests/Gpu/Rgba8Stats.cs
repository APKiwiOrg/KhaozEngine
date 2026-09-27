using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Whole-image measures over tightly packed RGBA8 readbacks, shared by the temporal scene and smoke tests. Luma takes
/// the Rec. 601 weights on the stored bytes, and every measure ignores alpha.
/// </summary>
internal static class Rgba8Stats
{
    /// <summary>The luma of the pixel whose red byte is at <paramref name="i"/>.</summary>
    public static double Luma(byte[] rgba, int i) => 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];

    /// <summary>The mean absolute difference of the colour bytes of two same-size images, in byte units.</summary>
    public static double MeanAbs(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("The images differ in size.", nameof(b));
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            if ((i & 3) != 3) sum += Math.Abs(a[i] - b[i]);
        return sum / (a.Length / 4 * 3);
    }

    /// <summary>The mean luma of the whole image.</summary>
    public static double MeanLuma(byte[] rgba)
    {
        double sum = 0;
        int n = rgba.Length / 4;
        for (int p = 0; p < n; p++) sum += Luma(rgba, p * 4);
        return sum / n;
    }

    /// <summary>The mean luma of rows <paramref name="fromRow"/> up to but not including <paramref name="toRow"/> of an
    /// image <paramref name="width"/> pixels wide, row 0 at the top.</summary>
    public static double MeanLuma(byte[] rgba, int width, int fromRow, int toRow)
    {
        double sum = 0;
        for (int y = fromRow; y < toRow; y++)
            for (int x = 0; x < width; x++) sum += Luma(rgba, (y * width + x) * 4);
        return sum / ((toRow - fromRow) * width);
    }

    /// <summary>The image <paramref name="factor"/> times smaller per axis. Each output byte, alpha included, is the
    /// rounded mean of the matching byte over a <paramref name="factor"/> by <paramref name="factor"/> block of
    /// <paramref name="rgba"/>, an image <paramref name="width"/> by <paramref name="height"/> pixels whose sizes are
    /// multiples of the factor.</summary>
    public static byte[] BoxDownsample(byte[] rgba, int width, int height, int factor)
    {
        if (factor < 1 || width % factor != 0 || height % factor != 0)
            throw new ArgumentException("The image size is not a multiple of the factor.", nameof(factor));
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("The image is not width by height RGBA8 pixels.", nameof(rgba));
        int w = width / factor, h = height / factor, n = factor * factor;
        var small = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < factor; dy++)
                        for (int dx = 0; dx < factor; dx++)
                            sum += rgba[((y * factor + dy) * width + x * factor + dx) * 4 + c];
                    small[(y * w + x) * 4 + c] = (byte)((sum + n / 2) / n);
                }
        return small;
    }

    /// <summary>The standard deviation of luma over the whole image, which a flat fill keeps near zero.</summary>
    public static double LumaDeviation(byte[] rgba)
    {
        double sum = 0, squares = 0;
        int n = rgba.Length / 4;
        for (int p = 0; p < n; p++)
        {
            double l = Luma(rgba, p * 4);
            sum += l;
            squares += l * l;
        }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, squares / n - mean * mean));
    }
}
