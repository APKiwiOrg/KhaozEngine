using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The two things every agentSize overload of <see cref="TileReach"/> promises and no test asked of them: a size
/// below 1 throws, and the plane argument is the only plane the answer is about. The one tile forms are covered in
/// <see cref="TileReachTests"/>, and the sizes here run through the delegating size of 1 as well as the NxN body,
/// because the delegation is where a refusal is easiest to lose.
/// <para>The map carries TWO walkable planes over the same ground, which is what makes a cross-plane answer a real
/// coordinate rather than an unloaded region reading blocked. The third plane is deliberately left unbuilt, so the
/// "no reach at all" case is here too.</para>
/// </summary>
public class TileReachOverloadTests
{
    const int Ground = 0, Upper = 1, Unbuilt = 2;

    static readonly TileRect Target = new(20, 20, 2, 2);

    // Plane 0 comes flat out of the shared helper and plane 1 is given the same underlay, so the two are the same
    // ground twice and any difference in an answer is the plane argument and nothing else.
    static TileCollisionMap TwoWalkablePlanes()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileRect rect = new RegionCoord(0, 0).Rect;
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++) doc.SetUnderlay(x, z, Upper, 1);
        return TileMoveSimulatorTests.Bake(doc);
    }

    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public void Set_anchors_carry_the_plane_it_was_asked_for_and_a_size_below_one_throws(int n)
    {
        TileCollisionMap map = TwoWalkablePlanes();

        IReadOnlyList<TileCoord> upper = TileReach.Set(map, Target, Upper, n);
        Assert.NotEmpty(upper);
        Assert.All(upper, a => Assert.Equal(Upper, a.Plane));
        Assert.Equal(TileReach.Set(map, Target, Ground, n).Count, upper.Count);   // the same ground twice

        // A plane nothing was built on reads blocked everywhere, so the target has no reach tile there at all. The
        // footprint's own tiles being walkable one floor down buys it nothing.
        Assert.Empty(TileReach.Set(map, Target, Unbuilt, n));

        Assert.Throws<ArgumentOutOfRangeException>("agentSize", () => { TileReach.Set(map, Target, Ground, 0); });
        Assert.Throws<ArgumentOutOfRangeException>("agentSize", () => { TileReach.Set(map, Target, Ground, -1); });
        Assert.Throws<ArgumentNullException>("map", () => { TileReach.Set(null!, Target, Ground, n); });
    }

    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public void Contains_answers_false_across_planes_and_a_size_below_one_throws(int n)
    {
        TileCollisionMap map = TwoWalkablePlanes();
        var upstairs = new TileCoord(20 - n, 20, Upper);             // due west of the target, in range at every size
        var downstairs = new TileCoord(20 - n, 20, Ground);

        Assert.True(TileReach.Contains(map, Target, Upper, upstairs, n));
        Assert.True(TileReach.Contains(map, Target, Ground, downstairs, n));

        // The same x and z on the other floor, which is the whole point: a body one plane off a target it is
        // standing right beside in x and z is not in reach of it.
        Assert.False(TileReach.Contains(map, Target, Ground, upstairs, n), "a body a floor up is not in reach");
        Assert.False(TileReach.Contains(map, Target, Upper, downstairs, n), "a body a floor down is not in reach");
        Assert.False(TileReach.Contains(map, Target, Unbuilt, new TileCoord(20 - n, 20, Unbuilt), n));

        Assert.Throws<ArgumentOutOfRangeException>("agentSize",
            () => { TileReach.Contains(map, Target, Ground, downstairs, 0); });
        Assert.Throws<ArgumentOutOfRangeException>("agentSize",
            () => { TileReach.Contains(map, Target, Ground, downstairs, -1); });
        Assert.Throws<ArgumentNullException>("map", () => { TileReach.Contains(null!, Target, Ground, downstairs, n); });
    }

    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public void FacingToward_reads_geometry_on_any_plane_and_a_size_below_one_throws(int n)
    {
        TileCollisionMap map = TwoWalkablePlanes();
        var west = new TileCoord(20 - n, 20, Upper);
        var east = new TileCoord(22, 20, Upper);

        // The plane is carried for symmetry with the rest of the type and nothing in the body reads it, so two rects
        // that touch answer the side they touch on whichever plane the caller names, an unbuilt one included. That
        // is the contract as documented, and it is what a later rule consulting the walls would have to change.
        foreach (int plane in new[] { Ground, Upper, Unbuilt })
        {
            Assert.Equal(TileDirection.E, TileReach.FacingToward(map, Target, plane, west, n));
            Assert.Equal(TileDirection.W, TileReach.FacingToward(map, Target, plane, east, n));
        }

        Assert.Throws<ArgumentOutOfRangeException>("agentSize",
            () => { TileReach.FacingToward(map, Target, Ground, west, 0); });
        Assert.Throws<ArgumentOutOfRangeException>("agentSize",
            () => { TileReach.FacingToward(map, Target, Ground, west, -1); });
        Assert.Throws<ArgumentNullException>("map", () => { TileReach.FacingToward(null!, Target, Ground, west, n); });
    }
}
