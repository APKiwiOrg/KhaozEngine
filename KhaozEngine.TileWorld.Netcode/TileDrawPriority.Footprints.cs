using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileDrawPriority
{
    // The preference order the widened rule resolves in, ASCENDING, so the last entry is the body to place first.
    // Two reused buffers rather than a sorted copy of the roster: they grow to the biggest crowd this instance has
    // seen and stop, which is the allocation story the roster itself has.
    long[] orderKeys = [];
    int[] orderIndex = [];

    /// <summary>
    /// Rebuilds from a caller's roster that carries each body's FOOTPRINT, the sized form of
    /// <see cref="Rebuild(long, TileCoord, TileCoord?, ReadOnlySpan{ValueTuple{long, TileCoord, float}}, float)"/>.
    /// A settled body's stack is every tile of its square, so a one-tile body standing anywhere inside a settled
    /// NxN body is in that body's stack and one of the two is hidden.
    /// <para>THE STACK COLLAPSES WHOLE, not tile by tile. Bodies are resolved best first and each takes EVERY tile
    /// it covers or none of them, so a body that loses any one of its tiles is hidden entirely rather than drawing
    /// the part of itself nobody else claimed. The order is the higher net id under
    /// <see cref="TileDrawPriorityPolicy.OneBodyPerTile"/> and <see cref="SettledComparison"/> ahead of the net id
    /// under <see cref="TileDrawPriorityPolicy.SettledStacksOnly"/>, which is the same answer those policies give
    /// two bodies on one tile. A comparison handed in for a roster with a body above one tile in it has to be a
    /// consistent ordering, because the whole roster is resolved in it rather than compared pairwise.</para>
    /// <para>A MOVING body keeps the answer it has today: judged on the tile it is committed to under the default
    /// policy, claiming nothing at all under the settled-stack policy. The widening is about a body at REST
    /// covering ground, and this overload invents no second rule for movers.</para>
    /// </summary>
    /// <param name="localNetId">The local player's net id, or <see cref="NoLocalPlayer"/> when there is no local
    /// player, in which case neither local tile is read.</param>
    /// <param name="localTile">The tile the local player is committed to, their PREDICTED tile on a live client,
    /// and the SOUTH-WEST corner of their footprint.</param>
    /// <param name="localFootprintSize">The edge of the local player's square footprint, in tiles. One for a
    /// one-tile body, which is every player this package serves. Below one reads as one, and above
    /// <see cref="TileMoveState.MaxFootprintSize"/> is capped there.</param>
    /// <param name="localLeaving">The tile the local player's step in flight is walking OUT of, or null when they
    /// are standing still. Its whole footprint is claimed alongside <paramref name="localTile"/>'s.</param>
    /// <param name="others">Every other actor, the tile it is COMMITTED to, how far through the step into that
    /// tile it is, and the edge of its square footprint. The progress is 0 as the step commits and 1 once the body
    /// is at rest, which is also what a body that is not stepping carries. A FINITE value outside 0 through 1 is
    /// CLAMPED into it, so a negative one reads as 0, the start of a step. Only a value that is not a number reads
    /// as 1, a body at rest. The size follows <see cref="TileMoveState.FootprintSize"/>, so anything below one is
    /// one tile.</param>
    /// <param name="dt">Seconds since the last rebuild, for the fades that have no step to ride. Ignored by the
    /// settled-stack policy, whose weights are all binary.</param>
    public void Rebuild(long localNetId, TileCoord localTile, int localFootprintSize, TileCoord? localLeaving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others, float dt)
    {
        if (Policy == TileDrawPriorityPolicy.SettledStacksOnly)
        {
            RebuildSettled(localNetId, localTile, localFootprintSize, localMoving: localLeaving.HasValue, others);
            return;
        }
        Rebuild(localNetId, localTile, localFootprintSize, localLeaving, others, dt, snap: false);
    }

    /// <summary>
    /// The sized roster with an explicit local-motion answer, the footprint form of
    /// <see cref="Rebuild(long, TileCoord, bool, ReadOnlySpan{ValueTuple{long, TileCoord, float}}, float)"/> and
    /// the custom-roster door for <see cref="TileDrawPriorityPolicy.SettledStacksOnly"/>. The stack rule is the
    /// one the other sized overload documents.
    /// <para>Both values of <paramref name="localMoving"/> preserve the local claim under
    /// <see cref="TileDrawPriorityPolicy.OneBodyPerTile"/>, because this overload has no departure tile. A caller
    /// that needs the exact leaving-tile claim uses the other sized overload.</para>
    /// </summary>
    /// <param name="localNetId">The local player's net id, or <see cref="NoLocalPlayer"/>.</param>
    /// <param name="localTile">The local player's committed tile, the south-west corner of their footprint.</param>
    /// <param name="localFootprintSize">The edge of the local player's square footprint, in tiles.</param>
    /// <param name="localMoving">True while the local body is moving on the presentation timeline.</param>
    /// <param name="others">Every other actor, its committed tile, its presented step progress and its footprint
    /// edge.</param>
    /// <param name="dt">Seconds since the last rebuild. Ignored by the settled-stack policy.</param>
    public void Rebuild(long localNetId, TileCoord localTile, int localFootprintSize, bool localMoving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others, float dt)
    {
        if (Policy == TileDrawPriorityPolicy.SettledStacksOnly)
        {
            RebuildSettled(localNetId, localTile, localFootprintSize, localMoving, others);
            return;
        }
        Rebuild(localNetId, localTile, localFootprintSize, localMoving ? localTile : null, others, dt, snap: false);
    }

    void Rebuild(long localNetId, TileCoord localTile, int localSize, TileCoord? localLeaving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others, float dt,
        bool snap)
    {
        Begin(winners, drawn);
        if (Widened(localSize, others))
        {
            ClaimLocal(localNetId, localTile, localSize, localLeaving);
            ClaimStack(localNetId, others, settledPolicy: false);
        }
        else
        {
            for (int i = 0; i < others.Length; i++) Offer(localNetId, others[i].NetId, others[i].Tile, winners);
            Settle(localNetId, localTile, localLeaving, winners, drawn);
        }
        Advance(localNetId, others, dt, snap);
    }

    void RebuildSettled(long localNetId, TileCoord localTile, int localSize, bool localMoving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others)
    {
        Begin(winners, drawn);
        if (Widened(localSize, others))
        {
            if (localMoving)
            {
                // Moving, so the local body owns no tile and is drawn anyway, which is the whole of this policy.
                if (localNetId != NoLocalPlayer) drawn.Add(localNetId);
            }
            else ClaimLocal(localNetId, localTile, localSize, localLeaving: null);
            ClaimStack(localNetId, others, settledPolicy: true);
        }
        else
        {
            for (int i = 0; i < others.Length; i++)
                OfferSettled(localNetId, others[i].NetId, others[i].Tile, StepAt(others[i].StepProgress) < 1f);
            FinishSettled(localNetId, localTile, localMoving);
        }
        Advance(localNetId, others, dt: 0f, snap: true);
    }

    // A GATE rather than a second rule: with every body one tile the two passes give the identical answer, and the
    // established one is the cheaper of them (no order, no sort). So a world with no large body in view pays
    // nothing for footprints, and the roster overloads that carry no size never reach this file at all.
    static bool Widened(int localSize,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others)
    {
        if (localSize > 1) return true;
        for (int i = 0; i < others.Length; i++)
            if (others[i].FootprintSize > 1) return true;
        return false;
    }

    // The local body takes every tile it covers, on both tiles of a step in flight, and takes them OUTRIGHT
    // because it is placed before anybody else. Same guarantee the one-tile rule gives: nothing is ever drawn over
    // the body its owner aims from.
    void ClaimLocal(long localNetId, TileCoord localTile, int localSize, TileCoord? localLeaving)
    {
        if (localNetId == NoLocalPlayer) return;
        int edge = Edge(localSize);
        Hold(localNetId, localTile, edge);
        if (localLeaving is TileCoord leaving) Hold(localNetId, leaving, edge);
        drawn.Add(localNetId);
    }

    // BEST FIRST, which is what collapses a stack to one body rather than to one body per tile. A body takes every
    // tile it covers or none of them, so the body that loses the contested tile is hidden whole, and the tiles it
    // would have held are still open to a body further down the order that does not overlap the winner.
    void ClaimStack(long localNetId,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others,
        bool settledPolicy)
    {
        int count = Order(localNetId, others, settledPolicy);
        for (int i = count - 1; i >= 0; i--)
        {
            (long netId, TileCoord tile, float progress, int size) = others[orderIndex[i]];
            bool moving = StepAt(progress) < 1f;
            if (moving && settledPolicy)
            {
                drawn.Add(netId);
                continue;
            }

            // A mover is judged on the tile it is COMMITTED to, exactly as a one-tile body is: the footprint
            // widens the ground a body AT REST covers, and a body mid-step is somewhere between two squares.
            if (TryHold(netId, tile, moving ? 1 : Edge(size))) drawn.Add(netId);
        }
    }

    // The roster's indices, ascending by preference, so a walk from the end is a walk best first. The local
    // player is left out: they are placed by ClaimLocal whatever a caller's roster says about them.
    int Order(long localNetId,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others,
        bool settledPolicy)
    {
        if (orderKeys.Length < others.Length)
        {
            Array.Resize(ref orderKeys, others.Length);
            Array.Resize(ref orderIndex, others.Length);
        }

        int count = 0;
        for (int i = 0; i < others.Length; i++)
        {
            if (others[i].NetId == localNetId) continue;
            orderKeys[count] = others[i].NetId;
            orderIndex[count] = i;
            count++;
        }

        Span<long> keys = orderKeys.AsSpan(0, count);
        Span<int> index = orderIndex.AsSpan(0, count);
        Comparison<long>? comparison = settledPolicy ? SettledComparison : null;
        // The net-id sort takes no comparer at all, which is the ordinary case and the allocation-free one. The
        // game comparison rides a STRUCT comparer for the same reason: a Comparison<T> overload would wrap it in a
        // fresh object every rebuild.
        if (comparison is null) keys.Sort(index);
        else keys.Sort(index, new Preference(comparison));
        return count;
    }

    // Two passes over the square on purpose: a body that cannot have all of its tiles must leave none of them
    // held, or a stack it lost would still hide the body that beat it on the tiles it grabbed first.
    bool TryHold(long netId, TileCoord anchor, int edge)
    {
        for (int dz = 0; dz < edge; dz++)
            for (int dx = 0; dx < edge; dx++)
                if (winners.TryGetValue(anchor.Offset(dx, dz), out long held) && held != netId) return false;

        Hold(netId, anchor, edge);
        return true;
    }

    void Hold(long netId, TileCoord anchor, int edge)
    {
        for (int dz = 0; dz < edge; dz++)
            for (int dx = 0; dx < edge; dx++)
                winners[anchor.Offset(dx, dz)] = netId;
    }

    // A size a caller never set, or set below one, is one tile, which is how TileMoveState reads its own zero
    // backing byte. The ceiling is that type's own, so no roster can turn the claim into a long loop.
    static int Edge(int size) => Math.Clamp(size, 1, TileMoveState.MaxFootprintSize);

    void Advance(long localNetId,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress, int FootprintSize)> others, float dt,
        bool snap)
    {
        stamp++;
        int touched = 0;
        for (int i = 0; i < others.Length; i++)
            touched += Touch(localNetId, others[i].NetId, others[i].StepProgress, dt, snap);
        Finish(localNetId, dt, touched);
    }

    // Ascending, so the LAST entry is the body to place first. The net-id half is the established tie break, and
    // it is the whole of the order under the default policy, which never consults a game comparison.
    readonly struct Preference(Comparison<long> comparison) : IComparer<long>
    {
        public int Compare(long x, long y)
        {
            int compared = comparison(x, y);
            return compared != 0 ? compared : x.CompareTo(y);
        }
    }
}
