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
/// would deny every reach tile of every real target. It never inspects the tile it leaves, which is the same
/// egress rule <see cref="TilePathfinder"/> leans on for a start that got built over. Outward therefore asks
/// exactly the three questions reach is about: no wall on the footprint's edge, the candidate is somewhere an
/// agent can stand, and no wall on the candidate's edge facing back. The baker mirrors every wall bit onto both
/// sides of the edge it blocks, so this stays symmetric with what a walk into the tile would find.</para>
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
    /// <see cref="TryNearest"/> breaks a tie by. Empty for a footprint walled in on all sides, and for an empty
    /// rect, both of which callers have to handle rather than assume a reach tile exists.</summary>
    /// <param name="map">The baked collision map to read walls and blocked tiles from.</param>
    /// <param name="footprint">The tiles the target covers, from <c>TileFootprint.Of</c> for a world object.</param>
    /// <param name="plane">The plane the target stands on. Reach never crosses planes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public static IReadOnlyList<TileCoord> Set(TileCollisionMap map, TileRect footprint, int plane)
    {
        ArgumentNullException.ThrowIfNull(map);
        var found = new List<TileCoord>();
        if (footprint.IsEmpty) return found;

        // A rect is contiguous, so an outside tile is cardinally adjacent to at most one of its tiles and the
        // seen set can never actually fire. It is kept because Set's contract is a set, and a caller reading
        // one entry twice would double-weight it: the cost is one allocation against the several searches
        // TryNearest is about to run.
        var seen = new HashSet<TileCoord>();
        for (int z = footprint.Z; z < footprint.Z1; z++)
        for (int x = footprint.X; x < footprint.X1; x++)
        {
            foreach (TileDirection outward in Cardinals)
            {
                (int dx, int dz) = TileDirections.Delta(outward);
                int nx = x + dx, nz = z + dz;
                if (footprint.Contains(nx, nz)) continue;             // inside the object is not a reach tile
                if (!TileCollision.CanStep(map, x, z, plane, outward)) continue;
                var candidate = new TileCoord(nx, nz, plane);
                if (seen.Add(candidate)) found.Add(candidate);
            }
        }
        return found;
    }

    /// <summary>True when <paramref name="from"/> is one of the footprint's reach tiles, which is the test for
    /// "close enough to act on it" and the reason an interaction that arrives already in range costs no walk at
    /// all. A tile on another plane is never in reach, however close it looks in x and z.</summary>
    /// <param name="map">The baked collision map to read walls and blocked tiles from.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on.</param>
    /// <param name="from">The tile the actor stands on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public static bool Contains(TileCollisionMap map, TileRect footprint, int plane, TileCoord from)
    {
        if (from.Plane != plane) return false;
        foreach (TileCoord c in Set(map, footprint, plane)) if (c.Equals(from)) return true;
        return false;
    }

    /// <summary>
    /// The reach tile to walk to, and the path there. Candidates are tried in <see cref="Set"/>'s scan order and
    /// scored by the LENGTH of the path <see cref="TilePathfinder.FindPath"/> actually reaches them by, never by
    /// a straight-line guess, so a tile one wall away from the target does not beat one a short walk away. Ties
    /// fall to scan order, which makes the choice total: both heads pick the same tile for the same map, and a
    /// prediction of an interaction walk reconciles instead of snapping.
    /// <para>Returns false when the footprint has no reach tile at all, and when none of them can be reached
    /// from <paramref name="from"/> inside <paramref name="maxRadius"/>. A caller treats that as "cannot get
    /// there", not as "walk as close as you can": <c>FindPath</c>'s nearest-reachable fallback is deliberately
    /// discarded here, because stopping short of a target you cannot act on is worse than not moving.</para>
    /// <para>One <c>FindPath</c> per candidate (at most eight) is deliberate. The pathfinder does not expose its
    /// distance field, and at one interaction per click that cost is invisible next to the tick it lands in. If
    /// it ever shows in a profile, the answer is a pooled multi-goal search on <c>TilePathfinder</c>, so both
    /// heads keep sharing one search, not a second BFS grown here that could disagree with the first.</para>
    /// </summary>
    /// <param name="map">The baked collision map to path over.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on, which overrides the plane on <paramref name="from"/>.</param>
    /// <param name="from">The tile the actor stands on.</param>
    /// <param name="agentSize">The actor's NxN footprint in tiles, passed straight to the pathfinder.</param>
    /// <param name="maxRadius">Half width of the search window, in tiles.</param>
    /// <param name="reachTile">The chosen reach tile, default when the call returns false.</param>
    /// <param name="path">The walk to <paramref name="reachTile"/>, empty when the actor already stands on it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="agentSize"/> or <paramref name="maxRadius"/>
    /// is outside what <see cref="TilePathfinder.FindPath"/> accepts.</exception>
    public static bool TryNearest(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
        int agentSize, int maxRadius, out TileCoord reachTile, out TilePath path)
    {
        ArgumentNullException.ThrowIfNull(map);
        reachTile = default;
        path = TilePath.Empty(from);
        int best = int.MaxValue;
        bool any = false;

        foreach (TileCoord candidate in Set(map, footprint, plane))
        {
            if (candidate.Equals(from))
            {
                reachTile = candidate;
                path = TilePath.Empty(from);
                return true;                                    // already standing on one: nothing beats zero steps
            }
            TilePath p = TilePathfinder.FindPath(map, plane, from, candidate, agentSize, maxRadius);
            if (!p.Reached || p.Tiles.Count >= best) continue;   // >= keeps the FIRST of a tie, so scan order decides
            best = p.Tiles.Count;
            reachTile = candidate;
            path = p;
            any = true;
        }
        return any;
    }

    /// <summary>The direction from a reach tile into the footprint tile beside it, so an actor that arrives
    /// faces what it came to interact with instead of keeping the facing its last step left it with. Scanning
    /// in <see cref="Cardinals"/> order makes the answer total for a corner tile touching the footprint on two
    /// sides, the same way the reach set is.
    /// <para>Falls back to <see cref="TileDirection.W"/> for a tile that touches no footprint tile at all, which
    /// is a caller passing something <see cref="TryNearest"/> never returns. A fallback rather than a throw
    /// because this is called as an arrival lands, and an odd facing is a far better outcome inside a server
    /// tick than an exception that takes the tick down.</para></summary>
    /// <param name="map">The baked collision map, read only for the null check. Geometry alone answers this
    /// today, and the map stays in the signature so a later rule that has to consult the walls (an object
    /// facing you may only stand in front of) does not change the shape of every call site.</param>
    /// <param name="footprint">The tiles the target covers.</param>
    /// <param name="plane">The plane the target stands on, carried for the same reason and for symmetry with the
    /// three members above, so a caller never has to remember which of the four takes one.</param>
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
}
