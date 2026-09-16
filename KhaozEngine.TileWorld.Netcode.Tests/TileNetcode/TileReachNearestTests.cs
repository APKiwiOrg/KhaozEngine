using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The oracle for the one-search reach walk. <c>TileReach.TryNearest</c> used to run one
/// <see cref="TilePathfinder.FindPath"/> per candidate anchor and keep the shortest, and it now runs ONE
/// multi-goal search over the same candidate list. That is only allowed to be a cost change, so the per-candidate
/// loop is kept HERE, in <see cref="PerCandidateNearest"/>, and the two are compared over a battery of seeded
/// random maps: same found flag, same anchor, same walk step for step.
/// <para>The reference carries neither the admission refusal nor the Chebyshev prune, so the battery also pins
/// those two as pure optimisations rather than trusting the argument for them.</para>
/// <para>Maps are built straight onto <see cref="TileCollisionMap"/> rather than baked from a document, because
/// the shapes that matter here are collision shapes: a mirrored wall edge, a Blocked tile, a doorway, a sealed
/// ring, and the region edge, which reads Blocked because only region (0, 0) has storage.</para>
/// </summary>
public class TileReachNearestTests
{
    const int Region = TileRegion.Size;              // 64, the only region these maps give storage to

    // THE REFERENCE, which is the shipped loop as it stood before the multi-goal search: every candidate pathed on
    // its own, the shortest walk winning, and a tie falling to the first candidate in TileReach.Set's order.
    static bool PerCandidateNearest(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
        int agentSize, int maxRadius, out TileCoord reachTile, out TilePath path)
    {
        reachTile = default;
        path = TilePath.Empty(from);
        if (from.Plane != plane) return false;
        if (footprint.IsEmpty) return false;

        int best = int.MaxValue;
        bool any = false;
        foreach (TileCoord candidate in TileReach.Set(map, footprint, plane, agentSize))
        {
            if (candidate.Equals(from))
            {
                reachTile = candidate;
                path = TilePath.Empty(from);
                return true;
            }
            TilePath p = TilePathfinder.FindPath(map, plane, from, candidate, agentSize, maxRadius);
            if (!p.Reached || p.Tiles.Count >= best) continue;
            best = p.Tiles.Count;
            reachTile = candidate;
            path = p;
            any = true;
        }
        return any;
    }

    static TileCollisionFlags Edge(TileDirection d) => d switch
    {
        TileDirection.W => TileCollisionFlags.WallW,
        TileDirection.E => TileCollisionFlags.WallE,
        TileDirection.S => TileCollisionFlags.WallS,
        _ => TileCollisionFlags.WallN,
    };

    static TileDirection Back(TileDirection d) => d switch
    {
        TileDirection.W => TileDirection.E,
        TileDirection.E => TileDirection.W,
        TileDirection.S => TileDirection.N,
        _ => TileDirection.S,
    };

    // Mirrored onto both tiles of the edge, which is what TileCollisionBaker does and what CanStand relies on.
    static void Wall(TileCollisionMap map, int x, int z, int plane, TileDirection d)
    {
        (int dx, int dz) = TileDirections.Delta(d);
        map.Or(x, z, plane, Edge(d));
        map.Or(x + dx, z + dz, plane, Edge(Back(d)));
    }

    static void Block(TileCollisionMap map, int x, int z, int plane) =>
        map.Or(x, z, plane, TileCollisionFlags.Blocked);

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>One randomised case: the map, the target, the walker and the two search knobs.</summary>
    readonly struct Case
    {
        internal readonly TileCollisionMap Map;
        internal readonly TileRect Target;
        internal readonly int Plane;
        internal readonly TileCoord From;
        internal readonly int AgentSize;
        internal readonly int MaxRadius;
        internal readonly string Label;

        internal Case(TileCollisionMap map, TileRect target, int plane, TileCoord from, int agentSize,
            int maxRadius, string label)
        {
            Map = map;
            Target = target;
            Plane = plane;
            From = from;
            AgentSize = agentSize;
            MaxRadius = maxRadius;
            Label = label;
        }
    }

