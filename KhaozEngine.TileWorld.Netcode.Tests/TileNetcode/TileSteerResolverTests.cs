using System;
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
    public void A_diagonal_with_its_corner_and_both_axis_steps_blocked_stands() =>
        Assert.Null(TileSteerResolver.Resolve(MapWithTrees((5, 6), (6, 5), (6, 6)), At, TileDirection.NE, 1));

    // Walled in on all eight sides, which is what the test above used to claim. Asked of every direction, because
    // the slide has an answer for a diagonal whenever either of its axis steps is open and the straight steps are
    // what close that off.
    [Theory]
    [InlineData(TileDirection.W)]
    [InlineData(TileDirection.E)]
    [InlineData(TileDirection.S)]
    [InlineData(TileDirection.N)]
    [InlineData(TileDirection.SW)]
    [InlineData(TileDirection.SE)]
    [InlineData(TileDirection.NW)]
    [InlineData(TileDirection.NE)]
    public void An_enclosed_body_stands(TileDirection held) =>
        Assert.Null(TileSteerResolver.Resolve(
            MapWithTrees((4, 4), (5, 4), (6, 4), (4, 5), (6, 5), (4, 6), (5, 6), (6, 6)), At, held, 1));

    // Resolve is public and it does not bound its own direction: the decoder and TileMoveSimulator.Accepts are the
    // gates, so a value outside the eight is an in-process misuse and is refused loudly rather than answered with a
    // stand that would look like a wall.
    [Fact]
    public void A_direction_outside_the_eight_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TileSteerResolver.Resolve(MapWithTrees(), At, (TileDirection)8, 1));
}
