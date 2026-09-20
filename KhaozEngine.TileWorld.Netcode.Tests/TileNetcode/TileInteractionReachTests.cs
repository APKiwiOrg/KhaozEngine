using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileInteractionReachTests
{
    const TileInteractionReachPolicy Wide =
        TileInteractionReachPolicy.IncludeDiagonals | TileInteractionReachPolicy.IncludeOverlap;

    [Fact]
    public void A_two_by_two_target_accepts_every_perimeter_and_interior_tile_without_movement()
    {
        TileCollisionMap map = OpenMap();
        var target = new TileRect(10, 10, 2, 2);
        var targets = new FixedTarget(7, target, Wide);
        var simulator = new TileMoveSimulator(map, TileMoveSimulatorTests.Ticks, combatTargets: targets);
        var accepted = new List<TileCoord>();

        for (int z = 9; z <= 12; z++)
            for (int x = 9; x <= 12; x++)
            {
                var start = new TileCoord(x, z, 0);
                TileMoveState state = simulator.Step(TileMoveState.At(start, TileDirection.W),
                    TileCommand.InteractEntity(7, TileMoveMode.Run), TileMoveSimulatorTests.Dt);
                Assert.Equal(start, state.Tile);
                Assert.True(state.Route.IsIdle);
                Assert.Equal(7, state.InteractTarget);
                accepted.Add(start);
            }

        Assert.Equal(16, accepted.Count);
        Assert.Equal(12, accepted.Count(tile => !target.Contains(tile.X, tile.Z)));
        Assert.Equal(4, accepted.Count(tile => target.Contains(tile.X, tile.Z)));
    }

    [Fact]
    public void Default_interactions_remain_cardinal_and_never_overlap()
    {
        TileCollisionMap map = OpenMap();
        var target = new TileRect(10, 10, 1, 1);
        Assert.Equal(TileReach.Set(map, target, 0),
            TileInteractionReach.Set(map, target, 0, 1, TileInteractionReachPolicy.Default));
        Assert.False(TileInteractionReach.Contains(map, target, 0, new TileCoord(9, 9, 0), 1,
            TileInteractionReachPolicy.Default));
        Assert.False(TileInteractionReach.Contains(map, target, 0, new TileCoord(10, 10, 0), 1,
            TileInteractionReachPolicy.Default));
    }

    [Fact]
    public void Diagonal_reach_obeys_blocked_wall_and_corner_collision()
    {
        var target = new TileRect(10, 10, 1, 1);
        var diagonal = new TileCoord(9, 9, 0);
        foreach ((int x, int z, TileCollisionFlags flags) in new[]
                 {
                     (9, 9, TileCollisionFlags.Blocked),
                     (9, 10, TileCollisionFlags.Blocked),
                     (10, 9, TileCollisionFlags.Blocked),
                     (10, 10, TileCollisionFlags.WallW),
                     (10, 10, TileCollisionFlags.WallS),
                     (10, 10, TileCollisionFlags.CornerSW),
                 })
        {
            TileCollisionMap map = OpenMap();
            map.Or(x, z, 0, flags);
            Assert.False(TileInteractionReach.Contains(map, target, 0, diagonal, 1,
                TileInteractionReachPolicy.IncludeDiagonals));
        }
    }

    [Fact]
    public void Overlap_requires_a_valid_standing_footprint_and_never_crosses_planes()
    {
        var target = new TileRect(10, 10, 2, 2);
        TileCollisionMap map = OpenMap();
        var interior = new TileCoord(10, 10, 0);
        Assert.True(TileInteractionReach.Contains(map, target, 0, interior, 1, Wide));
        Assert.False(TileInteractionReach.Contains(map, target, 0, interior with { Plane = 1 }, 1, Wide));

        map.Or(10, 10, 0, TileCollisionFlags.Blocked);
        Assert.False(TileInteractionReach.Contains(map, target, 0, interior, 1, Wide));
    }

    [Fact]
    public void Larger_agents_use_the_same_corner_overlap_and_standing_rules()
    {
        var target = new TileRect(20, 20, 2, 2);
        TileCollisionMap map = OpenMap();
        Assert.True(TileInteractionReach.Contains(map, target, 0, new TileCoord(18, 18, 0), 2, Wide));
        Assert.True(TileInteractionReach.Contains(map, target, 0, new TileCoord(19, 20, 0), 2, Wide));
        Assert.False(TileInteractionReach.Contains(map, target, 0, new TileCoord(19, 20, 0), 2,
            TileInteractionReachPolicy.Default));

        map.Or(20, 20, 0, TileCollisionFlags.CornerSW);
        Assert.False(TileInteractionReach.Contains(map, target, 0, new TileCoord(18, 18, 0), 2, Wide));
    }

    [Fact]
    public void A_distant_interaction_approaches_the_nearest_policy_reach_tile()
    {
        TileCollisionMap map = OpenMap();
        var target = new TileRect(10, 10, 2, 2);
        var targets = new FixedTarget(7, target, Wide);
        var simulator = new TileMoveSimulator(map, TileMoveSimulatorTests.Ticks, combatTargets: targets);
        TileMoveState state = TileMoveState.At(new TileCoord(4, 6, 0), TileDirection.W);

        state = simulator.Step(state, TileCommand.InteractEntity(7, TileMoveMode.Run),
            TileMoveSimulatorTests.Dt);
        for (int i = 0; i < 80 && !state.Route.IsIdle; i++)
            state = simulator.Step(state, TileCommand.Continue(TileMoveMode.Run), TileMoveSimulatorTests.Dt);

        Assert.Equal(7, state.InteractTarget);
        Assert.True(TileInteractionReach.Contains(map, target, 0, state.Tile, 1, Wide));
    }

    [Fact]
    public void Diagonal_and_coincident_overlap_facing_are_deterministic()
    {
        TileCollisionMap map = OpenMap();
        var target = new TileRect(10, 10, 2, 2);
        Assert.Equal(TileDirection.NE, TileInteractionReach.FacingToward(
            map, target, 0, new TileCoord(9, 9, 0), 1, Wide));
        Assert.Equal(TileDirection.NE, TileInteractionReach.FacingToward(
            map, target, 0, new TileCoord(10, 10, 0), 1, Wide));
        Assert.Equal(TileDirection.W, TileInteractionReach.FacingToward(
            map, target, 0, new TileCoord(10, 10, 0), 2, Wide));
    }

    static TileCollisionMap OpenMap()
    {
        var map = new TileCollisionMap(2);
        map.EnsureRegion(new RegionCoord(0, 0));
        return map;
    }

    sealed class FixedTarget(long id, TileRect footprint, TileInteractionReachPolicy policy) : ITileTargets
    {
        public bool TryGetFootprint(long target, out TileRect found, out int plane)
        {
            found = footprint;
            plane = 0;
            return target == id;
        }

        public TileInteractionReachPolicy GetInteractionReachPolicy(long target) =>
            target == id ? policy : TileInteractionReachPolicy.Default;
    }
}
