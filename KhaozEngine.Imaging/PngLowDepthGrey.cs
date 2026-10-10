namespace KhaozEngine.Imaging;

/// <summary>
/// Widens 1, 2 and 4-bit greyscale to 8 bits. The colour key compares the stored sample, so the key is applied
/// before scaling. Each sample then becomes <c>value * 255 / (2^depth - 1)</c>, the PNG specification's exact
/// rescale.
/// </summary>
internal static class PngLowDepthGrey
{
    public static PngImage Expand(byte[] samples, int width, int height, int bitDepth, PngTransparency? key)
    {
        int channels = key is null ? 1 : 2;
        byte[] output = key is { } transparent ? transparent.Expand(samples, 8) : samples;
        int max = (1 << bitDepth) - 1;
        for (int i = 0; i < output.Length; i += channels) output[i] = (byte)(output[i] * 255 / max);
        return new PngImage(width, height, channels, 8, output);
    }
}
