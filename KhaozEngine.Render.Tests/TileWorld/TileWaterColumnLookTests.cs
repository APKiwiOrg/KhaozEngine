using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWaterColumnLookTests
{
    [Fact]
    public void RiverCarriesOwnerSelectedColumnInputsButDoesNotOptIn()
    {
        Assert.Null(TileWaterLooks.River.WaterColumnLight);
        Assert.Equal(new Color(0.28f, 0.30f, 0.18f), TileWaterLooks.River.WaterColor);
        Assert.Equal(1.5f, TileWaterLooks.River.Clarity);
        Assert.True(TileWaterLooks.River.WaterColor.HasValue);
        Assert.True(TileWaterLooks.River.Clarity is > 0f);
        Assert.False(TileWaterLooks.River.ResolveInto(new WaterSettings(), new WaterSettings()).WaterColumnLight);
    }
}
