using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// OSRS's reach rule as a pure function over the collision map. The reach set of a footprint is every tile
/// CARDINALLY adjacent to a footprint tile that the footprint tile could step out onto. One rule encodes the
/// whole behaviour: a wall between you and the booth denies reach, a diagonal never counts, a blocked
/// neighbour is somewhere nobody can stand, and a 2x2 object has up to eight reach tiles minus the denied ones.
/// <para>The step is tested OUTWARD, from the footprint tile to the candidate, and that direction is
/// load-bearing rather than cosmetic. <see cref="TileCollision.CanStep"/> refuses to ENTER a Blocked tile, and
/// anything worth reaching (a booth, a rock, a bench) is Blocked by definition, so testing the step inward
/// would deny every reach tile of every real target. Inward is wrong in the other direction too: it would ADMIT
/// a Blocked candidate, because a step never inspects the BLOCKED flag of the tile it LEAVES, so a candidate
/// nobody can stand on is never questioned when the step starts there. What the leaving tile is read for is its
/// wall bit on the edge being crossed, and treating egress from a blocked tile as legal is the same rule
/// <see cref="TilePathfinder"/> leans on for a start that got built over. Outward therefore asks exactly the
/// three questions reach is about: no wall on the footprint's edge, the candidate is somewhere an agent can
/// stand, and no wall on the candidate's edge facing back. The baker mirrors every wall bit onto both sides of
/// the edge it blocks, so the two wall questions are the same pair either way round and only the blocked one
/// moves, which is exactly the asymmetry reach needs.</para>
/// <para>Every tile in the one tile set is an ANCHOR tile for a ONE TILE actor, since the outward step is tested
/// at agent size one. An NxN actor anchored on its south-west tile acts from any tile it covers, so the agent size
/// overloads (<see cref="Set(TileCollisionMap, TileRect, int, int)"/>,
/// <see cref="Contains(TileCollisionMap, TileRect, int, TileCoord, int)"/> and
/// <see cref="FacingToward(TileCollisionMap, TileRect, int, TileCoord, int)"/>) answer for its anchor: in reach
/// when its footprint does not overlap the target and covers at least one tile of the one tile set. Walls are
/// never asked a second question, so a fence between one of its tiles and the target denies that tile only.</para>
/// <para>The scan order is fixed (footprint tiles by z ascending then x ascending, and the four cardinals in
/// W, E, S, N order), because a server and a client agree on which reach tile a click meant only if they
/// enumerate the candidates in the same order. Nothing here iterates a dictionary or a set, so the order is the
/// declaration order and not a hash layout that could differ between two runtimes.</para>
/// </summary>
public static class TileReach
{
    // The cardinals in the order the whole package tie-breaks on, which is TileDirections.All's order with the
    // diagonals dropped. Held as its own array rather than filtered out of All at each call, so the order a
    // reach tile is chosen by is stated in one readable place.
    static readonly TileDirection[] Cardinals =
        { TileDirection.W, TileDirection.E, TileDirection.S, TileDirection.N };

