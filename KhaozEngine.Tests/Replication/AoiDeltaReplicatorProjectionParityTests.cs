using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Byte-identity acceptance for the O(interestSet) projection in <see cref="AoiDeltaReplicator"/> (which resolves each
/// client's interest set off the shared per-tick capture in O(interest) and re-orders the selection by capture
/// position, instead of walking the whole capture per client). An INDEPENDENT reference encoder - re-derived straight
/// from the documented AoI-delta wire format, with no shared code with the replicator - computes the expected wire
/// for every client every tick, and the real replicator's output is asserted equal byte-for-byte. Driven across
/// randomized multi-tick worlds (entities entering / leaving interest, component add / remove, spawns, despawns),
/// interest sets spanning empty / sparse / dense / full, owner-only scoping, and an ack skip. The reference emits
/// changed entities in world <c>ForEach</c> order and removed entities in the baseline's order, which is exactly what
/// the pre-index full-capture walk produced, so matching it pins the wire against the old behaviour.
/// </summary>
public class AoiDeltaReplicatorProjectionParityTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private struct Vel : IComponent { public float X; public float Y; } // built-in, optional + removable per entity

    private struct Secret : IComponent { public int V; }                // owner-only extension (length-prefixed)

    private const ushort PosId = 1;
    private const ushort VelId = 2;
    private const ushort SecretId = ReplicationRegistry.FirstExtensionTypeId; // 16

    // Component metadata mirroring the registry, in registration (ascending id) order. The reference serializes and
    // orders components off THIS list, independently of any engine internal, so it is a true cross-check.
    private sealed record CompMeta(ushort TypeId, bool OwnerOnly);

    private static readonly CompMeta[] Meta =
    {
        new(PosId, false),
        new(VelId, false),
        new(SecretId, true),
    };

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(PosId, (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Vel>(VelId, (v, bw) => { bw.Write(v.X); bw.Write(v.Y); },
            br => new Vel { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Secret>(SecretId, (s, bw) => bw.Write(s.V), br => new Secret { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        return r;
    }

    // Serializes one component's payload exactly as the registered write delegate does (typeId + framing are added by
    // the wire encoder, not here). Null when the entity does not carry it.
    private static byte[]? Payload(World w, Entity e, ushort typeId)
    {
        switch (typeId)
        {
            case PosId when w.TryGet(e, out Pos p): return Bytes(bw => { bw.Write(p.X); bw.Write(p.Y); });
            case VelId when w.TryGet(e, out Vel v): return Bytes(bw => { bw.Write(v.X); bw.Write(v.Y); });
            case SecretId when w.TryGet(e, out Secret s): return Bytes(bw => bw.Write(s.V));
            default: return null;
        }
    }

    private static byte[] Bytes(System.Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        write(bw);
        bw.Flush();
        return ms.ToArray();
    }

    // ---- Independent reference encoder ----------------------------------------------------------------------------

    // One client's projected, owner-scoped view for a tick: net ids in world ForEach order, each mapped to its present
    // components' payloads (owner-only ones dropped for a non-owner). This is what the client's baseline holds.
    private sealed class RefProjection
    {
        public readonly List<long> Order = new();
        public readonly Dictionary<long, Dictionary<ushort, byte[]>> Comps = new();
    }

    // The reference replicator: same public shape (BeginTick / WriteFor / Acknowledge) and the same baseline+ack
    // semantics as AoiDeltaReplicator, but built straight from the wire format with no shared code.
    private sealed class ReferenceAoiDelta
    {
        private int seq;
        private readonly Dictionary<int, Dictionary<int, RefProjection>> pending = new();
        private readonly Dictionary<int, RefProjection> ackedBaseline = new();
        private readonly Dictionary<int, int> ackedSeq = new();

        public int BeginTick() => ++seq;

        public byte[] WriteFor(int slot, World world, IReadOnlySet<long> interest, long? ownerNetId)
        {
            RefProjection current = Capture(world, interest, ownerNetId);
            RefProjection? baseline = ackedBaseline.GetValueOrDefault(slot);
            int baselineSeq = baseline is null ? -1 : ackedSeq[slot];
            byte[] wire = Encode(baselineSeq, seq, baseline, current);
            RecordPending(slot, seq, current);
            return wire;
        }

        public void Acknowledge(int slot, int ackSeq)
        {
            if (ackedSeq.TryGetValue(slot, out int cur) && ackSeq <= cur) return;
            if (!pending.TryGetValue(slot, out Dictionary<int, RefProjection>? map)
                || !map.TryGetValue(ackSeq, out RefProjection? proj)) return;
            ackedBaseline[slot] = proj;
            ackedSeq[slot] = ackSeq;
        }

        private void RecordPending(int slot, int atSeq, RefProjection proj)
        {
            if (!pending.TryGetValue(slot, out Dictionary<int, RefProjection>? map))
            {
                map = new Dictionary<int, RefProjection>();
                pending[slot] = map;
            }
            map[atSeq] = proj;
        }

        private static RefProjection Capture(World world, IReadOnlySet<long> interest, long? ownerNetId)
        {
            var proj = new RefProjection();
            world.ForEach<NetId>((Entity e, ref NetId id) =>
            {
                long netId = id.Value;
                if (!interest.Contains(netId)) return;
                var comps = new Dictionary<ushort, byte[]>();
                foreach (CompMeta m in Meta)
                {
                    if (m.OwnerOnly && (ownerNetId is null || netId != ownerNetId.Value)) continue; // owner scope
                    byte[]? payload = Payload(world, e, m.TypeId);
                    if (payload is not null) comps[m.TypeId] = payload;
                }
                proj.Order.Add(netId);
                proj.Comps[netId] = comps;
            });
            return proj;
        }

        private static byte[] Encode(int baselineSeq, int currentSeq, RefProjection? baseline, RefProjection current)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(baselineSeq);
            bw.Write(currentSeq);

            // Removed: in the baseline, gone from current, emitted in the baseline's ForEach order.
            var removed = new List<long>();
            if (baseline is not null)
                foreach (long netId in baseline.Order)
                    if (!current.Comps.ContainsKey(netId)) removed.Add(netId);
            bw.Write(removed.Count);
            foreach (long netId in removed) bw.Write(netId);

            // Changed / new: current entities that are new or whose components differ, in current ForEach order.
            var changed = new List<long>();
            foreach (long netId in current.Order)
            {
                bool isNew = baseline is null || !baseline.Comps.ContainsKey(netId);
                if (isNew || EntityChanged(baseline!.Comps[netId], current.Comps[netId])) changed.Add(netId);
            }
            bw.Write(changed.Count);
            foreach (long netId in changed)
            {
                bool isNew = baseline is null || !baseline.Comps.ContainsKey(netId);
                WriteChangedEntity(bw, netId, isNew, isNew ? null : baseline!.Comps[netId], current.Comps[netId]);
            }

            bw.Flush();
            return ms.ToArray();
        }

        private static bool EntityChanged(Dictionary<ushort, byte[]> baseComps, Dictionary<ushort, byte[]> curComps)
        {
            foreach (KeyValuePair<ushort, byte[]> kv in curComps)
                if (!baseComps.TryGetValue(kv.Key, out byte[]? prev) || !ByteEq(prev, kv.Value)) return true;
            foreach (ushort tid in baseComps.Keys)
                if (!curComps.ContainsKey(tid)) return true;
            return false;
        }

        private static void WriteChangedEntity(BinaryWriter bw, long netId, bool isNew,
            Dictionary<ushort, byte[]>? baseComps, Dictionary<ushort, byte[]> curComps)
        {
            bw.Write(netId);
            bw.Write(isNew ? (byte)1 : (byte)0);

            // Removed components (existing entities only), in registration order.
            var removedComps = new List<ushort>();
            if (!isNew)
                foreach (CompMeta m in Meta)
                    if (baseComps!.ContainsKey(m.TypeId) && !curComps.ContainsKey(m.TypeId)) removedComps.Add(m.TypeId);
            bw.Write(removedComps.Count);
            foreach (ushort tid in removedComps) bw.Write(tid);

            // Changed / added components, in registration order.
            foreach (CompMeta m in Meta)
            {
                if (!curComps.TryGetValue(m.TypeId, out byte[]? data)) continue;
                bool include = isNew || baseComps is null
                    || !baseComps.TryGetValue(m.TypeId, out byte[]? prev) || !ByteEq(prev, data);
                if (!include) continue;
                bw.Write(m.TypeId);
                if (ReplicationRegistry.IsExtension(m.TypeId)) bw.Write7BitEncodedInt(data.Length);
                bw.Write(data);
            }
            bw.Write((ushort)0);
        }

        private static bool ByteEq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    // ---- Driver ---------------------------------------------------------------------------------------------------

    private readonly struct Slot
    {
        public Slot(int id, long? owner, double interestFraction)
        {
            Id = id;
            Owner = owner;
            InterestFraction = interestFraction;
        }

        public int Id { get; }
        public long? Owner { get; }
        public double InterestFraction { get; } // -1 => full, 0 => empty, else random subset of that density
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(17UL)]
    [InlineData(2718UL)]
    public void RandomizedMultiTick_RealMatchesIndependentReference_ByteForByte(ulong seed)
    {
        var rng = new DeterministicRng(seed);
        ReplicationRegistry registry = NewRegistry();
        var world = new World();
        var handles = new Dictionary<long, Entity>();

        // Seed ~40 entities, ids 1..40. The first few are "players" carrying the owner-only Secret.
        long nextNetId = 1;
        const int players = 4;
        for (int i = 0; i < 40; i++)
        {
            long netId = nextNetId++;
            Entity e = world.Spawn();
            world.Set(e, new NetId(netId));
            world.Set(e, new Pos { X = rng.NextFloat() * 100f, Y = rng.NextFloat() * 100f });
            if (rng.NextFloat() < 0.6f) world.Set(e, new Vel { X = rng.NextFloat(), Y = rng.NextFloat() });
            if (netId <= players) world.Set(e, new Secret { V = (int)(rng.NextULong() & 0xFFFF) });
            handles[netId] = e;
        }

        var real = new AoiDeltaReplicator(registry);
        var reference = new ReferenceAoiDelta();

        // Slots: owners of players 1..4 with varied interest densities, plus observers with empty / sparse / dense /
        // full interest and no owner. The slot that skips an ack (id 2) forces a multi-tick baseline that stays put.
        var slots = new[]
        {
            new Slot(0, 1L, 0.5),
            new Slot(1, 2L, 0.25),
            new Slot(2, 3L, -1.0),   // full interest, will skip an ack on one tick
            new Slot(3, 4L, 0.75),
            new Slot(4, null, 0.0),  // empty interest
            new Slot(5, null, 0.05), // sparse
            new Slot(6, null, 1.0),  // dense (~all)
        };

        for (int tick = 1; tick <= 12; tick++)
        {
            // Mutate the world: move some, toggle Vel on some, occasionally spawn / despawn a non-player.
            var live = new List<long>(handles.Keys);
            foreach (long netId in live)
            {
                Entity e = handles[netId];
                if (rng.NextFloat() < 0.5f)
                {
                    ref Pos p = ref world.Get<Pos>(e);
                    p.X += rng.NextFloat() * 2f - 1f;
                    p.Y += rng.NextFloat() * 2f - 1f;
                }
                if (rng.NextFloat() < 0.2f)
                {
                    if (world.Has<Vel>(e)) world.Remove<Vel>(e);
                    else world.Set(e, new Vel { X = rng.NextFloat(), Y = rng.NextFloat() });
                }
            }
            if (rng.NextFloat() < 0.4f)
            {
                long netId = nextNetId++;
                Entity e = world.Spawn();
                world.Set(e, new NetId(netId));
                world.Set(e, new Pos { X = rng.NextFloat() * 100f, Y = rng.NextFloat() * 100f });
                if (rng.NextFloat() < 0.5f) world.Set(e, new Vel { X = rng.NextFloat(), Y = rng.NextFloat() });
                handles[netId] = e;
            }
            if (rng.NextFloat() < 0.3f)
            {
                // Despawn a random non-player entity.
                var candidates = new List<long>();
                foreach (long id in handles.Keys) if (id > players) candidates.Add(id);
                if (candidates.Count > 0)
                {
                    long victim = candidates[(int)(rng.NextULong() % (ulong)candidates.Count)];
                    world.Despawn(handles[victim]);
                    handles.Remove(victim);
                }
            }

            // Interest sets are recomputed each tick from the CURRENT population, identical for both encoders.
            var allNetIds = new List<long>(handles.Keys);
            allNetIds.Sort();

            int realSeq = real.BeginTick();
            int refSeq = reference.BeginTick();
            Assert.Equal(realSeq, refSeq);

            foreach (Slot slot in slots)
            {
                HashSet<long> interest = InterestFor(slot, allNetIds, rng);
                byte[] expected = reference.WriteFor(slot.Id, world, interest, slot.Owner);
                byte[] actual = real.WriteFor(slot.Id, world, interest, slot.Owner);
                Assert.Equal(expected, actual);
            }

            // Ack the tick just sent for every slot except slot 2 on tick 3 (a deliberate ack skip that keeps its
            // baseline two ticks back and exercises a longer diff span identically in both encoders).
            foreach (Slot slot in slots)
            {
                if (slot.Id == 2 && tick == 3) continue;
                real.Acknowledge(slot.Id, realSeq);
                reference.Acknowledge(slot.Id, refSeq);
            }
        }
    }

    private static HashSet<long> InterestFor(Slot slot, List<long> allNetIds, DeterministicRng rng)
    {
        if (slot.InterestFraction < 0) return new HashSet<long>(allNetIds); // full
        var set = new HashSet<long>();
        if (slot.InterestFraction == 0.0) return set;                       // empty
        foreach (long id in allNetIds)
            if (rng.NextFloat() < slot.InterestFraction) set.Add(id);
        // Always include the slot's own player when present, so owner-only scoping is actually exercised.
        if (slot.Owner is long owner && allNetIds.Contains(owner)) set.Add(owner);
        return set;
    }
}
