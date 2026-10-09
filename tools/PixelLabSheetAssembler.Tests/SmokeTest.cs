using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class SmokeTest
{
    [Fact]
    public void RgbaImage_creates_transparent_image()
    {
        var img = new RgbaImage(4, 4);
        Assert.Equal(0, img[0, 0].A);
    }
}
