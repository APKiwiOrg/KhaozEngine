using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static class WindowFoliageFixtures
{
    internal static NativeSceneFixture Scene(FoliageFixture fixture) => new(fixture.Document, fixture.Assets);

    internal static MapSceneScope Scope(MapFoliageLayer layer, MapFoliageChunkKey key)
    {
        var r = MapFoliageDistribution.Rectangle(layer, key);
        return Scope(r.MinX, r.MinZ, r.MaxX, r.MaxZ);
    }

    internal static MapSceneScope Scope(float minX, float minZ, float maxX, float maxZ) => new(
        new(new WorldFrame(0, 0), new(minX, minZ), new(maxX, maxZ), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits()),
        new(minX, -500, minZ, maxX, 500, maxZ), MapSceneChannels.All);

    internal static FoliageFixture Rounded()
    {
        var f = FoliageComparisonFixtures.Flat();
        var layer = FoliageFixtures.Layer("cover-a", FoliageFixtures.Surface("floor"), 0.1f, -1, 1, 65, 3,
            Enumerable.Repeat((byte)255, 65 * 3).ToArray(), []);
        return FoliageComparisonFixtures.Set(f, layer);
    }

    /// <summary>The coarse cell containing (80.25, -10.25) reads the medium north-neighbour centre (82, -6)
    /// and fine east-neighbour centre (86, -10). The moved fine owner clears the latter's own cell centre.</summary>
    internal static FoliageFixture ThreeLattices(float edge)
    {
        var f = FoliageFixtures.Clearance("solid");
        var document = f.Document;
        document.Placements.Clear();
        document.Prefabs = null;
        document.Foliage = null;
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, "coarse", MapSurfaceRole.SupportFloor,
            new(new(4, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0), 16, 1, 24, 5, (_, _) => 2000);
        WaterFixtures.Surface(set, "medium", MapSurfaceRole.SupportFloor,
            new(new(2, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0), 41, 2, 43, 6, (_, _) => 2100);
        foreach (var patch in set.Patches.Values)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
        var coarse = set.Patches.Values.Single(p => p.Key.SurfaceId == "coarse");
        var outside = new MapRecordRef("outside", coarse.Key);
        coarse.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
        coarse.Records.Add(new MapSpaceFootprint("outside-domain", outside, coarse.Key,
            Enumerable.Range(1, 4).SelectMany(z => Enumerable.Range(16, 8).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "coarse", null), new(MapBoundKind.OpenTop, null, null)));
        coarse.Records.Add(new MapSpaceDoc("room", MapSpaceKind.Room, outside, null, [], [], [], []));
        coarse.Records.Add(new MapPrismDomain("room-domain", 1, new("room", coarse.Key), outside, "", "storey",
            new(MapLatticeFrame.ImportedMetreCentimetre, [new(76, 4, 94, 16)], []), 19, 25));
        document.Surfaces = set;
        document.BuildingIntent = new(1, [], [], [], []) { Storeys = [new(1, "storey", "Storey", null)] };
        FoliageFixtures.Rehash(document);

        var patchFine = PrefabFixtures.Patch("fine", 8, 16, 0);
        for (int i = 0; i < patchFine.Cells.Length; i++) patchFine.Cells[i] = patchFine.Cells[i] with { Underlay = 10 };
        var childFine = PrefabFixtures.SurfaceChild("fine", MapSurfaceRole.SupportFloor, patchFine);
        childFine = childFine with
        {
            Surface = childFine.Surface with
            { Frame = new(new(1, 2), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0) }
        };
        var prop = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "prop"),
            FoliageFixtures.ClearingPlacement("prop", "fine", 2, -4, explicitY: true));
        prop.Value.Y = 0;
        var prefab = new MapPrefabAsset(1, "lattice-prefab", new MapPrefabChild[] { childFine, prop });
        var (roots, source) = FoliageFixtures.ClearanceAssets();
        var reference = source.Add("lattice-prefab", MapPrefabFile.Write(prefab));
        var index = source.Add("lattice-index", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(reference, "Prefab", "mesh", "collider", "selection", "material")),
        }.ToJsonString());
        roots = roots.Append(index).ToArray();
        document.NativeAssets = roots.ToList();
        document.NumericIdHighWaterMark = 1;
        document.Prefabs = new(1, 1, [new("p", reference.Id, reference.Sha256, new(Vector3.Zero, 0, 1), null,
            new(prefab.Children.Select(c => new MapPrefabBinding(c.Key, "p/" + c.Key.Key,
                c.Key.Kind == MapPrefabChildKind.Placement ? 1 : null, false)).ToArray()), [])], []);
        var assets = MapAssetClosure.Load(roots, source);
        document = MapPrefabEdits.Prepare(document, assets, new MapMovePrefab("p", new(new(85.5f, 22, -4.5f), 0.371f, 1.2f))).After;
        var binding = new MapSupportBinding(MapSupportBindingKind.Space, null, "room", 22.2f, 3, 0.5f);
        var layer = FoliageFixtures.Layer("cover-a", binding, 80, -12, 0.25f, 33, 33,
            Enumerable.Repeat((byte)255, 33 * 33).ToArray(), [10]);
        document.Foliage = new(1, [FoliageFixtures.ClearanceLayer(layer,
            rules: [new("fine-solid", "tree", "clear", MapFoliageClearanceKind.Solid, new(-0.75f, -0.75f, 0.75f, 0.75f))],
            owners: [new(FoliageFixtures.Owner("p/prop"), FoliageFixtures.Surface("p/fine"))],
            ordinals: [new("coarse", 0), new("medium", 0), new("p/fine", 0)], edge: edge, indoors: false)]);
        return new(document, assets);
    }

    internal static FoliageFixture DistantExceptionalOwner(bool marker)
    {
        var f = FoliageComparisonFixtures.TwoSamplesPerChunk();
        var layer = f.Document.Foliage!.Layers[0];
        var owner = FoliageFixtures.Owner("distant-owner", marker ? MapPrefabChildKind.Marker : MapPrefabChildKind.Placement);
        var footprint = new MapFoliageRect(-101, -4, -94, 4);
        if (marker)
            f.Document.BuildingIntent = new(1, [], [], [], [new(1, owner.Id, "Distant", new(100, 100, 0), 0, "test", true, [])]);
        else f.Document.Placements.Add(FoliageFixtures.ClearingPlacement(owner.Id, "floor", 100, 0, explicitY: true));
        f.Document.Foliage = new(1, [FoliageFixtures.ClearanceLayer(layer,
            rules: marker ? [] : [new("distant-solid", "tree", null, MapFoliageClearanceKind.Solid, footprint)],
            masks: marker ? [new("distant-mask", owner, FoliageFixtures.Surface("floor"), MapFoliageClearanceKind.Solid, footprint)] : [],
            owners: [new(owner, FoliageFixtures.Surface("floor"))], ordinals: [new("floor", 0)], edge: 0)]);
        return f;
    }

    internal static FoliageFixture NativeMask(MapPrefabChildKind kind, bool distant)
    {
        var f = FoliageComparisonFixtures.TwoSamplesPerChunk();
        var layer = f.Document.Foliage!.Layers[0];
        var set = new MapSurfaceSet();
        int x = distant ? 100 : 0;
        WaterFixtures.Surface(set, "mask-surface", MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre,
            x, 0, x + 2, 2, (_, _) => 1000);
        foreach (var p in set.Patches)
        {
            for (int i = 0; i < p.Value.Cells.Length; i++) p.Value.Cells[i] = p.Value.Cells[i] with { Underlay = 10 };
            if (kind == MapPrefabChildKind.Record) p.Value.Records.Add(new MapSpaceDoc("mask-record", MapSpaceKind.Exterior, null, null, [], [], [], []));
            f.Document.Surfaces.Patches.Add(p.Key, p.Value);
        }
        f.Document.Surfaces.Refs.AddRange(set.Refs);
        var owner = FoliageFixtures.Owner(kind == MapPrefabChildKind.Surface ? "mask-surface" : "mask-record", kind);
        f.Document.Foliage = new(1, [FoliageFixtures.ClearanceLayer(layer,
            masks: [new("native-solid", owner, FoliageFixtures.Surface("floor"), MapFoliageClearanceKind.Solid, new(2, -4, 6, 4))],
            owners: [new(owner, FoliageFixtures.Surface("floor"))], ordinals: [new("floor", 0)], edge: 0)]);
        FoliageFixtures.Rehash(f.Document);
        return f;
    }
}
