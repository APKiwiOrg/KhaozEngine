using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCellsTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(63, 63, 0, 0)]
    [InlineData(64, 0, 1, 0)]
    [InlineData(-1, -1, -1, -1)]
    [InlineData(-64, -65, -1, -2)]
    [InlineData(1000, -1000, 15, -16)]
    public void A_cell_is_exactly_a_region_including_across_the_origin(int x, int z, int cx, int cz)
    {
        var tile = new TileCoord(x, z, 0);
        CellCoord cell = TileCells.CoordOf(tile);
        Assert.Equal(new CellCoord(cx, cz), cell);
        Assert.Equal(RegionCoord.Of(x, z), TileCells.RegionOf(cell));
        Assert.Equal(CellCoord.FromWorld(x, z, TileCells.CellSize), cell);
    }

    // The plane rides along untouched. Planes do not shard: one cell holds every plane of its region, so two tiles
    // that differ only in plane are the same cell and the serve is what filters them apart.
    [Fact]
    public void Every_plane_of_a_region_lands_in_the_one_cell()
    {
        Assert.Equal(new CellCoord(1, 2), TileCells.CoordOf(new TileCoord(100, 150, 0)));
        Assert.Equal(new CellCoord(1, 2), TileCells.CoordOf(new TileCoord(100, 150, 3)));
    }
}
