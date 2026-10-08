using KhaozEngine.Render2D;
using static PixelLabSheetAssembler.Tests.ImageFixtures;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class SmokeTest
{
    [Fact]
    public void Empty_frame_is_transparent()
    {
        var img = Create(4, 4);
        Assert.Equal(0, img.AlphaAt(0, 0));
    }
}
