using System;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// THE FOOTPRINT HALF OF THE DRAW RULE. A settled body's stack is every tile of its NxN square rather than its
/// anchor alone, so a one-tile body standing in a cow's rump is IN the cow's stack and one of the two is hidden,
/// decided by the same comparison that decides two bodies sharing one tile.
/// <para>A moving body keeps the answer it has today under each policy, because the widening is about a body at
/// REST covering ground. The size-less overloads read every body as one tile, which is what keeps every roster
/// written against the older rule answering exactly as it did.</para>
/// </summary>
public class TileDrawPriorityFootprintTests
{
    const float Frame = 1f / 60f;
    static readonly TileCoord Away = new(0, 0, 0);
    static readonly TileCoord Anchor = new(10, 10, 0);
    // Inside a 2x2 anchored on Anchor and NOT its anchor tile, which is the whole case #899 describes.
    static readonly TileCoord Rump = new(11, 10, 0);

    static (long NetId, TileCoord Tile, float StepProgress, int FootprintSize)[] Bodies(
        params (long NetId, TileCoord Tile, float StepProgress, int FootprintSize)[] bodies) => bodies;

    static (long NetId, TileCoord Tile, float StepProgress)[] Steps(
        params (long NetId, TileCoord Tile, float StepProgress)[] actors) => actors;

    static TileDrawPriority Settled(Comparison<long>? comparison = null) => new()
    {
        Policy = TileDrawPriorityPolicy.SettledStacksOnly,
        SettledComparison = comparison,
    };

    // A consistent order, so the rule is asked the question and not a broken comparison: the named body ranks
    // above every other and a body compared with itself is equal.
    static Comparison<long> Favouring(long winner) =>
        (first, second) => Rank(first, winner).CompareTo(Rank(second, winner));

    static int Rank(long netId, long winner) => netId == winner ? 1 : 0;

    [Fact]
    public void A_settled_one_tile_body_inside_a_two_by_two_is_in_its_stack_under_the_settled_policy()
    {
        var cowWins = Settled(Favouring(20));
        cowWins.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localMoving: false,
            Bodies((20, Anchor, 1f, 2), (7, Rump, 1f, 1)), Frame);

        Assert.Equal(1f, cowWins.Weight(20));
        Assert.Equal(0f, cowWins.Weight(7));
        Assert.True(cowWins.TryGetDrawn(Rump, out long owner));
        Assert.Equal(20L, owner);

