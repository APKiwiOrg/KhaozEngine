using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportFootprintProofReuseTests(ITestOutputHelper output)
{
    [Fact]
    public void WorldDeclaresExactFootprintReuseCapability()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Assert.Contains(world.GetType().GetInterfaces(),
            type => type.FullName == "KhaozEngine.Physics.IPhysicsSupportFootprintReuseSource");
    }

    [Fact]
    public void IdenticalCrossLeaseQueryPublishesFreshCompleteWithOneEvaluation()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        var world = fixture.World;
        var leaseA = world.AcquireQueryReadLease();
        using var session = fixture.Open(leaseA);
        var first = fixture.Query(leaseA);
        Assert.Equal(CapsuleFeatureStatus.Complete, first.Status);
        Assert.True(first.Members > 0);
        long[] firstPayloadBits = fixture.Bits(first);
        leaseA.Dispose();
        using var leaseB = world.AcquireQueryReadLease();
        session.AssertCurrent(leaseB);
        Array.Fill(fixture.Joins, 73UL);
        var sentinel = new SupportFootprintMember(default, default, 12345, default, 0, default, 0, 0, false, 0, 0, 0, 0, default, default);
        Array.Fill(fixture.Members, sentinel);
        var second = fixture.Query(leaseB);
        Assert.Equal(CapsuleFeatureStatus.Complete, second.Status);
        Assert.Same(leaseB, second.Lease);
        Assert.Same(world, second.QueryWorld);
        Assert.Same(world, second.SourceWorld);
        Assert.Equal(firstPayloadBits, fixture.Bits(second));
        world.AssertFootprintCurrent(second, leaseB);
        Assert.Throws<InvalidOperationException>(() => world.AssertFootprintCurrent(first, leaseB));
        Assert.All(fixture.Members.Skip(second.Members), value => Assert.Equal(sentinel, value));
        Assert.All(fixture.Joins.Skip(second.Members * second.JoinWordsPerRow), value => Assert.Equal(73UL, value));
        SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(1, counts.Volumes);
        Assert.Equal(2, counts.Proposals);
        Assert.Equal(1, counts.FootprintReuseHits);
        Assert.Equal(1, counts.FootprintReuseMisses);
        Assert.Equal(1, counts.FootprintReuseStores);
        Assert.Equal(0, counts.EdgeSteps);
        Assert.Equal(0, counts.InitialHoldReuseHits);
        output.WriteLine($"Volumes={counts.Volumes}, Proposals={counts.Proposals}, Hits={counts.FootprintReuseHits}, " +
            $"Misses={counts.FootprintReuseMisses}, Stores={counts.FootprintReuseStores}, Retention={SupportQueryDiagnostics.Retention}");
    }

    [Fact]
    public void CompleteEmptyPayloadIsReusable()
    {
        using var fixture = new SupportFootprintProofReuseFixture(empty: true);
        using var lease = fixture.World.AcquireQueryReadLease();
        using var session = fixture.Open(lease);
        var first = fixture.Query(lease);
        var second = fixture.Query(lease);
        Assert.Equal(CapsuleFeatureStatus.Complete, second.Status);
        Assert.Equal(0, second.Members);
        Assert.Equal(fixture.Bits(first), fixture.Bits(second));
        Assert.Equal(1, SupportQueryDiagnostics.Snapshot().Volumes);
        Assert.Equal(1, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
    }

    [Fact]
    public void ResetAndDisposeReleaseOwnedPayload()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        var session = fixture.Open(lease);
        try
        {
            fixture.Query(lease);
            var retained = SupportQueryDiagnostics.Retention;
            Assert.Equal(1, retained.Keys);
            Assert.InRange(retained.Bytes, 1, 2 * 1024 * 1024);
            session.ResetSource(fixture.Domain);
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
            fixture.Query(lease);
            Assert.Equal(2, SupportQueryDiagnostics.Snapshot().Volumes);
            Assert.False(((IPhysicsSupportFootprintReuseSource)fixture.World).TryOpenSupportFootprintReuse(lease, out var nested));
            Assert.Null(nested);
        }
        finally { session.Dispose(); }
        var afterDispose = SupportQueryDiagnostics.Retention;
        Assert.Equal(0, afterDispose.Keys);
        Assert.Equal(0, afterDispose.Bytes);
        using var replacement = fixture.Open(lease);
    }

    [Fact]
    public void ReferenceQueryNeitherReadsNorSeedsReuse()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        using var session = fixture.Open(lease);
        var first = fixture.Query(lease);
        var expected = fixture.Bits(first);
        var before = SupportQueryDiagnostics.Snapshot();
        using (SupportReferenceEvaluation.Begin())
        {
            var reference = fixture.Query(lease);
            Assert.Equal(expected, fixture.Bits(reference));
        }
        var delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(1, delta.Volumes);
        Assert.Equal(0, delta.FootprintReuseHits);
        Assert.Equal(0, delta.FootprintReuseStores);
        session.ResetSource(fixture.Domain);
        using (SupportReferenceEvaluation.Begin()) fixture.Query(lease);
        before = SupportQueryDiagnostics.Snapshot();
        fixture.Query(lease);
        delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(1, delta.Volumes);
        Assert.Equal(1, delta.FootprintReuseMisses);
        Assert.Equal(1, delta.FootprintReuseStores);
        Assert.Equal(0, delta.FootprintReuseHits);
    }

    [Fact]
    public void InvalidAndUndersizedQueriesPreserveSentinels()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        using var session = fixture.Open(lease);
        var complete = fixture.Query(lease);
        Assert.Equal(CapsuleFeatureStatus.Complete, complete.Status);
        Array.Fill(fixture.Members, fixture.Members[0] with { ElementId = int.MinValue, ProposalMask = byte.MaxValue });
        var original = (SupportFootprintMember[])fixture.Members.Clone();
        Array.Fill(fixture.Joins, 73UL);
        var invalid = fixture.Request with { Radius = float.NaN };
        var refused = fixture.World.QuerySupportFootprint(lease, invalid, fixture.Members, fixture.Joins);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, refused.Status);
        Assert.Equal(original, fixture.Members);
        Assert.All(fixture.Joins, value => Assert.Equal(73UL, value));
        refused = fixture.World.QuerySupportFootprint(lease, fixture.Request,
            fixture.Members.AsSpan(0, complete.Members - 1), fixture.Joins);
        Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, refused.Status);
        Assert.Equal(complete.Members, refused.RequiredElements);
        Assert.Equal(original, fixture.Members);
        Assert.All(fixture.Joins, value => Assert.Equal(73UL, value));
        Assert.Throws<ArgumentException>(() => fixture.World.QuerySupportFootprint(lease, fixture.Request,
            fixture.Members, fixture.Joins.AsSpan(0, 1)));
        Assert.Equal(original, fixture.Members);
        Assert.All(fixture.Joins, value => Assert.Equal(73UL, value));
    }

    [Fact]
    public void WorkerProofReuseCountersRestoreAndMerge()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        foreach (SupportQueryCounter counter in Enum.GetValues<SupportQueryCounter>()) SupportQueryDiagnostics.Record(counter, 2);
        var priorCounts = SupportQueryDiagnostics.Snapshot();
        var priorRetention = SupportQueryDiagnostics.Retention;
        var scope = WorkerDiagnosticsScope.Enter(new WorkerDiagnosticsPlan(true, null), null);
        using (var lease = fixture.World.AcquireQueryReadLease())
        using (var session = fixture.Open(lease))
        {
            fixture.Query(lease);
            fixture.Query(lease);
            SupportQueryDiagnostics.Record(SupportQueryCounter.EdgeSteps, 3);
            SupportQueryDiagnostics.Record(SupportQueryCounter.InitialHoldReuseHits, 4);
            SupportQueryDiagnostics.Record(SupportQueryCounter.FootprintReuseBudgetSkips, 5);
        }
        var record = scope.Exit();
        Assert.Equal(priorCounts, SupportQueryDiagnostics.Snapshot());
        Assert.Equal(priorRetention, SupportQueryDiagnostics.Retention);
        Assert.Equal(0, record.Retention.Keys);
        Assert.Equal(0, record.Retention.Bytes);
        Assert.Equal(1, record.Retention.PeakKeys);
        Assert.True(record.Retention.PeakBytes > 0);
        WorkerDiagnostics.Merge([record, record]);
        var delta = SupportQueryDiagnostics.Snapshot() - priorCounts;
        Assert.Equal(2, delta.FootprintReuseHits);
        Assert.Equal(2, delta.FootprintReuseMisses);
        Assert.Equal(2, delta.FootprintReuseStores);
        Assert.Equal(10, delta.FootprintReuseBudgetSkips);
        Assert.Equal(6, delta.EdgeSteps);
        Assert.Equal(8, delta.InitialHoldReuseHits);
        Assert.Equal(record.Retention.PeakBytes, SupportQueryDiagnostics.Retention.PeakBytes);
        Assert.Equal(record.Retention.PeakKeys, SupportQueryDiagnostics.Retention.PeakKeys);
    }

    [Theory]
    [InlineData(0, 2 * 1024 * 1024)]
    [InlineData(512, 0)]
    [InlineData(1, 2 * 1024 * 1024)]
    public void BoundedMemoDeclinesWithoutChangingPayload(int capacity, int budget)
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        var result = fixture.Query(lease);
        var expected = fixture.Bits(result);
        var firstKey = SupportFootprintProofKey.Create(fixture.Request, default, false);
        var other = fixture.Request with { Axis = new(0.125f, 0) };
        var secondKey = SupportFootprintProofKey.Create(other, default, false);
        using var memo = new SupportFootprintProofMemo(capacity, budget);
        memo.Remember(firstKey, result, fixture.Members, fixture.Joins);
        memo.Remember(secondKey, result, fixture.Members, fixture.Joins);
        Assert.InRange(SupportQueryDiagnostics.Retention.Keys, 0, capacity);
        Assert.InRange(SupportQueryDiagnostics.Retention.Bytes, 0, budget);
        Assert.False(memo.TryCopy(fixture.World, lease, secondKey, fixture.Members, fixture.Joins, out _));
        Assert.Equal(capacity > 0 && budget > 0,
            memo.TryCopy(fixture.World, lease, firstKey, fixture.Members, fixture.Joins, out var copied));
        if (copied.Status == CapsuleFeatureStatus.Complete) Assert.Equal(expected, fixture.Bits(copied));
        Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseBudgetSkips > 0);
        memo.Reset();
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }

    [Fact]
    public void DomainUsesReferenceIdentityAndRejectsNullTokens()
    {
        var profile = new string('p', 1);
        var source = new string('s', 1);
        var domain = new SupportFootprintReuseDomain(profile, source);
        Assert.Equal(domain, new SupportFootprintReuseDomain(profile, source));
        Assert.NotEqual(domain, new SupportFootprintReuseDomain(new string('p', 1), source));
        Assert.NotEqual(domain, new SupportFootprintReuseDomain(profile, new string('s', 1)));
        Assert.Throws<ArgumentNullException>(() => new SupportFootprintReuseDomain(null!, source));
        Assert.Throws<ArgumentNullException>(() => new SupportFootprintReuseDomain(profile, null!));
    }

    [Fact]
    public void RetentionSurvivesCounterEnablementChanges()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        var session = fixture.Open(lease);
        try
        {
            SupportQueryDiagnostics.Enable(false);
            fixture.Query(lease);
            SupportQueryDiagnostics.Enable(true);
            Assert.Equal(1, SupportQueryDiagnostics.Retention.Keys);
            Assert.True(SupportQueryDiagnostics.Retention.Bytes > 0);
            SupportQueryDiagnostics.Enable(false);
        }
        finally
        {
            session.Dispose();
            SupportQueryDiagnostics.Enable(true);
        }
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }

    [Fact]
    public void NonDefaultCurvedWorkPolicyNeitherReadsNorSeedsReuse()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        using var session = fixture.Open(lease);
        Assert.Equal(CapsuleFeatureStatus.Complete, fixture.Query(lease).Status);
        var scratch = Assert.Single(ConcurrentReadGateTests.Gate(fixture.World).ScratchPool.Constructed());
        scratch.Footprints.Idle = new SupportFootprintCollector(32);
        var before = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(CapsuleFeatureStatus.Complete, fixture.Query(lease).Status);
        var delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(1, delta.Volumes);
        Assert.Equal(0, delta.FootprintReuseHits);
        Assert.Equal(0, delta.FootprintReuseStores);
        session.ResetSource(fixture.Domain);
        fixture.Query(lease);
        scratch.Footprints.Idle = new SupportFootprintCollector();
        before = SupportQueryDiagnostics.Snapshot();
        fixture.Query(lease);
        delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(1, delta.FootprintReuseMisses);
        Assert.Equal(1, delta.FootprintReuseStores);
        Assert.Equal(0, delta.FootprintReuseHits);
    }

    [Fact]
    public void ResetCountersKeepsPerSessionRetentionPeaks()
    {
        using var first = new SupportFootprintProofReuseFixture();
        using var second = new SupportFootprintProofReuseFixture();
        using var firstLease = first.World.AcquireQueryReadLease();
        using var secondLease = second.World.AcquireQueryReadLease();
        using var firstSession = first.Open(firstLease);
        using var secondSession = second.Open(secondLease);
        first.Query(firstLease);
        second.Query(secondLease);
        var retained = SupportQueryDiagnostics.Retention;
        Assert.Equal(2, retained.Keys);
        Assert.Equal(1, retained.PeakKeys);
        Assert.Equal(2 * retained.PeakBytes, retained.Bytes);
        SupportQueryDiagnostics.Reset();
        Assert.Equal(retained, SupportQueryDiagnostics.Retention);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetOrDisposeDuringEvaluationDoesNotSeedOrChangeTheFullResult(bool dispose)
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        var session = fixture.Open(lease);
        try
        {
            SupportQueryDiagnostics.SetCurvedWorkObserver(_ =>
            {
                SupportQueryDiagnostics.SetCurvedWorkObserver(null);
                if (dispose) session.Dispose();
                else session.ResetSource(fixture.Domain);
            });
            var first = fixture.Query(lease);
            Assert.Equal(CapsuleFeatureStatus.Complete, first.Status);
            var expected = fixture.Bits(first);
            Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
            if (dispose) session = fixture.Open(lease);
            var second = fixture.Query(lease);
            Assert.Equal(expected, fixture.Bits(second));
            var counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(2, counts.Volumes);
            Assert.Equal(0, counts.FootprintReuseHits);
            Assert.Equal(1, counts.FootprintReuseStores);
        }
        finally
        {
            SupportQueryDiagnostics.SetCurvedWorkObserver(null);
            session.Dispose();
        }
    }
}
