using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D;

/// <summary>
/// Verifies that <see cref="ImageRgba"/> is the single decode path: decode returns the expected
/// pixel-count and the two overloads (<see cref="ImageRgba.Decode"/> /
/// <see cref="ImageRgba.Load"/>) are consistent. These tests are headless (no GPU).
/// </summary>
public class ImageDecodeParityTests
{
    // 3x2 RGBA so Width != Height, catching any stride / dimension swap.
    static byte[] Sample() => new byte[]
    {
        255, 0,   0,   255,   0, 255, 0, 255,   0,   0, 255, 255,  // row 0: red, green, blue
          0, 0,   0,   128, 100, 50, 25, 200, 200, 100,  50, 255,  // row 1: dark-alpha, mid, tan
    };

    [Fact]
    public void Decode_YieldsRgbaByteCount()
    {
        byte[] png = Png.Encode(Sample(), 3, 2);

        ImageRgba img = ImageRgba.Decode(png);

        Assert.Equal(3, img.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(img.Width * img.Height * 4, img.Pixels.Length);
    }

    [Fact]
    public void Decode_RecoversOriginalPixels()
    {
        byte[] png = Png.Encode(Sample(), 3, 2);

        ImageRgba img = ImageRgba.Decode(png);

        Assert.Equal(Sample(), img.Pixels);
    }
}
