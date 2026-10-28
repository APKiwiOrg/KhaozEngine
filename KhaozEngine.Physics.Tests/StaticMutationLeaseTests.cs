using System;
using System.Numerics;
using System.Reflection;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed partial class StaticMutationLeaseTests
{
    static readonly BoxShape Box = new(new Vector3(0.5f));

    [Fact]
    public void AcquisitionAlonePreservesBothGenerationsAndCapturesTheCompleteOwner()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Vector3 origin = new(128, 32, -64);
        world.Rebase(origin);
        long geometry = ReadGeneration(world), statics = world.StaticGeometryGeneration;

        using (IPhysicsStaticMutationLease lease = Acquire(world))
        {
            Assert.Same(world, lease.SourceWorld);
            Assert.Equal(origin, lease.Origin);
            Assert.Equal(statics, lease.StaticGeometryGeneration);
            lease.AssertCurrent();
        }

        Assert.Equal(geometry, ReadGeneration(world));
        Assert.Equal(statics, world.StaticGeometryGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticEditsKeepTheirExactRevisionHooksInsideAndOutsideTheLease(bool leased)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        long geometry = ReadGeneration(world), statics = world.StaticGeometryGeneration;
        using (IPhysicsStaticMutationLease? lease = leased ? Acquire(world) : null)
        {
            StaticHandle handle = world.AddStatic(Box, Pose.At(new(2, 0, 0)));
            Assert.Equal(statics + 1, world.StaticGeometryGeneration);
            Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out RayHit hit));
            Assert.Equal(handle, hit.Body);
            lease?.AssertCurrent();
            if (lease is not null) Assert.Equal(statics + 1, lease.StaticGeometryGeneration);

            world.RemoveStatic(handle);
            Assert.Equal(statics + 2, world.StaticGeometryGeneration);
            Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out _));
            world.RemoveStatic(handle);
            Assert.Equal(statics + 2, world.StaticGeometryGeneration);
            lease?.AssertCurrent();
            if (lease is not null) Assert.Equal(statics + 2, lease.StaticGeometryGeneration);
        }

        // The missing removal still conservatively advances the physical query generation.
        Assert.Equal(geometry + 3, ReadGeneration(world));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnActiveReadRefusesAcquisitionBeforeEitherGenerationChanges(bool explicitLease)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        long geometry = ReadGeneration(world), statics = world.StaticGeometryGeneration;
        using (IDisposable read = explicitLease ? world.AcquireQueryReadLease() : EnterQuery(world))
        {
            Assert.Throws<InvalidOperationException>(() => Acquire(world));
            Assert.Equal(statics, world.StaticGeometryGeneration);
            Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out _));
        }

        Assert.Equal(geometry, ReadGeneration(world));
        using IPhysicsStaticMutationLease next = Acquire(world);
        next.AssertCurrent();
    }

    [Fact]
    public void NestedMutationAndExplicitReadLeasesAreRefusedWithoutShorteningTheWriter()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        using IPhysicsStaticMutationLease lease = Acquire(world);
        long statics = lease.StaticGeometryGeneration;

        Assert.Throws<InvalidOperationException>(() => Acquire(world));
        Assert.Throws<InvalidOperationException>(world.AcquireQueryReadLease);
        Assert.Throws<InvalidOperationException>(
            ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease);
        LeaseTestThread.AssertWaits(() => world.AddStatic(Box, Pose.Identity));
        Assert.Equal(statics, lease.StaticGeometryGeneration);
        world.AddStatic(Box, Pose.Identity);
        lease.AssertCurrent();
    }

    [Fact]
    public void ReadLeaseHoldsOutAWriterUntilRelease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        long statics = world.StaticGeometryGeneration;
        using var attempting = new ManualResetEventSlim();
        using var published = new ManualResetEventSlim();
        LeaseTestThread? pending = null;
        using (IPhysicsQueryLease read = world.AcquireQueryReadLease())
        {
            // The worker has no other wait. Interrupting admission proves it reached the gate,
            // rather than assuming an unscheduled worker is blocked because a signal is absent.
            LeaseTestThread.AssertWaits(() =>
            {
                using IPhysicsStaticMutationLease writer = Acquire(world);
                world.AddStatic(Box, Pose.Identity);
            });
            pending = LeaseTestThread.Start(() =>
            {
                attempting.Set();
                using (IPhysicsStaticMutationLease writer = Acquire(world))
                    world.AddStatic(Box, Pose.Identity);
                published.Set();
            });
            LeaseTestThread.Await(attempting);
            Assert.False(published.IsSet);
            Assert.Equal(statics, world.StaticGeometryGeneration);
            read.AssertCurrent();
        }

        pending.Join();
        LeaseTestThread.Await(published);
        Assert.Equal(statics + 1, world.StaticGeometryGeneration);
    }

    [Fact]
    public void ACompletedStaticEditHoldsReadersOutUntilMetadataPublication()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle? published = null;
        using var attempting = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        LeaseTestThread? pending = null;
        using (IPhysicsStaticMutationLease writer = Acquire(world))
        {
            StaticHandle installed = world.AddStatic(Box, Pose.At(new(2, 0, 0)));
            LeaseTestThread.AssertWaits(ReadPublished);
            pending = LeaseTestThread.Start(() =>
            {
                attempting.Set();
                ReadPublished();
                completed.Set();
            });
            LeaseTestThread.Await(attempting);
            Assert.False(completed.IsSet);
            published = installed;
            writer.AssertCurrent();
        }

        pending.Join();
        LeaseTestThread.Await(completed);

        void ReadPublished()
        {
            using IPhysicsQueryLease read = world.AcquireQueryReadLease();
            Assert.NotNull(published);
            Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out RayHit hit));
            Assert.Equal(published.Value, hit.Body);
            read.AssertCurrent();
        }
    }

    [Fact]
    public void WrongThreadCannotInspectOrReleaseTheWriter()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsStaticMutationLease lease = Acquire(world);
        LeaseTestThread.Start(() =>
        {
            Assert.Throws<InvalidOperationException>(lease.AssertCurrent);
            Assert.Throws<InvalidOperationException>(() => lease.StaticGeometryGeneration);
            Assert.Throws<InvalidOperationException>(lease.Dispose);
        }).Join();

        lease.AssertCurrent();
        LeaseTestThread.AssertWaits(() => world.AddStatic(Box, Pose.Identity));
        world.AddStatic(Box, Pose.Identity);
    }

    [Fact]
    public void ExpiredLeaseCannotCertifyOrReleaseAReplacementLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        IPhysicsStaticMutationLease expired = Acquire(world);
        expired.Dispose();
        using (IPhysicsStaticMutationLease current = Acquire(world))
        {
            Assert.Throws<ObjectDisposedException>(expired.AssertCurrent);
            Assert.Throws<ObjectDisposedException>(() => expired.StaticGeometryGeneration);
            expired.Dispose();
            current.AssertCurrent();
            LeaseTestThread.AssertWaits(() => world.AddStatic(Box, Pose.Identity));
        }

        LeaseTestThread.Start(() => world.AddStatic(Box, Pose.Identity)).Join();
    }

    [Fact]
    public void AComposingFailureReleasesTheWriterWithoutRollingBackItsEdits()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var failure = new InvalidOperationException("Metadata publication failed.");
        Action compose = () =>
        {
            using IPhysicsStaticMutationLease writer = Acquire(world);
            world.AddStatic(Box, Pose.At(new(2, 0, 0)));
            throw failure;
        };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(compose));

        LeaseTestThread.Start(() =>
        {
            using IPhysicsQueryLease read = world.AcquireQueryReadLease();
            Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out _));
            read.AssertCurrent();
        }).Join();
        LeaseTestThread.Start(() => world.AddStatic(Box, Pose.At(new(6, 0, 0)))).Join();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidatedWriterRefusesUseButDisposalStillReleasesTheGate(bool disposeOwner)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        IPhysicsStaticMutationLease lease = Acquire(world);
        if (disposeOwner) world.Dispose();
        else world.Rebase(new(8, 16, -32));

        Assert.Throws<InvalidOperationException>(lease.AssertCurrent);
        Assert.Throws<InvalidOperationException>(() => lease.StaticGeometryGeneration);
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(lease.AssertCurrent);
        LeaseTestThread.Start(world.Dispose).Join();
    }

    [Fact]
    public void FailedAcquisitionOnADisposedOwnerDoesNotLeakTheWriter()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Acquire(world));
        LeaseTestThread.Start(world.Dispose).Join();
    }

    [Fact]
    public void BorrowedViewsRetainReadCapabilitiesWithoutExposingMutation()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        Assert.False(view is IPhysicsStaticMutationSource);
        using IPhysicsQueryLease read = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        Assert.Same(world, read.SourceWorld);
        Assert.Throws<InvalidOperationException>(() => Acquire(world));
        read.AssertCurrent();
    }

    static IPhysicsStaticMutationLease Acquire(BepuPhysicsWorld world) =>
        Assert.IsAssignableFrom<IPhysicsStaticMutationSource>(world).AcquireStaticMutationLease();

    static long ReadGeneration(BepuPhysicsWorld world)
    {
        using IPhysicsQueryLease read = world.AcquireQueryReadLease();
        return read.GeometryGeneration;
    }

    static IDisposable EnterQuery(BepuPhysicsWorld world) => Assert.IsAssignableFrom<IDisposable>(
        typeof(BepuPhysicsWorld).GetMethod("EnterQuery", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(world, [false]));
}
