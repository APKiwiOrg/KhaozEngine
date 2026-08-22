using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileMoveSimulatorTests
{
    internal static readonly TileWorldCatalogs Catalogs = TileWorldCatalogs.Greybox();
    internal const float Dt = 0.25f;
    internal static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    internal static TileWorldDocument FlatWorld(int planeCount = 4, params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument { Id = "test", DisplayName = "Test", PlaneCount = planeCount };
        if (regions.Length == 0) regions = new[] { new RegionCoord(0, 0) };
        foreach (RegionCoord c in regions)
        {
            doc.GetOrCreateRegion(c);
            TileRect rect = c.Rect;
            for (int z = rect.Z; z < rect.Z1; z++)
                for (int x = rect.X; x < rect.X1; x++) doc.SetUnderlay(x, z, 0, 1);
        }
        return doc;
    }

    internal static TileCollisionMap Bake(TileWorldDocument doc) => TileCollisionBaker.Bake(doc, Catalogs);

    static TileMoveSimulator Sim(TileCollisionMap map) => new(map, Ticks);

    static TileMoveState Run(TileMoveSimulator sim, TileMoveState s, TileCommand first, int ticks)
    {
        s = sim.Step(s, first, Dt);
        for (int i = 1; i < ticks; i++) s = sim.Step(s, TileCommand.None, Dt);
        return s;
    }

    [Fact]
    public void Two_instances_on_the_same_inputs_stay_byte_identical()
    {
        TileWorldDocument doc = FlatWorld();
        TileMoveSimulator a = Sim(Bake(doc)), b = Sim(Bake(doc));
        TileMoveState sa = TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N);
        TileMoveState sb = sa;
        var cmd = TileCommand.WalkTo(new TileCoord(11, 9, 0), TileMoveMode.Run);
        for (int i = 0; i < 40; i++)
        {
            TileCommand c = i == 0 ? cmd : TileCommand.None;
            sa = a.Step(sa, c, Dt);
            sb = b.Step(sb, c, Dt);
            Assert.Equal(sa, sb);
        }
        Assert.Equal(new TileCoord(11, 9, 0), sa.Tile);
    }

    [Fact]
    public void A_run_commits_a_tile_every_two_ticks_and_a_walk_every_four()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 4, 0), TileMoveMode.Run), 1);
        Assert.Equal(new TileCoord(0, 0, 0), s.Tile);
        s = sim.Step(s, TileCommand.None, Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);
        Assert.Equal(0, s.StepTicks);

        TileMoveState w = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        w = Run(sim, w, TileCommand.WalkTo(new TileCoord(0, 4, 0), TileMoveMode.Walk), 3);
        Assert.Equal(new TileCoord(0, 0, 0), w.Tile);
        w = sim.Step(w, TileCommand.None, Dt);
        Assert.Equal(new TileCoord(0, 1, 0), w.Tile);
    }

    [Fact]
    public void A_diagonal_step_costs_the_same_as_a_cardinal_one_and_sets_the_facing()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(3, 3, 0), TileMoveMode.Run), 2);
        Assert.Equal(new TileCoord(1, 1, 0), s.Tile);
        Assert.Equal(TileDirection.NE, s.Facing);
    }

    [Fact]
    public void A_corner_is_never_cut()
    {
        TileWorldDocument doc = FlatWorld();
        doc.AddObject("tree", 1, 0, 0, 0);
        doc.AddObject("tree", 0, 1, 0, 0);
        TileMoveSimulator sim = Sim(Bake(doc));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(1, 1, 0), TileMoveMode.Run), 12);
        Assert.NotEqual(new TileCoord(1, 1, 0), s.Tile);
        Assert.Equal(new TileCoord(0, 0, 0), s.Tile);
    }

    [Fact]
    public void An_unreachable_goal_walks_to_the_nearest_reachable_tile()
    {
        TileWorldDocument doc = FlatWorld();
        // The column has to span the WHOLE region. A partial wall leaves a way round its end, and the
        // pathfinder finds it, so the goal is reachable and nothing here is testing the fallback.
        for (int z = 0; z < TileRegion.Size; z++) doc.AddObject("tree", 5, z, 0, 0);
        TileMoveSimulator sim = Sim(Bake(doc));
        TileMoveState s = TileMoveState.At(new TileCoord(2, 5, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(8, 5, 0), TileMoveMode.Run), 60);
        Assert.True(s.Route.IsIdle);
        Assert.True(s.Tile.X < 5);
    }

    [Fact]
    public void A_blocker_that_appears_mid_route_re_paths_once_and_then_stands()
    {
        TileWorldDocument doc = FlatWorld();
        TileCollisionMap map = Bake(doc);
        var sim = new TileMoveSimulator(map, Ticks);
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Run), 4);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);

        // Wall the whole row off, then keep ticking: the re-path fails and the route is dropped. The row spans
        // the region because the world either side of it is open, so a few tiles would only make the walk longer.
        for (int x = 0; x < TileRegion.Size; x++) doc.AddObject("tree", x, 3, 0, 0);
        TileCollisionBaker.Rebake(map, doc, Catalogs, new TileRect(0, 2, TileRegion.Size, 3), 0);
        for (int i = 0; i < 6; i++) s = sim.Step(s, TileCommand.None, Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);
    }

    [Fact]
    public void A_blocker_with_a_way_round_re_paths_and_still_arrives()
    {
        TileWorldDocument doc = FlatWorld();
        TileCollisionMap map = Bake(doc);
        var sim = new TileMoveSimulator(map, Ticks);
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Run), 4);
        doc.AddObject("tree", 0, 3, 0, 0);
        TileCollisionBaker.Rebake(map, doc, Catalogs, new TileRect(-1, 2, 3, 3), 0);
        for (int i = 0; i < 30; i++) s = sim.Step(s, TileCommand.None, Dt);
        Assert.Equal(new TileCoord(0, 6, 0), s.Tile);
    }

    [Fact]
    public void A_second_walk_command_replaces_the_route_from_where_the_player_stands()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 8, 0), TileMoveMode.Run), 4);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(4, 2, 0), TileMoveMode.Walk), Dt);
        Assert.Equal(TileMoveMode.Walk, s.Mode);
        Assert.Equal(4, s.StepTotal);
        // One, not zero: the command reset step progress and then this same tick advanced the new step by one.
        Assert.Equal(1, s.StepTicks);
        Assert.Equal(new TileCoord(4, 2, 0), s.Route.End);
    }

    [Fact]
    public void A_goal_the_player_already_stands_on_just_stands()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(4, 4, 0), TileDirection.S);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(4, 4, 0), TileMoveMode.Run), Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(4, 4, 0), s.Tile);
        Assert.Equal(TileDirection.S, s.Facing);
    }

    // Carried over from the task 2 review: StepFraction is what the presenter glides on, so a value outside 0 to 1
    // would put a remote past the tile it is walking into. The tick a step COMMITS is the one that could do it,
    // because that is the tick the count reaches the total, so the walk below covers several commits in both modes.
    [Theory]
    [InlineData(TileMoveMode.Walk)]
    [InlineData(TileMoveMode.Run)]
    public void The_step_fraction_stays_inside_zero_to_one_on_every_tick_including_a_commit(TileMoveMode mode)
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        TileCoord previous = s.Tile;
        int commits = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            TileCommand c = tick == 0 ? TileCommand.WalkTo(new TileCoord(6, 6, 0), mode) : TileCommand.None;
            s = sim.Step(s, c, Dt);
            if (!s.Tile.Equals(previous)) { commits++; previous = s.Tile; }
            Assert.InRange(s.StepFraction, 0f, 1f);
        }
        Assert.Equal(6, commits);
        Assert.Equal(new TileCoord(6, 6, 0), s.Tile);
    }
}
