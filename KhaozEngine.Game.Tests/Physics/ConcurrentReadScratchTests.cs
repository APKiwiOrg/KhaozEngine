using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>PB-1 scratch loans, scratch lifetime and installed support geometry publication.</summary>
public sealed class ConcurrentReadScratchTests
{
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);

    [Fact]
    public void AFootprintQueryFromADiagnosticObserverDuringAnOuterFootprintLeavesBothResultsCorrect()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new Vector3(2f, 1f, 2f)), Pose.At(new Vector3(0f, -1f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(0.25f, 0.05f, 0.25f)), Pose.At(new Vector3(0.6f, -0.02f, 0.3f)));
        var footprint = (IPhysicsSupportFootprint)world;
        SupportFootprintRequest outer = SupportFootprintTests.Request(), inner = SupportFootprintTests.Request(0.6f, 0.3f);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        Captured expectedOuter = Capture(footprint, lease, outer), expectedInner = Capture(footprint, lease, inner);
        Assert.NotEqual(expectedOuter.Text, expectedInner.Text);

        Captured? nested = null;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            // The outer collector has written its request and reset its storage when this phase begins.
            if (nested is not null || !entering || phase != ContactAllocationPhase.SupportColumnSetup) return;
            nested = default(Captured);
            nested = Capture(footprint, lease, inner);
        });
        Captured actualOuter;
        try { actualOuter = Capture(footprint, lease, outer); }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
        Assert.Equal(expectedInner.Text, nested!.Value.Text);
        Assert.Equal(expectedOuter.Text, actualOuter.Text);
    }

    [Fact]
    public void EveryConstructedScratchIsDisposedOnceWithItsNativePoolCleared()
    {
        var world = new BepuPhysicsWorld(Vector3.Zero);
        Vector3[] floor = [new(-4f, 0f, -4f), new(-4f, 0f, 4f), new(4f, 0f, 4f), new(4f, 0f, -4f)];
        world.AddStatic(new TriangleMeshShape(floor, [0, 2, 1, 0, 3, 2]), Pose.Identity);
        QueryScratchPool pool = ConcurrentReadGateTests.Gate(world).ScratchPool;

        // Success, including the mesh sweep and smoothed mesh contacts that allocate natively.
        Assert.True(world.SweepCapsule(Capsule, Pose.At(new Vector3(1f, 3f, 1f)), -Vector3.UnitY, 5f, out _));
        Assert.True(MeshContacts(world));

        // Two simultaneous readers each hold their own scratch.
        using (var barrier = new Barrier(2))
        {
            BoundedThread[] readers = [.. Enumerable.Range(0, 2).Select(_ => BoundedThread.Start(() =>
            {
                using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
                Assert.True(MeshContacts(world));
                BoundedThread.Arrive(barrier);
            }))];
            foreach (BoundedThread reader in readers) reader.Join();
        }

        // A query exception and a refused acquisition inside a live interval.
        using (IPhysicsQueryLease lease = world.AcquireQueryReadLease())
        {
            Assert.True(MeshContacts(world));
            Assert.Throws<ArgumentException>(() => world.GetDynamicPose(new DynamicBodyHandle(int.MaxValue)));
            Assert.Throws<InvalidOperationException>(() => world.AcquireQueryReadLease());
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                if (entering && phase == ContactAllocationPhase.SupportProposals)
                    throw new InvalidOperationException("observer failure");
            });
            try
            {
                Assert.Throws<InvalidOperationException>(() => ((IPhysicsSupportFootprint)world).QuerySupportFootprint(
                    lease, SupportFootprintTests.Request(), new SupportFootprintMember[256], new ulong[1024]));
            }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
        }

        QueryScratch[] constructed = pool.Constructed();
        Assert.True(constructed.Length >= 2, $"{constructed.Length} scratch constructed.");
        Assert.All(constructed, scratch => Assert.Equal(0, scratch.Disposals));
        Assert.Contains(constructed, scratch => scratch.NativeCapacity > 0);
        world.Dispose();
        Assert.All(constructed, scratch =>
        {
            Assert.Equal(1, scratch.Disposals);
            Assert.Equal(0, scratch.NativeCapacity);
        });
        Assert.Equal(constructed.Length, pool.Constructed().Length);
        Assert.Throws<ObjectDisposedException>(() => world.Raycast(Vector3.Zero, Vector3.UnitX, 1f, out _));
    }

    [Fact]
    public void TwoReadersMissingOneColdInstalledGeometryConstructItOnceAndWritesInvalidateIt()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle owner = world.AddStatic(new CompoundShape(
        [
            new CompoundChild(new BoxShape(new Vector3(1f, 0.5f, 2f)), Pose.At(new Vector3(-1f, 0f, 0f))),
            new CompoundChild(new BoxShape(new Vector3(1f, 0.5f, 2f)), Pose.At(new Vector3(1f, 0f, 0f))),
        ]), Pose.At(new Vector3(0f, -0.5f, 0f)));
        var cache = (InstalledSupportGeometryCache)typeof(BepuPhysicsWorld)
            .GetField("_installedSupportGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        SupportFootprintRequest request = SupportFootprintTests.Request(0.1f, 0.2f);

        var results = new string[2];
        using (var barrier = new Barrier(2))
        {
            BoundedThread[] readers = [.. Enumerable.Range(0, 2).Select(slot => BoundedThread.Start(() =>
            {
                bool arrived = false;
                ContactAllocationDiagnostics.SetObserver((phase, entering) =>
                {
                    // Both readers reach the cold lookup together.
                    if (arrived || !entering || phase != ContactAllocationPhase.SupportCapture) return;
                    arrived = true;
                    BoundedThread.Arrive(barrier);
                });
                try
                {
                    using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
                    results[slot] = Capture((IPhysicsSupportFootprint)world, lease, request).Text;
                }
                finally { ContactAllocationDiagnostics.SetObserver(null); }
            }))];
            foreach (BoundedThread reader in readers) reader.Join();
        }
        Assert.Equal(1, cache.Constructions);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(results[0], Query(world, request));
        Assert.Equal(1, cache.Constructions);

        StaticHandle other = world.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.At(new Vector3(9f, 0f, 9f)));
        Assert.Equal(results[0], Query(world, request));
        Assert.Equal(2, cache.Constructions);
        world.RemoveStatic(other);
        Assert.Equal(results[0], Query(world, request));
        Assert.Equal(3, cache.Constructions);

        // The replacement reuses the freed backend handle under a new seam handle and a different shape.
        world.RemoveStatic(owner);
        StaticHandle replacement = world.AddStatic(new CompoundShape(
        [
            new CompoundChild(new BoxShape(new Vector3(1f, 0.25f, 2f)), Pose.At(new Vector3(-1f, 0f, 0f))),
            new CompoundChild(new BoxShape(new Vector3(1f, 0.25f, 2f)), Pose.At(new Vector3(1f, 0f, 0f))),
        ]), Pose.At(new Vector3(0f, -0.25f, 0f)));
        string replaced = Query(world, request);
        Assert.Equal(4, cache.Constructions);
        Assert.NotEqual(results[0], replaced);
        Assert.Contains($"Static = {replacement}", replaced, StringComparison.Ordinal);
        Assert.DoesNotContain($"Static = {owner}", replaced, StringComparison.Ordinal);

        // A fresh world with the same seam numbering answers identically.
        using var fresh = new BepuPhysicsWorld(Vector3.Zero);
        fresh.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.At(new Vector3(9f, 0f, 9f)));
        fresh.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.At(new Vector3(12f, 0f, 12f)));
        fresh.AddStatic(new CompoundShape(
        [
            new CompoundChild(new BoxShape(new Vector3(1f, 0.25f, 2f)), Pose.At(new Vector3(-1f, 0f, 0f))),
            new CompoundChild(new BoxShape(new Vector3(1f, 0.25f, 2f)), Pose.At(new Vector3(1f, 0f, 0f))),
        ]), Pose.At(new Vector3(0f, -0.25f, 0f)));
        Assert.Equal(replaced, Query(fresh, request));
    }

    static bool MeshContacts(IPhysicsWorld world)
    {
        var destination = new CapsuleContact[64];
        CapsuleContactResult result = ((IPhysicsCapsuleContacts)world)
            .QueryCapsuleContacts(Capsule, Pose.At(new Vector3(0.02f, 0.751f, 0f)), 0.05f, destination);
        return result.Complete && result.Written > 0;
    }

    static string Query(BepuPhysicsWorld world, SupportFootprintRequest request)
    {
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        return Capture(world, lease, request).Text;
    }

    readonly record struct Captured(string Text);

    static Captured Capture(IPhysicsSupportFootprint footprint, IPhysicsQueryLease lease, SupportFootprintRequest request)
    {
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[1024];
        SupportFootprintResult result = footprint.QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.True(result.Members > 0);
        return new($"{result.Members} {result.Witnesses} {result.AxisVerdict} {result.FootVerdict} " +
            string.Join(";", members.Take(result.Members)) + " " +
            string.Join(",", joins.Take(result.Members * result.JoinWordsPerRow)));
    }
}
