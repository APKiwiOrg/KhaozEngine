using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedSource_DistinctHeightAndWitnessArePreserved(bool prefab)
    {
        var f = PrefabFixtures.SourceConsumer(prefab);
        var supplied = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        Array.Fill(supplied.Surfaces.Patches.Values.Single().Heights, 400);
        var source = MapDocumentSurfaceSource.Capture(supplied);
        var result = MapResolverV2.Resolve(f.Document, f.Assets, source, PrefabFixtures.Options());
        Assert.Equal(4, result.Document.Placements.Single(p => p.PlacementId == "consumer").Transform.Position.Y);
        var support = result.Supports.Single(s => s.PlacementId == "consumer").Support;
        Assert.Equal(4, support.WorldY);
        var effective = result.Document.EffectiveResources!;
        var view = effective.Acquire(new(WorldFrame.Origin, new(0, -2), new(2, 0), null, null, Enum.GetValues<MapSurfaceRole>(), null, new()));
        Assert.Equal(source.ReadPatch(new("exterior", 0, 0)).SemanticSha256, view.Patch(new("exterior", 0, 0)).SemanticSha256);
        Assert.Equal(view.ReadWitness.SnapshotId, effective.SurfaceSource.SnapshotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedSource_UnloadedFloorPreservesReleasedRefusal(bool prefab)
    {
        var f = PrefabFixtures.SourceConsumer(prefab);
        var source = new FilteredSurfaceSource(MapDocumentSurfaceSource.Capture(f.Document), new MapPatchKey("exterior", 0, 0));
        var error = Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(f.Document, f.Assets, source, PrefabFixtures.Options()));
        Assert.Contains("consumer", error.Message);
        Assert.Contains("MissingGeometry", error.Message);
    }

    [Fact]
    public void SuppliedSource_NativeRecordBudgetKeepsReleasedMeaning()
    {
        var f = PrefabFixtures.SourceConsumer(false);
        var supplied = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        for (int i = 0; i < 80; i++) supplied.Surfaces.Patches.Values.Single().Records.Add(
            new MapSpaceDoc("unreferenced-" + i, MapSpaceKind.Exterior, null, null, [], [], [], []));
        var source = MapDocumentSurfaceSource.Capture(supplied);
        var scope = new MapSurfaceScope(WorldFrame.Origin, new(0, -2), new(2, 0), null, null, Enum.GetValues<MapSurfaceRole>(), null, new());
        var released = MapScopedSurfaces.Acquire(source, scope, []);
        Assert.Equal(MapSupportStatus.Supported, new MapSupportQuery(released).SampleSurface(new(WorldFrame.Origin, new(1, 0, -1)), "exterior").Status);
        var result = MapResolverV2.Resolve(f.Document, f.Assets, source, PrefabFixtures.Options());
        Assert.Equal(-2, result.Document.Placements.Single().Transform.Position.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedSource_StoredMissingOrCorruptFloorPreservesReleasedRefusal(bool corrupt)
    {
        var f = PrefabFixtures.SourceConsumer(false);
        string directory = PrefabFixtures.TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(f.Document, directory);
            var key = new MapPatchKey("exterior", 0, 0);
            if (corrupt) SurfaceStorageFixtures.FlipPayloadByte(directory, key);
            else SurfaceStorageFixtures.DeletePayload(directory, key);
            var source = MapStoredSurfaceSource.Open(directory);
            var error = Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(f.Document, f.Assets, source, PrefabFixtures.Options()));
            Assert.Contains("consumer", error.Message);
            Assert.Contains("MissingGeometry", error.Message);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedSource_WorldAlignedNativeFloorUsesProducerFacts(bool unavailable)
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, 5, -29999.75f));
        var pose = MapEffectiveTransform.From(f.Document.Prefabs!.Instances.Single().Transform);
        var kernel = new MapResourceTransform(pose);
        var point = kernel.Point(new(new(1, 1), default, new(-1, 1)));
        f.Document.Placements.Add(new()
        {
            Id = "consumer",
            Kind = "item",
            AssetId = "item",
            X = point.X.ToSingle(),
            Z = point.Z.ToSingle(),
            Y = null,
            Scale = 1,
            SupportBinding = new(MapSupportBindingKind.Surface, "exterior", null, null, null, null)
        });
        var supplied = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        foreach (var patch in supplied.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "exterior")) Array.Fill(patch.Heights, 400);
        IMapSurfaceSource source = MapDocumentSurfaceSource.Capture(supplied);
        if (unavailable)
        {
            source = new FilteredSurfaceSource(source, MapPatchKey.ForCell("exterior", point.X.Floor(), point.Z.Negate().Floor()));
            Assert.Contains("MissingGeometry", Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(f.Document, f.Assets, source, PrefabFixtures.Options())).Message);
        }
        else
        {
            var result = MapResolverV2.Resolve(f.Document, f.Assets, source, PrefabFixtures.Options());
            Assert.Equal(4, result.Document.Placements.Single(p => p.PlacementId == "consumer").Transform.Position.Y);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingYProvider_ImplicitAndMaterializedSurfaceOrSpaceRefuse(bool materialized, bool space)
    {
        var f = PrefabFixtures.MissingYProvider(materialized, space);
        var error = Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(f));
        Assert.Contains("consumer", error.Message);
        Assert.Contains("provider", error.Message);
        Assert.Contains("missing Y", error.Message);
    }

    [Fact]
    public void ExplicitPrefixedSurface_KeepsItsOwnPose()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.PrefixedOwners(false));
        Assert.Equal(10, r.Transforms["Surface:a/deck/floor"].X);
    }

    [Fact]
    public void ImplicitOwner_OverlappingPlacementKeysUseActualOwner()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.PrefixedOwners(true));
        var id = r.Surfaces.Refs.Single(s => s.Id.Contains("p%2Fq%2Fdeck-support", StringComparison.Ordinal)).Id;
        Assert.Equal(10, r.Transforms["Surface:" + id].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveLegacyExterior_PreservesClassifierAndCompatibilityHeight(bool acquired)
    {
        var doc = LegacyExteriorFixtures.Row("tagged");
        var f = PrefabFixtures.Create("roomless");
        f.Document.Prefabs = null;
        f.Document.Surfaces = doc.Surfaces.Clone();
        var effective = PrefabFixtures.Effective(f);
        var view = acquired ? effective.Acquire(
            new(WorldFrame.Origin, new(0, -1), new(3, 0), null, null, Enum.GetValues<MapSurfaceRole>(), null, new()))
            : effective.CompleteView();
        var membership = new MapSpaceMembership(view);
        var at = new MapExactPoint(new(3, 2), new(3, 1), new(-1, 2));
        var result = membership.QueryExact(at);
        Assert.Equal(MapMembershipStatus.Resolved, result.Status);
        Assert.Equal(new MapLegacyCellTag(new("plane-0", 0, 0), 1, MapLegacyExteriorRecipe.PolicyId), result.LowerCompatibility);
        Assert.Equal(MapMembershipStatus.Outside, membership.QueryExact(at with { Y = new(299, 100) }).Status);
        Assert.Equal(new MapExactValue(3, 1), MapSpaceIntervals.AtExact(view, at).Lower);
    }

    [Theory]
    [InlineData(20, 0, 1, false)]
    [InlineData(20, 0.371f, 1, true)]
    [InlineData(0, 0, 1.2f, false)]
    [InlineData(20, 0.371f, 1.2f, true)]
    public void EffectiveIndoorSpan_UsesTransformedCellAndScaledOffsets(float x, float yaw, float scale, bool acquired)
    {
        var doc = CaveFixtures.IndoorRow();
        var pose = new MapEffectiveTransform(x, 5, -20, yaw, scale);
        var context = new MapEffectiveContext(doc.Surfaces, new Dictionary<string, MapEffectiveTransform> { ["Surface:plane-0"] = pose }, new string('b', 64));
        var kernel = new MapResourceTransform(pose);
        var point = kernel.Point(new(new(1, 2), new(4, 1), new(-1, 2)));
        var complete = MapScopedSurfaces.CompleteEffective(context);
        var view = acquired ? MapScopedSurfaces.Acquire(new MapEffectiveSurfaceSource(context), complete.Scope, []) : complete;
        Assert.Equal(new[] { "world", "indoor-0", "indoor" }, new MapSpaceMembership(view).QueryExact(point).DomainKeys);
        Assert.Equal(new[] { "world" }, new MapSpaceMembership(view).QueryExact(kernel.Point(new(new(1, 2), new(9, 2), new(-1, 2)))).DomainKeys);
        Assert.Equal(new[] { "world" }, new MapSpaceMembership(view).QueryExact(kernel.Point(new(new(1, 2), new(23, 5), new(-1, 2)))).DomainKeys);
    }

    [Fact]
    public void ExactQueries_NonzeroFrameRationalReturnsStatuses()
    {
        var effective = PrefabFixtures.Effective(PrefabFixtures.Sloped(true, 1.2f));
        var view = effective.CompleteView();
        Assert.NotEqual(WorldFrame.Origin, view.Scope.Frame);
        var point = new MapExactPoint(new(1, long.MaxValue), new(1, 1), new(-1, long.MaxValue));
        Assert.Equal(MapMembershipStatus.MissingGeometry, new MapSpaceMembership(view).QueryExact(point).Status);
        Assert.Equal(MapSupportStatus.MissingGeometry, new MapSupportQuery(view).SampleSurfaceExact(point, "a/floor").Status);
        Assert.Equal(MapMembershipStatus.MissingGeometry, MapSpaceIntervals.AtExact(view, point).Status);
    }
}
