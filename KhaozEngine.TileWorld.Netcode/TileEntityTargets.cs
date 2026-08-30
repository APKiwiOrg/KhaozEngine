using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The ENTITY-backed <see cref="ITileTargets"/>: a target id is a NET ID, and the footprint is the 1x1 rect on the
/// tile that entity is committed to. The server half of the combat target seam, and the sibling of
/// <see cref="TileDocumentTargets"/>, which answers the OBJECT space instead. Two resolvers rather than one because
/// the two id spaces overlap exactly, which is what <see cref="TileCommandKind.Attack"/> exists to discriminate.
/// <para>SNAPSHOTTED ONCE PER TICK rather than read through, and that is the one place this type deliberately
/// differs from <see cref="TileDocumentTargets"/>. An authored object does not move on a tick, so reading it
/// through is free and correct. An entity moves on every tick, and the FOLLOW that consults this runs inside
/// <see cref="TileMovementSystem"/>'s pass over a cell's archetypes, so a read-through resolver would answer with
/// the target's tile from before or after its own step depending on the ECS iteration order. That is not a cosmetic
/// difference: an attacker that sees its target's POST-step tile re-paths on the same tick the target commits, which
/// collapses the one-tick miss window a fleeing target depends on and changes how a chase resolves. The snapshot is
/// what makes the movement pass order-independent in fact rather than in claim, and it is also what makes the server
/// match a client, whose own read is snapshot-stable within a tick by construction.</para>
/// <para>Ghosts and migrating mirrors are excluded BY AN EXPLICIT CHECK, which is worth knowing rather than
/// assuming: <see cref="Refresh"/> walks every entity in each cell, and the capture skips anything carrying
/// <c>Ghost</c> or <c>Migrating</c> before it reads a tile. So a border mirror of an entity another cell simulates
/// is never in the map and cannot answer with a tick-stale tile under the same net id, and that property lives in
/// one line of code rather than in the shape of the walk.</para>
/// <para>WHAT AN EXCLUDED ENTITY GETS IS "gone", not "held", and that is worth knowing before a networked link
/// lands. An id this map does not hold does not resolve, and the follow reads a target that does not resolve as
/// dead, despawned or out of view, so it CLEARS the lock. A ghost is only ever a mirror of something its owning
/// cell is already following, so nothing is lost there. A MIGRATING entity is a different case: with the in-process
/// link the whole migrate, ack and release handshake completes inside one <c>ShardHost.ProcessHandoffs</c> call, so
/// nothing is ever <see cref="Migrating"/> when this runs and the window is zero. A networked <c>ICellLink</c>
/// spans calls by design, and then a target mid-handoff is unresolvable for a tick or more and a fight would break
/// silently whenever the target crossed a region boundary.</para>
/// <para>The same walk also snapshots the REVERSE of every combat lock: <see cref="TargetedBy"/> answers who is
/// locked onto an entity, out of the same tick-start view the tiles come from, so an actor deciding whether to
/// stand its ground reads the same instant every other consumer of this snapshot does. It is on this concrete type
/// rather than on <see cref="ITileTargets"/>, because the interface is the target-tile seam both heads share and
/// only the server's decision pass has any business asking who the attackers are.</para>
/// </summary>
public sealed class TileEntityTargets : ITileTargets
{
    readonly Dictionary<long, TileCoord> tiles = new();
    readonly Dictionary<long, long> attackerByTarget = new();
    RefAction<NetId>? capture;
    World? captureWorld;

    /// <summary>Builds the resolver. Nothing is read until <see cref="Refresh"/> runs, so a server can hand this to
    /// its simulators before it has any entities.</summary>
    public TileEntityTargets()
    {
    }

    /// <summary>
    /// Snapshots every owned entity's COMMITTED tile, once, at the top of a server tick. Everything that asks this
    /// resolver for the rest of that tick gets the same answer, which is the property the follow's determinism rests
    /// on. Allocation-free after the first call: the map is cleared and refilled and the callback is cached.
    /// <para>ONE THREAD AT A TIME, per instance. The cached callback reads the cell being walked out of a field, so
    /// two threads refreshing one instance would read each other's world. That is the price of not allocating a
    /// closure per cell per tick, and it is the right trade for something a server tick calls once: a caller wanting
    /// concurrent refreshes wants an instance each.</para>
    /// </summary>
    /// <param name="cells">The live cells to walk. The server passes its own list rather than
    /// <c>ShardHost.Cells</c>, whose enumerator boxes once per call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is null.</exception>
    public void Refresh(IReadOnlyList<CellSim> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        capture ??= Capture;
        tiles.Clear();
        attackerByTarget.Clear();
        for (int i = 0; i < cells.Count; i++)
        {
            captureWorld = cells[i].World;
            captureWorld.ForEach(capture);
        }
        captureWorld = null;
    }

    /// <inheritdoc/>
    /// <remarks>A 1x1 rect on the committed tile, because both parties are one tile this round.
    /// <see cref="TileReach"/> states three times that its set is anchor tiles for a ONE TILE actor, so a larger
    /// footprint is a rule this package does not have yet rather than a bigger rect.</remarks>
    public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
    {
        footprint = default;
        plane = 0;
        if (!tiles.TryGetValue(target, out TileCoord tile)) return false;
        footprint = new TileRect(tile.X, tile.Z, 1, 1);
        plane = tile.Plane;
        return true;
    }

    /// <summary>Who holds <paramref name="netId"/> as a combat target in this tick's snapshot, 0 when nobody
    /// does. Where several entities hold it, the LOWEST net id answers, which is deterministic whatever order
    /// the cells were walked in, and net ids are assigned in spawn order so the answer approximates the
    /// earliest arrival. This is the read the default behaviour's stand-your-ground rule rides: an actor that
    /// knows something has locked onto it can stop walking away before the first blow lands.</summary>
    /// <param name="netId">The entity being asked about as a TARGET.</param>
    public long TargetedBy(long netId) => attackerByTarget.TryGetValue(netId, out long attacker) ? attacker : 0L;

    // The RAW component, deliberately, and this is the one read in the package that may skip
    // TileProtocol.AssembleMoveState: the rule that helper exists for is about Route, and nothing here reads a route.
    // Tile is correct on the raw component on every tick including the one after a handoff.
    void Capture(Entity e, ref NetId id)
    {
        World world = captureWorld!;
        if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return;
        if (!world.TryGet(e, out TileMoveState state)) return;
        tiles[id.Value] = state.Tile;
        // The reverse of the lock, built in the same walk the tiles are. The min rule is what keeps the
        // answer independent of cell and ECS iteration order, which nothing here may depend on.
        if (state.CombatTarget == 0L) return;
        if (!attackerByTarget.TryGetValue(state.CombatTarget, out long held) || id.Value < held)
            attackerByTarget[state.CombatTarget] = id.Value;
    }
}
