using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Applies full-state snapshots into a client <see cref="World"/>: spawns entities new this snapshot, despawns
/// entities absent from it, and updates the rest (keyed by <see cref="NetId"/>). For components registered with
/// a lerp, it double-buffers the last two snapshots so <see cref="Interpolate"/> can smooth them at render time.
/// </summary>
public sealed class ClientReplicationView
{
    private readonly ReplicationRegistry registry;
    private readonly Dictionary<int, Entity> entityByNetId = new();
    private readonly Dictionary<(int netId, ushort typeId), byte[]> currentBytes = new();
    private readonly Dictionary<(int netId, ushort typeId), byte[]> previousBytes = new();

    public ClientReplicationView(ReplicationRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>The live NetId→Entity map.</summary>
    public IReadOnlyDictionary<int, Entity> Entities => entityByNetId;

    /// <summary>Looks up the local entity for a network id.</summary>
    public bool TryGetEntity(int netId, out Entity entity) => entityByNetId.TryGetValue(netId, out entity);

    /// <summary>
    /// Applies a snapshot: spawn-if-new + update every netId present, despawn every netId absent. Captures
    /// interpolatable components' raw bytes (shifting the prior snapshot's into the interpolation "previous").
    /// </summary>
    public void Apply(World world, byte[] snapshot)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        // Shift current -> previous for interpolation.
        previousBytes.Clear();
        foreach (KeyValuePair<(int, ushort), byte[]> kv in currentBytes) previousBytes[kv.Key] = kv.Value;
        currentBytes.Clear();

        using var ms = new MemoryStream(snapshot);
        using var br = new BinaryReader(ms);
        int count = br.ReadInt32();
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int netId = br.ReadInt32();
            seen.Add(netId);
            Entity entity = GetOrSpawn(world, netId);
            while (true)
            {
                ushort typeId = br.ReadUInt16();
                if (typeId == 0) break;
                if (!registry.TryGet(typeId, out ComponentCodec codec))
                    throw new InvalidOperationException($"Snapshot references unregistered type id {typeId}.");
                long posBefore = ms.Position;
                codec.Deserialize(world, entity, br);
                long posAfter = ms.Position;
                if (codec.Interpolatable)
                {
                    var slice = new byte[posAfter - posBefore];
                    Array.Copy(snapshot, (int)posBefore, slice, 0, slice.Length);
                    currentBytes[(netId, typeId)] = slice;
                }
            }
        }

        // Full-state: anything we still track but didn't see is gone.
        List<int>? gone = null;
        foreach (KeyValuePair<int, Entity> kv in entityByNetId)
            if (!seen.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
        if (gone is not null)
        {
            foreach (int netId in gone)
            {
                if (world.IsAlive(entityByNetId[netId])) world.Despawn(entityByNetId[netId]);
                entityByNetId.Remove(netId);
            }
        }
    }

    private Entity GetOrSpawn(World world, int netId)
    {
        if (entityByNetId.TryGetValue(netId, out Entity e) && world.IsAlive(e)) return e;
        e = world.Spawn();
        world.Set(e, new NetId(netId));
        entityByNetId[netId] = e;
        return e;
    }

    /// <summary>
    /// Writes interpolated values (<c>lerp(previous, current, alpha)</c>) for every interpolatable component
    /// that has both a previous and current snapshot. <paramref name="alpha"/> is the render fraction in [0,1].
    /// </summary>
    public void Interpolate(World world, float alpha)
    {
        foreach (KeyValuePair<(int netId, ushort typeId), byte[]> kv in currentBytes)
        {
            if (!previousBytes.TryGetValue(kv.Key, out byte[]? prev)) continue;
            if (!entityByNetId.TryGetValue(kv.Key.netId, out Entity e) || !world.IsAlive(e)) continue;
            if (!registry.TryGet(kv.Key.typeId, out ComponentCodec codec) || codec.LerpFromBytes is null) continue;
            codec.LerpFromBytes(world, e, prev, kv.Value, alpha);
        }
    }
}
