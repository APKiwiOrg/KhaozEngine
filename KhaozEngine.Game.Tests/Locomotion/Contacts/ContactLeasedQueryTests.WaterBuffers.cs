using System;
using System.Numerics;
using KhaozEngine.Tests.Physics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 6)]
    public void WaterBoundaryReusesIntervalBuffersAndClearsOnlyInitializedContacts(bool wet, int cleared)
    {
        // #1728. Multiple exact water certificates in one interval reuse bounded private storage.
        using var scene = new QueryScene(_ => 0);
        var body = new MovementBodyQuery(new(0, .751f, 0), .25f, .75f, Room, null);
        if (wet)
            scene.Environment.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
                Span<MovementDomainContact> contacts) =>
            {
                spans[0] = new(0, 1, 0, 1, false);
                contacts[0] = new(new("world", "lake"), Room, new(-2, 2, 2, true, "bed", "surface"),
                    Vector2.Zero, Vector3.UnitY, 0, "edge", 0,
                    MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);
                return new(MovementAvailability.Known, 1, 1, 1, 1, 0, scene.Lease.Identity);
            };
        Span<Vector3> normals = stackalloc Vector3[4];
        scene.Lease.BeginContactTick();
        MovementWaterBufferWork.Reset();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(MovementAvailability.Known, MovementWaterBoundary.Find(body, Vector3.UnitX,
                    .75f, .5f, WaterTraversalMode.DryOnly, scene.Lease, normals, out int count, out var hit));
                Assert.Equal(wet ? 1 : 0, count);
                Assert.Equal(wet, hit.Blocked);
            }
            Assert.Equal(0, MovementWaterBufferWork.Rents);
            Assert.Equal(cleared, MovementWaterBufferWork.ClearedReferences);
        }
        finally
        {
            MovementWaterBufferWork.Enabled = false;
            scene.Lease.EndContactTick();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompleteWriterLoansBulkClearDefaultEqualNumericResidue(bool boundary, bool diagnostics)
    {
        using var scene = new WaterBufferOrdinaryScene();
        // A preceding arbitrary writer can leave default-equal numeric bits in the retained lane.
        var arbitrary = scene.Lease.Workspace.WaterCoverage.Borrow(boundary);
        arbitrary.Contacts[^1] = MovementDomainContact.PartialFractionForTests(-0f);
        arbitrary.Return(MovementQueryLease.MaxDomainContacts);
        var loan = scene.Lease.BorrowWaterCoverage(boundary);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(loan.Contacts[^1].Fraction));
        // Deliberate fault sentinel distinguishes bulk zeroing from the default-equality scan.
        // Real complete writers never produce this partial record.
        loan.Contacts[^1] = MovementDomainContact.PartialFractionForTests(-0f);
        MovementWaterBufferWork.Reset();
        MovementWaterBufferWork.Enabled = diagnostics;
        try
        {
            loan.Return(MovementQueryLease.MaxDomainContacts);
            var reused = scene.Lease.BorrowWaterCoverage(boundary);
            try { Assert.Equal(0, BitConverter.SingleToInt32Bits(reused.Contacts[^1].Fraction)); }
            finally { reused.Return(MovementQueryLease.MaxDomainContacts); }
            Assert.Equal(0, MovementWaterBufferWork.ClearedReferences);
            Assert.Equal(0, MovementWaterBufferWork.Rents);
        }
        finally { MovementWaterBufferWork.Enabled = false; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArbitraryPartialWriterPreservesDefaultEqualSignedZero(bool throws)
    {
        using var scene = new QueryScene();
        var query = new MovementMediumSweepQuery(new(new(0, .751f, 0), .25f, .75f, Room, null), Vector3.Zero);
        int expectedBits = 0;
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            Assert.Equal(expectedBits, BitConverter.SingleToInt32Bits(contacts[^1].Fraction));
            contacts[^1] = MovementDomainContact.PartialFractionForTests(-0f);
            expectedBits = int.MinValue;
            if (throws) throw new InvalidOperationException("Partial callback failure.");
            return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, scene.Lease.Identity);
        };
        MovementWaterBufferWork.Reset();
        try
        {
            for (int i = 0; i < 3; i++)
                Assert.Equal(throws ? MovementAvailability.Stale : MovementAvailability.Unresolved,
                    scene.Lease.TraceWater(query, new MovementCoverageSpan[1],
                        new MovementDomainContact[MovementQueryLease.MaxDomainContacts]).Availability);
            Assert.Equal(0, MovementWaterBufferWork.ClearedReferences);
            Assert.Equal(0, MovementWaterBufferWork.Rents);
        }
        finally { MovementWaterBufferWork.Enabled = false; }
    }

    [Theory]
    [InlineData("known", MovementAvailability.Known)]
    [InlineData("refuse", MovementAvailability.Unresolved)]
    [InlineData("throw", MovementAvailability.Stale)]
    public void ProducerCleanupIncludesDirtyTailAfterPartialWrite(string outcome, MovementAvailability expected)
    {
        using var scene = new QueryScene();
        var query = new MovementMediumSweepQuery(new(new(0, .751f, 0), .25f, .75f, Room, null), Vector3.Zero);
        var spanSentinel = new MovementCoverageSpan(0, .5f, 0, 0, true);
        var callerSpans = new[] { spanSentinel };
        var callerContacts = new MovementDomainContact[MovementQueryLease.MaxDomainContacts];
        Array.Fill(callerContacts, WaterBufferContact());
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            Assert.All(contacts.ToArray(), c => Assert.Equal(default, c));
            contacts[0] = WaterBufferContact();
            contacts[^1] = WaterBufferContact();
            spans[0] = new(0, 1, 0, 0, true);
            if (outcome == "throw") throw new InvalidOperationException("Partial callback failure.");
            if (outcome == "refuse") return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, scene.Lease.Identity);
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, 0, scene.Lease.Identity);
        };
        MovementWaterBufferWork.Reset();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(expected, scene.Lease.TraceWater(query, callerSpans, callerContacts).Availability);
                Assert.Equal(outcome == "known" ? new MovementCoverageSpan(0, 1, 0, 0, true) : spanSentinel, callerSpans[0]);
                Assert.All(callerContacts, c => Assert.Equal(WaterBufferContact(), c));
            }
            Assert.Equal(6, MovementWaterBufferWork.ClearedReferences);
            Assert.Equal(0, MovementWaterBufferWork.Rents);
        }
        finally { MovementWaterBufferWork.Enabled = false; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedLoansKeepBothLanesIsolatedAndCountEveryDirtyEntry(bool ordinary, bool boundary)
    {
        using var custom = ordinary ? null : new QueryScene();
        using var engine = ordinary ? new WaterBufferOrdinaryScene() : null;
        var lease = engine?.Lease ?? custom!.Lease;
        MovementWaterBufferWork.Reset();
        try
        {
            var outer = lease.BorrowWaterCoverage(boundary);
            outer.Contacts[0] = WaterBufferContact();
            outer.Contacts[^1] = WaterBufferContact();
            var otherLane = lease.BorrowWaterCoverage(!boundary);
            Assert.Equal(default, otherLane.Contacts[0]);
            var inner = lease.BorrowWaterCoverage(boundary);
            Assert.All(inner.Contacts.ToArray(), c => Assert.Equal(default, c));
            inner.Contacts[^1] = WaterBufferContact();
            var nested = lease.BorrowWaterCoverage(boundary);
            Assert.All(nested.Contacts.ToArray(), c => Assert.Equal(default, c));
            nested.Contacts[0] = WaterBufferContact();
            nested.Return(MovementQueryLease.MaxDomainContacts);
            Assert.Equal(WaterBufferContact(), inner.Contacts[^1]);
            inner.Return(MovementQueryLease.MaxDomainContacts);
            Assert.Equal(WaterBufferContact(), outer.Contacts[0]);
            Assert.Equal(WaterBufferContact(), outer.Contacts[^1]);
            otherLane.Return(0);
            outer.Return(MovementQueryLease.MaxDomainContacts);
            var reused = lease.BorrowWaterCoverage(boundary);
            Assert.All(reused.Contacts.ToArray(), c => Assert.Equal(default, c));
            reused.Return(MovementQueryLease.MaxDomainContacts);
            Assert.Equal(4, MovementWaterBufferWork.ClearedReferences);
            Assert.Equal(4, MovementWaterBufferWork.Rents);
        }
        finally { MovementWaterBufferWork.Enabled = false; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ForeignThreadCannotClearOrReleaseWaterLoan(bool ordinary, bool boundary)
    {
        using var custom = ordinary ? null : new QueryScene();
        using var engine = ordinary ? new WaterBufferOrdinaryScene() : null;
        var lease = engine?.Lease ?? custom!.Lease;
        var loan = lease.BorrowWaterCoverage(boundary);
        loan.Contacts[0] = WaterBufferContact();
        var thread = BoundedThread.Start(() => loan.Return(1));
        Assert.IsType<InvalidOperationException>(thread.JoinFailure());
        Assert.Equal(WaterBufferContact(), loan.Contacts[0]);
        MovementWaterBufferWork.Reset();
        try
        {
            var nested = lease.BorrowWaterCoverage(boundary);
            nested.Return(0);
            Assert.Equal(2, MovementWaterBufferWork.Rents);
            loan.Return(1);
            Assert.Equal(1, MovementWaterBufferWork.ClearedReferences);
        }
        finally { MovementWaterBufferWork.Enabled = false; }
    }

    [Fact]
    public void ReentrantProducerCallbackCannotAliasItsOuterScratch()
    {
        using var scene = new QueryScene();
        var body = new MovementBodyQuery(new(0, .751f, 0), .25f, .75f, Room, null);
        bool reentrant = false;
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            Assert.All(contacts.ToArray(), c => Assert.Equal(default, c));
            contacts[^1] = WaterBufferContact();
            if (!reentrant)
            {
                reentrant = true;
                try
                {
                    Assert.Equal(MovementAvailability.Known, MovementWaterBoundary.Find(body, Vector3.Zero,
                        .75f, .5f, WaterTraversalMode.DryOnly, scene.Lease, new Vector3[4], out int count, out var hit));
                    Assert.Equal(0, count);
                    Assert.False(hit.Blocked);
                }
                finally { reentrant = false; }
                Assert.Equal(WaterBufferContact(), contacts[^1]);
            }
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, 0, scene.Lease.Identity);
        };
        MovementWaterBufferWork.Reset();
        try
        {
            Assert.Equal(MovementAvailability.Known, scene.Lease.TraceWater(new(body, Vector3.Zero),
                new MovementCoverageSpan[1], new MovementDomainContact[MovementQueryLease.MaxDomainContacts]).Availability);
            Assert.Equal(2, MovementWaterBufferWork.ClearedReferences);
            Assert.Equal(2, MovementWaterBufferWork.Rents);
        }
        finally { MovementWaterBufferWork.Enabled = false; }
    }

    static MovementDomainContact WaterBufferContact() => new(new("world", "lake"), Room,
        new(-2, 2, 2, true, "bed", "surface"), Vector2.Zero, Vector3.UnitY, 0, "edge", 0,
        MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);

}
