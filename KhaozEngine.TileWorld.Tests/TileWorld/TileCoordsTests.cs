using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileCoordsTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(63, 63, 0, 0)]
    [InlineData(64, 0, 1, 0)]
    [InlineData(-1, -1, -1, -1)]
    [InlineData(-64, 5, -1, 0)]
    [InlineData(-65, 5, -2, 0)]
    public void RegionCoord_Of_floors_negative_coordinates(int x, int z, int rx, int rz)
    {
        Assert.Equal(new RegionCoord(rx, rz), RegionCoord.Of(x, z));
    }

    [Fact]
    public void TileCoord_local_coordinates_wrap_positive_for_negative_world()
    {
        var t = new TileCoord(-1, -130, 0);
        Assert.Equal(new RegionCoord(-1, -3), t.Region);
        Assert.Equal(63, t.LocalX);
        Assert.Equal(62, t.LocalZ);
    }

    [Fact]
    public void TileRect_FromCorners_normalises_and_is_inclusive()
    {
        var r = TileRect.FromCorners(5, 9, 2, 3);
        Assert.Equal(new TileRect(2, 3, 4, 7), r);
        Assert.True(r.Contains(5, 9));
        Assert.False(r.Contains(6, 9));
        Assert.Equal(6, r.X1);
        Assert.Equal(10, r.Z1);
    }

    [Fact]
    public void TileRect_Expand_Intersect_Union()
    {
        var a = new TileRect(0, 0, 4, 4);
        var b = new TileRect(2, 2, 4, 4);
        Assert.Equal(new TileRect(-1, -1, 6, 6), a.Expand(1));
        Assert.Equal(new TileRect(2, 2, 2, 2), a.Intersect(b));
        Assert.Equal(new TileRect(0, 0, 6, 6), a.Union(b));
        Assert.True(a.Intersects(b));
        Assert.True(a.Intersect(new TileRect(10, 10, 1, 1)).IsEmpty);
        Assert.False(a.Intersects(new TileRect(4, 0, 1, 1)));
    }

    [Fact]
    public void TileDirections_are_in_the_OSRS_neighbour_order_with_correct_deltas()
    {
        Assert.Equal(new[] { TileDirection.W, TileDirection.E, TileDirection.S, TileDirection.N,
                             TileDirection.SW, TileDirection.SE, TileDirection.NW, TileDirection.NE }, TileDirections.All);
        Assert.Equal((-1, 0), TileDirections.Delta(TileDirection.W));
        Assert.Equal((1, 1), TileDirections.Delta(TileDirection.NE));
        Assert.Equal((0, -1), TileDirections.Delta(TileDirection.S));
        Assert.True(TileDirections.IsDiagonal(TileDirection.SW));
        Assert.False(TileDirections.IsDiagonal(TileDirection.N));
    }
}
