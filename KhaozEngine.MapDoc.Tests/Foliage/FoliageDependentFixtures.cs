using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Invented dependent-edit fixtures shared by the pure finalizer, editor and wire proofs.</summary>
internal static partial class FoliageFixtures
{
    internal const string DependentLayerId = "cover-d";
    internal const string ShrubId = "shrub";
    internal const string PostId = "post";
    internal const string FarId = "far";

    /// <summary>The unpacked basic hut with its standalone room, roof and no roof link, as the released interior wire
    /// proofs use it. It adds an unframed standalone shrub seated on the exterior floor at (10, -10), a standalone marker post at (6, -2, -12) and
    /// an unrelated marker far at (14, -2, -14), all over the exterior floor at world X [0, 16] and Z [-16, 0]. Layer
    /// cover-d binds that floor with a 64 by 64 raster of 200 from (-16, -16) at 1 m in four chunks, a Solid rule
    /// clearing item assets tagged shrub, and a Solid mask owned by the post marker with its exterior correspondence. The
    /// unpacked hut item carries other tags, so it owns no clearing. Without foliage it is the released control.</summary>
    internal static FoliageFixture Dependents(bool foliage = true)
    {
        PrefabFixture f = PrefabFixtures.Create();
        MapDocument doc = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var removed = doc.Surfaces.AllRecords().Where(r => r is MapCavePortal or MapBoundaryChain).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (MapSurfacePatch patch in doc.Surfaces.Patches.Values) patch.Records.RemoveAll(r => removed.Contains(r.Id));
        MapBuildingIntent intent = doc.BuildingIntent!;
        doc.BuildingIntent = new(1, [], [], intent.Lights, intent.Markers.Concat(new[]
        {
            new MapLocalMarkerDoc(1, PostId, "Post", new(6, -2, -12), 0, "post-role", true, ["post"]),
            new MapLocalMarkerDoc(1, FarId, "Far", new(14, -2, -14), 0, "far-role", true, ["far"]),
        }).ToArray())
        { Roofs = intent.Roofs, Storeys = intent.Storeys };
        doc.NativeResourceTransforms = doc.NativeResourceTransforms!.Where(t => !removed.Contains(t.OwnerId) && t.Kind != MapPrefabChildKind.RoofLink).ToArray();
        MapPrefabSet set = doc.Prefabs!;
        doc.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, set.Instances,
            set.Transfers.Select(t => removed.Contains(t.Binding.WorldId) || t.Binding.Child.Kind == MapPrefabChildKind.RoofLink ? t with { Retired = true } : t).ToArray())
        { Attachments = set.Attachments };
        doc.Placements.Add(new MapPlacement
        { Id = ShrubId, Kind = "item", AssetId = "item", X = 10, Z = -10, Scale = 1, Tags = ["shrub"], SupportBinding = Surface("exterior") });
        if (foliage) doc.Foliage = new(1, [DependentLayer()]);
        return new(doc, f.Assets);
    }

    internal static MapFoliageLayer DependentLayer()
    {
        var bytes = Enumerable.Repeat((byte)200, 64 * 64).ToArray();
        MapFoliageLayer raster = Layer(DependentLayerId, Surface("exterior"), -16, -16, 1, 64, 64, bytes, [10]);
        var post = Owner(PostId, MapPrefabChildKind.Marker);
        return new(1, DependentLayerId, raster.Support, raster.OriginX, raster.OriginZ, raster.CellSize, raster.Width, raster.Depth,
            raster.RowDirection, 17, 0.5f, 1, 1, 0, [new("item", 1)], [10], [new(10, "material", MapFoliageMaterialKind.Ordinary)],
            new("legacy-tile", 1, 0), true, true, 0, 0, [new("exterior", 0)],
            [new("shrub-solid", "item", "shrub", MapFoliageClearanceKind.Solid, new(-0.5f, -0.5f, 0.5f, 0.5f))],
            [new("post-mask", post, Surface("exterior"), MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))],
            [new(post, Surface("exterior"))], raster.Chunks);
    }

    /// <summary>The standalone roof link the released interior wire proof sets on the unpacked hut.</summary>
    internal static MapRoofLinkDoc DependentRoofLink() => new("link", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "a/roof"),
        [new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "a/room")], [], 2.5f);

    /// <summary>The standalone room's prism domain.</summary>
    internal static MapPrismDomain DependentRoom(MapDocument document) => document.Surfaces.AllRecords().OfType<MapPrismDomain>().Single();

    /// <summary>The stored standalone marker with <paramref name="id"/>.</summary>
    internal static MapLocalMarkerDoc DependentMarker(MapDocument document, string id) =>
        document.BuildingIntent!.Markers.Single(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>The prefab clearance fixture whose instance child p/prop is a rule owner with an exceptional owner
    /// correspondence, plus a RoofAbove mask the same child owns. Removing the instance alone leaves dangling owners,
    /// so it must travel with the foliage change in one sequence.</summary>
    internal static FoliageFixture OwnedByInstance()
    {
        FoliageFixture f = Clearance("prefab");
        MapFoliageLayer layer = f.Document.Foliage!.Layers[0];
        f.Document.Foliage = new(1, [ClearanceLayer(layer,
            masks: [new("prop-mask", Owner("p/prop"), Surface("floor-1"), MapFoliageClearanceKind.Solid, new(-2, -2, 2, 2))])]);
        return f;
    }

    /// <summary>The layer of <paramref name="fixture"/> without any row its instance child owns.</summary>
    internal static MapFoliageLayer WithoutInstanceOwner(FoliageFixture fixture) =>
        ClearanceLayer(fixture.Document.Foliage!.Layers[0], masks: [], owners: []);

    /// <summary>A world-terms copy of <paramref name="marker"/> moved to <paramref name="x"/> metres.</summary>
    internal static MapLocalMarkerDoc MovedMarker(MapLocalMarkerDoc marker, float x) => marker with { Position = marker.Position with { X = x } };

    /// <summary>The basic fixture plus <paramref name="count"/> metadata-only paint override surfaces, each targeting its
    /// own metadata-only support floor, so the context preflight counts that many distinct paint targets without any
    /// patch to compile.</summary>
    internal static FoliageFixture PaintTargets(int count)
    {
        FoliageFixture f = Create();
        for (int k = 0; k < count; k++)
            foreach (var surface in new[]
            {
                new MapSurfaceRef("target-" + k, MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""),
                new MapSurfaceRef("paint-" + k, MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.PaintOverride, MapPresencePolicy.Native, "target-" + k, null, ""),
            })
                f.Document.Surfaces.Refs.Add(surface with { SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, []) });
        return f;
    }

    /// <summary>The Solid clearance fixture whose unframed standalone placement prop owns both a typed exception mask and
    /// an owner correspondence, beside a placement other that no foliage row names.</summary>
    internal static FoliageFixture Renamable()
    {
        FoliageFixture f = Clearance("solid");
        f.Document.Placements.Add(ClearingPlacement("other", "floor-1", 10, 10));
        f.Document.Placements[^1].Tags = ["other"];
        MapFoliageLayer layer = f.Document.Foliage!.Layers[0];
        f.Document.Foliage = new(1, [ClearanceLayer(layer,
            masks: [new("prop-mask", Owner("prop"), Surface("floor-1"), MapFoliageClearanceKind.Solid, new(-2, -2, 2, 2))],
            owners: [new(Owner("prop"), Surface("floor-1"))])]);
        return f;
    }
}
