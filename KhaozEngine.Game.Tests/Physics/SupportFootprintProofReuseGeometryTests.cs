using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportFootprintProofReuseGeometryTests
{
    [Theory]
    [InlineData("tilted-cylinder")]
    [InlineData("capsule-crown")]
    [InlineData("tiny-face")]
    [InlineData("coincident-statics")]
    [InlineData("coincident-children")]
    [InlineData("mesh-edge-joins")]
    [InlineData("compound-hulls")]
    [InlineData("certified-hull")]
    [InlineData("independent-roof")]
    public void CurvedMeshAndCompoundPayloadsRemainBitExact(string scenario)
    {
        using var scene = SupportFootprintProofReuseFixture.Geometry(scenario, out var request);
        Check(scene.World, request, scenario == "independent-roof");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InstalledLaneBandCStairsKeepTheirRepresentedPayloads(bool compound, bool rebase)
    {
        using var fixture = new CapturedStairSweepFixture(compound, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            rebase ? new(1024, 2, -1024) : Vector3.Zero);
        Vector3 point = fixture.Rotate(new(0, 0.5f, -0.25f)) - fixture.World.Origin;
        Check(fixture.World, SupportFootprintTests.Request(point.X, point.Z, 0.15f, 0.70710677f,
            0.1 - fixture.World.Origin.Y, 0.9 - fixture.World.Origin.Y));
    }

    static void Check(BepuPhysicsWorld world, SupportFootprintRequest request, bool independentRoof = false)
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        IPhysicsSupportFootprintReuseSession? session = null;
        try
        {
            var members = new SupportFootprintMember[256];
            var joins = new ulong[1024];
            byte[] expected;
            SupportFootprintResult old;
            using (var lease = world.AcquireQueryReadLease())
            {
                old = world.QuerySupportFootprint(lease, request, members, joins);
                Assert.Equal(CapsuleFeatureStatus.Complete, old.Status);
                Assert.True(old.Members > 0);
                Assert.Equal(old.Members, members.Take(old.Members).Select(member => (member.Static, member.ElementId)).Distinct().Count());
                if (independentRoof)
                {
                    int roof = Array.FindIndex(members, 0, old.Members, member => member.Static.Value == 1);
                    Assert.True(roof >= 0);
                    for (int word = 0; word < old.JoinWordsPerRow; word++) Assert.Equal(0UL, joins[roof * old.JoinWordsPerRow + word]);
                }
                expected = SupportFootprintProofReuseFixture.SerializePayloadBits(old, members, joins);
                session = SupportFootprintProofReuseFixture.Open(world, lease);
                Assert.Equal(expected, Read(lease));
            }
            using (var lease = world.AcquireQueryReadLease())
            {
                Array.Clear(members);
                Array.Fill(joins, 73UL);
                Assert.Equal(expected, Read(lease));
                Assert.Throws<InvalidOperationException>(() => world.AssertFootprintCurrent(old, lease));
                Assert.Equal(1, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
                var before = SupportQueryDiagnostics.Snapshot();
                byte[] reference;
                using (SupportReferenceEvaluation.Begin()) reference = Read(lease);
                Assert.Equal(before.FootprintReuseHits, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
                Assert.Equal(before.FootprintReuseStores, SupportQueryDiagnostics.Snapshot().FootprintReuseStores);
                Assert.Equal(expected, reference);
                using (SupportReferenceEvaluation.Begin()) Assert.Equal(reference, Read(lease));
                Assert.Equal(expected, Read(lease));
            }
            session.Dispose();
            session = null;
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);

            byte[] Read(IPhysicsQueryLease lease)
            {
                var result = world.QuerySupportFootprint(lease, request, members, joins);
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
                world.AssertFootprintCurrent(result, lease);
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
    [InlineData("split-statics")]
    [InlineData("one-mesh")]
    [InlineData("strip")]
    [InlineData("gap-statics")]
    public void DirectOnlyEndpointContinuityIsRebuiltAfterTheHit(string partition)
    {
        using var scene = JoinedEdgeContinuityTests.Partition(partition);
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        IPhysicsSupportFootprintReuseSession? session = null;
        try
        {
            var world = (BepuPhysicsWorld)scene.World;
            long[] expected;
            SupportSample old;
            using (var lease = world.AcquireQueryReadLease())
            {
                expected = Cross(lease, out old);
                session = SupportFootprintProofReuseFixture.Open(world, lease);
                Assert.Equal(expected, Cross(lease, out _));
            }
            using (var lease = world.AcquireQueryReadLease()) Assert.Equal(expected, Cross(lease, out _));
            Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseHits > 0);
            Assert.Throws<InvalidOperationException>(() => old.Joined.Count);
        }
        finally
        {
            session?.Dispose();
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
            SupportQueryDiagnostics.Exchange(prior);
        }

        long[] Cross(IPhysicsQueryLease lease, out SupportSample start)
        {
            var tuning = MoveTuning.Default with { StepHeight = 0.4f };
            using var interval = new MemoInterval((BepuPhysicsWorld)scene.World, lease, tuning);
            Vector2 from = new(0.18f, -0.125f), to = new(0.18f, -0.025f);
            float height = (float)JoinedEdgeContinuityTests.Oracle(scene, partition, from).Height;
            float cosine = MathF.Cos(MathF.PI / 4);
            start = interval.Queries.FindSupport(new(from, height, 0.2f, 0.4f, 0.4f, cosine));
            var seat = GroundSeat.Resolve(interval.Queries, start, from, height, to, Vector2.UnitY, 0.2f, tuning, cosine);
            var bits = new List<long> { (int)start.Status, start.Static?.Value ?? -1, start.FeatureId,
                BitConverter.SingleToInt32Bits(start.Height), (int)seat.Outcome, BitConverter.SingleToInt32Bits(seat.FeetY),
                BitConverter.SingleToInt32Bits(seat.StepPart), BitConverter.SingleToInt32Bits(seat.DiscreteStep),
                seat.Support.Static?.Value ?? -1, seat.Support.FeatureId };
            Assert.Equal(SupportStatus.Walkable, start.Status);
            Assert.Equal(SeatOutcome.Seated, seat.Outcome);
            if (partition is "split-statics" or "one-mesh") Assert.Equal(0f, seat.StepPart);
            AddRow(start);
            AddRow(seat.Support);
            return [.. bits];

            void AddRow(SupportSample sample)
            {
                bits.Add(sample.Joined.Count);
                for (int i = 0; i < sample.Joined.Count; i++)
                {
                    bits.Add(sample.Joined[i].Static.Value);
                    bits.Add(sample.Joined[i].FeatureId);
                }
            }
        }
    }

    sealed class MemoInterval : IDisposable
    {
        readonly SupportRequestMemo _memo = new();
        internal ContactQueryContext Queries { get; }
        internal MemoInterval(BepuPhysicsWorld world, IPhysicsQueryLease lease, MoveTuning tuning)
        {
            _memo.Open(lease);
            Queries = new DryContactQueryContext(null, null, world, lease, tuning, null, _memo);
        }
        public void Dispose() => _memo.Close();
    }
}
