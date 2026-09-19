using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileSteerResolverTests
{
    static readonly TileCoord At = new(5, 5, 0);

    static TileCollisionMap MapWithTrees(params (int X, int Z)[] trees)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        foreach ((int x, int z) in trees) doc.AddObject("tree", x, z, 0, 0);
        return TileMoveSimulatorTests.Bake(doc);
    }

    [Fact]
    public void An_open_direction_is_itself() =>
        Assert.Equal(TileDirection.NE, TileSteerResolver.Resolve(MapWithTrees(), At, TileDirection.NE, 1));

    [Fact]
    public void A_blocked_straight_step_stands() =>
        Assert.Null(TileSteerResolver.Resolve(MapWithTrees((5, 6)), At, TileDirection.N, 1));

    [Fact]
    public void A_diagonal_blocked_by_a_wall_to_the_east_slides_north() =>
        Assert.Equal(TileDirection.N,
            TileSteerResolver.Resolve(MapWithTrees((6, 5), (6, 6)), At, TileDirection.NE, 1));

    [Fact]
    public void A_diagonal_blocked_by_a_wall_to_the_north_slides_east() =>
        Assert.Equal(TileDirection.E,
            TileSteerResolver.Resolve(MapWithTrees((5, 6), (6, 6)), At, TileDirection.NE, 1));

    [Fact]
    public void A_blocked_corner_with_both_axes_open_takes_the_z_axis_step() =>
        Assert.Equal(TileDirection.N,
            TileSteerResolver.Resolve(MapWithTrees((6, 6)), At, TileDirection.NE, 1));

    [Fact]
    public void A_south_west_corner_takes_south_before_west() =>
        Assert.Equal(TileDirection.S,
            TileSteerResolver.Resolve(MapWithTrees((4, 4)), At, TileDirection.SW, 1));

    [Fact]
    public void An_enclosed_body_stands() =>
        Assert.Null(TileSteerResolver.Resolve(MapWithTrees((5, 6), (6, 5), (6, 6)), At, TileDirection.NE, 1));
}
