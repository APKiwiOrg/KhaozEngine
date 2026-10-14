using System;
using System.Numerics;
using System.Reflection;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>PB-1 gate rules. Every scenario is event coordinated, bounded and joins its threads.</summary>
public sealed class ConcurrentReadGateTests
{
    static readonly BoxShape Box = new(new Vector3(0.5f));
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);

    [Fact]
    public void TwoThreadsHoldReadLeasesOnOneWorldAtOnceAndBothQuery()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle solid = world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        using var barrier = new Barrier(2);
        var hits = new RayHit[2];
        BoundedThread[] readers = new BoundedThread[2];
        for (int i = 0; i < readers.Length; i++)
        {
            int slot = i;
            readers[slot] = BoundedThread.Start(() =>
            {
                using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
                // Both leases are live before either thread queries or releases.
                BoundedThread.Arrive(barrier);
                Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out hits[slot]));
                Assert.True(world.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out _));
                lease.AssertCurrent();
                BoundedThread.Arrive(barrier);
            });
        }
        foreach (BoundedThread reader in readers) reader.Join();
        Assert.All(hits, hit => Assert.Equal(solid, hit.Body));
        Assert.Equal(hits[0], hits[1]);
    }

    [Fact]
    public void ANestedSecondLeaseStillThrowsWhileAnotherThreadHoldsItsOwn()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        BoundedThread other = BoundedThread.Start(() =>
        {
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            held.Set();
            BoundedThread.Await(release);
            lease.AssertCurrent();
        });
        try
        {
            BoundedThread.Await(held);
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            Assert.Throws<InvalidOperationException>(() => world.AcquireQueryReadLease());
            Assert.Throws<InvalidOperationException>(() => world.AddStatic(Box, Pose.Identity));
            lease.AssertCurrent();
            Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out _));
        }
        finally { release.Set(); }
        other.Join();
    }

    [Fact]
    public void AMutationFromADiagnosticCallbackThrowsBeforeTheGenerationChanges()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new Vector3(2f, 1f, 2f)), Pose.At(new Vector3(0f, -1f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        long staticGeneration = world.StaticGeometryGeneration;
        Exception? mutation = null, disposal = null;
        bool fired = false;
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        long generation = lease.GeometryGeneration;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (fired || !entering || phase != ContactAllocationPhase.SupportProposals) return;
            fired = true;
            mutation = Record.Exception(() => world.AddStatic(Box, Pose.At(new Vector3(0f, 0.25f, 0f))));
            disposal = Record.Exception(view.Dispose);
        });
        SupportFootprintResult result;
        try
        {
            result = ((IPhysicsSupportFootprint)world).QuerySupportFootprint(lease, SupportFootprintTests.Request(),
                new SupportFootprintMember[256], new ulong[1024]);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
        Assert.True(fired);
        Assert.IsType<InvalidOperationException>(mutation);
        Assert.IsType<InvalidOperationException>(disposal);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        lease.AssertCurrent();
        Assert.Equal(generation, lease.GeometryGeneration);
        Assert.Equal(staticGeneration, world.StaticGeometryGeneration);
        Assert.True(view.Raycast(new Vector3(0f, 3f, 0f), -Vector3.UnitY, 8f, out RayHit hit));
        Assert.Equal(0f, hit.Point.Y);
    }

    [Fact]
    public void ARefusedAcquisitionReleasesEverythingItTook()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        view.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease());
        // No read interval survives the refusal: this thread may mutate, and another thread's writer runs.
        world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        BoundedThread writer = BoundedThread.Start(() => world.AddStatic(Box, Pose.At(new Vector3(4f, 0f, 0f))));
        writer.Join();

        using (IPhysicsQueryLease first = world.AcquireQueryReadLease())
        {
            Assert.Throws<InvalidOperationException>(() => world.AcquireQueryReadLease());
            first.AssertCurrent();
        }
        world.AddStatic(Box, Pose.At(new Vector3(6f, 0f, 0f)));
        BoundedThread.Start(() => world.RemoveStatic(new StaticHandle(0))).Join();
        Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit hit));
        Assert.Equal(new StaticHandle(1), hit.Body);
    }
    [Fact]
    public void AQueuedWriterAndANestedQueryInsideAnActiveReaderBothComplete()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        QueryGate gate = Gate(world);
        using var held = new ManualResetEventSlim();
        using var queued = new ManualResetEventSlim();
        using var nested = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IPhysicsQueryLease? shared = null;
        StaticHandle written = default;
        RayHit late = default;
        BoundedThread? writer = null, newcomer = null;
        BoundedThread reader = BoundedThread.Start(() =>
        {
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            shared = lease;
            held.Set();
            BoundedThread.Await(queued);
            // Nested reads reuse the admitted interval even though a writer and a new reader now wait.
            Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit hit));
            Assert.Equal(2f - 0.5f, hit.Distance);
            Assert.True(view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out _));
            Assert.False(world.ComputePenetration(Capsule, Pose.At(new Vector3(-4f, 0f, 0f)), out _));
            lease.AssertCurrent();
            nested.Set();
            BoundedThread.Await(release);
        });
        try
        {
            BoundedThread.Await(held);
            writer = BoundedThread.Start(() => written = world.AddStatic(Box, Pose.At(new Vector3(1f, 0f, 0f))));
            BoundedThread.Until(() => gate.WaitingWriters == 1);
            newcomer = BoundedThread.Start(() => Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out late)));
            BoundedThread.Until(() => gate.WaitingReaders == 1);
            queued.Set();
            BoundedThread.Await(nested);
            Assert.True(writer.IsAlive);
            Assert.True(newcomer.IsAlive);
            // Wrong-thread use and disposal fail at once instead of queueing behind the writer.
            Assert.Throws<InvalidOperationException>(shared!.AssertCurrent);
            Assert.Throws<InvalidOperationException>(shared.Dispose);
        }
        finally { queued.Set(); release.Set(); }
        reader.Join();
        writer!.Join();
        newcomer!.Join();
        // The new reader queued behind the writer, so it sees the writer's static.
        Assert.Equal(written, late.Body);
    }

    [Fact]
    public void WriterIntervalsNestWritesAndReadsRefuseLeasesAndReleaseScratchAtTheirOuterExit()
    {
        var pool = new QueryScratchPool();
        var gate = new QueryGate(pool);
        QueryGate.Holder writer = gate.EnterWrite();
        Assert.Same(writer, gate.EnterWrite());
        gate.ExitWrite(writer);
        Assert.Throws<InvalidOperationException>(() => gate.EnterLease());
        QueryGate.Holder read = gate.EnterRead();
        Assert.Same(writer, read);
        QueryScratch scratch = gate.Scratch(read);
        Assert.Same(scratch, gate.Scratch(gate.EnterRead()));
        gate.ExitRead(read);
        gate.ExitRead(read);
        Assert.Same(scratch, writer.Scratch);
        gate.ExitWrite(writer);
        Assert.Null(writer.Scratch);
        Assert.Equal(0, writer.Writes);

        QueryGate.Holder reader = gate.EnterRead();
        Assert.Throws<InvalidOperationException>(() => gate.EnterWrite());
        Assert.Same(scratch, gate.Scratch(reader));
        gate.ExitRead(reader);
        Assert.Single(pool.Constructed());
        Assert.Equal(0, scratch.Disposals);

        QueryScratch borrowed = pool.Rent();
        Assert.Same(scratch, borrowed);
        QueryScratch late = pool.Rent();
        pool.Return(borrowed);
        pool.Close();
        Assert.Equal(1, scratch.Disposals);
        pool.Return(late);
        Assert.Equal(1, late.Disposals);
        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
    }

    [Fact]
    public void TheBepuWorldAndItsViewDeclareConcurrentReads()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        Assert.IsAssignableFrom<IPhysicsConcurrentReadSource>(world);
        Assert.IsAssignableFrom<IPhysicsConcurrentReadSource>(view);
    }

    [Fact]
    public void AnUnchangedLeaseSourceStillCompilesAndWorksSerially()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        IPhysicsQueryLeaseSource source = new ForwardingLeaseSource(world);
        Assert.False(source is IPhysicsConcurrentReadSource);
        using (IPhysicsQueryLease lease = source.AcquireQueryReadLease())
        {
            Assert.Same(world, lease.SourceWorld);
            Assert.Throws<InvalidOperationException>(() => source.AcquireQueryReadLease());
            Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out _));
            lease.AssertCurrent();
        }
        world.AddStatic(Box, Pose.At(new Vector3(5f, 0f, 0f)));
    }

    sealed class ForwardingLeaseSource(IPhysicsQueryLeaseSource inner) : IPhysicsQueryLeaseSource
    {
        public IPhysicsQueryLease AcquireQueryReadLease() => inner.AcquireQueryReadLease();
    }

    internal static QueryGate Gate(BepuPhysicsWorld world) => (QueryGate)typeof(BepuPhysicsWorld)
        .GetField("_queryGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
}
