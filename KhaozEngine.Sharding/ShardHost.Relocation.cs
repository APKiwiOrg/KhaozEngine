using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Sharding;

public sealed partial class ShardHost
{
    /// <summary>Immediately relocates an owned entity to a different cell after destination admission.
    /// Configure changes only a private, typed Migrate-channel copy. Refusal leaves the source untouched.
    /// This synchronous server-thread operation retains the admission read through both ownership changes.</summary>
    public bool TryRelocateOwned(long netId, CellCoord destination, Action<World, Entity> configure,
        out CellRestoreResult result)
    {
        ArgumentNullException.ThrowIfNull(configure);
        result = CellRestoreResult.Failed("The source entity is not owned.");
        if (!TryGetOwner(netId, out CellSim source, out Entity entity)) return false;
        if (source.Coord == destination) throw new ArgumentException("Relocation needs a different cell.", nameof(destination));
        CellSim target = GetOrCreateCell(destination);
        if (target.TryGetOwned(netId, out _))
        {
            result = CellRestoreResult.Failed("The destination already owns this network identity.");
            return false;
        }
        SnapshotStaging staged = SnapshotStaging.Capture(registry, source.World, entity, ReplicationChannels.Migrate);
        Entity candidate = staged.Entities[netId];
        configure(staged.World, candidate);
        if (!staged.World.TryGet(candidate, out NetId identity) || identity.Value != netId || positionAccessor is null ||
            !positionAccessor(staged.World, candidate, out float x, out float y) || CoordFor(x, y) != destination)
            throw new ArgumentException("The configured entity does not name the requested destination and identity.", nameof(configure));
        using CellSim.ImportPreparation prepared = target.PrepareImport(staged, CellImportPurpose.Relocate);
        result = prepared.CheckAdmission();
        if (!result.Ok) return false;
        Dictionary<long, TransientScope>? marks = source.World.TryGet(entity, out Transient mark)
            ? new() { [netId] = mark.Scope } : null;
        source.World.Set(entity, new Migrating { Destination = destination });
        source.UnregisterOwned(netId);
        try
        {
            result = prepared.Publish(marks);
            if (!result.Ok)
            {
                source.World.Remove<Migrating>(entity);
                source.RegisterOwned(netId, entity);
                return false;
            }
            target.FinishAdoption(prepared.AllNetIds);
            source.ReleaseMigrating(netId);
            return true;
        }
        catch
        {
            if (source.World.IsAlive(entity))
            {
                source.World.Remove<Migrating>(entity);
                source.RegisterOwned(netId, entity);
            }
            throw;
        }
    }
}