    /// <summary>Every tile the footprint can be reached from, in the fixed scan order, which is the order
    /// <c>TryNearest</c> breaks a tie by. Empty for a footprint walled in on all sides, and for an empty
    /// rect, both of which callers have to handle rather than assume a reach tile exists.
    /// <para>The tiles are anchor tiles for a ONE TILE actor. A caller with a larger agent asks
    /// <see cref="Set(TileCollisionMap, TileRect, int, int)"/>, which derives that agent's anchors from this set
    /// in this order.</para>
    /// <para>The footprint is assumed to lie in BAKED storage. A footprint in a region this map does not hold
    /// reads Blocked, which the outward test never consults, so reach is still reported from the loaded side.
    /// That can only arise on a shard whose neighbouring region is not baked into its map, never from a footprint
    /// taken off an object the map already holds.</para></summary>
    /// <param name="map">The baked collision map to read walls and blocked tiles from.</param>
    /// <param name="footprint">The tiles the target covers, from <c>TileFootprint.Of</c> for a world object.</param>
    /// <param name="plane">The plane the target stands on. Reach never crosses planes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public static IReadOnlyList<TileCoord> Set(TileCollisionMap map, TileRect footprint, int plane)
    {
        ArgumentNullException.ThrowIfNull(map);
        var found = new List<TileCoord>();
        if (footprint.IsEmpty) return found;

        // No dedupe, and none is needed. A rect is contiguous, so a tile outside it is cardinally adjacent to at
        // most one of its tiles, and each footprint tile offers each cardinal once: the same candidate cannot be
        // produced twice, which is what makes this list satisfy Set's contract of a set.
        for (int z = footprint.Z; z < footprint.Z1; z++)
        for (int x = footprint.X; x < footprint.X1; x++)
        {
            foreach (TileDirection outward in Cardinals)
            {
                (int dx, int dz) = TileDirections.Delta(outward);
                int nx = x + dx, nz = z + dz;
                if (footprint.Contains(nx, nz)) continue;             // inside the object is not a reach tile
                if (!TileCollision.CanStep(map, x, z, plane, outward)) continue;
                found.Add(new TileCoord(nx, nz, plane));
            }
        }
        return found;
    }

