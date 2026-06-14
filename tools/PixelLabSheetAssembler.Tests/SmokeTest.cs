using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class SmokeTest
{
    [Fact]
    public void ImageSharp_creates_transparent_image()
    {
        using var img = new Image<Rgba32>(4, 4);
        Assert.Equal(0, img[0, 0].A);
    }
}
