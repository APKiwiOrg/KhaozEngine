using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;
using static KhaozEngine.Tests.NetWorld.LegacyDeltaServeFixture;

namespace KhaozEngine.Tests.NetWorld;

[Collection("AllocSensitive")]
public class LegacyDeltaProjectionAllocationTests
{
    [Theory]
    [InlineData(false, 8)]
    [InlineData(false, 32)]
    [InlineData(true, 8)]
    [InlineData(true, 32)]
    public void ProjectionAllocatesOnlyAsTheVisibleViewGrows(bool sharded, int sessions)
    {
        bool showExtras = false;
        var extras = new HashSet<long>();
        using var server = new LegacyDeltaServeFixture(sharded, sessions, (_, id) => showExtras || !extras.Contains(id));
        for (int i = 0; i < 48; i++) extras.Add(Spawn(server, i));
        for (int i = 0; i < 12; i++) Spawn(server, i);

        var meter = new ProjectionMeter(server);
        for (int i = 0; i < 4; i++) meter.Tick();
        Assert.Equal(0, meter.MeasureTick());

        showExtras = true;
        Assert.True(meter.MeasureTick() > 0);
        meter.Tick(); // Warm the second buffer at the larger size.
        Assert.Equal(0, meter.MeasureTick());

        showExtras = false;
        Assert.Equal(0, meter.MeasureTick());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNestedBuildDuringSendLeavesTheLentSlotsSpareUntouched(bool owning)
    {
        bool showExtras = false;
        int nestedSlot = -1;
        var extras = new HashSet<long>();
        using var server = new LegacyDeltaServeFixture(useShards: true, 2,
            (slot, id) => slot != nestedSlot || showExtras || !extras.Contains(id));
        Seat viewer = server.Clients[0];
        nestedSlot = viewer.Net.Slot;
        for (int i = 0; i < 48; i++) extras.Add(Spawn(server, i));

        // The other viewer warms the shared ordering list at the larger size.
        var meter = new ProjectionMeter(server);
        for (int i = 0; i < 4; i++) meter.Tick();

        var fault = new IOException();
        server.Observe = (slot, world, set, owner) =>
        {
            if (slot != nestedSlot) return;
            var expanded = new HashSet<long>(set);
            expanded.UnionWith(extras);
            server.Transport.Observe = (target, frame, _) =>
            {
                if (target != viewer.Connection) return;
                byte[] original = frame.ToArray();
                server.Writer.FailNextLegacyBodyCompletionForTests(fault);
                void Build()
                {
                    if (owning) server.Writer.WriteFor(slot, world, expanded, owner);
                    else
                    {
                        var pass = new LegacyDeltaFrameSender(server.ServerTick);
                        using AoiDeltaReplicator.ServeFrame nested = pass.Build(server.Writer, slot, world, expanded, owner);
                    }
                }
                Assert.Same(fault, Record.Exception(Build));
                Assert.True(frame.SequenceEqual(original));
            };
        };

        meter.Tick();
        server.Observe = null;
        server.Transport.Observe = null;

        showExtras = true;
        // If the nested build grew the reusable spare, this first large regular serve would already be warm.
        Assert.True(meter.MeasureTick() > 0);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(5, true)]
    public void SustainedSmallViewsReleaseTheOutlierCapacity(int lowUses, bool grows)
    {
        var writer = new AoiDeltaReplicator(new ReplicationRegistry());
        var world = new World();
        var large = new HashSet<long>();
        var small = new HashSet<long> { 1 };
        for (int i = 1; i <= 1200; i++)
        {
            world.Set(world.Spawn(), new NetId(i));
            large.Add(i);
        }

        var probe = new LegacyDeltaAllocationProbe();
        writer.LegacyAllocationsForTests = probe;
        void Serve(HashSet<long> view)
        {
            writer.BeginTick();
            using AoiDeltaReplicator.ServeFrame frame = writer.BorrowForServe(0, world, view, 1, prefixLength: 0);
        }

        Serve(large);
        Serve(large);
        for (int i = 0; i < lowUses; i++) Serve(small);

        void AssertNextLargeView()
        {
            probe.Reset();
            AllocationMeasurement.Measure("outlier projection after small views", () => Serve(large));
            Assert.Equal(grows, probe[LegacyDeltaPhase.Projection] > 0);
        }

        // Each large serve uses a different dictionary.
        AssertNextLargeView();
        AssertNextLargeView();
    }

    private static long Spawn(LegacyDeltaServeFixture server, int value)
    {
        (World world, Entity entity) = server.Spawn(new Payload(value));
        return world.Get<NetId>(entity).Value;
    }

    private sealed class ProjectionMeter(LegacyDeltaServeFixture server)
    {
        private readonly LegacyDeltaAllocationProbe probe = Attach(server.Writer);

        private static LegacyDeltaAllocationProbe Attach(AoiDeltaReplicator writer)
        {
            var probe = new LegacyDeltaAllocationProbe();
            writer.LegacyAllocationsForTests = probe;
            return probe;
        }

        internal void Tick()
        {
            probe.Reset();
            server.Tick();
            foreach (Seat client in server.Clients) client.Drain();
        }

        internal long MeasureTick()
        {
            AllocationMeasurement.Measure("legacy serve projection", Tick);
            return probe[LegacyDeltaPhase.Projection];
        }
    }
}
