using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void RemovedAndReusedBackendStorageCannotReviveOldGeometry()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var removed = world.AddStatic(new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(0, 0, 2)], [0, 1, 2]), Pose.Identity);
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var capability = Capability(world);
        SupportFootprintResult first;
        using (var lease = world.AcquireQueryReadLease())
        {
            first = capability.QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, first.Status);
            Assert.Equal(removed, members[0].Static);
            Assert.Equal(0, members[0].DiscHeightUpper);
        }
        world.RemoveStatic(removed);
        var replacement = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -0.875f, 0)));
        using var selected = world.CreateQueryViewExcludingStatics([replacement]);
        using var next = world.AcquireQueryReadLease();
        Assert.True(next.GeometryGeneration > first.GeometryGeneration);
        Assert.Throws<InvalidOperationException>(() => capability.AssertFootprintCurrent(first, next));
        var second = capability.QuerySupportFootprint(next, Request(), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, second.Status);
        Assert.Equal(1, second.Members);
        Assert.Equal(replacement, members[0].Static);
        Assert.Equal(0.125, members[0].DiscHeightLower);
        Assert.Equal(0.125, members[0].DiscHeightUpper);
        Assert.Equal(0, Capability(selected).QuerySupportFootprint(next, Request(), members, joins).Members);
    }

    [Fact]
    public void WrongThreadAndDisposedViewRefuseCurrentChecksBeforeBackendWork()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        using var lease = world.AcquireQueryReadLease();
        var capability = Capability(view);
        var result = capability.QuerySupportFootprint(lease, Request(), [], []);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { capability.AssertFootprintCurrent(result, lease); }
            catch (Exception failure) { error = failure; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        lease.Dispose();
        view.Dispose();
        using var next = world.AcquireQueryReadLease();
        Assert.Throws<ObjectDisposedException>(() => capability.AssertFootprintCurrent(result, next));
        var foreign = new ForeignFootprintLease();
        Assert.Throws<ArgumentException>(() => capability.AssertFootprintCurrent(result, foreign));
        Assert.Equal(0, foreign.Callbacks);
    }

    [Fact]
    public void AboveColumnOverhangDoesNotEnterTheMemberOrContactUnion()
    {
        using var scene = new SupportFootprintScene();
        var floor = scene.Install(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        var roof = scene.Install(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, 1.2f, 0)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
        CheckQueryOracle(scene, Request(), result, members, joins);
        Assert.All(members.Take(result.Members), member => Assert.Equal(floor, member.Static));
        Assert.DoesNotContain(members.Take(result.Members), member => member.Static == roof);
        Assert.Equal(0, result.FootVerdict.LowestUpper);
    }
}
