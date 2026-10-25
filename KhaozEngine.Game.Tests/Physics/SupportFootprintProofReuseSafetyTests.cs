using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportFootprintProofReuseSafetyTests(ITestOutputHelper output)
{
    [Fact]
    public void JoinWordBoundaryAndOwnedCapacityRespectPayloadBudget()
    {
        using var fixture = new SupportFootprintProofReuseFixture(empty: true);
        var plane = new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(0, 0, 2)], [0, 1, 2]);
        for (int i = 0; i < 65; i++)
            fixture.World.AddStatic(plane, Pose.Identity);
        var roof = fixture.World.AddStatic(plane, Pose.At(new(0, 0.25f, 0)));
        using var lease = fixture.World.AcquireQueryReadLease();
        var result = fixture.Query(lease);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(66, result.Members);
        Assert.Equal(66, fixture.Members.Take(result.Members).Select(member => (member.Static, member.ElementId)).Distinct().Count());
        Assert.Equal(2, result.JoinWordsPerRow);
        Assert.Equal(roof, fixture.Members[65].Static);
        Assert.NotEqual(0UL, fixture.Joins[63 * 2 + 1] & 1UL);
        Assert.NotEqual(0UL, fixture.Joins[64 * 2] & (1UL << 63));
        Assert.Equal(0UL, fixture.Joins[65 * 2]);
        Assert.Equal(0UL, fixture.Joins[65 * 2 + 1]);
        var expected = SupportFootprintProofReuseFixture.SerializePayloadBits(result, fixture.Members, fixture.Joins);
        var sourceMembers = (SupportFootprintMember[])fixture.Members.Clone();
        var sourceJoins = (ulong[])fixture.Joins.Clone();
        var key = SupportFootprintProofKey.Create(fixture.Request, default, false);
        var empty = SupportFootprintResult.Completed(fixture.World, lease, 0, 0, default, default);
        int tableBytes;
        using (var memo = new SupportFootprintProofMemo())
        {
            memo.Remember(key, empty, [], []);
            tableBytes = checked((int)SupportQueryDiagnostics.Retention.Bytes);
            Assert.InRange(tableBytes, 1, 2 * 1024 * 1024);
        }
        using (var memo = new SupportFootprintProofMemo(1))
        {
            memo.Remember(key, empty, [], []);
            Assert.Equal(tableBytes, 512 * SupportQueryDiagnostics.Retention.Bytes);
        }
        int payloadBytes = result.Members * Unsafe.SizeOf<SupportFootprintMember>() +
            result.Members * result.JoinWordsPerRow * sizeof(ulong);
        foreach (int budget in new[] { 0, tableBytes + payloadBytes - 1, tableBytes + payloadBytes, 2 * 1024 * 1024 })
        {
            using var memo = new SupportFootprintProofMemo(512, budget);
            var inputMembers = (SupportFootprintMember[])sourceMembers.Clone();
            var inputJoins = (ulong[])sourceJoins.Clone();
            for (int i = 0; i < 512; i++)
                memo.Remember(key with { AxisX = i }, result, inputMembers, inputJoins);
            Array.Clear(inputMembers);
            Array.Fill(inputJoins, ulong.MaxValue);
            int stored = Math.Clamp((budget - tableBytes) / payloadBytes, 0, 512);
            Assert.Equal(stored, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(stored == 0 ? 0 : tableBytes + stored * payloadBytes, SupportQueryDiagnostics.Retention.Bytes);
            Assert.InRange(SupportQueryDiagnostics.Retention.PeakKeys, 0, 512);
            Assert.InRange(SupportQueryDiagnostics.Retention.PeakBytes, 0, 2 * 1024 * 1024);
            fixture.FillSentinels();
            var tails = (SupportFootprintMember[])fixture.Members.Clone();
            var joinTails = (ulong[])fixture.Joins.Clone();
            bool hit = memo.TryCopy(fixture.World, lease, key, fixture.Members, fixture.Joins, out var copied);
            Assert.Equal(stored > 0, hit);
            if (!hit) copied = fixture.Query(lease);
            Assert.Equal(expected, SupportFootprintProofReuseFixture.SerializePayloadBits(copied, fixture.Members, fixture.Joins));
            Assert.Equal(tails.Skip(result.Members), fixture.Members.Skip(result.Members));
            Assert.Equal(joinTails.Skip(result.Members * result.JoinWordsPerRow), fixture.Joins.Skip(result.Members * result.JoinWordsPerRow));
            Assert.False(memo.TryCopy(fixture.World, lease, key with { AxisX = stored }, fixture.Members, fixture.Joins, out _));
            output.WriteLine($"budget={budget}, table={tableBytes}, payload={payloadBytes}, retained={SupportQueryDiagnostics.Retention}");
        }
        using (var memo = new SupportFootprintProofMemo())
        {
            for (int i = 0; i < 512; i++) memo.Remember(key with { AxisX = i }, empty, [], []);
            Assert.Equal(512, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(tableBytes, SupportQueryDiagnostics.Retention.Bytes);
            memo.Remember(key with { AxisX = 512 }, empty, [], []);
            Assert.False(memo.TryCopy(fixture.World, lease, key with { AxisX = 512 }, [], [], out _));
            Assert.True(memo.TryCopy(fixture.World, lease, key with { AxisX = 511 }, [], [], out _));
        }
        using (var memo = new SupportFootprintProofMemo(0))
        {
            memo.Remember(key, result, sourceMembers, sourceJoins);
            Assert.False(memo.TryCopy(fixture.World, lease, key, fixture.Members, fixture.Joins, out _));
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
        }
        Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseBudgetSkips > 0);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(513, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2 * 1024 * 1024 + 1)]
    public void InvalidMemoSizesReject(int capacity, int bytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupportFootprintProofMemo(capacity, bytes));

    [Theory]
    [InlineData("unsupported", CapsuleFeatureStatus.Unsupported)]
    [InlineData("ambiguous", CapsuleFeatureStatus.Ambiguous)]
    [InlineData("raw-capacity", CapsuleFeatureStatus.CapacityExceeded)]
    [InlineData("short-members", CapsuleFeatureStatus.CapacityExceeded)]
    [InlineData("short-joins", CapsuleFeatureStatus.Unresolved)]
    [InlineData("null-axis", CapsuleFeatureStatus.Unresolved)]
    [InlineData("null-foot", CapsuleFeatureStatus.Unresolved)]
    [InlineData("filter", CapsuleFeatureStatus.Unresolved)]
    [InlineData("work-limit", CapsuleFeatureStatus.Unsupported)]
    public void RefusedOrShortSpanQueryCannotSeedReuse(string scenario, CapsuleFeatureStatus status)
    {
        using var fixture = new SupportFootprintProofReuseFixture(empty: true);
        var request = fixture.Request;
        if (scenario == "ambiguous")
        {
            var owner = fixture.World.AddStatic(new ConvexHullShape([
                new(-1, -0.1f, -1), new(1, -0.1f, -1), new(-1, -0.1f, 1), new(1, -0.1f, 1),
                new(-1, 0, -1), new(1, 0, -1), new(-1, 0, 1), new(1, 0, 1)]), Pose.Identity);
            var simulation = SupportFootprintTests.SimulationOf(fixture.World);
            simulation.Statics.GetDescription(SupportFootprintTests.HandlesOf(fixture.World)[owner.Value].Handle, out var description);
            ref var hull = ref simulation.Shapes.GetShape<BepuPhysics.Collidables.ConvexHull>(description.Shape.Index);
            hull.GetVertexIndicesForFace(0, out var indices);
            indices[1] = indices[0];
        }
        else if (scenario == "work-limit")
        {
            fixture.World.AddStatic(new CylinderShape(0.3f, 2),
                new(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
            request = SupportFootprintTests.Request(z: 0.1f, lower: -2, upper: 2, cosine: 0.4f);
        }
        else
        {
            int count = scenario == "raw-capacity" ? 257 : 1;
            for (int i = 0; i < count; i++)
                fixture.World.AddStatic(new BoxShape(new(1, 0.1f, 1)), Pose.At(new(0, -0.1f, 0)));
        }
        using var lease = fixture.World.AcquireQueryReadLease();
        QueryScratch? scratch = null;
        if (scenario == "work-limit")
        {
            Assert.Equal(CapsuleFeatureStatus.Complete,
                fixture.World.QuerySupportFootprint(lease, request, fixture.Members, fixture.Joins).Status);
            scratch = Assert.Single(ConcurrentReadGateTests.Gate(fixture.World).ScratchPool.Constructed());
            scratch.Footprints.Idle = new SupportFootprintCollector(32);
        }
        if (scenario == "unsupported") request = request with { Radius = float.NaN };
        if (scenario == "null-axis") request = request with { AxisProposal = request.AxisProposal with { Probe = null! } };
        if (scenario == "null-foot") request = request with { FootProposal = request.FootProposal with { Probe = null! } };
        fixture.FillSentinels();
        var members = (SupportFootprintMember[])fixture.Members.Clone();
        var joins = (ulong[])fixture.Joins.Clone();
        SupportFootprintResult uncached = default;
        Exception? uncachedError = Record.Exception(() => uncached = Query());
        if (scenario.StartsWith("null-", StringComparison.Ordinal)) Assert.IsType<ArgumentNullException>(uncachedError);
        else if (scenario is "filter" or "short-joins") Assert.IsType<ArgumentException>(uncachedError);
        else { Assert.Null(uncachedError); Assert.Equal(status, uncached.Status); }
        Assert.Equal(members, fixture.Members);
        Assert.Equal(joins, fixture.Joins);
        using var session = fixture.Open(lease);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var before = SupportQueryDiagnostics.Snapshot();
            SupportFootprintResult actual = default;
            Exception? error = Record.Exception(() => actual = Query());
            var delta = SupportQueryDiagnostics.Snapshot() - before;
            Assert.Equal(0, delta.FootprintReuseHits);
            Assert.Equal(0, delta.FootprintReuseStores);
            Assert.Equal(uncachedError?.GetType(), error?.GetType());
            Assert.Equal(uncached.Status, actual.Status);
            Assert.Equal(uncached.RequiredElements, actual.RequiredElements);
            Assert.Equal(members, fixture.Members);
            Assert.Equal(joins, fixture.Joins);
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
        }
        if (scratch is not null)
        {
            Assert.Equal(SupportCurvedWorkFailure.WorkLimit, scratch.Footprints.Idle!.CurvedWork.Failure);
            Assert.Equal(32, scratch.Footprints.Idle.CurvedWork.Consumed);
            scratch.Footprints.Idle = new SupportFootprintCollector();
            var before = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(CapsuleFeatureStatus.Complete, Query().Status);
            var delta = SupportQueryDiagnostics.Snapshot() - before;
            Assert.Equal(0, delta.FootprintReuseHits);
            Assert.Equal(1, delta.Volumes);
            Assert.Equal(1, delta.FootprintReuseStores);
            fixture.FillSentinels();
            scratch.Footprints.Idle = new SupportFootprintCollector(32);
            before = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(CapsuleFeatureStatus.Unsupported, Query().Status);
            delta = SupportQueryDiagnostics.Snapshot() - before;
            Assert.Equal(0, delta.FootprintReuseHits);
            Assert.Equal(0, delta.FootprintReuseStores);
            Assert.Equal(1, delta.Volumes);
            Assert.Equal(members, fixture.Members);
            Assert.Equal(joins, fixture.Joins);
            scratch.Footprints.Idle = new SupportFootprintCollector();
        }
        if (scenario is "short-members" or "short-joins")
        {
            Assert.Equal(CapsuleFeatureStatus.Complete, fixture.Query(lease).Status);
            fixture.FillSentinels();
            var before = SupportQueryDiagnostics.Snapshot();
            SupportFootprintResult actual = default;
            Exception? error = Record.Exception(() => actual = Query());
            Assert.Equal(uncachedError?.GetType(), error?.GetType());
            Assert.Equal(uncached.Status, actual.Status);
            Assert.Equal(uncached.RequiredElements, actual.RequiredElements);
            Assert.Equal(0, (SupportQueryDiagnostics.Snapshot() - before).FootprintReuseHits);
            Assert.Equal(members, fixture.Members);
            Assert.Equal(joins, fixture.Joins);
        }

        SupportFootprintResult Query() => fixture.World.QuerySupportFootprint(lease, request,
            scenario == "short-members" ? fixture.Members.AsSpan(0, 0) : fixture.Members,
            scenario == "short-joins" ? fixture.Joins.AsSpan(0, 1) : fixture.Joins,
            scenario == "filter" ? new(Layers: 1) : default);
    }

    [Fact]
    public void MemoNeverRetainsAnyRefusalStatus()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        using var memo = new SupportFootprintProofMemo();
        var key = SupportFootprintProofKey.Create(fixture.Request, default, false);
        foreach (CapsuleFeatureStatus status in Enum.GetValues<CapsuleFeatureStatus>())
        {
            if (status == CapsuleFeatureStatus.Complete) continue;
            memo.Remember(key, SupportFootprintResult.Refused(status), fixture.Members, fixture.Joins);
            Assert.False(memo.TryCopy(fixture.World, lease, key, fixture.Members, fixture.Joins, out _));
        }
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }
}
