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

    [Theory]
    [InlineData("open diagonal", TileDirection.NE, TileDirection.NE, new int[] { })]
    [InlineData("blocked straight", TileDirection.N, null, new[] { 5, 6 })]
    [InlineData("east wall slides north", TileDirection.NE, TileDirection.N, new[] { 6, 5, 6, 6 })]
    [InlineData("north wall slides east", TileDirection.NE, TileDirection.E, new[] { 5, 6, 6, 6 })]
    [InlineData("both axes open prefers north", TileDirection.NE, TileDirection.N, new[] { 6, 6 })]
    [InlineData("southwest corner prefers south", TileDirection.SW, TileDirection.S, new[] { 4, 4 })]
    [InlineData("diagonal and both axes blocked", TileDirection.NE, null, new[] { 5, 6, 6, 5, 6, 6 })]
    public void Resolve_SlidesOrStandsAccordingToTheBlockedTiles(
        string scenario, TileDirection held, TileDirection? expected, int[] treeCoordinates)
    {
        var trees = new (int X, int Z)[treeCoordinates.Length / 2];
        for (int i = 0; i < trees.Length; i++)
            trees[i] = (treeCoordinates[2 * i], treeCoordinates[2 * i + 1]);

        Assert.True(expected == TileSteerResolver.Resolve(MapWithTrees(trees), At, held, 1), scenario);
    }

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