        var goblinWins = Settled(Favouring(7));
        goblinWins.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localMoving: false,
            Bodies((20, Anchor, 1f, 2), (7, Rump, 1f, 1)), Frame);

        Assert.Equal(0f, goblinWins.Weight(20));
        Assert.Equal(1f, goblinWins.Weight(7));
        Assert.True(goblinWins.TryGetDrawn(Rump, out owner));
        Assert.Equal(7L, owner);
        // The cow lost its stack, so it holds NONE of its tiles rather than the three nobody contested.
        Assert.False(goblinWins.TryGetDrawn(Anchor, out _));
    }

    [Fact]
    public void A_settled_one_tile_body_inside_a_two_by_two_is_in_its_stack_under_the_default_policy()
    {
        var higherCow = new TileDrawPriority();
        higherCow.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localLeaving: null,
            Bodies((20, Anchor, 1f, 2), (7, Rump, 1f, 1)), Frame);

        Assert.Equal(1f, higherCow.Weight(20));
        Assert.Equal(0f, higherCow.Weight(7));
        Assert.True(higherCow.TryGetDrawn(Rump, out long owner));
        Assert.Equal(20L, owner);

        // The other direction, which under this policy is the higher net id and nothing else.
        var higherGoblin = new TileDrawPriority();
        higherGoblin.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localLeaving: null,
            Bodies((7, Anchor, 1f, 2), (20, Rump, 1f, 1)), Frame);

        Assert.Equal(0f, higherGoblin.Weight(7));
        Assert.Equal(1f, higherGoblin.Weight(20));
        Assert.True(higherGoblin.TryGetDrawn(Rump, out owner));
        Assert.Equal(20L, owner);
        Assert.False(higherGoblin.TryGetDrawn(Anchor, out _));
    }

    [Fact]
    public void A_settled_body_one_tile_outside_the_footprint_is_not_in_the_stack()
    {
        var east = new TileCoord(12, 10, 0);            // one past the 2x2's east edge
        var north = new TileCoord(10, 12, 0);           // one past its north edge
        var priority = new TileDrawPriority();

        priority.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localLeaving: null,
            Bodies((20, Anchor, 1f, 2), (7, east, 1f, 1), (9, north, 1f, 1)), Frame);

        Assert.Equal(1f, priority.Weight(20));
        Assert.Equal(1f, priority.Weight(7));
        Assert.Equal(1f, priority.Weight(9));
        Assert.True(priority.TryGetDrawn(east, out long owner));
        Assert.Equal(7L, owner);
        Assert.True(priority.TryGetDrawn(north, out owner));
        Assert.Equal(9L, owner);
        Assert.True(priority.TryGetDrawn(Anchor, out owner));
        Assert.Equal(20L, owner);
    }

    [Fact]
    public void A_three_by_three_covers_the_one_tile_body_on_its_far_corner()
    {
        var corner = new TileCoord(12, 12, 0);          // the last tile a 3x3 on Anchor covers
        var past = new TileCoord(13, 13, 0);            // the first one it does not
        var priority = Settled(Favouring(20));

        priority.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localMoving: false,
            Bodies((20, Anchor, 1f, 3), (7, corner, 1f, 1), (9, past, 1f, 1)), Frame);

        Assert.Equal(1f, priority.Weight(20));
        Assert.Equal(0f, priority.Weight(7));
        Assert.Equal(1f, priority.Weight(9));
        Assert.True(priority.TryGetDrawn(corner, out long owner));
        Assert.Equal(20L, owner);
        Assert.True(priority.TryGetDrawn(past, out owner));
        Assert.Equal(9L, owner);
    }

    [Fact]
    public void The_size_less_overloads_still_read_every_body_as_one_tile()
    {
        var withProgress = new TileDrawPriority();
        withProgress.Rebuild(TileDrawPriority.NoLocalPlayer, Away, localLeaving: null,
            Steps((20, Anchor, 1f), (7, Rump, 1f)), Frame);

        Assert.Equal(1f, withProgress.Weight(20));
        Assert.Equal(1f, withProgress.Weight(7));

        var cutting = new TileDrawPriority();
        cutting.Rebuild(TileDrawPriority.NoLocalPlayer, Away, localLeaving: null,
            new[] { (20L, Anchor), (7L, Rump) }.AsSpan());

        Assert.Equal(1f, cutting.Weight(20));
        Assert.Equal(1f, cutting.Weight(7));

        var settled = Settled(Favouring(20));
        settled.Rebuild(TileDrawPriority.NoLocalPlayer, Away, localMoving: false,
            Steps((20, Anchor, 1f), (7, Rump, 1f)), Frame);

        Assert.Equal(1f, settled.Weight(20));
        Assert.Equal(1f, settled.Weight(7));

        // And a SIZED roster of all ones is the same answer, so the size is the only thing that widens anything.
        var sized = Settled(Favouring(20));
        sized.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localMoving: false,
            Bodies((20, Anchor, 1f, 1), (7, Rump, 1f, 1)), Frame);

        Assert.Equal(1f, sized.Weight(20));
        Assert.Equal(1f, sized.Weight(7));
    }

    [Fact]
    public void A_moving_large_body_keeps_the_answer_a_moving_one_tile_body_has_under_each_policy()
    {
        // The settled policy: a moving body claims nothing and stays wholly visible, size or no size.
        var settled = Settled(Favouring(20));
        settled.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localMoving: false,
            Bodies((20, Anchor, 0.5f, 2), (7, Rump, 1f, 1)), Frame);

        Assert.Equal(1f, settled.Weight(20));
        Assert.Equal(1f, settled.Weight(7));
        Assert.True(settled.TryGetDrawn(Rump, out long owner));
        Assert.Equal(7L, owner);

        // Landing is what collapses the stack, exactly as it is for a one-tile body arriving on an occupied tile.
        settled.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localMoving: false,
            Bodies((20, Anchor, 1f, 2), (7, Rump, 1f, 1)), Frame);

        Assert.Equal(1f, settled.Weight(20));
        Assert.Equal(0f, settled.Weight(7));

        // The default policy: a mover is judged on the tile it is COMMITTED to, which is the anchor and nothing
        // wider, so a body inside the footprint of a walking cow is still drawn.
        var moving = new TileDrawPriority();
        moving.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localLeaving: null,
            Bodies((20, Anchor, 0.5f, 2), (7, Rump, 1f, 1)), Frame);

        Assert.Equal(1f, moving.Weight(20));
        Assert.Equal(1f, moving.Weight(7));
        Assert.True(moving.TryGetDrawn(Anchor, out owner));
        Assert.Equal(20L, owner);
        Assert.True(moving.TryGetDrawn(Rump, out owner));
        Assert.Equal(7L, owner);
    }

    [Fact]
    public void A_large_local_body_claims_every_tile_of_its_own_footprint()
    {
        var priority = new TileDrawPriority();

        priority.Rebuild(localNetId: 4, Anchor, localFootprintSize: 2, localLeaving: null,
            Bodies((99, Rump, 1f, 1)), Frame);

        Assert.Equal(1f, priority.Weight(4));
        Assert.Equal(0f, priority.Weight(99));          // outright, as it is on the local player's anchor tile
        Assert.True(priority.TryGetDrawn(Rump, out long owner));
        Assert.Equal(4L, owner);
    }

    [Fact]
    public void A_body_hidden_by_a_stack_its_blocker_lost_is_drawn_on_the_tile_nobody_holds()
    {
        // Resolved BEST FIRST, so the goblin that beats the cow takes its tile, the cow is hidden whole, and the
        // second goblin inherits the footprint tile the hidden cow no longer holds.
        var priority = Settled(Favouring(7));

        priority.Rebuild(TileDrawPriority.NoLocalPlayer, Away, 1, localMoving: false,
            Bodies((20, Anchor, 1f, 2), (7, Rump, 1f, 1), (9, Anchor, 1f, 1)), Frame);

        Assert.Equal(1f, priority.Weight(7));
        Assert.Equal(0f, priority.Weight(20));
        Assert.Equal(1f, priority.Weight(9));
        Assert.True(priority.TryGetDrawn(Anchor, out long owner));
        Assert.Equal(9L, owner);
    }
}
