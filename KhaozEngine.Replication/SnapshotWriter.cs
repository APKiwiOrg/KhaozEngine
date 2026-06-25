using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Serializes a full-state snapshot of a server <see cref="World"/>: every entity carrying a <see cref="NetId"/>
/// and its registered components. Format: <c>[entityCount][per entity: [netId][(typeId,data)...][0]]</c>.
/// The snapshot is opaque <c>byte[]</c> the game ships over its session transport.
/// </summary>
public static class SnapshotWriter
{
    /// <summary>Writes a full-state snapshot of all <see cref="NetId"/> entities in <paramref name="world"/>.</summary>
    public static byte[] Write(World world, ReplicationRegistry registry)
    {
        var entities = new List<(int netId, Entity entity)>();
        world.ForEach<NetId>((Entity e, ref NetId id) => entities.Add((id.Value, e)));

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
