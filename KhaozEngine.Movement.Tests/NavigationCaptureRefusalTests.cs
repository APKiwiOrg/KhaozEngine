using System;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public sealed class NavigationCaptureRefusalTests
{
    [Theory]
    [InlineData(float.NaN, 0, 1, NavigationBuildRefusal.SurfaceHeightNonfinite)]
    [InlineData(0, 0, 1, NavigationBuildRefusal.SurfaceHeightNotAscending)]
    [InlineData(1, 0, float.NaN, NavigationBuildRefusal.SurfaceHeadroomNaN)]
    [InlineData(1, 0, -1, NavigationBuildRefusal.SurfaceHeadroomNegative)]
    public void MalformedSurfaceNamesItsColumnAndFailedValue(float height, float previous, float headroom,
        NavigationBuildRefusal reason)
    {
        var failure = Assert.Throws<NavigationBuildException>(() =>
            PhysicsNavBake.InjectMalformedSurfaceForTests(2, 3, 1, height, previous, headroom));
        Assert.Equal(reason, failure.Reason);
        Assert.Equal(2, failure.ColumnX);
        Assert.Equal(3, failure.ColumnZ);
        Assert.Equal(1, failure.SurfaceIndex);
        if (reason is NavigationBuildRefusal.SurfaceHeightNonfinite or NavigationBuildRefusal.SurfaceHeightNotAscending)
            Assert.Equal(height, failure.ActualHeight);
        else Assert.Equal(headroom, failure.ActualHeadroom);
        if (reason == NavigationBuildRefusal.SurfaceHeightNotAscending) Assert.Equal(previous, failure.PreviousHeight);
        PhysicsNavBake.InjectMalformedSurfaceForTests(2, 3, 1, 1, 0, 0);
        PhysicsNavBake.InjectMalformedSurfaceForTests(2, 3, 1, 1, 0, float.PositiveInfinity);
    }

    [Theory]
    [InlineData(float.NaN, 0, float.NaN, NavigationBuildRefusal.SurfaceHeightNonfinite)]
    [InlineData(0, 0, float.NaN, NavigationBuildRefusal.SurfaceHeightNotAscending)]
    public void CompoundSurfaceGuardKeepsPredicateOrder(float height, float previous, float headroom,
        NavigationBuildRefusal reason)
    {
        var failure = Assert.Throws<NavigationBuildException>(() =>
            PhysicsNavBake.InjectMalformedSurfaceForTests(0, 0, 0, height, previous, headroom));
        Assert.Equal(reason, failure.Reason);
        PhysicsNavBake.InjectMalformedSurfaceForTests(0, 0, 0, 1, 0, 0);
    }

    [Fact]
    public void BasisOriginRefusalReportsTheValueItCompared()
    {
        using var backend = GroundTraversalProbeTests.FlatWorld();
        using var world = new OriginReportingWorld(backend);
        var basis = PhysicsNavCaptureBasis.Capture(world)!.Value;
        world.LeaseOrigin = Vector3.UnitX;
        var failure = Assert.Throws<NavigationBuildException>(() => basis.AssertCurrent());
        Assert.Equal(NavigationBuildRefusal.CaptureOriginChanged, failure.Reason);
        Assert.Equal(Vector3.Zero, failure.ExpectedOrigin);
        Assert.Equal(Vector3.UnitX, failure.ActualOrigin);
        world.LeaseOrigin = null;
        basis.AssertCurrent();
    }

    [Fact]
    public void StableScopeDistinguishesWorldOriginFromLeaseOrigin()
    {
        using var backend = GroundTraversalProbeTests.FlatWorld();
        using var world = new OriginReportingWorld(backend);
        using var context = new GroundMoveContext((_, _) => 0, physics: world);
        var basis = PhysicsNavCaptureBasis.Capture(world)!.Value;
        using var scope = LegacyNavigationReadScope.Acquire(context, basis, Vector3.Zero);
        world.ReportedOrigin = Vector3.UnitZ;
        var failure = Assert.Throws<NavigationBuildException>(scope.AssertCurrent);
        Assert.Equal(NavigationBuildRefusal.LegacyWorldOriginChanged, failure.Reason);
        Assert.Equal(Vector3.Zero, failure.ExpectedOrigin);
        Assert.Equal(Vector3.UnitZ, failure.ActualOrigin);
        world.ReportedOrigin = null;
        scope.AssertCurrent();
    }

    sealed class OriginReportingWorld : PhysicsForwarder, IPhysicsWorld, IPhysicsQueryLeaseSource
    {
        readonly IPhysicsWorld _world;
        internal OriginReportingWorld(IPhysicsWorld world) : base(world) => _world = world;
        internal Vector3? ReportedOrigin { get; set; }
        internal Vector3? LeaseOrigin { get; set; }
        public new Vector3 Origin => ReportedOrigin ?? _world.Origin;
        public IPhysicsQueryLease AcquireQueryReadLease() =>
            new LegacyLeaseMetadata(((IPhysicsQueryLeaseSource)_world).AcquireQueryReadLease(), this,
                LeaseOrigin ?? _world.Origin);
        public override void Dispose() { }
    }
}
