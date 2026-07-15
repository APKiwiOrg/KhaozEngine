using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// A reusable <see cref="NetId"/> -&gt; <see cref="Entity"/> index over one server <see cref="World"/>, capturing each
/// entity's world <c>ForEach</c> position so a filtered snapshot (<see cref="SnapshotWriter.WriteFiltered(WorldSnapshotIndex, SnapshotScratch, World, ReplicationRegistry, IReadOnlySet{long}, ReplicationChannels, long?)"/>)
/// can resolve a (usually small) interest / border set in O(setCount) instead of a full-world scan, while still
/// emitting the entities in the exact world order the unindexed <see cref="SnapshotWriter"/> walk produced - so the
/// indexed wire is byte-identical to the full-scan wire.
/// </summary>
/// <remarks>
/// Built once per world per tick and shared across the several filtered snapshots that target that world in the tick
/// (a cell's up-to-eight ghost neighbours, the clients homed in a cell, the crossings leaving a cell), which is what
/// turns those O(worldPop)-per-call scans into one O(worldPop) rebuild plus O(setCount) resolves. The captured
/// <see cref="Entity"/> handles are valid only until the world next mutates (spawn / despawn), so <see cref="Rebuild"/>
/// once the world is settled for the tick and use it only within that tick. Single-threaded per instance: one owner
/// drives it on its own server-tick thread, so there is no locking.
/// </remarks>
public sealed class WorldSnapshotIndex
{
    private readonly Dictionary<long, (Entity entity, int order)> byNetId = new();
    private static readonly Comparison<(int order, long netId, Entity entity)> ByWorldOrder =
        static (a, b) => a.order.CompareTo(b.order);

    /// <summary>The number of <see cref="NetId"/> entities captured by the last <see cref="Rebuild"/>.</summary>
    public int Count => byNetId.Count;

    /// <summary>
    /// Rebuilds the index from <paramref name="world"/>'s <see cref="NetId"/> entities, recording each one's world
    /// <c>ForEach</c> position so a later filtered projection can reproduce that order. Clears the prior contents,
    /// reusing the backing storage.
    /// </summary>
    public void Rebuild(World world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        byNetId.Clear();
        int order = 0;
        world.ForEach<NetId>((Entity e, ref NetId id) => byNetId[id.Value] = (e, order++));
    }

    /// <summary>The indexed entity for <paramref name="netId"/>, or false when this world has no such net id.</summary>
    public bool TryGet(long netId, out Entity entity)
    {
        if (byNetId.TryGetValue(netId, out (Entity entity, int order) hit)) { entity = hit.entity; return true; }
        entity = default;
        return false;
    }

    /// <summary>
    /// Resolves the entities of <paramref name="netIds"/> present in this index into <paramref name="ordered"/>, sorted
    /// by world <c>ForEach</c> order so the snapshot emits them exactly as a full-world filtered walk would. Net ids
    /// not in the index (not in this world) are skipped. Reuses <paramref name="ordered"/> as scratch (cleared first),
    /// so a steady-state projection allocates nothing here.
    /// </summary>
    internal void Project(IReadOnlySet<long> netIds, List<(int order, long netId, Entity entity)> ordered)
    {
        ordered.Clear();
        foreach (long netId in netIds)
            if (byNetId.TryGetValue(netId, out (Entity entity, int order) hit))
                ordered.Add((hit.order, netId, hit.entity));
        ordered.Sort(ByWorldOrder); // orders are unique per entity: no ties, deterministic regardless of set order
    }
}
