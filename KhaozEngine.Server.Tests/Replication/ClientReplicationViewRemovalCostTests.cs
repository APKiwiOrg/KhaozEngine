using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Dropping an entity's buffered state costs the components on THAT entity, not a walk of everything the client
/// tracks (#138). <c>RemoveEntityBuffers</c> used to scan all of <c>currentBytes</c>, then all of
/// <c>previousBytes</c>, then all of <c>sampleHistory</c>, collecting the matching keys into a list it allocated per
/// call. It runs once per departed entity from three sites on the apply path, so a delta dropping R entities out of T
/// tracked buffer entries cost O(R x T) and one collector list per removal. The spike lands on an area-of-interest
/// boundary crossing in a dense area, which is where a frame hitch is most visible.
/// <para>The measurable consequence is the collector list: the scans need somewhere to put the keys they find, and a
/// netId-keyed index needs nowhere at all. So the guard is per-removal allocation, and the second assertion pins the
/// threshold tight enough that the old shape could not slip under it. Joins <c>AllocSensitive</c> because it reads
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>.</para>
/// </summary>
[Collection("AllocSensitive")]
public class ClientReplicationViewRemovalCostTests
{
    private struct C0 : IComponent { public float V; }
    private struct C1 : IComponent { public float V; }
    private struct C2 : IComponent { public float V; }
    private struct C3 : IComponent { public float V; }
    private struct C4 : IComponent { public float V; }
    private struct C5 : IComponent { public float V; }
    private struct C6 : IComponent { public float V; }
    private struct C7 : IComponent { public float V; }

    // 400 entities carrying the eight components above, so the view tracks 3200 buffer entries per dictionary and a
    // scan-per-removal implementation walks 3200 keys three times for each of the 400 entities the delta drops.
    private const int Entities = 400;