    static Case Build(int seed)
    {
        var rng = new Random(seed);
        int plane = rng.Next(0, 2);
        int agentSize = rng.Next(1, 5);
        int maxRadius = rng.Next(5, 13);
        int tw = rng.Next(1, 5), th = rng.Next(1, 5);

        // Where the target sits: usually mid region, sometimes hard against an edge, where everything past the
        // region reads Blocked and the search window hangs off the loaded world.
        int edge = rng.Next(0, 8);
        int tx = edge == 0 ? rng.Next(0, 3) : edge == 1 ? Region - tw - rng.Next(0, 3) : rng.Next(10, 50);
        int tz = edge == 2 ? rng.Next(0, 3) : edge == 3 ? Region - th - rng.Next(0, 3) : rng.Next(10, 50);
        var target = new TileRect(tx, tz, tw, th);

        var map = new TileCollisionMap(2);
        map.EnsureRegion(new RegionCoord(0, 0));

        // Scatter: Blocked tiles and mirrored wall edges over the band the window can see.
        int lo = Clamp(tx - maxRadius - 2, 0, Region - 1), hi = Clamp(tx + maxRadius + 2, 0, Region - 1);
        int loZ = Clamp(tz - maxRadius - 2, 0, Region - 1), hiZ = Clamp(tz + maxRadius + 2, 0, Region - 1);
        int blocked = rng.Next(0, 70);
        for (int i = 0; i < blocked; i++) Block(map, rng.Next(lo, hi + 1), rng.Next(loZ, hiZ + 1), plane);
        int walls = rng.Next(0, 50);
        var cardinals = new[] { TileDirection.W, TileDirection.E, TileDirection.S, TileDirection.N };
        for (int i = 0; i < walls; i++)
            Wall(map, rng.Next(lo, hi + 1), rng.Next(loZ, hiZ + 1), plane, cardinals[rng.Next(0, 4)]);
        int corners = rng.Next(0, 12);
        var cornerBits = new[]
        {
            TileCollisionFlags.CornerNE, TileCollisionFlags.CornerNW,
            TileCollisionFlags.CornerSE, TileCollisionFlags.CornerSW,
        };
        for (int i = 0; i < corners; i++)
            map.Or(rng.Next(lo, hi + 1), rng.Next(loZ, hiZ + 1), plane, cornerBits[rng.Next(0, 4)]);

        // A doorway: a solid run with one gap, which is the shape that makes a detour the only way through and so
        // separates a real walk length from a straight-line guess.
        string label = "scatter";
        if (rng.Next(0, 3) == 0)
        {
            int wallX = Clamp(tx - rng.Next(3, 7), 1, Region - 2);
            int gap = Clamp(tz + rng.Next(-6, 7), loZ, hiZ);
            for (int z = loZ; z <= hiZ; z++) if (z != gap) Block(map, wallX, z, plane);
            label = "doorway";
        }

        // A target nobody can walk to: its reach ring stays free, so the candidate list is FULL, and a Blocked box
        // one tile further out seals every one of them off. That is the only shape that reaches the Reached check.
        if (rng.Next(0, 5) == 0)
        {
            TileRect box = target.Expand(2);
            for (int z = box.Z; z < box.Z1; z++)
                for (int x = box.X; x < box.X1; x++)
                    if (x == box.X || x == box.X1 - 1 || z == box.Z || z == box.Z1 - 1) Block(map, x, z, plane);
            for (int z = tz - 1; z <= tz + th; z++)
                for (int x = tx - 1; x <= tx + tw; x++)
                    if (x >= 0 && z >= 0 && x < Region && z < Region) map.Clear(new TileRect(x, z, 1, 1), plane);
            label = "sealed";
        }

        // Where the walker stands: adjacent, mid window, at the window edge, or past the admission bound.
        int reach = rng.Next(0, 10);
        int offX, offZ;
        if (reach < 3) { offX = rng.Next(-agentSize - 1, tw + 2); offZ = rng.Next(-agentSize - 1, th + 2); }
        else if (reach < 6) { offX = rng.Next(-maxRadius, maxRadius + 1); offZ = rng.Next(-maxRadius, maxRadius + 1); }
        else if (reach < 9) { offX = rng.Next(0, 2) == 0 ? -maxRadius : maxRadius; offZ = rng.Next(-maxRadius, maxRadius + 1); }
        else { offX = maxRadius + agentSize + rng.Next(0, 3); offZ = 0; }
        var from = new TileCoord(Clamp(tx + offX, 0, Region - 1), Clamp(tz + offZ, 0, Region - 1),
            rng.Next(0, 12) == 0 ? 1 - plane : plane);

        return new Case(map, target, plane, from, agentSize, maxRadius, $"{label}/{seed}");
    }

