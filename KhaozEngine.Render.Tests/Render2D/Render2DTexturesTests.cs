using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D;

public sealed class Render2DTexturesTests
{
    [Fact]
    public void WhitePixelSourceIsOneOpaqueRgbaPixel()
    {
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Render2DTextures.CreateWhitePixels());
    }
}
