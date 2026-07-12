using System.Collections.Generic;
using KhaozEngine.Benchmarks;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>
/// Headless acceptance for the replication-hotpath jobs-1 benchmark harness. The timing loop itself is not
/// unit-tested (wall-clock and allocation counts are observational, run via <c>dotnet run</c>) - what is asserted
/// here is the harness's deterministic and structural behaviour, and that it actually drives the real
/// <see cref="AoiDeltaReplicator"/> per-client hot path (interest set, full-then-delta, ack promotion) rather than
/// a stand-in.
/// </summary>
public class ReplicationTickBenchmarkTests
{
    private static ReplicationBenchmarkConfig SmallConfig(int clients = 3, int entities = 10, int componentsPerEntity = 1,
        ulong seed = 0xC0FFEEUL, int warmup = 0, int timed = 3, float fieldSize = 100f, float moveStep = 0.5f) => new()
    {
        Name = "test",
        ClientCount = clients,
        EntityCount = entities,
        ComponentsPerEntity = componentsPerEntity,
        Seed = seed,
        WarmupTicks = warmup,
        TimedTicks = timed,
        FieldSize = fieldSize,
        MoveStep = moveStep,
    };

    [Fact]
    public void Build_CreatesExpectedEntityAndClientCounts()
    {
        ReplicationBenchmarkConfig config = SmallConfig(clients: 5, entities: 20);
        ReplicationPopulation pop = ReplicationTickBenchmark.Build(config);

        Assert.Equal(20, pop.Entities.Length);
        Assert.Equal(20, pop.NetIds.Length);
        Assert.Equal(20, pop.VelX.Length);
        Assert.Equal(20, pop.VelY.Length);
        Assert.Equal(5, pop.Clients.Length);

        // Every entity actually carries a NetId in the world (not just the parallel array).
        int seen = 0;
        pop.World.ForEach<NetId>((Entity _, ref NetId _) => seen++);
        Assert.Equal(20, seen);
    }

    [Fact]
    public void Build_RegistersFillerComponentsOnlyWhenComponentsPerEntityCallsForThem()
    {
        ReplicationPopulation one = ReplicationTickBenchmark.Build(SmallConfig(entities: 4, componentsPerEntity: 1));
        Assert.False(one.World.TryGet<ReplFillerA>(one.Entities[0], out _));

        ReplicationPopulation four = ReplicationTickBenchmark.Build(SmallConfig(entities: 4, componentsPerEntity: 4));
        Assert.True(four.World.TryGet<ReplFillerA>(four.Entities[0], out _));
        Assert.True(four.World.TryGet<ReplFillerB>(four.Entities[0], out _));
        Assert.True(four.World.TryGet<ReplFillerC>(four.Entities[0], out _));
    }

    [Fact]
    public void Build_IsDeterministic_SameSeedYieldsIdenticalPopulation()
    {
        ReplicationBenchmarkConfig config = SmallConfig(seed: 12345UL, entities: 15);

        ReplicationPopulation a = ReplicationTickBenchmark.Build(config);
        ReplicationPopulation b = ReplicationTickBenchmark.Build(config);

        Assert.Equal(a.NetIds, b.NetIds);
        Assert.Equal(a.VelX, b.VelX);
        Assert.Equal(a.VelY, b.VelY);
        for (int i = 0; i < a.Entities.Length; i++)
        {
            ReplPosition pa = a.World.Get<ReplPosition>(a.Entities[i]);
            ReplPosition pb = b.World.Get<ReplPosition>(b.Entities[i]);
            Assert.Equal(pa.X, pb.X);
            Assert.Equal(pa.Y, pb.Y);
        }
        for (int c = 0; c < a.Clients.Length; c++)
            Assert.Equal(a.Clients[c], b.Clients[c]);
    }

    [Fact]
    public void Build_IsDeterministic_SameSeedYieldsIdenticalFirstTickWireBytes()
    {
        ReplicationBenchmarkConfig config = SmallConfig(seed: 777UL, warmup: 0, timed: 1);

        ReplicationBenchmarkResult a = ReplicationTickBenchmark.Run(config);
        ReplicationBenchmarkResult b = ReplicationTickBenchmark.Run(config);

        Assert.Equal(a.WireBytesTotal, b.WireBytesTotal);
        Assert.True(a.WireBytesTotal > 0);
    }

    [Fact]
    public void Build_DifferentSeedYieldsDifferentPopulation()
    {
        ReplicationPopulation a = ReplicationTickBenchmark.Build(SmallConfig(seed: 1UL, entities: 15));
        ReplicationPopulation b = ReplicationTickBenchmark.Build(SmallConfig(seed: 2UL, entities: 15));

        Assert.NotEqual(a.VelX, b.VelX);
    }

