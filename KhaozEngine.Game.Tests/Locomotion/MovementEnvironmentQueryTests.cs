using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementEnvironmentQueryTests
{
    static readonly MovementSpaceKey Space = new("world", "room");
    static readonly MovementBodyQuery Body = new(Vector3.Zero, 0.3f, 0.75f, Space, null);

    [Fact]
    public void KnownWetSampleKeepsIntervalAndNominalSurfaceSeparate()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view)
        {
            Point = new MovementWaterPoint(MovementAvailability.Known, Space,
                new MovementDomainKey("world", "flooded-room"), true, 1f,
                new MovementWaterInterval(-4f, -1f, 4f, false, "floor", "ceiling"))
        };
        using var lease = Acquire(environment);
        var body = new MovementBodyQuery(new Vector3(0f, -1f, 0f), 0.3f, 0.75f, Space, null);
        MovementWaterPoint point = Sample(lease, body);
        Assert.Equal(environment.Point, point);
        Assert.False(point.Interval!.Value.UpperIsFreeSurface);
        Assert.Equal(1, environment.SampleCalls);
    }

    [Fact]
    public void DefaultSampleStaysUnresolvedInsteadOfBecomingKnownDry()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        using var lease = Acquire(environment);
        Assert.Equal(MovementAvailability.Unresolved, Sample(lease, Body).Availability);
        Assert.Equal(1, environment.SampleCalls);
    }

    [Fact]
    public void BodyOutsideCertifiedScopeIsRefusedBeforeProviderAccess()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        using var lease = Acquire(environment);
        var body = new MovementBodyQuery(new Vector3(100f, 0f, 0f), 0.3f, 0.75f, Space, null);
        Assert.Equal(MovementAvailability.Unresolved, Sample(lease, body).Availability);
        Assert.Equal(0, environment.SampleCalls);
    }

    [Fact]
    public void DefaultBodyIsInvalidBeforeProviderAccess()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        using var lease = Acquire(environment);
        Assert.Equal(MovementAvailability.Invalid, Sample(lease, default).Availability);
        Assert.Equal(0, environment.SampleCalls);
    }

    [Fact]
    public void KnownSampleFromAnotherWorldIsRefused()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view)
        {
            Point = new MovementWaterPoint(MovementAvailability.Known,
                new MovementSpaceKey("another-world", "room"), null, false, 1f, null)
        };
        using var lease = Acquire(environment);
        Assert.Equal(MovementAvailability.Invalid, Sample(lease, Body).Availability);
    }

    [Fact]
    public void PinBecomingStaleDuringSampleCannotPublishItsKnownResult()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view)
        {
            Point = new MovementWaterPoint(MovementAvailability.Known, Space, null, false, 1f, null)
        };
        environment.OnSample = () => environment.Fault = "stale-pin";
        using var lease = Acquire(environment);
        Assert.Equal(MovementAvailability.Stale, Sample(lease, Body).Availability);
    }

    [Fact]
    public void DisposedLeaseCannotSampleOrCallTheProvider()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        using var lease = Acquire(environment);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Sample(lease, Body));
        Assert.Equal(0, environment.SampleCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherThreadCannotValidateOrDisposeTheLease(bool dispose)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        using var lease = Acquire(environment);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { if (dispose) lease.Dispose(); else lease.AssertCurrent(); }
            catch (Exception exception) { error = exception; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        Assert.IsType<InvalidOperationException>(error);
        lease.AssertCurrent();
        Assert.False(environment.Disposed);
    }

    [Fact]
    public void DisposedLeaseCannotValidateAgain()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        using var lease = Acquire(environment);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(lease.AssertCurrent);
    }

    [Fact]
    public void NestedContextAcquisitionDoesNotPrepareOrInvalidateItsFirstLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        int preparations = 0;
        environment.OnPrepare = () => preparations++;
        using var lease = Acquire(environment);
        var nested = environment.Acquire();
        using var unexpected = nested.Lease;
        Assert.Equal(MovementAvailability.Invalid, nested.Status);
        Assert.Null(nested.Lease);
        Assert.Equal(1, preparations);
        lease.AssertCurrent();
    }

    [Fact]
    public void LocalIdentityIgnoresSourceValueEqualityAndValueHashing()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var raw = world.CreateQueryViewExcludingStatics([]);
        var sourceA = new EquatablePhysicsView(raw);
        var sourceB = new EquatablePhysicsView(raw);
        var viewA = new EquatablePhysicsView(raw) { ReportedSource = sourceA, LeaseSource = sourceA };
        var viewB = new EquatablePhysicsView(raw) { ReportedSource = sourceB, LeaseSource = sourceB };
        MovementQueryLeaseId first;
        using (var lease = Acquire(new EnvironmentAcquisitionFixture(viewA))) first = lease.Id;
        using (var lease = Acquire(new EnvironmentAcquisitionFixture(viewB)))
        {
            Assert.NotEqual(first, lease.Id);
            _ = first.GetHashCode();
            _ = lease.Id.GetHashCode();
        }
        Assert.Equal(0, sourceA.ValueHashCalls);
        Assert.Equal(0, sourceB.ValueHashCalls);
    }

    static MovementQueryLease Acquire(EnvironmentAcquisitionFixture environment)
    {
        var result = environment.Acquire();
        Assert.Equal(MovementAvailability.Known, result.Status);
        return Assert.IsType<MovementQueryLease>(result.Lease);
    }

    static MovementWaterPoint Sample(MovementQueryLease lease, in MovementBodyQuery body) =>
        lease.SampleCentreWater(body);
}
