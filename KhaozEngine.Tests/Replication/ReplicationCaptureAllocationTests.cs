using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Steady-state allocation guard for the pooled, span-based capture path. The capture used to allocate a
/// <c>byte[]</c> per component per entity every tick (MemoryStream + BinaryWriter + ToArray inside the codec). It now
/// serializes the whole capture into one consolidated buffer with <c>(offset, length)</c> segments over it, diffs via
/// <see cref="ReadOnlySpan{T}"/> equality, and slices payloads straight to the wire. This test drives a populated
/// registry, a few clients with stable area of interest and steady acks, warms up past the first full snapshot, then
/// asserts per-tick allocation sits far below the old per-component churn. Joins <c>AllocSensitive</c> because it reads
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, which must not race the GC-churning parallel tests.
/// </summary>
[Collection("AllocSensitive")]
public class ReplicationCaptureAllocationTests
{
    // Eight distinct replicated components. C0 is mutated every tick (the steady delta), while C1..C7 are static
    // fillers that are captured but never change, so they exercise the capture path without inflating the delta.
    private struct C0 : IComponent { public float V; }
    private struct C1 : IComponent { public int V; }
    private struct C2 : IComponent { public int V; }
    private struct C3 : IComponent { public int V; }
    private struct C4 : IComponent { public int V; }
    private struct C5 : IComponent { public int V; }
    private struct C6 : IComponent { public int V; }
    private struct C7 : IComponent { public int V; }

    private const int Entities = 256;
    private const int Comps = 8;      // components per entity (C0..C7)
    private const int Clients = 3;
    private const int AoiWindow = 64; // entities each client keeps in interest (stable across ticks)

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<C0>(1, (c, bw) => bw.Write(c.V), br => new C0 { V = br.ReadSingle() });
        r.Register<C1>(2, (c, bw) => bw.Write(c.V), br => new C1 { V = br.ReadInt32() });
        r.Register<C2>(3, (c, bw) => bw.Write(c.V), br => new C2 { V = br.ReadInt32() });
        r.Register<C3>(4, (c, bw) => bw.Write(c.V), br => new C3 { V = br.ReadInt32() });
        r.Register<C4>(5, (c, bw) => bw.Write(c.V), br => new C4 { V = br.ReadInt32() });
        r.Register<C5>(6, (c, bw) => bw.Write(c.V), br => new C5 { V = br.ReadInt32() });
        r.Register<C6>(7, (c, bw) => bw.Write(c.V), br => new C6 { V = br.ReadInt32() });
        r.Register<C7>(8, (c, bw) => bw.Write(c.V), br => new C7 { V = br.ReadInt32() });
        return r;
    }

    [Fact]
    public void SteadyStateCapture_AllocatesFarBelowPerComponentChurn()
    {
        ReplicationRegistry registry = NewRegistry();
        var world = new World();
        var entities = new Entity[Entities];
        for (int i = 0; i < Entities; i++)
        {
            Entity e = world.Spawn();
            world.Set(e, new NetId(i + 1));
            world.Set(e, new C0 { V = i });
            world.Set(e, new C1 { V = i });
            world.Set(e, new C2 { V = i });
            world.Set(e, new C3 { V = i });
            world.Set(e, new C4 { V = i });
            world.Set(e, new C5 { V = i });
            world.Set(e, new C6 { V = i });
            world.Set(e, new C7 { V = i });
            entities[i] = e;
        }

        var repl = new AoiDeltaReplicator(registry);

        // Stable per-client interest: a fixed AoiWindow-entity slice each, built once and reused every tick, so no
        // interest-set allocation lands inside the measured loop.
        var interest = new IReadOnlySet<long>[Clients];
        for (int c = 0; c < Clients; c++)
        {
            var set = new HashSet<long>(AoiWindow);
            int start = c * 32;
            for (int k = 0; k < AoiWindow; k++) set.Add((start + k) % Entities + 1);
            interest[c] = set;
        }

        int prevSeq = 0;
        void Tick()
        {
            for (int i = 0; i < Entities; i++) world.Get<C0>(entities[i]).V += 1f; // move C0 only -> steady delta
            if (prevSeq > 0)
                for (int c = 0; c < Clients; c++) repl.Acknowledge(c, prevSeq);   // 1-tick RTT ack -> delta from last ack
            int seq = repl.BeginTick();
            for (int c = 0; c < Clients; c++) repl.WriteFor(c, world, interest[c]);
            prevSeq = seq;
        }

        // Warm up: JIT, drop the always-full first snapshot, and grow the reused capture/wire scratch capacities so the
        // measured window sees only steady-state allocation.
        for (int i = 0; i < 64; i++) Tick();

        const int measured = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measured; i++) Tick();
        long after = GC.GetAllocatedBytesForCurrentThread();
        long perTick = (after - before) / measured;

        // Budget breakdown (per tick), everything the segment scheme legitimately still allocates:
        //  - one shared world capture rebuilt each tick (history retains it for up to historyDepth ticks): one
        //    consolidated payload buffer, one outer netId->components dictionary, and per entity one CapturedComponents
        //    plus one small typeId->segment dictionary. No byte[] per component.
        //  - per client (fast path, no owner-scoped component here): one projected baseline dictionary retained for the
        //    ack (it references the shared capture, so no payload copy) plus the outgoing wire byte[] WriteFor returns
        //    (the caller owns it, so it is unavoidable).
        // What it must NOT allocate is a byte[] per present component per entity: the prior representation did exactly
        // that, Entities*Comps arrays every tick. Their object headers alone are Entities*Comps*24 = ~49 KB/tick, more
        // than double the slack between the measured steady state and this threshold, so reverting to the byte[]-per-
        // component representation pushes per-tick allocation over the bar and fails this test.
        // Measured steady state is ~180 KB/tick (segment dicts + projected baselines + wire, none of them a
        // per-component array). The bar sits ~11% above that for runtime noise and ~29 KB below the old scheme's total.
        const long threshold = 200_000;
        long oldPerComponentHeaderFloor = (long)Entities * Comps * 24; // headers only, before any payload

        // The two bounds sandwich the threshold in (perTick, perTick + headerFloor]: it passes at the measured
        // allocation now, and since the byte[]-per-component representation allocated about perTick + headerFloor per
        // tick (each payload moves out of the shared buffer into its own array, adding a >= 24 B object header), it
        // breaches the same threshold and fails. So the bar is provably tight enough to catch a representation revert.
        Assert.True(perTick < threshold,
            $"steady-state capture allocated {perTick} B/tick (threshold {threshold}); the per-component byte[] " +
            $"representation would add at least {oldPerComponentHeaderFloor} B/tick of array headers on top.");
        Assert.True(perTick + oldPerComponentHeaderFloor >= threshold,
            $"threshold {threshold} is too loose to discriminate: measured {perTick} + old header floor " +
            $"{oldPerComponentHeaderFloor} does not reach it, so a representation revert could still pass.");
    }
}
