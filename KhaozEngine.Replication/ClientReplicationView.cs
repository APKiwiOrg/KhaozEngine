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

    /// <summary>The snapshot seq of the last applied delta (-1 before any). Acknowledge this back to the server.</summary>
    public int LastAppliedSeq { get; private set; } = -1;

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

    /// <summary>
    /// Non-throwing <see cref="Apply"/>: catches a malformed/incompatible snapshot (e.g. an unregistered
    /// component type id from a newer server protocol) and returns <c>false</c> with the reason in
    /// <paramref name="error"/> instead of throwing into the caller's frame loop. A failed apply may leave the
    /// view partially updated, so the caller should treat <c>false</c> as terminal for the session (disconnect
    /// and surface "client out of date"), not retry against the same stream. Returns <c>true</c> on success.
    /// </summary>
    public bool TryApply(World world, byte[] snapshot, out string? error)
    {
        try
        {
            Apply(world, snapshot);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Non-throwing <see cref="ApplyDelta"/>: same contract as <see cref="TryApply"/> (a baseline
    /// mismatch or an unregistered type id yields <c>false</c> + <paramref name="error"/> instead of a throw).</summary>
    public bool TryApplyDelta(World world, byte[] delta, out string? error)
    {
        try
        {
            ApplyDelta(world, delta);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
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
    /// Applies a baseline+delta produced by <see cref="ServerReplicator.WriteFor"/>: despawns removed entities,
    /// spawns/updates changed ones (removing listed components), and maintains interpolation buffers so
    /// <see cref="Interpolate"/> keeps working. Throws if the delta's baseline does not match
    /// <see cref="LastAppliedSeq"/> (the caller should then request/await a full snapshot, baseline -1).
    /// </summary>
    public void ApplyDelta(World world, byte[] delta)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (delta is null) throw new ArgumentNullException(nameof(delta));

        using var ms = new MemoryStream(delta);
        using var br = new BinaryReader(ms);
        int baselineSeq = br.ReadInt32();
        int snapshotSeq = br.ReadInt32();
        if (baselineSeq != -1 && baselineSeq != LastAppliedSeq)
            throw new InvalidOperationException(
                $"Delta baseline {baselineSeq} does not match last applied {LastAppliedSeq}; a full snapshot is needed.");

        // Interpolation: the pre-delta state becomes 'previous'; changed components below refresh 'current'.
        previousBytes.Clear();
        foreach (KeyValuePair<(int netId, ushort typeId), byte[]> kv in currentBytes) previousBytes[kv.Key] = kv.Value;

        int removedCount = br.ReadInt32();
        for (int i = 0; i < removedCount; i++)
        {
            int netId = br.ReadInt32();
            if (entityByNetId.TryGetValue(netId, out Entity e))
            {
                if (world.IsAlive(e)) world.Despawn(e);
                entityByNetId.Remove(netId);
            }
            RemoveEntityBuffers(netId);
        }

        int changedCount = br.ReadInt32();
        for (int i = 0; i < changedCount; i++)
        {
            int netId = br.ReadInt32();
            br.ReadByte(); // isNew flag — GetOrSpawn handles both; read only to stay byte-aligned
            Entity entity = GetOrSpawn(world, netId);

            int removedCompCount = br.ReadInt32();
            for (int r = 0; r < removedCompCount; r++)
            {
                ushort rtid = br.ReadUInt16();
                if (registry.TryGet(rtid, out ComponentCodec rcodec))
                {
                    if (world.IsAlive(entity)) rcodec.RemoveComponent(world, entity);
                    currentBytes.Remove((netId, rtid));
                    previousBytes.Remove((netId, rtid));
                }
            }

            while (true)
            {
                ushort typeId = br.ReadUInt16();
                if (typeId == 0) break;
                if (!registry.TryGet(typeId, out ComponentCodec codec))
                    throw new InvalidOperationException($"Delta references unregistered type id {typeId}.");
                long posBefore = ms.Position;
                codec.Deserialize(world, entity, br);
                long posAfter = ms.Position;
                if (codec.Interpolatable)
                {
                    var slice = new byte[posAfter - posBefore];
                    Array.Copy(delta, (int)posBefore, slice, 0, slice.Length);
                    currentBytes[(netId, typeId)] = slice;
                }
            }
        }

        LastAppliedSeq = snapshotSeq;
    }

    private void RemoveEntityBuffers(int netIdToRemove)
    {
        var keys = new List<(int netId, ushort typeId)>();
        foreach ((int netId, ushort typeId) key in currentBytes.Keys) if (key.netId == netIdToRemove) keys.Add(key);
        foreach ((int netId, ushort typeId) key in keys) currentBytes.Remove(key);
        keys.Clear();
        foreach ((int netId, ushort typeId) key in previousBytes.Keys) if (key.netId == netIdToRemove) keys.Add(key);
        foreach ((int netId, ushort typeId) key in keys) previousBytes.Remove(key);
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
