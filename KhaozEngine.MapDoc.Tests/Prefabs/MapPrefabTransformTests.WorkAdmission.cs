using System;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

// T3-R3 and F-R1. A public copying adapter discards the internal shared allowance, so every pose-group session
// repeats its metadata and rectangle work. Composition must admit both counters against one common ceiling.
public sealed partial class MapPrefabTransformTests
{
    [Theory]
    [InlineData(1025, 1, MapAcquireStatus.CapacityExceeded, 1025)]
    [InlineData(1024, 2, MapAcquireStatus.Complete, 2048)]
    public void ComposedWork_CopiedSessionsShareOneMetadataCeilingForExcludedRoles(int count, int pages, MapAcquireStatus expected, long metadata)
    {
        var (doc, context) = PrefabFixtures.WorkAdmissionGeometry(count);
        var work = new MapSurfaceAcquisitionWork();
        var native = PrefabFixtures.PublicCopyingSource(PrefabFixtures.ObservedNative(doc, work));
        var view = MapScopedSurfaces.Acquire(new MapComposedSurfaceSource(context, native),
            new(WorldFrame.Origin, new(0.25f, -0.75f), new(0.75f, -0.25f), null, null,
                [MapSurfaceRole.Ceiling], null, new(MaxPageReads: pages)), []);
        Assert.Equal(expected, view.Status);
        Assert.Equal(2, native.Opens);
        Assert.Equal(0, work.PageVisits);
        Assert.Equal(metadata, work.MetadataChecks);
        Assert.Empty(view.Witness.Present);
        Assert.Empty(view.Witness.KnownEmpty);
        Assert.Empty(view.Surfaces);
    }

    [Theory]
    [InlineData(1020, false)]
    [InlineData(1021, true)]
    public void ComposedWork_CopiedSessionsShareOneZeroPageRectangleCeiling(int repeats, bool refused)
    {
        var (doc, context) = PrefabFixtures.WorkAdmissionGeometry(2);
        var work = new MapSurfaceAcquisitionWork();
        var native = PrefabFixtures.PublicCopyingSource(PrefabFixtures.ObservedNative(doc, work));
        using var session = new MapComposedSurfaceSource(context, native).OpenAcquisition(new(WorldFrame.Origin,
            new(0.25f, -0.75f), new(0.75f, -0.25f), null, null, Enum.GetValues<MapSurfaceRole>(), null, new(MaxPageReads: 1)));
        Assert.Equal(MapFindStatus.Complete, session.FindPatches().Status);
        Assert.Equal(4, work.RangeOperations);
        var first = new MapPatchKey("native-0000", 0, 0);
        for (int i = 0; i < repeats; i++) Assert.Equal(MapPatchStatus.KnownEmpty, session.ReadPatch(first).Status);
        var second = new MapPatchKey("native-0001", 0, 0);
        if (refused)
        {
            Assert.Throws<MapSurfaceCapacityException>(() => session.ReadPatch(second));
            Assert.Throws<InvalidOperationException>(() => session.TryGetIncidentRecords(second, out _));
            Assert.Equal(1025, work.RangeOperations);
        }
        else
        {
            Assert.Equal(MapPatchStatus.KnownEmpty, session.ReadPatch(second).Status);
            Assert.True(session.TryGetIncidentRecords(second, out var records));
            Assert.Empty(records!);
            Assert.Equal(1025, work.RangeOperations);
        }
        Assert.Equal(0, work.PageVisits);
        Assert.Equal(0, session.PagesRead);
    }

    [Theory]
    [InlineData(2, false, MapAcquireStatus.CapacityExceeded)]
    [InlineData(1, true, MapAcquireStatus.CapacityExceeded)]
    [InlineData(1, false, MapAcquireStatus.Complete)]
    public void ComposedWork_UnobservableWorkRefusesBeforeTraversalWithNamedCapability(int nativeCount, bool generated, MapAcquireStatus expected)
    {
        var (doc, context) = PrefabFixtures.ComposedBudgetGeometry(nativeCount, generated);
        var work = new MapSurfaceAcquisitionWork();
        var native = PrefabFixtures.PublicCopyingSource(PrefabFixtures.ObservedNative(doc, work), workOpaque: true);
        var view = MapScopedSurfaces.Acquire(new MapComposedSurfaceSource(context, native),
            new(WorldFrame.Origin, new(0.25f, -0.75f), new(0.75f, -0.25f), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new(MaxPageReads: 32)), []);
        Assert.Equal(expected, view.Status);
        Assert.Equal(1, native.Opens);
        if (expected == MapAcquireStatus.CapacityExceeded)
        {
            Assert.Contains("IMapSurfaceWorkSession.CanObserveWork", view.Detail);
            Assert.Contains("Raising query limits cannot cure this", view.Detail);
            Assert.DoesNotContain("CanObservePageVisits", view.Detail);
            Assert.Equal((0, 0L, 0L), (work.PageVisits, work.MetadataChecks, work.RangeOperations));
            Assert.Empty(view.Witness.Present);
            Assert.Empty(view.Witness.KnownEmpty);
        }
        else Assert.Equal("native-0", Assert.Single(view.Witness.Present).Key.SurfaceId);
    }
}
