using System;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class ScopedProducerBoundWorkTests
{
    [Fact]
    public void BoundPreflight_UsesKnownEmptyReservationsBeforeOneOverLimitRange()
    {
        var fixture = AcquisitionConformanceFixtures.CapThreeBounds();
        var work = new MapAcquisitionWork();
        MapScopedSurfaces view = MapScopedSurfaces.Acquire(fixture.Probe, fixture.Scope, Array.Empty<string>(), work);
        Assert.Equal(MapAcquireStatus.CapacityExceeded, view.Status);
        AcquisitionConformanceFixtures.AssertNoFacts(view);
        Assert.Equal((2L, 0L), (work.LargestBoundSlotRange, work.BoundSlotVisits));
        Assert.Empty(fixture.Probe.Session.ExplicitReads);
        Assert.Contains("bound slots", view.Detail);
        Assert.Equal(new[] { new MapPatchKey("seed", 0, 0), new("top", 0, 0) }, fixture.Probe.Session.ReservedAtDispose);
        Assert.Equal(1, fixture.Probe.Session.Disposals);
    }

    [Fact]
    public void BoundPreflight_GrossCountOverflowIsCapacity()
    {
        var fixture = AcquisitionConformanceFixtures.GrossOverflowBounds();
        var work = new MapAcquisitionWork();
        MapScopedSurfaces view = MapScopedSurfaces.Acquire(fixture.Probe, fixture.Scope, Array.Empty<string>(), work);
        Assert.Equal(MapAcquireStatus.CapacityExceeded, view.Status);
        AcquisitionConformanceFixtures.AssertNoFacts(view);
        Assert.Contains("bound slots", view.Detail);
        Assert.Equal(0L, work.BoundSlotVisits);
        Assert.Empty(fixture.Probe.Session.ExplicitReads);
        Assert.Equal(new[] { new MapPatchKey("top", 0, 0) }, fixture.Probe.Session.ReservedAtDispose);
        Assert.Equal(1, fixture.Probe.Session.Disposals);
    }
}
