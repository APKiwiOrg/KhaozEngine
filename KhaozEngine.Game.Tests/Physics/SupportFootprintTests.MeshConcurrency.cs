using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using RigidPose = BepuPhysics.RigidPose;
using TypedIndex = BepuPhysics.Collidables.TypedIndex;

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

    // A cold builder pauses after building its first triangle and before taking the budget gate. Meanwhile its
    // static's slot is evicted, evicted and rebuilt by another reader, or replaced under another geometry version.
    // The late publication keeps the global triangle count equal to the per-static counts and throws nothing.
    [Theory]
    [InlineData("evicted")]
    [InlineData("rebuilt")]
    [InlineData("versioned")]
    public void PausedMeshBuilderPublishesIntoAChangedSlotWithoutCountDrift(string change)
    {
        var shape = MeshConcurrencyPatches(1);
        var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle owner = world.AddStatic(shape, Pose.Identity);
        StaticHandle[] statics = [owner, .. Enumerable.Range(1, InstalledSupportMeshWorldCache.StaticCapacity)
            .Select(i => world.AddStatic(shape, Pose.At(new(0, 0, i * 4))))];
        using var scene = new WorldFormScene(world, CostRows().Single(row => row.Name == "flat mesh").Query);
        InstalledSupportMeshWorldCache cache = MeshForms(world);
        using var built = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        BoundedThread builder = BoundedThread.Start(() =>
        {
            using IPhysicsQueryLease lease = scene.AcquireLease();
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                // The first triangle is built and not yet published.
                if (entering || phase != ContactAllocationPhase.SupportMaterialization) return;
                ContactAllocationDiagnostics.SetObserver(null);
                built.Set();
                BoundedThread.Await(resume);
            });
            try { SupportFootprintEquivalence.AssertMatchesReference(() => scene.Find(lease)); }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
        });
        try
        {
            BoundedThread.Await(built);
            Assert.Equal(0, cache.TriangleCount);
            if (change == "versioned") ReplaceMeshSlot(cache, owner);
            else
            {
                using IPhysicsQueryLease lease = scene.AcquireLease();
                for (int i = 1; i < statics.Length; i++) MeshFind(world, lease, new(0, i * 4));
                Assert.Equal(0, cache.TriangleCountFor(owner));
                if (change == "rebuilt") MeshFind(world, lease, Vector2.Zero);
            }
            Assert.Equal(statics.Sum(cache.TriangleCountFor), cache.TriangleCount);
        }
        finally
        {
            resume.Set();
            JoinMeshReaders([builder]);
        }
        Assert.Equal(2, cache.TriangleCountFor(owner));
        Assert.Equal(statics.Sum(cache.TriangleCountFor), cache.TriangleCount);
        scene.Checked($"mesh after a paused build into a {change} slot", out _);
        Assert.Equal(statics.Sum(cache.TriangleCountFor), cache.TriangleCount);
    }

    // Replaces the owner's slot with one keyed by the next geometry version, as a version change does.
    static void ReplaceMeshSlot(InstalledSupportMeshWorldCache cache, StaticHandle owner)
    {
        var slots = (IDictionary)typeof(InstalledSupportMeshWorldCache)
            .GetField("_statics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        object slot = slots[owner.Value]!;
        var shape = (TypedIndex)slot.GetType().GetProperty("Shape", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(slot)!;
        long version = (long)slot.GetType().GetProperty("Version", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(slot)!;
        cache.Matches(owner, shape, version + 1, new RigidPose(Vector3.Zero));
        Assert.NotSame(slot, slots[owner.Value]);
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
