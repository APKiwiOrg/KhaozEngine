using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileMoveStateTests
{
    static TileRoute RouteOf(params (int x, int z)[] tiles)
    {
        var list = new TileCoord[tiles.Length];
        for (int i = 0; i < tiles.Length; i++) list[i] = new TileCoord(tiles[i].x, tiles[i].z, 0);
        return new TileRoute(list, 0);
    }

    // A step in flight, spelled the way the simulator spells it: the tile is already the one being walked INTO and
    // StepFrom is the one being left, so the drawn position runs from StepFrom up to Tile rather than from Tile
    // toward a route tile.
    static TileMoveState Stepping(TileCoord from, TileCoord into, byte ticks, byte total)
    {
        TileMoveState s = TileMoveState.At(into, TileRoute.Direction(from, into));
        s.StepFrom = from;
        s.StepTicks = ticks;
        s.StepTotal = total;
        return s;
    }

    [Fact]
    public void A_state_with_no_step_in_flight_sits_exactly_on_its_tile()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(3, 7, 0), TileDirection.N);
        Assert.Equal(new Vector2(3f, 7f), s.Position);
        Assert.Equal(0f, s.Vertical);
        Assert.True(s.Route.IsIdle);
        // A placement seeds the glide origin onto the tile itself, which is what says "standing" in one field.
        Assert.Equal(s.Tile, s.StepFrom);
        Assert.False(s.IsStepping);
        Assert.Equal(0f, s.StepFraction);
    }

    [Fact]
    public void Position_glides_from_the_departed_tile_into_the_committed_one_by_step_fraction()
    {
        TileMoveState s = Stepping(new TileCoord(3, 7, 0), new TileCoord(3, 8, 0), ticks: 1, total: 4);
        Assert.True(s.IsStepping);
        Assert.Equal(new Vector2(3f, 7.25f), s.Position);
        s.StepTicks = 3;
        Assert.Equal(new Vector2(3f, 7.75f), s.Position);
        // The fraction filling puts the body exactly on the tile the state already named, which is the only place
        // the glide can end. The simulator normalizes rather than parking here, but a decoded frame may say it.
        s.StepTicks = 4;
        Assert.Equal(new Vector2(3f, 8f), s.Position);
    }

    // The ROUTE is not consulted at all, which is what lets an observer draw a remote correctly off a snapshot that
    // deliberately carries no route. A route pointing somewhere else entirely cannot move the drawn position.
    [Fact]
    public void Position_ignores_the_route_and_reads_only_the_two_tiles_of_the_step()
    {
        TileMoveState s = Stepping(new TileCoord(3, 7, 0), new TileCoord(3, 8, 0), ticks: 2, total: 4);
        s.Route = RouteOf((9, 9), (9, 10));
        Assert.Equal(new Vector2(3f, 7.5f), s.Position);
    }

    [Fact]
    public void Position_on_a_diagonal_moves_on_both_axes()
    {
        TileMoveState s = Stepping(new TileCoord(0, 0, 0), new TileCoord(1, 1, 0), ticks: 1, total: 2);
        Assert.Equal(new Vector2(0.5f, 0.5f), s.Position);
    }

    [Fact]
    public void Vertical_is_the_plane_index()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 2), TileDirection.N);
        Assert.Equal(2f, s.Vertical);
    }

    // The glide origin is SIMULATION state, so two states that differ only in it are two different states. Left out
    // of equality, a reconciliation would accept a basis whose body is walking out of a different tile than the
    // prediction's and the two heads would draw the same step from two places.
    [Fact]
    public void The_glide_origin_is_compared_by_equality()
    {
        TileMoveState a = Stepping(new TileCoord(3, 7, 0), new TileCoord(3, 8, 0), ticks: 1, total: 4);
        TileMoveState b = a;
        b.StepFrom = new TileCoord(4, 8, 0);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void WithRenderState_only_touches_the_presentation_fields()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(3, 7, 0), TileDirection.N);
        TileMoveState r = s.WithRenderState(new Vector2(3.4f, 7.1f), 0.5f);
        Assert.Equal(s.Tile, r.Tile);
        Assert.Equal(s.StepFrom, r.StepFrom);
        Assert.Equal(s.Facing, r.Facing);
        Assert.True(r.HasRenderOverride);
        Assert.Equal(new Vector2(3.4f, 7.1f), r.RenderPosition);
        Assert.Equal(0.5f, r.RenderVertical);
        Assert.False(s.HasRenderOverride);

        // The claim the whole type doc rests on: a render stamp is invisible to equality, so a smoothed frame can
        // never read as a misprediction and the presentation fields cannot perturb determinism.
        Assert.Equal(s, r);
        Assert.Equal(s.GetHashCode(), r.GetHashCode());
    }

    [Fact]
    public void A_route_advances_by_index_and_reports_idle_at_its_end()
    {
        TileRoute r = RouteOf((1, 0), (2, 0));
        Assert.Equal(new TileCoord(1, 0, 0), r.Next);
        Assert.Equal(new TileCoord(2, 0, 0), r.End);
        TileRoute a = r.Advanced();
        Assert.Equal(new TileCoord(2, 0, 0), a.Next);
        Assert.True(a.Advanced().IsIdle);
        Assert.True(TileRoute.None.IsIdle);
    }

    [Fact]
    public void A_route_round_trips_through_its_step_directions()
    {
        var from = new TileCoord(10, 10, 0);
        TileRoute r = new(new[]
        {
            new TileCoord(11, 10, 0), new TileCoord(11, 11, 0), new TileCoord(12, 12, 0),
        }, 0);
        TileDirection[] steps = r.RemainingSteps(from);
        Assert.Equal(new[] { TileDirection.E, TileDirection.N, TileDirection.NE }, steps);
        TileRoute back = TileRoute.FromSteps(from, steps);
        Assert.Equal(r, back);
    }

    [Fact]
    public void Route_equality_compares_the_remaining_tiles_not_the_array_reference()
    {
        TileRoute a = new(new[] { new TileCoord(1, 0, 0), new TileCoord(2, 0, 0) }, 1);
        TileRoute b = new(new[] { new TileCoord(9, 9, 0), new TileCoord(2, 0, 0) }, 1);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Routes_that_differ_in_their_remaining_walk_are_not_equal()
    {
        // Every other equality assertion in this file is positive, so an Equals that returned true unconditionally
        // would pass them all. These are the other side of it.
        Assert.NotEqual(RouteOf((1, 0), (2, 0)), RouteOf((1, 0), (3, 0)));
        Assert.NotEqual(RouteOf((1, 0)), RouteOf((1, 0), (2, 0)));

        var tiles = new[] { new TileCoord(1, 0, 0), new TileCoord(2, 0, 0) };
        Assert.NotEqual(new TileRoute(tiles, 0), new TileRoute(tiles, 1));
    }

    // The licence for the reference fast path in Equals. It has to agree with the slice comparison in every
    // direction, and the case that catches a fast path answering on the reference ALONE is the two spellings of an
    // empty route: a defaulted struct holds a null backing field and TileRoute.None holds Array.Empty, so they are
    // not reference equal and are still the same route.
    [Fact]
    public void Route_equality_agrees_with_the_slice_whether_or_not_the_two_share_a_backing_list()
    {
        var tiles = new[] { new TileCoord(1, 0, 0), new TileCoord(2, 0, 0), new TileCoord(3, 0, 0) };
        var copy = (TileCoord[])tiles.Clone();

        for (int i = 0; i <= tiles.Length; i++)
        {
            Assert.Equal(new TileRoute(tiles, i), new TileRoute(tiles, i));    // one list, one index
            Assert.Equal(new TileRoute(tiles, i), new TileRoute(copy, i));     // equal walk, two lists
            Assert.Equal(new TileRoute(tiles, i).GetHashCode(), new TileRoute(copy, i).GetHashCode());
            for (int j = i + 1; j <= tiles.Length; j++)
                Assert.NotEqual(new TileRoute(tiles, i), new TileRoute(tiles, j));
        }

        Assert.Equal(TileRoute.None, default(TileRoute));
        Assert.Equal(TileRoute.None.GetHashCode(), default(TileRoute).GetHashCode());
        Assert.Equal(TileRoute.None, TileRoute.None);
    }

    [Fact]
    public void A_defaulted_route_reads_as_an_empty_one_rather_than_a_null_list()
    {
        // Tiles is annotated non-null, so nullable analysis will not warn a consumer off Route.Tiles.Count. A
        // zero-filled ECS column and a missed lookup both hand out a defaulted struct, so the annotation has to be
        // true rather than documented around.
        Assert.Empty(default(TileRoute).Tiles);
        Assert.True(default(TileRoute).IsIdle);
        Assert.Empty(default(TileMoveState).Route.Tiles);
    }

    [Fact]
    public void The_epoch_surfaces_as_the_teleport_epoch_and_survives_the_withers()
    {
        // The tile twin of KhaozEngine.Server.Tests/NetWorld/TeleportEpochTests.cs. The epoch is what makes
        // reconciliation cut instead of glide, so a wither that dropped it would show up as an avatar sliding
        // through a teleport in an integration run rather than as a red unit test here.
        TileMoveState s = TileMoveState.At(new TileCoord(1, 1, 0), TileDirection.N);
        s.Epoch = 11u;
        Assert.Equal(11u, s.TeleportEpoch);
        Assert.Equal(11u, s.WithRenderState(new Vector2(1.5f, 1f), 0f).TeleportEpoch);
        Assert.Equal(11u, s.WithPosition(new Vector2(1.5f, 1f)).TeleportEpoch);

        TileMoveState bumped = s;
        bumped.Epoch = 12u;
        Assert.NotEqual(s, bumped);
    }

    [Fact]
    public void Next_on_an_idle_route_throws_rather_than_answering_a_tile()
    {
        Assert.Throws<InvalidOperationException>(() => TileRoute.None.Next);
    }
}
