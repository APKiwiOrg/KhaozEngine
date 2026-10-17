using System;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void ConcurrentWarmMeshFindsTakeNoCacheLock()
    {
        const int readers = 3;
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat mesh"));
        scene.Checked("mesh warm before concurrent readers", out _);
        InstalledSupportMeshWorldCache cache = MeshForms(scene.World);
        long before = cache.LockAcquisitions;
        using var barrier = new Barrier(readers);
        BoundedThread[] threads = [.. Enumerable.Range(0, readers).Select(_ => BoundedThread.Start(() =>
        {
            using var counters = new WorldFormCounters();
            using IPhysicsQueryLease lease = scene.AcquireLease();
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                if (!entering || phase != ContactAllocationPhase.SupportCapture) return;
                ContactAllocationDiagnostics.SetObserver(null);
                BoundedThread.Arrive(barrier);
            });
            try
            {
                SupportFootprintEquivalence.AssertMatchesReference(() =>
                {
                    SupportQueryDiagnostics.Reset();
                    SupportSample result = scene.Find(lease);
                    Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
                    Assert.Equal(SupportReferenceEvaluation.Active ? 2 : 0,
                        SupportQueryDiagnostics.Snapshot().MeshTriangleCaptures);
                    return result;
                });
            }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
        }))];
        JoinMeshReaders(threads);
        Assert.Equal(before, cache.LockAcquisitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeshEvictionLeavesFacesHeldByConcurrentFindsValid(bool evictStatic)
    {
        const int readers = 3;
        int patches = evictStatic ? 1 : InstalledSupportMeshWorldCache.PerStaticTriangleCapacity / 2 + 1;
        var shape = MeshConcurrencyPatches(patches);
        var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle owner = world.AddStatic(shape, Pose.Identity);
        int statics = evictStatic ? InstalledSupportMeshWorldCache.StaticCapacity : 0;
        for (int i = 1; i <= statics; i++) world.AddStatic(shape, Pose.At(new(0, 0, i * 4)));
        using var scene = new WorldFormScene(world, CostRows().Single(row => row.Name == "flat mesh").Query);
        scene.Checked("mesh before eviction", out _);
        InstalledSupportMeshWorldCache cache = MeshForms(world);
        object first = MeshForm(cache, owner, 0);
        using var captured = new Barrier(readers + 1);
        using var resume = new ManualResetEventSlim();
        BoundedThread[] threads = [.. Enumerable.Range(0, readers).Select(_ => BoundedThread.Start(() =>
        {
            using IPhysicsQueryLease lease = scene.AcquireLease();
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                // Capture has bound both faces into this reader's polygons, before membership and joins use them.
                if (entering || phase != ContactAllocationPhase.SupportCapture) return;
                ContactAllocationDiagnostics.SetObserver(null);
                BoundedThread.Arrive(captured);
                BoundedThread.Await(resume);
            });
            try { SupportFootprintEquivalence.AssertMatchesReference(() => scene.Find(lease)); }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
        }))];
        try
        {
            BoundedThread.Arrive(captured);
            using IPhysicsQueryLease lease = scene.AcquireLease();
            for (int i = 1; i <= (evictStatic ? statics : patches - 1); i++)
                MeshFind(world, lease, evictStatic ? new(0, i * 4) : new(i, 0));
            Assert.Equal(evictStatic ? 0 : InstalledSupportMeshWorldCache.PerStaticTriangleCapacity,
                cache.TriangleCountFor(owner));
            Assert.Equal(evictStatic ? statics * 2 : InstalledSupportMeshWorldCache.PerStaticTriangleCapacity,
                cache.TriangleCount);
            // A revisit must rebuild the evicted face, while the paused readers still retain the old one.
            MeshFind(world, lease, Vector2.Zero);
            Assert.NotSame(first, MeshForm(cache, owner, 0));
        }
        finally
        {
            resume.Set();
            JoinMeshReaders(threads);
        }
        scene.Checked("mesh after concurrent eviction", out _);
    }

    static void JoinMeshReaders(BoundedThread[] threads)
    {
        Exception? failure = null;
        foreach (BoundedThread thread in threads)
        {
            try
            {
                Exception? error = thread.JoinFailure();
                failure ??= error;
            }
            catch (Exception error)
            {
                failure ??= error;
                // Even a timed-out join must clean up this reader and allow the other readers to be joined.
                try
                {
                    if (thread.IsAlive) thread.Interrupt();
                    thread.Join();
                }
                catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    static TriangleMeshShape MeshConcurrencyPatches(int count)
    {
        var vertices = new Vector3[count * 4];
        var indices = new int[count * 6];
        for (int patch = 0; patch < count; patch++)
        {
            vertices[patch * 4] = new(patch - 0.3f, 0, -0.3f);
            vertices[patch * 4 + 1] = new(patch - 0.3f, 0, 0.3f);
            vertices[patch * 4 + 2] = new(patch + 0.3f, 0, 0.3f);
            vertices[patch * 4 + 3] = new(patch + 0.3f, 0, -0.3f);
            new[] { 0, 2, 1, 0, 3, 2 }.Select(i => i + patch * 4).ToArray().CopyTo(indices, patch * 6);
        }
        return new(vertices, indices);
    }
}