    /// <summary>Every ANCHOR tile an NxN agent can act on the footprint from: anchors whose own footprint does not
    /// overlap the target and covers at least one tile of the one tile
    /// <see cref="Set(TileCollisionMap, TileRect, int)"/>. Derived from that set in its own order, each reach tile
    /// offering the anchors whose footprint holds it (offset dz ascending, then dx ascending, so the anchor with
    /// the reach tile on its south-west corner comes first), first occurrence kept. For a size of 1 the offsets
    /// are only (0, 0), nothing overlaps and nothing repeats, so the list is that set element for element, which
    /// keeps every one tile tie break.
    /// <para>On open ground an MxM target has 4(M + N - 1) anchors, and walls only remove them. Empty whenever
    /// the one tile set is.</para>
    /// <para>Reach is all this asks, not standing. The list can hold an anchor whose own footprint covers a Blocked
    /// tile or straddles a wall, which no agent of that size could stand on. <c>TryNearest</c> filters those out by
    /// pathing, since no walk reaches them.</para></summary>
    /// <param name="map">The baked collision map to read walls and blocked tiles from.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on. Reach never crosses planes.</param>
    /// <param name="agentSize">The agent's NxN footprint edge in tiles, anchored on its south-west tile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="agentSize"/> is below 1.</exception>
    public static IReadOnlyList<TileCoord> Set(TileCollisionMap map, TileRect footprint, int plane, int agentSize)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        IReadOnlyList<TileCoord> reach = Set(map, footprint, plane);
        if (agentSize == 1) return reach;
        var anchors = new List<TileCoord>();
        // The LIST carries the order and the set answers only "seen this one", never enumerated, so no hash layout
        // can reach the output: the anchors come out in exactly the order the loops emit them, first occurrence
        // kept, which is the order the tie rule reads and both heads have to agree on. A set rather than
        // List.Contains because the candidate count is quadratic in the sizes and the scan was quadratic in that.
        var seen = new HashSet<TileCoord>();
        foreach (TileCoord p in reach)
            for (int dz = 0; dz < agentSize; dz++)
                for (int dx = 0; dx < agentSize; dx++)
                {
                    var a = new TileCoord(p.X - dx, p.Z - dz, plane);
                    if (Overlaps(a, agentSize, footprint) || !seen.Add(a)) continue;
                    anchors.Add(a);
                }
        return anchors;
    }

    /// <summary>True when <paramref name="from"/> is one of the footprint's reach tiles, which is the test for
    /// "close enough to act on it" and the reason an interaction that arrives already in range costs no walk at
    /// all. A tile on another plane is never in reach, however close it looks in x and z.
    /// <para>The question is whether a ONE TILE actor anchored on <paramref name="from"/> is in reach, matching
    /// <see cref="Set(TileCollisionMap, TileRect, int)"/>. A larger agent asks
    /// <see cref="Contains(TileCollisionMap, TileRect, int, TileCoord, int)"/>, which is in reach from any tile it
    /// covers.</para></summary>
    /// <param name="map">The baked collision map to read walls and blocked tiles from.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on.</param>
    /// <param name="from">The tile the actor stands on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public static bool Contains(TileCollisionMap map, TileRect footprint, int plane, TileCoord from)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (from.Plane != plane) return false;
        foreach (TileCoord c in Set(map, footprint, plane)) if (c.Equals(from)) return true;
        return false;
    }

    /// <summary>True when an NxN agent anchored on <paramref name="from"/> is close enough to act on the footprint:
    /// its own footprint does not overlap the target, and it covers at least one tile of
    /// <see cref="Set(TileCollisionMap, TileRect, int)"/>. So a wall between one of its tiles and the target denies
    /// that tile only, and another tile of the agent that is not walled off still reaches. Any overlap is never in
    /// reach, since a body inside its target cannot act on it, and <c>TryNearest</c> walks it out instead. A tile on
    /// another plane is never in reach. For a size of 1 this is
    /// <see cref="Contains(TileCollisionMap, TileRect, int, TileCoord)"/>, answer for answer.</summary>
    /// <param name="map">The baked collision map to read walls and blocked tiles from.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on.</param>
    /// <param name="from">The agent's anchor, its south-west tile.</param>
    /// <param name="agentSize">The agent's NxN footprint edge in tiles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="agentSize"/> is below 1.</exception>
    public static bool Contains(TileCollisionMap map, TileRect footprint, int plane, TileCoord from, int agentSize)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        if (agentSize == 1) return Contains(map, footprint, plane, from);
        if (from.Plane != plane || Overlaps(from, agentSize, footprint)) return false;
        foreach (TileCoord c in Set(map, footprint, plane))
            if ((long)c.X >= from.X && (long)c.X < (long)from.X + agentSize
                && (long)c.Z >= from.Z && (long)c.Z < (long)from.Z + agentSize) return true;
        return false;
    }

    /// <summary>
    /// The anchor to walk to, and the path there. Candidates are the anchors of
    /// <see cref="Set(TileCollisionMap, TileRect, int, int)"/> for <paramref name="agentSize"/>, scored by the
    /// LENGTH of the path the search actually reaches them by, never by
    /// a straight-line guess, so a tile one wall away from the target does not beat one a short walk away. Ties
    /// fall to scan order, which makes the choice total: both heads pick the same tile for the same map, and a
    /// prediction of an interaction walk reconciles instead of snapping.
    /// <para>Returns false when the footprint has no reach tile at all, when <paramref name="from"/> stands on
    /// another plane, and when none of the candidate anchors can be reached from <paramref name="from"/> inside
    /// <paramref name="maxRadius"/>. That last refusal is ADMITTED CHEAPLY for a footprint the search window
    /// cannot hold: a footprint further than <paramref name="maxRadius"/> + <paramref name="agentSize"/> away has no
    /// candidate inside the window, so it is answered without a search and without the scratch one allocates. The
    /// answer is the same one, and the reason it is worth stating is that a caller naming a far target is how an
    /// unbounded search
    /// gets bought (see the comment in the body). The <see cref="ArgumentOutOfRangeException"/> below comes out of
    /// the top of the body rather than out of the pathfinder, so a bad argument is refused the same way whether
    /// the target is open, walled in, out of range, or on another plane. It used to surface only once a candidate
    /// was actually pathed, which made one caller bug read as "cannot reach" against some targets and throw
    /// against others. Reach never crosses planes, which is the refusal
    /// <see cref="Contains(TileCollisionMap, TileRect, int, TileCoord, int)"/>
    /// already makes and the one the two members have to agree on: an actor a plane above a booth is not standing
    /// on a reach tile, so it must not be handed a zero step walk to one either. A caller treats a false as
    /// "cannot get there", not as "walk as close as you can": <c>FindPath</c>'s nearest-reachable fallback is
    /// deliberately discarded here, because stopping short of a target you cannot act on is worse than not
    /// moving.</para>
    /// <para>ONE search, whatever the candidate count: <see cref="TilePathfinder.FindPathToAny"/> takes the whole
    /// candidate list and floods the window once, where this used to run a <c>FindPath</c> per candidate and keep
    /// the shortest. It is the SAME answer, because the pathfinder is a plain breadth-first search whose discovery
    /// order does not depend on which goal ends it, so each candidate's walk is the one its own search would have
    /// built, and finishing the level the first candidate is found on keeps the scan-order tie rule total. What
    /// changes is the cost: a 4x4 target against a one tile actor is 16 candidates, and a target nobody can reach
    /// used to flood the whole window once for each of them (at the player simulator's radius of 64, 16641 cells a
    /// time). That multi-goal search lives on <c>TilePathfinder</c> beside the single-goal one and shares its
    /// expansion, so both heads keep pathing through one implementation rather than a second BFS grown here.</para>
    /// </summary>
    /// <param name="map">The baked collision map to path over.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on. A <paramref name="from"/> on any other plane is
    /// refused rather than coerced onto this one, the way the rest of the package refuses a cross-plane goal.</param>
    /// <param name="from">The tile the actor stands on.</param>
    /// <param name="agentSize">The actor's NxN footprint in tiles. It shapes the walk, passed straight to the
    /// pathfinder, and the candidates, which are the anchors of
    /// <see cref="Set(TileCollisionMap, TileRect, int, int)"/> for this size, so the tile it stops on is one
    /// <see cref="Contains(TileCollisionMap, TileRect, int, TileCoord, int)"/> answers true for.</param>
    /// <param name="maxRadius">Half width of the search window, in tiles.</param>
    /// <param name="reachTile">The chosen ANCHOR tile for <paramref name="agentSize"/>, the south-west tile the agent
    /// stops with. At size 1 it is a reach tile of the one tile set, and above that it need not be one. Default when
    /// the call returns false.</param>
    /// <param name="path">The walk to <paramref name="reachTile"/>, empty when the agent's anchor is already on
    /// it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="agentSize"/> is below 1, or
    /// <paramref name="maxRadius"/> is outside 1..<see cref="TilePathfinder.MaxSearchRadius"/>, which is what
    /// <see cref="TilePathfinder.FindPath"/> accepts. Checked here rather than left to the first search, so the
    /// refusal does not depend on whether this particular target happens to reach one.</exception>
    public static bool TryNearest(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
        int agentSize, int maxRadius, out TileCoord reachTile, out TilePath path)
        => TryNearest(map, footprint, plane, from, agentSize, maxRadius, out reachTile, out path, null);

    /// <summary>Finds the nearest reachable anchor using caller-owned pathfinder working memory.</summary>
    /// <param name="map">The baked collision map.</param>
    /// <param name="footprint">The target footprint.</param>
    /// <param name="plane">The target plane.</param>
    /// <param name="from">The actor's tile.</param>
    /// <param name="agentSize">The moving actor's footprint edge. It shapes the walk and the candidates, which are
    /// the anchors of <see cref="Set(TileCollisionMap, TileRect, int, int)"/> for this size.</param>
    /// <param name="maxRadius">The pathfinder search radius.</param>
    /// <param name="reachTile">The selected ANCHOR tile for <paramref name="agentSize"/>, see the overload
    /// above.</param>
    /// <param name="path">The path to the selected anchor.</param>
    /// <param name="scratch">Reusable pathfinder working memory, or null to allocate per search.</param>
    /// <returns>True when a candidate anchor is reachable.</returns>
    public static bool TryNearest(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
        int agentSize, int maxRadius, out TileCoord reachTile, out TilePath path, TilePathfinderScratch? scratch)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRadius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRadius, TilePathfinder.MaxSearchRadius);
        reachTile = default;
        path = TilePath.Empty(from);
        if (from.Plane != plane) return false;               // reach never crosses planes, same as Contains
        if (footprint.IsEmpty) return false;                 // no candidates, and no distance to measure either

        // ADMISSION, and the counterpart to the server's own MaxGoalRadius refusal for a walk goal.
        // TilePathfinder.FindPath searches a (2r+1)^2 window centred on `from`, so a candidate outside that box is
        // never visited and the search always fails. Every candidate is an anchor whose NxN body touches a side of
        // the footprint, and the west and south anchors sit agentSize tiles out from it, so the nearest candidate
        // is at most agentSize tiles closer than the footprint itself: a footprint whose nearest tile is further
        // than maxRadius + agentSize has no candidate inside the window AT ALL, and the whole call is decided here.
        // Exactly the answer the search below would have reached, for none of the (2r+1)^2 scratch entries it
        // allocates.
        //
        // That cost is what a client naming a target it has never seen was buying. Net ids are handed out from a
        // counter, so a hostile Attack or Interact guesses a small integer rather than needing to have seen
        // anything, and at the player simulator's radius of 64 each guess was a flood of about 83 KB.
        // Refusing here rather than at the door is what gives both seams one rule and both heads one definition of
        // it, since the client predicts through this same member.
        if (FootprintDistance(footprint, from) > (long)maxRadius + agentSize) return false;

        IReadOnlyList<TileCoord> candidates = Set(map, footprint, plane, agentSize);
        if (candidates.Count == 0) return false;                 // walled in on every side: nothing to path to

        // ONE search over every candidate at once. FindPathToAny takes the list in THIS order and answers with the
        // shortest walk, ties falling to the lowest index, which is the scan-order tie rule stated as an argument
        // rather than reimplemented here. A candidate outside the window is never discovered, which is the
        // per-candidate window prune the loop used to do by hand, and an anchor no agent of this size could stand
        // on is never discovered either, because CanStep refuses to enter a cell the whole body does not fit.
        TilePath p = TilePathfinder.FindPathToAny(map, plane, from, candidates, agentSize, maxRadius, scratch,
            out int index);
        if (!p.Reached) return false;                            // the nearest-reachable fallback is not offered
        reachTile = candidates[index];
        path = p;
        return true;
    }

    /// <summary>The direction from a reach tile into the footprint tile beside it, so an actor that arrives
    /// faces what it came to interact with instead of keeping the facing its last step left it with. A rect is
    /// contiguous, so a tile outside it is cardinally adjacent to at most one of its tiles and there is never a
    /// second side to choose between. The fixed <see cref="Cardinals"/> order is here so the fallback below is
    /// reached deterministically rather than to settle a tie that cannot happen.
    /// <para>Falls back to <see cref="TileDirection.W"/> for a tile that touches no footprint tile at all, which
    /// is a caller passing something <c>TryNearest</c> never returns. A fallback rather than a throw
    /// because this is called as an arrival lands, and an odd facing is a far better outcome inside a server
    /// tick than an exception that takes the tick down.</para></summary>
    /// <param name="map">The baked collision map, read only for the null check. Geometry alone answers this
    /// today, and the map stays in the signature so a later rule that has to consult the walls (an object
    /// facing you may only stand in front of) does not change the shape of every call site.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on, carried for the same reason and for symmetry with every
    /// other member here, so a caller never has to remember which of them takes one.</param>
    /// <param name="from">The reach tile the actor stands on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public static TileDirection FacingToward(TileCollisionMap map, TileRect footprint, int plane, TileCoord from)
    {
        ArgumentNullException.ThrowIfNull(map);
        foreach (TileDirection d in Cardinals)
        {
            (int dx, int dz) = TileDirections.Delta(d);
            if (footprint.Contains(from.X + dx, from.Z + dz)) return d;
        }
        return TileDirection.W;
    }

    /// <summary>The side on which an NxN agent anchored on <paramref name="from"/> touches the footprint, as the
    /// direction it faces to act. Two rects that do not overlap and are cardinally adjacent touch on exactly one
    /// side (touching in x needs overlap in z, which rules out touching in z), so the answer is unique, and the
    /// sides are asked in the W, E, S, N order only so the fallback is reached deterministically. For a size of 1
    /// this is <see cref="FacingToward(TileCollisionMap, TileRect, int, TileCoord)"/>, answer for answer.
    /// <para>Above size 1, falls back to <see cref="TileDirection.W"/> for an agent that touches no side, which
    /// covers an overlapping agent and an empty footprint, for the reason the one tile overload gives. A size of 1
    /// delegates to that overload's four-neighbour scan, so an overlapping one tile agent can answer any side a
    /// footprint tile lies on, E included.</para></summary>
    /// <param name="map">The baked collision map, read only for the null check, as on the one tile overload.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on, carried for symmetry with the one tile overload.</param>
    /// <param name="from">The agent's anchor, its south-west tile.</param>
    /// <param name="agentSize">The agent's NxN footprint edge in tiles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="agentSize"/> is below 1.</exception>
    public static TileDirection FacingToward(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
        int agentSize)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        if (agentSize == 1) return FacingToward(map, footprint, plane, from);
        if (footprint.IsEmpty) return TileDirection.W;
        long x0 = from.X, x1 = x0 + agentSize, z0 = from.Z, z1 = z0 + agentSize;
        long fx0 = footprint.X, fx1 = fx0 + footprint.Width, fz0 = footprint.Z, fz1 = fz0 + footprint.Height;
        bool zOverlap = z0 < fz1 && z1 > fz0;
        bool xOverlap = x0 < fx1 && x1 > fx0;
        // W, E, S, N, the order the one tile scan asks in.
        if (zOverlap && x0 == fx1) return TileDirection.W;
        if (zOverlap && x1 == fx0) return TileDirection.E;
        if (xOverlap && z0 == fz1) return TileDirection.S;
        if (xOverlap && z1 == fz0) return TileDirection.N;
        return TileDirection.W;
    }

    // The Chebyshev gap between `from` and the footprint, 0 when `from` stands on it, and the whole of the
    // admission rule above. Its own member because the arithmetic is the only observable part of that rule at the
    // coordinates it is written to survive: a rect whose far edge passes int.MaxValue cannot be enumerated by Set
    // at all, so the call answers false either way and no end-to-end test can tell a wrapped edge from a sound one.
    //
    // In LONG throughout, for two independent reasons. The two coordinates can be far apart, so a footprint near
    // int.MinValue against a `from` at a positive tile overflows the SUBTRACTION in int, and the wrong sign would
    // ADMIT the call rather than refuse it. And the far edge is X + Width - 1 computed here rather than
    // TileRect.X1 - 1 read off the rect, because X1 is an int sum that has already wrapped by the time it is cast:
    // that read a footprint one tile away as about 2^32 away and refused it (#898), the same wrap the overlap
    // helper below spells out.
    internal static long FootprintDistance(TileRect footprint, TileCoord from)
    {
        long dx = Math.Max(Math.Max((long)footprint.X - from.X, (long)from.X - ((long)footprint.X + footprint.Width - 1)), 0L);
        long dz = Math.Max(Math.Max((long)footprint.Z - from.Z, (long)from.Z - ((long)footprint.Z + footprint.Height - 1)), 0L);
        return Math.Max(dx, dz);
    }

    // In long, both the agent's far edges and the rect's, because TileRect.X1 and Z1 are int sums that wrap near
    // int.MaxValue and a wrapped edge would read as no overlap.
    static bool Overlaps(TileCoord anchor, int size, TileRect r) =>
        (long)anchor.X < (long)r.X + r.Width && (long)anchor.X + size > r.X
        && (long)anchor.Z < (long)r.Z + r.Height && (long)anchor.Z + size > r.Z;
}
