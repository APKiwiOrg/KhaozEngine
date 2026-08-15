using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileCollisionStepTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileCollisionMap Map(params (string archetype, int x, int z, int rot)[] objects)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        foreach (var (a, x, z, r) in objects) doc.AddObject(a, x, z, 0, r);
        return TileCollisionBaker.Bake(doc, Cat);
    }

    [Fact]
    public void Open_ground_allows_all_eight_directions()
    {
        TileCollisionMap map = Map();
        foreach (TileDirection d in TileDirections.All) Assert.True(TileCollision.CanStep(map, 10, 10, 0, d));
    }

    [Fact]
    public void A_wall_blocks_the_edge_in_both_directions_and_the_diagonals_around_it()
    {
        TileCollisionMap map = Map(("wall", 10, 10, 2));
        Assert.False(TileCollision.CanStep(map, 10, 10, 0, TileDirection.E));
        Assert.False(TileCollision.CanStep(map, 11, 10, 0, TileDirection.W));
        Assert.True(TileCollision.CanStep(map, 10, 10, 0, TileDirection.N));
        Assert.False(TileCollision.CanStep(map, 10, 10, 0, TileDirection.NE));
        Assert.False(TileCollision.CanStep(map, 10, 9, 0, TileDirection.NE));
        Assert.False(TileCollision.CanStep(map, 11, 11, 0, TileDirection.SW));
    }

    [Fact]
    public void A_blocked_tile_forbids_entering_it_and_cutting_its_corner()
    {
        TileCollisionMap map = Map(("tree", 11, 10, 0));
        Assert.False(TileCollision.CanStep(map, 10, 10, 0, TileDirection.E));
        Assert.False(TileCollision.CanStep(map, 10, 9, 0, TileDirection.NE));
        Assert.False(TileCollision.CanStep(map, 10, 11, 0, TileDirection.SE));
        Assert.True(TileCollision.CanStep(map, 10, 10, 0, TileDirection.N));
        Assert.True(TileCollision.IsBlocked(map, 11, 10, 0));
    }

    [Fact]
    public void A_corner_piece_blocks_the_diagonal_across_it()
    {
        TileCollisionMap map = Map(("wall_corner", 10, 10, 0));
        Assert.False(TileCollision.CanStep(map, 10, 10, 0, TileDirection.NW));
        Assert.False(TileCollision.CanStep(map, 9, 11, 0, TileDirection.SE));
        Assert.True(TileCollision.CanStep(map, 10, 10, 0, TileDirection.SE));
    }

    [Fact]
    public void Steps_off_the_world_are_refused()
    {
        TileCollisionMap map = Map();
        Assert.False(TileCollision.CanStep(map, 0, 0, 0, TileDirection.W));
        Assert.False(TileCollision.CanStep(map, 63, 63, 0, TileDirection.NE));
    }

    [Fact]
    public void A_2x2_agent_needs_its_whole_leading_edge_clear()
    {
        TileCollisionMap map = Map(("tree", 12, 11, 0));
        Assert.True(TileCollision.CanStep(map, 10, 10, 0, TileDirection.N, 2));
        Assert.False(TileCollision.CanStep(map, 10, 10, 0, TileDirection.E, 2));
        Assert.True(TileCollision.CanStep(map, 10, 8, 0, TileDirection.E, 2));
        Assert.False(TileCollision.CanStep(map, 10, 9, 0, TileDirection.NE, 2));
        Assert.False(TileCollision.CanStep(map, 62, 62, 0, TileDirection.E, 2));
    }
}