    // Every component carries a lerp, which is what makes it fixed-delay sampled and therefore buffered at all.
    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<C0>(1, (c, bw) => bw.Write(c.V), br => new C0 { V = br.ReadSingle() },
            (a, b, t) => new C0 { V = a.V + (b.V - a.V) * t });
        r.Register<C1>(2, (c, bw) => bw.Write(c.V), br => new C1 { V = br.ReadSingle() },
            (a, b, t) => new C1 { V = a.V + (b.V - a.V) * t });
        r.Register<C2>(3, (c, bw) => bw.Write(c.V), br => new C2 { V = br.ReadSingle() },
            (a, b, t) => new C2 { V = a.V + (b.V - a.V) * t });
        r.Register<C3>(4, (c, bw) => bw.Write(c.V), br => new C3 { V = br.ReadSingle() },
            (a, b, t) => new C3 { V = a.V + (b.V - a.V) * t });
        r.Register<C4>(5, (c, bw) => bw.Write(c.V), br => new C4 { V = br.ReadSingle() },
            (a, b, t) => new C4 { V = a.V + (b.V - a.V) * t });
        r.Register<C5>(6, (c, bw) => bw.Write(c.V), br => new C5 { V = br.ReadSingle() },
            (a, b, t) => new C5 { V = a.V + (b.V - a.V) * t });
        r.Register<C6>(7, (c, bw) => bw.Write(c.V), br => new C6 { V = br.ReadSingle() },
            (a, b, t) => new C6 { V = a.V + (b.V - a.V) * t });
        r.Register<C7>(8, (c, bw) => bw.Write(c.V), br => new C7 { V = br.ReadSingle() },
            (a, b, t) => new C7 { V = a.V + (b.V - a.V) * t });
        return r;
    }

    private static byte[] PopulatedSnapshot(ReplicationRegistry registry)
    {
        var server = new World();
        for (int i = 0; i < Entities; i++)
        {
            Entity e = server.Spawn();
            server.Set(e, new NetId(i + 1));
            server.Set(e, new C0 { V = i });
            server.Set(e, new C1 { V = i });
            server.Set(e, new C2 { V = i });
            server.Set(e, new C3 { V = i });
            server.Set(e, new C4 { V = i });
            server.Set(e, new C5 { V = i });
            server.Set(e, new C6 { V = i });
            server.Set(e, new C7 { V = i });
        }
        return SnapshotWriter.Write(server, registry);
    }

    // A delta that removes every tracked entity and changes none: baseline -1 (a full snapshot carrying nothing),
    // the removed netId list, then a zero changed count, which is where ApplyDelta's read stops.
    private static byte[] RemoveEveryEntityDelta()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(-1);         // baselineSeq
        bw.Write(1);          // snapshotSeq
        bw.Write(Entities);   // removedCount
        for (int i = 0; i < Entities; i++) bw.Write((long)(i + 1));
        bw.Write(0);          // changedCount
        bw.Flush();
        return ms.ToArray();
    }

    // A view holding two snapshots' worth of buffers (so previousBytes is populated too) plus a recorded sample
    // history, i.e. all three dictionaries carrying Entities * Comps keys.
    private static (World World, ClientReplicationView View) Populated(ReplicationRegistry registry, byte[] snapshot)
    {
        var world = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(world, snapshot);
        view.Apply(world, snapshot);
        view.RecordInterpolationSample(1.0);
        return (world, view);
    }

    // Retires every entity from the client World up front, so the measured ApplyDelta skips the ECS despawn (the view
    // guards it with IsAlive) and measures only what dropping the BUFFERS costs. World.Despawn allocates in its own
    // right, an archetype-side cost this issue is not about and one that would sit in the window as a constant per
    // entity, blunting the discriminator. The view's own bookkeeping is untouched: entityByNetId still names all of
    // them, so the removal loop still calls RemoveEntityBuffers once per entity.
    private static void DespawnAllInWorld(World world, ClientReplicationView view)
    {
        foreach (Entity e in new List<Entity>(view.Entities.Values)) world.Despawn(e);
    }

    [Fact]
    public void Dropping_entities_does_not_allocate_per_removal()
    {
        ReplicationRegistry registry = NewRegistry();
        byte[] snapshot = PopulatedSnapshot(registry);
        byte[] delta = RemoveEveryEntityDelta();

        // Warm up on a throwaway view so JIT and the shared type handles land outside the measured window.
        (World warmWorld, ClientReplicationView warmView) = Populated(registry, snapshot);
        DespawnAllInWorld(warmWorld, warmView);
        warmView.ApplyDelta(warmWorld, delta);

        (World world, ClientReplicationView view) = Populated(registry, snapshot);
        DespawnAllInWorld(world, view);   // isolate the buffer bookkeeping from the ECS despawn, see the helper

        long before = GC.GetAllocatedBytesForCurrentThread();
        view.ApplyDelta(world, delta);
        long measured = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(view.Entities);   // the delta really did drop all of them

        // What the measured apply legitimately allocates: the MemoryStream + BinaryReader over the delta bytes and the
        // seen-set a baseline -1 delta builds. All fixed, none of it per removal.
        const long threshold = 4096;

        // A conservative floor for the old shape: ONE empty List<(long, ushort)> per removal is 32 bytes on 64-bit
        // (object header plus the items reference, count and version fields), before any of the backing arrays it
        // grew to hold the keys each of the three scans found. So the old code allocated at least this much more.
        const long oldCollectorFloor = (long)Entities * 32;

        Assert.True(measured < threshold,
            $"dropping {Entities} entities allocated {measured} B (threshold {threshold}); the per-removal collector " +
            $"list of the scanning implementation would add at least {oldCollectorFloor} B on top.");
        Assert.True(threshold <= oldCollectorFloor,
            $"threshold {threshold} must stay at or below the {oldCollectorFloor} B of collector lists the scanning " +
            "implementation allocated on their own, or raising it would let that implementation back in.");
    }

    [Fact]
    public void A_netId_that_leaves_and_returns_brings_no_stale_interpolation_history()
    {
        // The index is what removal now trusts, so this is the guard on it holding every buffered typeId: a leftover
        // sample would be lerped against the returning entity's first new one and streak it in from where it left.
        ReplicationRegistry registry = NewRegistry();
        var server = new World();
        Entity s = server.Spawn();
        server.Set(s, new NetId(7));
        server.Set(s, new C0 { V = 100f });

        var world = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(world, SnapshotWriter.Write(server, registry));
        view.RecordInterpolationSample(1.0);

        // An empty snapshot: full-state semantics despawn netId 7 and drop its buffers.
        view.Apply(world, SnapshotWriter.Write(new World(), registry));
        Assert.False(view.TryGetEntity(7, out _));

        // It comes back somewhere else entirely.
        server.Get<C0>(s).V = 900f;
        view.Apply(world, SnapshotWriter.Write(server, registry));
        view.RecordInterpolationSample(2.0);

        // One sample in the buffer, so this renders it. A surviving t=1.0 sample would lerp 100 -> 900 instead.
        view.InterpolateAt(world, 1.5);
        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(900f, world.Get<C0>(c).V, 3);
    }
}
