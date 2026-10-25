using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

[Collection("AllocSensitive")]
public class TileCornerHeightAllocationTests
{
    [Fact]
    public void LegacyCornerAndBilinearHeightReadsAllocateNothing()
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(default);
        doc.SetCornerHeightCm(63, 63, 0, 125);
        float corner = 0f, interpolated = 0f, missing = 0f;
        doc.CornerHeight(64, 64, 2);
        doc.HeightAt(63.5f, -63.5f, 2);
        doc.CornerHeight(-10, -10, 2);

        AllocAssert.NoPerCallAllocation("Legacy corner height resolution", () =>
        {
            for (int i = 0; i < 64; i++)
            {
                corner = doc.CornerHeight(64, 64, 2);
                interpolated = doc.HeightAt(63.5f, -63.5f, 2);
                missing = doc.CornerHeight(-10, -10, 2);
            }
        });

        Assert.Equal(7.25f, corner);
        Assert.Equal(7.25f, interpolated);
        Assert.Equal(6f, missing);
    }
}
