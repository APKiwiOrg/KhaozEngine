using KhaozEngine.Render2D;
using static PixelLabSheetAssembler.Tests.ImageFixtures;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class BboxTests
{
    [Fact]
    public void Finds_inclusive_bounds_of_opaque_pixels()
    {
        var img = Create(10, 10); // all transparent
        SetPixel(img, 2, 3, 255, 0, 0, 255);
        SetPixel(img, 6, 8, 0, 255, 0, 255);

        var b = Bbox.OpaqueBounds(img, alphaThreshold: 0);

        Assert.NotNull(b);
        Assert.Equal((2, 3, 6, 8), (b!.Value.MinX, b.Value.MinY, b.Value.MaxX, b.Value.MaxY));
    }

    [Fact]
    public void Returns_null_for_fully_transparent_image()
    {
        var img = Create(5, 5);
        Assert.Null(Bbox.OpaqueBounds(img, alphaThreshold: 0));
    }

    [Fact]
    public void Threshold_excludes_low_alpha_pixels()
    {
        var img = Create(5, 5);
        SetPixel(img, 1, 1, 0, 0, 0, 10);  // below threshold
        SetPixel(img, 3, 3, 0, 0, 0, 200); // above threshold

        var b = Bbox.OpaqueBounds(img, alphaThreshold: 50);

        Assert.NotNull(b);
        Assert.Equal((3, 3, 3, 3), (b!.Value.MinX, b.Value.MinY, b.Value.MaxX, b.Value.MaxY));
    }
}
