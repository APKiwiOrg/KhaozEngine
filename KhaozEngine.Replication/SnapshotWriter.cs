using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Serializes a full-state snapshot of a server <see cref="World"/>: each entity carrying a <see cref="NetId"/>
/// and its registered components. Format: <c>[entityCount][per entity: [netId][(typeId,[len],data)...][0]]</c>,
/// where <c>netId</c> is a 64-bit value (widened from 32-bit in 10.0.0) and the 7-bit-encoded <c>len</c> is present
/// only for consumer extension components (see <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) so an older
/// client can skip an id it never registered. The snapshot is opaque <c>byte[]</c> the game ships over its session
/// transport. Use <see cref="WriteFiltered"/> with an interest set for per-client area-of-interest replication: the
/// existing <see cref="ClientReplicationView.Apply"/> then spawns entities that entered the set and despawns those
/// that left.
/// </summary>
public static class SnapshotWriter
{
    /// <summary>
    /// Writes a full-state snapshot of every <see cref="NetId"/> entity in <paramref name="world"/>, including only
    /// the components in <paramref name="channel"/> (see <see cref="ReplicationChannels"/>). Defaults to
    /// <see cref="ReplicationChannels.Replicate"/> with no owner, so a registry using
    /// <see cref="ReplicationChannels.Default"/> everywhere writes byte-identically to before channels existed.
    /// </summary>
    public static byte[] Write(World world, ReplicationRegistry registry,
        ReplicationChannels channel = ReplicationChannels.Replicate, long? ownerNetId = null)
    {
        var entities = new List<(long netId, Entity entity)>();
        world.ForEach<NetId>((Entity e, ref NetId id) => entities.Add((id.Value, e)));
        return Encode(world, registry, entities, channel, ownerNetId, retainedExtensionFrames: null);
    }

    /// <summary>
    /// Writes a snapshot of only the entities whose <see cref="NetId"/> is in <paramref name="netIds"/> (the
    /// client's area of interest). Applied with <see cref="ClientReplicationView.Apply"/>, entities that left
    /// the set are despawned and ones that entered are spawned — full-state-per-interest AoI replication.
    /// Only components in <paramref name="channel"/> are written; on the default
    /// <see cref="ReplicationChannels.Replicate"/> channel an <see cref="ReplicationChannels.OwnerOnly"/> component
    /// is written only for the entity whose net id equals <paramref name="ownerNetId"/> (the receiving client's own
    /// player), and never when it is null (a ghost/handoff/persistence capture supplies its own channel + no owner).
    /// </summary>
    public static byte[] WriteFiltered(World world, ReplicationRegistry registry, IReadOnlySet<long> netIds,
        ReplicationChannels channel = ReplicationChannels.Replicate, long? ownerNetId = null)
    {
        var entities = new List<(long netId, Entity entity)>();
        world.ForEach<NetId>((Entity e, ref NetId id) => { if (netIds.Contains(id.Value)) entities.Add((id.Value, e)); });
        return Encode(world, registry, entities, channel, ownerNetId, retainedExtensionFrames: null);
    }

    /// <summary>
    /// As <see cref="WriteFiltered(World, ReplicationRegistry, IReadOnlySet{long}, ReplicationChannels, long?)"/>, but
    /// after each entity's registered components it re-emits any opaque extension frames
    /// <paramref name="retainedExtensionFrames"/> returns for that net id (length-prefixed, exactly as captured), then
    /// the terminator. This is the write side of retain-and-rewrite: a cell that restored under a registry missing an
    /// extension registration carries the unknown frames forward verbatim, so a registry regression cannot strip data
    /// at rest. The frames MUST be extension ids (&gt;= <see cref="ReplicationRegistry.FirstExtensionTypeId"/>); pass
    /// null (or a provider returning null) for the plain path.
    /// </summary>
    public static byte[] WriteFiltered(World world, ReplicationRegistry registry, IReadOnlySet<long> netIds,
        ReplicationChannels channel, long? ownerNetId,
        Func<long, IReadOnlyList<RetainedComponent>?>? retainedExtensionFrames)
    {
        var entities = new List<(long netId, Entity entity)>();
        world.ForEach<NetId>((Entity e, ref NetId id) => { if (netIds.Contains(id.Value)) entities.Add((id.Value, e)); });
        return Encode(world, registry, entities, channel, ownerNetId, retainedExtensionFrames);
    }

    private static byte[] Encode(World world, ReplicationRegistry registry, List<(long netId, Entity entity)> entities,
        ReplicationChannels channel, long? ownerNetId,
        Func<long, IReadOnlyList<RetainedComponent>?>? retainedExtensionFrames)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entities.Count);
        foreach ((long netId, Entity entity) in entities)
        {
            bw.Write(netId);
            foreach (ComponentCodec codec in registry.Ordered)
                if (codec.ShouldWrite(channel, netId, ownerNetId))
                    codec.TrySerialize(world, entity, bw); // writes [typeId][data] when present
            IReadOnlyList<RetainedComponent>? extra = retainedExtensionFrames?.Invoke(netId);
            if (extra is not null)
                foreach (RetainedComponent rc in extra)
                {
                    bw.Write(rc.TypeId);                       // retained extension frame: [typeId][7-bit len][data]
                    bw.Write7BitEncodedInt(rc.Payload.Length);
                    bw.Write(rc.Payload);
                }
            bw.Write((ushort)0); // end-of-entity terminator
        }
        bw.Flush();
        return ms.ToArray();
    }
}
