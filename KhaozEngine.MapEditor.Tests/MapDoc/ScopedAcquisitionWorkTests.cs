using System;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class ScopedAcquisitionWorkTests
{
    [Fact]
    public void BoundSlotEnumeration_RefusesFromTheCheckedCountWithoutVisitingSlots()
    {
        var work = new MapAcquisitionWork();
        MapScopedSurfaces s = MapScopedSurfaces.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.MicroBound()), AcquisitionBoundFixtures.MicroScope, Array.Empty<string>(), work);
        Assert.Equal((MapAcquireStatus.CapacityExceeded, 0, 244_140_625L, 0L), (s.Status, s.Witness.Present.Count, work.LargestBoundSlotRange, work.BoundSlotVisits));
        Assert.Contains("bound slots", s.Detail);
        Assert.Empty(s.Witness.Records);
        Assert.Empty(s.Witness.KnownEmpty);
        Assert.Empty(s.Witness.Unavailable);
        Assert.False(s.ReadWitness.Complete);
    }

    [Fact]
    public void BoundSlotEnumeration_SubtractsAlreadyAcquiredSlotsBeforeRefusing()
    {
        var work = new MapAcquisitionWork();
        MapScopedSurfaces s = MapScopedSurfaces.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.AcquiredBounds()), AcquisitionBoundFixtures.AcquiredScope, Array.Empty<string>(), work);
        Assert.Equal((MapAcquireStatus.Complete, 2, 1L, 0L), (s.Status, s.Witness.Present.Count, work.LargestBoundSlotRange, work.BoundSlotVisits));
        Assert.Equal(new[] { new MapPatchKey("low", 0, 0), new("top", 0, 0) }, s.Witness.Present.Select(p => p.Key).OrderBy(k => k));
        Assert.True(s.ReadWitness.Complete);
        Assert.Empty(s.Witness.Unavailable);
    }
}
