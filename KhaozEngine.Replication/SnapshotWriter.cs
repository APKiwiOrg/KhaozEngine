using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Serializes a full-state snapshot of a server <see cref="World"/>: each entity carrying a <see cref="NetId"/>
/// and its registered components. Format: <c>[entityCount][per entity: [netId][(typeId,[len],data)...][0]]</c>,
/// where the 7-bit-encoded <c>len</c> is present only for consumer extension components (see
/// <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) so an older client can skip an id it never registered.
/// The snapshot is opaque <c>byte[]</c> the game ships over its session transport. Use
/// <see cref="WriteFiltered"/> with an interest set for per-client area-of-interest replication: the existing
/// <see cref="ClientReplicationView.Apply"/> then spawns entities that entered the set and despawns those that left.
/// </summary>
public static class SnapshotWriter
{
    /// <summary>Writes a full-state snapshot of every <see cref="NetId"/> entity in <paramref name="world"/>.</summary>
    public static byte[] Write(World world, ReplicationRegistry registry)
    {
        var entities = new List<(int netId, Entity entity)>();
        world.ForEach<NetId>((Entity e, ref NetId id) => entities.Add((id.Value, e)));
        return Encode(world, registry, entities);
    }

    /// <summary>
    /// Writes a snapshot of only the entities whose <see cref="NetId"/> is in <paramref name="netIds"/> (the
    /// client's area of interest). Applied with <see cref="ClientReplicationView.Apply"/>, entities that left
    /// the set are despawned and ones that entered are spawned — full-state-per-interest AoI replication.
    /// </summary>
    public static byte[] WriteFiltered(World world, ReplicationRegistry registry, IReadOnlySet<int> netIds)
    {
        var entities = new List<(int netId, Entity entity)>();
        world.ForEach<NetId>((Entity e, ref NetId id) => { if (netIds.Contains(id.Value)) entities.Add((id.Value, e)); });
        return Encode(world, registry, entities);
    }

    private static byte[] Encode(World world, ReplicationRegistry registry, List<(int netId, Entity entity)> entities)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entities.Count);
        foreach ((int netId, Entity entity) in entities)
        {
            bw.Write(netId);
            foreach (ComponentCodec codec in registry.Ordered)
                codec.TrySerialize(world, entity, bw); // writes [typeId][data] when present
            bw.Write((ushort)0); // end-of-entity terminator
        }
        bw.Flush();
        return ms.ToArray();
    }
}
