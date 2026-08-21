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

    [Fact]
    public void An_idle_state_sits_exactly_on_its_tile()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(3, 7, 0), TileDirection.N);
        Assert.Equal(new Vector2(3f, 7f), s.Position);
        Assert.Equal(0f, s.Vertical);
        Assert.True(s.Route.IsIdle);
    }

    [Fact]
    public void Position_interpolates_toward_the_next_route_tile_by_step_fraction()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(3, 7, 0), TileDirection.N);
        s.Route = RouteOf((3, 8));
        s.StepTotal = 4;
        s.StepTicks = 1;
        Assert.Equal(new Vector2(3f, 7.25f), s.Position);
        s.StepTicks = 3;
        Assert.Equal(new Vector2(3f, 7.75f), s.Position);
    }

    [Fact]
    public void Position_on_a_diagonal_moves_on_both_axes()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.NE);
        s.Route = RouteOf((1, 1));
        s.StepTotal = 2;
        s.StepTicks = 1;
        Assert.Equal(new Vector2(0.5f, 0.5f), s.Position);
    }

    [Fact]
    public void Vertical_is_the_plane_index()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 2), TileDirection.N);
        Assert.Equal(2f, s.Vertical);
    }

    [Fact]
    public void WithRenderState_only_touches_the_presentation_fields()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(3, 7, 0), TileDirection.N);
        TileMoveState r = s.WithRenderState(new Vector2(3.4f, 7.1f), 0.5f);
        Assert.Equal(s.Tile, r.Tile);
        Assert.Equal(s.Facing, r.Facing);
        Assert.True(r.HasRenderOverride);
        Assert.Equal(new Vector2(3.4f, 7.1f), r.RenderPosition);
        Assert.Equal(0.5f, r.RenderVertical);
        Assert.False(s.HasRenderOverride);
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
    public void Next_on_an_idle_route_throws_rather_than_answering_a_tile()
    {
        Assert.Throws<InvalidOperationException>(() => TileRoute.None.Next);
    }
}
