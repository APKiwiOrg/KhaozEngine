using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabVillageTests
{
    [Fact]
    public void OffsiteSupportOwners_ContributeKnownEmptyWitnesses()
    {
        var f = PrefabFixtures.Create("support-resource");
        // The repeated hut names its marker in the same placement, because marker names are globally unique.
        var document = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapPlacePrefab("b", "hut",
            new(new(8, 0, 0), 0.371f, 1.2f), new("exterior", new("exterior", 0, 0)))
        { InitialOverrides = PrefabFixtures.MarkerNames(f.Assets.GetPrefab("hut"), "b") }).After;
        var resources = MapPrefabResolver.Expand(document, f.Assets);
        var scope = resources.CompleteView().Scope with { LocalMin = new(0.5f, -1.5f), LocalMax = new(1.5f, -0.5f) };
        var view = resources.Acquire(scope);
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        var support = new MapSupportQuery(view).SampleSurfaceExact(new(new(1, 1), default, new(-1, 1)), PrefabFixtures.SupportFloorId);
        Assert.Equal(MapSupportStatus.Supported, support.Status);
        Assert.Equal(PrefabFixtures.SupportFloorId, support.Face!.Value.OwnerId);
        Assert.Contains(view.Witness.KnownEmpty, range => range.SurfaceId == "prefab/b/Surface/deck%2Fdeck-support%2Ffloor");
    }

    [Fact]
    public void Village_ReloadKeepsEveryLeafOnceAndItsClosure()
    {
        var f = PrefabFixtures.Village();
        string dir = PrefabFixtures.TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(f.Document, dir);
            var a = MapPrefabResolver.Expand(f.Document, f.Assets);
            var loaded = MapDocumentFile.LoadTiled(dir);
            var assets = PrefabFixtures.ReloadVillageAssets(f.Assets);
            var b = MapPrefabResolver.Expand(loaded, assets);
            Assert.Equal(a.Digest, b.Digest);
            Assert.Equal(a.Placements.Count, a.Placements.Select(x => x.PlacementId).Distinct().Count());
            Assert.Equal(a.Placements.Select(x => (x.PlacementId, x.NumericId)), b.Placements.Select(x => (x.PlacementId, x.NumericId)));
            Assert.Equal(f.Assets.Hash, assets.Hash);
            Assert.Equal(f.Document.NativeAssets.ToArray(), loaded.NativeAssets.ToArray());
            Assert.Equal(a.AnnotationDigest, b.AnnotationDigest);
            Assert.Equal(a.Water!.Digest(), b.Water!.Digest());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Village_CombinesEveryResourceWithoutAllocatingOnResolve()
    {
        var f = PrefabFixtures.Village();
        string before = MapDocumentFile.SaveText(f.Document);
        var r = PrefabFixtures.Effective(f);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Equal(new[] { "bath", "canopy", "hut", "tower" },
            f.Assets.CapturedResources.Where(x => x.Kind == KhaozEngine.MapDoc.Assets.MapResourceKind.Prefab).Select(x => x.Reference.Id).Order(StringComparer.Ordinal));
        Assert.Equal(5, f.Document.Prefabs!.Instances.Count);
        Assert.Single(f.Document.Prefabs.Instances.SelectMany(i => i.Overrides).OfType<MapPrefabAddOverride>());
        Assert.Contains(r.Placements, p => p.PlacementId == "a/extra-furniture");
        Assert.Contains(r.Surfaces.Refs, s => s.Id == PrefabFixtures.SupportFloorId);
        Assert.Single(r.Water!.Bodies);
        Assert.Equal(5, MapEffectiveAnnotations.Markers(r).Count);
        Assert.All(MapEffectiveAnnotations.Markers(r), m =>
        {
            Assert.Equal("entrance", m.Role);
            Assert.True(m.Enabled);
            Assert.Equal(new[] { "first", "second" }, m.Tags.ToArray());
        });
        Assert.Equal(7, MapEffectiveAnnotations.Lights(r).Count);
        Assert.All(MapEffectiveAnnotations.Lights(r), l => Assert.Equal(1, l.PayloadVersion));
        Assert.NotEmpty(r.Surfaces.AllRecords().OfType<MapCavePortal>());
    }

    [Fact]
    public void VillageDoorway_RemainsAuthoredOpen()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Village());
        var relation = new MapSpaceRelations(r.CompleteView()).Relate(
            PrefabFixtures.VillageLocalPoint(r, "Record:a/room-domain", 2, 1, -1),
            PrefabFixtures.VillageLocalPoint(r, "Record:a/room-domain", 2, 1, 0.5f), new());
        Assert.Equal(MapRelationStatus.Resolved, relation.Status);
        Assert.Equal(MapGeometricRelation.ConnectedThroughPortals, relation.Relation);
        Assert.Equal(MapPortalStateSource.AuthoredOpen, Assert.Single(relation.Path).State);
    }

    [Fact]
    public void CanopyHasNoRoomAndAutoKeepsItVisible()
    {
        var f = PrefabFixtures.Village();
        Assert.Null(f.Document.Prefabs!.Instances.Single(i => i.Id == "canopy").ParentSpace);
        var r = PrefabFixtures.Effective(f);
        Assert.DoesNotContain(r.Surfaces.AllRecords().OfType<MapSpaceDoc>(), s => s.Id.StartsWith("canopy/", StringComparison.Ordinal));
        Assert.DoesNotContain(r.BuildingIntent.RoofLinks, l => l.Roof.Id == "canopy/roof");
        var point = PrefabFixtures.VillageLocalPoint(r, "Placement:canopy/roof", 0, -2, 0);
        var roof = MapRoofPolicy.Evaluate(r, r.CompleteView(), point, MapRoofMode.Auto).Roofs.Single(x => x.RoofPlacementId == "canopy/roof");
        Assert.Equal((true, true, true, true), (roof.ColourVisible, roof.CastShadows, roof.CameraBoomBlocks, roof.SkyPickingBlocks));
    }

    [Fact]
    public void TwoStoreysAndCrossBuildingConcealment_StayDistinct()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Village());
        var view = r.CompleteView();
        var membership = new MapSpaceMembership(view);
        foreach (var (height, owner) in new[] { (2.999f, "tower/room"), (3f, "tower/upper-room"), (5.999f, "tower/upper-room"), (6f, "exterior") })
        {
            var point = PrefabFixtures.VillageExactPoint(r, "Record:tower/room-domain", 1, height, -1);
            var member = membership.QueryExact(point);
            Assert.Equal(MapMembershipStatus.Resolved, member.Status);
            Assert.Equal(owner, member.SpaceId);
        }
        var roofs = MapRoofPolicy.Evaluate(r, view,
            PrefabFixtures.VillageLocalPoint(r, "Record:a/room-domain", 1, 1, -1), MapRoofMode.Auto).Roofs;
        Assert.Equal(new[] { "a/roof", "b/roof" }, roofs.Where(x => !x.ColourVisible).Select(x => x.RoofPlacementId).ToArray());
        Assert.All(roofs.Where(x => !x.ColourVisible), roof =>
            Assert.Equal((true, false, true), (roof.CastShadows, roof.CameraBoomBlocks, roof.SkyPickingBlocks)));
        Assert.True(roofs.Single(x => x.RoofPlacementId == "tower/roof").ColourVisible);
        var cave = membership.Query(PrefabFixtures.Point(r, 37, -4, -21));
        Assert.Equal("village-cave", cave.SpaceId);
        Assert.Equal(new[] { "exterior" }, cave.ParentSpaceIds.ToArray());
    }

    [Fact]
    public void PaintNotchAndOptionalWater_KeepCanonicalOwners()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Village());
        var paint = r.PaintRegions["exterior"];
        Assert.NotEmpty(paint.Regions);
        Assert.Equal(new[] { "a/paint", "b/paint" }, paint.Regions.Select(p => p.PaintId).Distinct().Order(StringComparer.Ordinal));
        Assert.All(paint.Regions, p => Assert.Equal("exterior", p.Face.OwnerId));
        var painted = PrefabFixtures.VillageExactPoint(r, "Surface:a/paint", 0.5f, 0, -0.5f);
        var notch = PrefabFixtures.VillageExactPoint(r, "Surface:a/paint", 1.5f, 0, -1.5f);
        Assert.Contains(paint.Regions, p => PrefabFixtures.PaintContains(p.Polygon, painted));
        Assert.DoesNotContain(paint.Regions, p => PrefabFixtures.PaintContains(p.Polygon, notch));
        var floor = PrefabFixtures.CanonicalPatch(r, PrefabFixtures.SupportFloorId);
        Assert.Equal(32, floor.Faces.Count);
        var water = new MapWaterQuery(r.CompleteView(), r.Water!);
        Assert.Equal(MapWaterStatus.Wet, water.Sample(PrefabFixtures.VillageLocalPoint(r, "Record:bath/pool-domain", 6, 0, -6)).Status);
        Assert.Equal(MapWaterStatus.Dry, water.Sample(PrefabFixtures.VillageLocalPoint(r, "Record:bath/pool-domain", 6, 1, -6)).Status);
        Assert.Equal(MapWaterStatus.Dry, water.Sample(PrefabFixtures.VillageLocalPoint(r, "Record:a/room-domain", 1, 1, -1)).Status);
    }

    [Fact]
    public void ScopedVillageMatchesComplete_WhenWitnessIsComplete()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Village());
        var complete = r.CompleteView();
        var scoped = r.Acquire(complete.Scope);
        Assert.Equal(MapAcquireStatus.Complete, scoped.Status);
        Assert.True(scoped.ReadWitness.Complete);
        Assert.NotEmpty(scoped.Witness.Records);
        foreach (var p in PrefabFixtures.VillageProbes(r))
        {
            var a = new MapSpaceMembership(complete).Query(p);
            var b = new MapSpaceMembership(scoped).Query(p);
            Assert.Equal((a.Status, a.SpaceId), (b.Status, b.SpaceId));
            Assert.Equal(a.DomainKeys.ToArray(), b.DomainKeys.ToArray());
            Assert.Equal(a.ParentSpaceIds.ToArray(), b.ParentSpaceIds.ToArray());
            Assert.Equal(MapRoofPolicy.Evaluate(r, complete, p, MapRoofMode.Auto).Roofs.ToArray(), MapRoofPolicy.Evaluate(r, scoped, p, MapRoofMode.Auto).Roofs.ToArray());
            var aw = new MapWaterQuery(complete, r.Water!).Sample(p);
            var bw = new MapWaterQuery(scoped, r.Water!).Sample(p);
            Assert.Equal((aw.Status, aw.BodyId, aw.SpaceId, aw.LowerY, aw.UpperY, aw.UpperIsFreeSurface),
                (bw.Status, bw.BodyId, bw.SpaceId, bw.LowerY, bw.UpperY, bw.UpperIsFreeSurface));
        }
    }
}