    [Fact]
    public void Run_MeasuresEveryTimedTick_AndProducesWireOutput()
    {
        ReplicationBenchmarkConfig config = SmallConfig(clients: 4, entities: 50, warmup: 2, timed: 7);
        ReplicationBenchmarkResult result = ReplicationTickBenchmark.Run(config);

        Assert.Equal(7, result.TicksMeasured);
        Assert.True(result.ElapsedMs >= 0.0);
        Assert.True(result.PerTickMs >= 0.0);
        Assert.True(result.WireBytesTotal > 0);
        Assert.True(result.WireBytesPerTick > 0);
        Assert.True(result.AllocatedBytes >= 0);
    }

    [Fact]
    public void WriteFor_ProducesNonEmptyBody_WhenClientHasEntitiesInAoi()
    {
        ReplicationBenchmarkConfig config = SmallConfig(clients: 1, entities: 10);
        ReplicationPopulation pop = ReplicationTickBenchmark.Build(config);

        pop.Grid.Clear();
        pop.World.ForEach(pop.InsertIntoGrid);
        pop.Replicator.BeginTick();

        // Query centered exactly on a known entity's own position with a small positive radius: that entity is
        // guaranteed in range (distance 0), independent of the client AoI sizing / population randomness - so
        // this pins WriteFor's behaviour deterministically rather than relying on a client's circle happening to
        // cover a seeded entity.
        ReplPosition target = pop.World.Get<ReplPosition>(pop.Entities[0]);
        HashSet<long> interest = pop.Grid.Query(target.X, target.Y, 1f);
        Assert.Contains(pop.NetIds[0], interest);

        byte[] body = pop.Replicator.WriteFor(pop.Clients[0].Slot, pop.World, interest);
        Assert.NotEmpty(body);
    }

    [Fact]
    public void AckPromotion_SecondTickDeltaIsSmallerThanFirstTickFullSnapshot_ForAnUnchangedFiller()
    {
        // componentsPerEntity=4: every entity always carries the never-mutated fillers, so a full first-tick
        // snapshot (position + 3 fillers per entity) must be strictly larger than the acked steady-state delta
        // (position only - the fillers never differ from the acked baseline, so they drop out of the delta).
        // A tiny MoveStep keeps every client's interest-set membership identical between the two ticks, so the
        // size drop is attributable to the delta encoding, not entities entering/leaving AoI.
        ReplicationBenchmarkConfig config = SmallConfig(clients: 1, entities: 150, componentsPerEntity: 4,
            fieldSize: 50f, moveStep: 0.001f);
        ReplicationPopulation pop = ReplicationTickBenchmark.Build(config);
        ClientAoi client = pop.Clients[0];

        // Tick 1: no baseline yet, so every in-AoI entity is sent in full (position + all 3 fillers).
        pop.Grid.Clear();
        pop.World.ForEach(pop.InsertIntoGrid);
        int seq1 = pop.Replicator.BeginTick();
        HashSet<long> interest1 = pop.Grid.Query(client.Cx, client.Cy, client.Radius);
        Assert.NotEmpty(interest1); // sanity: the config must put entities in AoI for this comparison to mean anything
        byte[] body1 = pop.Replicator.WriteFor(client.Slot, pop.World, interest1);

        // Simulated 1-tick RTT: the client acks tick 1's seq before tick 2 is built.
        pop.Replicator.Acknowledge(client.Slot, seq1);

        // Tick 2: move (position changes, fillers don't), rebuild the grid, write the delta from the acked baseline.
        for (int i = 0; i < pop.Entities.Length; i++)
        {
            ref ReplPosition pos = ref pop.World.Get<ReplPosition>(pop.Entities[i]);
            pos.X += pop.VelX[i] * config.MoveStep;
            pos.Y += pop.VelY[i] * config.MoveStep;
        }
        pop.Grid.Clear();
        pop.World.ForEach(pop.InsertIntoGrid);
        pop.Replicator.BeginTick();
        HashSet<long> interest2 = pop.Grid.Query(client.Cx, client.Cy, client.Radius);
        Assert.True(interest1.SetEquals(interest2), "the tiny MoveStep must not have changed AoI membership"); // isolates the size drop to the delta encoding, not AoI churn
        byte[] body2 = pop.Replicator.WriteFor(client.Slot, pop.World, interest2);

        Assert.True(body2.Length < body1.Length,
            $"expected the acked delta ({body2.Length} bytes) to be smaller than the full first snapshot ({body1.Length} bytes)");
    }
}
