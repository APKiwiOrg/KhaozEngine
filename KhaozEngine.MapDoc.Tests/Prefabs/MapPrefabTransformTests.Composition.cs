using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed partial class MapPrefabTransformTests
{
    [Theory]
    [InlineData(2, MapAcquireStatus.CapacityExceeded, 2)]
    [InlineData(6, MapAcquireStatus.CapacityExceeded, 6)]
    [InlineData(8, MapAcquireStatus.Complete, 8)]
    public void ComposedBudget_PublicScopeCopyCannotExceedCachedVisitCeiling(int limit, MapAcquireStatus expected, int visits)
    {
        var (doc, original) = PrefabFixtures.ComposedBudgetGeometry(2, false);
        var second = doc.Surfaces.Patches.Values.Single(p => p.Key.SurfaceId == "native-1");
        doc.Surfaces.Patches.Remove(second.Key);
        second.Key = new("native-1", 1, 0);
        doc.Surfaces.Patches.Add(second.Key, second);
        var transforms = original.Transforms.ToDictionary(p => p.Key, p => p.Value);
        transforms["Surface:native-1"] = new(-64, 0, 0, 0, 1);
        var context = new MapEffectiveContext(doc.Surfaces, transforms, new string('2', 64));
        var work = new MapSurfaceAcquisitionWork();
        var native = PrefabFixtures.PublicCopyingSource(PrefabFixtures.ObservedNative(doc, work));
        var view = MapScopedSurfaces.Acquire(new MapComposedSurfaceSource(context, native),
            new(WorldFrame.Origin, new(0.25f, -0.75f), new(0.75f, -0.25f), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new(MaxPageReads: limit)), []);
        Assert.Equal(expected, view.Status);
        Assert.Equal(visits, work.PageVisits);
        Assert.Equal(0, view.PagesRead);
        if (expected == MapAcquireStatus.CapacityExceeded) Assert.Empty(view.Witness.Present);
        else Assert.Equal(2, view.Witness.Present.Count);
    }

    [Theory]
    [InlineData(1, true, MapAcquireStatus.Complete, 2)]
    [InlineData(2, false, MapAcquireStatus.CapacityExceeded, 0)]
    public void ComposedBudget_UnobservableSessionsCannotMultiplyAllowance(int nativeCount, bool generated, MapAcquireStatus expected, int visits)
    {
        var (doc, context) = PrefabFixtures.ComposedBudgetGeometry(nativeCount, generated);
        var work = new MapSurfaceAcquisitionWork();
        var native = PrefabFixtures.PublicCopyingSource(PrefabFixtures.ObservedNative(doc, work), opaque: true);
        var view = MapScopedSurfaces.Acquire(new MapComposedSurfaceSource(context, native),
            new(WorldFrame.Origin, new(0.25f, -0.75f), new(0.75f, -0.25f), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new(MaxPageReads: 32)), []);
        Assert.Equal(expected, view.Status);
        Assert.Equal(visits, work.PageVisits);
        // The first page-opaque native group refuses composition before a second opaque session opens.
        Assert.Equal(1, native.Opens);
        if (expected == MapAcquireStatus.CapacityExceeded)
        {
            Assert.Empty(view.Witness.Present);
            Assert.Contains("IMapSurfacePageVisitSession.CanObservePageVisits", view.Detail);
            Assert.DoesNotContain("CanObserveWork", view.Detail);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposedGeometry_GeneratedRecordKeepsWorldNativeReferences(bool nativeRoute)
    {
        var f = PrefabFixtures.WorldGeometryReference();
        var resolved = nativeRoute ? MapNativeResolution.Resolve(f.Document, f.Assets, PrefabFixtures.Options())
            : MapResolverV2.Resolve(f.Document, f.Assets, MapDocumentSurfaceSource.Capture(f.Document), PrefabFixtures.Options()).Document;
        var effective = resolved.EffectiveResources!;
        var view = effective.CompleteView();
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        var chain = view.RecordsIn(new("a/floor", 0, 0)).OfType<MapBoundaryChain>().Single(c => c.Id == "a/world-edge");
        Assert.All(chain.Vertices, v => Assert.Equal("exterior", v.Vertex.SurfaceId));
        var geometry = MapBoundaryGeometry.ResolveChain(chain, view);
        Assert.Equal(MapResolveStatus.Resolved, geometry.Status);
        Assert.Equal(new MapExactPoint(default, new(-2, 1), default), geometry.Points[0]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ComposedBudget_UsesWholeAllowanceForRelevantPages(bool stored, bool generated)
    {
        var (doc, context) = PrefabFixtures.ComposedBudgetGeometry(generated ? 1 : 33, generated);
        string? directory = null;
        try
        {
            IMapSurfaceSource native = MapDocumentSurfaceSource.Capture(doc);
            if (stored)
            {
                directory = PrefabFixtures.TempDirectory();
                MapDocumentFile.SaveTiled(doc, directory);
                native = MapStoredSurfaceSource.Open(directory);
            }
            var source = new MapComposedSurfaceSource(context, native);
            var scope = new MapSurfaceScope(WorldFrame.Origin, new(0.25f, -0.75f), new(0.75f, -0.25f), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new(MaxPageReads: generated ? 2 : 32));
            var view = MapScopedSurfaces.Acquire(source, scope, []);
            Assert.Equal(MapAcquireStatus.Complete, view.Status);
            var support = new MapSupportQuery(view).SampleSurfaceExact(new(new(1, 2), default, new(-1, 2)), "native-0");
            Assert.True(support.Status == MapSupportStatus.Supported, support.Detail);
            Assert.Single(view.Witness.Present);
            Assert.Equal("native-0", view.Witness.Present.Single().Key.SurfaceId);
            Assert.InRange(view.PagesRead, 0, scope.Limits.MaxPageReads);
        }
        finally { if (directory is not null) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ComposedBudget_TotalExhaustionRefusesWithoutPartialFacts()
    {
        var (doc, original) = PrefabFixtures.ComposedBudgetGeometry(2, false);
        var transforms = original.Transforms.ToDictionary(p => p.Key, p => p.Value);
        transforms["Surface:native-1"] = new(0, 1, 0, 0, 1);
        var context = new MapEffectiveContext(original.Surfaces, transforms, new string('d', 64));
        var work = new MapSurfaceAcquisitionWork();
        var source = new MapComposedSurfaceSource(context, PrefabFixtures.ObservedNative(doc, work));
        var view = MapScopedSurfaces.Acquire(source, new(WorldFrame.Origin, new(0.25f, -0.75f), new(0.75f, -0.25f),
            null, null, Enum.GetValues<MapSurfaceRole>(), null, new(MaxPageReads: 2)), []);
        Assert.Equal(MapAcquireStatus.CapacityExceeded, view.Status);
        Assert.Empty(view.Witness.Present);
        Assert.Equal(2, work.PageVisits);
        Assert.Equal(1, work.DirectoryCallbacks);
        Assert.Equal(1, work.IndexCallbacks);
        Assert.Equal(1, work.PayloadReads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FrozenLegacyCell_EdgeAndPartialPatchKeepSelectedOwner(bool partial, bool acquired)
    {
        var doc = LegacyExteriorFixtures.Row("tagged");
        var patch = doc.Surfaces.Patches.Values.Single();
        if (partial)
        {
            patch.CellMinX = 1;
            patch.Width = 1;
            patch.Heights = new[] { 100, 300, 200, 600 };
            patch.Cells = new[] { patch.Cells[1] };
            patch.Presence = new ulong[] { 1 };
            int index = patch.Records.FindIndex(r => r is MapSpaceFootprint);
            patch.Records[index] = ((MapSpaceFootprint)patch.Records[index]) with { SlotCells = new[] { 1 } };
        }
        else patch.Cells[0] = patch.Cells[0] with { Flags = MapCellFlags.NoDraw };
        var context = new MapEffectiveContext(doc.Surfaces, new Dictionary<string, MapEffectiveTransform>
        { ["Surface:plane-0"] = new(0.0000004, 0, 0, 0, 1) }, new string('e', 64));
        var complete = MapScopedSurfaces.CompleteEffective(context);
        var view = acquired ? MapScopedSurfaces.Acquire(new MapEffectiveSurfaceSource(context), complete.Scope, []) : complete;
        var point = new MapExactPoint(new(partial ? 1 : 0, 1), new(5, 1), new(-1, 2));
        var result = new MapSpaceMembership(view).QueryExact(point);
        Assert.Equal(MapMembershipStatus.Resolved, result.Status);
        Assert.Equal(partial ? 1 : 0, result.LowerCompatibility!.Value.SlotCell);
        Assert.Equal(new MapExactValue(partial ? 3 : 0, partial ? 2 : 1), MapSpaceIntervals.AtExact(view, point).Lower);
        var support = new MapSupportQuery(view).SelectExact(new(MapEffectiveGeometry.Presentation(view, point), null, "world", null, 0, 10, null), point);
        Assert.Equal(MapSupportStatus.LegacyFallback, support.Status);
        Assert.Equal(partial ? 1.5f : 0, support.WorldY);
        var outside = point with { X = point.X.Subtract(new(1, MapResourceTransform.Quantum)) };
        Assert.Equal(MapMembershipStatus.MissingGeometry, new MapSpaceMembership(view).QueryExact(outside).Status);
        Assert.Equal(MapMembershipStatus.MissingGeometry, MapSpaceIntervals.AtExact(view, outside).Status);
    }

    [Fact]
    public void FrozenLegacyCell_InteriorEdgeUsesNextCellFractions()
    {
        var doc = LegacyExteriorFixtures.Row("tagged");
        var patch = doc.Surfaces.Patches.Values.Single();
        patch.Cells[0] = patch.Cells[0] with { Flags = MapCellFlags.NoDraw };
        var context = new MapEffectiveContext(doc.Surfaces, new Dictionary<string, MapEffectiveTransform>
        { ["Surface:plane-0"] = new(0.0000004, 0, 0, 0, 1) }, new string('f', 64));
        var view = MapScopedSurfaces.CompleteEffective(context);
        var point = new MapExactPoint(new(1, 1), new(5, 1), new(-1, 2));
        Assert.Equal(1, new MapSpaceMembership(view).QueryExact(point).LowerCompatibility!.Value.SlotCell);
        Assert.Equal(new MapExactValue(3, 2), MapSpaceIntervals.AtExact(view, point).Lower);
    }

    [Fact]
    public void FrozenLegacyCell_SampleOutsideSelectedCellBecomesRefusal()
    {
        var doc = LegacyExteriorFixtures.Row("tagged");
        var context = new MapEffectiveContext(doc.Surfaces, new Dictionary<string, MapEffectiveTransform>
        { ["Surface:plane-0"] = new(0.0000004, 0, 0, 0, 1) }, new string('1', 64));
        var view = MapScopedSurfaces.CompleteEffective(context);
        var footprint = view.RecordsIn(new("plane-0", 0, 0)).OfType<MapSpaceFootprint>().Single();
        var point = new MapExactXz(new(5, 2), new(-1, 2));
        var preparation = new MapSpacePointBounds(view, new(), null);
        var plan = preparation.Plan(footprint, footprint.Lower, new(new(3, 2), new(-1, 2)));
        var faces = MapBoundFaceContext.PrepareBounds(view, [], 256, 4096, null, out _, preparation.Preparation)!;
        var error = Assert.Throws<MapDocumentException>(() => MapSpacePointBounds.Height(faces, plan, point));
        Assert.Contains("outside selected legacy cell", error.Message);
    }
}
