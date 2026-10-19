using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void RepresentedRotatedTreadStillRejectsIndependentRoof()
    {
        using var scene = new SupportFootprintScene();
        Quaternion yaw = new(0, 0.70710677f, 0, 0.70710677f);
        scene.Install(new BoxShape(new(0.5f, 0.125f, 0.25f)), new(new(0, 0.125f, 0), yaw));
        scene.Install(new BoxShape(new(2, 0.125f, 2)), Pose.At(new(0, -0.625f, 0)));
        var query = new FootSupportQuery(new(-0.15f, 0), 0.25f, 0.15f, 0.4f, 0.4f, 0.66913056f);
        using (var clear = scene.World.AcquireQueryReadLease())
            Assert.Equal(SupportStatus.Walkable, FootSupport.Find(null, scene.World, clear, query).Status);
        // The roof's top is above the raised proposal start. Its raw InvalidStart must prevent lower support.
        StaticHandle roof = scene.Install(new BoxShape(new(0.5f, 0.125f, 0.5f)), Pose.At(new(0, 0.75f, 0)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, FootSupport.DescribeFootprint(query), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(SupportProposalStatus.InvalidStart, result.AxisVerdict.Status);
        Assert.Equal(SupportProposalStatus.InvalidStart, result.FootVerdict.Status);
        SupportSample roofSample = SupportFootprintEquivalence.AssertMatchesReference(() =>
            FootSupport.Find(null, scene.World, lease, query));
        Assert.Equal(SupportStatus.Refused, roofSample.Status);
        Assert.NotEqual(roof, roofSample.Static);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepresentedCoincidenceKeepsRawKeysAndIntervalAuthentication(bool compound)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Quaternion yaw = new(0, 0.70710677f, 0, 0.70710677f);
        var box = new BoxShape(new(2, 0.5f, 2));
        PhysicsShape shape = compound ? new CompoundShape([new(box, Pose.Identity), new(box, Pose.Identity)]) : box;
        StaticHandle first = world.AddStatic(shape, new(new(0, -0.5f, 0), yaw));
        StaticHandle second = compound ? first : world.AddStatic(box, new(new(0, -0.5f, 0), yaw));
        using var view = world.CreateQueryViewExcludingStatics([]);
        using var excluded = world.CreateQueryViewExcludingStatics([first, second]);
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, 0.70710677f);
        var scope = new SupportFootprintBounds(new(-10), new(10));
        var memo = new SupportRequestMemo();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        SupportSample original;
        long generation;
        IPhysicsQueryLease expired;
        var backendHandle = HandlesOf(world)[first.Value].Handle;
        try
        {
            using (var lease = world.AcquireQueryReadLease())
            {
                generation = lease.GeometryGeneration;
                expired = lease;
                memo.Open(lease);
                var members = new SupportFootprintMember[256];
                var joins = new ulong[1024];
                var result = Capability(world).QuerySupportFootprint(lease, FootSupport.DescribeFootprint(query), members, joins);
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
                var coincidentRawMembers = members.Take(result.Members).Where(m => m.HasDiscMaximum).ToList();
                Assert.Equal(2, coincidentRawMembers.Count);
                if (compound) Assert.NotEqual(coincidentRawMembers[0].ElementId, coincidentRawMembers[1].ElementId);
                else Assert.NotEqual(coincidentRawMembers[0].Static, coincidentRawMembers[1].Static);
                Assert.Equal(2, result.Members);
                Assert.Equal(2UL, joins[0]);
                Assert.Equal(1UL, joins[result.JoinWordsPerRow]);
                original = Read(world, lease, query, scope, null, false);
                Assert.Equal(SupportStatus.Walkable, original.Status);
                Assert.Equal(original, Read(world, lease, query, scope, null, true));
                Assert.Equal(1, original.Joined.Count);
                var neighbour = coincidentRawMembers.Single(m => m.Static != original.Static || m.ElementId != original.FeatureId);
                Assert.True(original.Joined.Contains(new(neighbour.Static, neighbour.ElementId)));
                Assert.True(SupportContinuity.IsContinuous(original, query.Axis, original, query.Axis, query.CosMaxSlope));
                SupportSample viewed = Read(view, lease, query, scope, null, false);
                var viewedSource = memo.Arena.Resolve(viewed.Continuity);
                Assert.Throws<InvalidOperationException>(() => Capability(world).AssertFootprintCurrent(viewedSource.Result, lease));
                Read(world, lease, query, scope with { Maximum = new(11) }, null, false);
                Read(world, lease, query, scope, new(-10, Vector3.UnitY), false);
                Read(world, lease, query with { Axis = new(-0f, 0) }, scope, null, false);
                Assert.Equal(SupportStatus.None, Read(excluded, lease, query, scope, null, false).Status);
                memo.Close();
                memo.Open(lease);
                SupportSample reacquired = Read(world, lease, query, scope, null, false);
                Assert.NotEqual(original.Continuity.Epoch, reacquired.Continuity.Epoch);
                Assert.Throws<InvalidOperationException>(() => memo.Arena.Resolve(original.Continuity));
                memo.Close();
            }
            memo.Open(expired);
            Assert.Throws<ObjectDisposedException>(() => FootSupport.Find(null, world, expired, query, scope, memo));
            memo.Close();
            using (var lease = world.AcquireQueryReadLease())
            {
                memo.Open(lease);
                Read(world, lease, query, scope, null, false);
                memo.Close();
            }
            world.RemoveStatic(first);
            StaticHandle replacement = world.AddStatic(shape, new(new(0, -0.5f, 0), yaw));
            Assert.Equal(backendHandle, HandlesOf(world)[replacement.Value].Handle);
            Assert.NotEqual(first, replacement);
            using (var lease = world.AcquireQueryReadLease())
            {
                Assert.NotEqual(generation, lease.GeometryGeneration);
                memo.Open(lease);
                var rebuilt = Read(world, lease, query, scope, null, false);
                Assert.Throws<InvalidOperationException>(() => memo.Arena.Resolve(original.Continuity));
                memo.Close();
            }
            world.Rebase(new(1, 0, 0));
            using (var lease = world.AcquireQueryReadLease())
            {
                memo.Open(lease);
                Read(world, lease, query, scope, null, false);
                memo.Close();
            }
        }
        finally { if (memo.Lease is not null) memo.Close(); SupportQueryDiagnostics.Exchange(prior); }

        SupportSample Read(IPhysicsWorld receiver, IPhysicsQueryLease lease, FootSupportQuery request,
            SupportFootprintBounds bounds, FootAnalyticGround? analytic, bool hit)
        {
            SupportQueryDiagnostics.Reset();
            var sample = FootSupport.Find(analytic, receiver, lease, request, bounds, memo);
            Assert.Equal(hit ? 1 : 0, SupportQueryDiagnostics.Snapshot().FindMemoHits);
            return sample;
        }
    }

    [Fact]
    public void RepresentedExactInstallsKeepBaselineBits()
    {
        new InstalledSupportPoseTests().ExactInstallControlsMatchBaseline();
        new ExactCurvedInstallControlTests().ExactCurvedInstallControlsMatchBaseline();
        foreach (Quaternion rotation in new[] { Quaternion.Identity, new(0, 1, 0, 0), new(0.5f, 0.5f, 0.5f, 0.5f) })
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            StaticHandle owner = world.AddStatic(new BoxShape(Vector3.One), new(new(0, -1, 0), rotation));
            var request = Request();
            using (var lease = world.AcquireQueryReadLease())
            {
                SupportFootprintEquivalence.AssertMatchesReference(Capability(world), lease, request, "cold");
                SupportFootprintEquivalence.AssertMatchesReference(Capability(world), lease, request, "warm");
                SupportFootprintEquivalence.AssertMatchesReference(Capability(world), lease, Request(x: 0.125f), "interleaved");
                SupportFootprintEquivalence.AssertMatchesReference(Capability(world), lease, request, "after interleave");
                var members = new SupportFootprintMember[256];
                var joins = new ulong[1024];
                var capability = Capability(world);
                capability.QuerySupportFootprint(lease, request, members, joins);
                long warmAllocatedBytes = AllocationMeasurement.Measure(nameof(RepresentedExactInstallsKeepBaselineBits),
                    () => capability.QuerySupportFootprint(lease, request, members, joins));
                Assert.Equal(0, warmAllocatedBytes);
            }
            world.RemoveStatic(owner);
            world.AddStatic(new BoxShape(Vector3.One), new(new(0, -1, 0), rotation));
            using var rebuilt = world.AcquireQueryReadLease();
            SupportFootprintEquivalence.AssertMatchesReference(Capability(world), rebuilt, request, "rebuilt");
        }
    }
}
