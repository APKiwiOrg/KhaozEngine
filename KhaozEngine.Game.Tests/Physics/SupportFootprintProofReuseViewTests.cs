using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportFootprintProofReuseViewTests
{
    [Fact]
    public void SelectedViewDeclaresExactFootprintReuseCapability()
    {
        using var fixture = new SupportFootprintProofReuseFixture(empty: true);
        StaticHandle excluded = fixture.World.AddStatic(new BoxShape(new(1, 0.1f, 1)), Pose.At(new(0, -0.1f, 0)));
        using var view = fixture.World.CreateQueryViewExcludingStatics([excluded]);
        Assert.Equal(0, excluded.Value);
        Assert.IsAssignableFrom<IPhysicsSupportFootprintReuseSource>(view);
    }

    [Fact]
    public void ChangedExclusionOrReceiverCannotReadAnotherViewsPayload()
    {
        using var fixture = new SupportFootprintProofReuseFixture(empty: true);
        var floor = fixture.World.AddStatic(new BoxShape(new(1, 0.1f, 1)), Pose.At(new(0, -0.1f, 0)));
        var roof = fixture.World.AddStatic(new BoxShape(new(1, 0.05f, 1)), Pose.At(new(0, 0.2f, 0)));
        StaticHandle[] exclusions = [floor];
        using var empty = fixture.World.CreateQueryViewExcludingStatics([]);
        using var selected = fixture.World.CreateQueryViewExcludingStatics(exclusions);
        exclusions[0] = roof;
        using var equalLooking = fixture.World.CreateQueryViewExcludingStatics([floor]);
        using var changed = fixture.World.CreateQueryViewExcludingStatics([roof]);
        using var foreign = new BepuPhysicsWorld(Vector3.Zero);
        using var foreignLease = foreign.AcquireQueryReadLease();
        IPhysicsWorld[] receivers = [fixture.World, empty, selected, equalLooking, changed];
        using var lease = fixture.World.AcquireQueryReadLease();
        byte[][] expected = receivers.Select(Read).ToArray();
        Assert.Equal(expected[0], expected[1]);
        Assert.Equal(expected[2], expected[3]);
        Assert.NotEqual(expected[0], expected[2]);
        for (int i = 0; i < receivers.Length; i++)
        {
            using var session = fixture.Open(lease, receivers[i]);
            Assert.Equal(expected[i], Read(receivers[i]));
            var before = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(expected[i], Read(receivers[i]));
            Assert.Equal(1, (SupportQueryDiagnostics.Snapshot() - before).FootprintReuseHits);
            for (int other = 0; other < receivers.Length; other++)
            {
                if (i == other) continue;
                before = SupportQueryDiagnostics.Snapshot();
                Assert.Equal(expected[other], Read(receivers[other]));
                Assert.Equal(0, (SupportQueryDiagnostics.Snapshot() - before).FootprintReuseHits);
            }
            Assert.Throws<ArgumentException>(() => session.AssertCurrent(foreignLease));
            Assert.Throws<ArgumentException>(() => ((IPhysicsSupportFootprintReuseSource)receivers[i])
                .TryOpenSupportFootprintReuse(foreignLease, out _));
            Assert.Throws<ArgumentException>(() => ((IPhysicsSupportFootprint)receivers[i])
                .QuerySupportFootprint(foreignLease, fixture.Request, fixture.Members, fixture.Joins));
        }
        using var selectedSession = fixture.Open(lease, selected);
        Read(selected);
        Assert.DoesNotContain(fixture.Members.Take(1), value => value.Static == floor);
        Assert.Contains(fixture.Members.Take(1), value => value.Static == roof);
        Assert.False(((IPhysicsSupportFootprintReuseSource)selected).TryOpenSupportFootprintReuse(lease, out var nested));
        Assert.Null(nested);
        using var independent = fixture.Open(lease, equalLooking);
        Read(equalLooking);
        var last = SupportQueryDiagnostics.Snapshot();
        Read(selected);
        Assert.Equal(1, (SupportQueryDiagnostics.Snapshot() - last).FootprintReuseHits);

        byte[] Read(IPhysicsWorld receiver)
        {
            var result = ((IPhysicsSupportFootprint)receiver).QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            ((IPhysicsSupportFootprint)receiver).AssertFootprintCurrent(result, lease);
            foreach (var other in receivers)
                if (!ReferenceEquals(other, receiver))
                    Assert.Throws<InvalidOperationException>(() => ((IPhysicsSupportFootprint)other).AssertFootprintCurrent(result, lease));
            return SupportFootprintProofReuseFixture.SerializePayloadBits(result, fixture.Members, fixture.Joins);
        }
    }

    [Fact]
    public void SelectedViewHitPublishesUnderItsNewLeaseOnly()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var view = fixture.World.CreateQueryViewExcludingStatics([]);
        var capability = (IPhysicsSupportFootprint)view;
        using var firstLease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        using var session = fixture.Open(firstLease, view);
        var first = capability.QuerySupportFootprint(firstLease, fixture.Request, fixture.Members, fixture.Joins);
        var expected = fixture.Bits(first);
        firstLease.Dispose();
        using var next = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        session.AssertCurrent(next);
        var result = capability.QuerySupportFootprint(next, fixture.Request, fixture.Members, fixture.Joins);
        Assert.Equal(expected, fixture.Bits(result));
        Assert.Same(view, result.QueryWorld);
        Assert.Same(fixture.World, result.SourceWorld);
        Assert.Same(next, result.Lease);
        capability.AssertFootprintCurrent(result, next);
        Assert.Throws<InvalidOperationException>(() => capability.AssertFootprintCurrent(first, next));
        Assert.Equal(1, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
    }

    [Fact]
    public void SelectedReferenceEvaluationNeitherReadsNorSeedsPayloads()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var view = fixture.World.CreateQueryViewExcludingStatics([]);
        using var lease = fixture.World.AcquireQueryReadLease();
        var capability = (IPhysicsSupportFootprint)view;
        var original = capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
        var expected = fixture.Bits(original);
        using var session = fixture.Open(lease, view);
        var before = SupportQueryDiagnostics.Snapshot();
        using (SupportReferenceEvaluation.Begin())
            Assert.Equal(expected, fixture.Bits(capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins)));
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, (SupportQueryDiagnostics.Snapshot() - before).FootprintReuseStores);
        before = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(expected, fixture.Bits(capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins)));
        var delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(0, delta.FootprintReuseHits);
        Assert.Equal(1, delta.Volumes);
        Assert.Equal(1, delta.FootprintReuseStores);
        before = SupportQueryDiagnostics.Snapshot();
        using (SupportReferenceEvaluation.Begin())
            Assert.Equal(expected, fixture.Bits(capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins)));
        delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(0, delta.FootprintReuseHits);
        Assert.Equal(1, delta.Volumes);
    }

    [Fact]
    public void ForwardedQueryConsumesJoinModeExactlyOnce()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        fixture.World.AddStatic(new BoxShape(new(0.08f, 0.1f, 0.08f)), Pose.At(new(0, 0.1f, 0)));
        using var view = fixture.World.CreateQueryViewExcludingStatics([]);
        using var lease = fixture.World.AcquireQueryReadLease();
        var capability = (IPhysicsSupportFootprint)view;
        byte[] full = Read(false), certification = Read(true);
        Assert.NotEqual(full, certification);
        using var session = fixture.Open(lease, view);
        Assert.Equal(certification, Read(true));
        Assert.Equal(full, Read(false));
        var forwarder = new ForwardingFootprint(capability);
        var before = SupportQueryDiagnostics.Snapshot();
        using (SupportCertificationJoins.Arm())
        {
            var result = forwarder.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
            Assert.Equal(full, SupportFootprintProofReuseFixture.SerializePayloadBits(result, fixture.Members, fixture.Joins));
            Assert.Equal(certification, forwarder.InsertedPayload);
        }
        Assert.Equal(2, (SupportQueryDiagnostics.Snapshot() - before).FootprintReuseHits);
        forwarder.ThrowInserted = true;
        before = SupportQueryDiagnostics.Snapshot();
        using (SupportCertificationJoins.Arm())
        {
            var result = forwarder.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
            Assert.Equal(full, SupportFootprintProofReuseFixture.SerializePayloadBits(result, fixture.Members, fixture.Joins));
        }
        Assert.Equal(1, (SupportQueryDiagnostics.Snapshot() - before).FootprintReuseHits);
        Assert.Equal(certification, Read(true));
        Assert.Equal(full, Read(false));

        byte[] Read(bool armed)
        {
            SupportFootprintResult result;
            if (armed)
            {
                using (SupportCertificationJoins.Arm())
                    result = capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
            }
            else result = capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            return SupportFootprintProofReuseFixture.SerializePayloadBits(result, fixture.Members, fixture.Joins);
        }
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("remove-reuse")]
    [InlineData("origin.x")]
    [InlineData("origin.y")]
    [InlineData("origin.z")]
    [InlineData("origin.signed-zero.x")]
    [InlineData("origin.signed-zero.y")]
    [InlineData("origin.signed-zero.z")]
    [InlineData("view-disposal")]
    [InlineData("source-disposal")]
    public void StaleRemovedReusedRebasedAndWrongThreadSessionsNeverPublish(string change)
    {
        using var fixture = new SupportFootprintProofReuseFixture(empty: true);
        var removed = fixture.World.AddStatic(new BoxShape(new(1, 0.1f, 1)), Pose.At(new(0, -0.1f, 0)));
        using var view = fixture.World.CreateQueryViewExcludingStatics([]);
        var capability = (IPhysicsSupportFootprint)view;
        using var lease = fixture.World.AcquireQueryReadLease();
        var session = fixture.Open(lease, view);
        var first = capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
        capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
        Assert.Equal(1, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
        fixture.FillSentinels();
        var members = (SupportFootprintMember[])fixture.Members.Clone();
        var joins = (ulong[])fixture.Joins.Clone();
        Exception? wrongThread = null;
        var thread = new Thread(() => wrongThread = Record.Exception(() =>
        {
            Assert.Throws<InvalidOperationException>(() => session.AssertCurrent(lease));
            Assert.Throws<InvalidOperationException>(() => session.ResetSource(fixture.Domain));
            Assert.Throws<InvalidOperationException>(() => session.Dispose());
            Assert.Throws<InvalidOperationException>(() => ((IPhysicsSupportFootprintReuseSource)view)
                .TryOpenSupportFootprintReuse(lease, out _));
            Assert.Throws<InvalidOperationException>(() => capability.QuerySupportFootprint(lease,
                fixture.Request, fixture.Members, fixture.Joins));
        }));
        thread.Start();
        thread.Join();
        Assert.Null(wrongThread);
        Assert.Equal(members, fixture.Members);
        Assert.Equal(joins, fixture.Joins);
        Assert.Equal(1, SupportQueryDiagnostics.Retention.Keys);
        session.AssertCurrent(lease);
        var afterWrongThread = SupportQueryDiagnostics.Snapshot();
        capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
        Assert.Equal(1, (SupportQueryDiagnostics.Snapshot() - afterWrongThread).FootprintReuseHits);
        fixture.FillSentinels();
        lease.Dispose();
        switch (change)
        {
            case "generation": fixture.World.AddStatic(new BoxShape(new(1)), Pose.At(new(10))); break;
            case "remove-reuse":
                var storage = SupportFootprintTests.HandlesOf(fixture.World)[removed.Value].Handle;
                fixture.World.RemoveStatic(removed);
                var replacement = fixture.World.AddStatic(new BoxShape(new(1, 0.1f, 1)), Pose.At(new(0, 0.025f, 0)));
                Assert.Equal(storage, SupportFootprintTests.HandlesOf(fixture.World)[replacement.Value].Handle);
                Assert.NotEqual(removed, replacement);
                break;
            case "origin.x": fixture.World.Rebase(new(MathF.BitIncrement(0), 0, 0)); break;
            case "origin.y": fixture.World.Rebase(new(0, MathF.BitIncrement(0), 0)); break;
            case "origin.z": fixture.World.Rebase(new(0, 0, MathF.BitIncrement(0))); break;
            case "origin.signed-zero.x": fixture.World.Rebase(new(-0.0f, 0, 0)); break;
            case "origin.signed-zero.y": fixture.World.Rebase(new(0, -0.0f, 0)); break;
            case "origin.signed-zero.z": fixture.World.Rebase(new(0, 0, -0.0f)); break;
            case "view-disposal": view.Dispose(); break;
            case "source-disposal": fixture.World.Dispose(); break;
        }
        var before = SupportQueryDiagnostics.Snapshot();
        if (change == "source-disposal")
        {
            Assert.Throws<ObjectDisposedException>(() => session.AssertCurrent(lease));
            Assert.Throws<ObjectDisposedException>(() => session.Dispose());
        }
        else
        {
            using var next = fixture.World.AcquireQueryReadLease();
            if (change == "view-disposal")
            {
                Assert.Throws<ObjectDisposedException>(() => session.AssertCurrent(next));
                Assert.Throws<ObjectDisposedException>(() => ((IPhysicsSupportFootprintReuseSource)view)
                    .TryOpenSupportFootprintReuse(next, out _));
                Assert.Throws<ObjectDisposedException>(() => capability.QuerySupportFootprint(next, fixture.Request,
                    fixture.Members, fixture.Joins));
                Assert.Equal(members, fixture.Members);
                Assert.Equal(joins, fixture.Joins);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => session.AssertCurrent(next));
                Assert.Throws<InvalidOperationException>(() => capability.AssertFootprintCurrent(first, next));
                var current = capability.QuerySupportFootprint(next, fixture.Request, fixture.Members, fixture.Joins);
                Assert.Equal(CapsuleFeatureStatus.Complete, current.Status);
                Assert.Equal(1, (SupportQueryDiagnostics.Snapshot() - before).Volumes);
                Assert.Throws<InvalidOperationException>(() => session.ResetSource(fixture.Domain));
            }
            Exception? disposal = Record.Exception(session.Dispose);
            if (change == "view-disposal") Assert.IsType<ObjectDisposedException>(disposal);
            else Assert.IsType<InvalidOperationException>(disposal);
        }
        var delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(0, delta.FootprintReuseHits);
        Assert.Equal(0, delta.FootprintReuseStores);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }

    sealed class ForwardingFootprint(IPhysicsSupportFootprint target) : IPhysicsSupportFootprint
    {
        internal bool ThrowInserted;
        internal byte[]? InsertedPayload;

        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
            Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default)
        {
            var insertedMembers = new SupportFootprintMember[256];
            var insertedJoins = new ulong[1024];
            if (ThrowInserted)
            {
                var captured = request;
                Assert.Throws<ArgumentException>(() => target.QuerySupportFootprint(lease, captured, insertedMembers,
                    insertedJoins, new(Layers: 1)));
            }
            else
            {
                var inserted = target.QuerySupportFootprint(lease, request, insertedMembers, insertedJoins);
                InsertedPayload = SupportFootprintProofReuseFixture.SerializePayloadBits(inserted, insertedMembers, insertedJoins);
            }
            return target.QuerySupportFootprint(lease, request, members, joins, filter);
        }

        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) =>
            target.AssertFootprintCurrent(result, lease);
    }
}
