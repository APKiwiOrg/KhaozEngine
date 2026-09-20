using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The idle retained-entity half of <see cref="TileWorldServer"/>.</summary>
public sealed partial class TileWorldServer
{
    readonly Dictionary<long, CellCoord> staticEntityCells = new();
    readonly Dictionary<CellCoord, HashSet<long>> staticEntitiesByCell = new();

    /// <summary>Builds an idle retained entity at <paramref name="at"/> and returns its net id.</summary>
    /// <remarks>The entity replicates its <see cref="TileMoveState"/> and can be reached through
    /// <see cref="TileCommand.InteractEntity(long, TileMoveMode)"/>. It has no actor, movement, health or combat
    /// components. A game can attach its own replicated identity immediately through <see cref="Host"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="at"/> is outside the configured world or the default
    /// traversal map does not admit the footprint.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="spec"/> has a footprint outside 1 through
    /// <see cref="TileMoveState.MaxFootprintSize"/>, or the configured interest margin cannot serve it.</exception>
    public long SpawnStaticEntity(TileCoord at, in TileStaticEntitySpawn spec)
    {
        if (spec.FootprintSize < 1 || spec.FootprintSize > TileMoveState.MaxFootprintSize)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.FootprintSize,
                $"A static entity's FootprintSize must be 1 through {TileMoveState.MaxFootprintSize}.");
        ValidateFootprintFitsInterest(spec.FootprintSize, nameof(spec));

        TileMoveState state = TileMoveState.At(at, spec.Facing);
        state.FootprintSize = spec.FootprintSize;
        _ = ValidatePlayerState(state);
        ValidateActorTraversalPlacement(TileActorTraversalProfile.Default, at, spec.FootprintSize, nameof(spec));

        NoteFootprintSize(spec.FootprintSize);
        long netId = allocator.Next().Value;
        Entity entity = host.SpawnOwned(at.X, at.Z, netId, out CellSim cell);
        cell.World.Set(entity, state);
        cell.World.Set(entity, new Transient { Scope = TransientScope.Always });

        staticEntityCells.Add(netId, cell.Coord);
        if (!staticEntitiesByCell.TryGetValue(cell.Coord, out HashSet<long>? inCell))
        {
            inCell = new HashSet<long>();
            staticEntitiesByCell.Add(cell.Coord, inCell);
        }
        inCell.Add(netId);
        return netId;
    }

    /// <summary>Removes a retained static entity.</summary>
    /// <param name="netId">The retained entity's net id.</param>
    /// <returns>False when the id is not a retained static entity on this server.</returns>
    public bool DespawnStaticEntity(long netId)
    {
        if (!staticEntityCells.Remove(netId, out CellCoord cellCoord)) return false;
        RemoveStaticEntityFromCell(cellCoord, netId);
        if (host.TryGetOwner(netId, out CellSim cell, out Entity entity) && cell.World.IsAlive(entity))
        {
            cell.UnregisterOwned(netId);
            cell.World.Despawn(entity);
        }
        return true;
    }

    void RemoveStaticEntityFromCell(CellCoord cellCoord, long netId)
    {
        if (!staticEntitiesByCell.TryGetValue(cellCoord, out HashSet<long>? inCell)) return;
        inCell.Remove(netId);
        if (inCell.Count == 0) staticEntitiesByCell.Remove(cellCoord);
    }

    void ForgetStaticEntitiesIn(CellCoord cellCoord)
    {
        if (!staticEntitiesByCell.Remove(cellCoord, out HashSet<long>? inCell)) return;
        foreach (long netId in inCell) staticEntityCells.Remove(netId);
    }
}
