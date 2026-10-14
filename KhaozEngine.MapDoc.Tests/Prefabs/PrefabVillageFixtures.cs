using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    // Invented public geometry. The reviewed support-resource variant owns the hut floor and its bindings.
    internal static PrefabFixture Village()
    {
        var support = Create("support-resource");
        var definitions = new[] { VillageHut(support.Assets.GetPrefab("hut")), VillageTower(), VillageBath(), VillageCanopy() };
        var assets = VillageClosure(support.Assets, definitions);
        var document = MapDocumentFile.LoadText(FormatSixJson(false));
        document.Id = "synthetic-village";
        document.TileSize = 64;
        document.Bounds = new() { MinX = 0, MinZ = -32, MaxX = 40, MaxZ = 0 };
        document.PlayableBounds = document.Bounds;
        document.NativeAssets = assets.Roots.ToList();
        document.NumericIdHighWaterMark = 100;
        var exterior = Patch("exterior", 10, 8, -600);
        var parent = new MapRecordRef("exterior", exterior.Key);
        exterior.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        exterior.Records.Add(new MapSpaceFootprint("exterior-domain", parent, exterior.Key,
            Enumerable.Range(0, 8).SelectMany(z => Enumerable.Range(0, 10).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "exterior", null), new(MapBoundKind.OpenTop, null, null)));
        document.Surfaces.Patches.Add(exterior.Key, exterior);
        document.Surfaces.Refs.Add(Surface("exterior", MapSurfaceRole.SupportFloor) with
        { Frame = MapLatticeFrame.ImportedMetreCentimetre with { CellUnitMetres = new(4, 1) } });
        VillageCave(document, parent);
        document.BuildingIntent = new(1, [], new[] { new MapRoofGroupDoc("shared-huts", new[]
            { World(MapPrefabChildKind.Record, "a/room"), World(MapPrefabChildKind.Record, "b/room") }) }, [], [])
        { Storeys = new[] { new MapStoreyDoc(1, "village-storey", "Village", null) } };
        SealWorldParent(document);
        document.Prefabs = new(1, 1, [], []);
        foreach (var (id, resource, position, scale) in new[]
        {
            ("a", "hut", new Vector3(2.25f, 0.25f, -3.125f), 0.8f),
            ("b", "hut", new Vector3(12.25f, 0.5f, -3.125f), 1.2f),
            ("tower", "tower", new Vector3(24.25f, 0.25f, -3.125f), 1.2f),
            ("bath", "bath", new Vector3(2.25f, 0.25f, -16.125f), 0.8f),
            ("canopy", "canopy", new Vector3(20.25f, 0.25f, -18.125f), 1.2f),
        })
        {
            var asset = assets.GetPrefab(resource);
            var extra = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "extra-furniture"), new MapPlacement
            { Id = "extra-furniture", Kind = "item", AssetId = "item", X = 2.5f, Y = 0, Z = -2.5f, Scale = 1 });
            MapPrefabOverride[] overrides = id == "a" ? new MapPrefabOverride[] { new MapPrefabAddOverride(extra.Key, extra) } : [];
            var bindings = asset.Children.Concat(id == "a" ? new[] { extra } : Array.Empty<MapPrefabChild>())
                .Select(c => new MapPrefabBinding(c.Key, id + "/" + c.Key.Key,
                    c.Key.Kind == MapPrefabChildKind.Placement ? ++document.NumericIdHighWaterMark : null, false)).ToArray();
            var instance = new MapPrefabInstance(id, resource, assets.GetResource(resource).Reference.Sha256,
                new(position, 0.371f, scale), resource == "canopy" ? null : parent, new(bindings), overrides);
            document.Prefabs = new(1, 1, document.Prefabs.Instances.Append(instance).ToArray(), []);
            var plan = MapPrefabBindingPlanner.Plan(document, asset, assets, id, instance.Bindings);
            document.Prefabs = new(1, 1, document.Prefabs.Instances.Select(i => i.Id == id ? i with { Bindings = plan.Bindings } : i).ToArray(), []);
            document.NumericIdHighWaterMark = plan.HighWaterMark;
        }
        return new(document, assets);
    }

    static MapPrefabAsset VillageHut(MapPrefabAsset original)
    {
        const string floorId = "deck/deck-support/floor";
        var children = original.Children.Select(c =>
        {
            if (c is MapPrefabPlacementChild p && p.Key.Key == "item")
            {
                var value = MapPrefabJson.Copy(p.Value); value.Y = null;
                value.SupportBinding = new(MapSupportBindingKind.Surface, floorId, null, null, null, null);
                return p with { Value = value, References = Slots(Surf("/value/supportBinding/surfaceId", floorId)) };
            }
            return c is MapPrefabRoofChild roof ? roof with
            { Value = roof.Value with { Groups = new[] { World(MapPrefabChildKind.RoofGroup, "shared-huts") } } } : c;
        }).ToList();
        var paint = Create("paint-notch").Assets.GetPrefab("hut").Children.OfType<MapPrefabSurfaceChild>().Single();
        children.Add(paint with
        {
            Surface = paint.Surface with { PaintTargetSurfaceId = "exterior" },
            References = Slots(Surf("/patches/0/key/surfaceId", "paint"),
                Surf("/surface/paintTargetSurfaceId", "exterior", MapPrefabReferenceScope.World)),
        });
        return original with { Children = children };
    }

    static MapPrefabAsset VillageTower()
    {
        var original = Create().Assets.GetPrefab("hut");
        var children = original.Children.Select(c =>
        {
            if (c is MapPrefabPlacementChild p && p.Key.Key == "roof")
            { var value = MapPrefabJson.Copy(p.Value); value.Y = 6; return p with { Value = value }; }
            if (c is MapPrefabPlacementChild door && door.Key.Key == "doorway")
            { var value = MapPrefabJson.Copy(door.Value); value.Z = 0.25f; return door with { Value = value }; }
            return c is MapPrefabRoofChild roof ? roof with
            {
                Value = roof.Value with
                { Rooms = roof.Value.Rooms.Append(new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "upper-room")).ToArray(), AboveY = 6 }
            } : c;
        }).ToList();
        children.Add(SurfaceChild("upper-floor", MapSurfaceRole.SupportFloor, Patch("upper-floor", 4, 4, 300)));
        children.Add(SurfaceChild("upper-ceiling", MapSurfaceRole.Ceiling, Patch("upper-ceiling", 4, 4, 600)));
        var anchor = new MapPatchKey("upper-floor", 0, 0);
        var parent = new MapRecordRef("$parent", new("$parent", 0, 0));
        children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "upper-room"),
            new MapSpaceDoc("upper-room", MapSpaceKind.Room, parent, null, [], [], [], []), anchor)
        { References = Slots(Anchor("upper-floor"), Ref("/value/parent", "$parent", "$parent")) });
        children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "upper-domain"),
            new MapPrismDomain("upper-domain", 1, new("upper-room", anchor), parent, "", "upper", Domain(0, 0, 4, 4), 3, 6), anchor)
        { References = Slots(Anchor("upper-floor"), Ref("/value/space", "upper-room", "upper-floor"), Ref("/value/parent", "$parent", "$parent"), Slot("/value/storeyId", MapPrefabChildKind.Storey, "upper")) });
        children.Add(new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "upper"), "Upper"));
        return original with { Id = "tower", Children = children };
    }

    static MapPrefabAsset VillageBath()
    {
        var original = Create("all-resources").Assets.GetPrefab("hut");
        var children = original.Children.Where(c => c.Key.Key.StartsWith("pool", StringComparison.Ordinal) && c is not MapPrefabRoofGroupChild
            || c.Key.Key is "water" or "lower").ToList();
        children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "roof"), new MapPlacement
        { Id = "roof", Kind = "roof", AssetId = "roof", X = 6, Y = 3, Z = -6, Scale = 1 }));
        children.Add(new MapPrefabRoofChild(new(MapPrefabChildKind.RoofLink, "roof-link"), new("roof-link",
            new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof"),
            new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "pool") }, [], 3)));
        children.Add(new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "entry"), new(1, "entry", "Bath entry", new(6, 1, -6), 0, "entrance", true, new[] { "first", "second" })));
        children.Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, "lamp"), new(1, "lamp", null, new(6, 2, -6), Vector3.One, 1, 4, false)));
        return original with { Id = "bath", Children = children };
    }

    static MapPrefabAsset VillageCanopy()
    {
        var original = Create("roomless").Assets.GetPrefab("hut");
        return original with
        {
            Id = "canopy",
            Children = original.Children.Concat(new MapPrefabChild[]
        {
            new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "roof"), new MapPlacement
            { Id = "roof", Kind = "roof", AssetId = "roof", X = 2, Y = 3, Z = -2, Scale = 1 }),
            new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "entry"), new(1, "entry", "Canopy entry", new(0, 1, 0), 0, "entrance", true, new[] { "first", "second" })),
            new MapPrefabLightChild(new(MapPrefabChildKind.Light, "lamp"), new(1, "lamp", null, new(2, 2, -2), Vector3.One, 1, 4, false)),
        }).ToArray(),
            Roofs = new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof") }
        };
    }

    static void VillageCave(MapDocument document, MapRecordRef parent)
    {
        foreach (var (id, role, height) in new[] { ("cave-floor", MapSurfaceRole.SupportFloor, -500), ("cave-ceiling", MapSurfaceRole.Ceiling, -200) })
        {
            var patch = Patch(id, 4, 4, height); patch.CellMinX = 36; patch.CellMinZ = 20;
            document.Surfaces.Patches.Add(patch.Key, patch); document.Surfaces.Refs.Add(Surface(id, role));
        }
        var floor = document.Surfaces.Patches[new("cave-floor", 0, 0)];
        var cave = new MapRecordRef("village-cave", floor.Key);
        var walls = new List<MapBoundaryRef>();
        foreach (var (side, x0, z0, x1, z1, facing, roomSide) in new[]
        {
            ("west", 36, 20, 36, 24, MapStripFacing.Front, MapSide.Front),
            ("east", 40, 20, 40, 24, MapStripFacing.Back, MapSide.Back),
            ("south", 36, 20, 40, 20, MapStripFacing.Back, MapSide.Back),
            ("north", 36, 24, 40, 24, MapStripFacing.Front, MapSide.Front),
        })
        {
            string id = "village-cave-" + side;
            foreach (var (suffix, surface, height) in new[] { ("lower", "cave-floor", -500), ("upper", "cave-ceiling", -200) })
                floor.Records.Add(new MapBoundaryChain(id + "-" + suffix, MapChainKind.Authored, null, new[]
                {
                    new MapChainVertex(new(surface, MapLatticeAddress.Corner(x0, z0)), height),
                    new MapChainVertex(new(surface, MapLatticeAddress.Corner(x1, z1)), height),
                }));
            floor.Records.Add(new MapWallStrip(id, new(id + "-lower", floor.Key), new(id + "-upper", floor.Key), facing, 1));
            walls.Add(new(new(id, floor.Key), roomSide));
        }
        floor.Records.Add(new MapSpaceDoc(cave.Id, MapSpaceKind.Cave, parent, null, [], walls, [], []));
        floor.Records.Add(new MapSpaceFootprint("village-cave-domain", cave, floor.Key,
            Enumerable.Range(20, 4).SelectMany(z => Enumerable.Range(36, 4).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "cave-floor", null), new(MapBoundKind.Ceiling, "cave-ceiling", null)));
    }

    static MapAssetClosure VillageClosure(MapAssetClosure original, IReadOnlyList<MapPrefabAsset> definitions)
    {
        var bytes = original.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        var manifest = JsonNode.Parse(bytes[original.Roots.Single().Id])!.AsObject();
        var rows = manifest["resources"]!.AsArray();
        string[] dependencies = rows.Single(r => r!["reference"]!["id"]!.GetValue<string>() == "hut")!["dependencies"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        rows.Remove(rows.Single(r => r!["reference"]!["id"]!.GetValue<string>() == "hut"));
        foreach (var definition in definitions)
        {
            byte[] payload = MapPrefabFile.Write(definition); bytes[definition.Id] = payload;
            rows.Add(Resource(new(definition.Id, "assets/" + definition.Id, Convert.ToHexStringLower(SHA256.HashData(payload)), 1), "Prefab", dependencies));
        }
        var root = original.Roots.Single();
        bytes[root.Id] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        root = root with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes[root.Id])) };
        return MapAssetClosure.Load(new[] { root }, new VillageSource(bytes));
    }

    internal static MapAssetClosure ReloadVillageAssets(MapAssetClosure captured, string? missingResource = null) =>
        MapAssetClosure.Load(captured.Roots, new VillageSource(captured.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal), missingResource));

    sealed class VillageSource(Dictionary<string, byte[]> bytes, string? missing = null) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => reference.Id == missing
            ? throw new MapDocumentException("Missing synthetic village resource '" + reference.Id + "'.") : bytes[reference.Id].ToArray();
    }

    internal static MapExactPoint VillageExactPoint(MapEffectiveResources resources, string owner, float x, float y, float z) =>
        new MapResourceTransform(resources.Transforms[owner]).Point(new(MapExactValue.FromSingle(x), MapExactValue.FromSingle(y), MapExactValue.FromSingle(z)));
    internal static MapFramePoint VillageLocalPoint(MapEffectiveResources resources, string owner, float x, float y, float z) =>
        MapEffectiveGeometry.Presentation(resources.CompleteView(), VillageExactPoint(resources, owner, x, y, z));

    // A fixed witness set, including exact storey planes and authored paint corners. Never a whole-world scan.
    internal static IReadOnlyList<MapFramePoint> VillageProbes(MapEffectiveResources resources) => new[]
    {
        VillageLocalPoint(resources, "Record:a/room-domain", 0, 1, -1),
        VillageLocalPoint(resources, "Record:a/room-domain", 4, 1, -1),
        VillageLocalPoint(resources, "Record:a/room-domain", 1, 1, -4),
        VillageLocalPoint(resources, "Record:a/room-domain", 1, 1, 0),
        VillageLocalPoint(resources, "Record:b/room-domain", 1, 1, -1),
        VillageLocalPoint(resources, "Record:tower/room-domain", 1, 2.999f, -1),
        VillageLocalPoint(resources, "Record:tower/room-domain", 1, 3, -1),
        VillageLocalPoint(resources, "Record:tower/room-domain", 1, 6, -1),
        VillageLocalPoint(resources, "Surface:a/paint", 0, 0, 0),
        VillageLocalPoint(resources, "Surface:a/paint", 1, 0, -1),
        VillageLocalPoint(resources, "Surface:a/paint", 1.5f, 0, -1.5f),
        VillageLocalPoint(resources, "Record:bath/pool-domain", 6, 0, -6),
        VillageLocalPoint(resources, "Record:bath/pool-domain", 6, 1, -6),
        VillageLocalPoint(resources, "Placement:canopy/roof", 0, -2, 0),
        Point(resources, 37, -4, -21), Point(resources, 37, -1, -21), Point(resources, 30, 1, -15),
    };
}
