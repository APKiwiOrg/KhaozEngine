using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportFootprintProofReuseWorkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("tilted-cylinder")]
    [InlineData("capsule-crown")]
    public void CurvedHitDoesNotReplayCertificationWork(string scenario)
    {
        using var scene = SupportFootprintProofReuseFixture.Geometry(scenario, out var request);
        var events = new List<SupportCurvedWorkObservation>();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true) with { CurvedWork = events.Add });
        IPhysicsSupportFootprintReuseSession? session = null;
        try
        {
            var members = new SupportFootprintMember[256];
            var joins = new ulong[1024];
            byte[] expected;
            using (var lease = scene.World.AcquireQueryReadLease())
            {
                var result = scene.World.QuerySupportFootprint(lease, request, members, joins);
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
                expected = SupportFootprintProofReuseFixture.SerializePayloadBits(result, members, joins);
                session = SupportFootprintProofReuseFixture.Open(scene.World, lease);
            }
            long[] oneEvaluation = Totals(events);
            Assert.True(oneEvaluation.Sum() > 0);
            events.Clear();
            SupportQueryDiagnostics.Reset();
            using (var lease = scene.World.AcquireQueryReadLease()) Assert.Equal(expected, Read(lease));
            var firstCounts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(1, firstCounts.Volumes);
            Assert.Equal(2, firstCounts.Proposals);
            Assert.Equal(1, firstCounts.FootprintReuseMisses);
            Assert.Equal(1, firstCounts.FootprintReuseStores);
            Assert.Equal(oneEvaluation, Totals(events));
            int evaluations = events.Count;
            using (var lease = scene.World.AcquireQueryReadLease()) Assert.Equal(expected, Read(lease));
            using (var lease = scene.World.AcquireQueryReadLease()) Assert.Equal(expected, Read(lease));
            var after = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(2, after.FootprintReuseHits);
            Assert.Equal(firstCounts.Volumes, after.Volumes);
            Assert.Equal(firstCounts.Proposals, after.Proposals);
            Assert.Equal(oneEvaluation, Totals(events));
            Assert.Equal(evaluations, events.Count);
            var retention = SupportQueryDiagnostics.Retention;
            Assert.InRange(retention.PeakKeys, 1, 512);
            Assert.InRange(retention.PeakBytes, 1, 2 * 1024 * 1024);
            session.ResetSource(new(new object(), new object()));
            Released();
            session.Dispose();
            session = null;
            Released();
            output.WriteLine($"CURVED {scenario} events={string.Join(",", oneEvaluation)} " +
                $"evaluations={evaluations} hits={after.FootprintReuseHits} volumes={after.Volumes} proposals={after.Proposals} " +
                $"peakKeys={retention.PeakKeys} peakBytes={retention.PeakBytes} keys=0 bytes=0");

            byte[] Read(IPhysicsQueryLease lease)
            {
                var result = scene.World.QuerySupportFootprint(lease, request, members, joins);
                scene.World.AssertFootprintCurrent(result, lease);
                return SupportFootprintProofReuseFixture.SerializePayloadBits(result, members, joins);
            }
        }
        finally
        {
            session?.Dispose();
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkLimitedRefusalConsumesItsRealWorkAgainAndDoesNotDependOnDiagnostics(bool diagnostics)
    {
        using var scene = SupportFootprintProofReuseFixture.Geometry("tilted-cylinder", out var request);
        var events = new List<SupportCurvedWorkObservation>();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(diagnostics) with { CurvedWork = events.Add });
        IPhysicsSupportFootprintReuseSession? session = null;
        try
        {
            var members = new SupportFootprintMember[256];
            var joins = Enumerable.Repeat(73UL, 1024).ToArray();
            using (var lease = scene.World.AcquireQueryReadLease())
            {
                Assert.Equal(CapsuleFeatureStatus.Complete,
                    scene.World.QuerySupportFootprint(lease, request, members, joins).Status);
                session = SupportFootprintProofReuseFixture.Open(scene.World, lease);
            }
            var scratch = Assert.Single(ConcurrentReadGateTests.Gate(scene.World).ScratchPool.Constructed());
            scratch.Footprints.Idle = new SupportFootprintCollector(32);
            Array.Clear(members);
            Array.Fill(joins, 73UL);
            var memberSentinels = (SupportFootprintMember[])members.Clone();
            var joinSentinels = (ulong[])joins.Clone();
            events.Clear();
            SupportQueryDiagnostics.Reset();
            using (var lease = scene.World.AcquireQueryReadLease()) Refuse(lease);
            long[] first = Totals(events);
            int observed = events.Count;
            events.Clear();
            // A stale count from the first refusal cannot stand in for work on the next lease.
            scratch.Footprints.Idle = new SupportFootprintCollector(32);
            Assert.Equal(0, scratch.Footprints.Idle.CurvedWork.Consumed);
            using (var lease = scene.World.AcquireQueryReadLease()) Refuse(lease);
            Assert.Equal(first, Totals(events));
            Assert.Equal(observed, events.Count);
            var counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(0, counts.FootprintReuseHits);
            Assert.Equal(0, counts.FootprintReuseStores);
            Assert.Equal(diagnostics ? 2 : 0, counts.Volumes);
            Released();
            session.Dispose();
            session = null;
            Released();
            output.WriteLine($"REFUSAL diagnostics={diagnostics} eventsPerRequest={string.Join(",", first)} " +
                $"consumed=32 failure=WorkLimit stores=0 hits=0 keys=0 bytes=0");

            void Refuse(IPhysicsQueryLease lease)
            {
                Assert.Equal(CapsuleFeatureStatus.Unsupported,
                    scene.World.QuerySupportFootprint(lease, request, members, joins).Status);
                Assert.Equal(32, scratch.Footprints.Idle!.CurvedWork.Consumed);
                Assert.Equal(SupportCurvedWorkFailure.WorkLimit, scratch.Footprints.Idle.CurvedWork.Failure);
                Assert.Equal(memberSentinels, members);
                Assert.Equal(joinSentinels, joins);
                Released();
            }
        }
        finally
        {
            session?.Dispose();
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    static long[] Totals(IEnumerable<SupportCurvedWorkObservation> observations)
    {
        var totals = new long[7];
        foreach (var row in observations)
        {
            totals[0] += row.New;
            totals[1] += row.Sign;
            totals[2] += row.Split;
            totals[3] += row.Compile;
            totals[4] += row.RootCells;
            totals[5] += row.Refine;
            totals[6] += row.AdmissionCells;
            Assert.Equal(row.Consumed, row.New + row.Sign + row.Split + row.Compile + row.RootCells + row.Refine + row.AdmissionCells);
        }
        return totals;
    }

    static void Released()
    {
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }
}