    [Fact]
    public void One_search_answers_what_a_search_per_candidate_answered()
    {
        int found = 0, refused = 0, standing = 0;
        for (int seed = 0; seed < 240; seed++)
        {
            Case c = Build(seed);
            bool expected = PerCandidateNearest(c.Map, c.Target, c.Plane, c.From, c.AgentSize, c.MaxRadius,
                out TileCoord expectedTile, out TilePath expectedPath);
            bool actual = TileReach.TryNearest(c.Map, c.Target, c.Plane, c.From, c.AgentSize, c.MaxRadius,
                out TileCoord actualTile, out TilePath actualPath);

            Assert.Equal(expected, actual);
            Assert.Equal(expectedTile, actualTile);
            Assert.Equal(expectedPath.Reached, actualPath.Reached);
            Assert.Equal(expectedPath.End, actualPath.End);
            Assert.Equal(expectedPath.Tiles.Count, actualPath.Tiles.Count);
            for (int i = 0; i < expectedPath.Tiles.Count; i++)
                Assert.True(expectedPath.Tiles[i].Equals(actualPath.Tiles[i]),
                    $"case {c.Label} step {i}: expected {expectedPath.Tiles[i]}, got {actualPath.Tiles[i]}");

            if (!actual) refused++;
            else if (actualPath.Tiles.Count == 0) standing++;
            else found++;
        }

        // The battery is worthless if every case answered the same way, so pin that all three outcomes occur.
        Assert.True(found > 60, $"only {found} cases walked to an anchor");
        Assert.True(refused > 20, $"only {refused} cases were refused");
        Assert.True(standing > 5, $"only {standing} cases started in reach");
    }

    // An anchor whose own NxN body cannot stand is listed by Set and has to be discarded. The claim the one-search
    // form rests on is that the search never DISCOVERS one, because CanStep at that size refuses to enter a cell
    // the body cannot stand on, so no pre-filter is needed. Here the only anchor a 2x2 could use on the west side
    // straddles a fence, and the walk has to come round to another side rather than stop on it.
    [Fact]
    public void An_anchor_the_agent_cannot_stand_on_is_never_the_answer()
    {
        var map = new TileCollisionMap(2);
        map.EnsureRegion(new RegionCoord(0, 0));
        var target = new TileRect(20, 20, 1, 1);

        // A north-south fence run through x 18/19, so every 2x2 anchored at x 18 straddles it.
        for (int z = 14; z <= 26; z++) Wall(map, 18, z, 0, TileDirection.E);

        IReadOnlyList<TileCoord> anchors = TileReach.Set(map, target, 0, 2);
        Assert.Contains(new TileCoord(18, 20, 0), anchors);                       // listed: reach asks no standing
        Assert.False(TileCollision.CanStand(map, 18, 20, 0, 2));                  // but nothing that size stands here

        Assert.True(TileReach.TryNearest(map, target, 0, new TileCoord(12, 20, 0), 2, 16,
            out TileCoord tile, out TilePath path));
        Assert.True(TileCollision.CanStand(map, tile.X, tile.Z, 0, 2));
        Assert.True(TileReach.Contains(map, target, 0, tile, 2));
        Assert.Equal(tile, path.End);
        Assert.NotEqual(new TileCoord(18, 20, 0), tile);
    }

    // The cost claim of #901, counted through the scratch rather than timed. A 4x4 target whose reach ring is free
    // but sealed off has a FULL sixteen candidate list and no reachable one, which is the worst case: the old loop
    // flooded its whole window once per candidate before answering false.
    [Fact]
    public void A_sealed_target_costs_one_window_of_expansion_not_one_per_candidate()
    {
        const int radius = 12;
        var map = new TileCollisionMap(2);
        map.EnsureRegion(new RegionCoord(0, 0));
        var target = new TileRect(20, 20, 4, 4);
        TileRect box = target.Expand(2);
        for (int z = box.Z; z < box.Z1; z++)
            for (int x = box.X; x < box.X1; x++)
                if (x == box.X || x == box.X1 - 1 || z == box.Z || z == box.Z1 - 1) Block(map, x, z, 0);

        var from = new TileCoord(10, 21, 0);
        Assert.Equal(16, TileReach.Set(map, target, 0, 1).Count);          // the whole ring is a candidate

        var scratch = new TilePathfinderScratch(radius);
        scratch.ClearCounters();
        Assert.False(TileReach.TryNearest(map, target, 0, from, 1, radius, out _, out _, scratch));

        int cells = (2 * radius + 1) * (2 * radius + 1);
        Assert.Equal(1, scratch.SearchesRun);
        Assert.True(scratch.CellsExpanded <= cells,
            $"the reach search dequeued {scratch.CellsExpanded} cells, past the {cells} one window holds");
    }
}
